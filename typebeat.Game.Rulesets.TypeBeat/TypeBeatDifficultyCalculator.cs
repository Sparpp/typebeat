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
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat
{
    /// <summary>
    /// Star rating from <see cref="LyricDifficulty"/>: a duration-weighted soft maximum over
    /// per-word typing strain (see sr-formula-v1.md). Rate-adjusting mods (DoubleTime/Nightcore/
    /// HalfTime) feed their combined clock rate in, so a faster clock raises the rating, and the
    /// LITERATE mod feeds in the cell stream it converts the map to, so its extra punctuation cells
    /// move the rating as well (backlog 144).
    ///
    /// <para>The Literate flag is read off the MOD STACK rather than off the beatmap, deliberately:
    /// the mod stamps <see cref="TypeBeatHitObject.Literate"/> on the line objects so the nested
    /// scoring objects flatten correctly, but the <see cref="LyricLine"/> underneath is untouched
    /// (it always holds the authored text), so the flag is the only thing that says which stream to
    /// rate. Routed through <see cref="PerformancePoints.IsLiterate"/> so this and the pp path can
    /// never disagree about what a Literate stack is.</para>
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
                return new TypeBeatDifficultyAttributes(mods, 0, 1);

            // Combined clock rate of any rate-adjusting mods (DT 1.5x, HT 0.75x, ...); 1 with none.
            double rate = 1;

            foreach (var mod in mods.OfType<IApplicableToRate>())
                rate = mod.ApplyToRate(0, rate);

            var lines = objects.Select(h => h.Line).ToList();

            // The pp rate multiplier travels with the attributes because the performance calculator
            // gets no beatmap of its own (see TypeBeatDifficultyAttributes). It is exactly 1 for
            // everything but a base-rate Half Time stack, and only that branch costs extra passes.
            return new TypeBeatDifficultyAttributes(mods, LyricDifficulty.Compute(lines, rate, PerformancePoints.IsLiterate(mods)), PerformancePoints.RateMultiplier(lines, mods));
        }

        protected override IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, Mod[] mods) => Enumerable.Empty<DifficultyHitObject>();

        protected override Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods) => Array.Empty<Skill>();
    }
}
