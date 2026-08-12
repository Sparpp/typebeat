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
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed class TypingEngine
    {
        /// <summary>
        /// How long before a line's first typeable cell's target the line becomes typeable
        /// (<see cref="TypingLine.ActivationTime"/>). Also the length of the on-screen approach
        /// cue, so the depleting bar exactly spans "you may type now" to "the word lands".
        /// </summary>
        public const double CUE_LEAD_MS = 1500;

        /// <summary>
        /// Fletcher mod: how many COUNTABLE characters (typeable and not a space, the same currency
        /// the Flashlight window measures in) the player's caret may sit ahead of the playhead before
        /// a keypress stops earning combo. The press still lands and still scores; it simply cannot
        /// build a combo while the caret is out past the cap (see <see cref="FletcherEnabled"/>).
        /// </summary>
        public const int FLETCHER_MAX_CHARS_AHEAD = 5;

        /// <summary>
        /// Fletcher mod: extra time past a line's normal hard deadline
        /// (<see cref="TypingLine.EndTime"/> + <see cref="TypingLine.SealGraceMs"/>) that the engine
        /// holds the line open while the PLAYER is still on it, so a dragging player may finish the
        /// line the song has already left. Deliberately the same magnitude as
        /// <see cref="CUE_LEAD_MS"/>: the beat of grace the game gives you to get ready, granted at
        /// the other end of the line as well. Bounded so a run always terminates: past it the line
        /// force-seals, its untyped cells become misses, and the caret lands on the next line.
        /// </summary>
        public const double FLETCHER_DRAG_GRACE_MS = 1500;

        private const int combo_cap = 50;

        /// <summary>How many of the most recent correct keypresses <see cref="LiveRollingWpm"/> averages over.</summary>
        private const int rolling_wpm_window = 30;

        public LyricBeatmap Beatmap { get; }

        public IReadOnlyList<TypingLine> Lines => lines;

        /// <summary>
        /// What a keypress's offset from its cell is measured in, and therefore which ladder of
        /// <see cref="SyncWindows"/> judges it. <see cref="SyncMeasure.CharacterDistance"/> is the
        /// live rule (backlog 133); nothing in the game selects
        /// <see cref="SyncMeasure.Milliseconds"/> yet, and the Rhythmic mod (backlog 135) is what
        /// will, by setting this the way <see cref="MashingEnabled"/> and
        /// <see cref="FletcherEnabled"/> are set. It must be set BEFORE the first keypress and left
        /// alone for the rest of the run: judgements already awarded are not revisited, so a play
        /// that changed measure mid-run would carry two different rules in one score.
        /// </summary>
        public SyncMeasure Measure { get; set; } = SyncMeasure.CharacterDistance;

        /// <summary>
        /// Whether correcting a wrong cell resumes the streak its keypress broke (see
        /// <see cref="ComboRestored"/>). <see cref="ComboRestoreRule.OnFix"/> is the live rule
        /// (backlog 140) and the default; only <see cref="Scoring.TypeBeatReplayScorer"/> ever sets
        /// the other one, to re-derive a score from before the restore existed. Like
        /// <see cref="Measure"/> it must be set BEFORE the first keypress and left alone afterwards:
        /// combo already awarded is never revisited.
        /// </summary>
        public ComboRestoreRule ComboRestore { get; set; } = ComboRestoreRule.OnFix;

        /// <summary>The window set the beatmap's own granularity gets under the current <see cref="Measure"/>.</summary>
        public SyncWindows Windows => SyncWindows.For(Beatmap.Granularity, Measure);

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
        /// Whether the PLAYHEAD is inside a typeable line window: the plain time rule
        /// (ActivationTime &lt;= now &lt; EndTime + SealGraceMs on the first unsealed line), read
        /// independently of where the player's caret has got to. Equal to <see cref="LineIsActive"/>
        /// without <see cref="FletcherEnabled"/>; under Fletcher the two diverge, because the caret
        /// can be parked on a line the song has not reached (rush) or still finishing one the song
        /// has left (drag). The key handler uses it so Space still reaches the skip overlay during a
        /// real instrumental gap.
        /// </summary>
        public bool SongWindowOpen
        {
            get
            {
                if (isFinished || nextSealIndex >= lines.Count || lastUpdateTime is not double time)
                    return false;

                var line = lines[nextSealIndex];

                return time >= line.ActivationTime && time < line.EndTime + line.SealGraceMs;
            }
        }

        /// <summary>
        /// True while the player has put nothing into the active line yet (no cell behind the caret
        /// is Correct or Wrong; leading auto-skipped punctuation does not count as progress). Used
        /// by the key handler under <see cref="FletcherEnabled"/> to tell "parked on a line I have
        /// not started" from "typing it".
        /// </summary>
        public bool ActiveLineUntouched
        {
            get
            {
                if (activeLineIndex == -1)
                    return false;

                var cells = lines[activeLineIndex].Cells;
                int end = Math.Min(caretIndex, cells.Count);

                for (int i = 0; i < end; i++)
                {
                    if (cells[i].State == CellState.Correct || cells[i].State == CellState.Wrong)
                        return false;
                }

                return true;
            }
        }

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

        public long Score => score;

        public int Combo => combo;

        public int MaxCombo => maxCombo;

        /// <summary>correctKeypresses / allCharKeypresses; 1.0 before any keypress.</summary>
        public double LiveAccuracy => totalKeypresses == 0 ? 1.0 : correctKeypresses / (double)totalKeypresses;

        /// <summary>
        /// Gross WPM over active time only; 0 before any active time. Active time is REAL elapsed
        /// time (see <see cref="activeRealTimeMs"/>), not beatmap time, so the readout is the
        /// player's actual typing speed under any speed-adjusting mod rather than 1/rate of it.
        /// </summary>
        public double LiveWpm
        {
            get
            {
                if (activeRealTimeMs <= 0)
                    return 0;

                return (countCorrectCells() / 5.0) / (activeRealTimeMs / 60000.0);
            }
        }

        /// <summary>
        /// Gross WPM over the last <see cref="rolling_wpm_window"/> correct keypresses: the HUD's live
        /// readout, so the number tracks how fast the player is typing RIGHT NOW instead of averaging the
        /// whole run flat. Display only; <see cref="LiveWpm"/> and <see cref="ResultsSummary.Wpm"/> keep
        /// the whole-run figure.
        /// Falls back to <see cref="LiveWpm"/> until the window holds at least two presses spread over a
        /// non-zero span, so the readout is meaningful from the first seconds instead of flapping.
        /// The clock is active time, exactly like <see cref="LiveWpm"/>: count-ins, instrumental gaps and
        /// post-line-completion waits do not decay the value, they simply do not pass. Being stamped in
        /// that same REAL-time currency (see <see cref="activeRealTimeMs"/>), this inherits the
        /// speed-adjusting-mod correction for free and must never be scaled by the rate a second time.
        /// </summary>
        public double LiveRollingWpm
        {
            get
            {
                if (rollingCount < 2)
                    return LiveWpm;

                // Once the ring is full, the next slot to write is also the oldest entry.
                double oldest = rollingSamples[rollingCount < rolling_wpm_window ? 0 : rollingNext];
                double newest = rollingSamples[(rollingNext + rolling_wpm_window - 1) % rolling_wpm_window];
                double spanMs = newest - oldest;

                // Every press in the window landed within a single frame, so active time never advanced
                // between them: there is no span to divide by, defer to the whole-run figure.
                if (spanMs <= 0)
                    return LiveWpm;

                // n presses bound n-1 inter-key gaps, so the span covers (n-1) chars' worth of typing,
                // not n. Using n would inflate the readout by n/(n-1) (3.4% at a 30-press window, and
                // badly more while the window is still filling).
                return ((rollingCount - 1) / 5.0) / (spanMs / 60000.0);
            }
        }

        /// <summary>Mean sync quality (x100) over cells resolved so far (judged correct + sealed); 100 before anything resolves.</summary>
        public double LiveSyncPercent
        {
            get
            {
                double sum = 0;
                int resolved = 0;

                for (int i = 0; i < lines.Count; i++)
                {
                    foreach (var cell in lines[i].Cells)
                    {
                        if (!cell.IsTypeable)
                            continue;

                        if (cell.State == CellState.Correct && cell.JudgedSyncQuality is double q)
                        {
                            sum += q;
                            resolved++;
                        }
                        else if (lineSealed[i])
                        {
                            // Missed / still-Wrong at seal: q = 0.
                            resolved++;
                        }
                    }
                }

                return resolved == 0 ? 100 : 100 * sum / resolved;
            }
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

        /// <summary>Mashing mod (Relax): every keypress is judged as the caret cell's expected char.</summary>
        public bool MashingEnabled { get; set; }

        /// <summary>
        /// Literate mod: when true, input is matched against the target's EXACT case (no
        /// <see cref="Typeability.Fold"/>), so a right letter typed in the wrong case is judged
        /// wrong: rejected/miss, exactly like any other wrong char. Off by default: gameplay is
        /// case-insensitive. Requires the input path to actually produce upper-case chars for
        /// Shift-held keys (see <see cref="KeyCharMap"/>), else capitals would be untypeable.
        /// Set from <see cref="Literate"/> at construction; still settable so a test can exercise
        /// exact-case matching on its own.
        /// </summary>
        public bool CaseSensitive { get; set; }

        /// <summary>
        /// Literate mod, the other half of what <see cref="CaseSensitive"/> does: the map's lines
        /// are typed EXACTLY as authored, supported punctuation included. Unlike every other mod
        /// flag this is fixed at construction, because it changes the CELL LIST itself (see
        /// <see cref="TypingLine.FromLyricLine"/>) rather than only how a press is judged, and the
        /// nested per-cell scoring objects have to be flattened the same way.
        /// Requires the input path to be able to produce the marks (see <see cref="KeyCharMap"/>).
        /// </summary>
        public bool Literate { get; }

        /// <summary>
        /// The DEFAULT typing model (backlog 107): wrong (non-space) characters are typed through
        /// and marked red instead of rejected, and can be backspaced, which is what every typing
        /// site does. ON by default; the <see cref="Mods.TypeBeatModGatekeeper"/> mod turns it off
        /// to get strict rejection back.
        ///
        /// <para>Deliberately still phrased as "allow wrong input" rather than as the mod's own
        /// name. This flag is what the replay CONFIG frame persists as a single bit (see
        /// <see cref="Replays.TypeBeatReplayFrame"/>), and that bit's meaning is fixed by every
        /// replay already on disk: 1 = wrong input allowed. Naming the property for the mod would
        /// invert it against the wire and force a negation at every encode/decode site, for nothing.</para>
        ///
        /// <para>Three consequences of the flip worth stating where the flag lives:</para>
        /// <list type="bullet">
        /// <item>The 13-in-a-row mash-fail streak (<see cref="ConsecutiveWrongKeys"/>) only ever
        /// accrued on the rejection path, so it is now a Gatekeeper-only guard. That is where it
        /// belongs: it exists to stop a masher farming a model that refuses wrong keys.</item>
        /// <item>Backspace is gated on this flag at the INPUT layer (see <c>TypeBeatPlayfield</c>),
        /// so it is now live by default and inert under Gatekeeper, which is the same rule as
        /// before ("erasing exists only where an erasable char can land") resolving the other way.</item>
        /// <item>A wrong char typed through does NOT resolve its cell against the score processor
        /// (backlog 109). A miss is a character the line ran out of time on; a typo is a typo, and
        /// backspace makes it fixable, so the cell's result waits to see which of the two it turns
        /// out to be. Uncorrected it resolves at the seal as
        /// <see cref="Scoring.TypeBeatResultMapping.UNFIXED_TYPO"/> and NOT as a miss (backlog 124):
        /// the cell was finished, just wrongly. It still counts as one judged note, so accuracy, the
        /// combo ratio and the pp length term stay honest, and it still costs the mistype and the
        /// combo break it took at the keypress; what it no longer costs is rank and the miss
        /// count.</item>
        /// </list>
        /// </summary>
        public bool AllowWrongInput { get; set; } = true;

        /// <summary>
        /// "Space to skip current word" (backlog 110), a local SETTING and not a mod, OFF by default.
        /// When on, a space pressed while the caret sits inside a word abandons the rest of that word
        /// and lands the caret on the word gap, so one bad character costs a word instead of the run.
        ///
        /// <para>What "abandons" means precisely: every <see cref="CellState.Untyped"/> cell of that
        /// word takes a <see cref="JudgementType.Miss"/> right now, exactly as the seal loop's misses
        /// do. Nothing else does. A cell typed CORRECTLY has handed its Great over and there is no
        /// un-apply (<c>DrawableTypeBeatCharObject.ApplyEngineResult</c> drops every later result on
        /// an already-judged cell). A cell typed WRONG is a cell the player finished, so abandoning
        /// the word cannot make it a miss (backlog 124); its deferred result is decided at the seal
        /// like every other unfixed typo, and until then backspacing back into the word can still
        /// fix it.</para>
        ///
        /// <para>The press itself is NOT a keypress judgement: it never enters the accuracy counters
        /// and never counts as a <see cref="Mistyped"/>, because it is a deliberate control action
        /// rather than a typo. It costs the abandoned cells (completion, sync and the osu-side
        /// accuracy) plus one combo break, and it can only ever LOSE cells, never earn any, which is
        /// why it needs no score or pp multiplier despite being judgement-relevant.</para>
        ///
        /// <para>Orthogonal to <see cref="AllowWrongInput"/>: Gatekeeper is about wrong LETTERS (is a
        /// mistyped char written into the cell or refused), this is about abandoning a WORD, so both
        /// combinations are meaningful and the skip works under Gatekeeper too. It is inert under
        /// <see cref="MashingEnabled"/>, where a pressed space has already been rewritten into the
        /// cell's expected char before this is reached: mashing makes every key the right key, so no
        /// word can ever need abandoning.</para>
        ///
        /// <para>Judgement-relevant, so it travels in the replay CONFIG frame as bit 1 (see
        /// <see cref="Replays.TypeBeatReplayFrame"/>).</para>
        /// </summary>
        public bool SpaceSkipsWord { get; set; }

        /// <summary>
        /// Fletcher mod ("Were you Rushing or were you Dragging?!"): decouples the player's caret
        /// from the song's playhead. Three behaviours, all confined to this flag so the default path
        /// stays byte-identical:
        /// <list type="bullet">
        /// <item>RUSH FREEDOM: finishing a line moves the caret straight on to the next one instead
        /// of waiting for its cue (<see cref="rollForwardIfFinishedEarly"/>). The finished line is
        /// left unsealed and seals on its own normal deadline with nothing missed.</item>
        /// <item>DRAG FREEDOM: a line the player is still typing is not force-sealed at its normal
        /// deadline; the seal is deferred by <see cref="FLETCHER_DRAG_GRACE_MS"/> so the caret is
        /// never yanked off a line mid-word (<see cref="sealPermitted"/>).</item>
        /// <item>CHARACTER-DISTANCE RUSH CAP: a press that puts the caret more than
        /// <see cref="FLETCHER_MAX_CHARS_AHEAD"/> countable chars ahead of the playhead lands and
        /// scores as normal but earns no combo.</item>
        /// </list>
        /// Per-char judgement windows are untouched: rushing reads as early deltas and dragging as
        /// late ones, so accuracy, sync% and the judgement counts report the drift honestly.
        /// </summary>
        public bool FletcherEnabled { get; set; }

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
        /// carry it (see <c>TypeBeatPlayfield.onMistyped</c>). Sudden Death fails the play from here
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
        /// <c>TypeBeatPlayfield.onComboRestored</c> and
        /// <see cref="Scoring.TypeBeatScoreProcessor.RestoreCombo"/>).</para>
        ///
        /// <para><b>What is restorable, and for how long.</b> A wrong keypress SNAPSHOTS the streak
        /// it breaks against the cell it spoiled, and correcting that cell redeems the snapshot:
        /// the run resumes at the snapshot plus everything earned since. Exactly one snapshot is
        /// ever outstanding, because ANY other combo break (a sealed line's misses, an abandoned
        /// word, a Premature/Lagging press, a rejected key, Fletcher's rush cap, or a wrong keypress
        /// on another cell) takes ownership of the streak and discards it: an intervening break is a
        /// run the player has already lost, and going back to fix the older cell cannot un-lose it.
        /// Repeated wrong/fix cycles on ONE cell therefore break and restore each time, each cycle
        /// snapshotting whatever the run had grown back to.</para>
        ///
        /// <para>Nothing else about a typo changes: the wrong keypress is still counted
        /// (<see cref="Mistyped"/>), still costs the accuracy denominator, and health is untouched
        /// in both directions.</para>
        /// </summary>
        public event Action<int>? ComboRestored;

        private readonly List<TypingLine> lines;
        private readonly bool[] lineSealed;
        private readonly Dictionary<JudgementType, int> counts = new Dictionary<JudgementType, int>();
        private readonly List<SyncSample> syncTimeline = new List<SyncSample>();

        /// <summary>Ring buffer of the ACTIVE-REAL-TIME stamps of the last correct keypresses (see <see cref="LiveRollingWpm"/>).</summary>
        private readonly double[] rollingSamples = new double[rolling_wpm_window];

        private readonly int totalTypeableCells;

        // --- Countable-character stream (Fletcher). The whole map read as one run of COUNTABLE
        // cells (typeable and not a space), which is the currency the rush cap measures in.
        // countableTargets: every countable cell's target time, sorted ascending, so the playhead's
        // position is a binary search. countableBase[k] / countablePrefix[k][i]: where line k, cell i
        // sits in that stream, so the caret's position is a lookup. All immutable after construction.
        private readonly double[] countableTargets;
        private readonly int[] countableBase;
        private readonly int[][] countablePrefix;

        private int nextSealIndex; // first line not yet sealed; lines seal strictly in order
        private int activeLineIndex = -1;
        private int caretIndex;
        private bool isFinished;

        private long score;
        private int combo;
        private int maxCombo;
        private int totalKeypresses;
        private int correctKeypresses;
        private int errorCount;
        private int consecutiveWrongKeys;

        /// <summary>
        /// The WPM clock: REAL (wall-clock) milliseconds spent with a line active, incomplete and the
        /// song actually singing it. Deliberately NOT beatmap milliseconds: <see cref="Update"/> is fed
        /// beatmap times, so each segment is divided by the clock rate that applied over it. Under Half
        /// Time (0.75x) 2000 ms of beatmap time is 2666.67 ms the player really had, and under Double
        /// Time (1.5x) it is 1333.33 ms. Nothing in judgement reads this; it only feeds
        /// <see cref="LiveWpm"/>, <see cref="LiveRollingWpm"/> and <see cref="ResultsSummary.Wpm"/>.
        /// </summary>
        private double activeRealTimeMs;

        private double? lastUpdateTime;

        private int rollingCount; // entries held in rollingSamples, capped at rolling_wpm_window
        private int rollingNext;  // next slot to write; also the oldest entry once the ring is full

        /// <summary>
        /// The one outstanding combo snapshot (see <see cref="ComboRestored"/>): the cell a wrong
        /// keypress spoiled and the streak that keypress broke, or null when there is nothing to go
        /// back for. Set by the wrong keypress, redeemed by the correction of that same cell, and
        /// discarded by any other combo break (<see cref="discardRestorableStreak"/>).
        /// </summary>
        private (int lineIndex, int cellIndex, int streak)? restorable;

        public TypingEngine(LyricBeatmap beatmap, bool literate = false)
        {
            Beatmap = beatmap ?? throw new ArgumentNullException(nameof(beatmap));

            Literate = literate;
            CaseSensitive = literate;

            lines = new List<TypingLine>(beatmap.Lines.Count);

            foreach (var line in beatmap.Lines)
                lines.Add(TypingLine.FromLyricLine(line, beatmap.Granularity, literate));

            lineSealed = new bool[lines.Count];

            foreach (var line in lines)
                totalTypeableCells += line.TypeableCount;

            foreach (JudgementType type in Enum.GetValues<JudgementType>())
                counts[type] = 0;

            countableBase = new int[lines.Count];
            countablePrefix = new int[lines.Count][];
            var targets = new List<double>();

            for (int k = 0; k < lines.Count; k++)
            {
                var cells = lines[k].Cells;
                countableBase[k] = targets.Count;
                var prefix = new int[cells.Count + 1];

                for (int i = 0; i < cells.Count; i++)
                {
                    prefix[i + 1] = prefix[i];

                    if (!cells[i].IsCountable)
                        continue;

                    prefix[i + 1]++;
                    targets.Add(cells[i].TargetTime);
                }

                countablePrefix[k] = prefix;
            }

            // Overlapping lines can interleave their targets across a boundary, so sort rather than
            // assume the per-line order carries: the playhead lookup only needs "how many countable
            // targets are at or before this time".
            targets.Sort();
            countableTargets = targets.ToArray();
        }

        /// <summary>
        /// How many COUNTABLE characters the song has reached by <paramref name="time"/>: the count
        /// of countable cells across the whole map whose target time is at or before it. The
        /// playhead's position in the countable stream, and the reference the Fletcher rush cap is
        /// measured against. Monotonic in time and a pure function of the beatmap, so a replay
        /// reproduces it exactly.
        /// </summary>
        public int PlayheadCountablePosition(double time)
        {
            int lo = 0;
            int hi = countableTargets.Length;

            while (lo < hi)
            {
                int mid = (lo + hi) / 2;

                if (countableTargets[mid] <= time)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        /// <summary>
        /// The player caret's position in the same countable stream: every countable cell in the
        /// lines before the active one, plus the countable cells behind the caret within it. 0 when
        /// no line is active.
        /// </summary>
        public int CaretCountablePosition
        {
            get
            {
                if (activeLineIndex == -1)
                    return 0;

                var prefix = countablePrefix[activeLineIndex];

                return countableBase[activeLineIndex] + prefix[Math.Clamp(caretIndex, 0, prefix.Length - 1)];
            }
        }

        /// <summary>
        /// Signed countable-character drift of the caret against the playhead at
        /// <paramref name="time"/>: positive = rushing ahead, negative = dragging behind. The
        /// quantity the Fletcher rush cap bounds (and the honest read-out of what the mod is about).
        /// </summary>
        public int CharsAheadOfPlayhead(double time) => CaretCountablePosition - PlayheadCountablePosition(time);

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
            // (1) Accrue active time while a line is active AND incomplete AND not finished
            //     (state as of the previous frame).
            if (lastUpdateTime is double last)
            {
                double dt = Math.Max(0, time - last);

                if (activeLineIndex != -1 && !IsLineComplete && !isFinished && wpmClockRuns(last))
                    activeRealTimeMs += dt / sanitisedRate(clockRate);
            }

            lastUpdateTime = time;

            // Fletcher: a drag cutoff inside the seal loop below moves the caret straight on to the
            // next line; the activation is announced once, after the loop, so a catch-up cascade
            // through several stale lines still relayouts the stage exactly once.
            bool pendingActivation = false;

            // (2) Seal, in order, every line whose deadline has passed. Normal lines seal AT
            //     EndTime; lines with a seal grace (vocals overrunning into the next line, or a
            //     boundary-pinned last target) stay typeable through the grace window and seal
            //     early the moment nothing is left to type, so the next line isn't held up.
            while (nextSealIndex < lines.Count && canSeal(lines[nextSealIndex], time) && sealPermitted(nextSealIndex, time))
            {
                int index = nextSealIndex;
                var line = lines[index];

                int missed = 0;

                foreach (var cell in line.Cells)
                {
                    // MISSED at seal: a typeable cell the line ran out of time on, and ONLY that.
                    // A cell left sitting WRONG is not one (backlog 124, reversing the predicate
                    // backlog 109 widened): the player finished that character, they just got it
                    // wrong, which is a mistype and not a miss. It keeps CellState.Wrong so the line
                    // still shows which character went wrong, it takes no engine miss, and it does
                    // not break the engine's combo here, because its break was already taken at the
                    // keypress. That is what puts the HUD combo back in agreement with the submitted
                    // max_combo (backlog 123); the cell's own osu result is decided on the drawable
                    // side by TypeBeatResultMapping.UnresolvedCellResult.
                    if (!cell.IsTypeable || cell.State != CellState.Untyped)
                        continue;

                    cell.State = CellState.Missed;

                    missed++;
                    counts[JudgementType.Miss]++;
                }

                bool broke = missed >= 1;

                if (broke)
                {
                    // AT MOST ONE combo break per sealed line, no matter how many cells were missed.
                    combo = 0;

                    // A real break, so it owns the streak. Distinct from the line-scoped drop
                    // below: under Fletcher the caret can already be on a LATER line, holding a
                    // snapshot this break has just cost it.
                    discardRestorableStreak();
                }

                // A sealed line's cells can never be typed again, so a snapshot left on this one is
                // unredeemable whether or not the seal broke anything. Dropping it here keeps the
                // state truthful rather than relying on the caret never going back.
                if (restorable?.lineIndex == index)
                    restorable = null;

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
                        pendingActivation = true;
                    }
                    else
                    {
                        activeLineIndex = -1;
                        caretIndex = 0;
                    }
                }

                if (broke)
                    ComboBroken?.Invoke();

                LineSealed?.Invoke(new LineSealResult(index, missed, broke));
            }

            // (3) Activate strictly by time: the first unsealed line, while it is judgeable
            //     (ActivationTime <= time < EndTime + grace). ActivationTime is the constant cue
            //     before the first word (CUE_LEAD_MS), not the boundary; crossing a boundary
            //     scrolls the stack (the seal above), but typing opens relative to the vocals.
            //     Typing never unlocks the next line.
            if (nextSealIndex >= lines.Count)
            {
                if (!isFinished)
                {
                    isFinished = true;
                    activeLineIndex = -1;
                    caretIndex = 0;
                    Finished?.Invoke();
                }
            }
            else if (activeLineIndex == -1)
            {
                var candidate = lines[nextSealIndex];

                if (time >= candidate.ActivationTime && time < candidate.EndTime + candidate.SealGraceMs)
                {
                    activeLineIndex = nextSealIndex;
                    caretIndex = 0;
                    autoSkipForward();
                    LineActivated?.Invoke(activeLineIndex);
                }
            }
            else if (pendingActivation)
            {
                // Fletcher drag cutoff (see the seal loop): the caret already moved, announce it once.
                autoSkipForward();
                LineActivated?.Invoke(activeLineIndex);
            }
        }

        /// <summary>
        /// Whether the WPM/sync active-time clock runs for the frame ending at <paramref name="previousTime"/>.
        /// Always, by default. Under <see cref="FletcherEnabled"/> the caret can be parked at the head
        /// of a line the song has not reached yet (rush freedom rolls it forward the instant a line is
        /// finished), and a clock that ran through a 20-second instrumental would read the wait as
        /// typing time; so the clock runs only from the point the playhead reaches that line's
        /// ActivationTime, which is exactly when the line would have gone active without the mod.
        /// </summary>
        private bool wpmClockRuns(double previousTime)
            => !FletcherEnabled || activeLineIndex == -1 || previousTime >= lines[activeLineIndex].ActivationTime;

        /// <summary>
        /// The usable magnitude of a clock rate for the WPM divisor. A rewinding clock reports a
        /// NEGATIVE rate, but rewind is a direction and not a speed, and dt is already clamped >= 0, so
        /// the sign is dropped rather than allowed to run active time backwards. A stopped (0) or
        /// non-finite rate carries no speed information at all, so it falls back to 1x instead of
        /// poisoning the accumulator with an infinity or a NaN that every later readout would inherit.
        /// </summary>
        private static double sanitisedRate(double rate)
        {
            double magnitude = Math.Abs(rate);

            return double.IsFinite(magnitude) && magnitude > 0 ? magnitude : 1;
        }

        /// <summary>
        /// Fletcher DRAG FREEDOM: a line the player is still typing must not be force-sealed out from
        /// under them at its normal deadline. The seal is deferred while the caret is on the line, up
        /// to <see cref="FLETCHER_DRAG_GRACE_MS"/> past its hard deadline; past that the line seals as
        /// usual (untyped cells become misses, one combo break) and the caret is moved on. Always true
        /// without the mod, and true under it for any line the player is not currently on, so a
        /// finished-early line still seals exactly on its own deadline.
        /// </summary>
        private bool sealPermitted(int index, double time)
        {
            if (!FletcherEnabled || activeLineIndex != index)
                return true;

            var line = lines[index];

            // Nothing left untyped means there is no drag to protect: the line seals on its normal
            // deadline, exactly as it would without the mod. (This is also what lets the FINAL line,
            // which has no next line to roll on to, finish the run on time once it is fully typed.)
            if (!hasUntypedTypeable(line))
                return true;

            return time >= line.EndTime + line.SealGraceMs + FLETCHER_DRAG_GRACE_MS;
        }

        /// <summary>
        /// A line may seal once its EndTime has passed AND either its grace window has elapsed
        /// or nothing typeable is left untyped (early seal so the next line isn't delayed).
        /// </summary>
        private static bool canSeal(TypingLine line, double time)
        {
            if (time < line.EndTime)
                return false;

            if (time >= line.EndTime + line.SealGraceMs)
                return true;

            return !hasUntypedTypeable(line);
        }

        /// <summary>Whether the line still holds a typeable cell nobody has put anything into.</summary>
        private static bool hasUntypedTypeable(TypingLine line)
        {
            foreach (var cell in line.Cells)
            {
                if (cell.IsTypeable && cell.State == CellState.Untyped)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Process a lowercased char from KeyCharMap at gameplay time <paramref name="time"/>.
        /// Returns false when inert (no active line / line complete / finished).
        /// A space pressed inside a word abandons it under <see cref="SpaceSkipsWord"/> (off by default).
        /// A wrong char is TYPED THROUGH by default (<see cref="AllowWrongInput"/>), or REJECTED
        /// under Gatekeeper. Either way it breaks combo, counts as a mistype, stays in the accuracy
        /// denominator forever, and resolves NO cell against the score processor; only the rejection
        /// path grows <see cref="ConsecutiveWrongKeys"/>.
        /// </summary>
        public bool ProcessKey(char c, double time)
        {
            if (isFinished || activeLineIndex == -1)
                return false;

            var line = lines[activeLineIndex];

            // Hop auto-skip cells before matching (normally already done on advance/activation).
            autoSkipForward();

            if (caretIndex >= line.Cells.Count)
                return false; // line complete, wait for the song.

            var cell = line.Cells[caretIndex];

            // Mashing mod: any key is the right key; judge it as the caret cell's expected char.
            // A FREESTYLE cell is exempt: it already accepts any key, and rewriting c here would
            // stamp the authoring marker over the char the player actually pressed (the one thing
            // a freestyle cell must remember). No double effect, mashing simply has nothing to add.
            // Space is the single exception to that exemption: a freestyle cell REJECTS space (see
            // the match below), so mashing's "any key is the right key" promise needs a substitute
            // to hand it, and the char an automated player presses into a freestyle slot is the
            // canonical one. Nothing else about the exemption changes, the pressed char still
            // survives on every other key.
            if (MashingEnabled)
            {
                if (!cell.IsFreestyle)
                    c = cell.Expected;
                else if (c == ' ')
                    c = Typeability.FREESTYLE_AUTO_CHAR;
            }

            // SPACE-SKIP (see SpaceSkipsWord), evaluated BEFORE the match: a space pressed while the
            // caret sits on a lyric character abandons the rest of that word. The caret cell is
            // typeable here (autoSkipForward ran above), so "Expected is not a space" is exactly "the
            // caret is inside a word"; a space pressed ON the word gap keeps its ordinary meaning and
            // never reaches this branch. Placed after the Mashing rewrite on purpose: mashing has
            // already turned the press into the expected char, so this is unreachable under it.
            if (SpaceSkipsWord && c == ' ' && cell.Expected != ' ')
            {
                skipCurrentWord(time);

                if (caretIndex >= line.Cells.Count)
                {
                    // The abandoned word ran to the end of the line, so there is no word gap for the
                    // space to land on. The line is complete, exactly as it would be had the player
                    // typed that last word out, and the same end-of-line handling applies.
                    rollForwardIfFinishedEarly();
                    return true;
                }

                // The caret now sits on the word gap, and the press is judged there: a space typed
                // from further back in the word is still a typed space, so it takes the ordinary
                // path below (same windows, points, combo, accuracy) and leaves the cell exactly as
                // a normally typed space would.
                cell = line.Cells[caretIndex];
            }

            // The press's lead/lag in milliseconds, which is what the judgement EVENT carries and
            // what the sync timeline records, and its offset in the measure the play is JUDGED in,
            // which is what the windows classify. Identical under SyncMeasure.Milliseconds.
            double delta = time - cell.TargetTime;
            double offset = judgementOffset(line, caretIndex, time);
            // FREESTYLE cell: every char EXCEPT SPACE matches, in any case, under every mod (so the
            // Literate mod's exact-case rule and the allow-wrong-input path are both bypassed for
            // it). The press is then judged exactly like a correct char: same windows, points,
            // combo, accuracy and completion, with the pressed char kept in TypedChar.
            // SPACE is carved out (backlog 50): it is the word-advance key, not a glyph a player
            // means to leave sitting in a lyric, so it falls through to the ordinary non-match path
            // below and is judged exactly as a wrong key on any other cell would be. The strict
            // rejection is the only outcome available to it, because the allow-wrong-input path
            // already refuses to type a space through (c != ' '). Unless SpaceSkipsWord is on, in
            // which case the space never reaches here at all: it was consumed by the word skip above,
            // freestyle slot included (a freestyle cell is a lyric character like any other, so a
            // space pressed on one is the player abandoning the word it sits in).
            // Literate mod folds nothing: the typed char must match the target's exact case.
            // Default gameplay is case-insensitive (both sides lower-cased through Fold).
            bool matched = (cell.IsFreestyle && c != ' ')
                           || (CaseSensitive ? c == cell.Expected : Typeability.Fold(c) == Typeability.Fold(cell.Expected));

            if (!matched)
            {
                // DEFAULT: a wrong LETTER is typed through, marked red, backspaceable, instead of
                // rejected. The space key stays strict (no wrong space, and no wrong char consuming
                // a word boundary), and this path never feeds the mash-fail streak
                // (consecutiveWrongKeys is left at 0, which is why that guard is Gatekeeper-only).
                if (AllowWrongInput && c != ' ' && cell.Expected != ' ')
                {
                    totalKeypresses++;
                    errorCount++;

                    // The streak this keypress is about to break, snapshotted against the cell it
                    // spoils: correcting that cell resumes it (backlog 140, see ComboRestored).
                    // Written unconditionally, so a wrong key on a SECOND cell discards the first
                    // cell's claim exactly as any other intervening break would.
                    int brokenStreak = combo;

                    combo = 0;
                    counts[JudgementType.WrongChar]++;

                    cell.State = CellState.Wrong;
                    cell.TypedChar = c;

                    int wrongCellIndex = caretIndex;

                    restorable = TypeBeatResultMapping.FixRestoresTheComboBreak(ComboRestore)
                        ? (activeLineIndex, wrongCellIndex, brokenStreak)
                        : null;

                    caretIndex++;
                    autoSkipForward();

                    // The keypress was wrong, so it is a mistype exactly as it would be in strict
                    // mode, and since backlog 109 it ACCOUNTS exactly as strict mode does too: the
                    // mistype carries the combo break by hand (TypeBeatPlayfield.onMistyped) and the
                    // cell hands the score processor nothing at all.
                    Mistyped?.Invoke();
                    ComboBroken?.Invoke();
                    // The CELL's judgement still travels here, for the stage's red/shake feedback,
                    // but DrawableTypeBeatHitObject.ApplyCharJudgement deliberately applies no osu
                    // result for a WrongChar: the cell's result is DEFERRED. Backspace and retype it
                    // correctly and it earns its real Perfect/Great/Ok/Meh, plus the streak this
                    // press just broke (backlog 140, see ComboRestored); leave it and the seal
                    // resolves it as an unfixed typo, which is a hit and not a miss (backlog 124).
                    CharJudged?.Invoke(new CharJudgement(activeLineIndex, wrongCellIndex, JudgementType.WrongChar, delta, 0, combo));
                    rollForwardIfFinishedEarly();
                    return true;
                }

                // Gatekeeper (strict): wrong key REJECTED, no cell mutation, no caret advance, no
                // CharJudged. It still costs the accuracy denominator, an error, a combo break, and
                // the consecutive-wrong-key streak (the game fails the play when it hits 13), and
                // it is counted as a MISTYPE, which is the only route by which a rejected key
                // reaches the score processor and the persisted statistics (see Mistyped).
                totalKeypresses++;
                errorCount++;
                consecutiveWrongKeys++;
                combo = 0;

                // Nothing was written into a cell, so there is nothing to go back and correct: this
                // break is final, and it ends any older cell's claim on the streak.
                discardRestorableStreak();
                counts[JudgementType.WrongChar]++;
                Mistyped?.Invoke();
                ComboBroken?.Invoke();
                WrongKeyRejected?.Invoke(c);
                return true;
            }

            consecutiveWrongKeys = 0;

            // COMBO RESTORE (backlog 140), before anything about this press is judged: if this is
            // the correction of the cell a wrong keypress spoiled, the run resumes at the streak
            // that keypress broke plus everything earned since. Placed here so the press below is
            // scored, and announced, at the RESUMED streak. Not a scoring-inert operation even for
            // an inert retype: the streak belongs to the fix, not to the cell's judgement.
            resumeStreakIfThisFixesTheTypo(caretIndex);

            // Correctly re-typing a cell that was EVER judged correct (reached again via backspace,
            // which resets State but not FirstCorrectDelta) is scoring-inert: no counters, no
            // points/combo, no timeline sample, and the first judgement stands; otherwise
            // backspace-retype farms score, combo and accuracy without bound.
            bool inertRetype = cell.FirstCorrectDelta is not null;

            JudgementType type;
            int points = 0;

            if (inertRetype)
            {
                delta = cell.FirstCorrectDelta!.Value;
                offset = cell.FirstCorrectOffset!.Value;
                type = windowsFor(cell).Classify(offset);

                cell.State = CellState.Correct;
                cell.TypedChar = c;
                cell.JudgedDelta = delta;
                cell.JudgedOffset = offset;
                cell.JudgedSyncQuality = windowsFor(cell).SyncQuality(offset);
            }
            else
            {
                // ALL scoring keypresses (correct + wrong) count in the accuracy denominator, forever.
                totalKeypresses++;

                // Correct char: always accepted; the clock decides the judgement.
                // Premature/Lagging still count as CORRECT keypresses (right char, wrong time).
                correctKeypresses++;

                type = windowsFor(cell).Classify(offset);
                int basePoints = SyncWindows.BasePoints(type);

                // Fletcher RUSH CAP, evaluated before the caret moves: does this press put the caret
                // more than FLETCHER_MAX_CHARS_AHEAD countable chars past the playhead?
                bool rushedPastCap = FletcherEnabled && rushesPastCap(cell, time);

                if (basePoints > 0)
                {
                    // Multiplier reads combo BEFORE the increment; capped at combo_cap => up to 2.0x.
                    points = (int)Math.Round(basePoints * (1 + Math.Min(combo, combo_cap) / (double)combo_cap));
                    score += points;

                    if (rushedPastCap)
                    {
                        // A combo penalty, not a block: the char lands and scores exactly as it would
                        // without the mod, but no combo may accumulate while the caret is out past the
                        // cap. ComboBroken therefore fires once, on the press that crosses the line,
                        // and re-arms the moment a press lands back inside it (combo starts building
                        // again, so the next excursion breaks it again).
                        bool hadCombo = combo > 0;
                        combo = 0;

                        if (hadCombo)
                        {
                            discardRestorableStreak();
                            ComboBroken?.Invoke();
                        }
                    }
                    else
                    {
                        combo++;
                        maxCombo = Math.Max(maxCombo, combo);
                    }
                }
                else
                {
                    // Premature / Lagging: 0 points, combo break, char still accepted.
                    combo = 0;
                    discardRestorableStreak();
                    ComboBroken?.Invoke();
                }

                cell.State = CellState.Correct;
                cell.TypedChar = c;
                cell.JudgedDelta = delta;
                cell.JudgedOffset = offset;
                cell.JudgedSyncQuality = windowsFor(cell).SyncQuality(offset);
                cell.FirstCorrectDelta = delta;   // the one awarded judgement; retypes are inert.
                cell.FirstCorrectOffset = offset;

                // SyncTimeline records every AWARDED correct-char judgement, incl. Premature/Lagging.
                syncTimeline.Add(new SyncSample(time, delta));

                counts[type]++;
            }

            // Log the press for the HUD's rolling WPM. Both branches above land the cell Correct, so a
            // scoring-inert retype still counts here: this is a record of keystrokes, not of cell states.
            pushRollingSample();

            int judgedCellIndex = caretIndex;
            caretIndex++;
            autoSkipForward();

            CharJudged?.Invoke(new CharJudgement(activeLineIndex, judgedCellIndex, type, delta, points, combo));
            rollForwardIfFinishedEarly();
            return true;
        }

        /// <summary>
        /// "Space to skip current word" (see <see cref="SpaceSkipsWord"/>): abandon the word the caret
        /// is inside and leave the caret on the word gap that follows it (or at the end of the line,
        /// for a word with no gap after it). Every typeable cell of that word nobody has typed
        /// ANYTHING into takes a Miss, the way the seal loop misses an untyped cell, and the whole
        /// abandonment costs AT MOST ONE combo break no matter how many characters were given up,
        /// which is the same rule a sealed line's misses follow. There is always at least one such
        /// cell, the one the caret is sitting on, so the break always has a miss behind it.
        ///
        /// <para>Non-typeable cells inside the run are marked <see cref="CellState.AutoSkipped"/>,
        /// which is exactly what <see cref="autoSkipForward"/> would have done to them had the caret
        /// walked over them one press at a time; they are not typed, so they cannot be missed.</para>
        /// </summary>
        private void skipCurrentWord(double time)
        {
            var cells = lines[activeLineIndex].Cells;

            // The WHOLE word the caret is inside: the run of cells between the word gaps either side
            // of it (a word gap being a typeable SPACE cell), or the ends of the line. Deliberately
            // the whole word rather than just the tail from the caret onwards, even though the two
            // give up exactly the same cells: every typeable cell BEHIND the caret is already Correct
            // or Wrong, because resolving it is the only way the caret got past it and backspace
            // takes the caret back with it. Scanning the word is what the feature promises, and it
            // puts the weight on the "already resolved" test below instead of on an off-screen
            // argument about where the caret can be.
            int start = caretIndex;
            int end = caretIndex;

            while (start > 0 && !(cells[start - 1].IsTypeable && cells[start - 1].Expected == ' '))
                start--;

            while (end < cells.Count && !(cells[end].IsTypeable && cells[end].Expected == ' '))
                end++;

            var abandoned = new List<int>();

            for (int i = start; i < end; i++)
            {
                var cell = cells[i];

                if (!cell.IsTypeable)
                {
                    cell.State = CellState.AutoSkipped;
                    continue;
                }

                // Only a cell nobody has put anything into is given up. A CORRECT one has already
                // handed its drawable the one osu result it will ever have and a Great cannot be
                // revoked (ApplyEngineResult drops every later result on an already-judged cell, and
                // there is no un-apply). A WRONG one is not given up either, and since backlog 124
                // that is for its own reason rather than that one: a typed-through wrong character
                // is a cell the player FINISHED, so abandoning the word cannot turn it into a miss.
                // Its deferred result is decided at the seal like every other unfixed typo, which
                // also leaves the promise intact that backspacing back into the word can still fix
                // it. Backlog 109 had it given up here, because at the time the only fate available
                // to an unfixed typo was a Miss.
                if (cell.State != CellState.Untyped)
                    continue;

                cell.State = CellState.Missed;

                counts[JudgementType.Miss]++;
                abandoned.Add(i);
            }

            caretIndex = end;

            if (abandoned.Count == 0)
                return;

            combo = 0;
            discardRestorableStreak();
            ComboBroken?.Invoke();

            // Announce the misses AFTER the break so every judgement carries the post-break combo,
            // and one per cell so the stage repaints it and its scoring drawable takes its Miss now
            // rather than at seal time (which would leave osu's combo counting on past a break the
            // engine has already taken).
            foreach (int i in abandoned)
                CharJudged?.Invoke(new CharJudgement(activeLineIndex, i, JudgementType.Miss, time - cells[i].TargetTime, 0, combo));
        }

        /// <summary>
        /// A combo break that is nobody's fixable typo happened, so the outstanding snapshot (if
        /// any) is discarded: the streak it was holding has been lost to THIS break, and correcting
        /// the older cell later cannot bring back a run that ended after it. Called at every
        /// <see cref="ComboBroken"/> seam except the wrong keypress's own, which takes the snapshot
        /// instead.
        /// </summary>
        private void discardRestorableStreak() => restorable = null;

        /// <summary>
        /// Redeem the outstanding snapshot if the cell about to be typed correctly is the cell it
        /// was taken against: the run resumes at that streak plus everything earned since, which is
        /// exactly <c>combo + streak</c> because no break has landed in between (any that had would
        /// have discarded the snapshot). The claim is spent either way, so a second correct retype
        /// of the same cell restores nothing.
        /// </summary>
        private void resumeStreakIfThisFixesTheTypo(int cellIndex)
        {
            if (restorable is not (int lineIndex, int typoCellIndex, int streak))
                return;

            if (lineIndex != activeLineIndex || typoCellIndex != cellIndex)
                return;

            restorable = null;

            // A break that cost nothing restores nothing, and announcing it would have every
            // consumer write back a combo it already holds.
            if (streak <= 0)
                return;

            combo += streak;
            maxCombo = Math.Max(maxCombo, combo);

            ComboRestored?.Invoke(streak);
        }

        /// <summary>
        /// Fletcher rush cap: would accepting <paramref name="cell"/> at <paramref name="time"/> leave
        /// the caret more than <see cref="FLETCHER_MAX_CHARS_AHEAD"/> countable chars past the
        /// playhead? Measured on the caret position AFTER the press, so with a cap of 5 the fifth
        /// char ahead is still fine and the sixth is not. A non-countable cell (a space) spends no
        /// budget, so pressing it can never push the caret over the line by itself.
        /// </summary>
        private bool rushesPastCap(TypingCell cell, double time)
        {
            int after = CaretCountablePosition + (cell.IsCountable ? 1 : 0);

            return after - PlayheadCountablePosition(time) > FLETCHER_MAX_CHARS_AHEAD;
        }

        /// <summary>
        /// Fletcher RUSH FREEDOM: the moment a press finishes a line, the caret moves straight on to
        /// the next one instead of waiting for its activation cue. The finished line is left UNSEALED
        /// and seals on its own normal deadline (with nothing missed, since it is fully typed), so
        /// nothing about the song's timeline moves; only the player's position does. No-op on the last
        /// line, which keeps the default "line complete, wait for the song" behaviour that lets the
        /// key handler pass Space through to the skip overlay.
        /// </summary>
        private void rollForwardIfFinishedEarly()
        {
            if (!FletcherEnabled || isFinished || activeLineIndex == -1)
                return;

            if (caretIndex < lines[activeLineIndex].Cells.Count)
                return;

            if (activeLineIndex + 1 >= lines.Count)
                return;

            // Lines seal in order and the player never leaves a line except by finishing it or by a
            // drag cutoff (which advances nextSealIndex with them), so the next line is always unsealed.
            activeLineIndex++;
            caretIndex = 0;
            autoSkipForward();
            LineActivated?.Invoke(activeLineIndex);
        }

        /// <summary>Windows for a cell's judgement tier (Line for estimated/low-confidence timing).</summary>
        private SyncWindows windowsFor(TypingCell cell) => SyncWindows.For(cell.JudgeGranularity, Measure);

        /// <summary>
        /// The offset a keypress at <paramref name="time"/> on cell <paramref name="cellIndex"/> of
        /// <paramref name="line"/> is JUDGED by, in the current <see cref="Measure"/>: how many
        /// characters it is from the character the playhead is on (negative = ahead of it), or the
        /// plain millisecond delta under <see cref="SyncMeasure.Milliseconds"/>.
        /// </summary>
        private double judgementOffset(TypingLine line, int cellIndex, double time)
            => Measure == SyncMeasure.Milliseconds
                ? time - line.Cells[cellIndex].TargetTime
                : line.CharacterDistanceAt(time, cellIndex);

        /// <summary>
        /// Erase the most recent typed cell within the active line, stepping back transparently
        /// over AutoSkipped punctuation (which is un-skipped so retyping re-marks it).
        /// Returns false if nothing to erase. The erased keypress stays in the accuracy counts.
        /// </summary>
        public bool ProcessBackspace()
        {
            if (isFinished || activeLineIndex == -1)
                return false;

            var cells = lines[activeLineIndex].Cells;

            // Find the nearest non-auto-skipped cell behind the caret (scan first, mutate after).
            int target = caretIndex - 1;

            while (target >= 0 && cells[target].State == CellState.AutoSkipped)
                target--;

            if (target < 0)
                return false;

            // Un-skip the punctuation cells we stepped back over.
            for (int i = target + 1; i < caretIndex; i++)
            {
                if (cells[i].State == CellState.AutoSkipped)
                    cells[i].State = CellState.Untyped;
            }

            var cell = cells[target];
            cell.State = CellState.Untyped;
            cell.TypedChar = null;
            cell.JudgedDelta = null;
            cell.JudgedOffset = null;
            cell.JudgedSyncQuality = null;

            caretIndex = target;
            return true;
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

        public ResultsSummary BuildResults()
        {
            // SyncPercent over ALL typeable cells: finally-Correct cells contribute the sync
            // quality banked at their judgement; everything else (Missed / Wrong / unresolved) is 0.
            double qualitySum = 0;

            foreach (var line in lines)
            {
                foreach (var cell in line.Cells)
                {
                    if (cell.IsTypeable && cell.State == CellState.Correct && cell.JudgedSyncQuality is double q)
                        qualitySum += q;
                }
            }

            double syncPercent = totalTypeableCells == 0 ? 100 : 100 * qualitySum / totalTypeableCells;

            double wpm = activeRealTimeMs <= 0 ? 0 : (countCorrectCells() / 5.0) / (activeRealTimeMs / 60000.0);

            return new ResultsSummary
            {
                Score = score,
                Accuracy = LiveAccuracy,
                Wpm = wpm,
                SyncPercent = syncPercent,
                MaxCombo = maxCombo,
                Counts = new Dictionary<JudgementType, int>(counts),
                SyncTimeline = syncTimeline.ToArray(),
                Artist = Beatmap.Metadata.Artist,
                Title = Beatmap.Metadata.Title,
            };
        }

        /// <summary>Correct cells (including spaces) across all lines: the WPM numerator source.</summary>
        private int countCorrectCells()
        {
            int count = 0;

            foreach (var line in lines)
            {
                foreach (var cell in line.Cells)
                {
                    if (cell.State == CellState.Correct)
                        count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Record one correct keypress at the current active REAL time for <see cref="LiveRollingWpm"/>.
        /// Nothing reads this back into judgement, so it can never move a score. <see cref="ProcessBackspace"/>
        /// deliberately does NOT pop: the buffer is what the player typed, not what the cells currently
        /// hold, so backspacing and retyping simply logs another (later) press.
        /// </summary>
        private void pushRollingSample()
        {
            rollingSamples[rollingNext] = activeRealTimeMs;
            rollingNext = (rollingNext + 1) % rolling_wpm_window;

            if (rollingCount < rolling_wpm_window)
                rollingCount++;
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
