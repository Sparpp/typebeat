// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>The one loop every engine factory runs over its mod list (see <see cref="IApplicableToTypingEngine"/>).</summary>
    public static class TypingEngineMods
    {
        /// <summary>
        /// Apply every engine-applicable mod in <paramref name="mods"/> to <paramref name="engine"/>.
        /// <paramref name="scaleByRate"/> false skips the rate mods (<see cref="ModRateAdjust"/>),
        /// which is the replay scorer's pre-150 era, when the windows ignored the clock rate.
        /// </summary>
        public static void Apply(TypingEngine engine, IEnumerable<Mod> mods, bool scaleByRate = true)
        {
            foreach (var mod in mods.OfType<IApplicableToTypingEngine>())
            {
                if (!scaleByRate && mod is ModRateAdjust)
                    continue;

                mod.ApplyToEngine(engine);
            }
        }
    }
}
