// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Audio;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Utils;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Muted's shipping surface: the acronym the server keys leaderboards off, the ranked flag that
    /// lets its scores reach them, the flat 1.0x it is scored at, and the one thing it actually does,
    /// namely zeroing the track's volume. Also guards the negative: Muted must never touch the engine
    /// or the score processor, because that is where the byte-compatible JS mirror lives.
    /// </summary>
    [TestFixture]
    public class TypeBeatModMutedTest
    {
        [Test]
        public void ReportsRankedFunModWithMuAcronym()
        {
            var mod = new TypeBeatModMuted();

            Assert.AreEqual("Muted", mod.Name);
            Assert.AreEqual("MU", mod.Acronym);
            Assert.AreEqual(ModType.Fun, mod.Type);
            Assert.IsTrue(mod.Ranked, "Muted is a flex, not a cheat; its scores must reach the leaderboards.");
            Assert.AreEqual("Can you still feel the rhythm without music?", mod.Description.ToString());
            Assert.IsTrue(mod.HasImplementation);
            Assert.IsNotNull(mod.Icon);
        }

        [Test]
        public void ScoreMultiplierIsExactlyOne()
        {
            var calculator = new TypeBeatScoreMultiplierCalculator(
                new ScoreMultiplierContext(new BeatmapDifficulty()));

            Assert.AreEqual(1.0, calculator.CalculateFor(new Mod[] { new TypeBeatModMuted() }), 1e-9);

            // Being unlisted must be neutral, not absorbing: stacking leaves the other mods untouched.
            double stacked = calculator.CalculateFor(new Mod[] { new TypeBeatModMuted(), new TypeBeatModLiterate() });
            Assert.AreEqual(1.05, stacked, 1e-9);

            double withoutMuted = calculator.CalculateFor(new Mod[] { new TypeBeatModLiterate() });
            Assert.AreEqual(withoutMuted, stacked, 1e-9);

            // The obsolete self-reported value agrees with the authoritative calculator.
#pragma warning disable CS0618 // Member is obsolete
            Assert.AreEqual(1.0, new TypeBeatModMuted().ScoreMultiplier, 1e-9);
#pragma warning restore CS0618
        }

        [Test]
        public void AcronymDoesNotCollideWithAnyOtherRulesetMod()
        {
            var ruleset = new TypeBeatRuleset();

            var acronyms = ruleset.AllMods.Select(m => m.Acronym).ToList();

            Assert.AreEqual(acronyms.Count, acronyms.Distinct().Count(), "two mods share an acronym");
            Assert.AreEqual(1, acronyms.Count(a => a == "MU"));
        }

        [Test]
        public void RulesetSurfacesMutedUnderFun()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.IsTrue(ruleset.GetModsFor(ModType.Fun).Any(m => m is TypeBeatModMuted),
                "Muted must be offered in the mod-select overlay under Fun.");
        }

        [Test]
        public void ApplyToTrackZeroesVolumeAndRemovalRestoresIt()
        {
            var mod = new TypeBeatModMuted();
            var adjustments = new AudioAdjustments();

            Assert.AreEqual(1.0, adjustments.AggregateVolume.Value, 1e-9);

            mod.ApplyToTrack(adjustments);
            Assert.AreEqual(0.0, adjustments.AggregateVolume.Value, 1e-9);

            mod.RemoveFromTrack(adjustments);
            Assert.AreEqual(1.0, adjustments.AggregateVolume.Value, 1e-9,
                "leaving gameplay must give the music back");
        }

        [Test]
        public void MuteOnlyTouchesVolume()
        {
            var mod = new TypeBeatModMuted();
            var adjustments = new AudioAdjustments();

            mod.ApplyToTrack(adjustments);

            // Rate mods own frequency and tempo; Muted must compose with DT/NC/HT rather than fight them.
            Assert.AreEqual(1.0, adjustments.AggregateFrequency.Value, 1e-9);
            Assert.AreEqual(1.0, adjustments.AggregateTempo.Value, 1e-9);
            Assert.AreEqual(0.0, adjustments.AggregateBalance.Value, 1e-9);
        }

        [Test]
        public void OneInstanceCanMuteSeveralComponentsIndependently()
        {
            // The selected-mod instance is handed to song select's preview adjustments and then to the
            // gameplay clock's; removing it from one must not un-mute the other.
            var mod = new TypeBeatModMuted();
            var preview = new AudioAdjustments();
            var gameplay = new AudioAdjustments();

            mod.ApplyToTrack(preview);
            mod.ApplyToTrack(gameplay);

            Assert.AreEqual(0.0, preview.AggregateVolume.Value, 1e-9);
            Assert.AreEqual(0.0, gameplay.AggregateVolume.Value, 1e-9);

            mod.RemoveFromTrack(gameplay);

            Assert.AreEqual(0.0, preview.AggregateVolume.Value, 1e-9);
            Assert.AreEqual(1.0, gameplay.AggregateVolume.Value, 1e-9);
        }

        /// <summary>
        /// The scoring-fidelity invariant: <c>TypingEngine</c> and <c>TypeBeatScoreProcessor</c> have a
        /// byte-compatible JS mirror in the web repo, so a purely cosmetic audio mod must not reach any
        /// of the hooks that could change what a play scores. If someone ever "improves" Muted into
        /// lazer's combo-ramping version, this fails first.
        /// </summary>
        [Test]
        public void DoesNotHookAnythingThatCouldAffectScoring()
        {
            var mod = new TypeBeatModMuted();

            Assert.IsTrue(mod is IApplicableToTrack);
            Assert.IsFalse(mod is IApplicableToScoreProcessor);
            Assert.IsFalse(mod is IApplicableToHealthProcessor);
            Assert.IsFalse(mod is IApplicableToBeatmap);
            Assert.IsFalse(mod is IApplicableToBeatmapConverter);
            Assert.IsFalse(mod is IApplicableToDifficulty);
            Assert.IsFalse(mod is IApplicableToRate);
            Assert.IsFalse(mod is IApplicableFailOverride);
            Assert.IsFalse(mod is ICreateReplayData);
        }

        [Test]
        public void ComposesWithEveryOtherRulesetMod()
        {
            var mod = new TypeBeatModMuted();

            Assert.IsEmpty(mod.IncompatibleMods, "Muted excludes nothing; silence composes with everything.");

            // Spot-check the combinations players will actually reach for.
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModMuted(), new TypeBeatModDoubleTime() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModMuted(), new TypeBeatModNightcore() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModMuted(), new TypeBeatModHalfTime() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModMuted(), new TypeBeatModFlashlight() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModMuted(), new TypeBeatModFletcher(), new TypeBeatModLiterate() }));

            // And nothing else in the ruleset declares Muted as incompatible.
            var ruleset = new TypeBeatRuleset();

            foreach (var other in ruleset.AllMods.OfType<Mod>())
            {
                Assert.IsFalse(other.IncompatibleMods.Any(t => t.IsAssignableFrom(typeof(TypeBeatModMuted))),
                    $"{other.Acronym} declares Muted incompatible");
            }
        }
    }
}
