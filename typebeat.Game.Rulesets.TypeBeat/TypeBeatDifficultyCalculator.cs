// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Difficulty;
using typebeat.Game.Rulesets.Difficulty.Preprocessing;
using typebeat.Game.Rulesets.Difficulty.Skills;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat
{
    /// <summary>
    /// Star rating from <see cref="LyricDifficulty"/> — a duration-weighted soft maximum over
    /// per-word typing strain (see sr-formula-v1.md). Rate-adjusting mods (DoubleTime/Nightcore/
    /// HalfTime) feed their combined clock rate in, so a faster clock raises the rating.
    /// </summary>
    public class TypeBeatDifficultyCalculator : DifficultyCalculator
    {
        public TypeBeatDifficultyCalculator(IRulesetInfo ruleset, IWorkingBeatmap beatmap)
            : base(ruleset, beatmap)
        {
        }

        protected override DifficultyAttributes CreateDifficultyAttributes(IBeatmap beatmap, Mod[] mods, Skill[] skills)
        {
            var objects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            if (objects.Count == 0)
                return new DifficultyAttributes(mods, 0);

            // Combined clock rate of any rate-adjusting mods (DT 1.5x, HT 0.75x, ...); 1 with none.
            double rate = 1;

            foreach (var mod in mods.OfType<IApplicableToRate>())
                rate = mod.ApplyToRate(0, rate);

            return new DifficultyAttributes(mods, LyricDifficulty.Compute(objects.Select(h => h.Line), rate));
        }

        protected override IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, Mod[] mods) => Enumerable.Empty<DifficultyHitObject>();

        protected override Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods) => Array.Empty<Skill>();
    }
}
