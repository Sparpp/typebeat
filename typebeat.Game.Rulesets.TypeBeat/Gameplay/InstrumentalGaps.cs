// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Pure C# — no osu.Framework dependencies. All times are double milliseconds.
// Detects long purely-instrumental stretches between lyric lines and computes where a
// mid-song skip should land the player, mirroring the intro skip's landing point.
//
// REAL-DATA SHAPE (the invariant two prior attempts missed): the production decoder
// (TimingJsonLoader.BuildLines) makes line windows CONTIGUOUS — a non-last line's EndTime IS the
// next line's StartMs, and on aligner-produced maps the next line's first vocal sits at (or within
// a few hundred ms of) its StartTime, so its ActivationTime clamps to StartTime too. A long
// instrumental therefore lives INSIDE the previous line's window: the line stays active (complete,
// input-inert) from its last sung word until the next line's start. There is never a timeline hole
// between EndTime and the next StartTime, and "seal → activation" is always ~zero. Any skip window
// anchored on "EndTime + grace" is therefore empty or negative on every real map (verified against
// the installed "Immortal Flame" and "NEON RAIN" maps; see InstrumentalGapsRealMapTest).

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// One long purely instrumental stretch between two lyric lines: from shortly after the
    /// earlier line's vocals end (<see cref="TypingLine.SingEndTime"/>) to the next line's
    /// <see cref="TypingLine.ActivationTime"/>. For most of it the earlier line is still the
    /// engine's active line — complete and input-inert — because real line windows run all the
    /// way to the next line's start.
    /// </summary>
    public readonly struct InstrumentalGap
    {
        /// <summary>When the skip period opens: the earlier line's vocals have ended (plus a short
        /// settle so the overlay doesn't pop over the final word being typed).</summary>
        public readonly double GapStartTime;

        /// <summary>The next line's activation — when typing reopens (end of the instrumental window).</summary>
        public readonly double ActivationTime;

        /// <summary>Where a skip should seek to: <see cref="ActivationTime"/> minus the intro run-up.</summary>
        public readonly double SkipTarget;

        public double Duration => ActivationTime - GapStartTime;

        public InstrumentalGap(double gapStartTime, double activationTime, double skipTarget)
        {
            GapStartTime = gapStartTime;
            ActivationTime = activationTime;
            SkipTarget = skipTarget;
        }
    }

    public static class InstrumentalGaps
    {
        /// <summary>
        /// Only instrumental stretches at least this long qualify for a skip. Measured as the
        /// PERCEIVED stretch — the previous line's <see cref="TypingLine.SingEndTime"/> to the next
        /// line's <see cref="TypingLine.FirstVocalTime"/> ("sections defined by lack of lyric
        /// line"). Mechanical quantities (EndTime, seal grace, boundaries) play no part in
        /// qualification: on real maps they carry no information about the instrumental at all.
        /// </summary>
        public const double MIN_GAP_MS = 10_000;

        /// <summary>
        /// How long after the previous line's last sung moment the skip period opens. Covers a
        /// normally lagging finish of the final word so the overlay does not flash in over live
        /// typing. (Space cannot be stolen from typing regardless — the playfield only lets keys
        /// fall through to the overlay once the line is complete — this is purely visual timing.)
        /// </summary>
        public const double GAP_START_SETTLE_MS = 1_000;

        /// <summary>
        /// The skip period (gap start → skip target) must be at least this long to be usable;
        /// matches MasterGameplayClockContainer.MINIMUM_SKIP_TIME. A qualifying perceived gap
        /// squeezed below this is dropped rather than flashing an unusable overlay.
        /// </summary>
        public const double MIN_SKIP_WINDOW_MS = 1_000;

        /// <summary>
        /// How far before the next line's activation the skip lands the player. This reproduces the
        /// intro skip's landing point: the ruleset's gameplay start sits 2000 ms before the first
        /// object (DrawableRuleset.GameplayStartTime), and the intro skip seeks to that minus
        /// MasterGameplayClockContainer.MINIMUM_SKIP_TIME (1000 ms) — i.e. object time minus 3000 ms.
        /// Anchoring on ActivationTime (not the line boundary) preserves the full CUE_LEAD approach
        /// before the next word even when the next line's vocals sit late in its window.
        /// </summary>
        public const double SKIP_LEAD_MS = 3_000;

        /// <summary>
        /// The instrumental gaps between consecutive lines that qualify for a mid-song skip.
        /// Qualification is on the perceived stretch (previous <see cref="TypingLine.SingEndTime"/>
        /// → next <see cref="TypingLine.FirstVocalTime"/> ≥ <see cref="MIN_GAP_MS"/>). The skip
        /// period runs from the previous line's sung content ending (its last typeable target /
        /// SingEndTime, whichever is later, plus <see cref="GAP_START_SETTLE_MS"/>) to the skip
        /// target (<see cref="InstrumentalGap.ActivationTime"/> − <see cref="SKIP_LEAD_MS"/>), and must be usable
        /// (≥ <see cref="MIN_SKIP_WINDOW_MS"/>). The trailing outro after the last line is never a
        /// gap (there is no next line).
        /// </summary>
        public static IReadOnlyList<InstrumentalGap> Compute(IReadOnlyList<TypingLine> lines)
        {
            var gaps = new List<InstrumentalGap>();

            if (lines == null || lines.Count < 2)
                return gaps;

            for (int i = 0; i < lines.Count - 1; i++)
            {
                double perceived = lines[i + 1].FirstVocalTime - lines[i].SingEndTime;

                if (perceived < MIN_GAP_MS)
                    continue;

                // The last moment the previous line's content is genuinely being sung/typed. The
                // last typeable target normally coincides with SingEndTime, but weird data can put
                // it later (word times overrunning the reported line end) — take the later one.
                double sungEnd = lines[i].SingEndTime;

                var cells = lines[i].Cells;

                for (int c = cells.Count - 1; c >= 0; c--)
                {
                    if (cells[c].IsTypeable)
                    {
                        sungEnd = Math.Max(sungEnd, cells[c].TargetTime);
                        break;
                    }
                }

                double gapStart = sungEnd + GAP_START_SETTLE_MS;
                double activation = lines[i + 1].ActivationTime;
                double skipTarget = activation - SKIP_LEAD_MS;

                if (skipTarget - gapStart >= MIN_SKIP_WINDOW_MS)
                    gaps.Add(new InstrumentalGap(gapStart, activation, skipTarget));
            }

            return gaps;
        }
    }
}
