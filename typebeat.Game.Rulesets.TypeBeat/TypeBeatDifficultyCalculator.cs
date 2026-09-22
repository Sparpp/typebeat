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
    /// Star rating from <see cref="LyricDifficulty"/>: the map's pace over sliding windows measured
    /// against human typing capability, summed over greedy non-overlapping FEATS (backlog 269).
    /// That class' own summary is the model description, and the prototype it is a literal port of
    /// is docs/sr-feats-model.js in the parent superrepo. Rate-adjusting mods (DoubleTime/Nightcore/
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

        /// <summary>
        /// THE DIFFICULTY VERSION, and it is not decoration: a star rating is STORED, not
        /// recalculated per view. <see cref="TypeBeat.Game.Database.BackgroundDataStoreProcessor"/>
        /// compares this against the ruleset's <c>LastAppliedDifficultyVersion</c> on startup and,
        /// when this is the higher of the two, stamps every stored rating back to -1 so the next
        /// pass recomputes it. Without a bump the database keeps serving the old numbers to song
        /// select (and to anything reading <c>BeatmapInfo.StarRating</c>) no matter what this
        /// calculator now returns.
        ///
        /// <para>BUMP THIS WHENEVER THE RATING MUST MOVE FOR EXISTING MAPS: a model change, a dial
        /// change in <see cref="ChunkedEndurance.Settings.Live"/>, or a fix to the demand field.
        /// Version 0 was the default this calculator inherited, so every stored rating in an
        /// existing install was written under it.</para>
        ///
        /// <para>v1 (2026-09-20): the endurance axis moved from the chunk grid to the OVERLAPPING
        /// window layout with the sandbox's current dials, which moves every map's rating; the pp
        /// side carries the same idea in <see cref="Scoring.PerformancePoints.VERSION"/>.</para>
        ///
        /// <para>v2 (2026-09-21): a PAUSE inside a word is a divider for the rating, priced from the
        /// stretches it is actually sung in (see <c>LyricDifficulty.BuildWords</c>), where it used to
        /// price as one span with the rests folded in as free time. Only maps carrying rests move, but
        /// every one of them moves, so their stored numbers have to be re-derived.</para>
        ///
        /// <para>v3 (2026-09-22): the chunked axis's dials were brought up to the Star Rating
        /// Sandbox's ACTIVE settings snapshot - the anchor (11.5 to 11.1), the character floor (16 to
        /// 22) and the chunk length bonus/falloff/floor/scale plus the decay power, which the game had
        /// been shipping from the snapshot's BASELINE column. Every map's rating moves.</para>
        ///
        /// <para>v4 (2026-09-22): the character floor goes back DOWN, 22 to 16, on the sandbox's own
        /// dial. The floor decides which windows may stand as candidates, so dropping it admits
        /// shorter and lighter stretches; on the bundled catalogue 72 of 89 maps move and every one of
        /// them moves UP, the largest by 0.54 stars at the bottom of the range where the floor was
        /// doing the most work. The envelope model's mirror of the same dial
        /// (<c>LyricDifficulty.MinimumWindowChars</c>) moves with it, which moves the song select
        /// Target WPM figure but no rating.</para>
        /// </summary>
        public override int Version => 4;

        protected override DifficultyAttributes CreateDifficultyAttributes(IBeatmap beatmap, Mod[] mods, Skill[] skills)
        {
            var objects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            if (objects.Count == 0)
                return new DifficultyAttributes(mods, 0);

            // Combined clock rate of any rate-adjusting mods (DT 1.5x, HT 0.75x, ...); 1 with none.
            double rate = 1;

            foreach (var mod in mods.OfType<IApplicableToRate>())
                rate = mod.ApplyToRate(0, rate);

            var lines = objects.Select(h => h.Line).ToList();

            // The rating AND the map's difficult characters. The second half is why this returns a
            // TypeBeat subclass again: the pp formula's miss penalty is measured against the
            // envelope model's own N, and the performance calculator is handed a score and these
            // attributes rather than the lyric lines, so the count has to travel with the rating it
            // was computed under.
            var model = LyricDifficulty.ComputeDetail(lines, rate, PerformancePoints.IsLiterate(mods), LyricDifficulty.Live, PerformancePoints.JudgementArmFor(mods));

            return new TypeBeatDifficultyAttributes(mods, model.Stars, model.DifficultCharacters);
        }

        protected override IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, Mod[] mods) => Enumerable.Empty<DifficultyHitObject>();

        protected override Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods) => Array.Empty<Skill>();
    }
}
