// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Utils;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Recite's shipping surface (backlog 229, multiplier raised to 1.07x by backlog 240): the
    /// acronym the server keys leaderboards and score badges off, the ranked flag that lets its
    /// scores reach them, the 1.07x it is scaled by, and the column it appears in. Plus the pure
    /// hiding rule the whole mod is made of.
    ///
    /// <para>The acronym is a cross-repo contract: once a score carrying "RE" is stored, the letters
    /// can never be reused or dropped from either the client's or the server's table (the FC/FT
    /// split is the lesson), so they are pinned here rather than left to the mod class alone.</para>
    /// </summary>
    [TestFixture]
    public class TypeBeatModReciteTest
    {
        [Test]
        public void ReportsRankedDifficultyIncreaseWithItsOwnAcronym()
        {
            var mod = new TypeBeatModRecite();

            Assert.AreEqual("Recite", mod.Name);
            Assert.AreEqual("RE", mod.Acronym);
            // DifficultyIncrease, following Flashlight (the mod whose behaviour it copies) rather
            // than Fletcher (its structural template): it adds a handicap on top of the same input
            // model instead of swapping models, which is where Conversion is drawn in this ruleset.
            Assert.AreEqual(ModType.DifficultyIncrease, mod.Type);
            Assert.IsTrue(mod.Ranked);
            Assert.AreEqual("The words are hidden until you type them.", mod.Description.ToString());
        }

        [Test]
        public void ScoreMultiplierIsOnePointZeroSeven()
        {
            // The authoritative (non-obsolete) path osu uses for scoring and the mod-select overlay.
            var calculator = new TypeBeatScoreMultiplierCalculator(
                new ScoreMultiplierContext(new BeatmapDifficulty()));

            double multiplier = calculator.CalculateFor(new Mod[] { new TypeBeatModRecite() });

            Assert.AreEqual(1.07, multiplier, 1e-9);

#pragma warning disable CS0618 // the obsolete self-report is exactly what is being pinned
            Assert.AreEqual(1.07, new TypeBeatModRecite().ScoreMultiplier, 1e-9,
                "the legacy self-report and the authoritative calculator must not drift");
#pragma warning restore CS0618

            // It stacks multiplicatively, as every other entry does. The calculator is pure math
            // and does not enforce selectability, so this pins the product even though Recite and
            // Flashlight are now mutually exclusive in the mod-select overlay (see
            // ReciteAndFlashlightAreMutuallyExclusive below).
            double stacked = calculator.CalculateFor(new Mod[] { new TypeBeatModRecite(), new TypeBeatModFlashlight() });
            Assert.AreEqual(1.07 * 1.05, stacked, 1e-9);
        }

        /// <summary>
        /// Both mods hide the lyric text surface, and stacked together the stack is unplayable, so
        /// the exclusion is declared on both sides (the same reciprocal pattern
        /// <see cref="TypeBeatModHardRock.IncompatibleMods"/> / <see cref="TypeBeatModEasy.IncompatibleMods"/>
        /// use) and fires no matter which mod is picked first.
        /// </summary>
        [Test]
        public void ReciteAndFlashlightAreMutuallyExclusive()
        {
            var recite = new TypeBeatModRecite();
            var flashlight = new TypeBeatModFlashlight();

            Assert.AreEqual(new[] { typeof(TypeBeatModFlashlight) }, recite.IncompatibleMods);
            Assert.AreEqual(new[] { typeof(TypeBeatModRecite) }, flashlight.IncompatibleMods);
            Assert.IsFalse(ModUtils.CheckCompatibleSet(new Mod[] { recite, flashlight }));
        }

        /// <summary>
        /// The fattest ranked stack the server can be HANDED still fits under its absolute
        /// STACK_CAP of 2.0 with Recite in it. FL and RE stopped being co-selectable in this client
        /// (backlog 239), but the server prices whatever acronym set a stored row carries, so the
        /// pair stays in the bound. Recomputed here from the client's own multipliers rather than
        /// copied, because the number that matters is the product, and a clamped honest stack stores
        /// UNRANKED (the failure mode the Hard Rock note in the calculator records).
        /// </summary>
        [Test]
        public void FattestRankedStackStaysUnderTheServerCeiling()
        {
            var calculator = new TypeBeatScoreMultiplierCalculator(
                new ScoreMultiplierContext(new BeatmapDifficulty()));

            var doubleTime = new TypeBeatModDoubleTime();
            doubleTime.SpeedChange.Value = 2.00;

            double withoutRecite = calculator.CalculateFor(new Mod[]
            {
                doubleTime, new TypeBeatModFlashlight(), new TypeBeatModLiterate(), new TypeBeatModHardRock(), new TypeBeatModFletcher(),
            });

            double withRecite = calculator.CalculateFor(new Mod[]
            {
                doubleTime, new TypeBeatModFlashlight(), new TypeBeatModLiterate(), new TypeBeatModHardRock(), new TypeBeatModFletcher(),
                new TypeBeatModRecite(),
            });

            Assert.AreEqual(1.80602730, withoutRecite, 1e-8);
            Assert.AreEqual(1.932449211, withRecite, 1e-8);
            Assert.Less(withRecite, 2.0, "the server clamps a stack over STACK_CAP and stores it unranked");
        }

        [Test]
        public void AcronymDoesNotCollideWithAnyOtherRulesetMod()
        {
            var ruleset = new TypeBeatRuleset();

            var acronyms = ruleset.AllMods.Select(m => m.Acronym).ToList();

            Assert.AreEqual(acronyms.Count, acronyms.Distinct().Count(), "two mods share an acronym");
            Assert.AreEqual(1, acronyms.Count(a => a == "RE"));

            var resolved = ruleset.CreateModFromAcronym("RE");

            Assert.IsInstanceOf<TypeBeatModRecite>(resolved, "a stored RE score must not resolve to UnknownMod");
        }

        [Test]
        public void RulesetSurfacesReciteUnderDifficultyIncrease()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.IsTrue(ruleset.GetModsFor(ModType.DifficultyIncrease).Any(m => m is TypeBeatModRecite),
                "Recite must be offered in the mod-select overlay under Difficulty Increase.");

            foreach (var type in new[] { ModType.DifficultyReduction, ModType.Conversion, ModType.Automation, ModType.Fun, ModType.System })
            {
                Assert.IsFalse(ruleset.GetModsFor(type).Any(m => m is TypeBeatModRecite),
                    $"Recite must appear in exactly one column, not also under {type}");
            }
        }

        /// <summary>
        /// The whole of Recite's behaviour, as the pure predicate the display multiplies in (see
        /// <see cref="LyricLineDisplay.HiddenByRecite"/>): hidden iff untyped and not freestyle.
        /// </summary>
        [Test]
        public void HidingRuleIsUntypedAndNotFreestyle()
        {
            var cells = line("ab&c").Cells;

            Assert.IsTrue(cells[2].IsFreestyle, "cell 2 must be the freestyle slot for this fixture");

            // Nothing typed yet: the ordinary letters hide, the freestyle slot does not (its shimmer
            // is a random pool glyph and reveals nothing about the lyric, and hiding it would make a
            // whole freestyle section invisible).
            Assert.IsTrue(LyricLineDisplay.HiddenByRecite(cells[0]));
            Assert.IsTrue(LyricLineDisplay.HiddenByRecite(cells[1]));
            Assert.IsFalse(LyricLineDisplay.HiddenByRecite(cells[2]), "a freestyle slot stays visible");
            Assert.IsTrue(LyricLineDisplay.HiddenByRecite(cells[3]));

            // Everything that is not Untyped is shown: those cells are at or behind the caret, and
            // "upcoming" is exactly what Untyped means.
            foreach (var state in new[] { CellState.Correct, CellState.Wrong, CellState.Missed, CellState.Abandoned, CellState.AutoSkipped })
            {
                cells[0].State = state;
                Assert.IsFalse(LyricLineDisplay.HiddenByRecite(cells[0]), $"{state} must stay visible");
            }

            // Back to Untyped (a backspace) re-hides it, which is the property that makes the mod
            // survive a retype.
            cells[0].State = CellState.Untyped;
            Assert.IsTrue(LyricLineDisplay.HiddenByRecite(cells[0]), "a backspaced cell hides again");
        }

        private static TypingLine line(string text)
        {
            var source = new LyricLine
            {
                RawText = text,
                StartTime = 0,
                EndTime = 10000,
                SingEndTime = 10000,
                Units = new[] { new TimedUnit { Text = text, StartTime = 0, EndTime = 10000 } },
            };

            return TypingLine.FromLyricLine(source, literate: false);
        }
    }
}
