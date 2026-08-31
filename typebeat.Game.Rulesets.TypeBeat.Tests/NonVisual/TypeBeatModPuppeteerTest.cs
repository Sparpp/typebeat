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
        private static PuppeteerTuning tuning() => PuppeteerTuning.Default;

        // -----------------------------------------------------------------------------------------
        // The shipping surface.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void ReportsUnrankedFunModWithPtAcronym()
        {
            var mod = new TypeBeatModPuppeteer();

            Assert.AreEqual("Puppeteer", mod.Name);
            Assert.AreEqual("PT", mod.Acronym);
            Assert.AreEqual(ModType.Fun, mod.Type);
            Assert.IsFalse(mod.Ranked,
                "a song that meets the caret by construction has no timing left to price, so no leaderboard can hold it");
            Assert.IsTrue(mod.HasImplementation);
            Assert.IsNotNull(mod.Icon);

            // A tape one player is pulling cannot be shared with a room.
            Assert.IsFalse(mod.ValidForMultiplayer);
            Assert.IsFalse(mod.ValidForMultiplayerAsFreeMod);

            // No settings in v1: the whole mod is one behaviour, and every number in it is a feel
            // constant rather than a band the player is meant to reason about.
            Assert.IsEmpty(mod.SettingDescription.ToArray());
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
        /// What the mod does to audio, and the only thing it does: it writes its commanded rate onto
        /// the FREQUENCY half of the aggregate the gameplay clock's rate is read off
        /// (<c>GameplayClockExtensions.GetTrueGameplayRate</c> is sign * AggregateFrequency *
        /// AggregateTempo of exactly this component). The tempo half is never touched, which is what
        /// "frequency only" means in practice, and it is why the pitch bends with the speed.
        /// </summary>
        [Test]
        public void PublishesAFrequencyOnlyAdjustmentInsideTheModelsOwnBand()
        {
            var mod = new TypeBeatModPuppeteer();
            var adjustments = new AudioAdjustments();

            mod.ApplyToTrack(adjustments);

            Assert.AreEqual(1.0, adjustments.AggregateFrequency.Value, 1e-12);
            Assert.AreEqual(1.0, adjustments.AggregateTempo.Value, 1e-12);

            foreach (double rate in new[] { TypeBeatModPuppeteer.V_EPSILON, 0.25, 1.0, 1.75, TypeBeatModPuppeteer.V_MAX })
            {
                mod.SpeedChange.Value = rate;

                Assert.IsTrue(adjustments.AggregateFrequency.Value.Equals(rate),
                    $"the frequency must carry the whole rate exactly, got {adjustments.AggregateFrequency.Value:R} for {rate:R}");

                Assert.IsTrue(adjustments.AggregateTempo.Value.Equals(1d),
                    $"the tempo aggregate moved to {adjustments.AggregateTempo.Value:R} at rate {rate}: this mod publishes frequency ONLY");

                Assert.AreEqual(rate, adjustments.AggregateFrequency.Value * adjustments.AggregateTempo.Value, 1e-12,
                    "this product IS GetTrueGameplayRate");
            }

            // The band is enforced by the bindable, so nothing can publish a frequency the audio
            // path cannot track (or the exact zero that STOPS the track rather than slowing it).
            mod.SpeedChange.Value = 0;
            Assert.AreEqual(TypeBeatModPuppeteer.V_EPSILON, adjustments.AggregateFrequency.Value, 1e-12);
            Assert.Greater(adjustments.AggregateFrequency.Value, 0);

            mod.SpeedChange.Value = 51;
            Assert.AreEqual(TypeBeatModPuppeteer.V_MAX, adjustments.AggregateFrequency.Value, 1e-12);

            Assert.AreEqual(TypeBeatModConductor.PITCH_ABSOLUTE_MAX_RATE, TypeBeatModPuppeteer.V_MAX, 1e-12,
                "the frequency path's wall is one fact, and both followers must read the same number for it");
            Assert.AreEqual(TypeBeatModConductor.MIN_FREQUENCY_SCALE, TypeBeatModPuppeteer.V_EPSILON, 1e-12);
        }

        /// <summary>
        /// The submission payload. The acronym is the whole of what the server needs, because the
        /// server's only job for this mod is to recognise "PT" in its always-unranked list.
        /// </summary>
        [Test]
        public void WirePayloadIsTheBareAcronym()
        {
            Assert.AreEqual(@"{""acronym"":""PT""}", JsonConvert.SerializeObject(new APIMod(new TypeBeatModPuppeteer())));

            var decoded = JsonConvert.DeserializeObject<APIMod>(@"{""acronym"":""PT""}")!.ToMod(new TypeBeatRuleset());

            Assert.IsInstanceOf<TypeBeatModPuppeteer>(decoded, "a stored PT score must not resolve to UnknownMod");
            Assert.IsFalse(decoded.Ranked);
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
        {
            var states = new PuppeteerState[wallMs + 1];
            states[0] = start;

            for (int ms = 1; ms <= wallMs; ms++)
            {
                states[ms] = PuppeteerClock.Step(states[ms - 1], armAtMs(ms), tuning());

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
        /// </summary>
        [Test]
        public void ResumingSpinsTheReelBackUpOverTheSmoothingConstant()
        {
            var parked = new PuppeteerState(0, TypeBeatModPuppeteer.V_EPSILON);

            // A target 100 seconds ahead: the requested velocity is pinned at the cap throughout, so
            // the only thing moving is the filter.
            var arm = new PuppeteerArm(100000, TypeBeatModPuppeteer.V_MAX);

            var states = trajectory(parked, _ => arm, (int)TypeBeatModPuppeteer.SMOOTHING_TAU_MS);

            double expected = TypeBeatModPuppeteer.V_MAX
                              + ((TypeBeatModPuppeteer.V_EPSILON - TypeBeatModPuppeteer.V_MAX) * Math.Exp(-1));

            Assert.AreEqual(expected, states[(int)TypeBeatModPuppeteer.SMOOTHING_TAU_MS].Velocity, 1e-9,
                "one smoothing time constant must cover exactly 1 - 1/e of the way to the cap");

            // Not a step: a single millisecond moves it by well under a hundredth of the distance.
            Assert.Less(states[1].Velocity, 0.03);
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
        /// THE COAST ARM NEVER SPRINTS. A finished line's target is the NEXT line's first vocal,
        /// which can be many seconds away, so an uncapped chase would tear through the tail of every
        /// line and every instrumental gap. Capped at exactly 1.00x the song simply plays out, and
        /// then eases into a PARK at the next vocal if the player has not started typing yet.
        /// </summary>
        [Test]
        public void ACoastingTapeNeverExceedsTheSongsOwnSpeedAndParksOnTheNextVocal()
        {
            const double next_vocal = 8000;

            var engine = twoLineEngine();

            // Drive the engine to the pre-roll, where no line is active at all: that is a coast arm
            // by way of the production ArmFor, so this pin fails if the cap is ever taken off there.
            engine.Update(0);

            var arm = TypeBeatModPuppeteer.ArmFor(engine, 0);

            Assert.AreEqual(TypeBeatModPuppeteer.COAST_MAX_VELOCITY, arm.VelocityCap, 1e-12,
                "the intro coasts at the song's own speed");

            // Eight seconds of song between here and the next vocal, which is an enormous gap: an
            // uncapped chase would ask for 8000 / 150 and be pinned at V_MAX for most of the way.
            var states = trajectory(new PuppeteerState(0, 1), _ => new PuppeteerArm(next_vocal, arm.VelocityCap), 12000);

            for (int ms = 1; ms <= 12000; ms++)
            {
                Assert.LessOrEqual(states[ms].Velocity, TypeBeatModPuppeteer.COAST_MAX_VELOCITY + 1e-12,
                    $"the coast sprinted to {states[ms].Velocity:R} at wall ms {ms}, {next_vocal - states[ms].PositionMs:N0} ms short of the next vocal");
            }

            // ...and a tape that was already sprinting when the line ended EASES down to the cap
            // rather than snapping to it: the cap bounds where the velocity is going, and the
            // smoothing constant is still what decides how fast it gets there.
            var hot = trajectory(new PuppeteerState(0, TypeBeatModPuppeteer.V_MAX),
                _ => new PuppeteerArm(next_vocal, arm.VelocityCap), 2000);

            for (int ms = 1; ms <= 2000; ms++)
            {
                Assert.LessOrEqual(hot[ms].Velocity, hot[ms - 1].Velocity + 1e-12,
                    $"a coasting tape sped back up at wall ms {ms}");
            }

            Assert.AreEqual(TypeBeatModPuppeteer.COAST_MAX_VELOCITY, hot[2000].Velocity, 1e-6);
            Assert.Greater(hot[1].Velocity, 1.9, "and it eases rather than snapping");

            // It really does play out at 1.00x for the whole approach, then eases in.
            Assert.AreEqual(1.0, states[2000].Velocity, 1e-6, "a coast far from its target rides the cap exactly");

            Assert.AreEqual(next_vocal, states[12000].PositionMs, TypeBeatModPuppeteer.T_CHASE_MS / 2,
                "the tape must park ON the next vocal, waiting for the player (within the same momentum overshoot a tape stop has)");

            Assert.Less(states[12000].Velocity, 0.01, "and it eases into the park rather than arriving at speed");
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

                target[ms] = coasting ? 12000 : caret;
                cap[ms] = coasting ? TypeBeatModPuppeteer.COAST_MAX_VELOCITY : TypeBeatModPuppeteer.V_MAX;
            }

            return ms => new PuppeteerArm(target[ms], cap[ms]);
        }

        // -----------------------------------------------------------------------------------------
        // The arms: where the model meets the engine's real line lifecycle.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The three arms, read off a real engine. The one that is not obvious is the middle one:
        /// a line whose caret has run off the end aims at <c>ActiveLineIndex + 1</c> and NOT at
        /// <see cref="TypingEngine.NextUnsealedLineIndex"/>, because a finished line has not SEALED
        /// yet, so the next-unsealed index is still that same line and its first vocal is already
        /// behind the tape.
        /// </summary>
        [Test]
        public void ArmsFollowTheEnginesOwnLineLifecycle()
        {
            var engine = twoLineEngine();

            double firstVocal = engine.Lines[0].FirstVocalTime;
            double secondVocal = engine.Lines[1].FirstVocalTime;

            // 1. The pre-roll: no line active at all, so coast toward the first vocal.
            engine.Update(0);

            Assert.AreEqual(-1, engine.ActiveLineIndex);

            var intro = TypeBeatModPuppeteer.ArmFor(engine, 0);

            Assert.AreEqual(firstVocal, intro.DesiredPositionMs, 1e-9);
            Assert.AreEqual(TypeBeatModPuppeteer.COAST_MAX_VELOCITY, intro.VelocityCap, 1e-12);

            // 2. A live caret on the first line: chase that cell's own target, uncapped.
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
            Assert.AreEqual(0, engine.NextUnsealedLineIndex, "the finished line has not sealed yet, which is the whole trap");

            var finished = TypeBeatModPuppeteer.ArmFor(engine, firstVocal + 10);

            Assert.AreEqual(secondVocal, finished.DesiredPositionMs, 1e-9,
                "a finished line must coast toward the NEXT line's vocal, not back to its own");
            Assert.AreEqual(TypeBeatModPuppeteer.COAST_MAX_VELOCITY, finished.VelocityCap, 1e-12);

            Assert.Greater(finished.DesiredPositionMs, firstVocal + 10, "...and that target is genuinely ahead of the tape");
        }

        /// <summary>
        /// The outro: past the last line there is nothing left to aim at, so the arm's CAP is the
        /// whole of it and the song plays itself out at its own speed. Pinned because the
        /// alternative (a finite target behind the tape) would park the reel over the outro.
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

            var outro = TypeBeatModPuppeteer.ArmFor(engine, 60000);

            Assert.AreEqual(double.PositiveInfinity, outro.DesiredPositionMs);
            Assert.AreEqual(TypeBeatModPuppeteer.COAST_MAX_VELOCITY, outro.VelocityCap, 1e-12);

            // An unbounded target is not a NaN generator: it simply pins the request at the cap.
            var states = trajectory(new PuppeteerState(60000, 1), _ => outro, 2000);

            Assert.AreEqual(1.0, states[2000].Velocity, 1e-9);
            Assert.AreEqual(62000, states[2000].PositionMs, 1e-6);
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
            var state = new PuppeteerState(10000, 1.2);

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
    }
}
