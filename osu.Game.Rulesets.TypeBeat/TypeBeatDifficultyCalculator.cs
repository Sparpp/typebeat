// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.TypeBeat.Beatmaps;
using osu.Game.Rulesets.TypeBeat.Objects;

namespace osu.Game.Rulesets.TypeBeat
{
    /// <summary>
    /// Star rating straight from the map's perfect-play typing pace: stars = WPM / 25,
    /// capped at 10 — a leisurely 50 WPM map sits at 2*, a demanding 150 WPM map at 6*.
    /// </summary>
    public class TypeBeatDifficultyCalculator : DifficultyCalculator
    {
        private const double wpm_per_star = 25;
        private const double max_stars = 10;

        public TypeBeatDifficultyCalculator(IRulesetInfo ruleset, IWorkingBeatmap beatmap)
            : base(ruleset, beatmap)
        {
        }

        protected override DifficultyAttributes CreateDifficultyAttributes(IBeatmap beatmap, Mod[] mods, Skill[] skills)
        {
            var objects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            if (objects.Count == 0)
                return new DifficultyAttributes(mods, 0);

            var pace = LyricPaceStatistics.Compute(objects.Select(h => h.Line), objects[0].Granularity);

            return new DifficultyAttributes(mods, Math.Min(max_stars, pace.AverageWpm / wpm_per_star));
        }

        protected override IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, Mod[] mods) => Enumerable.Empty<DifficultyHitObject>();

        protected override Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods) => Array.Empty<Skill>();
    }
}
