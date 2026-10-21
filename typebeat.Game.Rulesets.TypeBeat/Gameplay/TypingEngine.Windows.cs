// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// TypingEngine, JUDGEMENT WINDOWS AND SPANS: the ladder a press is graded on (its scale, the Hard
// Rock era factor and the freestyle widening), the span a cell's delta is measured from, and which
// cells the sync readouts count as timed.

using System;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        /// <summary>
        /// The one ladder every cell of every map is judged on, at the current
        /// <see cref="WindowScale"/>. The map's timing granularity no longer selects a tier, so
        /// there is nothing per-cell to resolve here.
        /// </summary>
        public SyncWindows Windows { get; private set; }

        private SyncWindows freestyleWindows = SyncWindows.Default.Scaled(SyncWindows.FREESTYLE_WINDOW_SCALE);

        /// <summary>The cell's judgement and feedback ladder, including freestyle and mod/rate scales.</summary>
        public SyncWindows WindowsFor(TypingCell cell) => cell.IsFreestyle ? freestyleWindows : Windows;

        /// <summary>
        /// A line's hard deadline, preserving the full late window of every freestyle target.
        /// Completed lines can still seal at EndTime, and manual newlines can advance at once.
        /// </summary>
        public double EffectiveSealTime(TypingLine line)
        {
            double deadline = line.EndTime + line.SealGraceMs;
            if (!line.HasFreestyleCells)
                return deadline;

            foreach (var cell in line.Cells)
            {
                if (cell.IsFreestyle)
                    deadline = Math.Max(deadline, cell.TargetTime + WindowsFor(cell).MehLate + 1);
            }

            return deadline;
        }

        /// <summary>
        /// A MULTIPLICATIVE scale on every judgement window this engine grades against, 1 by default
        /// (the ladder exactly as <see cref="SyncWindows.Default"/> hands it over). 2 doubles every
        /// window, 0.5 halves it.
        ///
        /// <para>DELIBERATELY NOT AN "EASY" FLAG. The Easy mod sets it to 2, but a mod that scales
        /// the windows by the audio rate wants exactly the same lever, and the two must COMPOSE:
        /// each such mod multiplies its own factor in (<c>WindowScale *= factor</c>) rather than
        /// assigning, so the result does not depend on the order the mods are applied in.</para>
        ///
        /// <para>Set BEFORE the first keypress and left alone afterwards, like
        /// <see cref="ComboRestore"/>: judgements already made are never revisited, and the two sync
        /// readouts re-derive quality from stored deltas, so moving it mid-run would restate old
        /// presses under a ladder they were never graded on. Each scaling mod does it from its own
        /// <c>ApplyToEngine</c>, which both engine factories call while the engine is being built
        /// (<c>DrawableTypeBeatRuleset.createEngine</c> for a live play,
        /// <c>TypeBeatReplayScorer.CreateEngine</c> for a re-judged replay).</para>
        ///
        /// <para>NOT the whole of the scale the ladder is built at. Hard Rock's halving (backlog
        /// 150, retired live by backlog 264 and kept for the runs stored under it) is a SECOND,
        /// era-gated factor that this property deliberately does not carry: see
        /// <see cref="UnhalvedHardRockWindows"/> for why it cannot be a multiply here, and
        /// <c>applyWindowScale</c> for the product the two make.</para>
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

        /// <summary>
        /// Whether HARD ROCK is on this run's score. The mod fact, and the one thing a CONFIG frame
        /// cannot say for itself, exactly like <see cref="FlexibleCaretFromMod"/>: a stored run's
        /// mods travel on the score, not in the frames. Set by BOTH engine factories
        /// (<c>DrawableTypeBeatRuleset.createEngine</c> and
        /// <see cref="Scoring.TypeBeatReplayScorer"/>) from the mod list, and read only together
        /// with <see cref="UnhalvedHardRockWindows"/>, which supplies the era half of the same
        /// question.
        ///
        /// <para>Setting it rebuilds the ladders, so it composes with <see cref="WindowScale"/> in
        /// either order and needs no ordering rule of its own.</para>
        /// </summary>
        public bool HardRockFromMod
        {
            get => hardRockFromMod;
            set
            {
                hardRockFromMod = value;
                applyWindowScale();
            }
        }

        /// <summary>
        /// THE HARD ROCK WINDOW ERA (backlog 264, CONFIG frame bit 13): whether this run's Hard Rock
        /// left the judgement windows at their NORMAL width. Backlog 150 shipped HR as a halving of
        /// every window and backlog 180 gave it a second half, the revert to per-character point
        /// targets (<see cref="SyllableTiming"/> off); stacked, the two made the mod unplayable for
        /// nearly everyone, so backlog 264 dropped the halving and kept the revert. The rows already
        /// on the leaderboards were played under BOTH, and this bit is what tells the two apart.
        ///
        /// <para>FALSE by default, which is the stored era: every HR row on disk carries the bit
        /// clear and re-derives on the halved ladder its player was actually graded on. Live play
        /// sets it TRUE for every stack (<c>DrawableTypeBeatRuleset.createEngine</c>), the uniform
        /// convention bits 3, 4, 6, 8 and 10 to 12 follow, and it is inert without
        /// <see cref="HardRockFromMod"/> the way bits 6 and 8 are inert under HR.</para>
        ///
        /// <para>WHY AN ASSIGNMENT AND NOT A <c>WindowScale *= 0.5</c>. The CONFIG frame is re-fed on
        /// every backwards seek (<see cref="Rebuild"/> keeps everything set from outside and
        /// replays the frames), so a multiply reachable from <c>ReplayEngineFeed.Apply</c> would
        /// compound: two rewinds and the ladder is a quarter of what the run was played on. Both this
        /// setter and <see cref="HardRockFromMod"/>'s instead re-run <c>applyWindowScale</c>, which
        /// multiplies the ladder by an EFFECTIVE scale computed from scratch each time, so feeding
        /// the same header a hundred times lands on the same ladder as feeding it once.</para>
        ///
        /// <para>Judgement relevant in the strongest sense a window bit can be: it decides the TIER
        /// every press on the run is classified as, and therefore the accuracy, the score and the
        /// rank. It moves no caret and no cell state, so a re-derivation under the wrong arm is a
        /// differently valued account of the same fingers rather than a desynchronised one.</para>
        /// </summary>
        public bool UnhalvedHardRockWindows
        {
            get => unhalvedHardRockWindows;
            set
            {
                unhalvedHardRockWindows = value;
                applyWindowScale();
            }
        }

        /// <summary>
        /// The same total with SPACE cells taken out: the denominator of both sync readouts since
        /// backlog 148 took the spacebar out of the timing challenge. Kept as its own field rather
        /// than subtracted at the call sites, because <see cref="totalTypeableCells"/> is the
        /// COMPLETION denominator (every character of the map the player owes, spaces included) and
        /// the two must not be confused. Counted here and not derived from
        /// the <see cref="CountableStream"/>, whose length happens to equal it today: that stream is the
        /// Fletcher rush cap's currency, and tying the sync readout to it would make one a silent
        /// constraint on the other.
        ///
        /// <para>Not readonly, because <see cref="SpaceTiming"/> decides which cells are in it and a
        /// replay harness selects that after construction. It is still fixed before the first
        /// keypress, like every other era switch.</para>
        /// </summary>
        private int totalTimedCells;

        private double windowScale = 1;

        private bool hardRockFromMod;

        private bool unhalvedHardRockWindows;

        /// <summary>
        /// The delta a press on cell <paramref name="cellIndex"/> is judged, stored and announced
        /// on. Classic rule: time minus the cell's point target. Under <see cref="SyllableTiming"/>
        /// a cell inside a syllable group is judged against the group's sung SPAN instead: 0
        /// anywhere inside [StartTime, EndTime] (edge-inclusive), the signed distance to the nearer
        /// edge outside it (negative early, positive late), and the same
        /// <see cref="SyncWindows.Classify"/> ladder grades distance from the syllable's edge. A
        /// cell in no group keeps the point delta under either rule, and that fallback is what gives
        /// a stylised word its classic per-character judgement (backlog 178 leaves such a token
        /// ungrouped rather than adding a second rule here): space cells, lines without groups, and
        /// the cells of an unsyllabifiable token all land in the same arm.
        ///
        /// <para>Under <see cref="WordShelter"/> the span rule is drawn around the WORD instead of
        /// the syllable (<see cref="Mods.TypeBeatModEasy"/>): the arms below are otherwise
        /// identical, which is the point -- the narrowing flags keep narrowing, and only the span
        /// the delta is measured from changes. A cell in no word keeps the point delta, exactly as a
        /// cell in no group does.</para>
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
        ///
        /// <para>WHICH groups (and therefore which stretch runs) is <see cref="AuthoredSyllablesOnly"/>'s
        /// answer (backlog 363): the line's authored grouping on every live run, the natural one a
        /// stored replay without the era bit was played against.</para>
        ///
        /// <para>The arms pick the span and live in <see cref="JudgedSpanOf"/>; this is only the
        /// distance from it, so the highlight that reads the span and the grade cannot disagree.</para>
        /// </summary>
        private double judgedDeltaFor(TypingLine line, int cellIndex, double time)
        {
            var (start, end) = JudgedSpanOf(line, cellIndex);

            if (time < start)
                return time - start;

            if (time > end)
                return time - end;

            return 0;
        }

        /// <summary>
        /// The span <see cref="judgedDeltaFor"/> measures a press on cell
        /// <paramref name="cellIndex"/> from: a press inside [Start, End] (edge-inclusive) is judged
        /// on a delta of 0, one outside it on the signed distance to the nearer edge. Every arm
        /// <see cref="judgedDeltaFor"/> documents is decided here, against the engine's CURRENT
        /// flags and grouping, so this is the one answer to "when is this cell judged": a point
        /// target comes back as Start == End == the cell's target, and so does the
        /// <see cref="FirstCharTiming"/> first cell, on the span's start.
        ///
        /// <para>Public so the press-now highlight can ask the judge rather than re-derive it: a
        /// cell is lit exactly while <see cref="judgedDeltaFor"/> would grade it inside
        /// <see cref="WindowsFor"/>'s Great window, through every era flag a replay header flips
        /// mid-run.</para>
        /// </summary>
        public (double Start, double End) JudgedSpanOf(TypingLine line, int cellIndex)
        {
            SyllableGrouping grouping = GroupingOf(line);

            if (SyllableTiming && !(CharTimedStretch && grouping.IsCharTimedStretch(cellIndex)))
            {
                // The shelter's span: the WORD under Easy's arm, the syllable otherwise. Both arms
                // are the same lines below, deliberately: one rule, two spans.
                int startCell;
                double startTime, endTime;

                if (WordShelter)
                {
                    int word = line.WordIndexOf(cellIndex);

                    if (word < 0)
                        return pointOf(line, cellIndex);

                    WordGroup group = line.Words[word];
                    startCell = group.StartCell;
                    startTime = group.StartTime;
                    endTime = group.EndTime;
                }
                else
                {
                    int syllable = grouping.IndexOf(cellIndex);

                    if (syllable < 0)
                        return pointOf(line, cellIndex);

                    SyllableGroup group = grouping.Groups[syllable];
                    startCell = group.StartCell;
                    startTime = group.StartTime;
                    endTime = group.EndTime;
                }

                if (FirstCharTiming && cellIndex == startCell)
                    return (startTime, startTime);

                return (startTime, endTime);
            }

            return pointOf(line, cellIndex);
        }

        private static (double Start, double End) pointOf(TypingLine line, int cellIndex)
        {
            double target = line.Cells[cellIndex].TargetTime;
            return (target, target);
        }

        /// <summary>
        /// Rebuild the per-granularity ladders (and <see cref="Windows"/>) at the current EFFECTIVE
        /// scale, which is <see cref="WindowScale"/> times Hard Rock's halving on the runs that were
        /// played under it (<see cref="HardRockFromMod"/> and <see cref="UnhalvedHardRockWindows"/>).
        ///
        /// <para>Recomputed from scratch on every call rather than folded into
        /// <see cref="WindowScale"/> once, which is the whole point: all three inputs are settable
        /// after construction and the replay CONFIG frame is re-fed on every backwards seek, so a
        /// scale that accumulated would compound with each rewind. Here the answer depends only on
        /// the three current values, so any number of re-feeds is one re-feed.</para>
        /// </summary>
        private void applyWindowScale()
        {
            // The era constant lives on SyncWindows with the ladder it scales, so this file keeps
            // its zero-dependency shape and the 0.5 has exactly one definition (the Hard Rock mod
            // names it as its own WINDOW_SCALE).
            double scale = windowScale * (hardRockFromMod && !unhalvedHardRockWindows ? SyncWindows.HARD_ROCK_WINDOW_SCALE : 1);

            Windows = SyncWindows.Default.Scaled(scale);
            freestyleWindows = Windows.Scaled(SyncWindows.FREESTYLE_WINDOW_SCALE);
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
            => cell.IsTypeable && (!EraRules.SpacesAreUntimed(spaceTiming) || cell.Expected != ' ');

        /// <summary>Recount <see cref="totalTimedCells"/> under the current <see cref="SpaceTiming"/>.</summary>
        private void countTimedCells()
        {
            totalTimedCells = 0;

            foreach (var line in lines)
            {
                foreach (var cell in line.Cells)
                {
                    if (isTimed(cell))
                        totalTimedCells += JapaneseInputScoring && JapaneseCharacterTiming && cell.MinimumRomajiInputs > 0
                            ? cell.InputJudgements?.Count ?? cell.MinimumRomajiInputs : JapaneseCharacterTiming && cell.EndTargetTime.HasValue ? 2 : 1;
                }
            }
        }
    }
}
