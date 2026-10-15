// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// Grades a hold's release by how much of [start, end] was held: within the slack of the end is Great,
    /// three quarters Ok, half Meh, less a Miss. Holding past the end is never penalised.
    /// </summary>
    public static class HoldTailRule
    {
        public const double MIN_SLACK_MS = 80;
        public const double SLACK_FRACTION = 0.1;
        public const double OK_FRACTION = 0.75;
        public const double MEH_FRACTION = 0.5;

        /// <summary>How long before the end a release still counts as Great. Also the hand-off window.</summary>
        public static double Slack(double start, double end) => Math.Max(MIN_SLACK_MS, SLACK_FRACTION * Math.Max(1, end - start));

        /// <summary>
        /// Whether <paramref name="time"/> is in [end - slack, end), where the letter after the held one
        /// (<see cref="TypingCell.HandsOffFrom"/>) is judged dead on time.
        /// </summary>
        public static bool InHandoffWindow(double start, double end, double time) => time >= end - Slack(start, end) && time < end;

        public static JudgementType Grade(double start, double end, double release)
        {
            double length = Math.Max(1, end - start);
            double slack = Slack(start, end);

            if (release >= end - slack)
                return JudgementType.Great;

            double held = (release - start) / length;

            if (held >= OK_FRACTION)
                return JudgementType.Ok;

            return held >= MEH_FRACTION ? JudgementType.Meh : JudgementType.Miss;
        }
    }
}
