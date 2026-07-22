// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Pure C# — no osu.Framework dependencies. All times are double milliseconds.
// Detects long purely-instrumental stretches between lyric lines and computes where a
// mid-song skip should land the player, mirroring the intro skip's landing point.

using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// One purely instrumental stretch between two lyric lines: from where the earlier line has
    /// sealed (input inert) to where the next line reopens typing (its <see cref="TypingLine.ActivationTime"/>).
    /// </summary>
    public readonly struct InstrumentalGap
    {
        /// <summary>The earlier line's seal — when input goes inert and the skip becomes available.</summary>
        public readonly double SealTime;

        /// <summary>The next line's activation — when typing reopens (end of the instrumental window).</summary>
        public readonly double ActivationTime;

        /// <summary>Where a skip should seek to: <see cref="ActivationTime"/> minus the intro run-up.</summary>
        public readonly double SkipTarget;

        public double Duration => ActivationTime - SealTime;

        public InstrumentalGap(double sealTime, double activationTime, double skipTarget)
        {
            SealTime = sealTime;
            ActivationTime = activationTime;
            SkipTarget = skipTarget;
        }
    }

    public static class InstrumentalGaps
    {
        /// <summary>Only instrumental stretches at least this long qualify for a skip.</summary>
        public const double MIN_GAP_MS = 10_000;

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
        /// A gap runs from the earlier line's seal (<see cref="TypingLine.EndTime"/> +
        /// <see cref="TypingLine.SealGraceMs"/>, the last instant it could still be typeable) to the
        /// next line's <see cref="TypingLine.ActivationTime"/>. The trailing outro after the last
        /// line is never a gap (there is no next line).
        /// </summary>
        public static IReadOnlyList<InstrumentalGap> Compute(IReadOnlyList<TypingLine> lines)
        {
            var gaps = new List<InstrumentalGap>();

            if (lines == null || lines.Count < 2)
                return gaps;

            for (int i = 0; i < lines.Count - 1; i++)
            {
                double sealTime = lines[i].EndTime + lines[i].SealGraceMs;
                double activation = lines[i + 1].ActivationTime;

                if (activation - sealTime >= MIN_GAP_MS)
                    gaps.Add(new InstrumentalGap(sealTime, activation, activation - SKIP_LEAD_MS));
            }

            return gaps;
        }
    }
}
