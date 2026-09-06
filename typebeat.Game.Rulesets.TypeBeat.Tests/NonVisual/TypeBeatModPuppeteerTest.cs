// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using NUnit.Framework;
using osu.Framework.Audio;
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
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;
using typebeat.Game.Utils;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The Puppeteer mod (backlog 256): the song strictly FOLLOWS the typing, like a tape reel the
    /// caret drags. Three halves are pinned here.
    ///
    /// <para>THE SHIPPING SURFACE: the acronym the server keys its always-unranked list off, the
    /// unranked flag, the Fun category, the flat 1.0x, the exclusions (every owner of the playback
    /// rate, plus the sibling follower), the hooks it does and does not carry, and the one thing it
    /// does to audio, which is write a FREQUENCY (never a tempo) adjustment onto the aggregate the
    /// gameplay clock's rate is read from.</para>
    ///
    /// <para>THE MODEL: <see cref="PuppeteerClock"/> is a pure function integrated in fixed one
    /// millisecond wall ticks, so the whole thing is driven here with no drawables, no clock and no
    /// audio at all. The determinism and frame-chunking pins are the load-bearing ones: a curve that
    /// has to be re-derivable from a replay's wall-stamped frames must be a function of the schedule
    /// alone and not of the frame rate that happened to sample it.</para>
    ///
    /// <para>FREEPLAY: timing is forgiven (every press on the right character judges Great), and
    /// everything else stays real. Pinned through the REPLAY SCORER, which is also the proof that
    /// the live seam and the replay seam apply the same scale.</para>
    /// </summary>
    [TestFixture]
    public class TypeBeatModPuppeteerTest
    {
        /// <summary>
        /// The FREQUENCY preset, and every law pin in the model section below is written against it,
        /// because it is backlog 256's numbers unchanged: those pins therefore still say exactly what
        /// they always said, to the digit. The TEMPO preset (the shipping default since backlog 258)
        /// differs in two constants and has its own region at the bottom of the model section,
        /// including the proof that the two really do produce different tapes.
        /// </summary>
        private static PuppeteerTuning tuning() => PuppeteerTuning.Frequency;

        /// <summary>A tape at a position and a velocity, with no typing behind it yet.</summary>
        private static PuppeteerState at(double positionMs, double velocity)
            => PuppeteerState.AnchoredAt(positionMs) with { Velocity = velocity };

        // -----------------------------------------------------------------------------------------
        // The shipping surface.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void ReportsUnrankedFunModWithPtAcronym()
        {
            var mod = new TypeBeatModPuppeteer();

            // The DISPLAY name is the retired mod's old one (backlog 257: this is the only follower
            // now), the CLASS name is unchanged, and the ACRONYM is a wire identity that can never
            // move: "PT" is stamped into scores already recorded, and "CT" belongs to
            // TypeBeatModConductor for as long as one of its rows exists.
            Assert.AreEqual("Conductor", mod.Name);
            Assert.AreEqual("PT", mod.Acronym);
            Assert.AreEqual("The song follows you.", mod.Description.ToString());
            Assert.AreEqual(ModType.Fun, mod.Type);
            Assert.IsFalse(mod.Ranked,
                "a song that meets the caret by construction has no timing left to price, so no leaderboard can hold it");
            Assert.IsTrue(mod.HasImplementation);
            Assert.IsNotNull(mod.Icon);

            // A tape one player is pulling cannot be shared with a room.
            Assert.IsFalse(mod.ValidForMultiplayer);
            Assert.IsFalse(mod.ValidForMultiplayerAsFreeMod);

            // ONE setting since backlog 258, where backlog 256 asserted none at all. It is SILENT at
            // its default, which is the house convention: the icon says "PT", and the description
            // earns a line only once the player has actually moved something.
            Assert.IsFalse(mod.AdjustPitch.Value, "TEMPO is the default, so pitch is preserved and there is no scratch");
            Assert.IsEmpty(mod.SettingDescription.ToArray());

            var pitched = new TypeBeatModPuppeteer { AdjustPitch = { Value = true } };

            Assert.IsTrue(pitched.SettingDescription.Any(d => d.setting.ToString() == "Adjust pitch"),
                "the toggle has to be described once it is on: it changes what the mod sounds like AND which preset a stored run re-derives under");
        }

        [Test]
        public void AcronymDoesNotCollideWithAnyOtherRulesetMod()
        {
            var ruleset = new TypeBeatRuleset();

            var acronyms = ruleset.AllMods.Select(m => m.Acronym).ToList();

            Assert.AreEqual(acronyms.Count, acronyms.Distinct().Count(), "two mods share an acronym");
            Assert.AreEqual(1, acronyms.Count(a => a == "PT"));
        }

        [Test]
        public void RulesetSurfacesPuppeteerUnderFun()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.IsTrue(ruleset.GetModsFor(ModType.Fun).Any(m => m is TypeBeatModPuppeteer),
                "Puppeteer must be offered in the mod-select overlay under Fun.");
        }

        [Test]
        public void ScoreMultiplierIsExactlyOne()
        {
            var calculator = new TypeBeatScoreMultiplierCalculator(new ScoreMultiplierContext(new BeatmapDifficulty()));

            Assert.AreEqual(1.0, calculator.CalculateFor(new Mod[] { new TypeBeatModPuppeteer() }), 1e-9);

            // Being unlisted must be neutral, not absorbing.
            Assert.AreEqual(1.05, calculator.CalculateFor(new Mod[] { new TypeBeatModPuppeteer(), new TypeBeatModLiterate() }), 1e-9);
        }

        /// <summary>
        /// Exactly the hooks the mod has, and no more. Unlike the Conductor this one IS allowed to
        /// reach <see cref="TypingEngine.WindowScale"/>, and that is the whole freeplay decision, so
        /// the list below is written as "these and only these" rather than as "nothing that could
        /// affect scoring": if anyone ever reaches for the engine, the score processor or a
        /// difficulty hook to make the feel better, this fails first, because those are the surfaces
        /// with a byte-compatible JS mirror in the web repo.
        /// </summary>
        [Test]
        public void HooksExactlyTheSurfacesItNeedsAndNoMore()
        {
            var mod = new TypeBeatModPuppeteer();

            Assert.IsTrue(mod is IApplicableToTrack);
            Assert.IsTrue(mod is IUpdatableByPlayfield);

            // It IS applied to the drawable ruleset, for two things and two only: multiplying the
            // window scale (freeplay) and publishing the live rate for the HUD readout.
            Assert.IsTrue(mod is IApplicableToDrawableRuleset<TypeBeatHitObject>);

            Assert.IsFalse(mod is IApplicableToScoreProcessor);
            Assert.IsFalse(mod is IApplicableToHealthProcessor);
            Assert.IsFalse(mod is IApplicableToBeatmap);
            Assert.IsFalse(mod is IApplicableToBeatmapConverter);
            Assert.IsFalse(mod is IApplicableToDifficulty);
            Assert.IsFalse(mod is IApplicableFailOverride);
            Assert.IsFalse(mod is ICreateReplayData);
            Assert.IsFalse(mod is IApplicableToDrawableHitObject);

            // NOT ApplyToRate, and not a ModRateAdjust: song select and the star-rating calculator
            // ask for one number that describes the whole play, and a follower has none. They show
            // 1.00x, which is honest, and the replay scorer's rate loop is left the size it was.
            Assert.IsFalse(mod is IApplicableToRate);
            Assert.IsNotInstanceOf<ModRateAdjust>(mod);
            Assert.IsNotInstanceOf<ModTimeRamp>(mod);
            Assert.IsNotInstanceOf<ModAdaptiveSpeed>(mod);

            Assert.AreEqual(3, new TypeBeatRuleset().AllMods.OfType<ModRateAdjust>().Count(),
                "Double Time, Nightcore and Half Time; adding Puppeteer must not enlarge the population the replay scorer's rate seam matches on");
        }

        [Test]
        public void ExcludesEveryOwnerOfThePlaybackRateAndTheSiblingFollower()
        {
            var mod = new TypeBeatModPuppeteer();

            var incompatible = mod.IncompatibleMods;

            Assert.AreEqual(4, incompatible.Length);
            Assert.Contains(typeof(ModRateAdjust), incompatible);
            Assert.Contains(typeof(ModTimeRamp), incompatible);
            Assert.Contains(typeof(ModAdaptiveSpeed), incompatible);
            Assert.Contains(typeof(TypeBeatModConductor), incompatible);

            // ...and the Conductor names Puppeteer too. CheckCompatibleSet reads the relation in
            // both directions, so one side would do; both are declared where both files exist.
            Assert.Contains(typeof(TypeBeatModPuppeteer), new TypeBeatModConductor().IncompatibleMods);

            foreach (var other in new Mod[]
                     {
                         new TypeBeatModConductor(),
                         new TypeBeatModDoubleTime(),
                         new TypeBeatModNightcore(),
                         new TypeBeatModHalfTime(),
                         new ModWindUp(),
                         new ModWindDown(),
                     })
            {
                Assert.IsFalse(ModUtils.CheckCompatibleSet(new[] { (Mod)new TypeBeatModPuppeteer(), other }),
                    $"Puppeteer and {other.Acronym} would fight over the same knob");

                Assert.IsFalse(ModUtils.CheckCompatibleSet(new[] { other, (Mod)new TypeBeatModPuppeteer() }),
                    $"{other.Acronym} and Puppeteer would fight over the same knob (the other order)");
            }

            // ...and it composes with everything that does not touch the rate.
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModPuppeteer(), new TypeBeatModMuted() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModPuppeteer(), new TypeBeatModFlashlight(), new TypeBeatModLiterate() }));
        }

        /// <summary>
        /// WHAT THE MOD DOES TO AUDIO BY DEFAULT (backlog 258): a TEMPO adjustment, so the pitch is
        /// preserved and there is no vinyl scratch. It is published as a PAIR, because
        /// <c>TrackBass</c> THROWS an <c>ArgumentException</c> below an aggregate tempo of 0.05 and
        /// this model's floor is <see cref="TypeBeatModPuppeteer.V_EPSILON"/> (1/512), two orders
        /// under it: the tempo is pinned at the floor and the remainder handed to the frequency as a
        /// POWER OF TWO, so the product reconstructs the command bit for bit. That split is the
        /// retired mod's <see cref="TypeBeatModConductor.TrackAdjustmentsFor"/>, called and not
        /// copied.
        ///
        /// <para>The product is the whole of what the gameplay clock reads:
        /// <c>GameplayClockExtensions.GetTrueGameplayRate</c> is sign * AggregateFrequency *
        /// AggregateTempo of exactly this component.</para>
        /// </summary>
        [Test]
        public void PublishesATempoPairThatReconstructsTheCommandExactly()
        {
            var mod = new TypeBeatModPuppeteer();
            var adjustments = new AudioAdjustments();

            mod.ApplyToTrack(adjustments);

            Assert.AreEqual(1.0, adjustments.AggregateFrequency.Value, 1e-12);
            Assert.AreEqual(1.0, adjustments.AggregateTempo.Value, 1e-12);

            foreach (double rate in new[] { TypeBeatModPuppeteer.V_EPSILON, 0.02, 0.25, 1.0, 1.75, TypeBeatModPuppeteer.V_MAX })
            {
                mod.SpeedChange.Value = rate;

                double tempo = adjustments.AggregateTempo.Value;
                double frequency = adjustments.AggregateFrequency.Value;

                Assert.GreaterOrEqual(tempo, TypeBeatModConductor.TEMPO_FLOOR_RATE,
                    $"TrackBass THROWS on an aggregate tempo below 0.05, and the command here is {rate:R}");

                Assert.Greater(frequency, 0,
                    $"a frequency of exactly zero STOPS the track rather than slowing it, and the command here is {rate:R}");

                Assert.IsTrue((tempo * frequency).Equals(rate),
                    $"the published pair must reconstruct {rate:R} exactly, got {tempo * frequency:R} ({tempo:R} * {frequency:R})");
            }

            // Above the tempo floor the frequency half is exactly 1, which is "pitch preserved" in
            // its plainest form: the whole band a player will ever hear while typing is pure tempo.
            mod.SpeedChange.Value = 1.75;
            Assert.IsTrue(adjustments.AggregateFrequency.Value.Equals(1d));
            Assert.IsTrue(adjustments.AggregateTempo.Value.Equals(1.75));

            // The band is enforced by the bindable, so nothing can publish a rate the audio path
            // cannot track (or the exact zero that STOPS the track rather than slowing it).
            mod.SpeedChange.Value = 0;
            Assert.AreEqual(TypeBeatModPuppeteer.V_EPSILON, adjustments.AggregateTempo.Value * adjustments.AggregateFrequency.Value, 1e-12);

            mod.SpeedChange.Value = 51;
            Assert.AreEqual(TypeBeatModPuppeteer.V_MAX, adjustments.AggregateTempo.Value * adjustments.AggregateFrequency.Value, 1e-12);
        }

        /// <summary>
        /// ...and WITH THE TOGGLE ON it is the frequency path exactly as backlog 256 shipped it: the
        /// whole rate on the frequency, the tempo aggregate at exactly 1, so the resampler bends the
        /// pitch with the speed. The toggle SWAPS the published set rather than adding to it, which
        /// is the retired mod's remove-old / add-new pattern, and the round trip has to leave the
        /// aggregates exactly where a fresh mod would.
        /// </summary>
        [Test]
        public void AdjustPitchPublishesFrequencyOnlyAndTogglingDoesNotDoubleAdd()
        {
            var mod = new TypeBeatModPuppeteer();
            var adjustments = new AudioAdjustments();

            mod.ApplyToTrack(adjustments);

            mod.AdjustPitch.Value = true;

            foreach (double rate in new[] { TypeBeatModPuppeteer.V_EPSILON, 0.25, 1.0, 1.75, TypeBeatModPuppeteer.V_MAX })
            {
                mod.SpeedChange.Value = rate;

                Assert.IsTrue(adjustments.AggregateFrequency.Value.Equals(rate),
                    $"the frequency must carry the whole rate exactly, got {adjustments.AggregateFrequency.Value:R} for {rate:R}");

                Assert.IsTrue(adjustments.AggregateTempo.Value.Equals(1d),
                    $"the tempo aggregate moved to {adjustments.AggregateTempo.Value:R} at rate {rate}: pitch mode publishes frequency ONLY");
            }

            // Back to tempo, and nothing is doubled up: the frequency half returns to exactly 1 and
            // the tempo half carries the whole rate again. A leaked adjustment would show here as a
            // squared rate or a stuck frequency.
            mod.AdjustPitch.Value = false;
            mod.SpeedChange.Value = 1.5;

            Assert.IsTrue(adjustments.AggregateTempo.Value.Equals(1.5));
            Assert.IsTrue(adjustments.AggregateFrequency.Value.Equals(1d));

            // ...and round and round, because the swap has to be idempotent, not merely correct once.
            for (int i = 0; i < 3; i++)
            {
                mod.AdjustPitch.Value = true;
                mod.AdjustPitch.Value = false;
            }

            Assert.IsTrue(adjustments.AggregateTempo.Value.Equals(1.5));
            Assert.IsTrue(adjustments.AggregateFrequency.Value.Equals(1d));

            Assert.AreEqual(TypeBeatModConductor.PITCH_ABSOLUTE_MAX_RATE, TypeBeatModPuppeteer.V_MAX, 1e-12,
                "the frequency path's wall is one fact, and both followers must read the same number for it");
            Assert.AreEqual(TypeBeatModConductor.MIN_FREQUENCY_SCALE, TypeBeatModPuppeteer.V_EPSILON, 1e-12);
        }

        /// <summary>
        /// The submission payload. The acronym is the whole of what the server needs at the defaults,
        /// because the server's only job for this mod is to recognise "PT" in its always-unranked
        /// list. The MODE rides along when it is not the default, by the ordinary mod-settings route,
        /// and that is what makes a stored frequency-mode run re-derivable (backlog 258): a payload
        /// with no <c>adjust_pitch</c> means tempo, which is why the default can never quietly move.
        /// </summary>
        [Test]
        public void WirePayloadIsTheBareAcronymAtTheDefaults()
        {
            Assert.AreEqual(@"{""acronym"":""PT""}", JsonConvert.SerializeObject(new APIMod(new TypeBeatModPuppeteer())));

            var decoded = JsonConvert.DeserializeObject<APIMod>(@"{""acronym"":""PT""}")!.ToMod(new TypeBeatRuleset());

            Assert.IsInstanceOf<TypeBeatModPuppeteer>(decoded, "a stored PT score must not resolve to UnknownMod");
            Assert.IsFalse(decoded.Ranked);
            Assert.IsFalse(((TypeBeatModPuppeteer)decoded).AdjustPitch.Value, "an absent toggle is the tempo mode");

            Assert.IsTrue(PuppeteerTuning.Tempo.Equals(TypeBeatModPuppeteer.TuningFor(new[] { decoded })),
                "...and that is the preset a run stored without the field re-derives under");

            const string pitched_payload = @"{""acronym"":""PT"",""settings"":{""adjust_pitch"":true}}";

            Assert.AreEqual(pitched_payload,
                JsonConvert.SerializeObject(new APIMod(new TypeBeatModPuppeteer { AdjustPitch = { Value = true } })));

            var pitched = JsonConvert.DeserializeObject<APIMod>(pitched_payload)!.ToMod(new TypeBeatRuleset());

            Assert.IsTrue(((TypeBeatModPuppeteer)pitched).AdjustPitch.Value, "the mode has to survive storage or the tape cannot be re-derived");

            Assert.IsTrue(PuppeteerTuning.Frequency.Equals(TypeBeatModPuppeteer.TuningFor(new[] { pitched })));
        }

        // -----------------------------------------------------------------------------------------
        // The model. Pure: no drawables, no clock, no audio.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Integrate one wall millisecond at a time, reading the arm at each integral wall
        /// millisecond, and record every state. THE TAPE IS ASSERTED MONOTONIC HERE, so every test
        /// that walks a trajectory through this helper carries the no-rewind pin for free.
        /// </summary>
        private static PuppeteerState[] trajectory(PuppeteerState start, Func<int, PuppeteerArm> armAtMs, int wallMs)
            => trajectory(tuning(), start, armAtMs, wallMs);

        /// <summary>The same, under a named preset. See <see cref="tuning"/> for which one the law pins use.</summary>
        private static PuppeteerState[] trajectory(PuppeteerTuning preset, PuppeteerState start, Func<int, PuppeteerArm> armAtMs, int wallMs)
        {
            var states = new PuppeteerState[wallMs + 1];
            states[0] = start;

            for (int ms = 1; ms <= wallMs; ms++)
            {
                states[ms] = PuppeteerClock.Step(states[ms - 1], armAtMs(ms), preset);

                Assert.GreaterOrEqual(states[ms].PositionMs, states[ms - 1].PositionMs,
                    $"the tape moved BACKWARDS at wall ms {ms}, which no arm schedule may ever make it do");
            }

            return states;
        }

        /// <summary>A caret advancing perfectly smoothly at <paramref name="pace"/> track ms per wall ms, from <paramref name="from"/>.</summary>
        private static Func<int, PuppeteerArm> steadyTypist(double pace, double from = 0)
            => ms => new PuppeteerArm(from + (pace * ms), TypeBeatModPuppeteer.V_MAX);

        /// <summary>
        /// THE STEADY STATE. A player typing at pace p settles the reel at exactly v = p, trailing
        /// their caret by exactly p * T_CHASE_MS: the chase horizon IS the lag, which is the whole
        /// reason this mod forgives timing rather than judging it.
        /// </summary>
        [Test]
        public void TypingAtASteadyPaceSettlesTheReelAtThatPaceATrailingChaseGapBehind()
        {
            foreach (double pace in new[] { 0.5, 1.0, 1.6 })
            {
                var settled = trajectory(PuppeteerState.AnchoredAt(0), steadyTypist(pace), 6000)[6000];

                Assert.AreEqual(pace, settled.Velocity, 1e-9,
                    $"the reel did not settle on the player's own pace of {pace}");

                // The pace ESTIMATE settles there too, which is what lifts the typing-sustained cap
                // out of the way and leaves the chase horizon deciding the steady state (backlog
                // 257). At 1.6 that cap would be 2.4 and is held at the hardware ceiling instead.
                Assert.AreEqual(pace, settled.PaceVelocity, 1e-9,
                    $"the pace estimate did not settle on the player's own pace of {pace}");

                Assert.Greater(PuppeteerClock.TypingSustainedCap(settled.PaceVelocity, tuning()), pace,
                    "a typist's own cap must not bind on them, or the tape could never close a gap");

                // Measured as the model itself measures it: the arm at wall ms n is read BEFORE
                // that tick advances the position, so the gap the law sees at ms 6000 is against
                // the position at 5999. Against the same-index position it is one tick of travel
                // less, which is the discretisation and not a different steady state.
                var previous = trajectory(PuppeteerState.AnchoredAt(0), steadyTypist(pace), 5999)[5999];

                Assert.AreEqual(pace * TypeBeatModPuppeteer.T_CHASE_MS, (pace * 6000) - previous.PositionMs, 1e-6,
                    $"the trailing gap at pace {pace} is not the chase horizon");
            }

            // At map pace the lag is a flat 150 ms of song, which is precisely the permanent "early"
            // that WINDOW_SCALE exists to stop leaking into every grade.
            Assert.AreEqual(150, TypeBeatModPuppeteer.T_CHASE_MS, 1e-12);
        }

        /// <summary>
        /// HESITATE AND IT DRAGS TO A STOP. The target freezes the instant the caret does, so the
        /// gap is eaten, the requested velocity falls with it and the reel winds down. There is no
        /// "player stopped" branch anywhere: a frozen target IS the whole behaviour.
        ///
        /// <para>IT DOES RUN A LITTLE PAST THE CARET, and it must: a smoothed velocity cannot be
        /// zero at the instant the gap closes, so the reel carries its remaining momentum for about
        /// one smoothing constant. Measured at 61 ms of song from a settled 1.00x, which is a
        /// fraction of the trailing gap it just ate and inaudible as a position. Zero overshoot
        /// would need a velocity that can be commanded to stop instantly, which is the one thing
        /// SMOOTHING_TAU_MS exists to forbid. What is NOT allowed is the tape going backwards, and
        /// the trajectory helper asserts that on every step.</para>
        /// </summary>
        [Test]
        public void StoppingTypingWindsTheReelDownToTheCrawlWithoutRunningPastTheCaret()
        {
            const int wall_ms = 4000;
            const double target = 1000;

            var settled = trajectory(PuppeteerState.AnchoredAt(0), steadyTypist(1), 6000)[6000];

            // The caret freezes exactly where it is; the target stops moving and nothing else changes.
            var states = trajectory(settled with { PositionMs = target - TypeBeatModPuppeteer.T_CHASE_MS },
                _ => new PuppeteerArm(target, TypeBeatModPuppeteer.V_MAX), wall_ms);

            for (int ms = 2; ms <= wall_ms; ms++)
            {
                Assert.Less(states[ms].Velocity, states[ms - 1].Velocity,
                    $"the reel did not wind DOWN at wall ms {ms}: a tape stop must be monotonic, not a wobble");
            }

            Assert.AreEqual(TypeBeatModPuppeteer.V_EPSILON, states[wall_ms].Velocity, 1e-9,
                "the reel must reach the crawl, and the crawl is never zero");

            double overshoot = states[wall_ms].PositionMs - target;

            Assert.Greater(overshoot, -1,
                "the tape has to actually ARRIVE at the caret's target, not stall short of it");

            Assert.Less(overshoot, TypeBeatModPuppeteer.T_CHASE_MS / 2,
                "the reel's momentum must not carry it anywhere near the gap it had just closed");

            // Once it has stopped it only CRAWLS: the remaining four seconds of the run are worth a
            // few milliseconds of song, because the floor is 1/512 rather than a taste value.
            var parked = trajectory(states[wall_ms], _ => new PuppeteerArm(target, TypeBeatModPuppeteer.V_MAX), wall_ms);

            Assert.Less(parked[wall_ms].PositionMs - states[wall_ms].PositionMs, (TypeBeatModPuppeteer.V_EPSILON * wall_ms) + 1e-9,
                "a parked tape may only crawl, and only ever forwards");
        }

        /// <summary>
        /// ...and TYPE AGAIN AND IT SPINS BACK UP, over the smoothing constant and not instantly.
        /// With a target far enough ahead that the request is pinned at the cap for the whole run,
        /// the filter is exact: one time constant covers 1 - 1/e of the distance, whatever the
        /// starting velocity was.
        ///
        /// <para>WHICH CAP is the backlog 257 half of this. A target sitting still miles ahead is not
        /// typing, however big the gap it opens, so the pace estimate reads zero and the reel spins
        /// up to the song's own speed and no further. Give it a caret that is actually MOVING and the
        /// same spin-up runs to the hardware ceiling.</para>
        /// </summary>
        [Test]
        public void ResumingSpinsTheReelBackUpOverTheSmoothingConstant()
        {
            var parked = at(0, TypeBeatModPuppeteer.V_EPSILON);

            int tau = (int)TypeBeatModPuppeteer.SMOOTHING_TAU_MS;

            // A target 100 seconds ahead and STILL: the requested velocity is pinned at the cap
            // throughout, so the only thing moving is the filter.
            var arm = new PuppeteerArm(100000, TypeBeatModPuppeteer.V_MAX);

            var states = trajectory(parked, _ => arm, tau);

            double expectedIdle = 1 + ((TypeBeatModPuppeteer.V_EPSILON - 1) * Math.Exp(-1));

            Assert.AreEqual(expectedIdle, states[tau].Velocity, 1e-9,
                "one smoothing time constant must cover exactly 1 - 1/e of the way to the cap, and with nobody typing the cap is 1.00x");

            // Not a step: a single millisecond moves it by well under a hundredth of the distance.
            Assert.Less(states[1].Velocity, 0.03);

            // ...and with a caret genuinely running away at the ceiling, the same filter runs to the
            // same 1 - 1/e of the way to V_MAX. The pace estimate needs a moment to believe it, so
            // this is measured from a caret that has been moving for a while rather than from cold.
            var typed = trajectory(parked, ms => new PuppeteerArm(100000 + (TypeBeatModPuppeteer.V_MAX * ms), TypeBeatModPuppeteer.V_MAX), 4000);

            Assert.AreEqual(TypeBeatModPuppeteer.V_MAX, typed[4000].Velocity, 1e-6,
                "a caret really moving at the ceiling must lift the cap all the way to it");

            Assert.Greater(typed[tau].Velocity, states[tau].Velocity,
                "the typing-sustained cap has to be the only difference between these two runs");
        }

        /// <summary>
        /// THE TAPE NEVER REWINDS. The caret is not monotonic (backspace, ctrl-backspace and a
        /// retype selection all move it backwards), so the desired position really does step back,
        /// and it needs no special case: the velocity clamp's LOWER bound is the crawl, never the
        /// requested velocity, so a negative gap asks for the crawl and gets it.
        /// </summary>
        [Test]
        public void ABackspacedCaretParksTheTapeAndNeverRewindsIt()
        {
            var settled = trajectory(PuppeteerState.AnchoredAt(0), steadyTypist(1), 4000)[4000];

            // The player backspaces a whole word: the caret target jumps 800 ms BEHIND the tape.
            double behind = settled.PositionMs - 800;

            var states = trajectory(settled, _ => new PuppeteerArm(behind, TypeBeatModPuppeteer.V_MAX), 2000);

            // trajectory() already asserts the position never decreases. What it cannot see is that
            // the velocity pins to the floor rather than going negative and being clamped later.
            // The filter approaches the floor asymptotically (exp(-2000/120) of the way short),
            // which is why this is a tolerance and not an Equals.
            Assert.AreEqual(TypeBeatModPuppeteer.V_EPSILON, states[2000].Velocity, 1e-6);

            for (int ms = 1; ms <= 2000; ms++)
            {
                Assert.GreaterOrEqual(states[ms].Velocity, TypeBeatModPuppeteer.V_EPSILON - 1e-12,
                    $"the velocity went under the floor at wall ms {ms}");
            }

            // It carries its momentum into the park (about a smoothing constant's worth of the
            // velocity it had, the same physics as the tape stop above), and then it only CRAWLS.
            Assert.Less(states[1000].PositionMs - settled.PositionMs, TypeBeatModPuppeteer.T_CHASE_MS,
                "the reel must spend its momentum, not keep rolling on a target behind it");

            Assert.Less(states[2000].PositionMs - states[1500].PositionMs, (TypeBeatModPuppeteer.V_EPSILON * 500) + 1e-3,
                "a parked tape crawls; it does not drift");

            // ...and the moment the player retypes past the playhead it picks straight back up.
            var resumed = trajectory(states[2000], ms => new PuppeteerArm(states[2000].PositionMs + 200 + ms, TypeBeatModPuppeteer.V_MAX), 1500);

            Assert.Greater(resumed[1500].Velocity, 0.9, "the reel must spin back up once the caret is ahead again");
        }

        /// <summary>
        /// OFF A LINE THE SONG JUST PLAYS, at exactly 1.00x, for as long as it takes (backlog 257,
        /// and it is the owner's rule verbatim: "for instrumental not on line sections just play the
        /// song normal speed"). The coast arm has no position term at all, so the intro, the tail of
        /// a finished line, an instrumental gap and the outro are all one behaviour, they all sound
        /// exactly as they do unmodded, and none of them can park, sprint or drift.
        ///
        /// <para>Before this the coast aimed at the next line's first vocal and eased into a park
        /// there. That put a slow-down and a stop in the middle of every instrumental break, which is
        /// what the flat cap here replaces. Parking in front of an untyped vocal is still done, by
        /// the ACTIVE arm, from the line's cue: see
        /// <see cref="AnUntypedApproachRunsAtTheSongsOwnSpeedAndParksOnTheCaretCell"/>.</para>
        ///
        /// <para>BACKLOG 261 NARROWS THIS TO A COLD TAPE, which is the case above and every case a
        /// player who is not outrunning the song ever sees: the intro (the anchor starts at velocity
        /// 1), an on-pace player, and a hesitating one, whose tape is dragged BELOW 1.00x and is sped
        /// back UP to it. A tape that was running FASTER than the song when the line ran out now
        /// holds that speed instead of easing down, which is the tail of this test and the whole of
        /// the held-coast region below.</para>
        /// </summary>
        [Test]
        public void ACoastingTapePlaysTheSongAtExactlyItsOwnSpeedAndNeverParks()
        {
            var engine = twoLineEngine();

            // Drive the engine to the pre-roll, where no line is active at all: that is a coast arm
            // by way of the production ArmFor, so this pin fails if the flat cap is taken off there.
            engine.Update(0);

            var arm = TypeBeatModPuppeteer.ArmFor(engine, 0);

            Assert.AreEqual(TypeBeatModPuppeteer.COAST_MAX_VELOCITY, arm.VelocityCap, 1e-12,
                "the intro coasts at the song's own speed");

            Assert.AreEqual(double.PositiveInfinity, arm.DesiredPositionMs,
                "a coast arm must have no position term: there is nothing on screen for the tape to be pulled toward");

            var states = trajectory(at(0, 1), _ => arm, 30000);

            for (int ms = 1; ms <= 30000; ms++)
            {
                Assert.AreEqual(1.0, states[ms].Velocity, 1e-12,
                    $"the coast was not flat at wall ms {ms}: it ran at {states[ms].Velocity:R}");
            }

            // Thirty seconds of wall time is thirty seconds of song, to the millisecond, however far
            // away the next line happens to be.
            Assert.AreEqual(30000, states[30000].PositionMs, 1e-6);

            // A tape BELOW the song's own speed (a player who was hesitating, so the reel had dragged
            // toward the crawl) is brought back UP to exactly 1.00x, and never further: the hold's
            // floor is what says a coast may never be slower than the song, and its value is what
            // says it may never be faster than the tape already was.
            var cold = trajectory(at(0, TypeBeatModPuppeteer.V_EPSILON), _ => arm, 4000);

            for (int ms = 1; ms <= 4000; ms++)
            {
                Assert.GreaterOrEqual(cold[ms].Velocity, cold[ms - 1].Velocity - 1e-12,
                    $"a coast out of a park has to spin UP, and it slowed at wall ms {ms}");

                Assert.LessOrEqual(cold[ms].Velocity, 1 + 1e-12,
                    $"a coast out of a park overshot the song's own speed at wall ms {ms} ({cold[ms].Velocity:R})");
            }

            Assert.AreEqual(TypeBeatModPuppeteer.COAST_MAX_VELOCITY, cold[4000].Velocity, 1e-6);

            // ...and a tape that was still sprinting when the line ended KEEPS that speed since
            // backlog 261, rather than easing back down to 1.00x. This used to be the ease-down pin,
            // and it is re-expected here rather than deleted because it is the exact trajectory the
            // owner asked to change: see TheGapFloorIsTheSpeedTheTapeArrivedAt below for the law and
            // TheOutroReleasesTheHoldWhileAMidMapGapKeepsIt for the one coast that still eases. On a
            // pure coast there is no position term, so the tape runs at the floor exactly.
            var hot = trajectory(at(0, TypeBeatModPuppeteer.V_MAX), _ => arm, 2000);

            for (int ms = 1; ms <= 2000; ms++)
            {
                Assert.AreEqual(TypeBeatModPuppeteer.V_MAX, hot[ms].Velocity, 1e-12,
                    $"the floored coast was not flat at wall ms {ms}: it ran at {hot[ms].Velocity:R}");
            }

            Assert.AreEqual(TypeBeatModPuppeteer.V_MAX * 2000, hot[2000].PositionMs, 1e-6,
                "two wall seconds of floored coast are two seconds of song at the floor's speed");
        }

        /// <summary>
        /// DETERMINISM, and the reason the integration cadence is a contract rather than a tuning
        /// knob. A trajectory is a function of the (wall millisecond -&gt; arm) schedule and NOTHING
        /// else: run the same key schedule twice and the states are bit identical, and chop the same
        /// schedule into 16 ms frames or 5 ms frames or single milliseconds and the answer does not
        /// move, because every arm is applied at its own integral wall millisecond.
        ///
        /// <para>This is what phase 2 is built on: a stored replay's frames are wall stamped, so
        /// re-running this model over them re-derives the player's own curve rather than the
        /// watcher's frame rate's.</para>
        /// </summary>
        [Test]
        public void TheSameKeyScheduleAlwaysProducesTheSameTapeWhateverTheFrameRateWas()
        {
            var schedule = keySchedule();
            const int wall_ms = 9000;

            var first = trajectory(PuppeteerState.AnchoredAt(0), schedule, wall_ms);
            var second = trajectory(PuppeteerState.AnchoredAt(0), schedule, wall_ms);

            for (int ms = 0; ms <= wall_ms; ms++)
            {
                Assert.IsTrue(first[ms].Equals(second[ms]),
                    $"the tape diverged at wall ms {ms}: {first[ms]} vs {second[ms]}");
            }

            // ...and the frame rate that sampled the schedule is not part of the answer. A key
            // landing at wall ms 1234 lands there whether the frame it arrived in was 5 ms or 33 ms
            // long, because the arm is applied at its own millisecond and the ticks are canonical.
            var canonical = first[wall_ms];

            foreach (int frameMs in new[] { 1, 5, 16, 33, 100 })
            {
                var chunked = runChunked(PuppeteerState.AnchoredAt(0), schedule, wall_ms, frameMs);

                Assert.IsTrue(chunked.Equals(canonical),
                    $"integrating the same schedule in {frameMs} ms frames gave {chunked} instead of {canonical}");
            }

            // Not vacuous: the schedule really does move the reel around.
            double slowest = first.Min(s => s.Velocity);
            double fastest = first.Max(s => s.Velocity);

            Assert.Greater(fastest - slowest, 0.5, $"the scripted play barely moved the tape ({slowest:R} to {fastest:R})");

            // ...and since backlog 261 it carries a GAP FLOOR through its tail, so everything above
            // is a determinism pin on a trajectory that has one in it. The scripted typist is running
            // the tape above 1.00x when the line runs out at wall ms 6500, and the floor is that
            // velocity, captured once and then carried for the remaining two and a half seconds.
            Assert.AreEqual(PuppeteerClock.NO_HELD_FLOOR, first[6499].HeldFloorVelocity, 1e-12,
                "a typing arm that never coasted carries no floor");

            double floor = first[6500].HeldFloorVelocity;

            Assert.Greater(floor, 1.2, $"the scripted typist was only running the tape at {floor:R}, so the coast tail floors at nothing worth pinning");

            for (int ms = 6500; ms <= wall_ms; ms++)
            {
                Assert.AreEqual(floor, first[ms].HeldFloorVelocity, 1e-12, $"the floor moved at wall ms {ms}");
                Assert.AreEqual(floor, first[ms].Velocity, 1e-12, $"the coast was not flat at its floor at wall ms {ms}");
            }
        }

        /// <summary>
        /// The same schedule, integrated by a driver whose frames are <paramref name="frameMs"/>
        /// long. A frame is split at every arm change so the arm still takes effect at its own
        /// integral wall millisecond, which is exactly what the live driver does with the ticks it
        /// accumulates; the runs in between go through <see cref="PuppeteerClock.Run"/>, which is
        /// the surface that must stay equal to repeated <see cref="PuppeteerClock.Step"/>.
        /// </summary>
        private static PuppeteerState runChunked(PuppeteerState start, Func<int, PuppeteerArm> armAtMs, int wallMs, int frameMs)
        {
            var state = start;
            int ms = 1;

            while (ms <= wallMs)
            {
                int frameEnd = Math.Min(wallMs, (((ms - 1) / frameMs) + 1) * frameMs);

                while (ms <= frameEnd)
                {
                    var arm = armAtMs(ms);
                    int ticks = 0;

                    while (ms + ticks <= frameEnd && armAtMs(ms + ticks).Equals(arm))
                        ticks++;

                    state = PuppeteerClock.Run(state, arm, tuning(), ticks);
                    ms += ticks;
                }
            }

            return state;
        }

        /// <summary>
        /// A reproducible stand-in for a play: a caret that steps forward on each key of a burst,
        /// stalls while the player hesitates, steps BACKWARDS over a backspaced word, and then runs
        /// out of line into a coast. Built off an explicit LCG rather than <c>Random</c> so the
        /// fixture does not depend on the runtime's generator, and quantised to whole wall
        /// milliseconds because that is the model's own unit.
        /// </summary>
        private static Func<int, PuppeteerArm> keySchedule()
        {
            var target = new double[9001];
            var cap = new double[9001];

            ulong seed = 0x2545F4914F6CDD1DUL;
            double caret = 0;
            double nextKeyAt = 0;

            for (int ms = 0; ms <= 9000; ms++)
            {
                // A silent stretch (the player hesitates), then a backspaced word, then the line
                // runs out and the tape coasts toward the next vocal.
                bool hesitating = ms >= 3000 && ms < 4200;
                bool backspacing = ms >= 4200 && ms < 4260;
                bool coasting = ms >= 6500;

                if (backspacing && ms == 4200)
                    caret -= 700;

                if (!hesitating && !backspacing && !coasting && ms >= nextKeyAt)
                {
                    seed = (seed * 6364136223846793005UL) + 1442695040888963407UL;
                    double roll = (seed >> 40) / (double)(1 << 24);

                    caret += 90 + (roll * 160);
                    nextKeyAt = ms + 60 + (roll * 90);
                }

                target[ms] = coasting ? PuppeteerArm.Coast.DesiredPositionMs : caret;
                cap[ms] = coasting ? TypeBeatModPuppeteer.COAST_MAX_VELOCITY : TypeBeatModPuppeteer.V_MAX;
            }

            return ms => new PuppeteerArm(target[ms], cap[ms]);
        }

        // -----------------------------------------------------------------------------------------
        // The arms: where the model meets the engine's real line lifecycle.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The two arms, read off a real engine. ON a line the target is the caret cell's own; OFF
        /// one, in every sense of off (no line yet, a line the caret has finished, past the last
        /// line), it is the one flat coast.
        ///
        /// <para>The middle case used to be the subtle one: a finished line aimed at
        /// <c>ActiveLineIndex + 1</c> and specifically NOT at
        /// <see cref="TypingEngine.NextUnsealedLineIndex"/>, because a finished line has not SEALED
        /// yet, so the next-unsealed index is still that same line and its first vocal is already
        /// behind the tape. Backlog 257 deleted the question along with the target: the engine's line
        /// lifecycle is not consulted by the arm at all any more, only its caret.</para>
        ///
        /// <para>Backlog 261 puts ONE lifecycle read back, and it is worth being precise about which:
        /// <see cref="TypingEngine.IsFinished"/>, to tell the outro from every other off-line stretch
        /// (see <see cref="PastTheLastLineTheTapeSimplyPlaysOutAtTheSongsOwnSpeed"/>). Not an index,
        /// so the trap above cannot come back. Both cases below are mid-map and both are therefore
        /// still <see cref="PuppeteerArm.Coast"/> exactly, which is what these value-equality
        /// assertions say: the arm grew a field and the pre-roll and the finished-line coasts did not
        /// move.</para>
        /// </summary>
        [Test]
        public void ArmsFollowTheEnginesOwnLineLifecycle()
        {
            var engine = twoLineEngine();

            double firstVocal = engine.Lines[0].FirstVocalTime;

            // 1. The pre-roll: no line active at all, so coast.
            engine.Update(0);

            Assert.AreEqual(-1, engine.ActiveLineIndex);

            Assert.IsTrue(PuppeteerArm.Coast.Equals(TypeBeatModPuppeteer.ArmFor(engine, 0)),
                "the intro plays the song at its own speed");

            // 2. A live caret on the first line: chase that cell's own target, uncapped by the arm.
            engine.Update(firstVocal);

            Assert.AreEqual(0, engine.ActiveLineIndex);
            Assert.IsFalse(engine.IsLineComplete);

            var typing = TypeBeatModPuppeteer.ArmFor(engine, firstVocal);

            Assert.AreEqual(engine.Lines[0].Cells[engine.CaretIndex].TargetTime, typing.DesiredPositionMs, 1e-9);
            Assert.AreEqual(TypeBeatModPuppeteer.V_MAX, typing.VelocityCap, 1e-12);

            // 3. The line finished early: the caret runs off the end while the line is still active.
            foreach (char c in "abc")
                Assert.IsTrue(engine.ProcessKey(c, firstVocal), $"'{c}' was refused by the engine");

            engine.Update(firstVocal + 10);

            Assert.AreEqual(0, engine.ActiveLineIndex);
            Assert.IsTrue(engine.IsLineComplete);
            Assert.AreEqual(0, engine.NextUnsealedLineIndex, "the finished line has not sealed yet, which used to be the whole trap");

            Assert.IsTrue(PuppeteerArm.Coast.Equals(TypeBeatModPuppeteer.ArmFor(engine, firstVocal + 10)),
                "a finished line plays out at the song's own speed, and no index is consulted to decide that");
        }

        /// <summary>
        /// The outro: past the last line the song plays itself out at its own speed, which is
        /// almost the same coast arm as every other off-line stretch. An infinite target is not a
        /// NaN generator, it simply pins the request at the cap.
        ///
        /// <para>ALMOST, since backlog 261: this is the one coast that RELEASES a hold rather than
        /// carrying it (<see cref="PuppeteerArm.ReleasedCoast"/>). Everywhere else the held speed
        /// ends when the next line takes the caret; here there is no next line, so a hold would
        /// simply play the rest of the song fast forever. A COLD tape cannot tell the two arms apart,
        /// which is why the original body below still stands word for word, and a HOT one is the
        /// whole difference.</para>
        /// </summary>
        [Test]
        public void PastTheLastLineTheTapeSimplyPlaysOutAtTheSongsOwnSpeed()
        {
            var engine = twoLineEngine();

            engine.Update(0);

            foreach (var line in engine.Lines)
            {
                engine.Update(line.FirstVocalTime);

                foreach (char c in "abc")
                    engine.ProcessKey(c, line.FirstVocalTime);
            }

            engine.Update(60000);

            Assert.AreEqual(-1, engine.NextUnsealedLineIndex, "every line has sealed");
            Assert.IsTrue(engine.IsFinished, "...which is the one engine fact the outro arm reads");

            var outro = TypeBeatModPuppeteer.ArmFor(engine, 60000);

            Assert.AreEqual(double.PositiveInfinity, outro.DesiredPositionMs);
            Assert.AreEqual(TypeBeatModPuppeteer.COAST_MAX_VELOCITY, outro.VelocityCap, 1e-12);
            Assert.IsTrue(outro.ReleasesHold, "the map is over, so there is no next line to give the held speed back at");

            var states = trajectory(at(60000, 1), _ => outro, 2000);

            Assert.AreEqual(1.0, states[2000].Velocity, 1e-9);
            Assert.AreEqual(62000, states[2000].PositionMs, 1e-6);

            // ...and a HOT tape, the player having sprinted the last line, eases back down to the
            // song's own speed rather than holding: the ending is heard as it was written.
            var hot = trajectory(at(60000, TypeBeatModPuppeteer.V_MAX), _ => outro, 2000);

            for (int ms = 1; ms <= 2000; ms++)
            {
                Assert.LessOrEqual(hot[ms].Velocity, hot[ms - 1].Velocity + 1e-12,
                    $"the outro sped back up at wall ms {ms}");
            }

            Assert.AreEqual(TypeBeatModPuppeteer.COAST_MAX_VELOCITY, hot[2000].Velocity, 1e-6);
            Assert.Greater(hot[1].Velocity, 1.9, "and it eases rather than snapping");

            Assert.AreEqual(PuppeteerClock.NO_HELD_FLOOR, hot[2000].HeldFloorVelocity, 1e-12,
                "a released coast must carry no hold at all, or the ease would be undone the next tick");
        }

        /// <summary>
        /// The model and a real engine, one canonical tick at a time, which is exactly the cadence
        /// <c>PuppeteerReplayTransform</c> co-simulates at. Asserts the tape's monotonicity on every
        /// step, as <c>trajectory</c> does.
        /// </summary>
        private static PuppeteerState[] against(TypingEngine engine, PuppeteerState start, int wallMs)
            => against(engine, start, wallMs, null);

        /// <summary>
        /// The same, with keystrokes delivered at chosen WALL milliseconds and fed at the tape's own
        /// position, which is the order the live driver runs in: the engine is advanced to the tape,
        /// the frame's keys are judged there, and the arm read afterwards sees the caret they moved.
        /// </summary>
        private static PuppeteerState[] against(TypingEngine engine, PuppeteerState start, int wallMs, IReadOnlyDictionary<int, char>? keys)
        {
            var states = new PuppeteerState[wallMs + 1];
            states[0] = start;

            for (int ms = 1; ms <= wallMs; ms++)
            {
                double position = states[ms - 1].PositionMs;

                engine.Update(position);

                if (keys != null && keys.TryGetValue(ms, out char character))
                    Assert.IsTrue(engine.ProcessKey(character, position), $"'{character}' was refused at wall ms {ms}");

                states[ms] = PuppeteerClock.Step(states[ms - 1], TypeBeatModPuppeteer.ArmFor(engine, position), tuning());

                Assert.GreaterOrEqual(states[ms].PositionMs, position,
                    $"the tape moved BACKWARDS at wall ms {ms}, which no arm schedule may ever make it do");
            }

            return states;
        }

        /// <summary>
        /// APPROACHING AN UNTYPED CELL IS NOT A SPRINT (backlog 257). A line is activated a cue lead
        /// (<see cref="TypingEngine.CUE_LEAD_MS"/>, 1500 ms) before its first vocal, so the moment
        /// the caret is in hand the raw chase request is <c>1500 / 150</c>, which is the hardware
        /// ceiling. Under the old flat cap the tape sprinted the cue lead at 2.00x and arrived early,
        /// parking mid-instrumental. Nobody has typed anything, so the typing-sustained cap is 1.00x
        /// and the song simply flows up to the vocal and parks ON the caret cell.
        /// </summary>
        [Test]
        public void AnUntypedApproachRunsAtTheSongsOwnSpeedAndParksOnTheCaretCell()
        {
            var engine = twoLineEngine();

            double activation = engine.Lines[0].ActivationTime;
            double firstVocal = engine.Lines[0].FirstVocalTime;

            Assert.AreEqual(TypingEngine.CUE_LEAD_MS, firstVocal - activation, 1e-9,
                "this pin is about the cue lead, so the fixture has to actually have one");

            var states = against(engine, PuppeteerState.AnchoredAt(0), 8000);

            for (int ms = 1; ms <= 8000; ms++)
            {
                Assert.LessOrEqual(states[ms].Velocity, 1 + 1e-12,
                    $"the approach sprinted to {states[ms].Velocity:R} at wall ms {ms}, with nobody typing a thing");
            }

            // The coast up to the cue is exact, so the line is in hand at its own activation time.
            Assert.AreEqual(activation, states[(int)activation].PositionMs, 1e-6);

            // ...and then the tape eases into a park ON the first cell, not before it and not past
            // it. The overshoot bound is the same momentum a tape stop carries.
            Assert.AreEqual(firstVocal, states[8000].PositionMs, TypeBeatModPuppeteer.T_CHASE_MS / 2,
                "the tape must park on the untyped cell, waiting for the player");

            Assert.Less(states[8000].Velocity, 0.01, "and it must ease into that park rather than arrive at speed");

            // NEVER STUCK: one keypress moves it again. The caret steps to the next cell, the gap
            // reopens and the reel spins back up.
            Assert.IsTrue(engine.ProcessKey('a', states[8000].PositionMs));

            var typed = against(engine, states[8000], 2000);

            Assert.Greater(typed[2000].PositionMs, states[8000].PositionMs + 500,
                "a keypress on a parked tape has to start the song again");
        }

        /// <summary>
        /// A LINE HAND-OVER IS A BLIP, NOT A SPRINT LICENCE. The caret leaves one line and arrives on
        /// the next THROUGH the coast arm, whose target is unreachable, so the pace estimate has no
        /// baseline when the next line's cell arrives and takes the new target as one rather than
        /// crediting the thousands of milliseconds between them as typing. Credit them and the cap
        /// would jump to the ceiling on a play where nobody has touched the keyboard.
        /// </summary>
        [Test]
        public void ALineHandoverReadsAsABlipAndNotAsTyping()
        {
            const double handover_at = 500;
            const double next_cell = 6000;

            // Coast for half a second, then the next line's first cell arrives three thousand
            // milliseconds ahead of where the tape has got to.
            var states = trajectory(PuppeteerState.AnchoredAt(3000),
                ms => ms < handover_at ? PuppeteerArm.Coast : new PuppeteerArm(next_cell, TypeBeatModPuppeteer.V_MAX), 2000);

            for (int ms = 1; ms <= 2000; ms++)
            {
                Assert.AreEqual(0, states[ms].PaceVelocity, 1e-12,
                    $"the hand-over was credited as typing at wall ms {ms} ({states[ms].PaceVelocity:R} ms of caret travel per wall ms, from a keyboard nobody touched)");

                Assert.LessOrEqual(states[ms].Velocity, 1 + 1e-12,
                    $"the hand-over funded a sprint to {states[ms].Velocity:R} at wall ms {ms}");
            }

            // Not vacuous: the gap really is one an uncapped chase would have been pinned at the
            // ceiling for the whole two seconds.
            Assert.Greater((next_cell - states[2000].PositionMs) / TypeBeatModPuppeteer.T_CHASE_MS, TypeBeatModPuppeteer.V_MAX);
        }

        /// <summary>
        /// FINISHING A LINE IS WAITING, NOT STOPPING, and it is pinned first-class rather than left
        /// to fall out of the arms. A player who types their line and then waits must NEVER have the
        /// song park on them: the next line would then never arrive and the play would be stuck. The
        /// whole flow is walked here against a real engine.
        ///
        /// <para>SINCE BACKLOG 266 the general law is that the gap runs at no less than
        /// <c>max(1, the rate the line ended at)</c>, and this fixture is its COLD ARM: the tape is
        /// at the song's own speed when the line runs out, so the floor is exactly 1.00x and the
        /// whole trajectory below is unchanged to the bit. Both claims are asserted, the law and this
        /// arm's exact value, because it is the exactness that says the feature costs an ordinary
        /// player nothing.</para>
        ///
        /// <para>The distinction the model draws is the caret's, not the clock's. A caret PAST the
        /// last cell of its line is waiting, so it coasts at 1.00x. A caret sitting mid-line with
        /// cells still ahead of it is stopping, so the tape drags to a halt on it and waits. The
        /// second case includes the one that looks like the first and is not: under
        /// <c>StrictSpaces</c> a typo on a word gap PARKS the caret on that gap cell, which is
        /// typeable and mid-line, so <see cref="TypingEngine.CurrentLeadLag"/> is non-null, the
        /// active arm holds, and the song correctly waits for the fix instead of coasting away from
        /// it. That case must never be swept into the coast predicate.</para>
        /// </summary>
        [Test]
        public void FinishingALineIsWaitingNotStopping()
        {
            var engine = twoLineEngine();

            double firstVocal = engine.Lines[0].FirstVocalTime;
            double nextActivation = engine.Lines[1].ActivationTime;
            double nextVocal = engine.Lines[1].FirstVocalTime;

            engine.Update(firstVocal);

            foreach (char c in "abc")
                Assert.IsTrue(engine.ProcessKey(c, firstVocal), $"'{c}' was refused by the engine");

            Assert.IsTrue(engine.IsLineComplete, "the caret has to be past the last cell for this to be the scenario");

            // The line tail, the seal, the instrumental gap and the next line's cue, all of it.
            int wall = (int)(nextVocal - firstVocal) + 2000;

            var states = against(engine, PuppeteerState.AnchoredAt(firstVocal), wall);

            int cue = (int)(nextActivation - firstVocal);

            // The tape was at the song's own speed when the line ran out, so the 266 floor is
            // max(1.0, 1.0) = exactly 1.00x, and it is the state's own number rather than a
            // constant read off the test.
            double endRate = states[1].Velocity;
            double gapFloor = Math.Max(1, endRate);

            Assert.AreEqual(1.0, gapFloor, 1e-12, "this fixture is the COLD arm of the law, so its floor has to be exactly 1.00x");

            for (int ms = 1; ms <= cue; ms++)
            {
                Assert.GreaterOrEqual(states[ms].Velocity, gapFloor - 1e-12,
                    $"the song ran under the gap floor at wall ms {ms} ({states[ms].Velocity:R} against {gapFloor:R})");

                Assert.AreEqual(1.0, states[ms].Velocity, 1e-12,
                    $"the song stopped waiting for a player who had FINISHED their line, at wall ms {ms} ({states[ms].Velocity:R})");
            }

            // Sixteen and a half seconds of song for sixteen and a half seconds of waiting: the tail
            // and the gap played exactly as they do unmodded, and the next line arrived on time.
            Assert.AreEqual(nextActivation, states[cue].PositionMs, 1e-6);

            // The approach across the cue lead continues at the song's own speed (the caret is on
            // the next line now, untyped, so the typing-sustained cap is still 1.00x), and then the
            // tape parks ON that line's first cell.
            for (int ms = cue; ms <= wall; ms++)
            {
                Assert.LessOrEqual(states[ms].Velocity, 1 + 1e-12,
                    $"the cue lead was sprinted at {states[ms].Velocity:R} at wall ms {ms}");
            }

            Assert.AreEqual(nextVocal, states[wall].PositionMs, TypeBeatModPuppeteer.T_CHASE_MS / 2,
                "the tape must park on the next line's first cell, not short of it and not past it");

            Assert.Less(states[wall].Velocity, 0.01, "...and it parks, rather than running on through an untyped line");

            // NEVER STUCK: the player arrives and types, and the song goes again.
            Assert.IsTrue(engine.ProcessKey('a', states[wall].PositionMs));

            var resumed = against(engine, states[wall], 2000);

            Assert.Greater(resumed[2000].PositionMs, states[wall].PositionMs + 500,
                "one keypress on the parked tape has to lift it");
        }

        // -----------------------------------------------------------------------------------------
        // THE GAP FLOOR (backlog 261, made a true floor in backlog 266): a between-line gap runs at
        // no less than the speed the tape arrived at, with the position chase still live above it.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// THE CAPTURE. When the arm goes from a finite target to a coast, <c>max(1, velocity)</c>
        /// becomes the LEAST the tape may run at for the rest of the gap. On a coast itself there is
        /// nothing above it (the target is unreachable, so the position term is absent) and the tape
        /// runs at exactly the floor, which is the owner's original rule verbatim: "hold the current
        /// speed upon user gameplay typing reaching line end, and keep constant until next line
        /// begins, floor for this should be 1.0x". Everyone at or below the song's own speed is
        /// exactly where they were.
        ///
        /// <para>The value is the SMOOTHED TAPE VELOCITY, which is the speed the player is actually
        /// hearing, and not the typing pace, which is a different number they never hear. It is
        /// captured once, so nothing about the gap can move it afterwards.</para>
        ///
        /// <para>BACKLOG 266 IS THE TAIL OF THIS TEST: the floor is no longer cleared by the next
        /// line's cue, and the whole trajectory it buys is
        /// <see cref="AFastPlayersGapRunsAtTheirOwnSpeedUntilTheTapeCatchesTheirCaret"/>.</para>
        /// </summary>
        [Test]
        public void TheGapFloorIsTheSpeedTheTapeArrivedAt()
        {
            const int wall_ms = 20000;

            foreach (double arrival in new[] { 1.25, 1.6, TypeBeatModPuppeteer.V_MAX })
            {
                var states = trajectory(at(0, arrival), _ => PuppeteerArm.Coast, wall_ms);

                Assert.AreEqual(arrival, states[1].HeldFloorVelocity, 1e-12,
                    $"the floor is the velocity the coast began at, and it read {states[1].HeldFloorVelocity:R} instead of {arrival:R}");

                for (int ms = 1; ms <= wall_ms; ms++)
                {
                    Assert.AreEqual(arrival, states[ms].HeldFloorVelocity, 1e-12, $"the floor moved at wall ms {ms}");

                    Assert.AreEqual(arrival, states[ms].Velocity, 1e-12,
                        $"the coast was not flat at the floor at wall ms {ms}: it ran at {states[ms].Velocity:R}");
                }

                // Twenty seconds of wall time is twenty seconds of song at the floor's speed, to the
                // millisecond: the position term is still absent, so nothing shapes this but the
                // floor. This is the identity that keeps the pure coast byte-identical to backlog
                // 261's held coast even though the arithmetic around it changed.
                Assert.AreEqual(arrival * wall_ms, states[wall_ms].PositionMs, 1e-6);
            }

            // FLOORED AT 1.00x, which is the half of the rule that protects everyone the feature is
            // not for. A tape dragged toward the crawl by a player who hesitated does not hold the
            // stall, it is brought back up to the song's own speed.
            foreach (double arrival in new[] { TypeBeatModPuppeteer.V_EPSILON, 0.4, 1.0 })
            {
                var states = trajectory(at(0, arrival), _ => PuppeteerArm.Coast, 6000);

                Assert.AreEqual(TypeBeatModPuppeteer.COAST_MAX_VELOCITY, states[1].HeldFloorVelocity, 1e-12,
                    $"a tape at {arrival:R} floored at its own stall instead of the song's own speed");

                Assert.AreEqual(1.0, states[6000].Velocity, 1e-6);
            }

            // The floor and the coast cap are ONE number, so neither can drift from the other.
            Assert.AreEqual(1.0, TypeBeatModPuppeteer.COAST_MAX_VELOCITY, 1e-12);

            // CEILED BY THE PRESET, which needs no clamp in practice (a real trajectory's velocity
            // can never exceed it) and has one anyway, so a hand-built state cannot command a rate
            // the audio path would refuse. A tape handed 2.5x eases DOWN to the ceiling.
            const double over_ceiling = 2.5;

            var overCeiling = trajectory(PuppeteerTuning.Tempo, at(0, over_ceiling), _ => PuppeteerArm.Coast, 6000);

            Assert.Greater(over_ceiling, TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY, "the fixture has to actually be over the ceiling");

            Assert.AreEqual(over_ceiling, overCeiling[1].HeldFloorVelocity, 1e-12,
                "the FLOOR is what the tape arrived at, uncapped: the ceiling is applied where the velocity is, not at capture");

            Assert.AreEqual(TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY, overCeiling[6000].Velocity, 1e-6,
                "the ease is asymptotic, so this is measured twenty smoothing constants in");

            Assert.Greater(overCeiling[6000].Velocity, TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY - 1e-12,
                "...and it approaches the preset's ceiling from ABOVE, never dipping under it");

            // ...and a FINITE target no longer clears the floor on sight (backlog 266): it CARRIES
            // it, because the gap floor's whole point is that it outlives the cue that hands the
            // caret to the next line. The target below is a hundred seconds away, so the chase law is
            // asking for far more than the floor and the floor stands.
            var typing = PuppeteerClock.Step(at(0, 1.6) with { HeldFloorVelocity = 1.6 },
                new PuppeteerArm(100000, TypeBeatModPuppeteer.V_MAX), tuning());

            Assert.AreEqual(1.6, typing.HeldFloorVelocity, 1e-12,
                "the cue is not where the floor ends: the tape catching the caret is");

            Assert.AreEqual(1.6, typing.Velocity, 1e-12,
                "...and the rate is continuous across it, because the clamp collapses to the floor at both ends");

            // A finite target with NO floor held never arms one: a floor is only ever captured by a
            // coast, which is what makes "the first tick of a coast" readable off the state alone
            // rather than needing edge detection.
            Assert.AreEqual(PuppeteerClock.NO_HELD_FLOOR,
                PuppeteerClock.HoldFor(at(0, 1.6), new PuppeteerArm(100000, TypeBeatModPuppeteer.V_MAX), tuning()), 1e-12);

            Assert.AreEqual(1.6, PuppeteerClock.HoldFor(at(0, 1.6), PuppeteerArm.Coast, tuning()), 1e-12);
            Assert.AreEqual(PuppeteerClock.NO_HELD_FLOOR, PuppeteerClock.HoldFor(at(0, 1.6), PuppeteerArm.ReleasedCoast, tuning()), 1e-12);

            // THE RELEASE PREDICATE, at the boundary. The floor ends where the chase law asks for
            // exactly it, which is the steady-state lag at that speed: floor * ChaseMs of song.
            var carried = at(0, 1.6) with { HeldFloorVelocity = 1.6 };

            double caughtAt = 1.6 * TypeBeatModPuppeteer.T_CHASE_MS;

            Assert.AreEqual(1.6, PuppeteerClock.HoldFor(carried, new PuppeteerArm(caughtAt + 1, TypeBeatModPuppeteer.V_MAX), tuning()), 1e-12,
                "a tape still short of the steady-state lag has not caught the caret yet");

            Assert.AreEqual(PuppeteerClock.NO_HELD_FLOOR,
                PuppeteerClock.HoldFor(carried, new PuppeteerArm(caughtAt, TypeBeatModPuppeteer.V_MAX), tuning()), 1e-12,
                "...and at exactly the lag it has, so the floor is given back");

            Assert.AreEqual(PuppeteerClock.NO_HELD_FLOOR,
                PuppeteerClock.HoldFor(carried, new PuppeteerArm(-5000, TypeBeatModPuppeteer.V_MAX), tuning()), 1e-12,
                "a backspaced caret behind the tape asks for less than any floor, so it releases on the same tick");
        }

        /// <summary>
        /// THE FLOOR OUTLIVES THE PACE ESTIMATE, and this is the pin that says why the floor is
        /// raised over <see cref="PuppeteerClock.TypingSustainedCap"/> rather than routed through it.
        ///
        /// <para><see cref="PuppeteerClock.StepPace"/> decays the pace toward zero across a coast, on
        /// purpose: it is what makes a line hand-over read as a blip and not as a burst of typing
        /// nobody performed. So within about three time constants the sustained cap is back at
        /// <c>max(1, 0)</c>, and a floor that had to pass through it would be quietly undone a third
        /// of a second into every instrumental. The decay stays and the floor is laid over it.</para>
        /// </summary>
        [Test]
        public void TheFloorOutlivesTheFadingPaceEstimate()
        {
            var settled = trajectory(PuppeteerState.AnchoredAt(0), steadyTypist(1.6), 6000)[6000];

            Assert.AreEqual(1.6, settled.Velocity, 1e-6, "the fixture has to actually be a tape running above the song");
            Assert.AreEqual(1.6, settled.PaceVelocity, 1e-6);

            var coasting = trajectory(settled, _ => PuppeteerArm.Coast, 3000);

            // Three seconds is twenty five smoothing constants: the pace estimate is gone.
            Assert.Less(coasting[3000].PaceVelocity, 1e-9,
                "the pace has to have decayed, or this test is not measuring the bypass at all");

            Assert.AreEqual(1.0, PuppeteerClock.TypingSustainedCap(coasting[3000].PaceVelocity, tuning()), 1e-12,
                "...and the cap it feeds is back at 1.00x, which is what would have undone the floor");

            for (int ms = 1; ms <= 3000; ms++)
            {
                Assert.AreEqual(settled.Velocity, coasting[ms].Velocity, 1e-12,
                    $"the floor decayed with the pace at wall ms {ms} ({coasting[ms].Velocity:R})");
            }
        }

        /// <summary>
        /// THE WHOLE FEATURE, end to end against a real engine: sprint a line, finish it, and run the
        /// whole gap at no less than the speed the tape arrived at, INCLUDING the stretch past the
        /// next line's cue, until the tape has actually caught the caret.
        ///
        /// <para><b>What backlog 266 changed, and it is the player's own report.</b> Backlog 261
        /// released the held speed at the ARM FLIP, the instant the next line took the caret. Nobody
        /// has typed on that line yet, so the typing-sustained cap is <c>max(1, 0)</c> and the rate
        /// slid straight back down to 1.00x while the player was still sitting a line and a half
        /// ahead of the song: the gap felt like two halves. The floor now outlives the cue and is
        /// given back where the chase law says the tape has ARRIVED, a steady-state lag short of the
        /// caret cell, so the cue is not a rate event at all. The proof of that is an EQUALITY, not a
        /// tolerance: the velocity across the flip tick is bit identical.</para>
        ///
        /// <para><b>The gap is crossed in less WALL time and the line still arrives at its own
        /// POSITION</b>, which is the whole reason no overshoot machinery is needed. The tape's target
        /// is a position, so a floor of 1.85x turns a thirty second gap into sixteen seconds of
        /// waiting rather than moving the line: the fast player simply reaches the next line sooner in
        /// wall time, which is what they asked for.</para>
        ///
        /// <para>The PARK is still backlog 257's, pinned by
        /// <see cref="FinishingALineIsWaitingNotStopping"/>: once the floor is released the pace has
        /// long decayed to nothing, so the cap is the song's own speed and the tape eases down into
        /// the same park a cold approach makes.</para>
        /// </summary>
        [Test]
        public void AFastPlayersGapRunsAtTheirOwnSpeedUntilTheTapeCatchesTheirCaret()
        {
            var engine = sprintEngine();

            var line = engine.Lines[0];
            double firstVocal = line.FirstVocalTime;
            double nextActivation = engine.Lines[1].ActivationTime;
            double nextVocal = engine.Lines[1].FirstVocalTime;

            // Eight cells 750 ms apart, struck every 250 wall ms: three times the song's own pace,
            // which is what puts the tape on the ceiling rather than merely above 1.00x.
            Assert.AreEqual(750, line.Cells[1].TargetTime - line.Cells[0].TargetTime, 1e-9);

            var keys = new Dictionary<int, char>();

            for (int i = 0; i < 8; i++)
                keys[1 + (250 * i)] = "abcdefgh"[i];

            const int wall_ms = 22000;

            var states = against(engine, PuppeteerState.AnchoredAt(firstVocal), wall_ms, keys);

            // 1. THE SPRINT, and THE FLIP on the very tick the last key runs the caret off the end of
            //    the line: the key is fed and the arm is read after it, exactly as the live driver
            //    does within one frame.
            int finished = 1 + (250 * 7);

            Assert.AreEqual(PuppeteerClock.NO_HELD_FLOOR, states[finished - 1].HeldFloorVelocity, 1e-12,
                "the tick before the last press is still on the typing arm");

            Assert.Greater(states[finished - 1].Velocity, 1.5,
                $"the scripted sprint only got the tape to {states[finished - 1].Velocity:R}, so there is no speed to hold");

            double floor = states[finished].HeldFloorVelocity;

            Assert.AreEqual(Math.Max(1, states[finished - 1].Velocity), floor, 1e-12,
                "the floor is max(1, the velocity the player was hearing) at the tick the arm flipped");

            // 2. THE ARM FLIP, which is a POSITION and not a wall instant: the caret is handed to the
            //    next line when the tape reaches that line's activation. It is located by position
            //    rather than by the floor's release, because separating those two is the whole point
            //    of backlog 266.
            int flip = Array.FindIndex(states, finished + 1, s => s.PositionMs >= nextActivation) + 1;

            Assert.Greater(flip - finished, 1500, "the fixture's gap has to be a real instrumental stretch");

            // 3. THE FLOOR OUTLIVES THE CUE, and the flip is not a rate event. The floor is still on
            //    the state after it, and the velocity across it is BIT IDENTICAL: the clamp collapses
            //    to the floor on both sides (an unreachable target on one, a target far enough away
            //    that the chase asks for more than the floor on the other), so the same target meets
            //    the same previous velocity through the same filter.
            Assert.AreEqual(floor, states[flip].HeldFloorVelocity, 1e-12,
                "the cue gave the floor back, which is exactly the sag backlog 266 removes");

            Assert.IsTrue(states[flip].Velocity.Equals(states[flip - 1].Velocity),
                $"the arm flip moved the rate from {states[flip - 1].Velocity:R} to {states[flip].Velocity:R}, and a cue must not be a rate event");

            // 4. THE RELEASE, which is where the TAPE CATCHES THE CARET rather than where the caret
            //    changed hands: the chase law asks for exactly the floor a steady-state lag short of
            //    the cell, and gives it back there.
            int release = Array.FindIndex(states, finished + 1, s => s.HeldFloorVelocity.Equals(PuppeteerClock.NO_HELD_FLOOR));

            Assert.Greater(release, flip, "the floor was released at the cue, which is backlog 261's behaviour and not this one");

            for (int ms = finished; ms < release; ms++)
            {
                Assert.GreaterOrEqual(states[ms].Velocity, floor - 1e-12,
                    $"the gap dipped under its floor at wall ms {ms}: it ran at {states[ms].Velocity:R} against a floor of {floor:R}");
            }

            Assert.AreEqual(nextVocal - (floor * TypeBeatModPuppeteer.T_CHASE_MS), states[release - 1].PositionMs, floor + 1e-6,
                "the floor ends a steady-state lag short of the caret cell, which is the model's own definition of having arrived");

            // 5. THE LINE ARRIVES AT ITS OWN POSITION, and SOONER IN WALL TIME. The tape covers the
            //    whole run from the line end to the release point at the floor, so it took that
            //    distance divided by the floor rather than the distance itself: the floor buys the
            //    player wall time, never song position.
            double gap = states[release - 1].PositionMs - states[finished].PositionMs;

            Assert.AreEqual(gap / floor, release - 1 - finished, 2,
                $"a {gap:N0} ms gap floored at {floor:R}x has to be {gap / floor:N0} ms of waiting, not {release - 1 - finished}");

            Assert.Less(release - finished, gap - 1000, "...and that really is less wall time than an unfloored coast would have taken");

            // 6. THE PARK IS BACKLOG 257'S, UNCHANGED. Once the floor is gone the pace has long since
            //    decayed, so the cap is the song's own speed, the tape eases down monotonically and
            //    parks on the untyped cell.
            Assert.AreEqual(1.0, PuppeteerClock.TypingSustainedCap(states[release].PaceVelocity, tuning()), 1e-12);

            for (int ms = release; ms < wall_ms; ms++)
            {
                Assert.LessOrEqual(states[ms + 1].Velocity, states[ms].Velocity + 1e-12,
                    $"the tape sped up during the approach at wall ms {ms}, which the cue lead may never do");
            }

            // The park runs a little further past the cell than a cold approach does, and that is the
            // one thing backlog 266 costs rather than a defect: the tape now arrives at the release
            // point still moving at the floor instead of already eased down to 1.00x, so there is
            // more momentum to spend. Measured at 92 ms of song past the cell against the cold
            // path's 61, and bounded, like every park overshoot, by one smoothing constant of song
            // (see TheTempoParkStillReachesTheCrawlThroughTheStretchersUglyBand). It is inaudible as
            // a POSITION because this mod does not judge the distance between a press and its target
            // at all.
            double overshoot = states[wall_ms].PositionMs - nextVocal;

            Assert.AreEqual(nextVocal, states[wall_ms].PositionMs, TypeBeatModPuppeteer.SMOOTHING_TAU_MS,
                "the tape must park on the next line's first cell, inside one smoothing constant of it");

            Assert.Greater(overshoot, 0, "...and it must ARRIVE at the cell rather than stopping short of it");

            Assert.Less(states[wall_ms].Velocity, 0.01, "...and park, rather than running on through an untyped line");

            // 7. NEVER STUCK. The player arrives and types, and the song goes again.
            Assert.IsTrue(engine.ProcessKey('a', states[wall_ms].PositionMs));

            var resumed = against(engine, states[wall_ms], 2000);

            Assert.Greater(resumed[2000].PositionMs, states[wall_ms].PositionMs + 500,
                "one keypress on the parked tape has to lift it");
        }

        /// <summary>
        /// A LINE FINISHED WHILE DECELERATING FLOORS AT THE RATE IT ENDED AT, AND STOPS DECELERATING
        /// THERE. This is the first of the two defects the player reported against backlog 261, and
        /// it is the one that needs the FLOOR rather than the release rule.
        ///
        /// <para>The scenario is ordinary: a fast player finishes the line's characters, the tape
        /// catches up with their caret, and the rate is already easing back toward 1.00x when the
        /// caret runs off the end. Backlog 261 froze whatever instantaneous value it found, so the
        /// gap ran at a DECELERATING number, and the player heard the song still sagging on a stretch
        /// with nothing to sag for. It is captured the same way now, but as a floor the rest of the
        /// gap sits on rather than a value that carries a direction with it, so the deceleration
        /// STOPS at the line end.</para>
        /// </summary>
        [Test]
        public void FinishingALineWhileDeceleratingFloorsAtTheRateItEndedAt()
        {
            // Settle a tape well above the song, then freeze the caret where the player left it: the
            // tape closes the remaining gap and the rate eases DOWN, which is the state a real fast
            // player is in a few hundred milliseconds after their last character.
            var settled = trajectory(PuppeteerState.AnchoredAt(0), steadyTypist(1.9), 6000)[6000];

            double frozen = 1.9 * 6000;

            var easing = trajectory(settled, _ => new PuppeteerArm(frozen, TypeBeatModPuppeteer.V_MAX), 600);

            int decelerating = Array.FindIndex(easing, s => s.Velocity < 1.3);

            Assert.Greater(decelerating, 1, "the fixture has to actually be mid-deceleration");

            double endRate = easing[decelerating].Velocity;

            Assert.Less(endRate, easing[decelerating - 1].Velocity, "...and FALLING at the tick the line runs out");
            Assert.Greater(endRate, 1, "...and still above the song's own speed, or the floor would be the trivial 1.00x");

            // The line runs out here. The floor is that rate, and the gap runs at it, flat.
            const int gap_ms = 20000;

            var gap = trajectory(easing[decelerating], _ => PuppeteerArm.Coast, gap_ms);

            Assert.AreEqual(endRate, gap[1].HeldFloorVelocity, 1e-12,
                "the floor is max(1, the rate the line ended at), whichever way that rate was moving");

            for (int ms = 1; ms <= gap_ms; ms++)
            {
                Assert.GreaterOrEqual(gap[ms].Velocity, endRate - 1e-12,
                    $"the song went on decelerating into the gap, reaching {gap[ms].Velocity:R} at wall ms {ms}");

                Assert.AreEqual(endRate, gap[ms].Velocity, 1e-12, $"the gap was not flat at its floor at wall ms {ms}");
            }

            // NOT VACUOUS, and this is the deleted behaviour stated as arithmetic: without the floor
            // the same tape on the same coast would have carried on down to the coast cap, because
            // the deceleration it was in the middle of had a target of 1.00x and nothing was stopping
            // it. Twenty seconds of gap at the floor is a whole second of song more than it would
            // have been.
            Assert.Greater(gap[gap_ms].PositionMs - (easing[decelerating].PositionMs + gap_ms), 1000,
                "the floor bought the player no song at all over an unfloored coast, so this pin is measuring nothing");

            // ...and the OTHER half of the defect, which the release rule answers rather than the
            // floor: the rate does not sag at the cue either. The next line's cell arrives far away,
            // so the chase law asks for more than the floor and the floor stands.
            var cued = trajectory(gap[gap_ms], _ => new PuppeteerArm(gap[gap_ms].PositionMs + 4000, TypeBeatModPuppeteer.V_MAX), 500);

            for (int ms = 1; ms <= 500; ms++)
            {
                Assert.GreaterOrEqual(cued[ms].Velocity, endRate - 1e-12,
                    $"the rate slid under the floor {ms} ms after the cue, which is the sag the player reported");
            }
        }

        /// <summary>
        /// A SLOW FINISHER GETS A NORMAL-SPEED GAP, NEVER A SLOW ONE, which is the half of the law
        /// that protects everyone the feature is not for. The floor is <c>max(1, endRate)</c>, so a
        /// player whose tape was below the song's own speed when their line ran out (they were
        /// hesitating, or simply typing slower than the song) gets exactly 1.00x through the
        /// instrumental rather than being made to sit through it at their own stall.
        ///
        /// <para>This is unchanged by backlog 266 and pinned again here as an ITEM, because it is the
        /// half a "keep the end-of-line rate" reading of the rule would get wrong.</para>
        /// </summary>
        [Test]
        public void ASlowFinishersGapRunsAtExactlyTheSongsOwnSpeed()
        {
            const int gap_ms = 12000;

            foreach (double endRate in new[] { TypeBeatModPuppeteer.V_EPSILON, 0.35, 0.8, 1.0 })
            {
                var gap = trajectory(at(0, endRate), _ => PuppeteerArm.Coast, gap_ms);

                Assert.AreEqual(1.0, gap[1].HeldFloorVelocity, 1e-12,
                    $"a line that ended at {endRate:R} floored at its own stall instead of at the song's own speed");

                for (int ms = 1; ms <= gap_ms; ms++)
                {
                    Assert.LessOrEqual(gap[ms].Velocity, 1 + 1e-12,
                        $"a slow finisher's gap ran FASTER than the song at wall ms {ms} ({gap[ms].Velocity:R})");

                    Assert.GreaterOrEqual(gap[ms].Velocity, gap[ms - 1].Velocity - 1e-12,
                        $"a slow finisher's gap has to spin UP toward the song's own speed, and it slowed at wall ms {ms}");
                }

                Assert.AreEqual(1.0, gap[gap_ms].Velocity, 1e-9, "...and it arrives at exactly the song's own speed");
            }
        }

        /// <summary>
        /// THE CATCH-UP IS LIVE ON TOP OF THE FLOOR, which is the whole of what backlog 266 adds to
        /// backlog 261 and the second half of the player's report. A player who starts typing the
        /// NEXT line during the gap is chased: the first keypress lifts the typing-sustained cap, the
        /// rate rises ABOVE the floor to close the distance to their caret, and it settles back onto
        /// the FLOOR (not onto 1.00x) once the typing stops.
        ///
        /// <para>Backlog 261 could not do this at all: the coast branch bypassed the sustained cap
        /// and pinned the rate at the frozen value, and the moment a finite target appeared the hold
        /// was dropped outright and the cap collapsed to <c>max(1, 0)</c>. So typing early made the
        /// song SLOWER, which is precisely the "two halves" gap that was reported.</para>
        ///
        /// <para>Run twice on the same fixture, once with the early typing and once without, so the
        /// rise is measured against the trajectory the same player would have had if they had waited
        /// rather than against a constant. The line before is finished by hand and the tape handed
        /// in at a stated 1.25x, rather than sprinted like
        /// <see cref="AFastPlayersGapRunsAtTheirOwnSpeedUntilTheTapeCatchesTheirCaret"/>'s, for one
        /// reason: the floor has to sit well BELOW the preset's ceiling or there would be no room
        /// above it for a catch-up to be visible in, and stating the arrival rate is the honest way
        /// to get that rather than hunting for a key schedule that lands on it.</para>
        /// </summary>
        [Test]
        public void TypingTheNextLineEarlyRaisesTheRateAboveTheFloorAndThenSettlesBackOntoIt()
        {
            const double end_rate = 1.25;
            const double line_end = 9250;
            const int wall_ms = 20000;

            var waited = against(finishedCatchUpEngine(), at(line_end, end_rate), wall_ms);

            double floor = waited[1].HeldFloorVelocity;

            Assert.AreEqual(end_rate, floor, 1e-12, "the floor is the rate the line ended at");

            Assert.Less(floor, TypeBeatModPuppeteer.V_MAX - 0.5,
                $"the fixture's floor of {floor:R} is too near the ceiling for a catch-up to show");

            // The arm flips when the tape reaches the next line's cue. Located on the WAITING run,
            // which types nothing at all, so it is the same instant in both.
            double nextActivation = catchUpEngine().Lines[1].ActivationTime;

            int flip = Array.FindIndex(waited, 1, s => s.PositionMs >= nextActivation) + 1;

            Assert.Greater(flip, 1500, "the fixture's gap has to be a real instrumental stretch");

            // THE SAME PLAYER, TYPING EARLY: three characters of the next line's first word, struck
            // just after the caret is handed to them.
            var early = new Dictionary<int, char>
            {
                [flip + 10] = 'a',
                [flip + 110] = 'b',
                [flip + 210] = 'c',
            };

            var typed = against(finishedCatchUpEngine(), at(line_end, end_rate), wall_ms, early);

            // Identical up to the first early keypress, floor included: nothing before it differs.
            // The bound stops one tick short of that press, because the press is fed BEFORE the arm
            // is read on its own tick, exactly as the live driver feeds a frame's keys.
            for (int ms = 1; ms < flip + 10; ms++)
                Assert.IsTrue(typed[ms].Equals(waited[ms]), $"the two runs diverged before the early typing, at wall ms {ms}");

            // 1. THE RISE. The waiting player sits at the floor; the typing one is chased above it,
            //    and the chase is bounded by the preset's ceiling and by nothing else.
            int rose = Array.FindIndex(typed, flip + 10, s => s.Velocity > floor + 0.1);

            Assert.Greater(rose, 0, "the first keypress of the next line did not lift the rate at all");
            Assert.Less(rose - flip, 300, $"the rate took {rose - flip} ms to answer the keypress, which is not an answer");

            Assert.AreEqual(floor, waited[rose].Velocity, 1e-12,
                "...and the player who waited is still at the floor at that same instant, which is what makes this the typing's doing");

            double peak = typed.Skip(flip).Max(s => s.Velocity);

            Assert.Greater(peak, floor + 0.3, $"the catch-up only reached {peak:R} against a floor of {floor:R}");
            Assert.LessOrEqual(peak, TypeBeatModPuppeteer.V_MAX + 1e-12, "...and it may never be chased past the preset's ceiling");

            // 2. NEVER UNDER THE FLOOR while it is held, which is the invariant the rise sits on.
            int release = Array.FindIndex(typed, 1, s => s.HeldFloorVelocity.Equals(PuppeteerClock.NO_HELD_FLOOR));

            Assert.Greater(release, flip, "the floor must outlive the cue on the typed run too");

            for (int ms = 1; ms < release; ms++)
            {
                Assert.GreaterOrEqual(typed[ms].Velocity, floor - 1e-12,
                    $"the typed gap dipped under its floor at wall ms {ms} ({typed[ms].Velocity:R})");
            }

            // 3. IT SETTLES BACK ONTO THE FLOOR, not onto 1.00x. The pace estimate decays across the
            //    silence after the burst, so the sustained cap is back at max(1, 0); under backlog
            //    261's release-at-the-cue that is exactly where the rate would have gone, and it is
            //    the sag the report described. The floor is what stops it.
            int settled = Array.FindIndex(typed, rose, s => s.Velocity < floor + 0.01);

            Assert.Greater(settled, rose, "the catch-up never came back down, so this fixture is not measuring a settle");
            Assert.Less(settled, release, "...and it came back down while the floor was still held, which is the case being pinned");

            Assert.AreEqual(floor, typed[settled].Velocity, 0.01,
                $"the rate settled to {typed[settled].Velocity:R} rather than back onto its floor of {floor:R}");

            Assert.AreEqual(1.0, PuppeteerClock.TypingSustainedCap(typed[settled].PaceVelocity, tuning()), 1e-12,
                "...with the sustained cap back at 1.00x, which is what would have taken the rate down with it");

            // 4. AND IT BOUGHT WALL TIME: the same song position is reached sooner because the
            //    catch-up ran above the floor for a while. That is the whole point of chasing a
            //    player who is ahead.
            Assert.Greater(typed[release].PositionMs, waited[release].PositionMs + 100,
                "the catch-up closed no distance at all");
        }

        /// <summary>
        /// THE CUE IS NOT A RATE EVENT, at BOTH instants a cue can happen, and asserted as an
        /// EQUALITY rather than as a tolerance.
        ///
        /// <para>The caret is handed to the next line at
        /// <see cref="TypingLine.ActivationTime"/> on a raw engine and
        /// <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> earlier than that under
        /// <see cref="TypingEngine.BoundedRush"/>, which is every live stack (backlog 218). Backlog
        /// 261 released the held speed AT that hand-over, so the flip was audible and its instant
        /// depended on which era the run was in. It is not a rate event at all now: the clamp
        /// collapses to the floor on both sides of the flip (an unreachable target on one side, a
        /// target far enough away that the chase asks for more than the floor on the other), so the
        /// same target meets the same previous velocity through the same filter and the arithmetic
        /// is bit identical.</para>
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void TheCueIsNotARateEventUnderEitherEntryBound(bool boundedRush)
        {
            var engine = boundedRush
                ? liveEngine(sprintTwoLineMap(), new TypeBeatModPuppeteer())
                : sprintEngine();

            Assert.AreEqual(boundedRush, engine.BoundedRush, "the two cases have to be two different entry bounds");

            double firstVocal = engine.Lines[0].FirstVocalTime;

            // Where the caret actually changes hands, which is the whole reason both cases are run.
            double entryOpens = engine.Lines[1].ActivationTime - (boundedRush ? TypingEngine.FLETCHER_DRAG_GRACE_MS : 0);

            var keys = new Dictionary<int, char>();

            for (int i = 0; i < 8; i++)
                keys[1 + (250 * i)] = "abcdefgh"[i];

            var states = against(engine, PuppeteerState.AnchoredAt(firstVocal), 22000, keys);

            int finished = 1 + (250 * 7);
            double floor = states[finished].HeldFloorVelocity;

            Assert.Greater(floor, 1.5, $"the scripted sprint only reached {floor:R}, so a flat rate would prove nothing");

            int flip = Array.FindIndex(states, finished + 1, s => s.PositionMs >= entryOpens) + 1;

            Assert.Greater(flip - finished, 1500, "the fixture's gap has to be a real instrumental stretch");

            // The flip really is located where this test says it is: the arm at wall ms n is read at
            // the position of state n-1, so the flip tick is the first whose PREVIOUS position had
            // reached the entry bound, and the tick before it had not.
            Assert.GreaterOrEqual(states[flip - 1].PositionMs, entryOpens);
            Assert.Less(states[flip - 2].PositionMs, entryOpens);

            // ...and the two eras really do flip at two different SONG positions, a drag grace
            // apart, which is what makes running this twice worth anything.
            Assert.AreEqual(engine.Lines[1].ActivationTime - entryOpens,
                boundedRush ? TypingEngine.FLETCHER_DRAG_GRACE_MS : 0, 1e-9);

            // THE EQUALITY.
            Assert.IsTrue(states[flip].Velocity.Equals(states[flip - 1].Velocity),
                $"the cue moved the rate from {states[flip - 1].Velocity:R} to {states[flip].Velocity:R}");

            Assert.AreEqual(floor, states[flip].HeldFloorVelocity, 1e-12, "...and the floor survived it");

            // ...and the rate never dips under the floor between the flip and the catch-up, which is
            // the stretch backlog 261 spent sliding back down to 1.00x.
            int release = Array.FindIndex(states, finished + 1, s => s.HeldFloorVelocity.Equals(PuppeteerClock.NO_HELD_FLOOR));

            Assert.Greater(release, flip);

            for (int ms = flip; ms < release; ms++)
            {
                Assert.GreaterOrEqual(states[ms].Velocity, floor - 1e-12,
                    $"the rate slid under the floor at wall ms {ms} ({states[ms].Velocity:R}), {ms - flip} ms past the cue");
            }
        }

        /// <summary>
        /// THE ONE COAST THAT CARRIES NO FLOOR, contrasted with the one that does on a single engine.
        /// A mid-map instrumental gap keeps the floor, because the floor has an end: the next line's
        /// caret, which the tape eventually catches (backlog 266 moved that end off the cue and onto
        /// the catch-up, but there still IS one). The OUTRO has no next caret at all, so it releases
        /// and eases back to the song's own speed: past the last line there is nothing to catch, and
        /// an ending sped up forever is not what was asked for. See
        /// <see cref="PastTheLastLineTheTapeSimplyPlaysOutAtTheSongsOwnSpeed"/> for the trajectory;
        /// this is the arm split.
        /// </summary>
        [Test]
        public void TheOutroReleasesTheHoldWhileAMidMapGapKeepsIt()
        {
            var engine = twoLineEngine();

            double firstVocal = engine.Lines[0].FirstVocalTime;

            engine.Update(firstVocal);

            foreach (char c in "abc")
                Assert.IsTrue(engine.ProcessKey(c, firstVocal), $"'{c}' was refused by the engine");

            engine.Update(firstVocal + 10);

            Assert.IsFalse(engine.IsFinished, "there is a second line to come, so this is a mid-map gap");

            var gap = TypeBeatModPuppeteer.ArmFor(engine, firstVocal + 10);

            Assert.IsFalse(gap.ReleasesHold, "an instrumental gap between two lines is exactly what the hold is for");
            Assert.IsTrue(PuppeteerArm.Coast.Equals(gap));

            // Type the second line out and let every line seal: now the map is over.
            engine.Update(engine.Lines[1].FirstVocalTime);

            foreach (char c in "abc")
                Assert.IsTrue(engine.ProcessKey(c, engine.Lines[1].FirstVocalTime), $"'{c}' was refused by the engine");

            engine.Update(60000);

            Assert.IsTrue(engine.IsFinished);

            var outro = TypeBeatModPuppeteer.ArmFor(engine, 60000);

            Assert.IsTrue(outro.ReleasesHold);
            Assert.IsTrue(PuppeteerArm.ReleasedCoast.Equals(outro));

            // The two arms differ in that field and in NOTHING else: same unreachable target, same
            // cap, so the outro is still "the song simply plays" and not a second behaviour.
            Assert.IsTrue(PuppeteerArm.Coast.Equals(PuppeteerArm.ReleasedCoast with { ReleasesHold = false }));

            // ...and the same hot tape really does end up in two different places.
            var held = trajectory(at(0, TypeBeatModPuppeteer.V_MAX), _ => gap, 3000);
            var released = trajectory(at(0, TypeBeatModPuppeteer.V_MAX), _ => outro, 3000);

            Assert.Greater(held[3000].PositionMs - released[3000].PositionMs, 1000,
                "the two coasts landed the tape in the same place, so nothing here is being proved");
        }

        /// <summary>
        /// TWO PLACES A HOLD MUST NOT COME FROM. The INTRO, because
        /// <see cref="PuppeteerState.AnchoredAt"/> starts at the song's own speed, so the hold is
        /// 1.00x and a player hears exactly what they always heard before their first line. And a
        /// SEEK, because both re-anchors go through that same factory: a speed earned on a stretch of
        /// song that is no longer being played is not one to carry across a discontinuity, and the
        /// velocity was already reset there before any of this existed.
        /// </summary>
        [Test]
        public void AnIntroCarriesNoHoldAndASeekDropsOne()
        {
            Assert.AreEqual(PuppeteerClock.NO_HELD_FLOOR, PuppeteerState.AnchoredAt(0).HeldFloorVelocity, 1e-12);

            var intro = trajectory(PuppeteerState.AnchoredAt(0), _ => PuppeteerArm.Coast, 30000);

            Assert.AreEqual(TypeBeatModPuppeteer.COAST_MAX_VELOCITY, intro[1].HeldFloorVelocity, 1e-12);
            Assert.AreEqual(30000, intro[30000].PositionMs, 1e-6, "thirty seconds of intro is thirty seconds of song, exactly as it always was");

            Assert.AreEqual(PuppeteerClock.NO_HELD_FLOOR, TypeBeatModPuppeteer.SeekReanchor(1000, 3000)!.Value.HeldFloorVelocity, 1e-12,
                "a forward skip lands where the music is meant to be playing, at the song's own speed");

            Assert.AreEqual(PuppeteerClock.NO_HELD_FLOOR, TypeBeatModPuppeteer.SeekReanchor(1000, -3000)!.Value.HeldFloorVelocity, 1e-12,
                "a rewind restarts the reel from still, and a hold would spin it straight back up");
        }

        /// <summary>
        /// ...and the case that looks like the one above and must NOT be treated like it: under
        /// <c>StrictSpaces</c> a typo on a word gap PARKS the caret on that gap cell. The player is
        /// mid-line with cells still ahead of them, so the song has to wait for the fix, and it does,
        /// because the gap cell is typeable and <see cref="TypingEngine.CurrentLeadLag"/> is
        /// therefore non-null: it is the ACTIVE arm and not the coast. Widening the coast predicate
        /// to "the caret is not moving" would coast the song away from a player who is stuck on a
        /// space.
        /// </summary>
        [Test]
        public void ATypoParkedOnAWordGapWaitsForTheFixInsteadOfCoasting()
        {
            var engine = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "gap", FolderPath = string.Empty, AudioFileName = "a.mp3" },
                Lines = new List<LyricLine>
                {
                    new LyricLine
                    {
                        RawText = "ab cd",
                        StartTime = 2000,
                        EndTime = 20000,
                        SingEndTime = 18000,
                        Units = new[] { new TimedUnit { Text = "ab cd", StartTime = 4000, EndTime = 12000 } },
                    },
                },
                Granularity = TimingGranularity.Line,
            })
            {
                StrictSpaces = true,
                SpaceSkipsWord = true,
                WrongInputOnWordGaps = true,
            };

            double firstVocal = engine.Lines[0].FirstVocalTime;

            engine.Update(firstVocal);

            foreach (char c in "ab")
                Assert.IsTrue(engine.ProcessKey(c, firstVocal), $"'{c}' was refused by the engine");

            // The word gap takes the typo and the caret PARKS on it, which is the whole of backlog
            // 184's rule.
            Assert.IsTrue(engine.ProcessKey('x', firstVocal), "the gap must take the typo");

            var cells = engine.Lines[0].Cells;

            Assert.Less(engine.CaretIndex, cells.Count, "the caret is mid-line, not past the end");
            Assert.IsFalse(engine.IsLineComplete);

            var arm = TypeBeatModPuppeteer.ArmFor(engine, firstVocal);

            Assert.AreNotEqual(double.PositiveInfinity, arm.DesiredPositionMs,
                "a caret stuck on a spoiled word gap is mid-line, so the song must wait for the fix rather than coast away from it");

            Assert.AreEqual(cells[engine.CaretIndex].TargetTime, arm.DesiredPositionMs, 1e-9);
            Assert.AreEqual(TypeBeatModPuppeteer.V_MAX, arm.VelocityCap, 1e-12);

            // ...and waiting is what it does: the target is frozen where the caret is, so the reel
            // drags down to the crawl instead of running on.
            var states = against(engine, at(arm.DesiredPositionMs, 1), 3000);

            Assert.AreEqual(TypeBeatModPuppeteer.V_EPSILON, states[3000].Velocity, 1e-6,
                "the song has to stop and wait for the player to fix the gap");
        }

        /// <summary>
        /// The commanded frequency: the model's velocity as feed-forward, plus its position error
        /// spread over <see cref="TypeBeatModPuppeteer.T_CORRECT_MS"/>, clamped into the model's own
        /// band. That correction is what absorbs the two known pieces of audio physics (the clock
        /// leads the heard audio by up to a playback buffer, and at low rates the time read back is
        /// the raw quantised position) without the model knowing anything about either.
        /// </summary>
        [Test]
        public void TheCommandedFrequencyIsFeedForwardPlusABoundedCorrection()
        {
            var state = at(10000, 1.2);

            // Glued: the correction is exactly zero and the command is the velocity.
            Assert.AreEqual(1.2, TypeBeatModPuppeteer.CommandedFrequency(state, 10000), 1e-12);

            // The clock is 50 ms behind the model, so the song is asked to hurry by 50/250 = 0.2.
            Assert.AreEqual(1.4, TypeBeatModPuppeteer.CommandedFrequency(state, 9950), 1e-12);
            Assert.AreEqual(1.0, TypeBeatModPuppeteer.CommandedFrequency(state, 10050), 1e-12);

            // ...and it is bounded by construction at both ends, however far the two have drifted,
            // so a hitch can never command a frequency the audio path cannot honour.
            Assert.AreEqual(TypeBeatModPuppeteer.V_MAX, TypeBeatModPuppeteer.CommandedFrequency(state, -100000), 1e-12);
            Assert.AreEqual(TypeBeatModPuppeteer.V_EPSILON, TypeBeatModPuppeteer.CommandedFrequency(state, 100000), 1e-12);
        }

        /// <summary>
        /// The rewind threshold is not slop. The platform offset is applied to the gameplay clock
        /// RATE-SCALED, so a fast rate change can walk the reported time back a millisecond or two
        /// while the song is playing perfectly normally forwards; a tape that re-anchored on that
        /// would stutter once per rate swing. The guard is therefore well below any real seek and
        /// well above the wobble.
        /// </summary>
        [Test]
        public void TheRewindGuardSitsAboveTheRateScaledOffsetWobble()
        {
            Assert.AreEqual(-50, TypeBeatModPuppeteer.REWIND_THRESHOLD_MS, 1e-12);
            Assert.AreEqual(ConductorPacing.MAX_REAL_FRAME_MS, TypeBeatModPuppeteer.MAX_REAL_FRAME_MS, 1e-12);

            // An ordinary frame is neither seek, in either direction.
            Assert.IsNull(TypeBeatModPuppeteer.SeekReanchor(1000, 16 * TypeBeatModPuppeteer.V_MAX));
            Assert.IsNull(TypeBeatModPuppeteer.SeekReanchor(1000, -20));
            Assert.IsNull(TypeBeatModPuppeteer.SeekReanchor(1000, 0));

            // The stall guard answers before this one, so the longest frame that can reach it is
            // MAX_REAL_FRAME_MS of wall time, and the fastest rate it can have been running at is
            // V_MAX. That product is the most track time ordinary playback can ever put in one
            // testable frame, and the guard has to sit clear above it or a hitch at speed would
            // re-anchor the tape for no reason.
            Assert.IsNull(TypeBeatModPuppeteer.SeekReanchor(1000, TypeBeatModPuppeteer.MAX_REAL_FRAME_MS * TypeBeatModPuppeteer.V_MAX),
                "the longest honest frame at the highest honest rate is not a seek");

            // A rewind restarts the reel from still; a skip lands where the song is meant to be
            // playing, so it resumes at the song's own speed. See SeekReanchor.
            Assert.AreEqual(0, TypeBeatModPuppeteer.SeekReanchor(1000, -3000)!.Value.Velocity, 1e-12);
            Assert.AreEqual(1, TypeBeatModPuppeteer.SeekReanchor(1000, 3000)!.Value.Velocity, 1e-12);
        }

        /// <summary>
        /// THE SKIP FREEZE (backlog 257), which is the bug the forward guard closes, pinned as the
        /// arithmetic that caused it rather than as a symptom.
        ///
        /// <para>Live play DOES seek forwards: the intro <c>SkipOverlay</c> jumps the clock to
        /// <c>GameplayStartTime</c> and every long instrumental gap has an overlay calling
        /// <c>Player.PerformSkipTo</c>. Without a guard the tape stays where it was, so
        /// <see cref="TypeBeatModPuppeteer.CommandedFrequency"/>'s correction term goes hugely
        /// negative and pins the command at the crawl, and the tape can only close the gap at the
        /// coast speed, so the song is frozen for about one real second per second skipped. On a map
        /// whose skip overlay is the first thing on screen, that is "the song never starts".</para>
        /// </summary>
        [Test]
        public void AForwardSkipReAnchorsTheTapeInsteadOfFreezingTheSong()
        {
            const double before = 2000;
            const double after = 10000;

            var stale = at(before, 1);

            // What the old driver was left holding: the tape at the pre-skip position, the clock
            // eight seconds ahead of it, and a command pinned at the floor.
            Assert.AreEqual(TypeBeatModPuppeteer.V_EPSILON, TypeBeatModPuppeteer.CommandedFrequency(stale, after), 1e-12,
                "a tape left behind by a skip commands the crawl, which is the freeze");

            // ...and it stays there for SECONDS, because the only thing that closes the gap is the
            // tape itself moving at the coast speed. Eight seconds skipped, eight seconds frozen.
            var crawling = trajectory(stale, _ => PuppeteerArm.Coast, 6000);

            Assert.Less(TypeBeatModPuppeteer.CommandedFrequency(crawling[6000], after), 0.5,
                "six wall seconds after an eight second skip the song is still not really playing");

            // THE FIX. One frame's track delta of +8000 ms is a seek, so the tape re-anchors onto
            // the clock at the song's own speed, and the very next command is exactly 1.00x.
            var reanchored = TypeBeatModPuppeteer.SeekReanchor(after, after - before);

            Assert.IsNotNull(reanchored, "a skip of eight seconds must be read as a seek");
            Assert.AreEqual(after, reanchored!.Value.PositionMs, 1e-12, "the tape re-anchors ON the clock");
            Assert.AreEqual(1, reanchored.Value.Velocity, 1e-12);
            Assert.AreEqual(0, reanchored.Value.PaceVelocity, 1e-12, "nobody typed through the skip");

            Assert.AreEqual(1.0, TypeBeatModPuppeteer.CommandedFrequency(reanchored.Value, after), 1e-12,
                "the song must be playing normally on the first frame after the skip");
        }

        // -----------------------------------------------------------------------------------------
        // The TEMPO preset (backlog 258), which is the shipping default.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// THE TWO PRESETS, and since backlog 266 the pin is that they differ in exactly ONE constant
        /// and no others. That one is the time-stretcher's physics rather than taste: it analyses in
        /// windows, so it answers a rate change a window late and smears under rapid modulation,
        /// hence the longer ease. Everything else, the chase horizon, the velocity floor, the pace
        /// estimate, its headroom and now the CEILING, is one set of numbers, which is what "the
        /// caret coupling stays strict, only the velocity trajectory is gentler" means in code.
        ///
        /// <para>THE CEILING USED TO BE THE SECOND DIFFERENCE, at 1.6x, the top of the band a
        /// stretcher holds together in. Backlog 266 raised it to the resampler's own wall because the
        /// owner wants the between-line catch-up to have real headroom and accepts the artefacts
        /// above 1.6x as its price. So the equality below is now a DECISION pinned as a number, not a
        /// physics claim, which is why it is asserted as an equality against <c>V_MAX</c> rather than
        /// as the inequality it used to be.</para>
        /// </summary>
        [Test]
        public void TheTwoPresetsDifferInExactlyTheOneStretcherNumber()
        {
            var tempo = PuppeteerTuning.Tempo;
            var frequency = PuppeteerTuning.Frequency;

            Assert.IsTrue(PuppeteerTuning.For(false).Equals(tempo), "pitch preserved is the DEFAULT, and it is the tempo preset");
            Assert.IsTrue(PuppeteerTuning.For(true).Equals(frequency));

            Assert.AreEqual(300, TypeBeatModPuppeteer.SMOOTHING_TAU_TEMPO_MS, 1e-12);
            Assert.AreEqual(2.0, TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY, 1e-12);

            Assert.AreEqual(TypeBeatModPuppeteer.SMOOTHING_TAU_TEMPO_MS, tempo.SmoothingTauMs, 1e-12);
            Assert.AreEqual(TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY, tempo.MaxVelocity, 1e-12);

            // The frequency preset is backlog 256's tuning, unchanged to the digit.
            Assert.AreEqual(TypeBeatModPuppeteer.SMOOTHING_TAU_MS, frequency.SmoothingTauMs, 1e-12);
            Assert.AreEqual(TypeBeatModPuppeteer.V_MAX, frequency.MaxVelocity, 1e-12);

            Assert.IsTrue(tempo.Equals(frequency with
            {
                SmoothingTauMs = TypeBeatModPuppeteer.SMOOTHING_TAU_TEMPO_MS,
            }), $"a second constant moved between the presets: {tempo} against {frequency}");

            Assert.Greater(TypeBeatModPuppeteer.SMOOTHING_TAU_TEMPO_MS, TypeBeatModPuppeteer.SMOOTHING_TAU_MS,
                "the stretcher needs a rate that moves more slowly than its own window");

            Assert.AreEqual(TypeBeatModPuppeteer.V_MAX, TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY, 1e-12,
                "the owner's decision (backlog 266) is that the stretcher's ceiling meets the resampler's wall");

            // The velocity floor is one number in both modes, and it is the sub-floor SPLIT that
            // makes that survivable on the tempo path: TrackBass throws below an aggregate tempo of
            // 0.05.
            Assert.IsTrue(tempo.MinVelocity.Equals(TypeBeatModPuppeteer.V_EPSILON));
            Assert.Less(TypeBeatModPuppeteer.V_EPSILON, TypeBeatModConductor.TEMPO_FLOOR_RATE);
        }

        /// <summary>
        /// NON-VACUITY FOR THE WHOLE MODE PLUMBING: the two presets really do produce different tapes
        /// on the same key schedule. Without this every "the right preset was used" pin below and in
        /// <c>TypeBeatPuppeteerReplayTest</c> could be satisfied by a transform that ignored the
        /// toggle entirely.
        ///
        /// <para>SINCE BACKLOG 266 THE DIFFERENCE IS THE EASE ALONE, because the two ceilings now
        /// agree. This test used to lean on the ceiling (the scripted typist ran past 1.6x under the
        /// frequency preset and was walled under the tempo one), which would make it vacuous today,
        /// so it is re-based on the TRAJECTORY: the same schedule through a 300 ms filter and a
        /// 120 ms one is a different curve at almost every millisecond, and it lands the tape
        /// somewhere else.</para>
        /// </summary>
        [Test]
        public void TheTwoPresetsProduceGenuinelyDifferentTapesOnTheSameSchedule()
        {
            var schedule = keySchedule();
            const int wall_ms = 9000;

            var tempo = trajectory(PuppeteerTuning.Tempo, PuppeteerState.AnchoredAt(0), schedule, wall_ms);
            var frequency = trajectory(PuppeteerTuning.Frequency, PuppeteerState.AnchoredAt(0), schedule, wall_ms);

            Assert.Greater(Math.Abs(tempo[wall_ms].PositionMs - frequency[wall_ms].PositionMs), 100,
                $"the two presets landed the tape in the same place ({tempo[wall_ms].PositionMs:N0} against {frequency[wall_ms].PositionMs:N0}), so nothing downstream can prove it read the toggle");

            // Both are still tapes: monotonic (asserted by the helper) and inside their own bands.
            Assert.LessOrEqual(tempo.Max(s => s.Velocity), TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY + 1e-12);
            Assert.LessOrEqual(frequency.Max(s => s.Velocity), TypeBeatModPuppeteer.V_MAX + 1e-12);

            // ...and THE EASE is the reason, not a rounding difference: the gentler filter is behind
            // the sharper one through the whole spin-up, so the two curves separate everywhere the
            // schedule actually moves the reel rather than at one clipped peak.
            int apart = Enumerable.Range(1, wall_ms).Count(ms => Math.Abs(tempo[ms].Velocity - frequency[ms].Velocity) > 0.05);

            Assert.Greater(apart, wall_ms / 4,
                $"the two velocity curves were within 5% of each other for all but {apart} of {wall_ms} ms, which is not two presets");
        }

        /// <summary>
        /// A TYPIST FASTER THAN THE CEILING IS TRAILED, NOT CHASED, which is the owner's stated trade
        /// written as arithmetic. The chase law is a POSITION law, so a velocity it cannot have
        /// simply leaves position error on the table: the tape settles at exactly
        /// <see cref="TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY"/> and the gap to the caret grows at
        /// exactly the difference. Nothing breaks, nothing is refused, and this mod does not judge on
        /// that distance at all.
        ///
        /// <para>BACKLOG 266 RE-TUNED THE NUMBER AND NOT THE BEHAVIOUR. The ceiling was 1.6x, so the
        /// typist this happened to was a 1.9x one and the frequency preset chased them where the
        /// tempo preset trailed them; the ceiling is 2.0x now, so a 1.9x typist is CHASED in both
        /// modes and the trade starts above 2.0x, in both. Both halves are pinned: the trailing at
        /// the new number, and the fact that the old fixture's typist has stopped being trailed,
        /// which is what the owner actually bought.</para>
        /// </summary>
        [Test]
        public void ATypistPastTheCeilingIsTrailedRatherThanChased()
        {
            // At the map's own pace the ceiling is not in the way, so the two modes agree on the
            // steady state: it is the chase horizon's, exactly as it always was. It is measured at
            // 12 seconds rather than at 6 because the longer ease costs DAMPING as well as speed
            // (the chase loop is position over velocity, so a bigger time constant is a lower
            // damping ratio, about 0.35 against the frequency preset's 0.56), and the ring takes
            // roughly twice as long to die. It rings around 1.00x, not around anything else.
            var onPace = trajectory(PuppeteerTuning.Tempo, PuppeteerState.AnchoredAt(0), steadyTypist(1), 12000);

            Assert.AreEqual(1.0, onPace[12000].Velocity, 1e-6);
            Assert.AreEqual(TypeBeatModPuppeteer.T_CHASE_MS, 12000 - onPace[12000].PositionMs, 1.5,
                "an on-pace player must trail by the chase horizon in tempo mode too");

            const double pace = 2.4;

            var fast = trajectory(PuppeteerTuning.Tempo, PuppeteerState.AnchoredAt(0), steadyTypist(pace), 8000);

            Assert.Greater(pace, TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY, "the fixture's typist has to be past the ceiling");

            Assert.AreEqual(TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY, fast[8000].Velocity, 1e-9,
                "the tape must pin at the ceiling rather than push through it");

            double gapAt6000 = (pace * 6000) - fast[6000].PositionMs;
            double gapAt8000 = (pace * 8000) - fast[8000].PositionMs;

            Assert.AreEqual((pace - TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY) * 2000, gapAt8000 - gapAt6000, 1e-3,
                "the excess has to be absorbed as POSITION error, at exactly the rate the tape is short by");

            Assert.Greater(gapAt8000, pace * TypeBeatModPuppeteer.T_CHASE_MS * 3, "...and the tape really is a long way further back than the chase horizon");

            // The SAME wall in frequency mode, since backlog 266 made the two ceilings one number.
            // This used to be the contrast half of the test, the frequency preset chasing a typist
            // the tempo preset trailed, and it is now the agreement half.
            var alsoFast = trajectory(PuppeteerTuning.Frequency, PuppeteerState.AnchoredAt(0), steadyTypist(pace), 8000);

            Assert.AreEqual(TypeBeatModPuppeteer.V_MAX, alsoFast[8000].Velocity, 1e-9,
                "both presets wall at the same rate now, so both trail the same typist");

            // ...and WHAT THE OWNER BOUGHT: the typist this test used to be written against, 1.9x,
            // is now CHASED in tempo mode rather than trailed. Under the old 1.6 ceiling the tape
            // would have pinned there and fallen behind at 0.3 ms of song per wall ms.
            const double was_trailed = 1.9;

            // Measured at twelve seconds rather than eight for the same reason the on-pace case
            // above is: the tempo preset's longer ease is a lower damping ratio, so the ring around
            // the settled value takes about twice as long to die.
            var nowChased = trajectory(PuppeteerTuning.Tempo, PuppeteerState.AnchoredAt(0), steadyTypist(was_trailed), 12000);

            Assert.AreEqual(was_trailed, nowChased[12000].Velocity, 1e-6,
                "the raised ceiling has to actually chase the typist it used to wall");

            Assert.Less((was_trailed * 12000) - nowChased[12000].PositionMs, (was_trailed * TypeBeatModPuppeteer.T_CHASE_MS) + 2,
                "...so their trailing gap is the chase horizon's again, not the ceiling's");
        }

        /// <summary>
        /// THE PARK STILL PARKS. The tempo preset only slows the velocity trajectory down, so the
        /// wind-down is the same monotonic drag to the same crawl, over a longer constant.
        ///
        /// <para>On the way there it descends THROUGH the band a time-stretcher sounds worst in, and
        /// that is documented rather than fought: the descent is a transient of well under a second
        /// of audible degradation, and the destination is near-silence, because the sub-floor split
        /// hands the remainder to the FREQUENCY and floors the real output at 100 Hz. Fighting it
        /// would mean switching modes mid-descent, which trades a smooth ugly moment for a click.
        /// </para>
        /// </summary>
        [Test]
        public void TheTempoParkStillReachesTheCrawlThroughTheStretchersUglyBand()
        {
            const int wall_ms = 6000;
            const double target = 1000;

            var settled = trajectory(PuppeteerTuning.Tempo, PuppeteerState.AnchoredAt(0), steadyTypist(1), 6000)[6000];

            var states = trajectory(PuppeteerTuning.Tempo, settled with { PositionMs = target - TypeBeatModPuppeteer.T_CHASE_MS },
                _ => new PuppeteerArm(target, TypeBeatModPuppeteer.V_MAX), wall_ms);

            for (int ms = 2; ms <= wall_ms; ms++)
            {
                Assert.Less(states[ms].Velocity, states[ms - 1].Velocity,
                    $"the reel did not wind DOWN at wall ms {ms}: a tape stop must be monotonic in either mode");
            }

            Assert.AreEqual(TypeBeatModPuppeteer.V_EPSILON, states[wall_ms].Velocity, 1e-6,
                "the crawl is the destination in both modes, and it is never zero");

            double overshoot = states[wall_ms].PositionMs - target;

            Assert.Greater(overshoot, -1, "the tape has to ARRIVE at the caret's target");

            // IT RUNS FURTHER PAST THE CARET THAN IN FREQUENCY MODE, and that is the price of the
            // longer ease rather than a defect: a smoothed velocity cannot be zero at the instant
            // the gap closes, so the reel spends about one smoothing constant's worth of momentum.
            // Measured at 239 ms here against 61 ms under the frequency preset. It is bounded by
            // that constant, and it is inaudible as a POSITION because this mod does not judge on
            // the distance between a press and its target at all.
            Assert.Less(overshoot, TypeBeatModPuppeteer.SMOOTHING_TAU_TEMPO_MS,
                "the reel's momentum must stay inside one smoothing constant of song");

            Assert.Greater(overshoot, TypeBeatModPuppeteer.T_CHASE_MS,
                "...and this really is the longer-momentum case, not the frequency preset's 61 ms");

            // It really does pass through the stretcher's ugly band, and it does not stop there:
            // about a second of the six, on the way to a destination that is near-silence.
            int ugly = states.Count(s => s.Velocity > TypeBeatModConductor.TEMPO_FLOOR_RATE && s.Velocity < 0.6);

            Assert.Greater(ugly, 0, "the descent has to cross the band, or this pin is describing something else");
            Assert.Less(ugly, 1500, "the tape LINGERED in the stretcher's worst band instead of passing through it");

            // ...and the destination is publishable: the split keeps the aggregate tempo above the
            // 0.05 TrackBass throws under, while the product is still the command bit for bit.
            (double tempo, double frequency) = TypeBeatModConductor.TrackAdjustmentsFor(TypeBeatModPuppeteer.V_EPSILON, false);

            Assert.GreaterOrEqual(tempo, TypeBeatModConductor.TEMPO_FLOOR_RATE);
            Assert.Less(frequency, 0.05, "...and the remainder is a frequency low enough that the park is near-silence");
            Assert.IsTrue((tempo * frequency).Equals(TypeBeatModPuppeteer.V_EPSILON));
        }

        /// <summary>
        /// The longer ease, measured the way the frequency one is: with the request pinned at the cap
        /// throughout, one time constant covers exactly 1 - 1/e of the distance, so
        /// <see cref="TypeBeatModPuppeteer.SMOOTHING_TAU_TEMPO_MS"/> is the filter's constant and not
        /// an approximation of one. And the tempo tape is genuinely BEHIND the frequency one at every
        /// point of the spin-up, which is what "gentler under rapid modulation" buys.
        /// </summary>
        [Test]
        public void TempoModeSpinsTheReelUpOverTheLongerConstant()
        {
            var parked = at(0, TypeBeatModPuppeteer.V_EPSILON);

            // Still and miles ahead, so nobody is typing and the cap is a flat 1.00x: only the
            // filter moves, which is what makes the closed form exact.
            var arm = new PuppeteerArm(100000, TypeBeatModPuppeteer.V_MAX);

            int tempoTau = (int)TypeBeatModPuppeteer.SMOOTHING_TAU_TEMPO_MS;
            int frequencyTau = (int)TypeBeatModPuppeteer.SMOOTHING_TAU_MS;

            var tempo = trajectory(PuppeteerTuning.Tempo, parked, _ => arm, tempoTau);
            var frequency = trajectory(PuppeteerTuning.Frequency, parked, _ => arm, tempoTau);

            double expected = 1 + ((TypeBeatModPuppeteer.V_EPSILON - 1) * Math.Exp(-1));

            Assert.AreEqual(expected, tempo[tempoTau].Velocity, 1e-9,
                "one tempo time constant must cover exactly 1 - 1/e of the way to the cap");

            Assert.AreEqual(expected, trajectory(PuppeteerTuning.Frequency, parked, _ => arm, frequencyTau)[frequencyTau].Velocity, 1e-9,
                "...and the frequency preset's own constant still does the same, unchanged");

            for (int ms = 1; ms <= tempoTau; ms++)
            {
                Assert.Less(tempo[ms].Velocity, frequency[ms].Velocity,
                    $"the tempo tape was not gentler at wall ms {ms}, which is the only thing the longer constant buys");
            }
        }

        // -----------------------------------------------------------------------------------------
        // The wobble deadband (backlog 258). Driver-side, so it is pinned on the pure static.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// ORDINARY JITTER MUST NOT MODULATE THE AUDIO. The clock is never exactly on the model (a
        /// playback buffer, the interpolating clock's quantisation, the rate-scaled platform offset),
        /// so the correction term jitters around zero forever and a jittering rate is audible on a
        /// held note in a way a steady one is not. Inside
        /// <see cref="TypeBeatModPuppeteer.RATE_DEADBAND"/> of 1.00 the command is published as
        /// EXACTLY 1.00; outside it, real drift still moves the song exactly as it always did.
        /// </summary>
        [Test]
        public void TheRateDeadbandPublishesExactlyOneThroughOrdinaryJitter()
        {
            Assert.AreEqual(0.03, TypeBeatModPuppeteer.RATE_DEADBAND, 1e-12);

            // The band is on DRIFT of RATE_DEADBAND * T_CORRECT_MS = 7.5 ms, either side.
            foreach (double drift in new[] { 0d, 1, -1, 5, -5, 7, -7 })
            {
                Assert.IsTrue(TypeBeatModPuppeteer.CommandedFrequency(at(10000 + drift, 1), 10000).Equals(1d),
                    $"{drift} ms of clock drift modulated the audio, which is the wobble this band exists to remove");
            }

            // ...and real drift still moves it, at exactly the rate it always did.
            Assert.AreEqual(1.04, TypeBeatModPuppeteer.CommandedFrequency(at(10010, 1), 10000), 1e-12);
            Assert.AreEqual(0.96, TypeBeatModPuppeteer.CommandedFrequency(at(9990, 1), 10000), 1e-12);

            // TWO-SIDED and NARROW, which is what stops it becoming a shelf: the edges are 0.97 and
            // 1.03, and everything outside them is published untouched.
            Assert.IsTrue(TypeBeatModPuppeteer.CommandedFrequency(at(10000, 0.97), 10000).Equals(0.97));
            Assert.IsTrue(TypeBeatModPuppeteer.CommandedFrequency(at(10000, 1.03), 10000).Equals(1.03));
            Assert.IsTrue(TypeBeatModPuppeteer.CommandedFrequency(at(10000, 0.98), 10000).Equals(1d));
            Assert.IsTrue(TypeBeatModPuppeteer.CommandedFrequency(at(10000, 1.02), 10000).Equals(1d));

            // The band is applied to the CLAMPED command, so it can never resurrect a rate the audio
            // path cannot take: the bounds still answer first.
            Assert.AreEqual(TypeBeatModPuppeteer.V_EPSILON, TypeBeatModPuppeteer.CommandedFrequency(at(10000, 1), 100000), 1e-12);
            Assert.AreEqual(TypeBeatModPuppeteer.V_MAX, TypeBeatModPuppeteer.CommandedFrequency(at(10000, 1), -100000), 1e-12);
        }

        /// <summary>
        /// THE LIMIT CYCLE, which is the honest cost of the deadband and is bounded rather than
        /// merely small. Inside the band the clock is held at exactly 1.00x while the MODEL goes on
        /// integrating its own velocity, so the two creep apart; the creep stops being hidden the
        /// moment the command leaves the band, which by definition is
        /// <c>RATE_DEADBAND * T_CORRECT_MS</c> of drift, 7.5 ms. Then the real command is published,
        /// the clock is pulled back, and it repeats. Seven and a half milliseconds of song is under a
        /// video frame, far under the 40 ms the retired Conductor's phase deadband accepted, and
        /// meaningless to a mod that forgives timing outright.
        /// </summary>
        [Test]
        public void TheDeadbandsDriftIsBoundedAtRateDeadbandTimesTheCorrectionHorizon()
        {
            double bound = TypeBeatModPuppeteer.RATE_DEADBAND * TypeBeatModPuppeteer.T_CORRECT_MS;

            Assert.AreEqual(7.5, bound, 1e-12);

            // A model creeping half a percent fast against a clock that only moves as commanded,
            // which is the worst case the band can hide: the drift grows while it is hidden.
            const double velocity = 1.005;

            double position = 0;
            double clock = 0;
            double worst = 0;
            int held = 0;

            for (int ms = 1; ms <= 20000; ms++)
            {
                position += velocity;

                double command = TypeBeatModPuppeteer.CommandedFrequency(
                    new PuppeteerState(position, velocity, double.PositiveInfinity, 0, PuppeteerClock.NO_HELD_FLOOR), clock);

                if (command.Equals(1d))
                    held++;

                clock += command;

                worst = Math.Max(worst, Math.Abs(position - clock));
            }

            Assert.Less(worst, bound, $"the drift the deadband hides ran to {worst:N2} ms, past its own bound of {bound}");

            // Not vacuous in either direction: the cycle really does run (most of the time the audio
            // is held at exactly 1.00x, which is the point) and the drift really does accumulate to
            // most of the bound rather than staying at nothing.
            Assert.Greater(held, 10000, "the band never engaged, so this is not measuring the limit cycle at all");
            Assert.Greater(worst, bound * 0.5, "...and the drift never approached the bound, so the bound is not the binding constraint here");
        }

        /// <summary>
        /// THE DEADBAND CANNOT HOLD A PARK AT 1.00X, which is the one way a rate deadband can go
        /// badly wrong. The band is on the COMMAND, and the command does not drive the model: as the
        /// player stops, the model's velocity falls toward the crawl whatever is being published, so
        /// the command falls with it, crosses the lower edge at 0.97 and keeps falling. The crossing
        /// is momentary because the band is narrow, which is exactly why it has to stay narrow.
        /// </summary>
        [Test]
        public void TheDeadbandCannotHoldTheSongAtOneThroughAPark()
        {
            const int wall_ms = 6000;
            const double target = 1000;

            var settled = trajectory(PuppeteerTuning.Tempo, PuppeteerState.AnchoredAt(0), steadyTypist(1), 6000)[6000];

            var states = trajectory(PuppeteerTuning.Tempo, settled with { PositionMs = target - TypeBeatModPuppeteer.T_CHASE_MS },
                _ => new PuppeteerArm(target, TypeBeatModPuppeteer.V_MAX), wall_ms);

            // The clock glued to the model, so the correction is zero and the deadband is the ONLY
            // thing that could hold the command up.
            double[] commands = states.Select(s => TypeBeatModPuppeteer.CommandedFrequency(s, s.PositionMs)).ToArray();

            int held = commands.Count(c => c.Equals(1d));

            Assert.Greater(held, 0, "a two-sided band has to be crossed on the way down, or this pin is vacuous");

            Assert.AreEqual(TypeBeatModPuppeteer.V_EPSILON, commands[wall_ms], 1e-6,
                "the park completed: the deadband delayed nothing it could hold");

            Assert.Less(held, 200,
                $"the deadband held the song at 1.00x for {held} ms, which is a shelf in the middle of the wind-down rather than a crossing (measured at 55 ms)");

            // Below the band the command keeps FALLING, every millisecond, all the way to the floor.
            int exit = Array.FindLastIndex(commands, c => c.Equals(1d));

            for (int ms = exit + 2; ms <= wall_ms; ms++)
            {
                Assert.Less(commands[ms], commands[ms - 1],
                    $"the command stopped falling at wall ms {ms}, so the park stalled inside the band");
            }

            Assert.Less(commands[exit + 1], 1 - TypeBeatModPuppeteer.RATE_DEADBAND,
                "leaving the band must put the command clear below it, not on its edge");
        }

        // -----------------------------------------------------------------------------------------
        // Freeplay: timing is FORGIVEN, everything else is real. Driven through the replay scorer,
        // which is also the proof that the live seam and the replay seam apply the same scale.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// A three-cell line "abc" over [0, 12000] (cells target 0, 4000 and 8000), each character
        /// struck <paramref name="pressOffset"/> ms late, or <paramref name="text"/> typed instead of
        /// "abc". Modelled on <c>TypeBeatRateModTest.scoreThreeLatePresses</c>.
        /// </summary>
        private static TypeBeatReplayAccount scoreThreePresses(double pressOffset, string text = "abc", params Mod[] mods)
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

            for (int i = 0; i < text.Length; i++)
                replay.Frames.Add(new TypeBeatReplayFrame((i * 4000) + pressOffset, text[i]));

            return TypeBeatReplayScorer.Score(map, mods, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
        }

        /// <summary>
        /// TIMING IS FORGIVEN. Presses 1900 ms late are the bottom tier of the ladder unmodded (the
        /// Line granularity Meh window is 2000 late); under Puppeteer every one of them is a Great,
        /// because under strict following the distance between a press and its target is a readout
        /// of the model's own trailing gap and says nothing about the player.
        ///
        /// <para>Driven through the replay scorer on purpose: that is the seam a stored run is
        /// re-judged on, and a live play that forgave what a rescore punished would be two different
        /// accounts of the same fingers.</para>
        /// </summary>
        [Test]
        public void EveryPressJudgesGreatUnderPuppeteerHoweverLateItLands()
        {
            var plain = scoreThreePresses(1900);
            var puppeteered = scoreThreePresses(1900, "abc", new TypeBeatModPuppeteer());

            Assert.AreEqual(3, plain.Statistics.GetValueOrDefault(HitResult.Meh),
                "1900 ms late is the bottom tier of the unmodded ladder");
            Assert.AreEqual(0, plain.Statistics.GetValueOrDefault(HitResult.Great));

            Assert.AreEqual(3, puppeteered.Statistics.GetValueOrDefault(HitResult.Great),
                "the window scale did not reach the replay scorer's engine");
            Assert.AreEqual(0, puppeteered.Statistics.GetValueOrDefault(HitResult.Meh));
            Assert.AreEqual(0, puppeteered.Statistics.GetValueOrDefault(HitResult.Ok));

            // Completion is untouched: the same three cells were typed either way.
            Assert.AreEqual(plain.Completion, puppeteered.Completion, 1e-12);
            Assert.AreEqual(3, puppeteered.MaxCombo);

            // ...and the forgiveness is TOTAL rather than merely generous. Ten seconds late is past
            // the bottom of the unmodded ladder entirely (a Lagging press, which is not even a Meh),
            // and it is still a Great here, which is what makes the mod's grade accuracy-only.
            //
            // What the scale does NOT do is keep a line alive: presses past the line's seal are
            // never judged at all, because sealing is a caret and lifecycle rule rather than a
            // window one, and this mod deliberately touches neither.
            Assert.AreEqual(0, scoreThreePresses(10000).Statistics.GetValueOrDefault(HitResult.Great));
            Assert.AreEqual(3, scoreThreePresses(10000, "abc", new TypeBeatModPuppeteer()).Statistics.GetValueOrDefault(HitResult.Great));

            // The live seam and the replay seam are the same constant, applied the same way (see
            // TypeBeatModPuppeteer.ApplyToDrawableRuleset and TypeBeatReplayScorer.createEngine).
            Assert.IsInstanceOf<IApplicableToDrawableRuleset<TypeBeatHitObject>>(new TypeBeatModPuppeteer(),
                "Puppeteer would scale a replay's windows but not a live play's");

            Assert.AreEqual(1e6, TypeBeatModPuppeteer.WINDOW_SCALE, 1e-9);
        }

        /// <summary>
        /// ...and NOTHING ELSE is forgiven. A wrong character is still wrong under Puppeteer: the
        /// mod widens the timing ladder and touches no other part of judgement, so accuracy,
        /// completion and the grade all still measure what the player actually typed.
        /// </summary>
        [Test]
        public void AWrongCharacterIsStillWrongUnderPuppeteer()
        {
            var clean = scoreThreePresses(1900, "abc", new TypeBeatModPuppeteer());
            var typo = scoreThreePresses(0, "axc", new TypeBeatModPuppeteer());

            Assert.AreEqual(2, typo.Statistics.GetValueOrDefault(HitResult.Great),
                "only the two correct characters may be paid");

            Assert.AreEqual(1, typo.Statistics.GetValueOrDefault(TypeBeatResultMapping.UNFIXED_TYPO),
                "the wrong character must still resolve as an unfixed typo, however wide the timing windows are");

            Assert.Less(typo.Accuracy, clean.Accuracy,
                "a typo must cost accuracy even though a press 1900 ms late does not");

            Assert.AreEqual(3, clean.Statistics.GetValueOrDefault(HitResult.Great),
                "...and the comparison is against a run that really was paid in full");
        }

        // -----------------------------------------------------------------------------------------
        // The RUSH CAP is exempt (backlog 261): the bug a sprinting player hit, and the two seams.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// THE BUG, as a field report: "typing too far ahead in conductor breaks combo but keeps 100%
        /// acc". Both halves of that sentence are the same press. The mod forgives timing outright
        /// (<see cref="TypeBeatModPuppeteer.WINDOW_SCALE"/>), so a press on the right character is a
        /// Great whatever the clock says, and accuracy stays perfect; but the PLAYHEAD under this mod
        /// is the tape the player is dragging, and the tape is walled at the preset's ceiling, so a
        /// player typing faster than the wall opens a CHARACTER lead that grows without bound. The
        /// unpinned caret's rush cap then broke the combo on a press it was still paying in full, and
        /// could not re-arm while the sprint continued.
        ///
        /// <para>The worked case below is the report's: cells 150 ms apart typed every 60 ms, which
        /// is about 200 words a minute, so the lead grows by 0.6 characters per character and is past
        /// the cap of five within ten presses. That is an ordinary fast player rather than a
        /// pathological one, which is why no larger cap VALUE is the fix: the lead has no bound to
        /// pick a number against.</para>
        ///
        /// <para>Pinned through the LIVE stack, built exactly as gameplay builds it, so this is also
        /// the proof that the flag is actually set rather than merely settable.</para>
        /// </summary>
        [Test]
        public void ASprinterUnderPuppeteerKeepsEveryIncrementOfTheirCombo()
        {
            var judgements = new List<CharJudgement>();

            var engine = liveEngine(sprintMap(), new TypeBeatModPuppeteer());
            engine.CharJudged += judgements.Add;

            Assert.IsTrue(engine.FletcherEnabled, "the shipped caret is unpinned, which is what has a rush cap at all");
            Assert.IsTrue(engine.RushCapExempt, "...and the mod is what lifts it");

            int comboBreaks = 0;
            engine.ComboBroken += () => comboBreaks++;

            for (int i = 0; i < sprint_chars.Length; i++)
            {
                double time = 1000 + (60 * i);

                engine.Update(time);
                Assert.IsTrue(engine.ProcessKey(sprint_chars[i], time), $"'{sprint_chars[i]}' was refused at {time}");
            }

            double last = 1000 + (60 * (sprint_chars.Length - 1));

            // The lead really is unbounded in the only sense that matters here: it grew every press
            // and finished miles past the cap of five.
            Assert.Greater(engine.CharsAheadOfPlayhead(last), TypingEngine.FLETCHER_MAX_CHARS_AHEAD * 2,
                "the burst has to leave the cap far behind, or this is not the reported case");

            Assert.AreEqual(sprint_chars.Length, engine.Combo, "the sprint kept its whole run");
            Assert.AreEqual(sprint_chars.Length, engine.MaxCombo);
            Assert.AreEqual(0, comboBreaks);

            // ...and the other half of the report: every one of those presses was a Great, which is
            // what made the break read as arbitrary. Accuracy was never the thing that was wrong.
            Assert.AreEqual(sprint_chars.Length, judgements.Count);
            Assert.IsTrue(judgements.TrueForAll(j => j.Type == JudgementType.Great),
                "every press on the right character judges Great under this mod, which is the whole freeplay decision");

            // NON-VACUOUS, and the exact shape of the bug: the identical burst on the identical
            // engine with only the exemption taken away breaks, once, and never re-arms.
            var capped = liveEngine(sprintMap(), new TypeBeatModPuppeteer());
            capped.RushCapExempt = false;

            int cappedBreaks = 0;
            capped.ComboBroken += () => cappedBreaks++;

            for (int i = 0; i < sprint_chars.Length; i++)
            {
                double time = 1000 + (60 * i);

                capped.Update(time);
                Assert.IsTrue(capped.ProcessKey(sprint_chars[i], time));
            }

            Assert.AreEqual(1, cappedBreaks, "one break per excursion, and the excursion never ends");
            Assert.AreEqual(0, capped.Combo, "...so the run is dead for the rest of the sprint");
            Assert.Less(capped.MaxCombo, sprint_chars.Length);
        }

        /// <summary>
        /// THE EXEMPTION IS SET AT BOTH SEAMS, which is the same pairing the window scale has and for
        /// the same reason: a live run that kept its combo and a rescore that took it away would be
        /// two accounts of one set of fingers, and the rescore is what a stored score's max_combo is
        /// checked against.
        ///
        /// <para>The third consumer needs no line of its own and is pinned here as an identity:
        /// <c>PuppeteerReplayTransform</c>'s co-simulation builds its scratch engine through
        /// <see cref="TypeBeatReplayScorer.CreateEngine"/>, so the engine it reads its arms off is the
        /// engine that judges the derived frames.</para>
        ///
        /// <para>A MOD FLAG AND NOT AN ERA (see <see cref="TypingEngine.RushCapExempt"/>): the mod
        /// list is the whole mechanism, so an engine built without the mod is untouched.</para>
        /// </summary>
        [Test]
        public void TheRushCapExemptionReachesBothTheLiveEngineAndTheRescore()
        {
            Assert.IsTrue(liveEngine(sprintMap(), new TypeBeatModPuppeteer()).RushCapExempt);
            Assert.IsFalse(liveEngine(sprintMap()).RushCapExempt, "an ordinary play is measured exactly as it was");

            Assert.IsTrue(scorerEngine(new TypeBeatModPuppeteer()).RushCapExempt);
            Assert.IsFalse(scorerEngine().RushCapExempt);

            // No CONFIG bit carries it, so the engine default has to be the cap applying: a replay
            // with no mod list re-derives under the rule every ordinary run was played on.
            Assert.IsFalse(new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "bare", FolderPath = string.Empty, AudioFileName = "a.mp3" },
                Lines = new List<LyricLine>(),
                Granularity = TimingGranularity.Line,
            }).RushCapExempt);
        }

        /// <summary>
        /// A dense line for the sprint: twenty characters 150 ms apart, over
        /// <c>[1000, 4000]</c>. Dense enough that a 200 wpm burst (one character every 60 ms) leaves
        /// the cap of five behind within ten presses while every press is still on the right
        /// character, which is exactly the reported case.
        /// </summary>
        private const string sprint_chars = "abcdefghijklmnopqrst";

        private static TypeBeatBeatmap sprintMap()
        {
            var line = new LyricLine
            {
                RawText = sprint_chars,
                StartTime = 0,
                EndTime = 60000,
                SingEndTime = 4000,
                Units = new[] { new TimedUnit { Text = sprint_chars, StartTime = 1000, EndTime = 4000 } },
            };

            var map = new TypeBeatBeatmap();
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Line });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        /// <summary>
        /// The engine gameplay really builds: <c>DrawableTypeBeatRuleset</c> over the beatmap and the
        /// mod list, then handed the mods the framework's <c>applyRulesetMods</c> would hand it. It is
        /// never loaded into a hierarchy and does not need to be (the engine is a lazy property off
        /// the constructor's arguments), which is the same harness
        /// <c>TypeBeatModHardRockTest.liveEngine</c> uses.
        /// </summary>
        private static TypingEngine liveEngine(TypeBeatBeatmap map, params Mod[] mods)
        {
            var drawable = new DrawableTypeBeatRuleset(new TypeBeatRuleset(), map, mods);

            foreach (var mod in mods.OfType<IApplicableToDrawableRuleset<TypeBeatHitObject>>())
                mod.ApplyToDrawableRuleset(drawable);

            return drawable.Engine;
        }

        /// <summary>The engine a RESCORE builds over the same map and mods, which is also the one the replay transform co-simulates against.</summary>
        private static TypingEngine scorerEngine(params Mod[] mods)
        {
            var map = sprintMap();
            var lineObjects = map.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();

            return TypeBeatReplayScorer.CreateEngine(map, lineObjects, mods, RateWindowRule.ScaledByRate);
        }

        // -----------------------------------------------------------------------------------------

        /// <summary>Two three-character lines, sung four seconds apart, with a gap between them.</summary>
        private static TypingEngine twoLineEngine()
        {
            var lines = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = "abc",
                    StartTime = 2000,
                    EndTime = 12000,
                    SingEndTime = 10000,
                    Units = new[] { new TimedUnit { Text = "abc", StartTime = 4000, EndTime = 10000 } },
                },
                new LyricLine
                {
                    RawText = "abc",
                    StartTime = 12000,
                    EndTime = 30000,
                    SingEndTime = 28000,
                    Units = new[] { new TimedUnit { Text = "abc", StartTime = 22000, EndTime = 28000 } },
                },
            };

            return new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "puppeteer", FolderPath = string.Empty, AudioFileName = "a.mp3" },
                Lines = lines,
                Granularity = TimingGranularity.Line,
            });
        }

        /// <summary>
        /// A line long enough to be SPRINTED, and a long instrumental gap after it (backlog 261).
        ///
        /// <para>L0 "abcdefgh" sung over [4000, 10000]: eight cells 750 ms apart, so a player striking
        /// one every 250 wall ms is running at three times the song's own pace and the tape pins at
        /// its ceiling rather than merely creeping over 1.00x. L1's vocals do not arrive until 40000,
        /// so its cue is 38500 and the gap is thirty seconds of instrumental: far more than the 1500
        /// ms a hand-over borrows, which is what makes "the coast held" and "the hand-over blipped"
        /// two distinguishable claims.</para>
        /// </summary>
        private static TypingEngine sprintEngine() => engineOver(sprintLines(), "sprint");

        private static List<LyricLine> sprintLines() => new List<LyricLine>
        {
            new LyricLine
            {
                RawText = "abcdefgh",
                StartTime = 2000,
                EndTime = 12000,
                SingEndTime = 10000,
                Units = new[] { new TimedUnit { Text = "abcdefgh", StartTime = 4000, EndTime = 10000 } },
            },
            new LyricLine
            {
                RawText = "abc",
                StartTime = 12000,
                EndTime = 60000,
                SingEndTime = 58000,
                Units = new[] { new TimedUnit { Text = "abc", StartTime = 40000, EndTime = 46000 } },
            },
        };

        /// <summary>
        /// The same two lines as a BEATMAP, so <see cref="liveEngine"/> can build the shipped stack
        /// over them. That stack sets <see cref="TypingEngine.BoundedRush"/>, which moves the instant
        /// the caret is handed to the second line
        /// (<see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> earlier), and that is the second cue
        /// timing <see cref="TheCueIsNotARateEventUnderEitherEntryBound"/> covers.
        /// </summary>
        private static TypeBeatBeatmap sprintTwoLineMap()
        {
            var map = new TypeBeatBeatmap();
            int index = 0;

            foreach (var line in sprintLines())
                map.HitObjects.Add(new TypeBeatHitObject { StartTime = line.StartTime, LineIndex = index++, Line = line, Granularity = TimingGranularity.Line });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        /// <summary>
        /// A fixture for the CATCH-UP (backlog 266), which needs two things the sprint fixture does
        /// not have.
        ///
        /// <para>L0's eight cells are 750 ms apart, struck every 600 wall ms in the test, so the tape
        /// settles at 1.25x and the gap floor lands well below the preset's ceiling: there has to be
        /// room ABOVE the floor for a catch-up to be visible in.</para>
        ///
        /// <para>L1 has eight cells rather than three, so they are 857 ms apart rather than 3000, and
        /// a keypress on it is inside <see cref="TypeBeatModPuppeteer.PACE_STEP_MAX_MS"/>. A caret
        /// step larger than that is read as a DISCONTINUITY and credited to the pace estimate as
        /// nothing (see <see cref="PuppeteerClock.StepPace"/>), so on a sparse line an early
        /// keypress could not lift the sustained cap at all and the test would be measuring the wrong
        /// thing.</para>
        /// </summary>
        private static TypingEngine catchUpEngine() => engineOver(new List<LyricLine>
        {
            new LyricLine
            {
                RawText = "abcdefgh",
                StartTime = 2000,
                EndTime = 12000,
                SingEndTime = 10000,
                Units = new[] { new TimedUnit { Text = "abcdefgh", StartTime = 4000, EndTime = 10000 } },
            },
            new LyricLine
            {
                RawText = "abcdefgh",
                StartTime = 12000,
                EndTime = 50000,
                SingEndTime = 48000,
                Units = new[] { new TimedUnit { Text = "abcdefgh", StartTime = 30000, EndTime = 36000 } },
            },
        }, "catch-up");

        /// <summary>The same fixture with its first line already typed out, so the caret is past the end of it and the tape is on a coast.</summary>
        private static TypingEngine finishedCatchUpEngine()
        {
            var engine = catchUpEngine();

            double firstVocal = engine.Lines[0].FirstVocalTime;

            engine.Update(firstVocal);

            foreach (char c in "abcdefgh")
                Assert.IsTrue(engine.ProcessKey(c, firstVocal), $"'{c}' was refused by the engine");

            Assert.IsTrue(engine.IsLineComplete, "the caret has to be past the last cell for the gap to have started");

            return engine;
        }

        private static TypingEngine engineOver(List<LyricLine> lines, string title)
            => new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "a", Title = title, FolderPath = string.Empty, AudioFileName = "a.mp3" },
                Lines = lines,
                Granularity = TimingGranularity.Line,
            });
    }
}
