// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using osu.Framework.Audio;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.API;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.UI;
using typebeat.Game.Utils;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The Conductor mod (backlog 226): the song's playback rate follows the player. Two halves are
    /// pinned here.
    ///
    /// <para>THE SHIPPING SURFACE: the acronym the server keys its always-unranked list off, the
    /// unranked flag, the Fun category, the flat 1.0x, the rate-mod exclusions, and the one thing
    /// the mod actually does to audio, which is write a tempo (not frequency) adjustment onto the
    /// aggregate the gameplay clock's rate is read from.</para>
    ///
    /// <para>THE CONTROL LAW: <see cref="ConductorController.Step"/> is a pure function, so the
    /// whole controller is driven here with no drawables, no clock and no audio at all. That is the
    /// point of factoring it out: a rate curve that has to be reproducible from a replay must be
    /// reproducible from nothing but its inputs.</para>
    /// </summary>
    [TestFixture]
    public class TypeBeatModConductorTest
    {
        private static ConductorTuning tuning(double min = TypeBeatModConductor.DEFAULT_MIN_RATE, double max = TypeBeatModConductor.DEFAULT_MAX_RATE)
            => ConductorTuning.Default.WithRateBand(min, max);

        /// <summary>Advance the controller by <paramref name="steps"/> fixed steps of the same input.</summary>
        private static ConductorState run(ConductorState state, ConductorInputs inputs, ConductorTuning tune, int steps)
        {
            for (int i = 0; i < steps; i++)
                state = ConductorController.Step(state, inputs, tune, ConductorController.STEP_SECONDS);

            return state;
        }

        /// <summary>A player typing at exactly <paramref name="cellsPerSecond"/>, perfectly smoothly.</summary>
        private static ConductorInputs typing(double cellsPerSecond, double demand, double? phaseErrorMs = null)
            => new ConductorInputs(cellsPerSecond * ConductorController.STEP_SECONDS, demand, phaseErrorMs, true);

        // -----------------------------------------------------------------------------------------
        // The shipping surface.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void ReportsUnrankedFunModWithCtAcronym()
        {
            var mod = new TypeBeatModConductor();

            Assert.AreEqual("Conductor", mod.Name);
            Assert.AreEqual("CT", mod.Acronym);
            Assert.AreEqual(ModType.Fun, mod.Type);
            Assert.IsFalse(mod.Ranked,
                "the song meeting the player halfway is exactly the kind of generosity a leaderboard cannot price");
            Assert.IsTrue(mod.HasImplementation);
            Assert.IsNotNull(mod.Icon);
            Assert.AreEqual("The song follows you.", mod.Description.ToString());

            // A follower's rate is one player's typing; there is nothing to share with a room.
            Assert.IsFalse(mod.ValidForMultiplayer);
            Assert.IsFalse(mod.ValidForMultiplayerAsFreeMod);
        }

        [Test]
        public void AcronymDoesNotCollideWithAnyOtherRulesetMod()
        {
            var ruleset = new TypeBeatRuleset();

            var acronyms = ruleset.AllMods.Select(m => m.Acronym).ToList();

            Assert.AreEqual(acronyms.Count, acronyms.Distinct().Count(), "two mods share an acronym");
            Assert.AreEqual(1, acronyms.Count(a => a == "CT"));
        }

        [Test]
        public void RulesetSurfacesConductorUnderFun()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.IsTrue(ruleset.GetModsFor(ModType.Fun).Any(m => m is TypeBeatModConductor),
                "Conductor must be offered in the mod-select overlay under Fun.");
        }

        [Test]
        public void ScoreMultiplierIsExactlyOne()
        {
            var calculator = new TypeBeatScoreMultiplierCalculator(new ScoreMultiplierContext(new BeatmapDifficulty()));

            Assert.AreEqual(1.0, calculator.CalculateFor(new Mod[] { new TypeBeatModConductor() }), 1e-9);

            // Being unlisted must be neutral, not absorbing.
            Assert.AreEqual(1.05, calculator.CalculateFor(new Mod[] { new TypeBeatModConductor(), new TypeBeatModLiterate() }), 1e-9);
        }

        /// <summary>
        /// The whole premise of the mod is that it lives in the mod layer. If someone ever reaches
        /// for the engine, the score processor or the window scale to make the feel better, this
        /// fails first: those are the surfaces with a byte-compatible JS mirror in the web repo.
        /// </summary>
        [Test]
        public void DoesNotHookAnythingThatCouldAffectScoring()
        {
            var mod = new TypeBeatModConductor();

            Assert.IsTrue(mod is IApplicableToTrack);
            Assert.IsTrue(mod is IUpdatableByPlayfield);

            Assert.IsFalse(mod is IApplicableToScoreProcessor);
            Assert.IsFalse(mod is IApplicableToHealthProcessor);
            Assert.IsFalse(mod is IApplicableToBeatmap);
            Assert.IsFalse(mod is IApplicableToBeatmapConverter);
            Assert.IsFalse(mod is IApplicableToDifficulty);
            Assert.IsFalse(mod is IApplicableFailOverride);
            Assert.IsFalse(mod is ICreateReplayData);
            Assert.IsFalse(mod is IApplicableToDrawableHitObject);

            // NOT ApplyToRate: song select and the star-rating calculator ask for one number that
            // describes the whole play, and a follower has none. They show 1.00x, which is honest.
            Assert.IsFalse(mod is IApplicableToRate);

            // It IS applied to the drawable ruleset, but only to publish the live rate for the HUD
            // readout; unlike the rate mods it must never reach Engine.WindowScale from there.
            Assert.IsTrue(mod is IApplicableToDrawableRuleset<TypeBeatHitObject>);
        }

        [Test]
        public void ExcludesEveryOtherOwnerOfThePlaybackRateKnob()
        {
            var mod = new TypeBeatModConductor();

            var incompatible = mod.IncompatibleMods;

            Assert.AreEqual(3, incompatible.Length);
            Assert.Contains(typeof(ModRateAdjust), incompatible);
            Assert.Contains(typeof(ModTimeRamp), incompatible);
            Assert.Contains(typeof(ModAdaptiveSpeed), incompatible);

            foreach (var other in new Mod[]
                     {
                         new TypeBeatModDoubleTime(),
                         new TypeBeatModNightcore(),
                         new TypeBeatModHalfTime(),
                         new ModWindUp(),
                         new ModWindDown(),
                     })
            {
                Assert.IsFalse(ModUtils.CheckCompatibleSet(new[] { (Mod)new TypeBeatModConductor(), other }),
                    $"Conductor and {other.Acronym} would fight over the same knob");
            }

            // ...and it composes with everything that does not touch the rate.
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModConductor(), new TypeBeatModMuted() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModConductor(), new TypeBeatModFlashlight(), new TypeBeatModLiterate() }));
        }

        [Test]
        public void RateBandSettingsCoverTheHardBoundsAndDefaultToHalfAndAHalf()
        {
            var mod = new TypeBeatModConductor();

            Assert.AreEqual(0.5, mod.MinRate.Default, 1e-9);
            Assert.AreEqual(1.5, mod.MaxRate.Default, 1e-9);

            foreach (var setting in new[] { mod.MinRate, mod.MaxRate })
            {
                Assert.AreEqual(TypeBeatModConductor.ABSOLUTE_MIN_RATE, setting.MinValue, 1e-9);
                Assert.AreEqual(TypeBeatModConductor.ABSOLUTE_MAX_RATE, setting.MaxValue, 1e-9);
                Assert.AreEqual(0.01, setting.Precision, 1e-9);
            }

            // The rate the audio actually reads is NOT snapped: quantising a follower to 0.01 puts
            // audible steps into a value that is meant to glide.
            Assert.AreEqual(TypeBeatModConductor.ABSOLUTE_MIN_RATE, mod.SpeedChange.MinValue, 1e-9);
            Assert.AreEqual(TypeBeatModConductor.ABSOLUTE_MAX_RATE, mod.SpeedChange.MaxValue, 1e-9);
            Assert.Less(mod.SpeedChange.Precision, 1e-6);

            // Tempo-preserving by default.
            Assert.IsFalse(mod.AdjustPitch.Value);
        }

        /// <summary>
        /// What the mod does to audio, and the only thing it does: it writes its rate onto the
        /// aggregate the gameplay clock's rate is read off
        /// (<c>GameplayClockExtensions.GetTrueGameplayRate</c> is sign * AggregateFrequency *
        /// AggregateTempo of exactly this component). So this is the clock-level proof that a
        /// controller write moves gameplay time, without standing up a Player.
        /// </summary>
        [Test]
        public void SpeedChangeMovesTheGameplayRateAsTempoByDefaultAndPitchOnDemand()
        {
            var mod = new TypeBeatModConductor();
            var adjustments = new AudioAdjustments();

            mod.ApplyToTrack(adjustments);

            Assert.AreEqual(1.0, adjustments.AggregateTempo.Value * adjustments.AggregateFrequency.Value, 1e-9);

            mod.SpeedChange.Value = 1.25;

            Assert.AreEqual(1.25, adjustments.AggregateTempo.Value, 1e-9, "the default adjustment is tempo, so pitch is preserved");
            Assert.AreEqual(1.0, adjustments.AggregateFrequency.Value, 1e-9);
            Assert.AreEqual(1.25, adjustments.AggregateTempo.Value * adjustments.AggregateFrequency.Value, 1e-9,
                "this product IS GetTrueGameplayRate");

            mod.AdjustPitch.Value = true;

            Assert.AreEqual(1.25, adjustments.AggregateFrequency.Value, 1e-9);
            Assert.AreEqual(1.0, adjustments.AggregateTempo.Value, 1e-9);
            Assert.AreEqual(1.25, adjustments.AggregateTempo.Value * adjustments.AggregateFrequency.Value, 1e-9);

            mod.SpeedChange.Value = 0.6;
            Assert.AreEqual(0.6, adjustments.AggregateTempo.Value * adjustments.AggregateFrequency.Value, 1e-9);
        }

        [Test]
        public void SettingDescriptionAlwaysCarriesTheBand()
        {
            var mod = new TypeBeatModConductor();

            var description = mod.SettingDescription.ToArray();

            Assert.IsTrue(description.Any(d => d.setting.ToString() == "Rate band" && d.value.ToString() == "0.50x to 1.50x"),
                $"got [{string.Join(", ", description.Select(d => $"{d.setting}={d.value}"))}]");

            Assert.IsFalse(description.Any(d => d.setting.ToString() == "Adjust pitch"));
            Assert.IsTrue(new TypeBeatModConductor { AdjustPitch = { Value = true } }.SettingDescription
                                                                                    .Any(d => d.setting.ToString() == "Adjust pitch"));
        }

        /// <summary>
        /// The submission payload. The acronym is the whole of what the server needs, because the
        /// server's ONLY job for this mod is to recognise "CT" in its always-unranked list; there is
        /// no rate to price, and no wire field carries one.
        /// </summary>
        [Test]
        public void WirePayloadIsTheBareAcronymAtTheDefaults()
        {
            Assert.AreEqual(@"{""acronym"":""CT""}", JsonConvert.SerializeObject(new APIMod(new TypeBeatModConductor())));

            var ruleset = new TypeBeatRuleset();
            var decoded = JsonConvert.DeserializeObject<APIMod>(@"{""acronym"":""CT""}")!.ToMod(ruleset);

            Assert.IsInstanceOf<TypeBeatModConductor>(decoded, "a stored CT score must not resolve to UnknownMod");
            Assert.IsFalse(decoded.Ranked);
        }

        // -----------------------------------------------------------------------------------------
        // Demand: the map's side of the feed-forward.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The same three-cell line the rate-mod fixture uses: "abc" sung over [0, 12000], so the
        /// cells target 0, 4000 and 8000. Three characters span two gaps of four seconds, so the
        /// line asks for 2 / 8 = 0.25 characters a second.
        /// </summary>
        [Test]
        public void DemandIsTheLinesOwnCharacterDensity()
        {
            var line = TypingLine.FromLyricLine(new LyricLine
            {
                RawText = "abc",
                StartTime = 0,
                EndTime = 20000,
                SingEndTime = 12000,
                Units = new[] { new TimedUnit { Text = "abc", StartTime = 0, EndTime = 12000 } },
            }, TimingGranularity.Line, false);

            Assert.AreEqual(0.25, TypeBeatModConductor.DemandFor(line), 1e-9);
        }

        [Test]
        public void ALineWithNothingToMeasureAsksForNothing()
        {
            var line = TypingLine.FromLyricLine(new LyricLine
            {
                RawText = "a",
                StartTime = 0,
                EndTime = 4000,
                SingEndTime = 2000,
                Units = new[] { new TimedUnit { Text = "a", StartTime = 0, EndTime = 2000 } },
            }, TimingGranularity.Line, false);

            // One character spans no gaps, so there is no density to read; the feed-forward term
            // drops out and the phase term does the whole job.
            Assert.AreEqual(0, TypeBeatModConductor.DemandFor(line), 1e-9);
            Assert.Less(0d, ConductorController.MIN_MEANINGFUL_DEMAND);
        }

        // -----------------------------------------------------------------------------------------
        // The control law.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The feed-forward term on its own. A player producing 5 characters a second on a line that
        /// asks for 4 is running at 1.25x of the map's pace, so the song is asked for 1.25x.
        /// </summary>
        [Test]
        public void ConvergesOnSupplyOverDemandForASteadyTypist()
        {
            var state = run(ConductorState.Initial, typing(5, 4), tuning(), 2000);

            Assert.AreEqual(5, state.SupplyCellsPerSecond, 1e-9, "the EMA must settle on the observed rate");
            Assert.AreEqual(1, state.Authority, 1e-6, "a filled filter is trusted whole");
            Assert.AreEqual(1.25, state.Rate, 1e-9);

            // ...and the same typist on a denser line is asked to be carried more slowly.
            Assert.AreEqual(0.625, run(ConductorState.Initial, typing(5, 8), tuning(), 2000).Rate, 1e-9);
        }

        /// <summary>
        /// An empty filter must not be read as "the player has stopped typing", or the rate would
        /// dive to the floor at the start of every play while the EMA fills. The share of the window
        /// that has not been observed yet is credited at the map's own pace instead, which makes a
        /// player who is exactly on pace read as exactly 1.00x from the very first step. Real
        /// silence is a different reading and IS followed, gliding down rather than snapping.
        /// </summary>
        [Test]
        public void AnUnfilledFilterIsCreditedAtTheMapsOwnPaceRatherThanReadAsSilence()
        {
            var state = ConductorState.Initial;

            for (int i = 0; i < 200; i++)
            {
                state = ConductorController.Step(state, typing(6, 6), tuning(), ConductorController.STEP_SECONDS);

                Assert.AreEqual(1.0, state.Rate, 1e-9, $"a player exactly on pace was slowed at step {i}");
            }

            var silent = ConductorState.Initial;
            double previous = silent.Rate;

            for (int i = 0; i < 200; i++)
            {
                silent = ConductorController.Step(silent, typing(0, 6), tuning(), ConductorController.STEP_SECONDS);

                Assert.LessOrEqual(previous - silent.Rate,
                    (TypeBeatModConductor.SLEW_PER_SECOND * ConductorController.STEP_SECONDS) + 1e-12,
                    "the song must glide down to wait, never snap");

                previous = silent.Rate;
            }

            Assert.AreEqual(TypeBeatModConductor.DEFAULT_MIN_RATE, silent.Rate, 1e-9);
        }

        /// <summary>
        /// The proportional term, isolated by feeding a typist who exactly matches the demand (so the
        /// feed-forward sits at 1.00x). Inside the deadband the song holds its pace; outside it the
        /// term eases in FROM the deadband edge, so 60 ms of lead is worth 0.002 * 20, not 0.002 * 60.
        /// </summary>
        [Test]
        public void DeadbandHoldsTheRateAndTheTermEasesInPastIt()
        {
            Assert.AreEqual(1.0, run(ConductorState.Initial, typing(4, 4, 30), tuning(), 2000).Rate, 1e-9);
            Assert.AreEqual(1.0, run(ConductorState.Initial, typing(4, 4, -30), tuning(), 2000).Rate, 1e-9);
            Assert.AreEqual(1.0, run(ConductorState.Initial, typing(4, 4, 40), tuning(), 2000).Rate, 1e-9,
                "exactly at the edge is still hold, and the term is continuous across it");

            // Ahead of the song speeds it UP to come and meet the player; behind slows it down.
            Assert.AreEqual(1.04, run(ConductorState.Initial, typing(4, 4, 60), tuning(), 2000).Rate, 1e-9);
            Assert.AreEqual(0.96, run(ConductorState.Initial, typing(4, 4, -60), tuning(), 2000).Rate, 1e-9);
        }

        /// <summary>
        /// The slew limit is a hard bound on how fast the rate may move, whatever the controller
        /// wants. A phase error of a full second asks for 2.92x; over exactly one second of track
        /// time the rate may only travel 0.8.
        /// </summary>
        [Test]
        public void SlewBoundsThePerSecondChange()
        {
            var tune = tuning(0.5, 2.0);
            var demanding = typing(4, 4, 1000);

            var afterOneSecond = run(ConductorState.Initial, demanding, tune, 50);

            Assert.AreEqual(1.8, afterOneSecond.Rate, 1e-9, "1.0 + 0.8 exactly");

            // It gets there, it just glides. 2.0 is 1.25 seconds away at the slew limit.
            Assert.AreEqual(2.0, run(ConductorState.Initial, demanding, tune, 100).Rate, 1e-9);

            // And every intermediate frame respects the bound.
            var state = ConductorState.Initial;

            for (int i = 0; i < 200; i++)
            {
                var next = ConductorController.Step(state, demanding, tune, ConductorController.STEP_SECONDS);

                Assert.LessOrEqual(Math.Abs(next.Rate - state.Rate),
                    TypeBeatModConductor.SLEW_PER_SECOND * ConductorController.STEP_SECONDS + 1e-12);

                state = next;
            }
        }

        [Test]
        public void ClampsBiteAtTheUsersRateBand()
        {
            var tune = tuning(0.8, 1.2);

            var state = ConductorState.Initial;
            double highest = state.Rate;

            for (int i = 0; i < 500; i++)
            {
                state = ConductorController.Step(state, typing(4, 4, 5000), tune, ConductorController.STEP_SECONDS);
                highest = Math.Max(highest, state.Rate);
            }

            Assert.AreEqual(1.2, state.Rate, 1e-9);
            Assert.LessOrEqual(highest, 1.2 + 1e-12, "the ceiling is a ceiling");

            double lowest = state.Rate;

            for (int i = 0; i < 500; i++)
            {
                state = ConductorController.Step(state, typing(4, 4, -5000), tune, ConductorController.STEP_SECONDS);
                lowest = Math.Min(lowest, state.Rate);
            }

            Assert.AreEqual(0.8, state.Rate, 1e-9);
            Assert.GreaterOrEqual(lowest, 0.8 - 1e-12, "the floor is a floor");
        }

        /// <summary>
        /// Two independent sliders can be dragged past each other. Read them as an unordered pair
        /// rather than letting Math.Clamp throw in the middle of a play.
        /// </summary>
        [Test]
        public void ASwappedRateBandIsReadAsAnUnorderedPair()
        {
            var inverted = tuning(1.5, 0.5);

            Assert.AreEqual(1.5, run(ConductorState.Initial, typing(4, 4, 5000), inverted, 500).Rate, 1e-9);
            Assert.AreEqual(0.5, run(ConductorState.Initial, typing(4, 4, -5000), inverted, 500).Rate, 1e-9);
        }

        /// <summary>
        /// The intro, an instrumental gap and the outro: nothing to follow, so the song plays at its
        /// own pace and the skip overlays behave exactly as they do unmodded. The filter relaxes
        /// while that lasts, so the next line is not entered holding a stale reading of a player who
        /// was necessarily silent.
        /// </summary>
        [Test]
        public void NoActiveLineEasesTheRateBackToNormal()
        {
            var running = ConductorState.Initial with { Rate = 1.4, SupplyCellsPerSecond = 8, Authority = 1 };
            var idle = new ConductorInputs(0, 0, null, false);

            var state = run(running, idle, tuning(), 100);

            Assert.AreEqual(1.0, state.Rate, 1e-9);
            Assert.Less(state.SupplyCellsPerSecond, 0.5, "the typing estimate must not survive a gap intact");
            Assert.Less(state.Authority, 0.1);

            // It EASES: half a second of track time at the slew limit is 0.4, exactly the distance.
            Assert.Greater(run(running, idle, tuning(), 10).Rate, 1.2);

            // A band that excludes 1.00x still wins: the user's floor is a floor even when idling.
            Assert.AreEqual(1.1, run(running, idle, tuning(1.1, 1.5), 100).Rate, 1e-9);
        }

        [Test]
        public void ADemandTooSmallToMeanAnythingFallsBackToTheNormalRate()
        {
            // A one-character line, or one whose characters share a target: supply/demand would be
            // an arbitrarily large number, so the term drops out entirely.
            var state = run(ConductorState.Initial, typing(20, 0), tuning(), 500);

            Assert.AreEqual(1.0, state.Rate, 1e-9);
        }

        /// <summary>
        /// The determinism the replay story rests on. The controller reads nothing but its own state
        /// and the inputs it is handed (no wall clock, no frame time, no random), so the same
        /// keystrokes always produce the same rate curve, bit for bit.
        /// </summary>
        [Test]
        public void TheSameInputsAlwaysProduceTheSameCurve()
        {
            var script = scriptedInputs(600);

            double[] first = curve(script);
            double[] second = curve(script);

            Assert.AreEqual(first.Length, second.Length);

            for (int i = 0; i < first.Length; i++)
            {
                Assert.IsTrue(first[i].Equals(second[i]),
                    $"the curve diverged at step {i}: {first[i]:R} vs {second[i]:R}");
            }

            // Not vacuous: the script actually moves the rate around.
            Assert.Greater(first.Max() - first.Min(), 0.2);
        }

        private static double[] curve(ConductorInputs[] script)
        {
            var state = ConductorState.Initial;
            double[] result = new double[script.Length];

            for (int i = 0; i < script.Length; i++)
            {
                state = ConductorController.Step(state, script[i], tuning(), ConductorController.STEP_SECONDS);
                result[i] = state.Rate;
            }

            return result;
        }

        /// <summary>
        /// A reproducible stand-in for a play: bursts of typing, varying line density, a phase error
        /// that wanders either side of the deadband, and two stretches with no active line. Built
        /// off an explicit LCG rather than <c>Random</c> so the fixture does not depend on the
        /// runtime's generator.
        /// </summary>
        private static ConductorInputs[] scriptedInputs(int count)
        {
            var script = new ConductorInputs[count];
            ulong seed = 0x2545F4914F6CDD1DUL;

            for (int i = 0; i < count; i++)
            {
                seed = (seed * 6364136223846793005UL) + 1442695040888963407UL;
                double roll = (seed >> 40) / (double)(1 << 24);

                bool active = i < 200 || i >= 260;
                active &= i < 420 || i >= 460;

                script[i] = new ConductorInputs(
                    active && roll > 0.7 ? 1 : 0,
                    3 + (i % 7),
                    active ? (roll - 0.5) * 400 : null,
                    active);
            }

            return script;
        }
    }
}
