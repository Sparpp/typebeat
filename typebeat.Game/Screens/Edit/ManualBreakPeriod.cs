// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Beatmaps.Timing;

namespace typebeat.Game.Screens.Edit
{
    public class ManualBreakPeriod : BreakPeriod
    {
        public ManualBreakPeriod(double startTime, double endTime)
            : base(startTime, endTime)
        {
        }
    }
}
