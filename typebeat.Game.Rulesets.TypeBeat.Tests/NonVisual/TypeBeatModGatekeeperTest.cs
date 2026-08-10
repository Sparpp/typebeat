// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Utils;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Gatekeeper (backlog 107): the strict wrong-key model, which used to be the default and used
    /// to be a ruleset SETTING, packaged as a mod. This fixture pins its shipping surface (acronym,
    /// type, ranked, and the fact that it costs and pays nothing), the one flag it flips, and the
    /// two consequences of the default flip that are easy to lose:
    ///
    /// <list type="bullet">
    /// <item>the 13-wrong-keys mash guard is now Gatekeeper-only, because the streak only ever
    /// accrued on the rejection path;</item>
    /// <item>Sudden Death must still fail on a wrong key WITHOUT this mod. Since backlog 109 that
    /// can no longer ride a judgement result (a typed-through wrong char applies none), so it rides
    /// <c>TypingEngine.Mistyped</c>, the one wrong-key event both models raise. The engine-level
    /// half of that is here; the end-to-end half is in <c>TestSceneTypeBeatGatekeeper</c>.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class TypeBeatModGatekeeperTest
    {
        #region Fixture

        private static LyricBeatmap map() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "t", FolderPath = string.Empty, AudioFileName = "a.mp3" },
            Granularity = TimingGranularity.Line,
            Lines = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = "ab",
                    StartTime = 1000,
                    EndTime = 9000,
                    SingEndTime = 3000,
                    Units = new[] { new TimedUnit { Text = "ab", StartTime = 1000, EndTime = 3000 } },
                },
            },
        };

        #endregion

        [Test]
        public void ReportsRankedDifficultyIncreaseWithGkAcronym()
        {
            var mod = new TypeBeatModGatekeeper();

            Assert.AreEqual("Gatekeeper", mod.Name);
            Assert.AreEqual("GK", mod.Acronym);
            Assert.AreEqual(ModType.DifficultyIncrease, mod.Type);
            Assert.IsTrue(mod.Ranked, "Gatekeeper is the old default model, not a cheat; its scores must reach the leaderboards.");
            Assert.IsTrue(mod.HasImplementation);
            Assert.IsTrue(mod.Description.ToString().Contains("rejected"),
                "the description must say plainly what it does to a wrong key");
        }

        [Test]
        public void RulesetSurfacesGatekeeperUnderDifficultyIncrease()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.IsTrue(ruleset.GetModsFor(ModType.DifficultyIncrease).Any(m => m is TypeBeatModGatekeeper),
                "Gatekeeper must be offered in the mod-select overlay under Difficulty Increase.");

            var acronyms = ruleset.AllMods.Select(m => m.Acronym).ToList();
            Assert.AreEqual(acronyms.Count, acronyms.Distinct().Count(), "two mods share an acronym");
            Assert.AreEqual(1, acronyms.Count(a => a == "GK"));
        }

        /// <summary>
        /// No multiplier of any kind. Gatekeeper swaps one wrong-key model for another rather than
        /// stacking a handicap on the same one, so it is priced at exactly 1.0 everywhere: the
        /// authoritative calculator, the obsolete self-report, and pp's own mod table (which is
        /// neutral for any acronym it does not list, which is how GK gets there).
        /// </summary>
        [Test]
        public void CostsAndPaysExactlyNothing()
        {
            var calculator = new TypeBeatScoreMultiplierCalculator(
                new ScoreMultiplierContext(new BeatmapDifficulty()));

            Assert.AreEqual(1.0, calculator.CalculateFor(new Mod[] { new TypeBeatModGatekeeper() }), 1e-9);

            // Being unlisted must be neutral, not absorbing.
            double stacked = calculator.CalculateFor(new Mod[] { new TypeBeatModGatekeeper(), new TypeBeatModLiterate() });
            Assert.AreEqual(1.05, stacked, 1e-9);
            Assert.AreEqual(calculator.CalculateFor(new Mod[] { new TypeBeatModLiterate() }), stacked, 1e-9);

#pragma warning disable CS0618 // Member is obsolete
            Assert.AreEqual(1.0, new TypeBeatModGatekeeper().ScoreMultiplier, 1e-9);
#pragma warning restore CS0618

            // pp: same statement on the other pricing path, and the reason VERSION must not move.
            Assert.AreEqual(1.0, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModGatekeeper() }, 500), 1e-12);
            Assert.AreEqual(
                PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModFlashlight() }, 500),
                PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModFlashlight(), new TypeBeatModGatekeeper() }, 500),
                1e-12);

            // And it stays pp-eligible: no rate mod, so the play is priced at the base rate.
            Assert.AreEqual(1.0, PerformancePoints.EligibleRate(new Mod[] { new TypeBeatModGatekeeper() })!.Value, 1e-12);
        }

        [Test]
        public void ComposesWithEveryOtherRulesetMod()
        {
            var mod = new TypeBeatModGatekeeper();

            Assert.IsEmpty(mod.IncompatibleMods);

            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModGatekeeper(), new TypeBeatModSuddenDeath() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModGatekeeper(), new TypeBeatModLiterate() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModGatekeeper(), new TypeBeatModDoubleTime() }));

            var ruleset = new TypeBeatRuleset();

            foreach (var other in ruleset.AllMods.OfType<Mod>())
            {
                Assert.IsFalse(other.IncompatibleMods.Any(t => t.IsAssignableFrom(typeof(TypeBeatModGatekeeper))),
                    $"{other.Acronym} declares Gatekeeper incompatible");
            }
        }

        /// <summary>
        /// The one thing the mod does, and the only engine hook it is allowed to have. Applied
        /// through <c>IApplicableToDrawableRuleset</c>, the same seam Mashing and Flashlight use,
        /// rather than through the beatmap-conversion seam Literate needs.
        /// </summary>
        [Test]
        public void ItsOnlyEffectIsClearingTheEngineFlag()
        {
            var mod = new TypeBeatModGatekeeper();

            Assert.IsTrue(mod is IApplicableToDrawableRuleset<TypeBeatHitObject>);
            Assert.IsFalse(mod is IApplicableToScoreProcessor);
            Assert.IsFalse(mod is IApplicableToHealthProcessor);
            Assert.IsFalse(mod is IApplicableAfterBeatmapConversion);
            Assert.IsFalse(mod is IApplicableToDifficulty);
            Assert.IsFalse(mod is IApplicableToRate);
            Assert.IsFalse(mod is IApplicableFailOverride);
        }

        [Test]
        public void TheEngineAllowsWrongInputUnlessSomethingTurnsItOff()
        {
            Assert.IsTrue(new TypingEngine(map()).AllowWrongInput,
                "typing wrong chars through is the DEFAULT model since backlog 107");
        }

        /// <summary>
        /// The mash guard moved with the model it belonged to. Deliberate: the streak exists to stop
        /// a player farming a model that will not take a wrong char, and the default model takes it.
        /// </summary>
        [Test]
        public void TheMashFailStreakIsGatekeeperOnly()
        {
            var strict = new TypingEngine(map()) { AllowWrongInput = false };
            strict.Update(1000);

            for (int i = 0; i < 5; i++)
                strict.ProcessKey('z', 1000 + i);

            Assert.AreEqual(5, strict.ConsecutiveWrongKeys);
            Assert.AreEqual(0, strict.CaretIndex, "rejection holds the caret on the cell");

            var relaxed = new TypingEngine(map());
            relaxed.Update(1000);

            // Only two cells exist, so only two keys can land; both are wrong and neither counts.
            relaxed.ProcessKey('z', 1000);
            relaxed.ProcessKey('z', 1001);

            Assert.AreEqual(0, relaxed.ConsecutiveWrongKeys, "the default path leaves the streak at 0");
            Assert.AreEqual(2, relaxed.Mistypes, "the keypresses are still counted as mistypes");
            Assert.AreEqual(CellState.Wrong, relaxed.Lines[0].Cells[0].State);
        }

        /// <summary>
        /// The Sudden Death regression check, at the engine level. Since backlog 109 a typed-through
        /// wrong char raises no osu result either (its cell's result is deferred), so the two events
        /// a wrong keypress CAN be caught on are <c>WrongKeyRejected</c> and <c>Mistyped</c>, and
        /// only one of them fires in both models. Sudden Death must fail on the FIRST wrong key
        /// whichever model is in force, so it has to be <c>Mistyped</c>, exactly once per press.
        /// </summary>
        [Test]
        public void MistypedIsTheOnlyWrongKeyEventBothModelsRaise()
        {
            var engine = new TypingEngine(map());

            var rejected = new List<char>();
            var judged = new List<CharJudgement>();
            int mistypes = 0;
            engine.WrongKeyRejected += rejected.Add;
            engine.CharJudged += judged.Add;
            engine.Mistyped += () => mistypes++;

            engine.Update(1000);
            Assert.IsTrue(engine.ProcessKey('z', 1000));

            Assert.IsEmpty(rejected, "nothing was rejected, so Sudden Death cannot ride the rejection event");
            Assert.AreEqual(1, mistypes, "exactly one mistype, which is what Sudden Death rides");
            Assert.AreEqual(1, judged.Count);
            Assert.AreEqual(JudgementType.WrongChar, judged[0].Type,
                "the cell judgement still travels, for the stage; it just applies no osu result now");

            // ...and with Gatekeeper both events fire, the mistype still exactly once, so subscribing
            // to it (and only it) cannot double-fail a strict play.
            var strict = new TypingEngine(map()) { AllowWrongInput = false };
            var strictRejected = new List<char>();
            int strictMistypes = 0;
            strict.WrongKeyRejected += strictRejected.Add;
            strict.Mistyped += () => strictMistypes++;
            strict.Update(1000);
            strict.ProcessKey('z', 1000);

            Assert.AreEqual(new[] { 'z' }, strictRejected);
            Assert.AreEqual(1, strictMistypes);
        }
    }
}
