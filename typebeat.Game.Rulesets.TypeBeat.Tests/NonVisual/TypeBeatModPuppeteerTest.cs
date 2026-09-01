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

            // ...and a tape that was still sprinting when the line ended EASES down to 1.00x rather
            // than snapping to it: the cap bounds where the velocity is going, and the smoothing
            // constant is still what decides how fast it gets there.
            var hot = trajectory(at(0, TypeBeatModPuppeteer.V_MAX), _ => arm, 2000);

            for (int ms = 1; ms <= 2000; ms++)
            {
                Assert.LessOrEqual(hot[ms].Velocity, hot[ms - 1].Velocity + 1e-12,
                    $"a coasting tape sped back up at wall ms {ms}");
            }

            Assert.AreEqual(TypeBeatModPuppeteer.COAST_MAX_VELOCITY, hot[2000].Velocity, 1e-6);
            Assert.Greater(hot[1].Velocity, 1.9, "and it eases rather than snapping");
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
        /// The outro: past the last line the song plays itself out at its own speed, which is the
        /// same coast arm as every other off-line stretch. An infinite target is not a NaN generator,
        /// it simply pins the request at the cap.
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

            var states = trajectory(at(60000, 1), _ => outro, 2000);

            Assert.AreEqual(1.0, states[2000].Velocity, 1e-9);
            Assert.AreEqual(62000, states[2000].PositionMs, 1e-6);
        }

        /// <summary>
        /// The model and a real engine, one canonical tick at a time, which is exactly the cadence
        /// <c>PuppeteerReplayTransform</c> co-simulates at. Asserts the tape's monotonicity on every
        /// step, as <see cref="trajectory"/> does.
        /// </summary>
        private static PuppeteerState[] against(TypingEngine engine, PuppeteerState start, int wallMs)
        {
            var states = new PuppeteerState[wallMs + 1];
            states[0] = start;

            for (int ms = 1; ms <= wallMs; ms++)
            {
                double position = states[ms - 1].PositionMs;

                engine.Update(position);

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

            for (int ms = 1; ms <= cue; ms++)
            {
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
