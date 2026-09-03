// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The Puppeteer REPLAY ERA (backlog 256, phase 2): a run under the mod stores WALL stamps
    /// behind CONFIG frame bit 9, and its track times are re-derived by re-running the tape model.
    ///
    /// <para>Four things are pinned here. THE BIT, on the same terms every era bit before it was
    /// pinned on: a legacy round trip, a value of 512, and every older bit left exactly where it
    /// was. THE AXIS DECISION, which is what the recorder writes and when. THE TRANSFORM, which is
    /// the heart: derived times monotonic, bit-identical twice, idempotent, and an ordinary replay
    /// handed straight back. And THE END TO END equivalence, that a simulated live run and the
    /// re-derivation of its stored wall stamps produce the same account.</para>
    ///
    /// <para>The model itself is <c>TypeBeatModPuppeteerTest</c>'s subject; nothing here re-pins
    /// it.</para>
    /// </summary>
    [TestFixture]
    public class TypeBeatPuppeteerReplayTest
    {
        // -----------------------------------------------------------------------------------------
        // The bit.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Bit 9, value 512, surviving the legacy .osr mapping (MouseY carries the flags word). The
        /// same round trip every era bit before it has, because the same thing would go wrong: a
        /// stored run whose header did not survive re-derives on the wrong rules, and here on the
        /// wrong AXIS, which is worse than any of them.
        /// </summary>
        [Test]
        public void WallClockFramesIsBitNineAndSurvivesTheLegacyRoundTrip()
        {
            var frame = TypeBeatReplayFrame.CreateConfigFrame(0, false, wallClockFrames: true);

            var legacy = frame.ToLegacy(new TypeBeatBeatmap());

            Assert.AreEqual(512, (int)legacy.MouseY!.Value, "bit 9 is 512 and nothing else may be set");
            Assert.AreEqual(0, (int)legacy.MouseX!.Value, "a CONFIG frame's MouseX is still the NUL sentinel");

            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(legacy, new TypeBeatBeatmap());

            Assert.IsTrue(decoded.WallClockFrames);
            Assert.IsFalse(decoded.AllowWrongInput);
            Assert.IsFalse(decoded.FirstCharTiming);
        }

        /// <summary>
        /// The append-only rule: the new bit sits ABOVE every existing one and renumbers nothing, so
        /// a replay already on disk decodes bit for bit as it always did and simply reads false for
        /// the newest bit. The ten bits below and including this one make at most 1023, which the
        /// encoder carries exactly as harmlessly as the single bit it started with (backlog 259 has
        /// since added bit 10 above them, which this fixture deliberately leaves clear: what it pins
        /// is that bit 9 and everything under it did not move).
        /// </summary>
        [Test]
        public void TheNewBitLeavesEveryOlderBitExactlyWhereItWas()
        {
            var everything = TypeBeatReplayFrame.CreateConfigFrame(0, true, true, true, true, true, true,
                flexibleLines: true, boundedRush: true, firstCharTiming: true, wallClockFrames: true);

            Assert.AreEqual(1023, (int)everything.ToLegacy(new TypeBeatBeatmap()).MouseY!.Value,
                "the ten bits this fixture sets, all of them, and nothing above them");

            // The word every live pre-256 stack wrote, and the one every stored replay carries.
            var live = TypeBeatReplayFrame.CreateConfigFrame(0, true, true, true, true, true, true,
                flexibleLines: true, boundedRush: true, firstCharTiming: true);

            Assert.AreEqual(511, (int)live.ToLegacy(new TypeBeatBeatmap()).MouseY!.Value,
                "the flags word a run without Puppeteer writes must not have moved at all");

            // ...and every older word decodes with the new bit clear, which is what those runs mean.
            foreach (int flags in new[] { 0, 1, 4, 256, 511 })
            {
                var decoded = new TypeBeatReplayFrame();
                decoded.FromLegacy(new LegacyReplayFrame(0, 0, flags, ReplayButtonState.None), new TypeBeatBeatmap());

                Assert.IsFalse(decoded.WallClockFrames, $"a stored replay with flags {flags} is not on the wall axis");
                Assert.AreEqual((flags & 1) != 0, decoded.AllowWrongInput);
                Assert.AreEqual((flags & 256) != 0, decoded.FirstCharTiming);
            }
        }

        // -----------------------------------------------------------------------------------------
        // The axis decision (what the recorder writes).
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// WITHOUT PUPPETEER NOTHING MOVED. The stamp is the lyric time the engine was fed at, the
        /// axis decision is false, and the CONFIG frame's word is the one every stored replay
        /// carries. This is the pin that says the era costs the other 99% of runs nothing.
        /// </summary>
        [Test]
        public void RecordingIsUntouchedWithoutPuppeteer()
        {
            Assert.IsFalse(TypeBeatReplayRecorder.WallStamps(null), "no mod, no wall axis");
            Assert.AreEqual(1234, TypeBeatReplayRecorder.StampFor(null, false, 1234), 1e-12);

            // ...and a mod that has not been through a gameplay frame has no axis to offer yet, so
            // the decision is still false rather than half-made.
            var unanchored = new TypeBeatModPuppeteer();

            Assert.IsNull(unanchored.AnchorMs);
            Assert.IsNull(unanchored.WallStampMs);
            Assert.IsFalse(TypeBeatReplayRecorder.WallStamps(unanchored));
            Assert.AreEqual(1234, TypeBeatReplayRecorder.StampFor(unanchored, false, 1234), 1e-12);
        }

        /// <summary>
        /// The one number that cannot be an anchor. A CONFIG frame is the first frame of a run, so
        /// its time IS its encoded delta, and <c>LegacyScoreDecoder</c> silently DROPS a frame whose
        /// delta reads "-12345" (stable's seed-frame sentinel): the run would lose its whole header,
        /// which is nine era bits and the anchor itself. The origin is a free choice, so it is moved
        /// by a millisecond and the transform reads back whatever was written.
        /// </summary>
        [Test]
        public void TheAnchorIsNudgedOffTheLegacyDecodersSeedSentinel()
        {
            Assert.AreEqual(-12344, TypeBeatReplayRecorder.AnchorTimeFor(-12345), 1e-12);

            foreach (double anchor in new[] { -12346d, -12344d, -3000d, 0d, 512d })
                Assert.AreEqual(anchor, TypeBeatReplayRecorder.AnchorTimeFor(anchor), 1e-12, "every other anchor is written as it is");
        }

        // -----------------------------------------------------------------------------------------
        // The transform.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void AnOrdinaryReplayIsHandedStraightBack()
        {
            var map = twoLineMap();

            var replay = new Replay();
            replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true, syllableTiming: true));
            replay.Frames.Add(new TypeBeatReplayFrame(2000, 'a'));

            Assert.IsFalse(PuppeteerReplayTransform.IsWallClockStamped(replay));

            Assert.IsTrue(ReferenceEquals(replay, PuppeteerReplayTransform.Derived(map, Array.Empty<Mod>(), replay)),
                "a track-time replay must not even be copied, let alone re-timed");
        }

        /// <summary>
        /// AUTOPLAY UNDER PUPPETEER STAYS ON TRACK TIME. The generator writes target times, which are
        /// lyric times by construction and have no wall axis behind them, so it sets no bit and the
        /// transform is a no-op on its output. Keying the transform on the BIT rather than on the mod
        /// list is what makes that true without the generator having to know the mod exists.
        /// </summary>
        [Test]
        public void AutoplayIsNeverWallStampedEvenWithPuppeteerInTheStack()
        {
            var map = twoLineMap();

            var generated = new TypeBeatAutoGenerator(map, syllableTiming: true).Generate();

            Assert.IsFalse(PuppeteerReplayTransform.IsWallClockStamped(generated),
                "an autoplay frame's time is a target time, which is already a lyric time");

            var mods = new Mod[] { new TypeBeatModPuppeteer() };

            Assert.IsTrue(ReferenceEquals(generated, PuppeteerReplayTransform.Derived(map, mods, generated)),
                "the mod being in the stack must not re-time frames that were never on the wall axis");
        }

        /// <summary>
        /// The shape of a derived stream: the CONFIG frame keeps the anchor it carried (that is what
        /// its time always was), every keystroke lands on a track time, the whole thing is monotonic
        /// because the tape never rewinds, and bit 9 is CLEAR because the stream is track time now.
        /// One derived frame per stored frame, so a caller's frame accounting means the same thing
        /// on both sides.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void AWallStampedRunIsDerivedBackToTrackTime(bool adjustPitch)
        {
            var map = twoLineMap();
            var mods = puppeteer(adjustPitch);

            var run = simulateLiveRun(map, mods, defaultKeys, anchor: -2000, frameMs: 16);

            var derived = PuppeteerReplayTransform.Derive(map, mods, wallReplay(run));

            Assert.AreEqual(run.WallFrames.Count, derived.Count, "one derived frame per stored frame");

            Assert.IsTrue(derived[0].IsConfig);
            Assert.AreEqual(-2000, derived[0].Time, 1e-12, "the CONFIG frame keeps the anchor");
            Assert.IsFalse(derived[0].WallClockFrames, "a derived stream must describe itself as track time");
            Assert.IsTrue(derived[0].SyllableTiming, "...while every judgement bit it carried survives");

            for (int i = 1; i < derived.Count; i++)
            {
                Assert.GreaterOrEqual(derived[i].Time, derived[i - 1].Time,
                    $"derived frame {i} went backwards, which the tape's own monotonicity forbids");

                Assert.AreEqual(run.WallFrames[i].Character, derived[i].Character, $"frame {i} changed character");
                Assert.IsFalse(derived[i].WallClockFrames);
            }

            // Not vacuous: the stored stamps and the derived times are genuinely different numbers,
            // which is the whole reason this transform exists.
            Assert.Greater(derived.Skip(1).Zip(run.WallFrames.Skip(1), (d, w) => Math.Abs(d.Time - w.Time)).Max(), 100,
                "the wall axis and the track axis came out the same, so nothing was really re-derived");
        }

        /// <summary>
        /// BIT-LEVEL DETERMINISM. The transform reads the frames, the beatmap and the mods and
        /// nothing else (no wall clock, no frame timing, no random), so the same stored run derives
        /// to the identical times twice. This is the property the whole era rests on: it is what
        /// makes a stored Puppeteer run mean one thing rather than whatever the watching machine
        /// happened to do.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void TheTransformIsBitIdenticalTwice(bool adjustPitch)
        {
            var map = twoLineMap();
            var mods = puppeteer(adjustPitch);

            var stored = wallReplay(simulateLiveRun(map, mods, defaultKeys, anchor: -2000, frameMs: 16));

            var first = PuppeteerReplayTransform.Derive(map, mods, stored);
            var second = PuppeteerReplayTransform.Derive(map, mods, stored);

            Assert.AreEqual(first.Count, second.Count);

            for (int i = 0; i < first.Count; i++)
            {
                Assert.IsTrue(first[i].Time.Equals(second[i].Time),
                    $"frame {i} derived to {first[i].Time:R} and then to {second[i].Time:R}");

                Assert.AreEqual(first[i].Character, second[i].Character);
            }

            // ...and it is IDEMPOTENT, because the derived stream carries bit 9 clear. A second pass
            // over an already-derived run must not re-time it as though its track times were stamps.
            var again = PuppeteerReplayTransform.Derive(map, mods, trackReplay(first));

            for (int i = 0; i < first.Count; i++)
                Assert.IsTrue(first[i].Time.Equals(again[i].Time), $"a second pass moved frame {i}");
        }

        /// <summary>
        /// THE MODE IS PART OF WHAT A STORED RUN MEANS (backlog 258). The model's tuning is now a
        /// function of the mod's "Adjust pitch" toggle, so the transform reads that toggle off the
        /// stored mod list rather than assuming a preset: re-deriving a frequency-mode run under the
        /// tempo preset puts the watcher on a tape the player never heard, which is the same class of
        /// failure as moving a model constant, and the toggle rides into a stored score with the mod
        /// settings payload precisely so that it does not have to be inferred.
        ///
        /// <para>The wall stamps a run stores are <c>anchor + ticks</c>, which is a function of the
        /// key schedule and nothing else, so THE SAME STORED FRAMES can be derived under both
        /// presets. That is what makes this pin sharp: the input is identical and only the toggle
        /// differs, so any difference in the output is the toggle and nothing else.</para>
        /// </summary>
        [Test]
        public void AStoredRunIsDerivedUnderThePresetItsToggleNames()
        {
            var map = twoLineMap();

            var tempoMods = puppeteer(false);
            var pitchMods = puppeteer(true);

            Assert.IsTrue(PuppeteerTuning.Tempo.Equals(TypeBeatModPuppeteer.TuningFor(tempoMods)), "the default is the tempo preset");
            Assert.IsTrue(PuppeteerTuning.Frequency.Equals(TypeBeatModPuppeteer.TuningFor(pitchMods)));

            // A run whose mod list was lost, trimmed or resolved to UnknownMod reads as the default,
            // which is the same thing an absent toggle in the payload means.
            Assert.IsTrue(PuppeteerTuning.Tempo.Equals(TypeBeatModPuppeteer.TuningFor(Array.Empty<Mod>())));
            Assert.IsTrue(PuppeteerTuning.Tempo.Equals(TypeBeatModPuppeteer.TuningFor(null)));

            var stored = wallReplay(simulateLiveRun(map, tempoMods, defaultKeys, anchor: -2000, frameMs: 16));

            var underTempo = PuppeteerReplayTransform.Derive(map, tempoMods, stored);
            var underPitch = PuppeteerReplayTransform.Derive(map, pitchMods, stored);

            Assert.AreEqual(underTempo.Count, underPitch.Count);

            double worst = 0;

            for (int i = 0; i < underTempo.Count; i++)
                worst = Math.Max(worst, Math.Abs(underTempo[i].Time - underPitch[i].Time));

            Assert.Greater(worst, 20,
                $"the two presets derived the same tape from the same stamps (worst difference {worst:N1} ms), so nothing here proves the toggle was read at all");

            // ...and the run really was played on the tempo tape, so THAT is the one its own derived
            // times have to match. This is the assertion a wrong preset breaks.
            var run = simulateLiveRun(map, tempoMods, defaultKeys, anchor: -2000, frameMs: 16);

            for (int i = 1; i < underTempo.Count; i++)
            {
                Assert.IsTrue(underTempo[i].Time.Equals(run.TrackFrames[i].Time),
                    $"frame {i} derived to {underTempo[i].Time:R} where the live tempo run fed it at {run.TrackFrames[i].Time:R}");
            }
        }

        // -----------------------------------------------------------------------------------------
        // End to end.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// THE EQUIVALENCE PIN. A simulated live run under the driver's own shape produces two
        /// things: the track times its engine was actually fed at, and the wall stamps a recorder
        /// would have stored. Re-deriving the second must reproduce the account of the first.
        ///
        /// <para>The two are NOT computed the same way, which is what gives the pin content: the
        /// live driver samples its arm once per display frame and steps the model in batches, while
        /// the transform samples it every canonical millisecond. On this fixture they nonetheless
        /// come out EXACTLY equal, and that is worth understanding rather than assuming: the arm
        /// only moves when a keystroke moves the caret or a line boundary is crossed, keystrokes
        /// land on tick boundaries by construction (the stamp is a tick COUNT), and the tape is
        /// parked on the next vocal when the boundaries here are crossed, so the two cadences have
        /// nothing to disagree about. A map that crossed a line boundary while the tape was moving
        /// would separate them by a few milliseconds, and the ACCOUNT equality above is the pin that
        /// has to survive that; the exact time equality below is a bonus, and a very loud alarm if
        /// the wall origin is ever wrong.</para>
        ///
        /// <para>What this harness does NOT model is the gameplay clock's own lag behind the model
        /// (the correction transient and the audio buffer), which cannot be simulated without an
        /// audio stack. That is the source of the recorded seal-boundary limitation documented on
        /// <see cref="PuppeteerReplayTransform"/>, and it is documented rather than pinned
        /// precisely because a headless harness cannot honestly reproduce it.</para>
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void ADerivedRunReproducesTheLiveRunsAccount(bool adjustPitch)
        {
            var map = twoLineMap();
            var mods = puppeteer(adjustPitch);

            var run = simulateLiveRun(map, mods, defaultKeys, anchor: -2000, frameMs: 16);

            var live = account(map, mods, trackReplay(run.TrackFrames));
            var rederived = account(map, mods, wallReplay(run));

            Assert.AreEqual(live.MaxCombo, rederived.MaxCombo, "max combo moved under re-derivation");
            Assert.AreEqual(live.Accuracy, rederived.Accuracy, 1e-12);
            Assert.AreEqual(live.Completion, rederived.Completion, 1e-12);
            Assert.AreEqual(live.TotalScore, rederived.TotalScore);
            Assert.AreEqual(live.Rank, rederived.Rank);
            Assert.AreEqual(0, rederived.UnconsumedFrames, "every derived frame must be fed");

            foreach (var (result, count) in live.Statistics)
                Assert.AreEqual(count, rederived.Statistics.GetValueOrDefault(result), $"{result} count moved under re-derivation");

            Assert.AreEqual(live.Statistics.Count, rederived.Statistics.Count);

            // Not vacuous: the run actually typed something and was paid for it.
            Assert.AreEqual(defaultKeys.Count, live.Statistics.GetValueOrDefault(HitResult.Great));

            // ...and here the derived times ARE the live ones, to the millisecond. See the remarks:
            // this is exact rather than close because nothing on this fixture gives the two arm
            // cadences anything to disagree about. It is the sharp end of the pin, because a wrong
            // wall ORIGIN moves every one of these at once.
            var derived = PuppeteerReplayTransform.Derive(map, mods, wallReplay(run));

            double worst = 0;
            int worstFrame = 0;

            for (int i = 1; i < derived.Count; i++)
            {
                double drift = derived[i].Time - run.TrackFrames[i].Time;

                if (Math.Abs(drift) > Math.Abs(worst))
                {
                    worst = drift;
                    worstFrame = i;
                }
            }

            Assert.IsTrue(worst.Equals(0d),
                $"frame {worstFrame} derived {worst:N0} ms from where the live run fed it (at {derived[worstFrame].Time:R} rather than {run.TrackFrames[worstFrame].Time:R})");

            // Not vacuous: those times are real track times a long way from the wall stamps they
            // were derived from.
            Assert.Greater(derived[^1].Time, 20000);
        }

        /// <summary>
        /// ...AND WITH A HELD COAST IN THE TRAJECTORY (backlog 261). The hold lives on
        /// <see cref="PuppeteerState"/> rather than on the mod precisely so that this works with no
        /// edit to the transform at all: it threads a state through
        /// <see cref="PuppeteerClock.Step"/> and never builds the driver, so a hold parked on the
        /// driver would have been invisible to every stored run and a watcher would see a tape the
        /// player never heard.
        ///
        /// <para>The fixture sprints a line so the tape is well above the song's own speed when the
        /// caret runs off the end of it, then holds that speed across a thirty second instrumental
        /// gap. The account has to survive it and the derivation has to be bit identical twice, which
        /// is the same pair of claims the ordinary fixture makes, on a trajectory that exercises the
        /// new state field.</para>
        ///
        /// <para>The exact per-frame time equality of
        /// <see cref="ADerivedRunReproducesTheLiveRunsAccount"/> is deliberately NOT asserted here:
        /// this run crosses a line boundary while the tape is MOVING, which is the case those remarks
        /// name as the one that separates the live driver's per-frame arm cadence from the transform's
        /// per-millisecond one. The account is what has to survive that, and does.</para>
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void ADerivedRunReproducesAHeldCoast(bool adjustPitch)
        {
            var map = sprintMap();
            var mods = puppeteer(adjustPitch);

            var run = simulateLiveRun(map, mods, sprintKeys, anchor: -2000, frameMs: 16);

            Assert.Greater(run.PeakHeldCoast, 1.2,
                $"the scripted sprint only held {run.PeakHeldCoast:R}, so this is not a held-coast fixture at all");

            var live = account(map, mods, trackReplay(run.TrackFrames));
            var rederived = account(map, mods, wallReplay(run));

            Assert.AreEqual(live.MaxCombo, rederived.MaxCombo, "max combo moved under re-derivation");
            Assert.AreEqual(live.Accuracy, rederived.Accuracy, 1e-12);
            Assert.AreEqual(live.Completion, rederived.Completion, 1e-12);
            Assert.AreEqual(live.TotalScore, rederived.TotalScore);
            Assert.AreEqual(0, rederived.UnconsumedFrames);

            foreach (var (result, count) in live.Statistics)
                Assert.AreEqual(count, rederived.Statistics.GetValueOrDefault(result), $"{result} count moved under re-derivation");

            // Not vacuous: every character of both lines was typed, paid and credited, so the two
            // accounts being equal is a statement about a run that actually happened.
            Assert.AreEqual(sprintKeys.Count, live.Statistics.GetValueOrDefault(HitResult.Great));
            Assert.AreEqual(sprintKeys.Count, live.MaxCombo);

            // ...and the derivation is bit identical twice, on this trajectory as on the other.
            var stored = wallReplay(run);

            var first = PuppeteerReplayTransform.Derive(map, mods, stored);
            var second = PuppeteerReplayTransform.Derive(map, mods, stored);

            for (int i = 0; i < first.Count; i++)
                Assert.IsTrue(first[i].Time.Equals(second[i].Time), $"frame {i} derived to {first[i].Time:R} and then to {second[i].Time:R}");
        }

        /// <summary>
        /// ...and the SCORER runs the transform itself, so a caller that knows nothing about the era
        /// still gets the right account out of a stored wall-stamped run. Scoring the raw stamps as
        /// though they were lyric times is the failure this prevents, and it is not subtle: the
        /// keystrokes land at wall times that have no relation to the song.
        /// </summary>
        [Test]
        public void TheScorerDerivesAWallStampedRunBeforeJudgingIt()
        {
            var map = twoLineMap();
            var mods = new Mod[] { new TypeBeatModPuppeteer() };

            var run = simulateLiveRun(map, mods, defaultKeys, anchor: -2000, frameMs: 16);

            var scored = account(map, mods, wallReplay(run));

            Assert.AreEqual(defaultKeys.Count, scored.Statistics.GetValueOrDefault(HitResult.Great),
                "every character was typed, so every character must be paid");

            Assert.AreEqual(0, scored.Statistics.GetValueOrDefault(HitResult.Miss));
            Assert.AreEqual(1.0, scored.Completion, 1e-12, "the run typed the whole map");
            Assert.AreEqual(0, scored.UnconsumedFrames);

            // The stored replay itself is untouched by the scoring pass: it still says what it is.
            var stored = wallReplay(run);

            account(map, mods, stored);

            Assert.IsTrue(PuppeteerReplayTransform.IsWallClockStamped(stored),
                "scoring a run must not quietly rewrite the stored frames it was handed");
        }

        // -----------------------------------------------------------------------------------------
        // The harness.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Wall milliseconds (since the anchor) at which each character is struck, and the shape is
        /// chosen so the two axes genuinely disagree rather than merely differing by a constant.
        /// The player waits out the pre-roll, types two characters, then HESITATES for fifteen
        /// seconds mid-line, which is the mod's signature behaviour: the tape parks on their caret,
        /// so fifteen seconds of wall time buy almost no song at all. They then finish the line, wait
        /// out the instrumental gap the tape coasts through at 1.00x, and type the second line.
        ///
        /// <para>That hesitation is what makes the end-to-end pins non-vacuous. Fed on the wrong
        /// axis, every keystroke after it lands fifteen seconds late, which is past the first line's
        /// seal and into a different line entirely, so an account scored from the raw stamps cannot
        /// accidentally match the real one.</para>
        /// </summary>
        private static readonly IReadOnlyList<(int WallMs, char Character)> defaultKeys = new[]
        {
            (4200, 'a'), (5100, 'b'), (20100, 'c'),
            (33000, 'd'), (34500, 'e'), (36000, 'f'),
        };

        /// <summary>
        /// The mod stack for a run in one mode. Since backlog 258 the toggle selects the model
        /// preset, so it is an input to every harness here rather than a cosmetic setting.
        /// </summary>
        private static Mod[] puppeteer(bool adjustPitch)
            => new Mod[] { new TypeBeatModPuppeteer { AdjustPitch = { Value = adjustPitch } } };

        private sealed class LiveRun
        {
            public required double Anchor { get; init; }

            /// <summary>What a recorder would have stored: the CONFIG frame at the anchor, then wall stamps.</summary>
            public required List<TypeBeatReplayFrame> WallFrames { get; init; }

            /// <summary>What the live engine was actually fed: the same events at track times.</summary>
            public required List<TypeBeatReplayFrame> TrackFrames { get; init; }

            /// <summary>
            /// The largest held coast the run's tape ever carried (backlog 261), or
            /// <see cref="PuppeteerClock.NO_HELD_COAST"/> if it never held anything. Recorded so a
            /// co-simulation pin can say that the trajectory it re-derived actually had a hold in it.
            /// </summary>
            public required double PeakHeldCoast { get; init; }
        }

        /// <summary>
        /// The live driver's shape, headless. Per display frame it advances the tape by that frame's
        /// worth of canonical ticks under ONE arm (which is what
        /// <c>TypeBeatModPuppeteer.Update</c> does: the playfield dispatches the mod before its
        /// children, so the arm is sampled once and held), then feeds any keystroke that arrived
        /// during the frame to the engine at the tape's position.
        ///
        /// <para>The stamp it stores is <c>anchor + ticks</c>, which is
        /// <c>TypeBeatModPuppeteer.WallStampMs</c>'s definition: the number of ticks the model has
        /// been stepped, never the raw stopwatch.</para>
        /// </summary>
        private static LiveRun simulateLiveRun(IBeatmap map, IReadOnlyList<Mod> mods, IReadOnlyList<(int WallMs, char Character)> keys, double anchor, int frameMs)
        {
            var engine = engineFor(map, mods);

            var config = TypeBeatReplayFrame.CreateConfigFrame(anchor, true, true, true, true, true, true,
                flexibleLines: true, boundedRush: true, firstCharTiming: true, wallClockFrames: true);

            ReplayEngineFeed.Apply(engine, config);

            var wallFrames = new List<TypeBeatReplayFrame> { config };

            var trackConfig = TypeBeatReplayFrame.CreateConfigFrame(anchor, true, true, true, true, true, true,
                flexibleLines: true, boundedRush: true, firstCharTiming: true);

            var trackFrames = new List<TypeBeatReplayFrame> { trackConfig };

            var tape = PuppeteerState.AnchoredAt(anchor);

            long ticks = 0;
            int next = 0;
            double peakHeld = PuppeteerClock.NO_HELD_COAST;

            while (next < keys.Count)
            {
                engine.Update(tape.PositionMs);
                tape = PuppeteerClock.Run(tape, TypeBeatModPuppeteer.ArmFor(engine, tape.PositionMs), TypeBeatModPuppeteer.TuningFor(mods), frameMs);
                ticks += frameMs;

                peakHeld = Math.Max(peakHeld, tape.HeldCoastVelocity);

                while (next < keys.Count && keys[next].WallMs <= ticks)
                {
                    var fed = new TypeBeatReplayFrame(Math.Round(tape.PositionMs), keys[next].Character);

                    ReplayEngineFeed.Apply(engine, fed);

                    trackFrames.Add(fed);
                    wallFrames.Add(new TypeBeatReplayFrame(anchor + ticks, keys[next].Character));

                    next++;
                }
            }

            return new LiveRun { Anchor = anchor, WallFrames = wallFrames, TrackFrames = trackFrames, PeakHeldCoast = peakHeld };
        }

        private static Replay wallReplay(LiveRun run) => trackReplay(run.WallFrames);

        private static Replay trackReplay(IEnumerable<TypeBeatReplayFrame> frames)
        {
            var replay = new Replay();

            foreach (var frame in frames)
                replay.Frames.Add(clone(frame));

            return replay;
        }

        /// <summary>Fresh frame objects, so a test cannot accidentally share mutable state between two runs.</summary>
        private static TypeBeatReplayFrame clone(TypeBeatReplayFrame source) => new TypeBeatReplayFrame(source.Time, source.Character)
        {
            AllowWrongInput = source.AllowWrongInput,
            SpaceSkipsWord = source.SpaceSkipsWord,
            SyllableTiming = source.SyllableTiming,
            WrongInputOnWordGaps = source.WrongInputOnWordGaps,
            StrictSpaces = source.StrictSpaces,
            FlexibleLines = source.FlexibleLines,
            CharTimedStretch = source.CharTimedStretch,
            BoundedRush = source.BoundedRush,
            FirstCharTiming = source.FirstCharTiming,
            WallClockFrames = source.WallClockFrames,
        };

        private static TypeBeatReplayAccount account(IBeatmap map, IReadOnlyList<Mod> mods, Replay replay)
            => TypeBeatReplayScorer.Score(map, mods, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);

        private static TypingEngine engineFor(IBeatmap map, IReadOnlyList<Mod> mods)
        {
            var lineObjects = map.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();

            for (int i = 0; i < lineObjects.Count; i++)
                lineObjects[i].LineIndex = i;

            return TypeBeatReplayScorer.CreateEngine(map, lineObjects, mods, RateWindowRule.ScaledByRate);
        }

        /// <summary>
        /// A SPRINT and a long instrumental gap (backlog 261). Eight cells 750 ms apart struck every
        /// 250 wall ms, which is three times the song's own pace, so the tape is pinned at the
        /// preset's ceiling when the caret runs off the end of the line and the coast has a real speed
        /// to hold. The second line's vocals do not arrive until 40000, so the held coast runs for
        /// thirty seconds of song before the hand-over.
        ///
        /// <para>The last three keys are placed where the tape is parked on line 1's first cell under
        /// EITHER preset (the tempo tape crosses the gap slower, so it arrives later), which is what
        /// lets one schedule serve both.</para>
        /// </summary>
        private static readonly IReadOnlyList<(int WallMs, char Character)> sprintKeys = new[]
        {
            (4500, 'a'), (4750, 'b'), (5000, 'c'), (5250, 'd'),
            (5500, 'e'), (5750, 'f'), (6000, 'g'), (6250, 'h'),
            (30000, 'x'), (30500, 'y'), (31000, 'z'),
        };

        private static TypeBeatBeatmap sprintMap() => beatmap(
            new LyricLine
            {
                RawText = "abcdefgh",
                StartTime = 0,
                EndTime = 12000,
                SingEndTime = 10000,
                Units = new[] { new TimedUnit { Text = "abcdefgh", StartTime = 2000, EndTime = 8000 } },
            },
            new LyricLine
            {
                RawText = "xyz",
                StartTime = 12000,
                EndTime = 60000,
                SingEndTime = 58000,
                Units = new[] { new TimedUnit { Text = "xyz", StartTime = 40000, EndTime = 46000 } },
            });

        /// <summary>
        /// Two three-character lines with an instrumental stretch between them, so the tape has a
        /// pre-roll to coast through, a line to chase, a gap to coast again and a second line.
        /// </summary>
        private static TypeBeatBeatmap twoLineMap() => beatmap(
            new LyricLine
            {
                RawText = "abc",
                StartTime = 0,
                EndTime = 12000,
                SingEndTime = 10000,
                Units = new[] { new TimedUnit { Text = "abc", StartTime = 2000, EndTime = 8000 } },
            },
            new LyricLine
            {
                RawText = "def",
                StartTime = 12000,
                EndTime = 30000,
                SingEndTime = 28000,
                Units = new[] { new TimedUnit { Text = "def", StartTime = 20000, EndTime = 26000 } },
            });

        /// <summary>The lines as a playable beatmap, with the line indices the engine reads position off.</summary>
        private static TypeBeatBeatmap beatmap(params LyricLine[] lines)
        {
            var map = new TypeBeatBeatmap();

            for (int i = 0; i < lines.Length; i++)
                map.HitObjects.Add(new TypeBeatHitObject { StartTime = lines[i].StartTime, LineIndex = i, Line = lines[i], Granularity = TimingGranularity.Line });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }
    }
}
