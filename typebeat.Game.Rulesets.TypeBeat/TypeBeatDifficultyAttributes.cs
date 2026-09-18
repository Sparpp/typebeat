// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Difficulty;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Rulesets.TypeBeat
{
    /// <summary>
    /// The star rating PLUS the map's DIFFICULT CHARACTERS, which the pp formula needs and the
    /// performance calculator cannot recompute: that calculator is handed a score and these
    /// attributes, and no lyric lines, while the count is a property of the MAP at the played rate
    /// and on the played stream (see <c>PerformancePoints</c>'s class docs).
    ///
    /// <para>Same pattern as the rate-multiplier subclass this replaces in spirit: anything the
    /// per-play price needs that only the difficulty pass can see travels here rather than being
    /// recomputed from a worse approximation.</para>
    /// </summary>
    public class TypeBeatDifficultyAttributes : DifficultyAttributes
    {
        /// <summary>See <see cref="LyricDifficulty.ModelResult.DifficultCharacters"/>.</summary>
        public double DifficultCharacters { get; }

        public TypeBeatDifficultyAttributes(Mod[] mods, double starRating, double difficultCharacters)
            : base(mods, starRating)
        {
            DifficultCharacters = difficultCharacters;
        }
    }
}
