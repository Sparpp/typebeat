// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypeBeatModLiterateTest
    {
        [Test]
        public void ReportsRankedDifficultyIncreaseWithLtAcronym()
        {
            var mod = new TypeBeatModLiterate();

            Assert.AreEqual("Literate", mod.Name);
            Assert.AreEqual("LT", mod.Acronym);
            Assert.AreEqual(ModType.DifficultyIncrease, mod.Type);
            Assert.IsTrue(mod.Ranked);
        }

        [Test]
        public void ScoreMultiplierIsOnePointZeroFive()
        {
            // The authoritative (non-obsolete) path osu uses for scoring and the mod-select
            // overlay display.
            var calculator = new TypeBeatScoreMultiplierCalculator(
                new ScoreMultiplierContext(new BeatmapDifficulty()));

            double multiplier = calculator.CalculateFor(new Mod[] { new TypeBeatModLiterate() });

            Assert.AreEqual(1.05, multiplier, 1e-9);
        }

        [Test]
        public void RulesetSurfacesLiterateInDifficultyIncrease()
        {
            var ruleset = new TypeBeatRuleset();

            var increaseMods = ruleset.GetModsFor(ModType.DifficultyIncrease).ToList();

            Assert.IsTrue(increaseMods.Any(m => m is TypeBeatModLiterate),
                "Literate must be offered in the mod-select overlay under Difficulty Increase.");
        }
    }
}
