// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.Difficulty;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>Prices a score from its current difficulty attributes, including local estimates
    /// for unranked mods. The difficulty calculator supplies the played input stream's SR,
    /// difficult-character count and duration; ranked eligibility is handled separately.</summary>
    public class TypeBeatPerformanceCalculator : PerformanceCalculator
    {
        public TypeBeatPerformanceCalculator(Ruleset ruleset)
            : base(ruleset)
        {
        }

        protected override PerformanceAttributes CreatePerformanceAttributes(ScoreInfo score, DifficultyAttributes attributes)
        {
            double stars = PerformancePointsDisplay.RateFor(score.Mods) == null
                           || !attributes.CanCalculatePerformance
                           || !PolyglotRatingSupport.SupportsDeclaredLanguage(score.BeatmapInfo?.Metadata.Language ?? Game.Beatmaps.BeatmapLanguage.Unspecified, score.Mods) ? 0 : attributes.StarRating;

            // The difficult-character count rides on the attributes because only the difficulty pass
            // can see the map's lyric lines; a stack that is not the ruleset's own (a test harness
            // handing base attributes in) has none, and 0 prices every miss as a wiped cleanliness
            // term rather than silently measuring against the cell count.
            var counts = PerformancePoints.CountNotes(score)
                with { DifficultCharacters = attributes is TypeBeatDifficultyAttributes typed ? typed.DifficultCharacters : 0 };

            return new PerformanceAttributes
            {
                Total = PerformancePoints.ForPlay(stars, counts, score.Accuracy, score.MaxCombo, score.Mods,
                    attributes is TypeBeatDifficultyAttributes durationAttributes ? durationAttributes.PlayedDurationSeconds : double.PositiveInfinity,
                    attributes.MaxCombo > 0 && score.BeatmapInfo?.Metadata.Language == BeatmapLanguage.Japanese && score.Mods.OfType<TypeBeatModPolyglot>().Any()
                        ? attributes.MaxCombo : null),
            };
        }
    }
}
