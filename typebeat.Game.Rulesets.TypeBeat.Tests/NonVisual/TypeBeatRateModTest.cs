// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.API;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;
using typebeat.Game.Scoring.Legacy;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Variable-rate Half Time / Double Time / Nightcore (backlog 27). type!beat ranks the rate mods
    /// at EVERY speed, so this fixture pins the four things that makes load-bearing:
    /// the ranked flag, the rate-to-multiplier curve, the fact that the chosen rate is visible on
    /// every score display, and the fact that it survives storage, the .osr round trip and the wire.
    /// </summary>
    [TestFixture]
    public class TypeBeatRateModTest
    {
        private static TypeBeatScoreMultiplierCalculator calculator()
            => new TypeBeatScoreMultiplierCalculator(new ScoreMultiplierContext(new BeatmapDifficulty()));

        // -----------------------------------------------------------------------------------------
        // Ranked at any speed.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void RateModsAreRankedAtEverySpeedTheSliderAllows()
        {
            var dt = new TypeBeatModDoubleTime();
            var nc = new TypeBeatModNightcore();
            var ht = new TypeBeatModHalfTime();

            Assert.IsTrue(dt.Ranked, "Double Time must be ranked at its default speed.");
            Assert.IsTrue(nc.Ranked, "Nightcore must be ranked at its default speed.");
            Assert.IsTrue(ht.Ranked, "Half Time must be ranked at its default speed.");

            foreach (double rate in new[] { 1.01, 1.23, 1.99, 2.0 })
            {
                dt.SpeedChange.Value = rate;
                nc.SpeedChange.Value = rate;
                Assert.IsTrue(dt.Ranked, $"Double Time must stay ranked at {rate:N2}x.");
                Assert.IsTrue(nc.Ranked, $"Nightcore must stay ranked at {rate:N2}x.");
            }

            foreach (double rate in new[] { 0.5, 0.66, 0.9, 0.99 })
            {
                ht.SpeedChange.Value = rate;
                Assert.IsTrue(ht.Ranked, $"Half Time must stay ranked at {rate:N2}x.");
            }

            // Pitch is not a difficulty lever, so toggling it must not unrank either.
            dt.AdjustPitch.Value = true;
            Assert.IsTrue(dt.Ranked);
        }

        [Test]
        public void SliderRangesAreTheOnesTheMultiplierCurveIsDefinedOver()
        {
            var dt = new TypeBeatModDoubleTime();
            var ht = new TypeBeatModHalfTime();
            var nc = new TypeBeatModNightcore();

            Assert.AreEqual(1.5, dt.SpeedChange.Default, 1e-9);
            Assert.AreEqual(1.5, nc.SpeedChange.Default, 1e-9);
            Assert.AreEqual(0.75, ht.SpeedChange.Default, 1e-9);

            Assert.AreEqual(1.01, dt.SpeedChange.MinValue, 1e-9);
            Assert.AreEqual(2.0, dt.SpeedChange.MaxValue, 1e-9);
            Assert.AreEqual(0.5, ht.SpeedChange.MinValue, 1e-9);
            Assert.AreEqual(0.99, ht.SpeedChange.MaxValue, 1e-9);

            // The curve snaps the rate to 2dp; the sliders must not offer finer than that.
            Assert.AreEqual(0.01, dt.SpeedChange.Precision, 1e-9);
            Assert.AreEqual(0.01, ht.SpeedChange.Precision, 1e-9);
            Assert.AreEqual(0.01, nc.SpeedChange.Precision, 1e-9);
        }

        // -----------------------------------------------------------------------------------------
        // The multiplier curve.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The exact wire/scoring contract. Any change to these numbers re-bases every rate-modded
        /// score on every leaderboard and must be mirrored in the web backend.
        /// </summary>
        [TestCase(0.50, 0.1000)]
        [TestCase(0.60, 0.2800)]
        [TestCase(0.70, 0.4600)]
        [TestCase(0.75, 0.5500)] // Half Time default.
        [TestCase(0.80, 0.6400)]
        [TestCase(0.90, 0.8200)]
        [TestCase(0.99, 0.9820)]
        [TestCase(1.00, 1.0000)]
        [TestCase(1.01, 1.0046)]
        [TestCase(1.10, 1.0460)]
        [TestCase(1.25, 1.1150)]
        [TestCase(1.50, 1.2300)] // Double Time / Nightcore default.
        [TestCase(1.75, 1.3450)]
        [TestCase(1.90, 1.4140)]
        [TestCase(2.00, 1.4600)]
        public void MultiplierCurveIsPinned(double rate, double expected)
        {
            Assert.AreEqual(expected, TypeBeatRateMultiplier.For(rate), 1e-12);
        }

        [Test]
        public void DefaultSpeedsPayExactlyWhatTheOldFlatValuesPaid()
        {
            var calc = calculator();

            // These three numbers are the pre-task values. They must not move, or every existing
            // default-speed DT/NC/HT score on the boards is silently re-valued.
            Assert.AreEqual(1.23, calc.CalculateFor(new Mod[] { new TypeBeatModDoubleTime() }), 1e-9);
            Assert.AreEqual(1.23, calc.CalculateFor(new Mod[] { new TypeBeatModNightcore() }), 1e-9);
            Assert.AreEqual(0.55, calc.CalculateFor(new Mod[] { new TypeBeatModHalfTime() }), 1e-9);
        }

        [Test]
        public void CurveIsContinuousAndUnityAtTheNoModRate()
        {
            Assert.AreEqual(1.0, TypeBeatRateMultiplier.For(1.0), 1e-12,
                "a rate of 1.0x must pay exactly what no rate mod pays");

            // Approaching 1.0x from either side converges on 1.0; there is no cliff at the seam.
            Assert.Less(Math.Abs(TypeBeatRateMultiplier.For(0.99) - 1.0), 0.02);
            Assert.Less(Math.Abs(TypeBeatRateMultiplier.For(1.01) - 1.0), 0.02);
        }

        [Test]
        public void CurveIsStrictlyMonotonicOverEveryReachableRate()
        {
            // 0.50x is the Wind Down / Half Time floor, 2.00x the Double Time / Wind Up ceiling.
            double previous = double.NegativeInfinity;

            for (int step = 50; step <= 200; step++)
            {
                double rate = step / 100.0;
                double multiplier = TypeBeatRateMultiplier.For(rate);

                Assert.Greater(multiplier, previous,
                    $"the multiplier must strictly increase with rate; it did not at {rate:N2}x");
                Assert.Greater(multiplier, 0, $"the multiplier must stay positive at {rate:N2}x");

                previous = multiplier;
            }
        }

        [Test]
        public void CurveIsSnappedSoAnIndependentImplementationCanMatchItExactly()
        {
            for (int step = 50; step <= 200; step++)
            {
                double multiplier = TypeBeatRateMultiplier.For(step / 100.0);

                Assert.AreEqual(Math.Round(multiplier, TypeBeatRateMultiplier.MULTIPLIER_DECIMALS), multiplier,
                    "the multiplier must already be snapped to 4dp");
            }

            // Sub-precision jitter on the rate (a JSON round trip, a slider drag) must not change
            // the payout: the rate is snapped to the slider's 2dp before the curve is evaluated.
            Assert.AreEqual(TypeBeatRateMultiplier.For(1.5), TypeBeatRateMultiplier.For(1.5000000001), 1e-12);
            Assert.AreEqual(TypeBeatRateMultiplier.For(1.5), TypeBeatRateMultiplier.For(1.4999999999), 1e-12);
        }

        [Test]
        public void CurveNeverReturnsAZeroOrNegativeMultiplier()
        {
            // Unreachable through the UI, but the clamp is the reason a future lower bound cannot
            // hand out a zero-scoring or sign-flipped play.
            Assert.AreEqual(TypeBeatRateMultiplier.MINIMUM, TypeBeatRateMultiplier.For(0.2), 1e-12);
            Assert.AreEqual(TypeBeatRateMultiplier.MINIMUM, TypeBeatRateMultiplier.For(0.0), 1e-12);
        }

        [Test]
        public void CalculatorPaysEachRateModOffTheSharedCurve()
        {
            var calc = calculator();

            foreach (double rate in new[] { 1.01, 1.2, 1.5, 1.77, 2.0 })
            {
                double expected = TypeBeatRateMultiplier.For(rate);

                Assert.AreEqual(expected, calc.CalculateFor(new Mod[] { new TypeBeatModDoubleTime { SpeedChange = { Value = rate } } }), 1e-9);
                Assert.AreEqual(expected, calc.CalculateFor(new Mod[] { new TypeBeatModNightcore { SpeedChange = { Value = rate } } }), 1e-9);
            }

            foreach (double rate in new[] { 0.5, 0.63, 0.75, 0.9, 0.99 })
            {
                Assert.AreEqual(TypeBeatRateMultiplier.For(rate),
                    calc.CalculateFor(new Mod[] { new TypeBeatModHalfTime { SpeedChange = { Value = rate } } }), 1e-9);
            }
        }

        [Test]
        public void RateMultiplierStacksMultiplicativelyAndStaysUnderTheServerCeiling()
        {
            var calc = calculator();

            double stacked = calc.CalculateFor(new Mod[]
            {
                new TypeBeatModDoubleTime { SpeedChange = { Value = 2.0 } },
                new TypeBeatModFlashlight(),
                new TypeBeatModLiterate(),
            });

            Assert.AreEqual(1.46 * 1.05 * 1.05, stacked, 1e-9);

            // The server bounds a submitted total by TotalScoreWithoutMods * 2.0. The richest legal
            // ranked stack must stay under that or honest maximum-rate plays get clamped.
            Assert.Less(stacked, 2.0, "the fattest ranked mod stack must stay under the server's 2.0x ceiling");
        }

        [Test]
        public void WindRampsAreUnchangedAtTheirDefaults()
        {
            var calc = calculator();

            // 0.8 * For(1.00) + 0.2 * For(1.50)
            Assert.AreEqual(1.046, calc.CalculateFor(new Mod[] { new ModWindUp() }), 1e-9);
            // 0.8 * For(0.75) + 0.2 * For(1.00)
            Assert.AreEqual(0.64, calc.CalculateFor(new Mod[] { new ModWindDown() }), 1e-9);

            // ...and they are still unranked, so none of this reaches a leaderboard.
            Assert.IsFalse(new ModWindUp().Ranked);
            Assert.IsFalse(new ModWindDown().Ranked);
        }

        // -----------------------------------------------------------------------------------------
        // Display: the chosen rate must be legible wherever the mod is.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// <see cref="typebeat.Game.Rulesets.UI.ModIcon"/> renders <c>ExtendedIconInformation</c> as
        /// a pill attached to the icon, and every score display (results panel, song-select
        /// leaderboard rows, online boards, profile scores, gameplay HUD) builds its icons with
        /// extended information on. Because every rate is now ranked, the pill must be present even
        /// at the default, or a leaderboard "DT" is ambiguous between 1.01x and 2.00x.
        /// </summary>
        [Test]
        public void IconAlwaysCarriesTheRate()
        {
            Assert.AreEqual("1.50x", new TypeBeatModDoubleTime().ExtendedIconInformation);
            Assert.AreEqual("1.50x", new TypeBeatModNightcore().ExtendedIconInformation);
            Assert.AreEqual("0.75x", new TypeBeatModHalfTime().ExtendedIconInformation);

            Assert.AreEqual("1.73x", new TypeBeatModDoubleTime { SpeedChange = { Value = 1.73 } }.ExtendedIconInformation);
            Assert.AreEqual("2.00x", new TypeBeatModDoubleTime { SpeedChange = { Value = 2.0 } }.ExtendedIconInformation);
            Assert.AreEqual("0.50x", new TypeBeatModHalfTime { SpeedChange = { Value = 0.5 } }.ExtendedIconInformation);
            Assert.AreEqual("1.05x", new TypeBeatModNightcore { SpeedChange = { Value = 1.05 } }.ExtendedIconInformation);
        }

        /// <summary>
        /// The text form, used by the mod tooltip, the online-leaderboard hover card (which draws its
        /// icons with the pill suppressed and prints this instead) and preset rows.
        /// </summary>
        [Test]
        public void SettingDescriptionAlwaysCarriesTheRate()
        {
            assertDescribesRate(new TypeBeatModDoubleTime(), "1.50x");
            assertDescribesRate(new TypeBeatModNightcore(), "1.50x");
            assertDescribesRate(new TypeBeatModHalfTime(), "0.75x");
            assertDescribesRate(new TypeBeatModDoubleTime { SpeedChange = { Value = 1.73 } }, "1.73x");
            assertDescribesRate(new TypeBeatModHalfTime { SpeedChange = { Value = 0.62 } }, "0.62x");

            // The pitch toggle still describes itself, and only when it is actually on.
            var pitched = new TypeBeatModDoubleTime { AdjustPitch = { Value = true } };
            Assert.IsTrue(pitched.SettingDescription.Any(d => d.setting.ToString() == "Adjust pitch"));
            Assert.IsFalse(new TypeBeatModDoubleTime().SettingDescription.Any(d => d.setting.ToString() == "Adjust pitch"));
        }

        private static void assertDescribesRate(Mod mod, string expected)
        {
            var description = mod.SettingDescription.ToArray();

            Assert.IsTrue(description.Any(d => d.setting.ToString() == "Speed change" && d.value.ToString() == expected),
                $"{mod.Acronym} must describe its rate as {expected}; got [{string.Join(", ", description.Select(d => $"{d.setting}={d.value}"))}]");
        }

        // -----------------------------------------------------------------------------------------
        // Wire + storage.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The exact submission payload. <c>speed_change</c> is pinned on even at the default so the
        /// server never has to know the client's default to price or display the play; the shape is
        /// still the plain osu APIMod shape, so an older server that only reads the key when it is
        /// present keeps working unchanged.
        /// </summary>
        [Test]
        public void WirePayloadAlwaysCarriesTheRate()
        {
            assertWire(new TypeBeatModDoubleTime(), @"{""acronym"":""DT"",""settings"":{""speed_change"":1.5}}");
            assertWire(new TypeBeatModNightcore(), @"{""acronym"":""NC"",""settings"":{""speed_change"":1.5}}");
            assertWire(new TypeBeatModHalfTime(), @"{""acronym"":""HT"",""settings"":{""speed_change"":0.75}}");
            assertWire(new TypeBeatModDoubleTime { SpeedChange = { Value = 1.73 } }, @"{""acronym"":""DT"",""settings"":{""speed_change"":1.73}}");
            assertWire(new TypeBeatModHalfTime { SpeedChange = { Value = 0.5 } }, @"{""acronym"":""HT"",""settings"":{""speed_change"":0.5}}");

            // Non-rate mods are untouched: no settings block at all.
            assertWire(new TypeBeatModFletcher(), @"{""acronym"":""FT""}");
            assertWire(new TypeBeatModNoFail(), @"{""acronym"":""NF""}");
        }

        private static void assertWire(Mod mod, string expectedJson)
            => Assert.AreEqual(expectedJson, JsonConvert.SerializeObject(new APIMod(mod)));

        [Test]
        public void WirePayloadRoundTripsBackIntoTheSameRate()
        {
            var ruleset = new TypeBeatRuleset();

            foreach (var original in new Mod[]
                     {
                         new TypeBeatModDoubleTime { SpeedChange = { Value = 1.73 } },
                         new TypeBeatModNightcore { SpeedChange = { Value = 1.01 } },
                         new TypeBeatModHalfTime { SpeedChange = { Value = 0.62 } },
                         new TypeBeatModDoubleTime(),
                     })
            {
                string json = JsonConvert.SerializeObject(new APIMod(original));
                var decoded = JsonConvert.DeserializeObject<APIMod>(json)!.ToMod(ruleset);

                Assert.AreEqual(original.GetType(), decoded.GetType());
                Assert.AreEqual(rateOf(original), rateOf(decoded), 1e-9,
                    $"{original.Acronym} lost its rate across the wire");
                Assert.IsTrue(decoded.Ranked);
            }
        }

        /// <summary>The local (realm) score store keeps mods as the same APIMod JSON.</summary>
        [Test]
        public void LocalScoreStorePreservesTheRate()
        {
            var ruleset = new TypeBeatRuleset();

            var stored = new ScoreInfo { Ruleset = ruleset.RulesetInfo };
            stored.Mods = new Mod[] { new TypeBeatModDoubleTime { SpeedChange = { Value = 1.73 } } };

            Assert.IsTrue(stored.ModsJson.Contains(@"""speed_change"":1.73"), $"unexpected stored mods json: {stored.ModsJson}");

            // Reload the way realm does: a fresh row carrying only the json.
            var reloaded = new ScoreInfo { Ruleset = ruleset.RulesetInfo, ModsJson = stored.ModsJson };

            var mod = reloaded.Mods.OfType<TypeBeatModDoubleTime>().Single();
            Assert.AreEqual(1.73, mod.SpeedChange.Value, 1e-9);
            Assert.IsTrue(mod.Ranked);
        }

        /// <summary>
        /// The blob the .osr carries (<see cref="LegacyReplaySoloScoreInfo"/>) is the ONLY place a
        /// replay's rate can live; the legacy mod bitfield can only say "DT", not "DT at 1.73x".
        /// </summary>
        [Test]
        public void LegacyReplayScoreBlobPreservesTheRate()
        {
            var ruleset = new TypeBeatRuleset();

            var score = new ScoreInfo { Ruleset = ruleset.RulesetInfo };
            score.Mods = new Mod[] { new TypeBeatModHalfTime { SpeedChange = { Value = 0.62 } } };

            string json = JsonConvert.SerializeObject(LegacyReplaySoloScoreInfo.FromScore(score));
            var read = JsonConvert.DeserializeObject<LegacyReplaySoloScoreInfo>(json)!;

            var mod = read.Mods.Select(m => m.ToMod(ruleset)).OfType<TypeBeatModHalfTime>().Single();
            Assert.AreEqual(0.62, mod.SpeedChange.Value, 1e-9);
            Assert.IsTrue(mod.Ranked);
        }

        [Test]
        public void RateChangeIsWhatActuallyMovesTheClock()
        {
            // The engine is untouched by this task: a rate mod is a clock adjustment, nothing else.
            // ApplyToRate is what the gameplay clock and the replay playback both go through.
            var dt = new TypeBeatModDoubleTime { SpeedChange = { Value = 1.73 } };
            var ht = new TypeBeatModHalfTime { SpeedChange = { Value = 0.62 } };

            Assert.AreEqual(1.73, dt.ApplyToRate(0, 1), 1e-9);
            Assert.AreEqual(0.62, ht.ApplyToRate(0, 1), 1e-9);
        }

        private static double rateOf(Mod mod) => ((ModRateAdjust)mod).SpeedChange.Value;

        // -----------------------------------------------------------------------------------------
        // Sanity: nothing else in the mod list moved.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void OtherModMultipliersAreUnchanged()
        {
            var calc = calculator();

            var expected = new Dictionary<Mod, double>
            {
                { new TypeBeatModNoFail(), 0.5 },
                { new TypeBeatModSuddenDeath(), 1.0 },
                { new TypeBeatModFlashlight(), 1.05 },
                { new TypeBeatModLiterate(), 1.05 },
                { new TypeBeatModFletcher(), 0.98 },
                { new TypeBeatModMashing(), 0.1 },
            };

            foreach (var (mod, multiplier) in expected)
                Assert.AreEqual(multiplier, calc.CalculateFor(new[] { mod }), 1e-9, $"{mod.Acronym} multiplier moved");
        }
    }
}
