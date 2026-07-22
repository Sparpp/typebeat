// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Screens.Play
{
    /// <summary>
    /// A purely instrumental stretch of gameplay (no active/typeable content) long enough to be
    /// worth skipping past, exposed by a ruleset so <see cref="Player"/> can offer a mid-song skip
    /// that reuses the same <see cref="SkipOverlay"/> machinery as the intro skip.
    /// </summary>
    public readonly struct InstrumentalSkipSection
    {
        /// <summary>
        /// The time at which the skip button becomes available — the moment the previous content
        /// has ended and input has gone inert (mirrors the intro overlay appearing at load).
        /// </summary>
        public readonly double GapStartTime;

        /// <summary>
        /// The time to seek to when the skip is taken. Chosen so the player keeps the same run-up
        /// before the next content that the intro skip leaves before the first object.
        /// </summary>
        public readonly double SkipTargetTime;

        public InstrumentalSkipSection(double gapStartTime, double skipTargetTime)
        {
            GapStartTime = gapStartTime;
            SkipTargetTime = skipTargetTime;
        }
    }
}
