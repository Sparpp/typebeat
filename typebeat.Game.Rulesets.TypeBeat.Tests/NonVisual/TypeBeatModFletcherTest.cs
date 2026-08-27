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
    /// Fletcher's shipping surface after backlog 208 REVERSED it: the acronym the server keys
    /// leaderboards off, the ranked flag that lets its scores reach them, the multiplier they are
    /// scaled by, and the in-client description. Plus the other half of the reversal, which is the
    /// one that can silently corrupt an existing leaderboard: the retired "FT" acronym must still
    /// resolve, still price at the 0.98x its rows were submitted under, and still be OFF the
    /// mod-select overlay.
    /// </summary>
    [TestFixture]
    public class TypeBeatModFletcherTest
    {
        [Test]
        public void ReportsRankedConversionWithItsNewAcronym()
        {
            var mod = new TypeBeatModFletcher();

            Assert.AreEqual("Fletcher", mod.Name);
            Assert.AreEqual("FC", mod.Acronym);
            Assert.AreNotEqual("FT", mod.Acronym, "FT belongs to the retired mod and to the rows already carrying it");
            Assert.AreEqual(ModType.Conversion, mod.Type);
            Assert.IsTrue(mod.Ranked);
            Assert.AreEqual("Were you Rushing or were you Dragging?!", mod.Description.ToString());
        }

        [Test]
        public void ScoreMultiplierIsOnePointZeroTwo()
        {
            // The authoritative (non-obsolete) path osu uses for scoring and the mod-select overlay.
            var calculator = new TypeBeatScoreMultiplierCalculator(
                new ScoreMultiplierContext(new BeatmapDifficulty()));

            double multiplier = calculator.CalculateFor(new Mod[] { new TypeBeatModFletcher() });

            Assert.AreEqual(1.02, multiplier, 1e-9);

            // It stacks multiplicatively with the other ranked mods, as every other entry does.
            double stacked = calculator.CalculateFor(new Mod[] { new TypeBeatModFletcher(), new TypeBeatModLiterate() });
            Assert.AreEqual(1.02 * 1.05, stacked, 1e-9);
        }

        /// <summary>
        /// The retired mod is still priced at what its stored rows were submitted under. Resolution
        /// goes through the mod REGISTRY (the multiplier calculator matches on TYPE, and a stored
        /// acronym reaches a type through <c>Ruleset.CreateModFromAcronym</c>), which is why the
        /// retired mod has to stay listed under <see cref="ModType.System"/> rather than simply be
        /// deleted: an unresolved acronym becomes <c>UnknownMod</c> and prices at 1.0x.
        /// </summary>
        [Test]
        public void TheRetiredFtAcronymStillResolvesAndStillPricesAtZeroPointNineEight()
        {
            var ruleset = new TypeBeatRuleset();

            var resolved = ruleset.CreateModFromAcronym("FT");

            Assert.IsInstanceOf<TypeBeatModLegacyFletcher>(resolved, "a stored FT score must not resolve to UnknownMod");
            Assert.IsTrue(resolved!.Ranked, "retiring the mod must not unrank the rows already on the board");

            var calculator = new TypeBeatScoreMultiplierCalculator(
                new ScoreMultiplierContext(new BeatmapDifficulty()));

            Assert.AreEqual(0.98, calculator.CalculateFor(new[] { resolved }), 1e-9);
        }

        [Test]
        public void AcronymDoesNotCollideWithAnyOtherRulesetMod()
        {
            var ruleset = new TypeBeatRuleset();

            var acronyms = ruleset.AllMods.Select(m => m.Acronym).ToList();

            Assert.AreEqual(acronyms.Count, acronyms.Distinct().Count(), "two mods share an acronym");
            Assert.AreEqual(1, acronyms.Count(a => a == "FC"));
            Assert.AreEqual(1, acronyms.Count(a => a == "FT"), "the retired acronym must stay resolvable");
        }

        [Test]
        public void RulesetSurfacesFletcherUnderConversionAndHidesTheRetiredOne()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.IsTrue(ruleset.GetModsFor(ModType.Conversion).Any(m => m is TypeBeatModFletcher),
                "Fletcher must be offered in the mod-select overlay under Conversion.");

            // The mod-select overlay builds columns for DifficultyReduction, DifficultyIncrease,
            // Automation, Conversion and Fun only, and marks every System mod invalid for selection,
            // so this is exactly "nobody can pick FT again".
            foreach (var type in new[] { ModType.DifficultyReduction, ModType.DifficultyIncrease, ModType.Conversion, ModType.Automation, ModType.Fun })
            {
                Assert.IsFalse(ruleset.GetModsFor(type).Any(m => m is TypeBeatModLegacyFletcher),
                    $"the retired mod must not be selectable under {type}");
            }

            var retired = new TypeBeatModLegacyFletcher();

            Assert.AreEqual(ModType.System, retired.Type);
            Assert.IsFalse(retired.UserPlayable);
        }
    }
}
