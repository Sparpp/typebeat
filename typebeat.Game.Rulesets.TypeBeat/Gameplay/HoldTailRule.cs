// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// Grades a hold's release by how much of it was held: within the slack of the end is Great, three
    /// quarters Ok, half Meh, less a Miss. The share is of the span from the head's PRESS (or the hold's
    /// start, when the head was pressed before it) to the end, so a head pressed on its letter's target
    /// after the hold's authored start is not credited time it never held, and is not asked for time it
    /// could not hold either. Holding past the end is never penalised.
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

        /// <param name="start">The hold's authored start.</param>
        /// <param name="end">The hold's authored end.</param>
        /// <param name="pressed">When the head was pressed.</param>
        /// <param name="release">When the hold was let go.</param>
        public static JudgementType Grade(double start, double end, double pressed, double release)
        {
            if (release >= end - Slack(start, end))
                return JudgementType.Great;

            double from = Math.Max(start, pressed);
            double held = (release - from) / Math.Max(1, end - from);

            if (held >= OK_FRACTION)
                return JudgementType.Ok;

            return held >= MEH_FRACTION ? JudgementType.Meh : JudgementType.Miss;
        }
    }
}
