// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.Mods
{
    /// <summary>
    /// An interface for mods that make general adjustments to score processor.
    /// </summary>
    public interface IApplicableToScoreProcessor : IApplicableMod
    {
        /// <summary>
        /// Provides a loaded <see cref="ScoreProcessor"/> to a mod. Called once on initialisation of a play instance.
        /// </summary>
        void ApplyToScoreProcessor(ScoreProcessor scoreProcessor);

        /// <summary>
        /// Called every time a rank calculation is requested. Allows mods to adjust the final rank.
        /// </summary>
        ScoreRank AdjustRank(ScoreRank rank, double accuracy);
    }
}
