// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Difficulty;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat
{
    /// <summary>
    /// <see cref="DifficultyAttributes"/> plus the one thing a type!beat play cannot be priced
    /// without and the star rating alone cannot carry: its pp RATE multiplier.
    ///
    /// <para>
    /// Backlog 90 gave a base-rate Half Time play an extra multiplier
    /// (<see cref="PerformancePoints.HalfTimeMultiplier"/>) that is a function of the map's rating
    /// at THREE rates, not just the one the play ran at.
    /// <see cref="Scoring.TypeBeatPerformanceCalculator"/> is handed difficulty attributes and a
    /// score and nothing else, so without this it could not tell an HT play's real price from its
    /// unpenalised one, and the score panel would disagree with the results table beside it.
    /// The difficulty calculator, which DOES have the beatmap, computes it once and puts it here.
    /// </para>
    ///
    /// <para>
    /// Deliberately not serialised (no <c>JsonProperty</c>, no database attribute id): it is
    /// derived from ratings this client recomputes locally in microseconds, and osu-web's difficulty
    /// attribute table has no column that means this.
    /// </para>
    /// </summary>
    public class TypeBeatDifficultyAttributes : DifficultyAttributes
    {
        /// <summary>
        /// The pp rate multiplier for the mod stack these attributes were computed with. Exactly
        /// 1.0 for everything except a base-rate Half Time play, so a consumer that ignores it
        /// prices every other play correctly and only ever over-pays Half Time.
        /// </summary>
        public double RateMultiplier { get; set; } = 1;

        public TypeBeatDifficultyAttributes()
        {
        }

        public TypeBeatDifficultyAttributes(Mod[] mods, double starRating, double rateMultiplier)
            : base(mods, starRating)
        {
            RateMultiplier = rateMultiplier;
        }
    }
}
