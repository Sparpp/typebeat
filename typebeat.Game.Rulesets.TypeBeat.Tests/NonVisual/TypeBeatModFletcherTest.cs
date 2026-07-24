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
    /// <summary>
    /// Fletcher's shipping surface: the acronym the server keys leaderboards off, the ranked flag that
    /// lets its scores reach them, the multiplier they are scaled by, and the in-client description.
    /// </summary>
    [TestFixture]
    public class TypeBeatModFletcherTest
    {
        [Test]
        public void ReportsRankedConversionWithFtAcronym()
        {
            var mod = new TypeBeatModFletcher();

            Assert.AreEqual("Fletcher", mod.Name);
            Assert.AreEqual("FT", mod.Acronym);
            Assert.AreEqual(ModType.Conversion, mod.Type);
            Assert.IsTrue(mod.Ranked);
            Assert.AreEqual("Were you Rushing or were you Dragging?!", mod.Description.ToString());
        }

        [Test]
        public void ScoreMultiplierIsZeroPointNineEight()
        {
            // The authoritative (non-obsolete) path osu uses for scoring and the mod-select overlay.
            var calculator = new TypeBeatScoreMultiplierCalculator(
                new ScoreMultiplierContext(new BeatmapDifficulty()));

            double multiplier = calculator.CalculateFor(new Mod[] { new TypeBeatModFletcher() });

            Assert.AreEqual(0.98, multiplier, 1e-9);

            // It stacks multiplicatively with the other ranked mods, as every other entry does.
            double stacked = calculator.CalculateFor(new Mod[] { new TypeBeatModFletcher(), new TypeBeatModLiterate() });
            Assert.AreEqual(0.98 * 1.05, stacked, 1e-9);
        }

        [Test]
        public void AcronymDoesNotCollideWithAnyOtherRulesetMod()
        {
            var ruleset = new TypeBeatRuleset();

            var acronyms = ruleset.AllMods.Select(m => m.Acronym).ToList();

            Assert.AreEqual(acronyms.Count, acronyms.Distinct().Count(), "two mods share an acronym");
            Assert.AreEqual(1, acronyms.Count(a => a == "FT"));
        }

        [Test]
        public void RulesetSurfacesFletcherUnderConversion()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.IsTrue(ruleset.GetModsFor(ModType.Conversion).Any(m => m is TypeBeatModFletcher),
                "Fletcher must be offered in the mod-select overlay under Conversion.");
        }
    }
}
