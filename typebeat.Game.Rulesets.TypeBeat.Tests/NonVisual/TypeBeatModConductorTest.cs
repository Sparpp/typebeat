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
            => new ConductorInputs(cellsPerSecond * ConductorController.STEP_SECONDS, demand, phaseErrorMs, true, false);

        /// <summary>
        /// A player who has run out of line: the caret is past the last cell of a line that is still
        /// active, so nothing is typeable, nothing is accepted and there is no judgeable cell to take
        /// a phase error from. This is what finishing a line EARLY looks like to the controller, and
        /// it is also what a skipped or abandoned line looks like.
        /// </summary>
        private static ConductorInputs finishedLine(double demand)
            => new ConductorInputs(0, demand, null, true, true);

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
            Assert.AreEqual("The song follows you. Audio quality degrades at extreme rates.", mod.Description.ToString(),
                "the band reaches 51x now (backlog 252), and a player should be told the audio suffers there before they drag it");

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

            // Backlog 252 uncapped the band at both ends, and neither end is a taste call: 0 is
            // "stop and wait for me" and 51 is BASS_FX's own +5000% tempo limit.
            Assert.AreEqual(0, TypeBeatModConductor.ABSOLUTE_MIN_RATE, 1e-12);
            Assert.AreEqual(51.0, TypeBeatModConductor.ABSOLUTE_MAX_RATE, 1e-12);

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
        /// The pitch mode's own ceiling (backlog 252). Its path multiplies the track's FREQUENCY, and
        /// BASS caps an absolute frequency at 100 kHz, so a 44.1 kHz song stops speeding up at about
        /// 2.27x while the gameplay clock (which reads the bindable, not the hardware) carries on
        /// accelerating: past the wall the music and the judgement times come apart silently. The
        /// band is therefore pulled in to 2.0x whenever the setting is on, and let back out when it
        /// is off.
        /// </summary>
        [Test]
        public void AdjustingPitchDropsTheBandToWhatTheFrequencyPathCanTrack()
        {
            var mod = new TypeBeatModConductor();

            mod.MaxRate.Value = TypeBeatModConductor.ABSOLUTE_MAX_RATE;
            Assert.AreEqual(51.0, mod.MaxRate.Value, 1e-9);

            mod.AdjustPitch.Value = true;

            foreach (var setting in new[] { mod.MinRate, mod.MaxRate, mod.SpeedChange })
                Assert.AreEqual(TypeBeatModConductor.PITCH_ABSOLUTE_MAX_RATE, setting.MaxValue, 1e-9);

            Assert.AreEqual(2.0, mod.MaxRate.Value, 1e-9,
                "a band the frequency path cannot honour must be pulled in, not silently desynced from the clock");

            mod.AdjustPitch.Value = false;

            foreach (var setting in new[] { mod.MinRate, mod.MaxRate, mod.SpeedChange })
                Assert.AreEqual(TypeBeatModConductor.ABSOLUTE_MAX_RATE, setting.MaxValue, 1e-9);

            Assert.AreEqual(2.0, mod.MaxRate.Value, 1e-9, "the re-clamped value stays where the toggle left it");

            // The FLOOR is the same on both paths: a true zero.
            Assert.AreEqual(0, mod.MinRate.MinValue, 1e-12);
            Assert.AreEqual(0, mod.SpeedChange.MinValue, 1e-12);
        }

        /// <summary>
        /// What the mod does to audio, and the only thing it does: it writes its rate onto the
        /// aggregate the gameplay clock's rate is read off
        /// (<c>GameplayClockExtensions.GetTrueGameplayRate</c> is sign * AggregateFrequency *
        /// AggregateTempo of exactly this component). So this is the clock-level proof that a
        /// controller write moves gameplay time, without standing up a Player.
        ///
        /// <para>Since backlog 252 it is a PAIR of adjustments on the default path, because the band
        /// now reaches under the 0.05 tempo TrackBass throws below: there the tempo is pinned at that
        /// floor and the remainder is handed to the frequency as a power of two, so the product is
        /// still the rate bit for bit. Only a rate of exactly 0 publishes something else, a crawl of
        /// about 1e-4 rather than the zero frequency that would make the framework stop the track.</para>
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

            foreach (double rate in new[] { 51.0, 1.5, 1.0, 0.05, 0.02, 0.0 })
            {
                mod.SpeedChange.Value = rate;

                double tempo = adjustments.AggregateTempo.Value;
                double frequency = adjustments.AggregateFrequency.Value;

                Assert.GreaterOrEqual(tempo, TypeBeatModConductor.TEMPO_FLOOR_RATE,
                    $"TrackBass THROWS on an aggregate tempo below 0.05, and the rate here is {rate}");
                Assert.Greater(frequency, 0,
                    $"a frequency of exactly zero STOPS the track rather than slowing it, and the rate here is {rate}");

                if (rate > 0)
                {
                    Assert.IsTrue((tempo * frequency).Equals(rate),
                        $"the published pair must reconstruct {rate} exactly, got {tempo * frequency:R} ({tempo:R} * {frequency:R})");
                }
                else
                {
                    Assert.AreEqual(TypeBeatModConductor.TEMPO_FLOOR_RATE * TypeBeatModConductor.MIN_FREQUENCY_SCALE,
                        tempo * frequency, 1e-12, "a rate of 0 crawls instead of stopping");
                    Assert.AreEqual(0, (int)Math.Round(tempo * frequency * 100), "...and still reads 0% on the HUD");
                }
            }

            mod.AdjustPitch.Value = true;

            mod.SpeedChange.Value = 1.25;

            Assert.AreEqual(1.25, adjustments.AggregateFrequency.Value, 1e-9);
            Assert.AreEqual(1.0, adjustments.AggregateTempo.Value, 1e-9, "the tempo adjustment leaves the track entirely in pitch mode");
            Assert.AreEqual(1.25, adjustments.AggregateTempo.Value * adjustments.AggregateFrequency.Value, 1e-9);

            mod.SpeedChange.Value = TypeBeatModConductor.ABSOLUTE_MAX_RATE;

            Assert.AreEqual(TypeBeatModConductor.PITCH_ABSOLUTE_MAX_RATE,
                adjustments.AggregateTempo.Value * adjustments.AggregateFrequency.Value, 1e-9,
                "the frequency path is capped where it stops tracking, it does not get the tempo path's ceiling");

            mod.SpeedChange.Value = 0;

            Assert.Greater(adjustments.AggregateFrequency.Value, 0, "still never exactly zero");
            Assert.Less(adjustments.AggregateFrequency.Value, 0.005);
            Assert.AreEqual(1.0, adjustments.AggregateTempo.Value, 1e-9);

            // ...and switching back restores the tempo path without doubling either adjustment up.
            mod.AdjustPitch.Value = false;
            mod.SpeedChange.Value = 3;

            Assert.AreEqual(3.0, adjustments.AggregateTempo.Value, 1e-9);
            Assert.AreEqual(1.0, adjustments.AggregateFrequency.Value, 1e-9);
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
        /// own pace and the skip overlays behave exactly as they do unmodded. The filter is HELD
        /// while that lasts (backlog 253): the player is not typing because there is nothing to type,
        /// which is no evidence at all about their pace, so relaxing toward zero would open the next
        /// line reading them as silent and dip the rate at every line start after a gap.
        /// </summary>
        [Test]
        public void NoActiveLineEasesTheRateBackToNormalAndHoldsTheFilter()
        {
            var running = ConductorState.Initial with { Rate = 1.4, SupplyCellsPerSecond = 8, Authority = 1 };
            var idle = new ConductorInputs(0, 0, null, false, false);

            var state = run(running, idle, tuning(), 100);

            Assert.AreEqual(1.0, state.Rate, 1e-9);
            Assert.AreEqual(8, state.SupplyCellsPerSecond, 1e-9, "the measured pace must survive a gap it says nothing about");
            Assert.AreEqual(1, state.Authority, 1e-9, "authority freezes with the supply it weighs");

            // It EASES: half a second of track time at the slew limit is 0.4, exactly the distance.
            Assert.Greater(run(running, idle, tuning(), 10).Rate, 1.2);

            // A band that excludes 1.00x still wins: the user's floor is a floor even when idling.
            Assert.AreEqual(1.1, run(running, idle, tuning(1.1, 1.5), 100).Rate, 1e-9);
        }

        /// <summary>
        /// FINISHING A LINE EARLY (backlog 253). A player fast enough to run out of characters before
        /// the next line's cue parks the caret past the last cell, where nothing is typeable: the line
        /// is still active, no cell is accepted and there is no judgeable cell to take a phase error
        /// from. Read as ordinary gameplay that is three separate reasons to slow down, which had the
        /// controller hauling the song to the band floor for exactly the player who was ahead of it.
        /// It is the same bypass as no line at all: ease to 1.00x, never under, and hold the filter.
        /// </summary>
        [Test]
        public void FinishingALineEarlyEasesToNormalRatherThanPunishingThePlayer()
        {
            // A converged player running 40% hot: 5.6 characters a second on a line asking for 4.
            var running = run(ConductorState.Initial, typing(5.6, 4), tuning(), 2000);

            Assert.AreEqual(1.4, running.Rate, 1e-9);
            Assert.AreEqual(5.6, running.SupplyCellsPerSecond, 1e-9);

            var state = running;
            var waiting = finishedLine(4);

            for (int i = 0; i < 200; i++)
            {
                state = ConductorController.Step(state, waiting, tuning(), ConductorController.STEP_SECONDS);

                Assert.GreaterOrEqual(state.Rate, 1.0 - 1e-12,
                    $"the song dropped below its own pace at step {i} for a player who was AHEAD");
            }

            Assert.AreEqual(1.0, state.Rate, 1e-9);
            Assert.AreEqual(5.6, state.SupplyCellsPerSecond, 1e-9, "the pace the player actually typed at must be held, not decayed");
            Assert.AreEqual(1, state.Authority, 1e-9);
        }

        /// <summary>
        /// ...and the reason the filter is held rather than relaxed: the next line has to open on the
        /// pace that was really measured. With a decaying filter the four seconds of waiting above
        /// would empty it, the feed-forward would read 0/demand + 1 = 1.00x, and the same typist
        /// resuming at the same speed would be held at 1.00x while the EMA refilled, a dip at the
        /// start of every line that follows a gap.
        /// </summary>
        [Test]
        public void TheNextLineOpensAtThePaceTheFrozenFilterMeasured()
        {
            var running = run(ConductorState.Initial, typing(5.6, 4), tuning(), 2000);
            var held = run(running, finishedLine(4), tuning(), 200);

            Assert.AreEqual(1.0, held.Rate, 1e-9);

            var resumed = ConductorController.Step(held, typing(5.6, 4), tuning(), ConductorController.STEP_SECONDS);

            // The very first step of the new line already asks for 1.4 and gets a full slew step
            // toward it. A collapsed filter would have asked for exactly 1.00x and not moved at all.
            Assert.AreEqual(1.0 + (TypeBeatModConductor.SLEW_PER_SECOND * ConductorController.STEP_SECONDS), resumed.Rate, 1e-9);

            var state = held;

            for (int i = 0; i < 200; i++)
            {
                state = ConductorController.Step(state, typing(5.6, 4), tuning(), ConductorController.STEP_SECONDS);

                Assert.GreaterOrEqual(state.Rate, 1.0 - 1e-12, $"the resumed line dipped below the normal rate at step {i}");
            }

            Assert.AreEqual(1.4, state.Rate, 1e-9, "back to the player's own pace");
        }

        /// <summary>
        /// The guard against overcorrecting. Silence MID-LINE, with characters still in front of the
        /// caret, is the real thing the filter is for: that player is genuinely behind, so the supply
        /// must still decay and the song must still slow down to wait for them. Only the caret having
        /// nowhere left to go buys the freeze.
        /// </summary>
        [Test]
        public void SilenceMidLineStillDecaysTheFilterAndSlowsTheSong()
        {
            var running = run(ConductorState.Initial, typing(5.6, 4), tuning(), 2000);

            // Same shape as the finish-early input except the caret still has cells in front of it,
            // so there IS a judgeable cell and the song is 200 ms past it.
            var stalled = new ConductorInputs(0, 4, -200, true, false);

            var state = run(running, stalled, tuning(), 200);

            Assert.Less(state.SupplyCellsPerSecond, 0.5, "a player with characters left in front of them who types nothing IS behind");
            Assert.AreEqual(1, state.Authority, 1e-9, "the filter is full and trusted here; it is the supply reading that falls");
            Assert.AreEqual(TypeBeatModConductor.DEFAULT_MIN_RATE, state.Rate, 1e-9, "the song must still drop to wait");
        }

        // -----------------------------------------------------------------------------------------
        // Frame pacing (backlog 252). The driver's own decision, extracted so it is pinned without a
        // playfield: how far to advance the fixed-step accumulator, and when the filter is stale.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The region the mod has always shipped in is unchanged: the accumulator advances on TRACK
        /// time, which is the axis a replay agrees on. Under the tempo floor it cannot, because there
        /// track time has stopped and a driver stepping on it stops with it, taking the keypress that
        /// should have lifted the rate with it.
        /// </summary>
        [Test]
        public void PacingStepsOnTrackTimeNormallyAndOnRealTimeWhileParked()
        {
            var normal = ConductorPacing.Decide(16, 16, 1.0);

            Assert.IsFalse(normal.ClearFilter);
            Assert.AreEqual(16, normal.AdvanceMs, 1e-12, "an ordinary frame advances by its own track time");

            // Half speed: the frame is worth half as much of the song, and always was.
            Assert.AreEqual(8, ConductorPacing.Decide(8, 16, 0.5).AdvanceMs, 1e-12);

            // The floor itself is still the normal region; the switch is strictly below it.
            Assert.AreEqual(0.8, ConductorPacing.Decide(0.8, 16, TypeBeatModConductor.TEMPO_FLOOR_RATE).AdvanceMs, 1e-12);

            var parked = ConductorPacing.Decide(0, 16, 0);

            Assert.IsFalse(parked.ClearFilter, "a parked frame must keep the keypresses waiting on it");
            Assert.AreEqual(16, parked.AdvanceMs, 1e-12, "a parked controller has to run on the only clock still moving");

            Assert.AreEqual(16, ConductorPacing.Decide(0.0016, 16, 0.0001).AdvanceMs, 1e-12);
        }

        /// <summary>
        /// The two things that DO invalidate the filter, and the one that used to be confused for
        /// them. A stall is 250 ms of REAL time: measured in track time the same guard fired on every
        /// single frame above roughly 15x, which killed the controller over exactly the part of the
        /// band backlog 252 opened up.
        /// </summary>
        [Test]
        public void PacingClearsOnARewindOrARealStallButNotOnAFastSong()
        {
            Assert.IsTrue(ConductorPacing.Decide(-1, 16, 1).ClearFilter, "a backwards step in track time is a seek");
            Assert.IsTrue(ConductorPacing.Decide(16, 400, 1).ClearFilter, "400 ms of wall time is a hitch, not a frame");
            Assert.AreEqual(250, ConductorPacing.MAX_REAL_FRAME_MS, 1e-12);

            var fast = ConductorPacing.Decide(816, 16, 51);

            Assert.IsFalse(fast.ClearFilter, "816 ms of track time in a 16 ms frame is just 51x, not a stall");
            Assert.AreEqual(816, fast.AdvanceMs, 1e-12);
        }

        /// <summary>
        /// THE RESUME (backlog 252). With the band floor at a true 0 the song can be brought to a
        /// complete stop, and a stopped song freezes the very axis the controller integrates on. The
        /// law reaches an exact 0 (pinned here rather than assumed: the slew lands on the target
        /// rather than approaching it), and from there the pacing seam is the whole of what makes the
        /// floor a door rather than a wall: one accepted keypress, stepped at real-time pace, lifts
        /// the rate off zero and the song starts again.
        /// </summary>
        [Test]
        public void AParkedSongIsStartedAgainByASingleKeypress()
        {
            var band = tuning(TypeBeatModConductor.ABSOLUTE_MIN_RATE, TypeBeatModConductor.DEFAULT_MAX_RATE);

            // A converged typist who then goes silent MID-LINE, with characters still in front of the
            // caret: the one reading that really does mean "behind", so the song slows to wait, and
            // with a floor of 0 it waits all the way to a standstill.
            var state = run(ConductorState.Initial, typing(5.6, 4), band, 2000);

            Assert.AreEqual(1.4, state.Rate, 1e-9);

            state = run(state, new ConductorInputs(0, 4, -200, true, false), band, 400);

            Assert.IsTrue(state.Rate.Equals(0d), $"the law must reach a TRUE zero, got {state.Rate:R}");

            double accumulator = 0;
            double pending = 1;
            int frames = 0;

            while (state.Rate <= 0 && frames < 20)
            {
                frames++;

                // Track time is frozen at rate 0, so this is what every frame looks like: a real
                // 16 ms, and nothing at all of the song.
                var pacing = ConductorPacing.Decide(state.Rate * 16, 16, state.Rate);

                Assert.IsFalse(pacing.ClearFilter, $"the parked frame {frames} threw the waiting keypress away");

                accumulator = Math.Min(accumulator + pacing.AdvanceMs, 8 * ConductorController.STEP_MS);

                while (accumulator >= ConductorController.STEP_MS)
                {
                    accumulator -= ConductorController.STEP_MS;

                    state = ConductorController.Step(state, new ConductorInputs(pending, 4, 0, true, false), band, ConductorController.STEP_SECONDS);
                    pending = 0;
                }
            }

            Assert.Greater(state.Rate, 0, $"the song never restarted, {frames} real-time frames in");
            Assert.LessOrEqual(frames, 3, "one keypress should lift a parked song inside a couple of frames");
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
        /// that wanders either side of the deadband, two stretches with no active line and one where
        /// the line is active but finished. Built off an explicit LCG rather than <c>Random</c> so
        /// the fixture does not depend on the runtime's generator.
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

                // Two stretches with no line at all, and a third where the line is still active but
                // the caret has run off the end of it (a player who finished early and is waiting).
                bool finished = active && i >= 300 && i < 340;

                script[i] = new ConductorInputs(
                    active && !finished && roll > 0.7 ? 1 : 0,
                    3 + (i % 7),
                    active && !finished ? (roll - 0.5) * 400 : null,
                    active,
                    finished);
            }

            return script;
        }
    }
}
