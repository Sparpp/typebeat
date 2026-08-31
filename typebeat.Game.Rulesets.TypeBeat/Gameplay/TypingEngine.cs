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
        ///
        /// <para>Since backlog 218 it bounds BOTH directions, and the name is kept for the replays
        /// and the mirrors that already use it. Drag holds a line open this long past its natural
        /// END (<see cref="sealPermitted"/>); rush enters a line this long before its natural START
        /// (<see cref="entryPermitted"/>, gated on <see cref="BoundedRush"/>). One constant, so the
        /// two freedoms cannot drift apart: a player may run ahead of the song by exactly the margin
        /// they may fall behind it.</para>
        /// </summary>
        public const double FLETCHER_DRAG_GRACE_MS = 1500;

        private const int combo_cap = 50;

        /// <summary>How many of the most recent correct keypresses <see cref="LiveRollingWpm"/> averages over.</summary>
        private const int rolling_wpm_window = 30;

        public LyricBeatmap Beatmap { get; }

        public IReadOnlyList<TypingLine> Lines => lines;

        /// <summary>
        /// Whether correcting a wrong cell resumes the streak its keypress broke (see
        /// <see cref="ComboRestored"/>). <see cref="ComboRestoreRule.OnFix"/> is the live rule
        /// (backlog 140) and the default; only <see cref="Scoring.TypeBeatReplayScorer"/> ever sets
        /// the other one, to re-derive a score from before the restore existed. It must be set
        /// BEFORE the first keypress and left alone afterwards: combo already awarded is never
        /// revisited.
        /// </summary>
        public ComboRestoreRule ComboRestore { get; set; } = ComboRestoreRule.OnFix;

        /// <summary>
        /// Which break owns the streak when a redeemable one lands on top of an outstanding claim
        /// (see <see cref="ComboRestored"/>). <see cref="ComboClaimRule.StreakedBreakWins"/> is the
        /// live rule (backlog 176) and the default: a break takes ownership only if it HAS a streak
        /// to own. Only <see cref="Scoring.TypeBeatReplayScorer"/> ever sets the other one, to
        /// re-derive a score from before that was true. Set BEFORE the first keypress and left alone
        /// afterwards, exactly like <see cref="ComboRestore"/>, whose companion it is: it decides
        /// nothing at all under <see cref="ComboRestoreRule.Never"/>, where no snapshot is ever
        /// taken.
        /// </summary>
        public ComboClaimRule ComboClaim { get; set; } = ComboClaimRule.StreakedBreakWins;

        /// <summary>
        /// Whether combo credited by the press that TOOK the outstanding claim counts as a streak the
        /// next break may take that claim with (see <see cref="ComboRestored"/>).
        /// <see cref="SkipSpaceCreditRule.NotAStreakOfItsOwn"/> is the live rule (backlog 243) and the
        /// default: a skipping space rebuilds the run to 1 on the gap it lands on, and that 1 is the
        /// break's own press rather than anything the player typed after it. Only
        /// <see cref="Scoring.TypeBeatReplayScorer"/> ever sets the other one, to re-derive a score
        /// from before that was true. Set BEFORE the first keypress and left alone afterwards, exactly
        /// like <see cref="ComboRestore"/> and <see cref="ComboClaim"/>, whose refinement it is: it
        /// decides nothing under <see cref="ComboClaimRule.LatestBreakWins"/>, where no break is
        /// passive, nor under <see cref="ComboRestoreRule.Never"/>, where no snapshot exists.
        /// </summary>
        public SkipSpaceCreditRule SkipSpaceCredit { get; set; } = SkipSpaceCreditRule.NotAStreakOfItsOwn;

        /// <summary>
        /// Whether a word abandoned by <see cref="SpaceSkipsWord"/> stays re-typeable (see
        /// <see cref="WordAbandoned"/>). <see cref="WordSkipRule.Reclaimable"/> is the live rule
        /// (backlog 167) and the default; only <see cref="Scoring.TypeBeatReplayScorer"/> ever sets
        /// the other one, to re-derive a score from before the reclaim existed. Set BEFORE the first
        /// keypress and left alone afterwards, exactly like <see cref="ComboRestore"/>: cells already
        /// given up are never revisited.
        /// </summary>
        public WordSkipRule WordSkip { get; set; } = WordSkipRule.Reclaimable;

        /// <summary>
        /// What an OFF-TIME press costs: the right character struck outside the outermost Meh
        /// window, judged <see cref="JudgementType.Premature"/> or <see cref="JudgementType.Lagging"/>.
        /// <see cref="OffTimeRule.MehHit"/> is the live rule (backlog 199) and the default: the press
        /// earns no points but EXTENDS the run, and only accuracy pays. Only
        /// <see cref="Scoring.TypeBeatReplayScorer"/> ever sets the other one, to re-derive a score
        /// from when such a press broke the combo. Set BEFORE the first keypress and left alone
        /// afterwards, exactly like <see cref="ComboRestore"/>: combo already awarded is never
        /// revisited.
        /// </summary>
        public OffTimeRule OffTime { get; set; } = OffTimeRule.MehHit;

        /// <summary>
        /// What a CORRECTED typo's cell is worth: the judgement a cell earns from a correct retype,
        /// having held a wrong character before it was ever judged.
        /// <see cref="CorrectionCreditRule.Capped"/> is the live rule (backlog 210) and the default:
        /// such a cell resolves at min(the retype's own tier, <see cref="JudgementType.Ok"/>), so a
        /// fix always costs some accuracy and perfect play strictly beats corrected play per cell.
        /// Only <see cref="Scoring.TypeBeatReplayScorer"/> ever sets the other one, to re-derive a
        /// score from when a fast fix was free. Set BEFORE the first keypress and left alone
        /// afterwards, exactly like <see cref="ComboRestore"/>: judgements already awarded are never
        /// revisited.
        /// </summary>
        public CorrectionCreditRule CorrectionCredit { get; set; } = CorrectionCreditRule.Capped;

        /// <summary>
        /// THE live judgement rule (backlog 174, graduated by backlog 179, narrowed by backlog 180
        /// to every mod stack except Hard Rock): judge each keypress
        /// against its cell's SYLLABLE time span instead of the cell's point target. Characters
        /// belong to a syllable
        /// (<see cref="TypingLine.Syllables"/>), and any character of a syllable is perfectly timed
        /// while that syllable is being sung: the judged delta is 0 anywhere inside
        /// [<see cref="SyllableGroup.StartTime"/>, <see cref="SyllableGroup.EndTime"/>]
        /// (edge-inclusive) and the signed distance to the nearer edge outside it, fed through the
        /// same <see cref="SyncWindows.Classify"/> ladder and stored in
        /// <see cref="TypingCell.JudgedDelta"/> like any point delta, so points, combo, the sync
        /// readouts and the results screen all work unmodified. A cell in no group keeps the
        /// classic point delta: space cells, any line with no groups, and since backlog 178 every
        /// cell of a token that is not a syllabifiable English word ("wooooooords", "ohhh"), which
        /// is how a stylised spelling keeps the per-character rule it is actually timed on.
        ///
        /// <para>FALSE by default, and era-styled like <see cref="SpaceTiming"/>: set before the
        /// first keypress and left alone afterwards, judgements already made are never revisited.
        /// Live play turns it on for every mod stack but Hard Rock
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), which reverts to the classic rule because
        /// a span that grants delta 0 over hundreds of milliseconds undercuts HR's halved windows
        /// (see <see cref="Mods.TypeBeatModHardRock"/>). The default is the CLASSIC era, which is
        /// what a bare engine, every replay recorded before backlog 179, and every HR replay must
        /// judge under. Which one a re-derivation gets is decided by the replay's
        /// own CONFIG frame (<see cref="Replays.TypeBeatReplayFrame.SyllableTiming"/>, flags bit 2)
        /// and applied in <see cref="Replays.ReplayEngineFeed.Apply"/>, so a stored score always
        /// reproduces the rule its fingers were graded on.</para>
        /// </summary>
        public bool SyllableTiming { get; set; }

        /// <summary>
        /// The narrowing backlog 209 puts on <see cref="SyllableTiming"/>: a STRETCH cell
        /// (<see cref="TypingLine.IsCharTimedStretch"/>, a freestyle slot or a cell of a run of
        /// three or more identical characters inside one syllable) reverts to its own point target
        /// even while the rest of the line is judged on syllable spans.
        ///
        /// <para>The span rule prices a press on WHICH CHARACTER of the syllable is being sung, and
        /// those two shapes carry no such information: every key satisfies a freestyle slot, and the
        /// characters of "&amp;&amp;&amp;&amp;&amp;&amp;" or the "000" of "1000" are interchangeable
        /// to the matcher. So a player could mash the whole run the instant its syllable opened,
        /// seconds ahead of the vocal, and be graded a delta of ZERO on every press: a field report
        /// had accuracy going UP for spamming a freestyle section. Reverting exactly those cells to
        /// the character targets they are actually timed on (the equal division of the syllable's
        /// time range that <c>TypingLine.syllableCharTarget</c> already assigns) puts the mash back
        /// on the clock and leaves every ordinary character on the span rule it was given.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like the flags above: set before the first
        /// keypress and left alone. Live play sets it for EVERY mod stack
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), Hard Rock included, where it is inert
        /// because HR turns <see cref="SyllableTiming"/> off and is therefore already
        /// point-timed; recording it unconditionally is what makes re-derivation uniform. It
        /// travels per replay on the CONFIG frame's flags bit 6
        /// (<see cref="Replays.TypeBeatReplayFrame.CharTimedStretch"/>) and is applied in
        /// <see cref="Replays.ReplayEngineFeed.Apply"/>, so every replay recorded before it existed
        /// carries the bit clear and re-derives on the pure span rule its fingers were graded on,
        /// exploit and all.</para>
        /// </summary>
        public bool CharTimedStretch { get; set; }

        /// <summary>
        /// The second narrowing on <see cref="SyllableTiming"/> (backlog 247): the FIRST cell of a
        /// syllable group is judged on its distance from the group's <see cref="SyllableGroup.StartTime"/>
        /// rather than paid 0 anywhere inside the span, so pacing a syllable out beats bursting its
        /// characters just before the window closes. Every other cell of the group keeps the span
        /// rule, which is what makes this a hybrid: the syllable's opening is back on the clock and
        /// the rest of the syllable stays as forgiving as backlog 179 made it.
        ///
        /// <para>The anchor is the SPAN START, deliberately not the cell's own
        /// <see cref="TypingCell.TargetTime"/>: the playhead reaches a syllable's first character
        /// when the syllable starts being sung, and under mapper subtimings the flat-ramp target
        /// routinely sits OUTSIDE its own group's span (see
        /// <see cref="Replays.TypeBeatAutoGenerator"/>, whose 99.23% autoplay incident came from
        /// exactly that gap). The EARLY side is unchanged either way, since a press before
        /// <see cref="SyllableGroup.StartTime"/> already judges on that same distance; only the late
        /// side tightens. A stretch cell (<see cref="CharTimedStretch"/>) that opens a group is NOT
        /// re-anchored: it is already point-judged, and stricter, so the stretch narrowing keeps
        /// precedence.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like the flags above: set before the first
        /// keypress and left alone. Live play sets it for EVERY mod stack
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), Hard Rock included, where it is inert
        /// because HR turns <see cref="SyllableTiming"/> off; recording it unconditionally is what
        /// keeps re-derivation uniform. It travels per replay on the CONFIG frame's flags bit 8
        /// (<see cref="Replays.TypeBeatReplayFrame.FirstCharTiming"/>) and is applied in
        /// <see cref="Replays.ReplayEngineFeed.Apply"/>, so every replay recorded before it existed
        /// carries the bit clear and re-derives with the whole span paying its first character 0,
        /// exactly as its player was scored.</para>
        /// </summary>
        public bool FirstCharTiming { get; set; }

        /// <summary>
        /// Whether <see cref="AllowWrongInput"/> reaches the WORD GAP as well as the lyric
        /// characters (backlog 181). With it on, a wrong (non-space) key pressed while the caret
        /// sits on a space cell is typed THROUGH exactly like a wrong letter on a lyric cell: the
        /// gap takes the typo character, shows it in the error red instead of an invisible red space
        /// (see <c>LyricLineDisplay.CellGlyph</c>), the caret advances, backspace erases it, and
        /// retyping the space earns the cell's real judgement plus any streak the typo broke. With
        /// it off, that same press is REJECTED by the gatekeeper branch, which is the strict outcome
        /// the word gap has always had.
        ///
        /// <para>It moves the CELL side only. The space KEY stays strict under both arms: no wrong
        /// space is ever typed into any cell, because the spacebar is the word-advance key and not a
        /// glyph a player means to leave in a lyric (backlog 50, and <see cref="SpaceSkipsWord"/>'s
        /// interception of a space on a lyric character is untouched). It is also inert under
        /// <see cref="AllowWrongInput"/> = false, being an extension of that flag and not a
        /// competitor to it: Gatekeeper rejects every wrong key, gap or no gap.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like <see cref="SyllableTiming"/>: set
        /// before the first keypress and left alone afterwards, judgements already made are never
        /// revisited. Live play turns it on UNCONDITIONALLY, Hard Rock included
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>): HR halves the judgement windows, and this
        /// is not a window, it is the input model, which HR does not touch (its runs already type
        /// wrong LETTERS through). The default is the CLASSIC era, which every replay recorded
        /// before backlog 181 must re-derive under: those runs contain wrong-key-on-gap frames that
        /// were REJECTED at record time, and typing them through would move the caret, the cells and
        /// the whole account. Which arm a re-derivation gets is decided by the replay's own CONFIG
        /// frame (<see cref="Replays.TypeBeatReplayFrame.WrongInputOnWordGaps"/>, flags bit 3) and
        /// applied in <see cref="Replays.ReplayEngineFeed.Apply"/>, so a stored score always
        /// reproduces the model its fingers were graded on.</para>
        /// </summary>
        public bool WrongInputOnWordGaps { get; set; }

        /// <summary>
        /// MONKEYTYPE SPACE DISCIPLINE (backlog 184): the spacebar is the WORD BOUNDARY, a key the
        /// player owes at every gap rather than one the caret glides over. Two rules, and
        /// <see cref="SpaceSkipsWord"/> decides which of them a run gets, because each fixes what that
        /// setting's own arm did with a misplaced space. Not a user setting: live play turns it on
        /// unconditionally, exactly as <see cref="WrongInputOnWordGaps"/> is turned on.
        ///
        /// <para>With <see cref="SpaceSkipsWord"/> ON, a wrong letter typed on a WORD GAP spoils the
        /// gap WITHOUT moving the caret. The gap takes the typo in every other particular
        /// <see cref="WrongInputOnWordGaps"/> already gives it (the keypress, the error, the streak
        /// snapshot, the deferred judgement and the error-red typed glyph), but the caret PARKS on it:
        /// a second wrong letter overwrites that same cell instead of spoiling the next one, so one
        /// parked gap is one unfixed typo however many letters land on it; SPACE steps over the gap
        /// and leaves the typo standing; backspace clears it where it sits. Without the park, the
        /// follow-up space met a gap whose <c>Expected</c> the skip gate no longer reads as a gap, and
        /// one mistimed keystroke threw away the whole of the next word.</para>
        ///
        /// <para>With <see cref="SpaceSkipsWord"/> OFF, a SPACE typed on a lyric character is typed
        /// THROUGH as an ordinary typo instead of being rejected: with no word to skip, that press is
        /// nothing but a wrong character, so it takes the path every other wrong character takes (cell
        /// <see cref="CellState.Wrong"/>, caret advances, backspace-correctable). Rendering needs no
        /// arm: a wrong LYRIC cell shows its EXPECTED character in the error red
        /// (<c>LyricLineDisplay.CellGlyph</c> substitutes the typed one for gaps only), which is what
        /// this wants, since a red space is nothing at all to look at. The knock-on is deliberate:
        /// mid-word spaces stop feeding the mash-fail streak in live play, because they no longer
        /// reach the rejection branch that grows it.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like <see cref="WrongInputOnWordGaps"/>: set
        /// before the first keypress and left alone afterwards. Both halves change what an
        /// already-recorded keystroke MEANS, i.e. where the caret sits after it, so a replay written
        /// before backlog 184 re-derived under the live rule would desynchronise every keystroke that
        /// follows the first misplaced space in it. Which arm a re-derivation gets is decided by the
        /// replay's own CONFIG frame (<see cref="Replays.TypeBeatReplayFrame.StrictSpaces"/>, flags
        /// bit 4) and applied in <see cref="Replays.ReplayEngineFeed.Apply"/>.</para>
        /// </summary>
        public bool StrictSpaces { get; set; }

        /// <summary>
        /// Whether the spacebar is inside the timing challenge (see <see cref="ProcessKey"/>).
        /// <see cref="SpaceTimingRule.Untimed"/> is the live rule (backlog 148) and the default; only
        /// <see cref="Scoring.TypeBeatReplayScorer"/> ever sets the other one, to re-derive a score
        /// from before the exemption existed. It must be set BEFORE the first keypress and left alone
        /// afterwards, exactly like <see cref="ComboRestore"/> and <see cref="WindowScale"/>:
        /// judgements already made are never revisited.
        ///
        /// <para>Setting it recomputes the sync readouts' denominator
        /// (<see cref="totalTimedCells"/>), which is the one piece of the rule that is decided per
        /// BEATMAP rather than per keypress. Doing that here rather than in the constructor is what
        /// lets the rule be selected after the engine is built, which is when a replay harness knows
        /// which era it is judging.</para>
        /// </summary>
        public SpaceTimingRule SpaceTiming
        {
            get => spaceTiming;
            set
            {
                spaceTiming = value;
                countTimedCells();
            }
        }

        /// <summary>
        /// The ladder for the BEATMAP's own granularity, at the current <see cref="WindowScale"/>.
        /// Individual cells may be judged at a wider tier than this (an estimated or low-confidence
        /// word falls back to Line), which is what <see cref="windowsFor"/> resolves.
        /// </summary>
        public SyncWindows Windows { get; private set; }

        /// <summary>
        /// A MULTIPLICATIVE scale on every judgement window this engine grades against, 1 by default
        /// (the ladder exactly as <see cref="SyncWindows.For"/> hands it over). 2 doubles every
        /// window, 0.5 halves it; the scale multiplies each granularity tier's bounds, so it widens
        /// Line, Word and Syllable cells in the same proportion rather than flattening them.
        ///
        /// <para>DELIBERATELY NOT AN "EASY" FLAG. The Easy mod sets it to 2, but a mod that scales
        /// the windows by the audio rate wants exactly the same lever, and the two must COMPOSE:
        /// each such mod multiplies its own factor in (<c>WindowScale *= factor</c>) rather than
        /// assigning, so the result does not depend on the order the mods are applied in.</para>
        ///
        /// <para>Set BEFORE the first keypress and left alone afterwards, like
        /// <see cref="ComboRestore"/>: judgements already made are never revisited, and the two sync
        /// readouts re-derive quality from stored deltas, so moving it mid-run would restate old
        /// presses under a ladder they were never graded on. Both application sites do it while the
        /// engine is being built (<c>DrawableTypeBeatRuleset.createEngine</c> for a live play,
        /// <c>TypeBeatReplayScorer.createEngine</c> for a re-judged replay).</para>
        /// </summary>
        public double WindowScale
        {
            get => windowScale;
            set
            {
                if (!double.IsFinite(value) || value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "A judgement window scale must be finite and positive.");

                windowScale = value;
                applyWindowScale();
            }
        }

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
        /// Whether the SONG is asking for characters on the very line the player's caret is on:
        /// the playhead is inside a typeable window AND that window belongs to the caret's line.
        /// Equal to <see cref="SongWindowOpen"/> with a pinned caret, where the two are always the
        /// same line, and the pair the key handler needs once the caret is unpinned.
        ///
        /// <para><see cref="SongWindowOpen"/> alone is not that question, and the difference is the
        /// whole of a real map's instrumental gap: a decoder-built line's window runs to the NEXT
        /// line's start (contiguous, no holes), so through a twelve-second instrumental the playhead
        /// is still inside line N's window and <see cref="SongWindowOpen"/> stays true, while the
        /// player who finished line N is parked at the head of line N+1 with nothing being asked of
        /// them. That is exactly when Space has to reach the mid-song skip overlay instead of being
        /// eaten as a keystroke (see <c>TypeBeatPlayfield</c>'s key handler).</para>
        /// </summary>
        public bool SongIsOnTheCaretsLine => activeLineIndex != -1 && activeLineIndex == nextSealIndex && SongWindowOpen;

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

        /// <summary>
        /// Mean sync quality (x100) over TIMED cells resolved so far (judged correct + sealed); 100
        /// before anything resolves. SPACE cells are excluded from both halves of the mean since
        /// backlog 148: an untimed space is judged on a zeroed delta, so counting it would hand back
        /// a full 1.0 quality it never earned and lift this readout for free. Out of the numerator
        /// AND the denominator, so a space neither helps nor hurts, which is the same treatment the
        /// sync timeline gives it (see <see cref="ProcessKey"/>).
        ///
        /// <para>DISPLAY ONLY, and only when asked for: the HUD reads this behind
        /// <c>TypeBeatRulesetSetting.ShowSyncMetric</c> (off by default since backlog 251) and
        /// nothing else reads it at all. It is still computed unconditionally, which is what keeps
        /// the toggle a pure display switch.</para>
        /// </summary>
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
                        if (!isTimed(cell))
                            continue;

                        if (cell.State == CellState.Correct && cell.JudgedDelta is double d)
                        {
                            sum += windowsFor(cell).SyncQuality(d);
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

        /// <summary>Mashing mod (Relax): every keypress is judged as the caret cell's expected char.</summary>
        public bool MashingEnabled { get; set; }

        /// <summary>
        /// Dyslexia mod (backlog 231, unranked): the characters of a WORD may be typed in ANY ORDER.
        /// A press is matched against the first cell of the word the caret is inside that nobody has
        /// typed anything into yet, scanned ascending, under exactly the rules the caret cell is
        /// matched under today (<see cref="CaseSensitive"/> included). Only a key that matches
        /// NOTHING still untyped in that word is a wrong key, and it is then priced exactly as it is
        /// now, at the caret, by the same wrong-input machinery.
        ///
        /// <para>The caret stays the LEFTMOST untyped typeable cell of the line, which is what the
        /// mod costs to implement and what it buys: <see cref="CaretCountablePosition"/>, the
        /// Fletcher rush cap (<see cref="rushesPastCap"/>), the Flashlight reveal window and
        /// <see cref="IsLineComplete"/> all read the caret as a monotone frontier, so a caret allowed
        /// to sit anywhere else would make all four measure something else. A press that lands ahead
        /// of the caret therefore moves nothing; the frontier rolls forward over the run of cells
        /// already typed the moment the leftmost one is finally struck
        /// (<see cref="advanceCaretToFrontier"/>).</para>
        ///
        /// <para>A FREESTYLE slot is not an any-order target. It matches every key but space, so a
        /// scan that offered it would consume it with the first press and starve the exact match the
        /// player meant; it still accepts anything AT THE CARET, exactly as it does today, which is
        /// reached whenever the scan finds nothing (so the slot fills with the key that fits nothing
        /// else, which is the character it stands for).</para>
        ///
        /// <para>Deterministic, which is what makes a Dyslexia replay re-derive: first match
        /// ascending is a pure function of (the cells' states, the pressed char), and a replay stores
        /// (char, time) alone.</para>
        ///
        /// <para>A MOD flag and NOT an era flag, which is why it has no CONFIG frame bit and no line
        /// in <c>ReplayEngineFeed.Apply</c>. An era bit exists to disambiguate runs recorded BEFORE a
        /// rule existed, and no stored run can carry a mod that did not exist when it was recorded:
        /// re-derivation is driven by the score's MOD LIST, read by the two engine factories
        /// (<c>DrawableTypeBeatRuleset.createEngine</c> and
        /// <see cref="Scoring.TypeBeatReplayScorer"/>). <see cref="MashingEnabled"/> is the exact
        /// precedent, and the two mods are declared incompatible: mashing rewrites the press into the
        /// caret cell's expected char before any of this is reached, so on an ordinary cell the
        /// leftmost untyped cell always matches and the scan can only ever return the caret. A
        /// FREESTYLE cell is exempt from that rewrite (the pressed char is the one thing such a cell
        /// must remember), which is the one place the pair would not merely be inert, and the
        /// incompatibility covers it rather than a guard here.</para>
        /// </summary>
        public bool AnyOrderWithinWord { get; set; }

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
        ///
        /// <para>WHICH CELLS it reaches is a second axis, <see cref="WrongInputOnWordGaps"/>: the
        /// lyric characters always, the word gap only under that era flag (backlog 181). This one
        /// stays the gate on both, so Gatekeeper is still a single "no wrong key lands anywhere".</para>
        /// </summary>
        public bool AllowWrongInput { get; set; } = true;

        /// <summary>
        /// "Space to skip current word" (backlog 110), a local SETTING and not a mod, OFF by default.
        /// When on, a space pressed while the caret sits inside a word abandons the rest of that word
        /// and lands the caret on the word gap, so one bad character costs a word instead of the run.
        ///
        /// <para>What "abandons" means precisely, since backlog 167: every
        /// <see cref="CellState.Untyped"/> cell of that word enters
        /// <see cref="CellState.Abandoned"/>, which is a PHANTOM state and not a resolution. Nothing
        /// else does. A cell typed CORRECTLY has handed its Great over and there is no un-apply
        /// (<c>DrawableTypeBeatCharObject.ApplyEngineResult</c> drops every later result on an
        /// already-judged cell). A cell typed WRONG is a cell the player finished, so abandoning the
        /// word cannot make it a miss (backlog 124); its deferred result is decided at the seal like
        /// every other unfixed typo.</para>
        ///
        /// <para><b>Abandoning is not giving up (backlog 167).</b> A skipped word is RE-TYPEABLE:
        /// one backspace steps transparently back over the phantom cells, resetting them to
        /// <see cref="CellState.Untyped"/> and landing the caret on the last character actually
        /// typed, and re-typing them earns their ordinary judgements, their ordinary HP recovery and
        /// the streak the skip broke. The setting therefore means "I will come back to this" rather
        /// than "I give up on this word", which is the accepted consequence of making the cells
        /// earnable at all: a cell takes exactly ONE osu result, so applying a Miss at the skip is
        /// precisely what made re-earning impossible.</para>
        ///
        /// <para>What the skip takes IMMEDIATELY is the one thing that cannot wait: a single combo
        /// break, snapshotted against the first abandoned cell through the same backlog 140
        /// machinery a typo's break uses, so the run resumes when that cell is finally typed. The
        /// miss COUNT and the osu RESULTS wait for the seal, where any cell still phantom resolves
        /// exactly as an untyped cell does. So a skip nobody goes back for costs precisely what it
        /// always cost, and one the player returns to costs nothing beyond the detour.</para>
        ///
        /// <para>The press itself is NOT a keypress judgement: it never enters the accuracy counters
        /// and never counts as a <see cref="Mistyped"/>, because it is a deliberate control action
        /// rather than a typo. It can only ever LOSE cells, never earn any, which is why it needs no
        /// score or pp multiplier despite being judgement-relevant.</para>
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
        /// FLEXIBLE LINES: the player's caret is decoupled from the song's playhead. Three
        /// behaviours, all confined to this flag so the pinned path stays byte-identical:
        /// <list type="bullet">
        /// <item>RUSH FREEDOM: finishing a line moves the caret straight on to the next one instead
        /// of waiting for its cue (<see cref="rollForwardIfFinishedEarly"/>), and since backlog 218
        /// no earlier than <see cref="FLETCHER_DRAG_GRACE_MS"/> before that cue
        /// (<see cref="BoundedRush"/>). The finished line is left unsealed and seals on its own
        /// normal deadline with nothing missed.</item>
        /// <item>DRAG FREEDOM: a line the player is still typing is not force-sealed at its normal
        /// deadline; the seal is deferred by <see cref="FLETCHER_DRAG_GRACE_MS"/> so the caret is
        /// never yanked off a line mid-word (<see cref="sealPermitted"/>).</item>
        /// <item>CHARACTER-DISTANCE RUSH CAP: a press that puts the caret more than
        /// <see cref="FLETCHER_MAX_CHARS_AHEAD"/> countable chars ahead of the playhead lands and
        /// scores as normal but earns no combo.</item>
        /// </list>
        /// Per-char judgement windows are untouched: rushing reads as early deltas and dragging as
        /// late ones, so accuracy, sync% and the judgement counts report the drift honestly.
        ///
        /// <para>Since backlog 208 this is the LIVE default for every stack
        /// (<c>DrawableTypeBeatRuleset.createEngine</c>), and the mod named Fletcher is the one that
        /// turns it OFF and re-pins the caret to the playhead
        /// (<see cref="Mods.TypeBeatModFletcher"/>). It stays FALSE by default here, which is the
        /// classic pinned ERA every replay recorded before 208 was played under, and
        /// <see cref="FlexibleLineSnap"/> is the bit that says a stored run was played the new
        /// way.</para>
        /// </summary>
        public bool FletcherEnabled { get; set; }

        /// <summary>
        /// The FLEXIBLE-LINES era (backlog 208), and the one behaviour that separates the new
        /// default from the old "FT" mod that shipped the same three freedoms: a caret sitting PAST
        /// THE LAST CHARACTER of its line is SNAPPED to the next line the moment that line starts
        /// (its <see cref="TypingLine.ActivationTime"/>), so a player who has finished their line is
        /// carried onto the new one exactly as pinned play always carried them
        /// (<see cref="snapForwardOnLineStart"/>). A line the player has NOT finished is never
        /// snapped: lagging behind is the freedom the flexible caret exists to grant.
        ///
        /// <para>Judgement relevant, so it is an ERA flag of its own on CONFIG frame bit 5, and NOT
        /// simply implied by <see cref="FletcherEnabled"/>: every stored "FT" run was played without
        /// the snap, so re-deriving one with it would move the caret onto a line its player was
        /// still parked behind and desynchronise every keystroke after it. Set for every new live
        /// stack that is not running the pinning mod; false everywhere else.</para>
        /// </summary>
        public bool FlexibleLineSnap { get; set; }

        /// <summary>
        /// THE SYMMETRIC RUSH BOUND (backlog 218): a finished caret may enter the next line only
        /// from <see cref="FLETCHER_DRAG_GRACE_MS"/> before that line's own
        /// <see cref="TypingLine.ActivationTime"/> onward (<see cref="entryPermitted"/>). The exact
        /// mirror of the drag side (<see cref="sealPermitted"/>), which holds a line open exactly
        /// that long past its natural end: one constant, both directions, so the player may run
        /// ahead of the song by the same margin they may fall behind it.
        ///
        /// <para>Without it RUSH was time-UNBOUNDED while DRAG never was.
        /// <see cref="rollForwardIfFinishedEarly"/> handed the caret to the next line the instant the
        /// last cell of the current one landed, however many seconds before that line's cue, and the
        /// roll is TRANSITIVE (the line it lands on can be typed out and rolled off in turn), so a
        /// fast player could walk the whole map at the top of the song. Nothing stopped that but the
        /// rush cap, which costs combo and blocks nothing.</para>
        ///
        /// <para>What a refused roll does: the caret PARKS past the last cell of its line, the state
        /// <see cref="FlexibleLineSnap"/> already understands. Keypresses there are inert
        /// (<see cref="ProcessKey"/> answers false on a complete line: no judgement, no typo, no
        /// combo break, exactly the dead-zone semantics the pinned game has), the WPM clock does not
        /// run (<see cref="Update"/> accrues only while the line is INCOMPLETE), and
        /// <see cref="snapForwardOnLineStart"/> performs the deferred roll the moment the bound
        /// opens. The DRAG side is untouched, and neither the drag cutoff's hand-over nor the seal
        /// loop's ordinary one is refused: those are the SONG arriving, an entry that is late rather
        /// than early.</para>
        ///
        /// <para>FALSE by default, and era-styled exactly like <see cref="FlexibleLineSnap"/>: it
        /// decides which line the caret is on at a given time, so a run stored before it existed
        /// carries CONFIG frame bit 7 (<see cref="Replays.TypeBeatReplayFrame.BoundedRush"/>) clear
        /// and re-derives with the unbounded roll its player actually had, byte for byte. Set for
        /// EVERY new live stack (<c>DrawableTypeBeatRuleset.createEngine</c>), the pinning mod's
        /// included, where it is inert because <see cref="FletcherEnabled"/> gates every roll: the
        /// same "record it uniformly" convention bits 3, 4 and 6 follow.</para>
        /// </summary>
        public bool BoundedRush { get; set; }

        /// <summary>
        /// Whether the flexible caret was asked for by a MOD rather than by the era bit, which is
        /// the one thing a CONFIG frame cannot say for itself. The retired "FT" mod is the only
        /// pre-208 way a run was flexible, and it recorded flags bit 5 CLEAR (the bit did not
        /// exist), so re-deriving such a run from the frame alone would pin a caret that was played
        /// unpinned. Set by the two engine factories from the score's mod list
        /// (<c>DrawableTypeBeatRuleset.createEngine</c> and
        /// <see cref="Scoring.TypeBeatReplayScorer"/>) and read in exactly one place,
        /// <c>ReplayEngineFeed.Apply</c>.
        /// </summary>
        public bool FlexibleCaretFromMod { get; set; }

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
        /// <para>HEALTH is what listens (see <c>TypeBeatPlayfield.onTypoErased</c>): a typo drains
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

        /// <summary>Ring buffer of the ACTIVE-REAL-TIME stamps of the last correct keypresses (see <see cref="LiveRollingWpm"/>).</summary>
        private readonly double[] rollingSamples = new double[rolling_wpm_window];

        private readonly int totalTypeableCells;

        /// <summary>
        /// The same total with SPACE cells taken out: the denominator of both sync readouts since
        /// backlog 148 took the spacebar out of the timing challenge. Kept as its own field rather
        /// than subtracted at the call sites, because <see cref="totalTypeableCells"/> is the
        /// COMPLETION denominator (every character of the map the player owes, spaces included) and
        /// the two must not be confused. Counted here and not derived from
        /// <see cref="countableTargets"/>, whose length happens to equal it today: that array is the
        /// Fletcher rush cap's currency, and tying the sync readout to it would make one a silent
        /// constraint on the other.
        ///
        /// <para>Not readonly, because <see cref="SpaceTiming"/> decides which cells are in it and a
        /// replay harness selects that after construction. It is still fixed before the first
        /// keypress, like every other era switch.</para>
        /// </summary>
        private int totalTimedCells;

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

        /// <summary>
        /// THE LAZY CLOCK ARM (backlog 222): the line the WPM clock has been armed on AHEAD OF ITS
        /// CUE, and the instant it was armed at, which is the time of the first press the player put
        /// on that line while the song had not reached it yet. -1 / 0 when nothing is armed, which is
        /// every ordinary in-sync frame. See <see cref="clockRunsFrom"/> for what it buys.
        ///
        /// <para>Read only through <see cref="clockRunsFrom"/>, which validates it against the CURRENT
        /// <see cref="activeLineIndex"/> rather than clearing it at each of the six sites that can move
        /// the caret. An arm left behind on a line the caret has left is simply not this line's arm,
        /// and a line is only ever entered once (the caret advances, or goes to -1 and comes back on a
        /// LATER line, never on an earlier one), so a stale arm can never be mistaken for a live
        /// one.</para>
        ///
        /// <para>A pure function of (keypress times, beatmap): no wall clock, no frame cadence, and
        /// cleared by <see cref="reset"/> like every other accumulator, which is what keeps the WPM
        /// digest in <c>ReplayRewindTest</c> reproducible across a seek.</para>
        /// </summary>
        private int wpmClockArmedLine = -1;

        private double wpmClockArmedAt;

        private double? lastUpdateTime;

        private int rollingCount; // entries held in rollingSamples, capped at rolling_wpm_window
        private int rollingNext;  // next slot to write; also the oldest entry once the ring is full

        /// <summary>
        /// The one outstanding combo snapshot (see <see cref="ComboRestored"/>): the cell a wrong
        /// keypress spoiled or a word skip abandoned, and the streak that break cost, or null when
        /// there is nothing to go back for. Set by that keypress or skip (through
        /// <see cref="snapshotRedeemableBreak"/>, the one write site the two share), redeemed by
        /// typing that same cell correctly, and discarded by any other combo break that had a streak
        /// to take (<see cref="discardRestorableStreak"/>).
        ///
        /// <para><c>ownPressCredit</c> is backlog 243: how much of the CURRENT run was credited by
        /// the claim's own press rather than typed after it. It is 1 for a claim a word skip took,
        /// because the skipping space is judged on the word gap it lands on and rebuilds the run to
        /// exactly 1 (<see cref="creditTheClaimsOwnPress"/>), and 0 for every other claim and for a
        /// skip whose space credited nothing. A break standing on no more than that is passive, the
        /// same way a break landing at zero is, and SPENDS the credit when it does. See
        /// <see cref="SkipSpaceCredit"/>.</para>
        /// </summary>
        private (int lineIndex, int cellIndex, int streak, int ownPressCredit)? restorable;

        private double windowScale = 1;

        private SpaceTimingRule spaceTiming = SpaceTimingRule.Untimed;

        /// <summary>
        /// The ladder each granularity is judged at under the current <see cref="WindowScale"/>,
        /// indexed by <see cref="TimingGranularity"/>. Rebuilt when the scale is set, so a keypress
        /// costs one array read and no allocation whatever the scale is: at 1 the entries ARE
        /// <see cref="SyncWindows.For"/>'s cached instances, and at any other scale there are three
        /// scaled ones for this engine, never a global cache keyed by granularity and scale.
        /// </summary>
        private readonly SyncWindows[] windowsByGranularity = new SyncWindows[Enum.GetValues<TimingGranularity>().Length];

        public TypingEngine(LyricBeatmap beatmap, bool literate = false)
        {
            Beatmap = beatmap ?? throw new ArgumentNullException(nameof(beatmap));

            // Assigned here as well as in applyWindowScale (which sets the same value at the default
            // scale of 1) because definite assignment of a get-only-outside property cannot see
            // through a helper call.
            Windows = SyncWindows.For(beatmap.Granularity);
            applyWindowScale();

            Literate = literate;
            CaseSensitive = literate;

            lines = new List<TypingLine>(beatmap.Lines.Count);

            foreach (var line in beatmap.Lines)
                lines.Add(TypingLine.FromLyricLine(line, beatmap.Granularity, literate));

            lineSealed = new bool[lines.Count];
            lineAbandoned = new bool[lines.Count];

            foreach (var line in lines)
                totalTypeableCells += line.TypeableCount;

            countTimedCells();

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
        /// <see cref="StrictSpaces"/>), the mod flags
        /// (<see cref="FletcherEnabled"/>, <see cref="MashingEnabled"/>, <see cref="Literate"/>,
        /// <see cref="CaseSensitive"/>), <see cref="WindowScale"/> and the era rules
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
            foreach (var line in lines)
            {
                foreach (var cell in line.Cells)
                {
                    cell.State = CellState.Untyped;
                    cell.TypedChar = null;
                    cell.JudgedDelta = null;
                    cell.FirstCorrectDelta = null;

                    // The one place backlog 210's correction flag is ever cleared: it survives a
                    // backspace by design, so only a whole-run rebuild puts it back.
                    cell.HeldWrongBeforeJudged = false;
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

            score = 0;
            combo = 0;
            maxCombo = 0;
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

            restorable = null;
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
            while (nextSealIndex < lines.Count && canSeal(lines[nextSealIndex], time) && sealPermitted(nextSealIndex, time))
            {
                int index = nextSealIndex;
                var line = lines[index];

                int missed = 0;

                // Of those, the ones the line really did run out of time on: the only group whose
                // break has not been taken yet, and therefore the only one that can break combo here.
                int unforeseen = 0;

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

                    bool phantom = cell.State == CellState.Abandoned;

                    if (cell.State != CellState.Untyped && !phantom)
                        continue;

                    cell.State = CellState.Missed;

                    missed++;
                    counts[JudgementType.Miss]++;

                    if (phantom)
                        (abandoned ??= new List<int>()).Add(i);
                    else
                        unforeseen++;
                }

                bool broke = unforeseen >= 1;

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

                // BEFORE the seal itself, so a consumer settles the phantom cells before their Miss
                // results land on them: health gives the skip's drain back into the very drain those
                // misses are about to take, and the ledger that keeps them from breaking osu's combo
                // a second time has to be written before the result consults it.
                if (abandoned != null)
                    raise(AbandonSealed, new AbandonedCells(index, abandoned));

                if (broke)
                    raise(ComboBroken);

                raise(LineSealed, new LineSealResult(index, missed, broke));
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
                    raise(Finished);
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
                    pendingActivation = true;
                }
            }

            // (4) FLEXIBLE-LINES SNAP (backlog 208): the caret is sitting past the last character of
            //     its line and the next line has just started, so hand the player onto it, exactly
            //     as the pinned arm above would have. Placed after the activation arm and sharing
            //     its announcement so a snap that follows a fresh activation (or a drag cutoff, or
            //     several snaps at once) still relayouts the stage once.
            if (activeLineIndex != -1 && snapForwardOnLineStart(time))
                pendingActivation = true;

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
        /// FLEXIBLE-LINES SNAP (backlog 208, see <see cref="FlexibleLineSnap"/>): while the caret
        /// sits PAST THE LAST CHARACTER of its line, the next line STARTING takes it, which is what
        /// keeps the flexible default feeling like the pinned game it replaced (finish your line and
        /// the song moves you on). A line the player has not finished is never touched: dragging
        /// behind is precisely the freedom the flexible caret grants, and
        /// <see cref="sealPermitted"/> makes the same distinction for the same reason ("nothing left
        /// untyped means there is no drag to protect").
        ///
        /// <para>"Finished" is <see cref="IsLineComplete"/>, i.e. the caret has walked off the end
        /// of the cell list. That is exact rather than approximate: every caret advance runs
        /// <see cref="autoSkipForward"/>, so a caret at <c>Cells.Count</c> is a caret with no
        /// typeable cell left in front of it, and it is the same predicate the keypress-driven
        /// <see cref="rollForwardIfFinishedEarly"/> gates on. Cells left BEHIND the caret wrong or
        /// abandoned do not hold the line: the player is done with them, and the seal resolves them
        /// exactly as it always did.</para>
        ///
        /// <para>A LOOP rather than a single step, because the line it lands on can be finished the
        /// instant it is reached (a line whose cells are all non-typeable is complete at caret 0),
        /// and the roll-forward this backs up does not recurse. Returns whether the caret moved, so
        /// the caller announces one <c>LineActivated</c> however many lines were crossed.</para>
        ///
        /// <para>Since backlog 218 this is also the DEFERRED ROLL arm (see <see cref="BoundedRush"/>),
        /// which is why the two are one method rather than two: both move a FINISHED caret onto the
        /// next line on a TIME condition, they would fire on the same frame, and a second arm could
        /// only ever raise a second <c>LineActivated</c> for a caret the first one had already moved.
        /// The instant they fire at is <see cref="entryOpensAt"/>, the line's activation under the
        /// unbounded era and <see cref="FLETCHER_DRAG_GRACE_MS"/> before it under the bounded one, so
        /// the head start the bound grants a rushing player still exists: refusing the keypress roll
        /// and then waiting for the full activation would take with one hand what the mirror gives
        /// with the other.</para>
        /// </summary>
        private bool snapForwardOnLineStart(double time)
        {
            // BoundedRush belongs in this gate as well as FlexibleLineSnap: this is the only arm that
            // can move a caret the bound parked, and a live stack always sets both anyway.
            if (!FletcherEnabled || (!FlexibleLineSnap && !BoundedRush) || isFinished)
                return false;

            bool snapped = false;

            while (IsLineComplete
                   && activeLineIndex + 1 < lines.Count
                   && time >= entryOpensAt(activeLineIndex + 1))
            {
                activeLineIndex++;
                caretIndex = 0;
                autoSkipForward();
                snapped = true;
            }

            return snapped;
        }

        /// <summary>
        /// The instant from which the WPM/sync active-time clock runs across the frame that STARTED at
        /// <paramref name="previousTime"/>, or null when it does not run over that frame at all.
        /// Normally that instant is <paramref name="previousTime"/> itself, i.e. the whole frame
        /// counts; the one case that returns something later is the lazy arm below.
        ///
        /// <para>The whole frame, always, with a pinned caret. Under <see cref="FletcherEnabled"/> the
        /// caret can be sitting on a line the song has not reached yet, and a clock that ran through a
        /// 20-second instrumental would read the wait as typing time; so being there is not by itself
        /// enough, and the clock runs from that line's <see cref="TypingLine.ActivationTime"/>, which
        /// is exactly when the line would have gone active while pinned.</para>
        ///
        /// <para>THE TWO PARKED STATES ARE NOT THE SAME FACT (backlog 222 correcting backlog 218,
        /// whose note here said they were, and that claim is what hid the defect below). A caret the
        /// rush bound left sitting past the last cell of its OWN line still needs nothing from this
        /// method: the caller accrues only while the active line is INCOMPLETE, that caret's line is
        /// complete by definition, and there is genuinely nothing the player can type. But a caret
        /// that <see cref="rollForwardIfFinishedEarly"/> or <see cref="snapForwardOnLineStart"/> has
        /// moved on to the NEXT line sits there from <see cref="entryOpensAt"/>, which is
        /// <see cref="FLETCHER_DRAG_GRACE_MS"/> BEFORE that line's activation, and
        /// <see cref="ProcessKey"/> has no time gate of its own: the player really can type there, up
        /// to 1500 ms per line (unboundedly, before 218 bounded the roll). Every character they land
        /// is counted by <see cref="countCorrectCells"/> for the rest of the run, so counting them
        /// against a stopped clock walked <see cref="LiveWpm"/> and <see cref="LiveRollingWpm"/>
        /// upward for free.</para>
        ///
        /// <para>THE LAZY ARM. So the clock arms on the FIRST press the player puts on such a line
        /// (<see cref="armWpmClockAheadOfTheCue"/>) and runs from that press's own time onward. Not
        /// from <see cref="entryOpensAt"/>: that would hand the whole head start back as typing time
        /// for a player who is sitting there NOT typing, which is the dilution the ActivationTime gate
        /// exists to prevent and which <c>ActiveTimeDoesNotRunWhileParkedAheadOfTheCue</c> pins. And
        /// nothing is back-dated: the arming press is credited no elapsed time at all, exactly like a
        /// press made at ActivationTime + 0 on the ordinary path. Once the playhead reaches the line,
        /// the ActivationTime branch above answers first and the arm is never consulted again.</para>
        /// </summary>
        private double? clockRunsFrom(double previousTime)
        {
            if (!FletcherEnabled || activeLineIndex == -1)
                return previousTime;

            if (previousTime >= lines[activeLineIndex].ActivationTime)
                return previousTime;

            // Ahead of the cue: the clock runs only from an arm, and only from an arm belonging to
            // the line the caret is on now (see wpmClockArmedLine for why that check is the reset).
            if (wpmClockArmedLine == activeLineIndex)
                return Math.Max(previousTime, wpmClockArmedAt);

            return null;
        }

        /// <summary>
        /// Arm the WPM clock on the active line when the press at <paramref name="time"/> is the first
        /// one the player has put on it AHEAD OF ITS CUE (see <see cref="clockRunsFrom"/>). No-op on
        /// every ordinary press, i.e. one made at or after the line's
        /// <see cref="TypingLine.ActivationTime"/>, where the clock is already running.
        ///
        /// <para>Called from <see cref="ProcessKey"/> once the press is known not to be inert, so
        /// every press that can resolve or spoil a cell arms the clock, a Gatekeeper-rejected wrong
        /// key included: what the arm records is that the player is TYPING here, not what the
        /// keystroke turned out to be worth. A press the caret-parked guard refuses outright never
        /// reaches it, which is right, since that press does nothing at all.</para>
        /// </summary>
        private void armWpmClockAheadOfTheCue(double time)
        {
            if (!FletcherEnabled || activeLineIndex == -1)
                return;

            if (time >= lines[activeLineIndex].ActivationTime)
                return;

            // Only the FIRST press arms. A later one must not push the arm forward, or the time
            // between the two presses (the player typing) would be swallowed.
            if (wpmClockArmedLine == activeLineIndex)
                return;

            wpmClockArmedLine = activeLineIndex;
            wpmClockArmedAt = time;
        }

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
        /// DRAG FREEDOM (see <see cref="FletcherEnabled"/>): a line the player is still typing must not
        /// be force-sealed out from under them at its normal deadline. The seal is deferred while the
        /// caret is on the line, up to <see cref="FLETCHER_DRAG_GRACE_MS"/> past its hard deadline;
        /// past that the line seals as usual (untyped cells become misses, one combo break) and the
        /// caret is moved on. Always true with a pinned caret, and true under a flexible one for any
        /// line the player is not currently on, so a finished-early line still seals exactly on its
        /// own deadline.
        ///
        /// <para>Its mirror is <see cref="entryPermitted"/> (backlog 218): this one is how far past a
        /// line's natural END a dragging player may still be on it, that one is how far before a
        /// line's natural START a rushing player may already be on it, and both distances are the
        /// one <see cref="FLETCHER_DRAG_GRACE_MS"/>.</para>
        ///
        /// <para>A line the player ABANDONED with a line skip keeps the grace after the caret has
        /// left it (see <see cref="lineAbandoned"/>): the skip is caret movement only, so the line it
        /// walked out of has to reach its misses and its one combo break at the very instant it would
        /// have with the player still sitting there doing nothing.</para>
        /// </summary>
        private bool sealPermitted(int index, double time)
        {
            if (!FletcherEnabled || (activeLineIndex != index && !lineAbandoned[index]))
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
        /// THE RUSH BOUND (see <see cref="BoundedRush"/>), and the exact mirror of
        /// <see cref="sealPermitted"/>: may a FINISHED caret move on to line <paramref name="index"/>
        /// at <paramref name="time"/> yet? Drag holds a line open up to
        /// <see cref="FLETCHER_DRAG_GRACE_MS"/> past its natural end
        /// (<see cref="TypingLine.EndTime"/> + <see cref="TypingLine.SealGraceMs"/>); rush enters a
        /// line up to the same <see cref="FLETCHER_DRAG_GRACE_MS"/> before its natural start
        /// (<see cref="TypingLine.ActivationTime"/>). Always true under the unbounded era, which is
        /// what every run stored before backlog 218 was played under.
        ///
        /// <para>Asked ONLY of a caret moving itself: the keypress roll
        /// (<see cref="rollForwardIfFinishedEarly"/>) and the time-driven one
        /// (<see cref="snapForwardOnLineStart"/>). The hand-overs the SEAL LOOP performs, the
        /// ordinary one and the drag cutoff's, do not consult it and must not: the song has moved
        /// off the old line there, so entry is late rather than early, and refusing it would leave
        /// the player in a dead zone the flexible caret does not otherwise have. On a decoder-built
        /// map that is never even a near thing, because a line's activation is clamped to its own
        /// StartTime, which IS the previous line's EndTime, so this bound opens at most
        /// <see cref="FLETCHER_DRAG_GRACE_MS"/> before the previous line could seal at all.</para>
        /// </summary>
        private bool entryPermitted(int index, double time) => !BoundedRush || time >= entryOpensAt(index);

        /// <summary>
        /// The earliest instant the caret may be on line <paramref name="index"/> by RUSHING onto it:
        /// the line's own <see cref="TypingLine.ActivationTime"/> under the unbounded era, and
        /// <see cref="FLETCHER_DRAG_GRACE_MS"/> before it under <see cref="BoundedRush"/>, which is
        /// the head start a player earns for having finished the line before it. Read by both arms
        /// that move a finished caret: <see cref="entryPermitted"/> (the keypress roll) and
        /// <see cref="snapForwardOnLineStart"/> (the time-driven one).
        /// </summary>
        private double entryOpensAt(int index) => lines[index].ActivationTime - (BoundedRush ? FLETCHER_DRAG_GRACE_MS : 0);

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

        /// <summary>
        /// Whether the line still holds a typeable cell nobody has put anything into. An ABANDONED
        /// cell is one of those (backlog 167): the player owes that character exactly as much as one
        /// they simply have not reached, and it is re-typeable until the line seals, so the early
        /// seal ("nothing left to type, do not hold the next line up") must not fire on a line the
        /// player can still come back into. That keeps the reclaim window running to the line's own
        /// deadline, which is the window the grace exists to grant.
        /// </summary>
        private static bool hasUntypedTypeable(TypingLine line)
        {
            foreach (var cell in line.Cells)
            {
                if (cell.IsTypeable && (cell.State == CellState.Untyped || cell.State == CellState.Abandoned))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// LINE SKIP (backlog 241): give up the rest of the active line and move on. Deterministic in
        /// (input, time) exactly like <see cref="ProcessKey"/>, and returns whether it did anything,
        /// so the caller records a frame only for an effective press.
        ///
        /// <para>IT IS CARET MOVEMENT AND NOTHING ELSE. The caret parks past the line's last cell,
        /// which is the SAME parked state a <see cref="BoundedRush"/>-refused roll leaves behind
        /// (see <see cref="rollForwardIfFinishedEarly"/>), and from there the machinery that already
        /// exists carries the player onward: the roll below hands them the next line at once when its
        /// entry window is open, and <see cref="snapForwardOnLineStart"/> performs the deferred hand
        /// over when it opens otherwise. The cells left behind are NOT judged here. They stay
        /// <see cref="CellState.Untyped"/> and become misses in the seal loop, all at once, with the
        /// line's one combo break, at the line's own deadline: precisely what would have happened to
        /// a player who stopped typing and sat there. So no judged quantity changes value or timing
        /// against the un-skipped run, which is why the skip carries NO era bit of its own (the
        /// abandoned line's drag grace is held for it by <see cref="lineAbandoned"/>, which is the
        /// one piece of state that claim depends on).</para>
        ///
        /// <para>Deliberately NOT the word skip's shape (<see cref="skipCurrentWord"/>): that one
        /// marks cells <see cref="CellState.Abandoned"/>, takes an immediate combo break and
        /// snapshots a redeemable claim, all of which move the account at press time and would make
        /// this an era change. A line skip gives up more and costs nothing extra for it, because the
        /// player pays the same misses either way, just without having to sit through them.</para>
        ///
        /// <para>NO-OP when there is nothing to skip: no active line, the run finished, or the caret
        /// already past the last cell (a line typed out, or one already skipped). In particular Enter
        /// on a COMPLETE line does NOT perform the roll the next keypress would: the two time-driven
        /// arms already own that caret, so a second way in could only duplicate them, and the key
        /// handler lets the press fall through to its global binding in that state rather than
        /// swallowing it for nothing.</para>
        ///
        /// <para>The WPM clock needs nothing here and is deliberately NOT armed: an Enter is not
        /// typing. Accrual stops by itself the moment the caret parks (<see cref="Update"/> accrues
        /// only while the active line is INCOMPLETE, and a parked caret's line reads complete), and
        /// on the line the player lands on <see cref="clockRunsFrom"/> answers exactly as it does
        /// after a refused rush: stopped until the first press there arms it
        /// (<see cref="armWpmClockAheadOfTheCue"/>), because a stale arm belongs to a line index the
        /// caret has left.</para>
        /// </summary>
        public bool ProcessEnter(double time)
        {
            if (isFinished || activeLineIndex == -1)
                return false;

            var line = lines[activeLineIndex];

            // Hop auto-skip cells before measuring, exactly as ProcessKey does, so "the caret is at
            // the end" is asked of the same frontier a keypress would see.
            autoSkipForward();

            if (caretIndex >= line.Cells.Count)
                return false; // nothing left to give up: parked already, or the line is fully typed.

            // Only a line with something still untyped is ABANDONED. A caret walked to the end over
            // nothing but wrong cells owes no misses, so the seal has no drag to protect and the flag
            // would only hold the line open for cells that are already resolved.
            if (hasUntypedTypeable(line))
                lineAbandoned[activeLineIndex] = true;

            caretIndex = line.Cells.Count;

            // The same call the last character of a line makes, so an Enter inside the next line's
            // entry window rolls on at once and one outside it parks, with no second rule.
            rollForwardIfFinishedEarly(time);

            return true;
        }

        /// <summary>
        /// Process a lowercased char from KeyCharMap at gameplay time <paramref name="time"/>.
        /// Returns false when inert (no active line / line complete / finished).
        /// A space pressed inside a word abandons it under <see cref="SpaceSkipsWord"/> (off by default).
        /// A space typed ON a space cell is UNTIMED (backlog 148): it is judged as though it landed on
        /// target, so it always takes the top tier and never breaks combo, however loosely it was hit.
        /// Under <see cref="AnyOrderWithinWord"/> the press is offered to the whole of the caret's
        /// word rather than to the caret cell alone, and only a key that matches nothing still
        /// untyped there is wrong at all.
        /// A wrong char is TYPED THROUGH by default (<see cref="AllowWrongInput"/>), or REJECTED
        /// under Gatekeeper. Either way it breaks combo, counts as a mistype, stays in the accuracy
        /// denominator forever, and resolves NO cell against the score processor; only the rejection
        /// path grows <see cref="ConsecutiveWrongKeys"/>. On a SPACE cell the type-through needs
        /// <see cref="WrongInputOnWordGaps"/> as well (live play sets it; a stored replay says so in
        /// its own header), and a wrong SPACE key is rejected on every cell except under
        /// <see cref="StrictSpaces"/> with <see cref="SpaceSkipsWord"/> off, where it is a typo like
        /// any other. <see cref="StrictSpaces"/> also PARKS the caret on a gap a typo spoiled, so the
        /// space that gap was owed is still owed: pressing it steps over the typo, and backspace
        /// clears the typo where it sits.
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

            // The press is going to do something, so the player is typing on this line; if the song
            // has not reached it yet, that is the instant the WPM clock starts counting (backlog 222,
            // see clockRunsFrom). Placed here, above every branch below, so it is one site and no
            // path can quietly skip it, and attributed to the line the press LANDS on: a press that
            // finishes this line and rolls the caret onward has typed here, not there.
            armWpmClockAheadOfTheCue(time);

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

            // Backlog 243: set when this press is a skip that left a claim outstanding, so the combo
            // the SAME press goes on to earn on the word gap is recorded as the claim's OWN credit
            // rather than as a run the player built after the break. Read once, at the one arm that
            // increments the combo.
            bool skipLeftAClaimOutstanding = false;

            // SPACE-SKIP (see SpaceSkipsWord), evaluated BEFORE the match: a space pressed while the
            // caret sits on a lyric character abandons the rest of that word. The caret cell is
            // typeable here (autoSkipForward ran above), so "Expected is not a space" is exactly "the
            // caret is inside a word"; a space pressed ON the word gap keeps its ordinary meaning and
            // never reaches this branch. Placed after the Mashing rewrite on purpose: mashing has
            // already turned the press into the expected char, so this is unreachable under it.
            if (SpaceSkipsWord && c == ' ' && cell.Expected != ' ')
            {
                skipLeftAClaimOutstanding = skipCurrentWord(time);

                if (caretIndex >= line.Cells.Count)
                {
                    // The abandoned word ran to the end of the line, so there is no word gap for the
                    // space to land on. The line is complete, exactly as it would be had the player
                    // typed that last word out, and the same end-of-line handling applies.
                    rollForwardIfFinishedEarly(time);
                    return true;
                }

                // The caret now sits on the word gap, and the press is judged there: a space typed
                // from further back in the word is still a typed space, so it takes the ordinary
                // path below (same windows, points, combo, accuracy) and leaves the cell exactly as
                // a normally typed space would.
                cell = line.Cells[caretIndex];
            }

            // STEP OVER A SPOILED GAP (backlog 184, see StrictSpaces): the caret is PARKED on a word
            // gap that a wrong letter took, and the space that gap was owed has arrived. It walks the
            // caret past the gap and leaves the typo exactly as it is. The cell is NOT rewritten to
            // Correct, because the character sitting in it is not the one that was owed: it stays an
            // unfixed, backspace-redeemable claim, and the seal resolves it as UNFIXED_TYPO like every
            // other one. It judges nothing: no tier, no points, no combo gained and none broken (the
            // typo already took the break, and this press cannot be asked to pay for it twice).
            //
            // Counted as a CORRECT keypress, which is the same argument once more: the space IS the
            // right key for the cell it lands on, the gap it was owed, and the typo has already paid
            // an error and a break of its own. Charging accuracy for it as well would punish the same
            // word twice, and the press a player must make to recover from a mistake is not itself a
            // mistake. correctKeypresses feeds LiveAccuracy alone, so this credits accuracy and moves
            // nothing else: the cell still resolves as an unfixed typo and still costs COMPLETION,
            // which is where an unfixed typo is supposed to be paid for.
            //
            // Gated on the CELL STATE rather than on the era flags, which is exact rather than
            // approximate: the caret can only come to rest on a Wrong cell through the park above, so
            // a replay re-deriving with flags bit 4 CLEAR never reaches this branch at all. Everywhere
            // else, resolving a cell is the only way the caret got past it.
            if (c == ' ' && cell.Expected == ' ' && cell.State == CellState.Wrong)
            {
                totalKeypresses++;
                correctKeypresses++;

                caretIndex++;
                advanceCaretToFrontier();

                rollForwardIfFinishedEarly(time);
                return true;
            }

            // ANY ORDER WITHIN A WORD (backlog 231, see AnyOrderWithinWord): the press is offered to
            // every cell of the caret's word nobody has typed anything into yet, ascending, and the
            // first one it matches is the cell it lands on. This is the WHOLE of the mod, because
            // everything downstream of here is already index-parameterised: judgedDeltaFor grades a
            // cell against ITS OWN point target or syllable span, the combo-restore claim is keyed by
            // cell, the mutation writes through the cell object, and CharJudged carries the index the
            // display repaints.
            //
            // A miss leaves targetIndex at the caret, so a key matching nothing still untyped in the
            // word falls through UNCHANGED and is priced exactly as it is without the mod: typed
            // through, parked, or rejected by the unchanged block below.
            //
            // Skipped outright when the caret is ON a word gap, which is not "inside a word" at all.
            // Two ways the caret gets there and neither wants this: the gap a finished word owes its
            // space (every cell behind it is resolved, so the scan could only look backwards into a
            // word with nothing left to find), and the gap a typo has PARKED the caret on under
            // StrictSpaces, where the press must keep its ordinary meaning or the park is not a park.
            int targetIndex = caretIndex;

            if (AnyOrderWithinWord && !isWordGap(cell) && matchWithinWord(line.Cells, c) is int outOfOrder)
            {
                targetIndex = outOfOrder;
                cell = line.Cells[targetIndex];
            }

            double delta = judgedDeltaFor(line, targetIndex, time);

            // SPACES ARE UNTIMED (backlog 148), decided here rather than after the match so that
            // EVERY reading of this press agrees on what its cell was worth: the correct press
            // below, and the wrong one typed through it since backlog 181, whose CharJudgement
            // carries exactly the delta a correct press on the same cell would have carried. The
            // rule and its argument are on the untimed-space block further down; only the position
            // moved, and the move reaches nothing new: the predicate is false for every LYRIC cell,
            // so the only press it can newly touch is the gap typo it was moved for.
            bool untimedSpace = cell.Expected == ' ' && TypeBeatResultMapping.SpacesAreUntimed(spaceTiming);

            if (untimedSpace)
                delta = 0;

            // FREESTYLE cell: every char EXCEPT SPACE matches, in any case, under every mod (so the
            // Literate mod's exact-case rule and the allow-wrong-input path are both bypassed for
            // it). The press is then judged exactly like a correct char: same windows, points,
            // combo, accuracy and completion, with the pressed char kept in TypedChar.
            // SPACE is carved out (backlog 50): it is the word-advance key, not a glyph a player
            // means to leave sitting in a lyric, so it falls through to the ordinary non-match path
            // below and is judged exactly as a wrong key on any other cell would be. The strict
            // rejection is the only outcome available to it, because neither allow-wrong-input path
            // will type a space through (c != ' ' guards both arms). Unless SpaceSkipsWord is on, in
            // which case the space never reaches here at all: it was consumed by the word skip above,
            // freestyle slot included (a freestyle cell is a lyric character like any other, so a
            // space pressed on one is the player abandoning the word it sits in).
            // Literate mod folds nothing: the typed char must match the target's exact case.
            // Default gameplay is case-insensitive (both sides lower-cased through Fold).
            bool matched = (cell.IsFreestyle && c != ' ')
                           || (CaseSensitive ? c == cell.Expected : Typeability.Fold(c) == Typeability.Fold(cell.Expected));

            if (!matched)
            {
                // Reached only with targetIndex == caretIndex, under every mod: a scan hit above
                // satisfies exactly the test just made, so a press the word took out of order can
                // never arrive here and this whole block still speaks about the caret's own cell.
                //
                // DEFAULT: a wrong LETTER is typed through, marked red, backspaceable, instead of
                // rejected. What the WORD GAP does with a wrong letter is the first era switch (see
                // WrongInputOnWordGaps): it takes the typo exactly as a lyric cell does, which is the
                // live rule, or it drops to the strict branch below, which is what every stored replay
                // was played under. This path never feeds the mash-fail streak (consecutiveWrongKeys
                // is left at 0, which is why that guard is Gatekeeper-only).
                //
                // The space KEY is the second era switch (see StrictSpaces). Classically it is strict
                // on every cell: there is no cell a wrong space is typed into, because it is the
                // word-advance key and not a glyph a player means to leave sitting in a lyric. Under
                // StrictSpaces with SpaceSkipsWord OFF it is admitted on a lyric character, and the
                // reason is that with no word to skip the press means nothing else: it is a wrong
                // character, so it is treated as one, no differently from a wrong letter (the cell
                // still renders its own expected character in the error red, since CellGlyph
                // substitutes the typed char for GAPS only, which is what makes an invisible red space
                // a non-problem). With SpaceSkipsWord ON the same press never arrives here: the skip
                // gate above consumed it.
                //
                // A FREESTYLE slot keeps refusing the space key under every arm. Its promise is "any
                // character except the word-advance key" (backlog 50) and it has no expected glyph to
                // redden, so a space typed into one would blank the cell rather than mark it.
                bool spaceMayLand = StrictSpaces && !SpaceSkipsWord && !cell.IsFreestyle;

                if (AllowWrongInput && (c != ' ' || spaceMayLand) && (WrongInputOnWordGaps || cell.Expected != ' '))
                {
                    totalKeypresses++;
                    errorCount++;

                    // The streak this keypress is about to break, snapshotted against the cell it
                    // spoils: correcting that cell resumes it (backlog 140, see ComboRestored). A
                    // wrong key on a SECOND cell discards the first cell's claim the way any other
                    // intervening break would, but only if it broke a streak of its own (backlog
                    // 176, see snapshotRedeemableBreak).
                    int brokenStreak = combo;

                    combo = 0;
                    counts[JudgementType.WrongChar]++;

                    cell.State = CellState.Wrong;
                    cell.TypedChar = c;

                    // The cell is now one whose eventual judgement, if the player goes back for it,
                    // will be a CORRECTION and not a clean first attempt, and backlog 210 prices
                    // those differently (see TypeBeatResultMapping.AwardedTier). Recorded on the
                    // cell rather than counted, so a wrong-fix-wrong-fix cycle caps exactly once.
                    //
                    // Gated on the cell being UNJUDGED, which is what makes the flag mean what it
                    // says. A cell that was already judged CLEAN and then spoiled by a wrong key on
                    // the way back through keeps that clean judgement (a cell takes only its first
                    // result, and the retype that follows is inert), so flagging it would demote a
                    // judgement the player earned honestly before they ever fumbled it.
                    //
                    // Era-independent on purpose: this records what HAPPENED, and CorrectionCredit
                    // decides what it is worth, so a stored replay re-derives the same flag under
                    // either arm.
                    if (cell.FirstCorrectDelta is null)
                        cell.HeldWrongBeforeJudged = true;

                    int wrongCellIndex = caretIndex;

                    snapshotRedeemableBreak(wrongCellIndex, brokenStreak);

                    // PARK on a spoiled word gap under StrictSpaces (backlog 184), instead of moving
                    // on: the space is still owed, so the player pays it (which steps over the typo,
                    // see the branch above) or backspaces it away, rather than being carried into the
                    // next word behind a gap the skip gate can no longer read as one. A further wrong
                    // letter lands on this same cell and overwrites this same character, so one park
                    // is one unfixed typo however many letters arrive; the snapshot above is idempotent
                    // for the same reason (a break with no streak behind it leaves the standing claim
                    // alone, see snapshotRedeemableBreak).
                    //
                    // Scoped to SpaceSkipsWord because that is where the damage was: with the setting
                    // off, an advancing gap typo costs the player one cell, and with it on the next
                    // space fed the skip gate a spoiled gap and gave up a whole word. Every typo on a
                    // LYRIC cell advances exactly as it always has, under both arms.
                    //
                    // The advance rolls the frontier the same way a correct press does (see
                    // advanceCaretToFrontier), which matters under AnyOrderWithinWord and nowhere
                    // else: the cells this typo steps over can already have been typed out of order,
                    // and leaving the caret parked on one of them would break the very invariant the
                    // mod is built to keep.
                    if (!(StrictSpaces && SpaceSkipsWord && cell.Expected == ' '))
                    {
                        caretIndex++;
                        advanceCaretToFrontier();
                    }

                    // The keypress was wrong, so it is a mistype exactly as it would be in strict
                    // mode, and since backlog 109 it ACCOUNTS exactly as strict mode does too: the
                    // mistype carries the combo break by hand (TypeBeatPlayfield.onMistyped) and the
                    // cell hands the score processor nothing at all.
                    raise(Mistyped);
                    raise(ComboBroken);
                    // The CELL's judgement still travels here, for the stage's red/shake feedback,
                    // but DrawableTypeBeatHitObject.ApplyCharJudgement deliberately applies no osu
                    // result for a WrongChar: the cell's result is DEFERRED. Backspace and retype it
                    // correctly and it earns its real Great/Ok/Meh, plus the streak this press
                    // just broke (backlog 140, see ComboRestored); leave it and the seal
                    // resolves it as an unfixed typo, which is a hit and not a miss (backlog 124).
                    raise(CharJudged, new CharJudgement(activeLineIndex, wrongCellIndex, JudgementType.WrongChar, delta, 0, combo));
                    rollForwardIfFinishedEarly(time);
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
                raise(Mistyped);
                raise(ComboBroken);
                raise(WrongKeyRejected, c);
                return true;
            }

            consecutiveWrongKeys = 0;

            // SPACES ARE UNTIMED (backlog 148); the zeroing itself is done above the match, where a
            // typed-through gap typo can read the same value. Reaching HERE on a space CELL means a
            // SPACE was typed on it: Fold is only ToLowerInvariant, so nothing but ' ' folds onto
            // ' ', a freestyle cell refuses space outright, and under Mashing the press was already
            // rewritten to the cell's expected char, which is the space it stood for anyway. The
            // spacebar is deliberately outside the timing challenge (the word gap is where a
            // typist's hands reset, not a note to hit), so the press is judged as though it landed
            // dead on the cell's target: top tier whatever the clock said, and never one of the two
            // zero-point tiers that break combo.
            //
            // Written as a ZEROED DELTA rather than as a forced JudgementType so every reader of
            // this press agrees with the judgement it was handed: the ladder below (Classify(0) is
            // Great, the top tier since backlog 147 dropped Perfect), the sync tint (which reads
            // JudgedDelta back, see LyricLineDisplay), LiveSyncPercent and the results SyncPercent
            // (both average SyncQuality over that same field), and the inert retype, which
            // re-classifies the stored FirstCorrectDelta. Forcing only the tier would leave a space
            // graded Great while its real delta still dragged down the sync readout the final grade
            // is computed from, which is the same timing hazard wearing a different hat.
            //
            // Scoped to the CELL and not to the KEY, which is what keeps the three ways a space can
            // be pressed apart. A space that lands on a LYRIC character never reaches here: with
            // SpaceSkipsWord off it is rejected above (combo break, mistype, and the
            // consecutive-wrong-key streak that fails a masher at 13), and with it on it was
            // consumed by the word skip, which misses the abandoned cells and takes its one combo
            // break before the caret ever reaches the gap. And an untimed space is not a FREE one:
            // a space cell nobody pressed is still a character of the map left untyped, and seals a
            // Miss alongside every other one (the seal loop in Update tests IsTypeable, which a
            // space cell is; only IsCountable excludes it). Since backlog 181 the cell has a fourth
            // way of being resolved, a wrong LETTER typed into it, and that one takes the zeroed
            // delta too, for the reason the block above states: a typo is priced at what a correct
            // press on the same cell would have been priced at.
            //
            // Gated on SpaceTiming, the era switch, for the same reason ComboRestore is: a replay is
            // re-judged from scratch, so a run stored before backlog 148 has to be graded with the
            // spacebar back inside the timing challenge or its tier counts and its max_combo come
            // back as a ladder it was never played on. Live play never selects the other arm.

            // COMBO RESTORE (backlog 140, widened to the word skip by backlog 167), before anything
            // about this press is judged: if this is the cell a wrong keypress spoiled or a skip
            // abandoned, the run resumes at the streak that break cost plus everything earned since.
            // Placed here so the press below is scored, and announced, at the RESUMED streak. Not a
            // scoring-inert operation even for an inert retype: the streak belongs to the return, not
            // to the cell's judgement.
            // Keyed on the cell the press LANDED on and not on the caret, which is the same thing
            // everywhere but under AnyOrderWithinWord: the claim is a (line, cell, streak) triple
            // redeemed by typing THAT cell, so a typo fixed out of order has to restore its streak
            // exactly as one fixed in order does.
            resumeStreakIfThisRedeemsTheBreak(targetIndex);

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

                // The SAME award the first judgement took, re-derived: the stored delta through the
                // same ladder, and through the same backlog 210 cap, because the flag it reads is
                // set only before a cell is judged and never cleared. Announcing anything else here
                // would show a Great on a cell whose stored result is the capped Ok.
                type = TypeBeatResultMapping.AwardedTier(windowsFor(cell).Classify(delta), cell.HeldWrongBeforeJudged, CorrectionCredit);

                cell.State = CellState.Correct;
                cell.TypedChar = c;
                cell.JudgedDelta = delta;
            }
            else
            {
                // ALL scoring keypresses (correct + wrong) count in the accuracy denominator, forever.
                totalKeypresses++;

                // Correct char: always accepted; the clock decides the judgement.
                // Premature/Lagging still count as CORRECT keypresses (right char, wrong time).
                correctKeypresses++;

                // The clock classifies the press, then backlog 210's CORRECTION CAP decides what it
                // is awarded: a cell that held a wrong character before it was ever judged resolves
                // at min(that tier, Ok), so a corrected cell can never be worth what a clean one is
                // (see TypeBeatResultMapping.AwardedTier for why the cap sits on the TIER rather
                // than on the osu result). Applied here, above everything the tier decides, so the
                // engine's point ladder, the tier counts, the announced CharJudged and the cell's
                // osu result all follow the one decision and cannot say different things. The delta
                // itself is untouched, so the sync timeline and the sync readouts see the press the
                // player actually made.
                type = TypeBeatResultMapping.AwardedTier(windowsFor(cell).Classify(delta), cell.HeldWrongBeforeJudged, CorrectionCredit);
                int basePoints = SyncWindows.BasePoints(type);

                // Fletcher RUSH CAP, evaluated before the caret moves: does this press put the caret
                // more than FLETCHER_MAX_CHARS_AHEAD countable chars past the playhead?
                bool rushedPastCap = FletcherEnabled && rushesPastCap(cell, time);

                if (basePoints > 0)
                {
                    // Multiplier reads combo BEFORE the increment; capped at combo_cap => up to 2.0x.
                    points = (int)Math.Round(basePoints * (1 + Math.Min(combo, combo_cap) / (double)combo_cap));
                    score += points;
                }

                // Premature / Lagging (an OFF-TIME press: the right character, outside the outermost
                // Meh window) earns nothing above, and since backlog 199 that is the whole of what it
                // costs the score ladder. Whether it also costs the RUN is the OffTime era axis:
                //
                //   MehHit (live)      the press is a hit. It extends the combo like any other
                //                      accepted character, raises no ComboBroken, and leaves an
                //                      outstanding restorable claim alone, because only a BREAK
                //                      discards one. Its cell resolves as an osu Meh
                //                      (TypeBeatResultMapping.CellResult), so ACCURACY is the
                //                      punishment and osu's combo follows the engine's without any
                //                      hand-mirroring at the playfield seam.
                //   BreaksCombo        the pre-199 rule every stored row was played under: the run is
                //                      zeroed, the claim discarded, ComboBroken raised, and the cell
                //                      takes an osu Miss that carries the break.
                //
                // A space can never reach either arm under the live space rule: an untimed space is
                // judged on a zeroed delta and always takes the top tier (see SpaceTiming).
                bool offTimeBreak = basePoints <= 0 && !TypeBeatResultMapping.OffTimePressIsAHit(OffTime);

                if (offTimeBreak)
                {
                    combo = 0;
                    discardRestorableStreak();
                    raise(ComboBroken);
                }
                else if (rushedPastCap)
                {
                    // A combo penalty, not a block: the char lands and scores exactly as it would
                    // without the mod, but no combo may accumulate while the caret is out past the
                    // cap. ComboBroken therefore fires once, on the press that crosses the line,
                    // and re-arms the moment a press lands back inside it (combo starts building
                    // again, so the next excursion breaks it again).
                    //
                    // It reaches an off-time press too, under MehHit only, and that is the coherent
                    // reading of both rules rather than an accident: the cap measures where the CARET
                    // is, not how well the press was timed, so a press it would refuse combo for
                    // cannot earn combo merely by also being mistimed. Under BreaksCombo the arm
                    // above has already taken the break, exactly as it did pre-199.
                    bool hadCombo = combo > 0;
                    combo = 0;

                    if (hadCombo)
                    {
                        discardRestorableStreak();
                        raise(ComboBroken);
                    }
                }
                else
                {
                    combo++;
                    maxCombo = Math.Max(maxCombo, combo);

                    // The one press that can credit combo it also broke (backlog 243): the space that
                    // skipped the word, now being judged on the gap the skip parked the caret on. The
                    // combo is real and stands, but it belongs to the break, so the claim remembers it
                    // and the next break has to beat it to take the claim away.
                    if (skipLeftAClaimOutstanding)
                        creditTheClaimsOwnPress();
                }

                cell.State = CellState.Correct;
                cell.TypedChar = c;
                cell.JudgedDelta = delta;
                cell.FirstCorrectDelta = delta; // the one awarded judgement; retypes are inert.

                // SyncTimeline records every AWARDED correct-char judgement, incl. Premature/
                // Lagging, and since backlog 148 EXCEPT an untimed space. This series is offset and
                // sync ANALYSIS: a record of where the player's hands sit against the map. A space
                // no longer measures that. Its delta is 0 by RULE rather than by observation, so
                // keeping it adds a sample that saw nothing and pulls the mean toward zero, and
                // keeping the true delta instead would be worse still, because a player told the
                // spacebar does not matter will type it loosely on purpose and every word gap in
                // the map would then widen the spread. Fewer honest samples beat more polluted
                // ones, and the lyric characters (which the player is still timing) are the whole
                // of what the analysis is about.
                if (!untimedSpace)
                    syncTimeline.Add(new SyncSample(time, delta));

                counts[type]++;
            }

            // Log the press for the HUD's rolling WPM. Both branches above land the cell Correct, so a
            // scoring-inert retype still counts here: this is a record of keystrokes, not of cell states.
            pushRollingSample();

            // The cell the press landed on, which is what the display repaints and what a consumer
            // reading the judgement back has to be told: under AnyOrderWithinWord that is not always
            // where the caret is.
            int judgedCellIndex = targetIndex;

            // The caret is the LEFTMOST UNTYPED TYPEABLE CELL of the line, always. Without the mod
            // that is one step forward, because the cell just resolved is the one the caret was on.
            // With it, a press that landed AHEAD of the caret moves nothing (the leftmost untyped
            // cell is still untyped) and a press that landed ON the caret rolls the frontier over the
            // whole run of cells already typed out of order behind it, both of which the walk below
            // says in one line.
            if (!AnyOrderWithinWord)
                caretIndex++;

            advanceCaretToFrontier();

            raise(CharJudged, new CharJudgement(activeLineIndex, judgedCellIndex, type, delta, points, combo));
            rollForwardIfFinishedEarly(time);
            return true;
        }

        /// <summary>
        /// "Space to skip current word" (see <see cref="SpaceSkipsWord"/>): abandon the word the caret
        /// is inside and leave the caret on the word gap that follows it (or at the end of the line,
        /// for a word with no gap after it). Every typeable cell of that word nobody has typed
        /// ANYTHING into enters <see cref="CellState.Abandoned"/>, and the whole abandonment costs AT
        /// MOST ONE combo break no matter how many characters were given up, which is the same rule a
        /// sealed line's misses follow. There is always at least one such cell, the one the caret is
        /// sitting on, so the break always has a cell behind it.
        ///
        /// <para>Backlog 167 moved everything except that break out of this method and on to the two
        /// places a phantom cell can end up: the backspace that reclaims it, and the seal that
        /// resolves it as a miss. What is left here is the entry into the phantom state, the break,
        /// and the SNAPSHOT of the streak that break cost, taken against the first abandoned cell so
        /// that typing it later resumes the run through the backlog 140 machinery (see
        /// <see cref="ComboRestored"/>). That replaces the outright discard the skip used to do, and
        /// it is why the skip is one of the breaks that can be redeemed rather than one that takes
        /// ownership of the streak.</para>
        ///
        /// <para>Non-typeable cells inside the run are marked <see cref="CellState.AutoSkipped"/>,
        /// which is exactly what <see cref="autoSkipForward"/> would have done to them had the caret
        /// walked over them one press at a time; they are not typed, so they cannot be missed.</para>
        ///
        /// <para>Returns whether a redeemable claim is outstanding when it hands the press back, which
        /// is what tells <see cref="ProcessKey"/> that the combo the SAME press is about to earn on the
        /// word gap belongs to the break rather than to the player (backlog 243, see
        /// <see cref="creditTheClaimsOwnPress"/>). True for the claim this skip took AND for an older
        /// one it passively left alone, because the argument is about the press and not about which
        /// break wrote the claim.</para>
        /// </summary>
        private bool skipCurrentWord(double time)
        {
            var cells = lines[activeLineIndex].Cells;

            // The era switch (backlog 167), read once: the whole of the difference between today's
            // reclaimable skip and the immediate-miss one every score stored before it was played
            // under. Nothing else in the engine needs a switch, because the phantom state is what
            // every other part of the behaviour hangs off and the other arm never creates one.
            bool reclaimable = TypeBeatResultMapping.SkippedWordIsReclaimable(WordSkip);

            // The WHOLE word the caret is inside: the run of cells between the word gaps either side
            // of it (a word gap being a typeable SPACE cell), or the ends of the line. Deliberately
            // the whole word rather than just the tail from the caret onwards, and that choice is
            // what keeps this correct now that a cell can be resolved out of turn: without
            // AnyOrderWithinWord the two scans give up exactly the same cells (every typeable cell
            // behind the caret is already Correct or Wrong, resolving it being the only way the caret
            // got past it), and WITH it (backlog 231) the tail scan would abandon cells the player
            // has already typed while the caret still sits back at the first one they have not.
            // Scanning the word is what the feature promises either way, and it puts the weight on
            // the "already resolved" test below instead of on an argument about where the caret can be.
            int start = caretIndex;
            int end = caretIndex;

            while (start > 0 && !isWordGap(cells[start - 1]))
                start--;

            while (end < cells.Count && !isWordGap(cells[end]))
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

                // The phantom state (backlog 167) or, under the pre-167 era rule, the Miss the cell
                // used to take here. Nothing else in this method differs between the two arms: the
                // count and the announced judgement type follow from this, and every downstream
                // consequence of the phantom state is unreachable when no cell is ever in it.
                cell.State = reclaimable ? CellState.Abandoned : CellState.Missed;

                // The miss COUNT is the cell's resolution, so under the live rule it waits for the
                // seal exactly as the osu result does. Counting it now would say the character is
                // lost while the player can still walk back into it and type it.
                if (!reclaimable)
                    counts[JudgementType.Miss]++;

                abandoned.Add(i);
            }

            caretIndex = end;

            if (abandoned.Count == 0)
                return false;

            int brokenStreak = combo;

            combo = 0;

            // The break is IMMEDIATE under both rules, and under the live one it is also the only
            // thing the skip spends. Snapshotted against the FIRST abandoned cell, so re-typing that
            // cell resumes the run: the skip is a break the player can come back for, which is
            // exactly what a typo's break is (see ComboRestored). A skip discards an older cell's
            // claim the way any other intervening break would, but only if it broke a streak of its
            // own (backlog 176): a skip taken over a typo that has already zeroed the run leaves
            // that typo's claim redeemable, because the skip itself cost nothing.
            //
            // Under the pre-167 rule nothing is left to come back to, so the skip is a plain break
            // and ends any outstanding claim outright, exactly as it did then.
            if (reclaimable)
                snapshotRedeemableBreak(abandoned[0], brokenStreak);
            else
                discardRestorableStreak();

            raise(ComboBroken);

            // The break rides here rather than on a result, and this is the seam it rides on. The
            // ORIGINAL argument for announcing the abandoned cells immediately was that leaving them
            // to the seal would let osu's combo count on past a break the engine had already taken.
            // That argument survives backlog 167 intact, because the BREAK is still taken here; only
            // the RESULTS moved. What moved with them is the obligation: with no Miss result left to
            // carry the break, WordAbandoned carries it by hand (TypeBeatPlayfield.onWordAbandoned),
            // and the seal marks the deferred misses combo-neutral so they cannot take it a second
            // time (see AbandonSealed).
            if (reclaimable)
                raise(WordAbandoned, new AbandonedCells(activeLineIndex, abandoned));

            // Announce the cells AFTER the break so every judgement carries the post-break combo, and
            // one per cell so the stage repaints it. Under the live rule the judgement resolves
            // nothing (JudgementType.Abandoned maps to no osu result, exactly as a typed-through
            // wrong char does); under the pre-167 rule it IS the cell's Miss, taken now.
            foreach (int i in abandoned)
            {
                var type = reclaimable ? JudgementType.Abandoned : JudgementType.Miss;
                raise(CharJudged, new CharJudgement(activeLineIndex, i, type, time - cells[i].TargetTime, 0, combo));
            }

            return restorable is not null;
        }

        /// <summary>
        /// A combo break that is nobody's fixable typo happened, so the outstanding snapshot (if
        /// any) is discarded: the streak it was holding has been lost to THIS break, and correcting
        /// the older cell later cannot bring back a run that ended after it. Called at every
        /// <see cref="ComboBroken"/> seam except the two that can be walked back into and are
        /// therefore snapshotted instead: a wrong keypress, and (since backlog 167) a word skip.
        /// </summary>
        private void discardRestorableStreak() => restorable = null;

        /// <summary>
        /// Take the snapshot for a REDEEMABLE break (a wrong keypress, or a word skip): the streak
        /// it cost, against the cell the player has to come back to. The one write site for
        /// <see cref="restorable"/> other than the discards, so the two breaks that can be walked
        /// back into cannot drift apart.
        ///
        /// <para>A break takes ownership of the streak only if it HAS a streak to own (backlog 176).
        /// One landing at a combo of zero costs the player nothing, so it does not get to end an
        /// older cell's claim on a run that is still redeemable: the claim it would write is empty,
        /// and swapping a live claim for an empty one is a pure loss to a player who then goes back
        /// and fixes both cells. With NOTHING outstanding it still writes its own empty claim, so
        /// that redeeming it restores nothing, which is what
        /// <see cref="resumeStreakIfThisRedeemsTheBreak"/> has always done with a zero.</para>
        ///
        /// <para>"A streak to own" excludes the streak the OUTSTANDING claim's own press credited
        /// (backlog 243). One press can both take a claim and rebuild the run: a space struck inside
        /// a word abandons the rest of it and is then judged on the word gap it lands on, which puts
        /// the combo back to 1. A break on that very gap therefore broke a run of 1 rather than of 0,
        /// and under 176 alone that was enough to overwrite a claim hundreds deep with a worthless
        /// one. The 1 was not progress, it was the break's own press, so a break taking no more than
        /// the claim's own credit is passive exactly as a zero-streak break is, and SPENDS that credit
        /// on its way past: whatever the player rebuilds after this break is measured from zero, so
        /// the next break arms normally. See <see cref="SkipSpaceCredit"/> for the era arm.</para>
        ///
        /// <para>Under <see cref="ComboRestoreRule.Never"/> no snapshot exists at all, so the break
        /// is as final here as it is everywhere else.</para>
        /// </summary>
        private void snapshotRedeemableBreak(int cellIndex, int brokenStreak)
        {
            if (!TypeBeatResultMapping.FixRestoresTheComboBreak(ComboRestore))
            {
                restorable = null;
                return;
            }

            if (restorable is (int heldLine, int heldCell, int heldStreak, int ownPressCredit)
                && TypeBeatResultMapping.OnlyABreakWithAStreakTakesTheClaim(ComboClaim))
            {
                // The most this break can have taken and still be passive. Zero is backlog 176's
                // rule, and it is what the pre-243 arm keeps: the credit is READ through the era
                // switch rather than written through it, so the claim itself says the same thing
                // under both arms and only its consequence moves.
                int passive = TypeBeatResultMapping.TheClaimsOwnCreditIsNotAStreak(SkipSpaceCredit) ? ownPressCredit : 0;

                if (brokenStreak <= passive)
                {
                    restorable = (heldLine, heldCell, heldStreak, 0);
                    return;
                }
            }

            restorable = (activeLineIndex, cellIndex, brokenStreak, 0);
        }

        /// <summary>
        /// The press that just took (or passively kept) the outstanding claim has itself credited one
        /// combo: record that on the claim, so a break landing before the player has typed anything
        /// else takes nothing and leaves the claim alone (backlog 243, see
        /// <see cref="snapshotRedeemableBreak"/>).
        ///
        /// <para>Called from the one arm that can be reached by such a press: a word skip's space,
        /// falling through to be judged on the word gap the skip parked the caret on. It is deliberately
        /// keyed on the press having ACTUALLY credited combo rather than on the skip having happened,
        /// so a skip whose space earned nothing (an inert retype of an already judged gap, the rush cap
        /// refusing a caret out past its bound, or a word abandoned all the way to the end of a line,
        /// where there is no gap for the space to land on at all) records no credit and behaves exactly
        /// as backlog 176 left it.</para>
        /// </summary>
        private void creditTheClaimsOwnPress()
        {
            if (restorable is (int lineIndex, int cellIndex, int streak, _))
                restorable = (lineIndex, cellIndex, streak, 1);
        }

        /// <summary>
        /// Redeem the outstanding snapshot if the cell about to be typed correctly is the cell it
        /// was taken against: the run resumes at that streak plus everything earned since, which is
        /// exactly <c>combo + streak</c> because no break has landed in between (any that had would
        /// have discarded the snapshot). The claim is spent either way, so a second correct retype
        /// of the same cell restores nothing.
        ///
        /// <para>Two breaks can be waiting here, and the redemption is identical for both: the wrong
        /// keypress that spoiled the cell (backlog 140), and the word skip that abandoned it
        /// (backlog 167). In both cases the cell is the one the player has to come back to, so
        /// typing it is what says they came back.</para>
        /// </summary>
        private void resumeStreakIfThisRedeemsTheBreak(int cellIndex)
        {
            if (restorable is not (int lineIndex, int typoCellIndex, int streak, _))
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

            raise(ComboRestored, streak);
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
        /// RUSH FREEDOM (see <see cref="FletcherEnabled"/>): the moment a press finishes a line, the
        /// caret moves straight on to the next one instead of waiting for its activation cue. It is
        /// the KEYPRESS half of moving a finished caret on; the time-driven half, for a caret that
        /// became finished without a press of its own, is <see cref="snapForwardOnLineStart"/>.
        /// The finished line is left UNSEALED
        /// and seals on its own normal deadline (with nothing missed, since it is fully typed), so
        /// nothing about the song's timeline moves; only the player's position does. No-op on the last
        /// line, which keeps the default "line complete, wait for the song" behaviour that lets the
        /// key handler pass Space through to the skip overlay.
        ///
        /// <para>BOUNDED since backlog 218 (see <see cref="BoundedRush"/>): "the moment a press
        /// finishes a line" is now "the moment a press finishes a line, if that next line is within
        /// <see cref="FLETCHER_DRAG_GRACE_MS"/> of starting". Refused, the caret parks past the last
        /// cell and <see cref="snapForwardOnLineStart"/> makes the move for it when the bound opens.
        /// <paramref name="time"/> is the keypress's own time, the same value the press was judged
        /// on, so the bound is a pure function of (char, time) like everything else here and a replay
        /// reproduces it exactly.</para>
        /// </summary>
        private void rollForwardIfFinishedEarly(double time)
        {
            if (!FletcherEnabled || isFinished || activeLineIndex == -1)
                return;

            if (caretIndex < lines[activeLineIndex].Cells.Count)
                return;

            if (activeLineIndex + 1 >= lines.Count)
                return;

            if (!entryPermitted(activeLineIndex + 1, time))
                return;

            // Lines seal in order and the player never leaves a line except by finishing it or by a
            // drag cutoff (which advances nextSealIndex with them), so the next line is always unsealed.
            activeLineIndex++;
            caretIndex = 0;
            autoSkipForward();
            raise(LineActivated, activeLineIndex);
        }

        /// <summary>
        /// Windows for a cell's judgement tier (Line for estimated/low-confidence timing), at this
        /// engine's <see cref="WindowScale"/>. NOT static, which is the whole point of the scale: the
        /// tier is a property of the CELL and the scale is a property of the ENGINE, so the four
        /// sites that grade or measure a delta (both <see cref="SyncWindows.Classify"/> calls plus
        /// the two <see cref="SyncWindows.SyncQuality"/> ones behind
        /// <see cref="LiveSyncPercent"/> and <see cref="BuildResults"/>) all resolve it here and
        /// none of them can quietly miss the scale. Scaling the sync readouts is correct rather than
        /// incidental even now that they are display-only (backlog 251 cut SyncPercent out of the
        /// letter grade): a wider window really is easier to sit inside, so a readout that ignored
        /// the scale would be telling the player something untrue about their own play.
        /// </summary>
        private SyncWindows windowsFor(TypingCell cell)
        {
            int index = (int)cell.JudgeGranularity;

            // Mirrors SyncWindows.For's default arm: an unrecognised granularity is judged at Line.
            return (uint)index < (uint)windowsByGranularity.Length
                ? windowsByGranularity[index]
                : windowsByGranularity[(int)TimingGranularity.Line];
        }

        /// <summary>
        /// The delta a press on cell <paramref name="cellIndex"/> is judged, stored and announced
        /// on. Classic rule: time minus the cell's point target. Under <see cref="SyllableTiming"/>
        /// a cell inside a syllable group is judged against the group's sung SPAN instead: 0
        /// anywhere inside [StartTime, EndTime] (edge-inclusive), the signed distance to the nearer
        /// edge outside it (negative early, positive late), so the same asymmetric
        /// <see cref="SyncWindows.Classify"/> ladder grades distance from the syllable's edge. A
        /// cell in no group keeps the point delta under either rule, and that fallback is what gives
        /// a stylised word its classic per-character judgement (backlog 178 leaves such a token
        /// ungrouped rather than adding a second rule here): space cells, lines without groups, and
        /// the cells of an unsyllabifiable token all land in the same arm.
        ///
        /// <para>Under <see cref="CharTimedStretch"/> a third kind of cell lands there too, and it
        /// is the only one that IS in a group: a STRETCH cell
        /// (<see cref="TypingLine.IsCharTimedStretch"/>, a freestyle slot or a cell of a run of
        /// three or more identical characters inside one syllable), whose span would otherwise pay
        /// a whole mashed run a delta of zero. Everything else keeps the span rule, so this narrows
        /// backlog 179 rather than replacing it.</para>
        ///
        /// <para>Under <see cref="FirstCharTiming"/> the group's FIRST cell narrows again, to the
        /// signed distance from the span's start (backlog 247): pacing a syllable out beats bursting
        /// it at the window's edge, and the early side is byte-identical to the span rule since a
        /// press before the start already judged on that distance. The stretch exclusion above wins
        /// for a stretch cell that opens a group, which stays on its own (stricter) point target.</para>
        /// </summary>
        private double judgedDeltaFor(TypingLine line, int cellIndex, double time)
        {
            if (SyllableTiming)
            {
                int syllable = line.SyllableIndexOf(cellIndex);

                if (syllable >= 0 && !(CharTimedStretch && line.IsCharTimedStretch(cellIndex)))
                {
                    var group = line.Syllables[syllable];

                    if (FirstCharTiming && cellIndex == group.StartCell)
                        return time - group.StartTime;

                    if (time < group.StartTime)
                        return time - group.StartTime;

                    if (time > group.EndTime)
                        return time - group.EndTime;

                    return 0;
                }
            }

            return time - line.Cells[cellIndex].TargetTime;
        }

        /// <summary>Rebuild the per-granularity ladders (and <see cref="Windows"/>) at the current scale.</summary>
        private void applyWindowScale()
        {
            foreach (var granularity in Enum.GetValues<TimingGranularity>())
                windowsByGranularity[(int)granularity] = SyncWindows.For(granularity).Scaled(windowScale);

            Windows = SyncWindows.For(Beatmap.Granularity).Scaled(windowScale);
        }

        /// <summary>
        /// Whether a cell's timing is part of the challenge, and so whether its delta means anything:
        /// every typeable cell EXCEPT a space, which backlog 148 judges on a zeroed delta. This is
        /// the sync readouts' filter, on both the numerator and the denominator. Identical in effect
        /// to <see cref="TypingCell.IsCountable"/> today, and deliberately not written as it: that
        /// property is the Fletcher rush cap's currency ("how much budget does pressing this spend"),
        /// and one answering the other by coincidence is not a reason to make either the definition
        /// of the other.
        ///
        /// <para>Under <see cref="SpaceTimingRule.Timed"/> a space IS timed, because it was graded on
        /// its real delta, so it belongs in both halves of the mean exactly as any other character
        /// does. The exemption and the readout it feeds move together or the engine would report a
        /// sync figure no client ever produced.</para>
        /// </summary>
        private bool isTimed(TypingCell cell)
            => cell.IsTypeable && (!TypeBeatResultMapping.SpacesAreUntimed(spaceTiming) || cell.Expected != ' ');

        /// <summary>Recount <see cref="totalTimedCells"/> under the current <see cref="SpaceTiming"/>.</summary>
        private void countTimedCells()
        {
            totalTimedCells = 0;

            foreach (var line in lines)
            {
                foreach (var cell in line.Cells)
                {
                    if (isTimed(cell))
                        totalTimedCells++;
                }
            }
        }

        /// <summary>
        /// Erase the most recent typed cell within the active line, stepping back transparently
        /// over AutoSkipped punctuation (which is un-skipped so retyping re-marks it) and, since
        /// backlog 167, over the ABANDONED cells of a skipped word (which go back to
        /// <see cref="CellState.Untyped"/> for the same reason: retyping them re-earns them).
        /// Returns false if nothing to erase. The erased keypress stays in the accuracy counts.
        ///
        /// <para>Both step-overs are transparent because neither cell holds anything the player put
        /// there, so neither is an erase. That is what makes ONE press re-enter a skipped word and
        /// land on the last character actually typed, however many characters were given up.</para>
        ///
        /// <para>The one case that does not erase BEHIND the caret is a typo the caret is parked ON,
        /// which only <see cref="StrictSpaces"/> can produce (backlog 184): that cell is cleared in
        /// place and the caret does not move, because the gap it sits on is still owed its space.</para>
        ///
        /// <para>UNCHANGED by <see cref="AnyOrderWithinWord"/> (backlog 231), deliberately. "The most
        /// recently typed cell" and "the nearest typed cell behind the caret" are the same cell
        /// without that mod and can differ under it, and this stays the NEAREST ONE BEHIND: it is the
        /// character the player is looking at, the caret is still the leftmost cell nobody has typed,
        /// and erasing a cell somewhere off to the right because it happened to be typed last would
        /// be the one place in the game where backspace did not take back what is in front of it. It
        /// is also rarely felt, because every cell behind the frontier caret is Correct or Wrong
        /// whichever order they were typed in.</para>
        /// </summary>
        public bool ProcessBackspace()
        {
            if (isFinished || activeLineIndex == -1)
                return false;

            var cells = lines[activeLineIndex].Cells;

            // A typo the caret is PARKED ON (backlog 184, see StrictSpaces): cleared where it sits,
            // not erased from behind. The gap is still owed its space, so the caret has no business
            // retreating into the perfectly good word in front of it, and the character the player
            // wants back is the one they are looking at. One press, one cell, caret unmoved.
            //
            // Keyed on the STATE rather than on the era flags, and the two are the same thing here:
            // only the StrictSpaces + SpaceSkipsWord arm ever leaves the caret sitting on a Wrong
            // cell, because everywhere else resolving a cell is how the caret got past it. A replay
            // re-deriving with flags bit 4 clear therefore cannot reach this branch.
            if (caretIndex < cells.Count && cells[caretIndex].State == CellState.Wrong)
            {
                var parked = cells[caretIndex];

                parked.State = CellState.Untyped;
                parked.TypedChar = null;
                parked.JudgedDelta = null;

                raise(TypoErased);
                return true;
            }

            // Find the nearest cell behind the caret holding something the player typed, stepping
            // over the two transparent states (scan first, mutate after).
            int target = caretIndex - 1;

            while (target >= 0 && (cells[target].State == CellState.AutoSkipped || cells[target].State == CellState.Abandoned))
                target--;

            // Which of the stepped-over cells are abandoned ones being RECLAIMED, still without
            // mutating: the answer decides whether a press with nothing typed behind it did anything
            // at all.
            List<int>? reclaimed = null;

            for (int i = target + 1; i < caretIndex; i++)
            {
                if (cells[i].State == CellState.Abandoned)
                    (reclaimed ??= new List<int>()).Add(i);
            }

            if (target < 0 && reclaimed == null)
                return false;

            // Un-skip the punctuation and re-open the abandoned cells we stepped back over.
            for (int i = target + 1; i < caretIndex; i++)
            {
                if (cells[i].State == CellState.AutoSkipped || cells[i].State == CellState.Abandoned)
                    cells[i].State = CellState.Untyped;
            }

            if (target < 0)
            {
                // Nothing typed is left behind the caret. Ordinarily that is "nothing to erase", but
                // a word skipped at the very start of a line leaves phantom cells and no keypress
                // before them, and refusing here would make that one word the only unreclaimable one
                // on the map. The reclaim IS the state change, so the press did something: put the
                // caret back at the head of the word it just re-opened. (Non-null by the guard
                // above: with nothing typed behind the caret, a reclaim is the only thing left.)
                caretIndex = 0;
                autoSkipForward();
                raise(AbandonReclaimed, new AbandonedCells(activeLineIndex, reclaimed!));
                return true;
            }

            var cell = cells[target];

            // Read BEFORE the cell is cleared: a wrong character is being taken back, which is the
            // one erase anything downstream has to hear about (backlog 166, see TypoErased).
            bool erasedTypo = cell.State == CellState.Wrong;

            cell.State = CellState.Untyped;
            cell.TypedChar = null;
            cell.JudgedDelta = null;

            caretIndex = target;

            // Announced with the engine already settled, like every other event here, and silent
            // during a rebuild for the same reason every other one is (backlog 165): a backwards
            // seek re-walks the whole prefix, so a raw invoke here would refund a drain per
            // backspace in the run while the matching drains, riding on CharJudged, stayed silent.
            if (reclaimed != null)
                raise(AbandonReclaimed, new AbandonedCells(activeLineIndex, reclaimed));

            if (erasedTypo)
                raise(TypoErased);

            return true;
        }

        /// <summary>
        /// A WORD GAP: the typeable SPACE cell that separates two words. The one boundary both
        /// word-level queries below are written against, and the same test
        /// <see cref="skipCurrentWord"/> scans a word with, so "word" means one thing in this file.
        /// A non-typeable cell (punctuation the default stream kept, and every mark under the
        /// Literate mod) is NOT a boundary: it rides inside the word it is attached to.
        /// </summary>
        private static bool isWordGap(TypingCell cell) => cell.IsTypeable && cell.Expected == ' ';

        /// <summary>
        /// ANY ORDER WITHIN A WORD (see <see cref="AnyOrderWithinWord"/>): the index of the first
        /// cell of the word the caret is inside that <paramref name="c"/> can be typed into, or null
        /// when there is none. The word is scanned with the two-sided <see cref="isWordGap"/> walk
        /// <see cref="skipCurrentWord"/> uses, so "word" still means exactly one thing in this file:
        /// punctuation the stream kept rides INSIDE the word, and only a typeable space ends it.
        ///
        /// <para>ASCENDING, and that is a contract rather than a detail: the answer is then a pure
        /// function of the cells' states and the pressed char, so a stored Dyslexia run re-derives
        /// keystroke for keystroke from the (char, time) pairs a replay holds and nothing else.</para>
        ///
        /// <para>Only an UNTYPED typeable cell is a candidate. A Correct or Wrong one is a cell the
        /// player is finished with (a Wrong one is corrected by backspacing back to it, which stays
        /// the one route into a spoiled cell), an Abandoned one is reclaimed by the backspace its own
        /// feature promises, and a non-typeable one is the auto-skip's business. A FREESTYLE slot is
        /// excluded for the reason given on <see cref="AnyOrderWithinWord"/>: it matches every key
        /// but space, so offering it here would swallow the first press of the word and starve every
        /// exact match in it.</para>
        ///
        /// <para>The match is the SAME test the caret cell is matched with in
        /// <see cref="ProcessKey"/>, <see cref="CaseSensitive"/> included, so a wrong-case letter
        /// satisfies nothing under the Literate mod whether it is typed in order or out of it.</para>
        /// </summary>
        private int? matchWithinWord(IReadOnlyList<TypingCell> cells, char c)
        {
            int start = caretIndex;
            int end = caretIndex;

            while (start > 0 && !isWordGap(cells[start - 1]))
                start--;

            while (end < cells.Count && !isWordGap(cells[end]))
                end++;

            for (int i = start; i < end; i++)
            {
                var cell = cells[i];

                if (!cell.IsTypeable || cell.IsFreestyle || cell.State != CellState.Untyped)
                    continue;

                if (CaseSensitive ? c == cell.Expected : Typeability.Fold(c) == Typeability.Fold(cell.Expected))
                    return i;
            }

            return null;
        }

        /// <summary>
        /// Where a CTRL+BACKSPACE (backlog 182, the typing-site "erase the previous word" gesture)
        /// should leave the caret: a PURE QUERY, mutating nothing. The caller composes the gesture
        /// out of ordinary <see cref="ProcessBackspace"/> calls
        /// (<c>while (CaretIndex &gt; target &amp;&amp; ProcessBackspace()) record();</c>), which is
        /// what keeps the whole gesture inside the existing replay vocabulary: a stored run holds the
        /// same run of backspace frames the live engine consumed, and nothing here has to be
        /// re-derived by a replay at all.
        ///
        /// <para>The rule is the one every typing site implements. Walk back over the word GAPS
        /// immediately behind the caret, then over the word behind them, and stop at that word's
        /// first cell. So a caret sitting mid-word erases back to the start of the word it is inside,
        /// and a caret sitting at the head of a word (the gap immediately behind it) erases that gap
        /// AND the whole word before it. At the head of the line the answer is
        /// <see cref="CaretIndex"/> itself, which makes the composed gesture a no-op: it never calls
        /// the engine and therefore never records anything.</para>
        ///
        /// <para>The target is a FLOOR, not a promise: a single <see cref="ProcessBackspace"/> steps
        /// transparently back over auto-skipped and abandoned cells, so one press over a word that
        /// was entirely given up to a word skip can land the caret further back than this, exactly as
        /// a plain backspace there would. That is the existing reclaim behaviour and is deliberately
        /// not fought here.</para>
        ///
        /// <para>Answers <see cref="CaretIndex"/> unchanged when no line is active or the run has
        /// finished, so the caller needs no second guard.</para>
        /// </summary>
        public int WordBackspaceTarget
        {
            get
            {
                if (isFinished || activeLineIndex == -1)
                    return caretIndex;

                var cells = lines[activeLineIndex].Cells;
                int target = Math.Min(caretIndex, cells.Count);

                // The gaps directly behind the caret (normally one; a map never authors two in a
                // row, and the loop costs nothing for being written to survive one that did).
                while (target > 0 && isWordGap(cells[target - 1]))
                    target--;

                // Then the word they follow, back to the gap that opens it or to the line's head.
                while (target > 0 && !isWordGap(cells[target - 1]))
                    target--;

                return target;
            }
        }

        /// <summary>
        /// Where a CTRL+A (backlog 182, "select back to the mistake I have to retype") should put the
        /// start of its selection: the first cell of the run holding the EARLIEST unfixed MISTAKE
        /// behind the caret, or -1 when there is no mistake behind the caret at all (the gesture is
        /// then a no-op). The selection itself is the half-open range [this, <see cref="CaretIndex"/>),
        /// and it is pure UI state: nothing in the engine knows it exists. Consuming it is composed,
        /// like the gesture above, out of ordinary <see cref="ProcessBackspace"/> calls back to this
        /// index plus at most one <see cref="ProcessKey"/>, so a replay stores exactly the engine
        /// calls that were made.
        ///
        /// <para>A MISTAKE is a cell in one of two states, and they are the two ways a cell behind the
        /// caret can still be owed something. <see cref="CellState.Wrong"/> is a wrong character typed
        /// through and not yet backspaced away. <see cref="CellState.Abandoned"/> is a character a
        /// word skip gave up (backlog 167, see <see cref="SpaceSkipsWord"/>), which since backlog 244
        /// anchors a selection exactly as a typo does: an abandoned cell is precisely a cell the
        /// player still has to type, the reclaim already runs through
        /// <see cref="ProcessBackspace"/>'s transparent step-over, and re-typing the cell REDEEMS the
        /// combo claim the skip took against it (backlog 140's machinery, and backlog 243's fix to
        /// who owns that claim). Without this the one mistake the game hands a player in a single
        /// keystroke was the one mistake the one-keystroke correction refused to reach.</para>
        ///
        /// <para>An abandoned cell is never itself a word GAP: <see cref="skipCurrentWord"/> scans
        /// strictly between the gaps either side of the caret, so the gap-anchoring case below is
        /// reachable only by a typo. The two states share the scan anyway, because "the first cell I
        /// must retype to fix this" is one rule and stating it twice is how the two drift apart.</para>
        ///
        /// <para>The scan takes the EARLIEST mistake of either kind on the line, so the selection
        /// covers every unfixed one behind the caret rather than only the most recent (backlog 184).
        /// An earlier ABANDONED cell therefore wins over a later typo just as an earlier typo wins
        /// over a later abandoned one: the two are one ordered list, not two ranked kinds.
        /// The gesture is "fix my mistakes", and it is one keystroke: offering the shortest retype
        /// would leave a player with two spoiled words pressing it, retyping, pressing it again, and
        /// having no way to see from the caret how many rounds are left. Retyping the cells in between
        /// costs nothing, since a correct cell re-typed is scoring-inert.</para>
        ///
        /// <para>WHICH run the mistake's cell opens has two cases, and they are the same rule stated
        /// twice: the selection starts at the first cell the player must retype to fix it. For an
        /// ordinary lyric character (a typo or an abandoned cell alike) that is its WORD's first cell
        /// (walk back to the gap before it), which for a skipped word is its head: the mass backspace
        /// the caller composes reclaims the abandoned tail on its way past, exactly as a plain
        /// backspace there does. For a WORD GAP holding a typo (possible since backlog 181, see
        /// <see cref="WrongInputOnWordGaps"/>) the gap IS the cell to retype and it belongs to no
        /// word, so the selection starts on the gap itself; walking back from it would swallow the
        /// perfectly good word in front of it for nothing.</para>
        ///
        /// <para>The answer is never equal to <see cref="CaretIndex"/> when it is non-negative: the
        /// scan is over [0, <see cref="CaretIndex"/>), so a selection always covers at least one cell.
        /// The one typo that can sit AT the caret, the gap a <see cref="StrictSpaces"/> park is
        /// holding, is deliberately outside that range: it needs no selection, being one backspace
        /// away under the same rule that parked it. An abandoned cell can never sit at or ahead of
        /// the caret at all, since the skip that made it left the caret past the whole word.</para>
        /// </summary>
        public int RetypeSelectionAnchor
        {
            get
            {
                if (isFinished || activeLineIndex == -1)
                    return -1;

                var cells = lines[activeLineIndex].Cells;
                int limit = Math.Min(caretIndex, cells.Count);
                int mistake = -1;

                for (int i = 0; i < limit; i++)
                {
                    // The two unfixed states, taken in one pass so the earliest of EITHER kind wins
                    // (backlog 244 added the abandoned one).
                    if (cells[i].State == CellState.Wrong || cells[i].State == CellState.Abandoned)
                    {
                        mistake = i;
                        break;
                    }
                }

                if (mistake < 0)
                    return -1;

                if (isWordGap(cells[mistake]))
                    return mistake;

                int anchor = mistake;

                while (anchor > 0 && !isWordGap(cells[anchor - 1]))
                    anchor--;

                return anchor;
            }
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
            // SyncPercent over every TIMED cell: finally-Correct cells contribute SyncQuality(final
            // correct delta); everything else (Missed / Wrong / unresolved) is 0. SPACE cells are
            // out of both the sum and the divisor since backlog 148, for the reason on
            // LiveSyncPercent: their delta is zeroed by rule, so leaving them in would pay a full
            // quality per word gap for nothing and inflate the readout (a map runs roughly one space
            // in six typeable cells, which used to be enough to carry a 90 to an S back when this
            // figure gated the letter grade; backlog 251 cut that tie, and the exclusion outlives it
            // because a readout nobody is graded on still has to be true).
            double qualitySum = 0;

            foreach (var line in lines)
            {
                foreach (var cell in line.Cells)
                {
                    if (isTimed(cell) && cell.State == CellState.Correct && cell.JudgedDelta is double d)
                        qualitySum += windowsFor(cell).SyncQuality(d);
                }
            }

            double syncPercent = totalTimedCells == 0 ? 100 : 100 * qualitySum / totalTimedCells;

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
