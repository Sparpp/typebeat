// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>Local pp estimates, shared by the HUD and results. Display eligibility is
    /// independent of score ranking: unranked mods can be priced without earning ranked pp.</summary>
    public static class PerformancePointsDisplay
    {
        /// <summary>Shown when a local estimate cannot be calculated.</summary>
        public const string INELIGIBLE_TEXT = "-";

        /// <summary>Standard eligible rates, plus local estimates for unranked mod stacks.</summary>
        public static double? RateFor(IReadOnlyList<Mod>? mods)
        {
            if (mods?.Any(m => !m.Ranked) != true)
                return PerformancePoints.EligibleRate(mods);
            double rate = 1;
            foreach (var mod in mods.OfType<IApplicableToRate>())
                rate = mod.ApplyToRate(0, rate);
            return double.IsFinite(rate) && rate > 0 ? rate : null;
        }

        public static double? StarRatingFor(IBeatmap? playableBeatmap, IReadOnlyList<Mod>? mods, bool requireRankedMap = true)
            => DifficultyFor(playableBeatmap, mods, requireRankedMap)?.Stars;

        /// <summary>Uses the same played input projection as the difficulty calculator.
        /// The live counter may also price maps which do not grant ranked pp.</summary>
        public static LyricDifficulty.ModelResult? DifficultyFor(IBeatmap? playableBeatmap, IReadOnlyList<Mod>? mods,
                                                                bool requireRankedMap = true, double playbackRate = 1)
        {
            if (playableBeatmap == null)
                return null;

            if (requireRankedMap && !playableBeatmap.BeatmapInfo.Status.GrantsPerformancePoints())
                return null;

            if (!CanCalculate(playableBeatmap, mods) || RateFor(mods) is not double rate)
                return null;

            try
            {
                return LyricDifficulty.ComputeDetail(PolyglotDifficulty.LinesFor(
                    playableBeatmap.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line), mods,
                    Mods.TypeBeatModPolyglot.LanguageFor(playableBeatmap)), rate * playbackRate, PerformancePoints.IsLiterate(mods), LyricDifficulty.Live, PerformancePoints.JudgementArmFor(mods));
            }
            catch (InvalidOperationException) when (playbackRate != 1)
            {
                // Very slow editor tempos can stretch ordinary maps past the model's 30-minute
                // limit. Keep Test Play usable and show a dash instead of failing the HUD's load.
                return null;
            }
        }

        public static bool CanCalculate(IBeatmap beatmap, IReadOnlyList<Mod>? mods)
            => PolyglotRatingSupport.Supports(beatmap.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line), mods,
                Mods.TypeBeatModPolyglot.LanguageFor(beatmap));

        /// <summary>Reprice a finished score when possible, otherwise retain its archived value.</summary>
        public static double? ForScore(ScoreInfo score, IBeatmap? playableBeatmap)
        {
            if (!PolyglotRatingSupport.SupportsDeclaredLanguage(score.BeatmapInfo?.Metadata.Language ?? BeatmapLanguage.Unspecified, score.Mods)
                || playableBeatmap != null && !CanCalculate(playableBeatmap, score.Mods))
                return null;
            if (Eligible(score) && DifficultyFor(playableBeatmap, score.Mods) is { } model)
            {
                var lines = playableBeatmap!.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line);
                var counts = PerformancePoints.CountNotes(score)
                    with { DifficultCharacters = model.DifficultCharacters };

                return PerformancePoints.ForPlay(model.Stars, counts, score.Accuracy, score.MaxCombo, score.Mods, PerformancePoints.PlayedDurationFor(lines, score.Mods), TypeBeatScoreProcessor.JapaneseComboFor(playableBeatmap));
            }

            return score.PP;
        }

        public static bool Eligible(ScoreInfo score) => new TypeBeatRuleset().ScoreCanDisplayPerformancePoints(score);

        public static string Format(double? pp)
            => pp is double value ? value.ToString("0", CultureInfo.InvariantCulture) : INELIGIBLE_TEXT;
    }
}
