// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring.Legacy;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// type!beat has no osu!stable ancestry, so no legacy (score V1) scores exist to convert —
    /// this simulator exists only because <see cref="ILegacyRuleset"/> requires one.
    /// The interface is implemented purely to claim online ruleset ID 0 (see
    /// <see cref="TypeBeatRuleset"/>); every call path into this class is gated on
    /// <c>ScoreInfo.IsLegacyScore</c>, which is never true for type!beat scores.
    /// </summary>
    public class TypeBeatLegacyScoreSimulator : ILegacyScoreSimulator
    {
        public LegacyScoreAttributes Simulate(IWorkingBeatmap workingBeatmap, IBeatmap playableBeatmap) => default;

        public double GetLegacyScoreMultiplier(IReadOnlyList<Mod> mods, LegacyBeatmapConversionDifficultyInfo difficulty) => 1;
    }
}
