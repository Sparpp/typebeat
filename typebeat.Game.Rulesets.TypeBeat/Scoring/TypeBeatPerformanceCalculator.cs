// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Difficulty;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// The ruleset's osu-native pp entry point: prices a finished score from the difficulty
    /// attributes the game already computed for it.
    ///
    /// <para>
    /// It exists so the SHARED results-screen components work for type!beat instead of falling back
    /// to a hardcoded 0: the score panel's pp readout
    /// (<see cref="Screens.Ranking.Expanded.Statistics.PerformanceStatistic"/>) and the performance
    /// breakdown chart both reach pp through <c>Ruleset.CreatePerformanceCalculator</c>, and both
    /// showed nothing meaningful while it returned null.
    /// </para>
    ///
    /// <para>
    /// THE ARITHMETIC IS NOT DUPLICATED: this is a thin adapter over
    /// <see cref="PerformancePoints"/>, the calculator that is pinned byte-for-byte against the
    /// server's. The one thing it contributes is the star rating, and it takes that from
    /// <see cref="DifficultyAttributes.StarRating"/>, which <see cref="TypeBeatDifficultyCalculator"/>
    /// already produces by running <see cref="LyricDifficulty"/> at the play's clock rate. For every
    /// play that can earn pp that is the same rating <see cref="PerformancePointsDisplay"/> computes
    /// and the same one the server stores, because all three are the same pass over the same lines
    /// at the same rate.
    /// </para>
    ///
    /// <para>
    /// RATE ELIGIBILITY is re-applied here rather than trusted from the attributes, and this is the
    /// one place the two rating sources can differ. The difficulty calculator rates a play at
    /// WHATEVER rate it was played at, including a custom 1.75x; docs/pp.md prices only the base
    /// rates (DT/NC 1.50x, HT 0.75x) and pays a custom rate nothing at all. Pricing off the
    /// attributes blindly would therefore invent pp for a play the server refuses to pay for, so a
    /// rate-ineligible play returns 0 here. Display surfaces do not show that 0: they gate on
    /// <see cref="TypeBeatRuleset.ScoreEarnsPerformancePoints"/> first and print a dash instead. The
    /// 0 is what an unguarded consumer (the performance breakdown chart) gets, and it is the honest
    /// answer for one: this play earns nothing.
    /// </para>
    /// </summary>
    public class TypeBeatPerformanceCalculator : PerformanceCalculator
    {
        public TypeBeatPerformanceCalculator(Ruleset ruleset)
            : base(ruleset)
        {
        }

        protected override PerformanceAttributes CreatePerformanceAttributes(ScoreInfo score, DifficultyAttributes attributes)
        {
            double stars = PerformancePoints.EligibleRate(score.Mods) == null ? 0 : attributes.StarRating;

            return new PerformanceAttributes
            {
                Total = PerformancePoints.ForPlay(stars, PerformancePoints.CountNotes(score), score.Accuracy, score.MaxCombo, score.Mods),
            };
        }
    }
}
