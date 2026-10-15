// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Shorthand: type only the first letter of each word and authored syllable; the rest completes itself.
    /// It changes the cell list, so like <see cref="TypeBeatModLiterate"/> it stamps the line objects.
    /// Experimental, so unranked.
    /// </summary>
    public class TypeBeatModShorthand : Mod, IApplicableAfterBeatmapConversion
    {
        public override string Name => "Shorthand";

        public override string Acronym => "SH";

        public override LocalisableString Description => "Type the first letter of each word and syllable. The rest completes itself.";

        public override ModType Type => ModType.DifficultyReduction;

        // Legacy self-report; the real multiplier is in TypeBeatScoreMultiplierCalculator.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 0.6;
#pragma warning restore CS0672

        public override bool Ranked => false;

        /// <summary>
        /// Polyglot has no single "first letter of a syllable"; under Dyslexia, with every space completed,
        /// the whole line would become one word.
        /// </summary>
        public override Type[] IncompatibleMods => new[] { typeof(TypeBeatModPolyglot), typeof(TypeBeatModDyslexia) };

        public void ApplyToBeatmap(IBeatmap beatmap)
        {
            foreach (var line in beatmap.HitObjects.OfType<TypeBeatHitObject>())
                line.Shorthand = true;
        }
    }
}
