// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Configuration.Tracking;

namespace typebeat.Game.Rulesets.Configuration
{
    public interface IRulesetConfigManager : ITrackableConfigManager, IDisposable
    {
        /// <summary>
        /// Puts every setting of this ruleset back on its default and saves (Maintenance, "Reset all
        /// settings to defaults"). Returns how many were reset.
        /// </summary>
        int ResetToDefaults();
    }
}
