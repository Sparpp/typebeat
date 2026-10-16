// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Gameplay/TypingEngine.cs (regression-anchored).
// type!beat gameplay-core: the headless gameplay/judgement heart.
// Time-driven line activation/sealing, keypress judgement, backspace, auto-skip,
// score/combo/accuracy/active-time-WPM/sync accumulation, SyncTimeline capture.
// Pure C#: zero osu.Framework dependencies. Driven entirely by explicit
// double-millisecond time arguments. Events fire synchronously on the caller thread.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        /// <summary>
        /// How long before a line's first typeable cell's target the line becomes typeable
        /// (<see cref="TypingLine.ActivationTime"/>). Also the length of the on-screen approach
        /// cue, so the depleting bar exactly spans "you may type now" to "the word lands".
        /// </summary>
        public const double CUE_LEAD_MS = 1500;

        /// <summary>
        /// How long before its first word the map's FIRST line opens for typing.
        ///
        /// <para>A later line is reachable early by RUSHING from the one before it (<see
        /// cref="entryPermitted"/>, up to <see cref="FLETCHER_DRAG_GRACE_MS"/> of head start), but
        /// the first line has nothing to rush from, and its boundary is usually set ON its first
        /// word - so <see cref="TypingLine.ActivationTime"/>'s clamp to that boundary left the
        /// player unable to type a single character until the word was already being sung. This is
        /// the head start the first line gets instead: typing before its word lands, the same kind of
        /// lead the later lines' rush bound hands out for free - a short one, because a first line is
        /// where the player is still finding their hands, not a line they arrived at mid-run.</para>
        ///
        /// <para>A FLOOR, not a fixed window: a mapper whose first line starts well before its vocals
        /// still gets the longer lead that boundary already gives them (never less than
        /// <see cref="CUE_LEAD_MS"/>, since that clamp still applies).</para>
        /// </summary>
        public const double FIRST_LINE_LEAD_MS = 300;

        /// <summary>
        /// Fletcher mod: extra time past a line's normal hard deadline
        /// (<see cref="TypingLine.EndTime"/> + <see cref="TypingLine.SealGraceMs"/>) that the engine
        /// holds the line open while the PLAYER is still on it, so a dragging player may finish the
        /// line the song has already left. Deliberately the same magnitude as
        /// <see cref="CUE_LEAD_MS"/>: the beat of grace the game gives you to get ready, granted at
        /// the other end of the line as well. Bounded so a run always terminates: past it the line
        /// force-seals, its untyped cells become misses, and the caret lands on the next line.
        ///
        /// <para>Since backlog 218 it bounds BOTH directions, and the name is kept for the replays
        /// and the mirrors that already use it. Drag holds a line open this long past its natural
        /// END (<see cref="sealPermitted"/>); rush enters a line this long before its natural START
        /// (<see cref="entryPermitted"/>, gated on <see cref="BoundedRush"/>). One constant, so the
        /// two freedoms cannot drift apart: a player may run ahead of the song by exactly the margin
        /// they may fall behind it.</para>
        /// </summary>
        public const double FLETCHER_DRAG_GRACE_MS = 1500;

        public LyricBeatmap Beatmap { get; }

        public IReadOnlyList<TypingLine> Lines => lines;

        /// <summary>-1 before the first line and after finish.</summary>
        public int ActiveLineIndex => activeLineIndex;

        /// <summary>
        /// Whether a lyric line is currently typeable: false during the pre-roll, the dead zone
        /// between a line's seal and the next line's cue, and after the final line. This is the seam
        /// the playfield's raw key handler gates on: keys are consumed for typing only while a line
        /// is active, and fall through to global bindings (so Space can trigger the skip overlay)
        /// once the player has reached the end of the line. Because Space is itself a typeable
        /// character, gating on an active line is what guarantees a skip can never eat a live keystroke.
        /// </summary>
        public bool LineIsActive => activeLineIndex != -1;

        /// <summary>
        /// The first line that has not sealed yet; -1 once every line has sealed. While no line is
        /// active (pre-roll, or the dead zone between a seal and the next line's cue) this is the
        /// UPCOMING line, the one the stage should focus, dimmed, after the boundary scroll.
        /// </summary>
        public int NextUnsealedLineIndex => nextSealIndex < lines.Count ? nextSealIndex : -1;

        /// <summary>Display-cell index in the active line; == Cells.Count when complete.</summary>
        public int CaretIndex => caretIndex;

        public bool IsLineComplete => activeLineIndex != -1 && caretIndex >= lines[activeLineIndex].Cells.Count;

        public bool IsFinished => isFinished;

        /// <summary>Whether a declared play start still owes the caret its walk (see <see cref="SetPlayStart"/>).</summary>
        private bool playStartPending;

        /// <summary>
        /// Where THIS play began: the clock time its first frame stood at, declared once by the play that
        /// owns the clock (<see cref="SetPlayStart"/>). Null for an engine nobody has declared a start
        /// for - every re-derivation, and every engine a test drives by hand.
        ///
        /// <para>A type!beat play does not always start at the beginning of its map. The editor's
        /// gameplay test can start anywhere - the mapper parks the playhead and presses test - and
        /// everything the map asks for BEFORE this time was never in front of the player. The engine
        /// therefore places its caret on the first character still to come
        /// (<see cref="alignCaretToThePlayStart"/>) and leaves the characters it walked past to be
        /// granted rather than missed at the seal (<c>TypeBeatPlayfield</c> reads this same time), which
        /// is what keeps a test play from charging its player for the part of the map they skipped.</para>
        ///
        /// <para>An ordinary play never declares one at all: <c>TypeBeatPlayfield</c> declares only for
        /// the editor's gameplay test (<c>DrawableRuleset.IsEditorGameplayTest</c>), so a normal play,
        /// a spectated one and a replay are charged for every character of the map whatever their
        /// clock's start time is.</para>
        /// </summary>
        public double? PlayStartTime { get; private set; }

        /// <summary>
        /// Declares where this play began (see <see cref="PlayStartTime"/>), which places the caret on
        /// the first character still to come when this engine is next updated.
        ///
        /// <para>Declared by the PLAY, and only by the play: the same engine is also driven by
        /// re-derivations - replay scoring, puppeteer tapes - which pick a map up at whatever time their
        /// tape happens to start and must judge exactly what the tape says. Those never declare a start,
        /// so nothing about them moves.</para>
        /// </summary>
        public void SetPlayStart(double time)
        {
            PlayStartTime = time;
            playStartPending = true;
        }

        /// <summary>Current run of consecutive rejected wrong keys; any accepted char resets it to 0.</summary>
        public int ConsecutiveWrongKeys => consecutiveWrongKeys;

        /// <summary>
        /// Whether the cell at (<paramref name="lineIndex"/>, <paramref name="cellIndex"/>) is
        /// currently holding a typed-through WRONG character: the player finished that character and
        /// got it wrong, and has not backspaced it away. Read at the seal to tell an unfixed TYPO
        /// from a cell the line ran out of time on, which are the two ways a cell can reach the seal
        /// with nothing resolved and, since backlog 124, two different results
        /// (<see cref="Scoring.TypeBeatResultMapping.UnresolvedCellResult"/>).
        ///
        /// <para>State, not history, on purpose: a typo the player backspaced away and then never
        /// retyped leaves an EMPTY cell, which is a character they did not finish, and it must read
        /// as the miss it is. Out-of-range coordinates answer false rather than throwing, because
        /// the callers are event handlers routed by index.</para>
        ///
        /// <para>Since backlog 210 the cell also carries the HISTORY question
        /// (<see cref="TypingCell.HeldWrongBeforeJudged"/>: was it ever wrong before it was judged),
        /// and the two coexist because they are asked by different consumers about different things.
        /// This one prices the cell the player LEFT wrong, so the answer has to change the moment
        /// the character is erased. That one prices the CORRECTION, so the answer must not: a fix
        /// the player made is a fact about the run whatever the cell holds afterwards. Neither is a
        /// cheaper version of the other, and neither may be rewritten in terms of it.</para>
        /// </summary>
        public bool CellLeftWrong(int lineIndex, int cellIndex)
        {
            if (lineIndex < 0 || lineIndex >= lines.Count)
                return false;

            var cells = lines[lineIndex].Cells;

            if (cellIndex < 0 || cellIndex >= cells.Count)
                return false;

            return cells[cellIndex].IsTypeable && cells[cellIndex].State == CellState.Wrong;
        }

        /// <summary>
        /// Wrong KEYPRESSES so far, in either input mode: the play's mistype stat (see
        /// <see cref="Mistyped"/>). Identical to <c>Counts[JudgementType.WrongChar]</c>, named for
        /// what it means outside the engine.
        /// </summary>
        public int Mistypes => counts[JudgementType.WrongChar];

        /// <summary>
        /// Literate mod, the other half of what <see cref="CaseSensitive"/> does: the map's lines
        /// are typed EXACTLY as authored, supported punctuation included. Unlike every other mod
        /// flag this is fixed at construction, because it changes the CELL LIST itself (see
        /// <see cref="TypingLine.FromLyricLine"/>) rather than only how a press is judged, and the
        /// nested per-cell scoring objects have to be flattened the same way.
        /// Requires the input path to be able to produce the marks (<see cref="TextInputFold"/> passes them under the mod).
        /// </summary>
        public bool Literate { get; }

        /// <summary>
        /// Polyglot mod (backlog 331): the lines are played in their ORIGINAL script (see
        /// <see cref="PolyglotLine"/>), fixed at construction for the reason <see cref="Literate"/>
        /// is, and a press is matched against a cell in Unicode NFC (see
        /// <see cref="PolyglotText.Matches"/>). The input path is the OS's committed TEXT, kept in its
        /// own script rather than folded by <see cref="TextInputFold"/>. Japanese maps accept romaji
        /// through the internal processor under <see cref="JapaneseRomajiInput"/> instead.
        /// A MOD and not an era: a score carries it in its mod list, and no stored run can
        /// carry a mod that did not exist.
        /// </summary>
        public bool Polyglot { get; }

        public event Action<CharJudgement>? CharJudged;
        public event Action<int>? LineActivated;
        public event Action<LineSealResult>? LineSealed;
        public event Action? ComboBroken;
        public event Action? Finished;

        /// <summary>
        /// A wrong key was REJECTED: nothing was input (no cell state change, no caret move,
        /// no <see cref="CharJudged"/>), but combo has been reset and the consecutive streak
        /// incremented. Carries the offending char for feedback visuals.
        ///
        /// <para>Since backlog 107 this fires only under <see cref="Mods.TypeBeatModGatekeeper"/>,
        /// plus the two cases the default path refuses anyway (a space pressed on a lyric char, any
        /// key pressed on a word gap). The first of those is what <see cref="SpaceSkipsWord"/>
        /// intercepts, so with that setting on the space consumes the word instead of being rejected
        /// here. In DEFAULT play a wrong char is typed through and its cell carries a
        /// <see cref="JudgementType.WrongChar"/> on <see cref="CharJudged"/> instead.</para>
        ///
        /// <para>Since backlog 109 it is no longer the seam anything but the MASH GUARD hangs off:
        /// the combo break rides on <see cref="Mistyped"/> (which fires for this key too, one event
        /// earlier, in both models) and Sudden Death rides on it as well. Only the consecutive-wrong-
        /// key drain is left here, because only the rejection model ever accrues that streak.</para>
        /// </summary>
        public event Action<char>? WrongKeyRejected;

        /// <summary>
        /// A WRONG KEYPRESS happened, in EITHER input mode (backlog 72). The keypress, as opposed to
        /// the CELL it landed on (or failed to), which is <see cref="CharJudged"/>'s business.
        ///
        /// <list type="bullet">
        /// <item>Strict (Gatekeeper): the key is rejected, so no <see cref="CharJudged"/> exists to
        /// carry it and the score processor would otherwise never learn the press happened.</item>
        /// <item><see cref="AllowWrongInput"/> (default): the wrong char IS typed into the cell, and
        /// its judgement travels on <see cref="CharJudged"/>, but since backlog 109 that judgement
        /// applies no osu result (the cell's result is deferred until it is corrected or sealed on).
        /// So in this model too the keypress is the only thing the score processor hears about at the
        /// time.</item>
        /// </list>
        ///
        /// <para>It therefore carries BOTH consequences a wrong keypress has on the submitted
        /// account, identically in both models: the mistype COUNT, and the COMBO BREAK, which is
        /// mirrored into osu's incrementally-maintained combo by hand because no result exists to
        /// carry it (see <c>TypeBeatComboFeed.OnMistyped</c>). Sudden Death fails the play from here
        /// for the same reason.</para>
        ///
        /// Raised BEFORE <see cref="ComboBroken"/> / <see cref="WrongKeyRejected"/> /
        /// <see cref="CharJudged"/>, with <see cref="Mistypes"/> already incremented.
        /// </summary>
        public event Action? Mistyped;

        /// <summary>
        /// A corrected typo just RESUMED the streak its wrong keypress broke (backlog 140). Carries
        /// how much combo was put back, i.e. the streak that keypress broke, which is always
        /// positive (a break that cost nothing announces nothing).
        ///
        /// <para>Raised from <see cref="ProcessKey"/> with <see cref="Combo"/> and
        /// <see cref="MaxCombo"/> already restored, and BEFORE the corrected retype is judged, so
        /// every consumer prices that retype at the resumed streak: the standardised combo score
        /// pays for the fix, not only the accuracy does. osu's own combo is maintained
        /// incrementally off results and no result carries this, so the playfield mirrors it by hand
        /// exactly as it mirrors the break on <see cref="Mistyped"/> (see
        /// <c>TypeBeatComboFeed.OnComboRestored</c> and
        /// <see cref="Scoring.TypeBeatScoreProcessor.RestoreCombo"/>).</para>
        ///
        /// <para><b>What is restorable, and for how long.</b> A wrong keypress SNAPSHOTS the streak
        /// it breaks against the cell it spoiled, and correcting that cell redeems the snapshot:
        /// the run resumes at the snapshot plus everything earned since. Since backlog 167 a WORD
        /// SKIP takes the same snapshot, against the first cell it abandons, because it is the same
        /// kind of break: one the player can walk back into and undo. Exactly one snapshot is ever
        /// outstanding, because a combo break TAKES OWNERSHIP OF THE STREAK IF IT HAS A STREAK TO
        /// OWN, and discards whatever claim was outstanding when it does: an intervening break is a
        /// run the player has already lost, and going back to fix the older cell cannot un-lose it.
        /// That covers a sealed line's misses, a rejected key, Fletcher's rush cap, and a wrong
        /// keypress or skip on another cell. An off-time press is NOT in that list since backlog 199
        /// (see <see cref="OffTime"/>): it is a hit now, it breaks nothing, and only a break discards
        /// a claim, so fumbling the beat between a typo and its fix no longer costs the fix its
        /// restore. It rejoins the list under <see cref="OffTimeRule.BreaksCombo"/>, the pre-199 era.
        /// Repeated wrong/fix cycles on ONE cell therefore break and restore each time, each cycle
        /// snapshotting whatever the run had grown back to.</para>
        ///
        /// <para>The "if it has a streak to own" is backlog 176, and it is the whole of the
        /// difference from the rule as backlog 140 shipped it. A break landing while the run is
        /// ALREADY at zero costs nothing, so there is nothing for it to take: it leaves the
        /// outstanding claim alone, and correcting the older cell still resumes the run. Without
        /// that, a player who fumbled two adjacent characters and then went back and fixed both got
        /// nothing back, because the second wrong key had rewritten a 447-deep claim with its own
        /// empty one. A zero-streak break with NOTHING outstanding still snapshots its own empty
        /// claim, which restores nothing when it is redeemed, exactly as it always did. The old arm
        /// is <see cref="ComboClaimRule.LatestBreakWins"/>, which is what every stored row was
        /// played under.</para>
        ///
        /// <para>Backlog 243 finishes that sentence: "already at zero" means "having earned nothing
        /// since the claim was taken", which is not the same as a combo of zero, because ONE press
        /// can both take a claim and rebuild the run. A space struck inside a word abandons the rest
        /// of it (the claim) and is then judged on the word gap it lands on, putting the combo back
        /// to 1. A typo on that same gap was therefore breaking a run of 1, passing the zero test,
        /// and overwriting a claim hundreds deep with a worthless one, which is the shape a real play
        /// lost about 430 combo to: skip, typo on the gap, second typo, then the whole word walked
        /// back and typed out for nothing. So the claim REMEMBERS the combo its own press credited
        /// (1 for a skip, 0 for everything else), a break taking no more than that is passive, and it
        /// spends the credit as it passes, so anything the player really types afterwards arms the
        /// next break normally. A correct character after the skipping space puts the run at 2 and the
        /// next break takes the claim exactly as it always did. The old arm is
        /// <see cref="SkipSpaceCreditRule.AStreakLikeAnyOther"/>.</para>
        ///
        /// <para>Nothing else about a typo changes here: the wrong keypress is still counted
        /// (<see cref="Mistyped"/>) and still costs the accuracy denominator. Health does move for a
        /// typo since backlog 166, but on its own pair of seams and not on this one: the keypress
        /// drains it and the ERASE refunds it (<see cref="TypoErased"/>), so HP is settled the same
        /// way whether or not the correction that follows has a streak left to claim.</para>
        /// </summary>
        public event Action<int>? ComboRestored;

        /// <summary>
        /// A backspace just erased a typed-through WRONG character (backlog 166): the cell held the
        /// <see cref="JudgementType.WrongChar"/> a keypress wrote and is empty again. The mirror
        /// image of that keypress, and the only way a cell ever leaves <see cref="CellState.Wrong"/>,
        /// so the two bracket the typo exactly.
        ///
        /// <para>HEALTH is what listens (see <see cref="Scoring.TypeBeatHealthFeed.OnTypoErased"/>): a typo drains
        /// HP the moment it is typed rather than at the line seal, and erasing it refunds that
        /// drain, so typing a character wrong, backspacing and retyping it correctly leaves the bar
        /// exactly where typing it right first time would have. Nothing else moves: the mistype
        /// COUNT is spent for good, and the streak the keypress broke comes back at the corrected
        /// RETYPE (<see cref="ComboRestored"/>), not here, because an erase alone fixes nothing.</para>
        ///
        /// <para>Raised from <see cref="ProcessBackspace"/> with the cell already cleared. Erasing a
        /// CORRECT character raises nothing: there was no drain to give back.</para>
        /// </summary>
        public event Action? TypoErased;

        /// <summary>
        /// A word skip just put cells into <see cref="CellState.Abandoned"/> (backlog 167). The
        /// mirror image of <see cref="TypoErased"/>'s partner drain, one word wide instead of one
        /// character: raised once per skip, carrying every cell it gave up.
        ///
        /// <para>TWO things ride on it, and neither can travel on a result, because the skip applies
        /// none (see <see cref="JudgementType.Abandoned"/>). HEALTH drains
        /// <see cref="Scoring.TypeBeatHealthProcessor.MISS_HEALTH_DRAIN"/> per cell, NOW, because the
        /// bar is the account a typist reads while typing (backlog 166's rule); it is given back the
        /// moment a cell leaves the phantom state, by either exit. And osu's incrementally-maintained
        /// <c>Combo</c> is zeroed by hand, exactly as <see cref="Mistyped"/> zeroes it, because the
        /// engine has taken its break here and the Miss results that used to carry it now arrive at
        /// the seal, a whole line later.</para>
        ///
        /// <para>Raised AFTER <see cref="ComboBroken"/> and BEFORE the per-cell
        /// <see cref="CharJudged"/> announcements, with the engine already settled.</para>
        /// </summary>
        public event Action<AbandonedCells>? WordAbandoned;

        /// <summary>
        /// A backspace stepped back into an abandoned word and put its cells back to
        /// <see cref="CellState.Untyped"/> (backlog 167): they are ordinary untyped characters again
        /// and the caret is inside the word. One of the two exits from the phantom state, so HEALTH
        /// refunds exactly what <see cref="WordAbandoned"/> drained for them and nothing else moves:
        /// the combo the skip broke comes back at the RETYPE (<see cref="ComboRestored"/>), not here,
        /// because an erase alone fixes nothing. This is <see cref="TypoErased"/>'s rule, one word
        /// wide.
        /// </summary>
        public event Action<AbandonedCells>? AbandonReclaimed;

        /// <summary>
        /// The line sealed on cells the player never came back for (backlog 167): they are
        /// <see cref="CellState.Missed"/> now and their line is about to resolve them as ordinary
        /// misses. The other exit from the phantom state, raised from <see cref="Update"/>'s seal
        /// loop immediately BEFORE <see cref="LineSealed"/>, so a consumer can settle them before the
        /// results land.
        ///
        /// <para>Two things ride on it, and together they are what makes a never-reclaimed skip cost
        /// exactly what it cost before backlog 167. HEALTH refunds the skip's drain, because the Miss
        /// each cell is about to take carries the very same drain; the pair nets to one charge per
        /// cell, by construction rather than by bookkeeping. And each cell is marked COMBO-NEUTRAL
        /// (<see cref="Scoring.TypeBeatScoreProcessor.MarkComboNeutral"/>), because its break was
        /// taken at the skip: a Miss landing here would otherwise break osu's combo a second time,
        /// wiping a run the player rebuilt through the rest of the line while the engine's own combo
        /// kept it.</para>
        /// </summary>
        public event Action<AbandonedCells>? AbandonSealed;

        /// <summary>
        /// The engine has been re-derived to an EARLIER time (see <see cref="Rebuild"/>): every piece
        /// of state a subscriber tracks incrementally, cell states included, has just changed
        /// underneath it and must be re-read.
        ///
        /// <para>This is the ONE event a rebuild raises. The keystrokes it walked back over are not
        /// re-announced, so a subscriber must not treat this as a stream of judgements: it is a
        /// single "everything you knew is stale" edge.</para>
        /// </summary>
        public event Action? Rewound;

        /// <summary>
        /// True while <see cref="Rebuild"/> is re-deriving state, which is the whole of how a rebuild
        /// stays silent: every event goes through <see cref="raise(Action?)"/>.
        /// </summary>
        private bool rebuilding;

        private void raise(Action? handler)
        {
            if (!rebuilding)
                handler?.Invoke();
        }

        private void raise<T>(Action<T>? handler, T argument)
        {
            if (!rebuilding)
                handler?.Invoke(argument);
        }

        private readonly List<TypingLine> lines;
        private readonly bool[] lineSealed;

        /// <summary>
        /// Which lines the player WALKED OUT OF with a line skip (<see cref="ProcessEnter"/>), i.e.
        /// parked the caret past the last cell of while typeable cells were still untyped. Parallel
        /// to <see cref="lineSealed"/> and read by exactly one thing, <see cref="sealPermitted"/>,
        /// which grants an abandoned line the same drag grace a caret still sitting on it would have.
        ///
        /// <para>WHY IT HAS TO BE REMEMBERED. Without it, an Enter skip would move the line's seal
        /// EARLIER (up to <see cref="FLETCHER_DRAG_GRACE_MS"/>) than the identical run that simply
        /// stopped typing there, because the deferral in <see cref="sealPermitted"/> keys on the
        /// caret and the caret has moved on. The seal is where the abandoned cells become misses and
        /// where the line's one combo break is taken, so an earlier seal would drain health earlier
        /// and re-price every keypress made on the NEXT line in between at a combo the player had not
        /// actually lost yet. Holding the grace is what makes the skip PURE CARET MOVEMENT and is
        /// therefore why it needs no era bit of its own: nothing judged changes value or timing.</para>
        ///
        /// <para>An array rather than a single index because a fast player can abandon line N and be
        /// on line N+1 (and abandon that too) before N has sealed, and losing N's grace to N+1 is the
        /// very defect this exists to prevent. Entries are never cleared on seal, since
        /// <see cref="sealPermitted"/> is only ever asked about <see cref="nextSealIndex"/> and that
        /// only moves forward; <see cref="reset"/> clears the whole array like every other
        /// accumulator, which is what keeps a backwards seek reproducible.</para>
        /// </summary>
        private readonly bool[] lineAbandoned;
        private readonly Dictionary<JudgementType, int> counts = new Dictionary<JudgementType, int>();
        private readonly List<SyncSample> syncTimeline = new List<SyncSample>();

        private readonly int totalTypeableCells;

        private int nextSealIndex; // first line not yet sealed; lines seal strictly in order
        private int activeLineIndex = -1;
        private int caretIndex;
        private bool isFinished;
        private int totalKeypresses;
        private int correctKeypresses;
        private int errorCount;
        private int consecutiveWrongKeys;

        private double? lastUpdateTime;

        public TypingEngine(LyricBeatmap beatmap, bool literate = false, bool polyglot = false, string? polyglotLanguage = null)
        {
            Beatmap = beatmap ?? throw new ArgumentNullException(nameof(beatmap));

            // Assigned here as well as in applyWindowScale (which sets the same value at the default
            // scale of 1) because definite assignment of a get-only-outside property cannot see
            // through a helper call.
            Windows = SyncWindows.Default;
            applyWindowScale();

            Literate = literate;
            CaseSensitive = literate;
            Polyglot = polyglot;
            UsesJapaneseRomaji = polyglot && JapaneseReading.IsJapanese(polyglotLanguage);
            JapaneseRomajiInput = UsesJapaneseRomaji;

            lines = new List<TypingLine>(beatmap.Lines.Count);

            foreach (var line in beatmap.Lines)
                lines.Add(TypingLine.ForMods(line, literate, polyglot, polyglotLanguage));

            lineSealed = new bool[lines.Count];
            lineAbandoned = new bool[lines.Count];

            foreach (var line in lines)
                totalTypeableCells += line.TypeableCount;

            countTimedCells();

            foreach (JudgementType type in Enum.GetValues<JudgementType>())
                counts[type] = 0;

            countable = new CountableStream(lines);
        }

        /// <summary>
        /// Re-derive the whole run from the beginning: wipe every piece of progress, let
        /// <paramref name="replay"/> feed the engine back up to wherever it should now be, then raise
        /// <see cref="Rewound"/> exactly once. The seam a BACKWARDS SEEK during replay or autoplay
        /// playback goes through (see <c>TypeBeatPlayfield.EngineTicker</c>), and the only supported
        /// way to move this engine's state backwards: <see cref="Update"/> is monotonic by
        /// construction, so nothing else can.
        ///
        /// <para>The feed is EXACT rather than approximate, because a replay holds the whole
        /// keystroke sequence and judgement is a pure function of it (the argument
        /// <see cref="Scoring.TypeBeatReplayScorer"/> is built on). Re-deriving to time T therefore
        /// lands on the state a run watched straight to T would have been in.</para>
        ///
        /// <para>The feed is also SILENT: not one <see cref="CharJudged"/>, <see cref="LineSealed"/>,
        /// <see cref="Mistyped"/> or <see cref="ComboRestored"/> escapes it. Re-announcing thousands
        /// of keystrokes would be wrong in both directions at once: a cell takes exactly ONE osu
        /// result (<c>DrawableTypeBeatCharObject.ApplyEngineResult</c> drops every later one), so the
        /// re-announced judgements of the cells that were NOT rewound past would be silently dropped,
        /// while the hand-mirrored counters that ride on <see cref="Mistyped"/> would take a second
        /// copy of every wrong keypress in the run. <see cref="Rewound"/> replaces the whole stream
        /// with one "re-read everything" edge instead.</para>
        ///
        /// <para>IN PLACE, never by constructing a fresh engine: <see cref="Lines"/> and every
        /// <see cref="TypingCell"/> in them are handed out once and held for the life of the play by
        /// the stage's line displays, so a replacement engine would leave the display bound to cells
        /// nothing writes to any more.</para>
        /// </summary>
        /// <param name="replay">Feeds this engine forward to the new time, normally with
        /// <c>ReplayEngineFeed</c>. Runs exactly once.</param>
        public void Rebuild(Action<TypingEngine> replay)
        {
            ArgumentNullException.ThrowIfNull(replay);

            reset();

            rebuilding = true;

            try
            {
                replay(this);
            }
            finally
            {
                rebuilding = false;
            }

            Rewound?.Invoke();
        }

        /// <summary>
        /// Every piece of RUN state back to the moment before the first frame: cell states, the
        /// caret, the seals, the counters, the WPM clock, the rolling-WPM ring and the sync timeline.
        ///
        /// <para>What is deliberately NOT touched is everything that describes the run rather than
        /// its progress, i.e. everything set from outside after construction: the replay CONFIG bits
        /// (<see cref="AllowWrongInput"/>, <see cref="SpaceSkipsWord"/>,
        /// <see cref="SyllableTiming"/>, <see cref="WrongInputOnWordGaps"/>,
        /// <see cref="StrictSpaces"/>, <see cref="BackDatedSealBreak"/>,
        /// <see cref="UnhalvedHardRockWindows"/>, and the second word's <see cref="RushCapCostsAccuracy"/>,
        /// <see cref="InputEra2"/>, <see cref="AuthoredSyllablesOnly"/> and <see cref="EarlyFinish"/>), the mod flags
        /// (<see cref="FletcherEnabled"/>, <see cref="MashingEnabled"/>, <see cref="Literate"/>,
        /// <see cref="CaseSensitive"/>, <see cref="HardRockFromMod"/>),
        /// <see cref="WindowScale"/> and the era rules
        /// (<see cref="ComboRestore"/>, <see cref="ComboClaim"/>, <see cref="SkipSpaceCredit"/>,
        /// <see cref="SpaceTiming"/>, <see cref="WordSkip"/>, <see cref="OffTime"/>,
        /// <see cref="CorrectionCredit"/>). A rebuild re-judges the same run, not a different one. The CONFIG frame is re-fed anyway, being the
        /// first frame of every replay, so those bits land on the same values a second time.</para>
        ///
        /// <para>The PHANTOM state backlog 167 added needs nothing of its own here: it lives on the
        /// cells, which are all put back to <see cref="CellState.Untyped"/> by the loop below, and
        /// the snapshot a skip leaves is the same <c>restorable</c> field a typo leaves.</para>
        /// </summary>
        private void reset()
        {
            clearJapanesePending();
            japaneseCompletionLine = japaneseCompletionCaret = -1;
            japaneseWpmInputs = 0;
            foreach (var line in lines)
            {
                foreach (var cell in line.Cells)
                {
                    cell.State = CellState.Untyped;
                    cell.TypedChar = null;
                    cell.JudgedDelta = null;
                    cell.JudgedTier = null;
                    cell.FirstCorrectDelta = null;

                    // The one place backlog 210's correction flag is ever cleared: it survives a
                    // backspace by design, so only a whole-run rebuild puts it back.
                    cell.HeldWrongBeforeJudged = false;

                    // ...and backlog 347's rush-cap flag, on the same terms: history, so only a
                    // whole-run rebuild clears it.
                    cell.JudgedPastRushCap = false;
                }
            }

            Array.Clear(lineSealed);
            Array.Clear(lineAbandoned);
            Array.Clear(rollingSamples);
            syncTimeline.Clear();

            foreach (JudgementType type in Enum.GetValues<JudgementType>())
                counts[type] = 0;

            nextSealIndex = 0;
            activeLineIndex = -1;
            caretIndex = 0;
            isFinished = false;

            ledger.Reset();
            totalKeypresses = 0;
            correctKeypresses = 0;
            errorCount = 0;
            consecutiveWrongKeys = 0;

            activeRealTimeMs = 0;
            wpmClockArmedLine = -1;
            wpmClockArmedAt = 0;
            lastUpdateTime = null;

            rollingCount = 0;
            rollingNext = 0;
        }

        /// <summary>
        /// Call once per frame BEFORE routing input for that frame.
        /// Drives activation, sealing, active-time accrual, Finished.
        /// Never throws on out-of-order times (dt is clamped >= 0; lines never unseal).
        /// </summary>
        /// <param name="time">The BEATMAP time to advance to, in milliseconds.</param>
        /// <param name="clockRate">
        /// The speed-adjusting-mod rate that applied over the segment ENDING at <paramref name="time"/>,
        /// i.e. over [previous update time, <paramref name="time"/>]: 1.5 under Double Time, 0.75 under
        /// Half Time, whatever the slider says at a custom rate, and the CURRENT ramp value under
        /// ModWindUp / ModWindDown. Only the WPM clock uses it, dividing that segment's beatmap
        /// milliseconds back into real ones (see <see cref="activeRealTimeMs"/>); judgement is
        /// untouched. Per-segment rather than per-run is what makes a rate that varies across the play
        /// accrue piecewise instead of being smeared by whatever value happened to be sampled last.
        /// The value is sanitised before dividing (magnitude only; zero and non-finite fall back to 1),
        /// so a rewinding, paused or otherwise degenerate clock can never produce a negative, infinite
        /// or NaN active time. Defaults to 1, the no-rate-mod case.
        /// </param>
        public void Update(double time, double clockRate = 1)
        {
            accrueActiveTime(time, clockRate);

            // A final n is already a complete reading. If no confirmation key arrives before the
            // line ends, resolve it at its typed timestamp before sealing can turn it into a miss.
            if (activeLineIndex >= 0 && time >= lines[activeLineIndex].EndTime)
                tryCommitJapanesePending();

            // Whether the caret ends this update on a line it was not on when the update started.
            // Three things can move it (a drag cutoff inside the seal loop below, the ordinary
            // time-driven activation, and the flexible-lines snap), and all three announce through
            // the single raise at the end, so a catch-up cascade through several stale lines still
            // relayouts the stage exactly once.
            bool pendingActivation = false;

            // (2) Seal, in order, every line whose deadline has passed. Normal lines seal AT
            //     EndTime; lines with a seal grace (vocals overrunning into the next line, or a
            //     boundary-pinned last target) stay typeable through the grace window and seal
            //     early the moment nothing is left to type, so the next line isn't held up.
            while (nextSealIndex < lines.Count && (canSeal(lines[nextSealIndex], time) || finishedEarly(nextSealIndex)) && sealPermitted(nextSealIndex, time))
            {
                if (sealLine(nextSealIndex))
                    pendingActivation = true;
            }

            // (3) Activate strictly by time: the first unsealed line, while it is judgeable
            //     (ActivationTime <= time < EndTime + grace). ActivationTime is the constant cue
            //     before the first word (CUE_LEAD_MS), not the boundary; crossing a boundary
            //     scrolls the stack (the seal above), but typing opens relative to the vocals.
            //     Typing never unlocks the next line.
            if (activateByTime(time))
                pendingActivation = true;

            // (4) FLEXIBLE-LINES SNAP (backlog 208): the caret is sitting past the last character of
            //     its line and the next line has just started, so hand the player onto it, exactly
            //     as the pinned arm above would have. Placed after the activation arm and sharing
            //     its announcement so a snap that follows a fresh activation (or a drag cutoff, or
            //     several snaps at once) still relayouts the stage once.
            if (activeLineIndex != -1 && snapForwardOnLineStart(time))
                pendingActivation = true;

            // (5) A DECLARED PLAY START: the caret starts where the PLAY did, not where the LINE did. A
            //     play that begins in the middle of a line begins past the characters that line has
            //     already sung - which is the whole point of the editor's test play starting at the
            //     mapper's playhead - so the caret is walked up to the first character still to come
            //     rather than left owing the ones behind it. Placed after the ordinary activation above
            //     (which is what puts the caret on the line in the first place) and before the
            //     announcement below, so the stage is told about the caret the play actually starts
            //     with. One shot: only the first update after the declaration.
            if (playStartPending)
            {
                playStartPending = false;
                alignCaretToThePlayStart();
            }

            // The caret moved (a fresh activation, a drag cutoff inside the seal loop, or a snap);
            // announce it exactly once. Guarded on there being a line to announce: a cutoff that
            // cascaded off the end of the map has already parked the caret nowhere and finished the
            // run above.
            if (pendingActivation && activeLineIndex != -1)
            {
                autoSkipForward();
                raise(LineActivated, activeLineIndex);
            }
        }

        /// <summary>
        /// Step (1) of <see cref="Update"/>: the WPM clock's accrual over the frame ending at
        /// <paramref name="time"/>, and the frame time it is measured from next.
        /// </summary>
        private void accrueActiveTime(double time, double clockRate)
        {
            // (1) Accrue active time while a line is active AND incomplete AND not finished
            //     (state as of the previous frame), over the part of the frame the clock actually
            //     runs for. That is the whole frame except when the pre-cue lazy arm (backlog 222,
            //     see clockRunsFrom) falls inside it, where it is the stretch after the arm.
            //
            //     KNOWN AND LEFT ALONE: the predicate is evaluated on the CURRENT activeLineIndex but
            //     with the PREVIOUS frame's time, so on a frame the caret rolled forward inside, the
            //     part of it spent typing on the OLD line is tested against the NEW line's cue and
            //     dropped. That is at most one frame of real typing time per line transition (and
            //     only when a keypress-driven roll lands BETWEEN two frames), and splitting the
            //     interval would need the old line index and the transition instant carried as extra
            //     state, which is more machinery than the milliseconds are worth.
            if (lastUpdateTime is double last)
            {
                if (activeLineIndex != -1 && !IsLineComplete && !isFinished && clockRunsFrom(last) is double from)
                    activeRealTimeMs += Math.Max(0, time - from) / sanitisedRate(clockRate);
            }

            lastUpdateTime = time;
        }

        /// <summary>
        /// Step (2) of <see cref="Update"/>, once per line due: seal line <paramref name="index"/> (the next
        /// one due, which the caller has already found may seal), resolving its untyped cells as misses
        /// with at most one combo break, and hand a dragging caret on. Returns whether the caret was handed
        /// on to the next line, which the caller announces once.
        /// </summary>
        private bool sealLine(int index)
        {
            bool handedOn = false;

            var line = lines[index];

            int missed = 0;

            // Of those, the ones the line really did run out of time on: the only group whose
            // break has not been taken yet, and therefore the only one that can break combo here.
            int unforeseen = 0;

            // The LAST of them, in cell order: the position this line's break is back-dated to
            // (see BackDatedSealBreak). Meaningless while unforeseen is 0, which is exactly when
            // nothing reads it.
            int lastUnforeseenCell = -1;

            // Cells a word skip had abandoned and the player never came back for (backlog 167),
            // in ascending cell order. Null while there are none, which is every seal on a run
            // that never skipped a word.
            List<int>? abandoned = null;

            for (int i = 0; i < line.Cells.Count; i++)
            {
                var cell = line.Cells[i];

                // MISSED at seal: a typeable cell nobody typed, and ONLY that. Two states
                // qualify, and they differ in one thing only, whether the break has been taken.
                //
                // A cell left sitting WRONG is not one (backlog 124, reversing the predicate
                // backlog 109 widened): the player finished that character, they just got it
                // wrong, which is a mistype and not a miss. It keeps CellState.Wrong so the line
                // still shows which character went wrong, it takes no engine miss, and it does
                // not break the engine's combo here, because its break was already taken at the
                // keypress. That is what puts the HUD combo back in agreement with the submitted
                // max_combo (backlog 123); the cell's own osu result is decided on the drawable
                // side by TypeBeatResultMapping.UnresolvedCellResult.
                //
                // An ABANDONED cell (backlog 167) is a miss, and this is where it becomes one:
                // the player skipped its word and never reclaimed it, so it turned out to be a
                // character they never typed. It counts and resolves exactly as an untyped cell
                // does, and the ONE thing it does not do is break combo, for precisely the
                // reason a still-Wrong cell does not: that break was taken at the skip.
                if (!cell.IsTypeable)
                    continue;

                // A recoverable typo gets its final zero-credit result only when the line seals.
                // Preserve any earlier award: erasing and mistyping a judged cell cannot revoke it.
                if (cell.State == CellState.Wrong)
                    cell.JudgedTier ??= JudgementType.Miss;

                bool phantom = cell.State == CellState.Abandoned;

                if (cell.State != CellState.Untyped && !phantom)
                    continue;

                cell.State = CellState.Missed;

                missed++;
                counts[JudgementType.Miss]++;

                if (phantom)
                {
                    (abandoned ??= new List<int>()).Add(i);
                }
                else
                {
                    unforeseen++;
                    lastUnforeseenCell = i;
                }
            }

            bool broke = unforeseen >= 1;

            // What the break leaves the player holding: 0 under the classic era, and under
            // BackDatedSealBreak the increments earned strictly past the last missed cell.
            int survivingCombo = 0;

            if (broke)
            {
                // AT MOST ONE combo break per sealed line, no matter how many cells were missed.
                // BACK-DATED since backlog 259 (see BackDatedSealBreak): the break belongs to the
                // cells the line ran out of time on, so it destroys the run as it stood AT the
                // last of them and leaves everything earned past it standing. The classic era
                // takes the whole run, which is what every stored replay was scored under.
                if (BackDatedSealBreak)
                    survivingCombo = backDateBreakTo(new ComboPosition(index, lastUnforeseenCell));
                else
                    breakRun();

                // A real break, so it owns the streak. Distinct from the line-scoped drop
                // below: under Fletcher the caret can already be on a LATER line, holding a
                // snapshot this break has just cost it.
                //
                // Unconditional under both eras, a partial survival included: a claim's streak
                // was earned EARLIER than the run this break cuts back, so redeeming it later
                // could only put back combo the break was entitled to take.
                discardRestorableStreak();
            }

            // A sealed line's cells can never be typed again, so a snapshot left on this one is
            // unredeemable whether or not the seal broke anything. Dropping it here keeps the
            // state truthful rather than relying on the caret never going back.
            ledger.DropClaimOn(index);

            lineSealed[index] = true;
            nextSealIndex++;

            if (activeLineIndex == index)
            {
                if (FletcherEnabled && index + 1 < lines.Count)
                {
                    // Drag cutoff: the player ran out of borrowed time mid-line. Land them on the
                    // next line immediately rather than in a dead zone. Setting the caret here,
                    // inside the loop, is also what stops one cutoff cascading into the line the
                    // player just landed on: the next sealPermitted call sees them on it and
                    // grants it its own drag grace. A cascade still happens when that grace has
                    // ALSO expired (an idle player), which is the intended catch-up to the song.
                    activeLineIndex = index + 1;
                    caretIndex = 0;
                    handedOn = true;
                }
                else
                {
                    activeLineIndex = -1;
                    caretIndex = 0;
                }
            }

            // BEFORE the seal itself, so a consumer settles the phantom cells before their Miss
            // results land on them: health gives the skip's drain back into the very drain those
            // misses are about to take, and the ledger that keeps them from breaking osu's combo
            // a second time has to be written before the result consults it.
            if (abandoned != null)
                raise(AbandonSealed, new AbandonedCells(index, abandoned));

            if (broke)
                raise(ComboBroken);

            raise(LineSealed, new LineSealResult(index, missed, broke, survivingCombo));

            return handedOn;
        }

        /// <summary>
        /// Step (3) of <see cref="Update"/>: finish the run once every line has sealed, or activate the
        /// first unsealed line by time. Returns whether a line was activated.
        /// </summary>
        private bool activateByTime(double time)
        {
            if (nextSealIndex >= lines.Count)
            {
                if (!isFinished)
                {
                    isFinished = true;
                    activeLineIndex = -1;
                    caretIndex = 0;
                    raise(Finished);
                }
            }
            else if (activeLineIndex == -1)
            {
                var candidate = lines[nextSealIndex];

                if (time >= candidate.ActivationTime && time < EffectiveSealTime(candidate))
                {
                    activeLineIndex = nextSealIndex;
                    caretIndex = 0;
                    autoSkipForward();
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Signed lead/lag of the caret cell: time - Cells[CaretIndex].TargetTime;
        /// null when no judgeable caret cell (no active line / line complete / finished).
        /// </summary>
        public double? CurrentLeadLag(double time)
        {
            if (isFinished || activeLineIndex == -1)
                return null;

            var cells = lines[activeLineIndex].Cells;

            if (caretIndex >= cells.Count)
                return null;

            var cell = cells[caretIndex];

            if (!cell.IsTypeable)
                return null; // defensive: caret normally rests on a typeable cell.

            return time - cell.TargetTime;
        }

        /// <summary>
        /// Leave the caret on the LEFTMOST cell of the line that still needs a keystroke:
        /// <see cref="autoSkipForward"/> exactly as before, plus, under
        /// <see cref="AnyOrderWithinWord"/> only, the run of cells already typed OUT OF ORDER that
        /// the caret would otherwise be parked in the middle of. Called from every caret advance in
        /// <see cref="ProcessKey"/>, so the frontier invariant holds after a correct press, after a
        /// typo typed through, and after a space stepping over a spoiled gap alike.
        ///
        /// <para>Only a <see cref="CellState.Correct"/> cell is walked over, which is exactly the one
        /// state the mod can leave AHEAD of the caret and no more: a Wrong cell is one the caret is
        /// PARKED on under <see cref="StrictSpaces"/> and must not be walked off, and an Abandoned
        /// one only ever sits behind the caret (<see cref="skipCurrentWord"/> leaves the caret past
        /// the word it gave up, and the backspace that reclaims one resets it to Untyped first).</para>
        ///
        /// <para>Without the mod the loop cannot run at all, because nothing can resolve a cell ahead
        /// of the caret, so the pinned path is a plain <see cref="autoSkipForward"/> call.</para>
        /// </summary>
        private void advanceCaretToFrontier()
        {
            autoSkipForward();

            if (!AnyOrderWithinWord || activeLineIndex == -1)
                return;

            var cells = lines[activeLineIndex].Cells;

            while (caretIndex < cells.Count && cells[caretIndex].State == CellState.Correct)
            {
                caretIndex++;
                autoSkipForward();
            }
        }

        /// <summary>Hop the caret forward over non-typeable cells, marking them AutoSkipped.</summary>
        private void autoSkipForward()
        {
            if (activeLineIndex == -1)
                return;

            var cells = lines[activeLineIndex].Cells;

            while (caretIndex < cells.Count && !cells[caretIndex].IsTypeable)
            {
                cells[caretIndex].State = CellState.AutoSkipped;
                caretIndex++;
            }
        }
    }
}
