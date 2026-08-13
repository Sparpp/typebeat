// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Online.API;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
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
        [TestCase(0.60, 0.1000)] // Below the r = 0.70 floor point; clamped flat.
        [TestCase(0.70, 0.1000)] // Exactly the floor point.
        [TestCase(0.75, 0.2500)] // Half Time default.
        [TestCase(0.80, 0.4000)]
        [TestCase(0.90, 0.7000)]
        [TestCase(0.99, 0.9700)]
        [TestCase(0.71, 0.1300)] // Just above the floor point; the plateau has already ended.
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
        public void DefaultSpeedsPayExactlyWhatTheCurveAnchorsThemTo()
        {
            var calc = calculator();

            // DT/NC are untouched by the Half Time nerf, so these two are still the pre-task values.
            // HT default was renerfed from 0.55 to 0.25 (DECREASE_SLOPE 1.80 -> 3.00); this is the
            // new pinned value, and it must not move again without a matching backlog item.
            Assert.AreEqual(1.23, calc.CalculateFor(new Mod[] { new TypeBeatModDoubleTime() }), 1e-9);
            Assert.AreEqual(1.23, calc.CalculateFor(new Mod[] { new TypeBeatModNightcore() }), 1e-9);
            Assert.AreEqual(0.25, calc.CalculateFor(new Mod[] { new TypeBeatModHalfTime() }), 1e-9);
        }

        [Test]
        public void CurveIsContinuousAndUnityAtTheNoModRate()
        {
            Assert.AreEqual(1.0, TypeBeatRateMultiplier.For(1.0), 1e-12,
                "a rate of 1.0x must pay exactly what no rate mod pays");

            // Approaching 1.0x from either side converges on 1.0; there is no cliff at the seam like
            // osu's own V2 curve has (Half Time at 0.99x pays 0.886x there, an 0.114 jump). The
            // steeper post-nerf DECREASE_SLOPE widens the down-side step at 0.99x from 0.018 to 0.03,
            // still nowhere near a cliff, so the tolerance only needs to stay comfortably under 0.114.
            Assert.Less(Math.Abs(TypeBeatRateMultiplier.For(0.99) - 1.0), 0.05);
            Assert.Less(Math.Abs(TypeBeatRateMultiplier.For(1.01) - 1.0), 0.05);
        }

        [Test]
        public void CurveIsStrictlyMonotonicAboveTheFloorPlateau()
        {
            // 0.50x is the Wind Down / Half Time floor, 2.00x the Double Time / Wind Up ceiling.
            // r = 0.70 is where the curve first reaches the 0.10 floor; at and below it the curve is
            // clamped flat, so the strict-increase check only applies strictly above 0.70x.
            double previous = double.NegativeInfinity;

            for (int step = 50; step <= 200; step++)
            {
                double rate = step / 100.0;
                double multiplier = TypeBeatRateMultiplier.For(rate);

                Assert.Greater(multiplier, 0, $"the multiplier must stay positive at {rate:N2}x");

                if (rate <= 0.70)
                    Assert.AreEqual(TypeBeatRateMultiplier.MINIMUM, multiplier, 1e-12,
                        $"the multiplier must sit on the floor at or below r = 0.70x; it did not at {rate:N2}x");
                else
                    Assert.Greater(multiplier, previous,
                        $"the multiplier must strictly increase with rate above r = 0.70x; it did not at {rate:N2}x");

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
                // Hard Rock (backlog 150) is the newest entry to RAISE this product, and the reason
                // its score multiplier is 1.10 rather than its 1.25 pp value: at 1.25 the stack
                // below is 2.0121, over the ceiling asserted here.
                new TypeBeatModHardRock(),
            });

            Assert.AreEqual(1.46 * 1.05 * 1.05 * 1.10, stacked, 1e-9);

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
            // 0.8 * For(0.75) + 0.2 * For(1.00), For(0.75) renerfed from 0.55 to 0.25
            Assert.AreEqual(0.4, calc.CalculateFor(new Mod[] { new ModWindDown() }), 1e-9);

            // ...and they are still unranked, so none of this reaches a leaderboard.
            Assert.IsFalse(new ModWindUp().Ranked);
            Assert.IsFalse(new ModWindDown().Ranked);
        }

        // -----------------------------------------------------------------------------------------
        // Judgement windows scale with the rate (backlog 150), so the real-time tolerance around a
        // character is the same at every speed and the mod's difficulty is purely its pace.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// A three-cell line "abc" over [0, 12000], so the cells target 0, 4000 and 8000, struck
        /// <paramref name="pressOffset"/> ms late apiece. Replay frames are MAP time and are fed
        /// unshifted, which is exactly the point: the same deltas are graded against a ladder the
        /// mods have scaled.
        /// </summary>
        private static TypeBeatReplayAccount scoreThreeLatePresses(double pressOffset, params Mod[] mods)
        {
            var line = new LyricLine
            {
                RawText = "abc",
                StartTime = 0,
                EndTime = 20000,
                SingEndTime = 12000,
                Units = new[] { new TimedUnit { Text = "abc", StartTime = 0, EndTime = 12000 } },
            };

            var map = new TypeBeatBeatmap();
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Line });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            var replay = new Replay();
            replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true));

            for (int i = 0; i < 3; i++)
                replay.Frames.Add(new TypeBeatReplayFrame(i * 4000 + pressOffset, "abc"[i]));

            return TypeBeatReplayScorer.Score(map, mods, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
        }

        /// <summary>
        /// The live seam and the replay seam must cover the SAME set of mods. The replay scorer
        /// matches on <see cref="ModRateAdjust"/>, so every rate mod the ruleset offers has to carry
        /// <see cref="IApplicableToDrawableRuleset{T}"/>, or a live play and its own replay would be
        /// judged on different ladders. The ramps are outside both on purpose: a ramp's rate is a
        /// function of time, which one scale set before the first keypress cannot express.
        /// </summary>
        [Test]
        public void EveryRateModCarriesTheLiveWindowScaleSeam()
        {
            var rateMods = new TypeBeatRuleset().AllMods.OfType<ModRateAdjust>().ToList();

            Assert.AreEqual(3, rateMods.Count, "Double Time, Nightcore and Half Time");

            foreach (var mod in rateMods)
            {
                Assert.IsInstanceOf<IApplicableToDrawableRuleset<TypeBeatHitObject>>(mod,
                    $"{mod.Acronym} would scale a replay's windows but not a live play's");
            }

            Assert.IsNotInstanceOf<ModRateAdjust>(new ModWindUp());
            Assert.IsNotInstanceOf<ModRateAdjust>(new ModWindDown());
        }

        /// <summary>
        /// The engine works in MAP time and holds no rate, so a fixed map-time window elapses in
        /// 1/rate of the real time it used to: before this, speeding the track up TIGHTENED the
        /// windows and slowing it down LOOSENED them, on top of the rate change itself. Scaling the
        /// ladder by the rate cancels that exactly. Three presses 500 ms late are an Ok apiece
        /// unmodded (GreatLate 400, OkLate 1000); at 1.50x the Great window reaches 600 and pays all
        /// three, and three presses 800 ms late fall from Ok to Meh at 0.75x (OkLate 750).
        /// </summary>
        [Test]
        public void RateScalesTheWindowsSoTheRealTimeToleranceIsConstant()
        {
            var plain = scoreThreeLatePresses(500);
            var doubleTime = scoreThreeLatePresses(500, new TypeBeatModDoubleTime());
            var nightcore = scoreThreeLatePresses(500, new TypeBeatModNightcore());

            Assert.AreEqual(3, plain.Statistics.GetValueOrDefault(HitResult.Ok));
            Assert.AreEqual(0, plain.Statistics.GetValueOrDefault(HitResult.Great));

            Assert.AreEqual(3, doubleTime.Statistics.GetValueOrDefault(HitResult.Great));
            Assert.AreEqual(0, doubleTime.Statistics.GetValueOrDefault(HitResult.Ok));

            // Nightcore differs from Double Time only in pitch, which is not a difficulty lever.
            Assert.AreEqual(3, nightcore.Statistics.GetValueOrDefault(HitResult.Great));

            var plainLater = scoreThreeLatePresses(800);
            var halfTime = scoreThreeLatePresses(800, new TypeBeatModHalfTime());

            Assert.AreEqual(3, plainLater.Statistics.GetValueOrDefault(HitResult.Ok));
            Assert.AreEqual(3, halfTime.Statistics.GetValueOrDefault(HitResult.Meh));
            Assert.AreEqual(0, halfTime.Statistics.GetValueOrDefault(HitResult.Ok));
        }

        /// <summary>
        /// The factor is the mod's own SpeedChange, not the 1.50x default: the slider is ranked
        /// across its whole range, so a play at 1.80x is judged on 1.80x windows. Presses 700 ms
        /// late are an Ok at 1.50x (GreatLate 600) and a Great at 1.80x (720).
        /// </summary>
        [Test]
        public void TheWindowScaleIsReadOffTheUserAdjustableSlider()
        {
            var atDefault = scoreThreeLatePresses(700, new TypeBeatModDoubleTime());
            var faster = scoreThreeLatePresses(700, new TypeBeatModDoubleTime { SpeedChange = { Value = 1.80 } });

            Assert.AreEqual(3, atDefault.Statistics.GetValueOrDefault(HitResult.Ok));
            Assert.AreEqual(3, faster.Statistics.GetValueOrDefault(HitResult.Great));
        }

        /// <summary>
        /// Every window-scaling mod multiplies its factor in, so a rate and Easy compose:
        /// 0.75x x 2 is the same 1.5x ladder Double Time's default produces on its own, and Hard
        /// Rock's 0.5 puts a 1.50x play back on the unscaled one.
        /// </summary>
        [Test]
        public void RateComposesWithTheOtherWindowScalingMods()
        {
            var halfTimeEasy = scoreThreeLatePresses(500, new TypeBeatModHalfTime(), new TypeBeatModEasy());
            var doubleTime = scoreThreeLatePresses(500, new TypeBeatModDoubleTime());

            Assert.AreEqual(3, halfTimeEasy.Statistics.GetValueOrDefault(HitResult.Great));
            Assert.AreEqual(doubleTime.Statistics.GetValueOrDefault(HitResult.Great),
                halfTimeEasy.Statistics.GetValueOrDefault(HitResult.Great),
                "0.75 x 2 and 1.50 are the same ladder");

            var plain = scoreThreeLatePresses(500);
            var doubleTimeHardRock = scoreThreeLatePresses(500, new TypeBeatModDoubleTime(), new TypeBeatModHardRock());

            Assert.AreEqual(plain.Statistics.GetValueOrDefault(HitResult.Ok),
                doubleTimeHardRock.Statistics.GetValueOrDefault(HitResult.Ok),
                "1.50 x 0.5 lands back on the unscaled ladder");
            Assert.AreEqual(3, doubleTimeHardRock.Statistics.GetValueOrDefault(HitResult.Ok));
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
