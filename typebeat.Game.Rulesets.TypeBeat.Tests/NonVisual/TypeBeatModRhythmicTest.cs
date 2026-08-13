// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 135: the Rhythmic mod, which puts judgement back on the MILLISECOND ladder backlog 133
// replaced as the default. The mod itself is one property on the engine, so what these pins cover
// is the shipping surface (acronym, type, ranked flag, both multipliers) and the thing the property
// actually buys: the same press, at the same time, on the same map, resolving to a different tier
// under the two measures. TestSceneTypeBeatRhythmic proves the property reaches a real Player's
// engine, and TypeBeatReplayScorerTest proves it reaches the RE-DERIVED one.

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
    [TestFixture]
    public class TypeBeatModRhythmicTest
    {
        #region Fixture

        /// <summary>
        /// "ab" at Line granularity over [1000, 3000], so the two cell targets are 1000 and 2000 and
        /// the line's character spacing is exactly 1000 ms. That spacing is what makes the two
        /// ladders disagree readably: one character IS one second here, so a press a few hundred
        /// milliseconds out is a rounding error on the character axis and a whole tier on the
        /// millisecond one.
        /// </summary>
        private static LyricBeatmap map() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = new[]
            {
                new LyricLine
                {
                    RawText = "ab",
                    StartTime = 1000,
                    EndTime = 4000,
                    SingEndTime = 3000,
                    Units = new[] { new TimedUnit { Text = "ab", StartTime = 1000, EndTime = 3000 } },
                },
            },
            Granularity = TimingGranularity.Line,
        };

        /// <summary>The tier the first cell resolves at when 'a' is pressed at <paramref name="time"/>.</summary>
        private static CharJudgement press(SyncMeasure measure, double time)
        {
            var engine = new TypingEngine(map()) { Measure = measure };

            CharJudgement? judged = null;
            engine.CharJudged += j => judged = j;

            engine.Update(1000);
            engine.ProcessKey('a', time);

            Assert.IsTrue(judged.HasValue, "the press was not judged at all");

            return judged!.Value;
        }

        #endregion

        [Test]
        public void ReportsRankedDifficultyIncreaseWithRhAcronym()
        {
            var mod = new TypeBeatModRhythmic();

            Assert.AreEqual("Rhythmic", mod.Name);
            Assert.AreEqual("RH", mod.Acronym);
            Assert.AreEqual(ModType.DifficultyIncrease, mod.Type);
            Assert.IsTrue(mod.Ranked);
            Assert.AreEqual("Every character on its own beat, not just near the playhead.", mod.Description.ToString());

            // No icon of its own, so the overlay renders the acronym pill, exactly as Literate,
            // Fletcher and Gatekeeper do.
            Assert.IsNull(mod.Icon);
        }

        [Test]
        public void ScoreMultiplierIsOnePointOne()
        {
            // The authoritative (non-obsolete) path osu uses for scoring and the mod-select overlay.
            var calculator = new TypeBeatScoreMultiplierCalculator(
                new ScoreMultiplierContext(new BeatmapDifficulty()));

            Assert.AreEqual(1.10, calculator.CalculateFor(new Mod[] { new TypeBeatModRhythmic() }), 1e-9);

            // It stacks multiplicatively with the other ranked mods, as every other entry does.
            Assert.AreEqual(1.10 * 1.05,
                calculator.CalculateFor(new Mod[] { new TypeBeatModRhythmic(), new TypeBeatModLiterate() }), 1e-9);

            // And the obsolete self-report agrees, for any legacy reader.
#pragma warning disable CS0618 // Member is obsolete
            Assert.AreEqual(1.10, new TypeBeatModRhythmic().ScoreMultiplier, 1e-9);
#pragma warning restore CS0618
        }

        /// <summary>
        /// The pp VALUE lives in <c>PerformancePointsTest.ModMultiplier_RhythmicPaysTenPercent</c>,
        /// which carries the marker <c>pp.py</c> rewrites on a retune; duplicating the number here
        /// would leave a second copy to go stale. What belongs here is the property the mod has to
        /// keep whatever that number becomes: it is priced at all, and it does not make the play
        /// pp-ineligible the way a custom rate does.
        /// </summary>
        [Test]
        public void TheModIsPricedAndStaysPpEligible()
        {
            Assert.AreNotEqual(1.0, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModRhythmic() }, 500),
                "a mod pp does not know is silently neutral, which is exactly the failure to catch");

            // No rate mod, so the play is priced at the base rate rather than made ineligible.
            Assert.AreEqual(1.0, PerformancePoints.EligibleRate(new Mod[] { new TypeBeatModRhythmic() })!.Value, 1e-12);
        }

        [Test]
        public void AcronymDoesNotCollideWithAnyOtherRulesetMod()
        {
            var ruleset = new TypeBeatRuleset();

            var acronyms = ruleset.AllMods.Select(m => m.Acronym).ToList();

            Assert.AreEqual(acronyms.Count, acronyms.Distinct().Count(), "two mods share an acronym");
            Assert.AreEqual(1, acronyms.Count(a => a == "RH"));
        }

        [Test]
        public void RulesetSurfacesRhythmicUnderDifficultyIncrease()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.IsTrue(ruleset.GetModsFor(ModType.DifficultyIncrease).Any(m => m is TypeBeatModRhythmic),
                "Rhythmic must be offered in the mod-select overlay under Difficulty Increase.");
        }

        /// <summary>
        /// It excludes nothing. Rhythmic changes the UNIT a press is measured in, where Mashing
        /// changes which keys count and Fletcher changes where the caret may be, so no two of them
        /// contend for the same decision.
        /// </summary>
        [Test]
        public void ComposesWithEveryOtherRulesetMod()
        {
            var mod = new TypeBeatModRhythmic();

            Assert.IsEmpty(mod.IncompatibleMods);

            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModRhythmic(), new TypeBeatModFletcher() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModRhythmic(), new TypeBeatModMashing() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModRhythmic(), new TypeBeatModLiterate(), new TypeBeatModDoubleTime() }));

            var ruleset = new TypeBeatRuleset();

            foreach (var other in ruleset.AllMods.OfType<Mod>())
            {
                Assert.IsFalse(other.IncompatibleMods.Any(t => t.IsAssignableFrom(typeof(TypeBeatModRhythmic))),
                    $"{other.Acronym} declares Rhythmic incompatible");
            }
        }

        /// <summary>
        /// The one thing the mod does, and the only engine hook it is allowed to have. Applied
        /// through <c>IApplicableToDrawableRuleset</c>, the seam Mashing, Fletcher and Gatekeeper
        /// use, rather than through the beatmap-conversion seam Literate needs: the cell list is
        /// untouched, only the ruler held against it changes.
        /// </summary>
        [Test]
        public void ItsOnlyEffectIsTheEngineMeasure()
        {
            var mod = new TypeBeatModRhythmic();

            Assert.IsTrue(mod is IApplicableToDrawableRuleset<TypeBeatHitObject>);
            Assert.IsFalse(mod is IApplicableToScoreProcessor);
            Assert.IsFalse(mod is IApplicableToHealthProcessor);
            Assert.IsFalse(mod is IApplicableAfterBeatmapConversion);
            Assert.IsFalse(mod is IApplicableToDifficulty);
            Assert.IsFalse(mod is IApplicableToRate);
            Assert.IsFalse(mod is IApplicableFailOverride);
        }

        /// <summary>
        /// What the mod is FOR, on a map whose characters are a second apart: presses that the
        /// character ladder cannot tell from perfect are a tier and then two tiers down on the
        /// millisecond one. The 'a' cell's target is 1000, so 1300 is 300 ms late (past the
        /// millisecond Perfect row's 200) and 2500 is 1500 ms late (past the Ok row's 1000), while
        /// on the character axis they are 0.3 and 1.5 characters out, both inside the Perfect row's
        /// 4.00.
        ///
        /// <para>The DELTA the event carries is milliseconds under either measure, deliberately: it
        /// is the honest read-out of when the press happened, and a timing display wants it whatever
        /// the play is judged by.</para>
        /// </summary>
        [Test]
        public void TheMillisecondLadderGradesPressesTheCharacterLadderCallsPerfect()
        {
            var lateDefault = press(SyncMeasure.CharacterDistance, 1300);
            var lateRhythmic = press(SyncMeasure.Milliseconds, 1300);

            var laterDefault = press(SyncMeasure.CharacterDistance, 2500);
            var laterRhythmic = press(SyncMeasure.Milliseconds, 2500);

            Assert.AreEqual(JudgementType.Perfect, lateDefault.Type);
            Assert.AreEqual(JudgementType.Great, lateRhythmic.Type);

            Assert.AreEqual(JudgementType.Perfect, laterDefault.Type);
            Assert.AreEqual(JudgementType.Meh, laterRhythmic.Type);

            // The points follow the tier, which is the whole cost of the mod.
            Assert.AreEqual(300, lateDefault.PointsAwarded);
            Assert.AreEqual(200, lateRhythmic.PointsAwarded);
            Assert.AreEqual(300, laterDefault.PointsAwarded);
            Assert.AreEqual(50, laterRhythmic.PointsAwarded);

            // Both arms report the same lead/lag, in milliseconds, under either measure.
            Assert.AreEqual(300, lateDefault.Delta, 1e-9);
            Assert.AreEqual(300, lateRhythmic.Delta, 1e-9);
            Assert.AreEqual(1500, laterDefault.Delta, 1e-9);
            Assert.AreEqual(1500, laterRhythmic.Delta, 1e-9);
        }

        /// <summary>
        /// A press dead on its own target is the top tier under BOTH measures, which is what makes
        /// the mod a narrower window rather than a different game: the same play, typed exactly, is
        /// worth exactly the same.
        /// </summary>
        [Test]
        public void APressOnTargetIsPerfectUnderEitherMeasure()
        {
            Assert.AreEqual(JudgementType.Perfect, press(SyncMeasure.CharacterDistance, 1000).Type);
            Assert.AreEqual(JudgementType.Perfect, press(SyncMeasure.Milliseconds, 1000).Type);
        }

        /// <summary>
        /// The default is untouched: an engine nobody has applied the mod to is on the character
        /// ladder, and its windows are the character ones.
        /// </summary>
        [Test]
        public void AnUnmoddedEngineIsStillOnTheCharacterLadder()
        {
            var engine = new TypingEngine(map());

            Assert.AreEqual(SyncMeasure.CharacterDistance, engine.Measure);
            Assert.AreEqual(SyncMeasure.CharacterDistance, engine.Windows.Measure);
            Assert.AreEqual(2.50, engine.Windows.PerfectEarly, 1e-12);

            engine.Measure = SyncMeasure.Milliseconds;

            // The MILLISECOND ladder is frozen and backlog 146 did not touch it: the mod exists to
            // reproduce the pre-133 timing judgement byte for byte, so widening it would make
            // Rhythmic a different game rather than the old one.
            Assert.AreEqual(SyncMeasure.Milliseconds, engine.Windows.Measure);
            Assert.AreEqual(125, engine.Windows.PerfectEarly, 1e-12);
        }
    }
}
