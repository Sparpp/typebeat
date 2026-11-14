// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    internal readonly record struct HoldPlacementBounds(double Lower, double Upper);

    /// <summary>Editor geometry for a hold's own sung subdivision. Gameplay remains the final validator.</summary>
    internal static class HoldPlacement
    {
        /// <summary>Gameplay lets a hold touch only the cuts beside its letter, so the editor keeps this far clear of the others.</summary>
        private const double cut_margin_ms = 0.001;

        /// <summary>How far past its letter's target a hold end is kept, so rounding never puts it on the target.</summary>
        private const double hold_end_past_due_ms = 1;

        /// <summary>
        /// Where a hold on <paramref name="charIndex"/> may sit: inside its subdivision and stretch, clear of the other
        /// usable holds, and leaving <see cref="TypeBeatEditorOperations.MIN_SYLLABLE_MS"/> to the letters either side.
        /// </summary>
        internal static HoldPlacementBounds? GetBounds(TimedUnit unit, int charIndex)
        {
            if (charIndex < 0 || charIndex >= unit.Text.Length || !Typeability.IsCell(unit.Text[charIndex]))
                return null;

            var rests = HeldWord.RestsOf(unit);
            double lower = charIndex == 0 ? unit.StartTime : unit.StartTime + TypeBeatEditorOperations.MIN_SYLLABLE_MS;
            double upper = charIndex + 1 < unit.Text.Length ? unit.EndTime - TypeBeatEditorOperations.MIN_SYLLABLE_MS : unit.EndTime;

            foreach (var cut in HeldWord.CutsOf(unit, rests.Count > 0, Typeability.IsCell))
            {
                if (charIndex >= cut.Char)
                    lower = Math.Max(lower, cut.Time + (charIndex > cut.Char ? cut_margin_ms : 0));
                else
                    upper = Math.Min(upper, cut.Time - (charIndex + 1 < cut.Char ? cut_margin_ms : 0));
            }

            foreach (var rest in rests)
            {
                if (charIndex >= rest.SplitChar)
                    lower = Math.Max(lower, rest.EndTime);
                else
                    upper = Math.Min(upper, rest.StartTime);
            }

            foreach (var hold in HeldWord.UsableHolds(unit))
            {
                if (hold.Char < charIndex)
                    lower = Math.Max(lower, hold.EndTime);
                else if (hold.Char > charIndex)
                    upper = Math.Min(upper, hold.StartTime);
            }

            return upper >= lower ? new HoldPlacementBounds(lower, upper) : null;
        }

        /// <summary>
        /// A new hold on <paramref name="charIndex"/>: the conventional span (from its letter to its syllable's end, or
        /// halfway there), fitted into <see cref="GetBounds"/>. No press target or usable hold changes; null when it cannot fit.
        /// </summary>
        internal static WordHold? Create(TimedUnit unit, int charIndex)
        {
            if (unit.Objects.Holds.Any(h => h.Char == charIndex) || GetBounds(unit, charIndex) is not { } bounds)
                return null;

            var flat = TypeBeatEditorOperations.FlattenedWithoutHolds(unit);
            if (flat.Cells.Count < unit.Text.Length)
                return null;

            // A stylised spelling ("meee", "ohhh") has no syllable groups at all (see TypingLine's
            // buildSyllables), and those are the words a hold is most wanted on: the whole word is its
            // stretch (backlog 457).
            int groupIndex = flat.SyllableIndexOf(charIndex);
            int lastCell = Enumerable.Range(0, unit.Text.Length).Last(i => Typeability.IsCell(unit.Text[i]));
            bool endsStretch = groupIndex >= 0 ? charIndex == flat.Syllables[groupIndex].EndCellExclusive - 1 : charIndex == lastCell;
            double stretchEnd = groupIndex >= 0 ? flat.Syllables[groupIndex].EndTime : unit.EndTime;

            double due = HeldWord.LetterDueAt(unit, charIndex);
            double start = charIndex == 0 ? unit.StartTime : flat.Cells[charIndex].TargetTime;
            double end = endsStretch ? stretchEnd : start + (stretchEnd - start) / 2;

            if (!HeldWord.EndsAfterItsLetter(end, due))
                end = due + HeldWord.MIN_HOLD_MS;

            // Grow toward the available end before borrowing earlier time from the syllable.
            start = Math.Max(bounds.Lower, start);
            end = Math.Min(bounds.Upper, Math.Max(end, Math.Max(start + HeldWord.MIN_HOLD_MS, due + hold_end_past_due_ms)));
            start = Math.Max(bounds.Lower, Math.Min(start, end - HeldWord.MIN_HOLD_MS));

            var hold = new WordHold(charIndex, start, end);
            var holds = HeldWord.UsableHolds(unit).Append(hold).OrderBy(h => h.Char).ToArray();
            return HeldWord.UsableHolds(unit.WithObjects(unit.Objects with { Holds = holds })).Count == holds.Length ? hold : null;
        }

        /// <summary><paramref name="proposedTime"/> clamped to where an edge of hold <paramref name="holdIndex"/> may go, or null.</summary>
        internal static double? ClampEdge(TimedUnit unit, int holdIndex, bool moveStart, double proposedTime)
        {
            if (!double.IsFinite(proposedTime) || holdIndex < 0 || holdIndex >= unit.Objects.Holds.Count)
                return null;

            var hold = unit.Objects.Holds[holdIndex];
            if ((moveStart && hold.Char == 0) || GetBounds(unit, hold.Char) is not { } bounds)
                return null;

            double lower = moveStart ? bounds.Lower : Math.Max(hold.StartTime + HeldWord.MIN_HOLD_MS, HeldWord.LetterDueAt(unit, hold.Char) + hold_end_past_due_ms);
            double upper = moveStart ? hold.EndTime - HeldWord.MIN_HOLD_MS : bounds.Upper;
            return upper >= lower ? Math.Clamp(proposedTime, lower, upper) : null;
        }

        /// <summary><paramref name="hold"/> over a new span. A tick marks a moment in the song, so ticks keep their times; any the span no longer holds are dropped.</summary>
        internal static WordHold Respanned(WordHold hold, double start, double end)
        {
            var moved = hold with { StartTime = start, EndTime = end };
            return hold.Ticks.Count == 0 ? moved : moved with { Ticks = HoldTicks.UsableTicks(moved) };
        }

        /// <summary>
        /// <paramref name="unit"/>'s holds on <paramref name="resized"/>, the same word over a new span: a first-letter hold
        /// starts with the word, any other keeps its times trimmed inside the word (see <see cref="Respanned"/>). A hold the
        /// resize left whole, but whose letter the word's new spread moved past it, scales with the word rather than be lost.
        /// </summary>
        internal static WordHold[] Resized(TimedUnit unit, TimedUnit resized)
        {
            var old = unit.Objects.Holds;
            var holds = old.Select(h => trimmed(h, unit.Text.Length, resized.StartTime, resized.EndTime)).ToArray();
            var usable = HeldWord.UsableHolds(resized.WithObjects(resized.Objects with { Holds = holds }));

            if (usable.Count == holds.Length)
                return holds;

            var scaled = unit.Objects.Retimed(unit.StartTime, unit.EndTime, resized.StartTime, resized.EndTime).Holds;

            for (int i = 0; i < holds.Length; i++)
            {
                bool whole = holds[i].EndTime == old[i].EndTime && (old[i].Char == 0 || holds[i].StartTime == old[i].StartTime);
                if (whole && !usable.Contains(holds[i]))
                    holds[i] = scaled[i];
            }

            return holds;
        }

        private static WordHold trimmed(WordHold hold, int length, double wordStart, double wordEnd)
        {
            double start = hold.Char == 0 ? wordStart : Math.Max(hold.StartTime, wordStart + TypeBeatEditorOperations.MIN_SYLLABLE_MS);
            double end = Math.Min(hold.EndTime, hold.Char + 1 < length ? wordEnd - TypeBeatEditorOperations.MIN_SYLLABLE_MS : wordEnd);
            return Respanned(hold, start, end);
        }

        /// <summary>
        /// Whether every usable hold of <paramref name="before"/> is still usable in <paramref name="after"/> and crosses
        /// its <see cref="GetBounds"/> no further. Imported holds may already cross a cut gameplay exempts; an edit may keep that.
        /// </summary>
        internal static bool PreservesHolds(TimedUnit before, TimedUnit after)
        {
            var still = HeldWord.UsableHolds(after);
            foreach (var hold in HeldWord.UsableHolds(before))
            {
                if (!still.Contains(hold) || GetBounds(before, hold.Char) is not { } oldBounds || GetBounds(after, hold.Char) is not { } bounds)
                    return false;
                if (Math.Max(0, bounds.Lower - hold.StartTime) > Math.Max(0, oldBounds.Lower - hold.StartTime)
                    || Math.Max(0, hold.EndTime - bounds.Upper) > Math.Max(0, hold.EndTime - oldBounds.Upper))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Where divider <paramref name="boundaryIndex"/> may move without crossing a usable hold. A hold that already
        /// crosses it (see <see cref="PreservesHolds"/>) only stops it crossing further.
        /// </summary>
        internal static HoldPlacementBounds DividerBounds(TimedUnit unit, int boundaryIndex)
        {
            double lower = double.NegativeInfinity, upper = double.PositiveInfinity;
            var splits = SyllableSegments.SplitsFor(unit);

            if (boundaryIndex >= splits.Count)
                return new HoldPlacementBounds(lower, upper);

            // On a paused word the divider's cut is its stretch's own, matched by time.
            double time = unit.SyllableBoundaries[boundaryIndex];
            int cut = HeldWord.CutsOf(unit, HeldWord.RestsOf(unit).Count > 0, Typeability.IsCell)
                              .Where(c => c.Time == time).Select(c => c.Char)
                              .DefaultIfEmpty(splits[boundaryIndex]).Last();

            foreach (var hold in HeldWord.UsableHolds(unit))
            {
                if (hold.Char < cut)
                    lower = Math.Max(lower, Math.Min(time, hold.EndTime + (hold.Char + 1 == cut ? 0 : cut_margin_ms)));
                else
                    upper = Math.Min(upper, Math.Max(time, hold.StartTime - (hold.Char == cut ? 0 : cut_margin_ms)));
            }

            return new HoldPlacementBounds(lower, upper);
        }
    }
}
