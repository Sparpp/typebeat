// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 424: a backwards seek while watching a replay is walked by the frame-stable clock one
// 16.7 ms sub-step at a time, and the engine ticker used to rebuild the engine from time 0 on every
// one of them, so a long rewind cost sub-steps times prefix length. ReplayFeedCursor defers the
// rebuild until the walk stops and does it once. These pins hold the deferred feed to the ticker it
// replaced, sub-step for sub-step, and to a fresh replay watched straight to the same time, across the
// era bits a replay's headers can carry.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class ReplayFeedCursorTest
    {
        private const double frame_ms = ReplayEngineFeed.FRAME_MS;

        private const double line_ms = 4000;

        private static readonly string[] line_text = { "ab cd ef", "gh ij kl", "mn op qr", "st uv wx", "yz ab cd", "ef gh ij" };

        private static double mapEnd => line_text.Length * line_ms + 3000;

        #region Fixture

        /// <summary>Six three-word lines, each word sung for a second, with a seal grace after each line.</summary>
        private static LyricBeatmap beatmap()
        {
            var lines = new List<LyricLine>();

            for (int k = 0; k < line_text.Length; k++)
            {
                double start = k * line_ms;
                string[] words = line_text[k].Split(' ');

                lines.Add(new LyricLine
                {
                    RawText = line_text[k],
                    StartTime = start,
                    EndTime = start + line_ms,
                    SingEndTime = start + 3000,
                    Units = words.Select((w, i) => new TimedUnit { Text = w, StartTime = start + i * 1000, EndTime = start + (i + 1) * 1000 }).ToArray(),
                });
            }

            return new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "a", Title = "feed", FolderPath = string.Empty, AudioFileName = "a.mp3", HasWordTiming = true },
                Lines = lines,
                Granularity = TimingGranularity.Word,
            };
        }

        private static IReadOnlyList<double> targets(LyricBeatmap map, int lineIndex)
            => TypingLine.FromLyricLine(map.Lines[lineIndex]).Cells.Select(c => c.TargetTime).ToList();

        /// <summary>
        /// The headers a replay can open with: none at all (autoplay's shape, the factory's flags
        /// stand), every era clear (the oldest stored replay), every era set, and each engine era bit
        /// set ALONE. A bit set alone is the sharpest probe: it is the arm that bit selects with nothing
        /// else to mask it.
        /// </summary>
        private static IEnumerable<TestCaseData> headerCases()
        {
            yield return new TestCaseData((Func<List<ReplayFrame>>)(() => new List<ReplayFrame>())).SetArgDisplayNames("NoHeaders");
            yield return new TestCaseData((Func<List<ReplayFrame>>)(() => headers(_ => false))).SetArgDisplayNames("AllClear");
            yield return new TestCaseData((Func<List<ReplayFrame>>)(() => headers(_ => true))).SetArgDisplayNames("AllSet");

            foreach (var bit in ReplayEraBits.ALL.Where(b => b.IsEngineFlag))
            {
                string name = bit.Name;
                yield return new TestCaseData((Func<List<ReplayFrame>>)(() => headers(b => b.Name == name))).SetArgDisplayNames(name);
            }
        }

        private static List<ReplayFrame> headers(Func<ReplayEraBit, bool> set)
        {
            var config = TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: true);
            var extended = TypeBeatReplayFrame.CreateExtendedConfigFrame(0);

            foreach (var bit in ReplayEraBits.ALL.Where(b => b.IsEngineFlag))
                bit.Set(bit.Word == EraWord.Config ? config : extended, set(bit));

            // The two watcher-side settings ride on the CONFIG frame as well; keep the run's input model
            // fixed so every case types the same keystrokes into the same cells.
            config.AllowWrongInput = true;
            config.SpaceSkipsWord = true;

            return new List<ReplayFrame> { config, extended };
        }

        /// <summary>
        /// A deliberately messy run so a rebuild has things to get wrong: clean words, a typo typed
        /// through and fixed, a word skipped and reclaimed, a typo left standing, a cell nobody types,
        /// a retype of a cell already correct, and an Enter. Times are integral and strictly
        /// increasing, as the recorder stamps them.
        /// </summary>
        private static Replay run(LyricBeatmap map, List<ReplayFrame> header)
        {
            var frames = new List<ReplayFrame>(header);
            double last = 0;

            void press(double time, char c)
            {
                double t = Math.Max(Math.Round(time), last + 1);
                frames.Add(new TypeBeatReplayFrame(t, c));
                last = t;
            }

            void typeLine(int k, int from, int to, double lateBy = 0)
            {
                var times = targets(map, k);
                for (int i = from; i < to; i++)
                    press(times[i] + lateBy, line_text[k][i]);
            }

            // line 0: clean, a little late on purpose so the deltas are not all zero.
            typeLine(0, 0, line_text[0].Length, 40);

            // line 1: typo on the second cell typed through, erased and fixed, then the rest.
            var t1 = targets(map, 1);
            press(t1[0], 'g');
            press(t1[1], 'x');
            press(t1[1] + 60, TypeBeatReplayFrame.BACKSPACE);
            press(t1[1] + 120, 'h');
            typeLine(1, 2, line_text[1].Length);

            // line 2: a space inside the first word abandons it, then a backspace goes back for it.
            var t2 = targets(map, 2);
            press(t2[0], 'm');
            press(t2[0] + 80, ' ');
            press(t2[0] + 160, TypeBeatReplayFrame.BACKSPACE);
            press(t2[0] + 220, 'n');
            press(t2[0] + 280, ' ');
            typeLine(2, 3, line_text[2].Length);

            // line 3: a typo left standing, and the last cell never typed (a miss at the seal).
            var t3 = targets(map, 3);
            press(t3[0], 's');
            press(t3[1], 'q');
            typeLine(3, 2, line_text[3].Length - 1);

            // line 4: clean, then a backspace and a retype of the final, already correct, cell, and an Enter.
            typeLine(4, 0, line_text[4].Length, -30);
            press(last + 50, TypeBeatReplayFrame.BACKSPACE);
            press(last + 50, line_text[4][^1]);
            press(last + 50, TypeBeatReplayFrame.ENTER);

            // line 5: clean and early.
            typeLine(5, 0, line_text[5].Length, -60);

            var replay = new Replay();
            replay.Frames.AddRange(frames);
            return replay;
        }

        /// <summary>
        /// The ticks the frame-stable clock delivers walking from <paramref name="from"/> to
        /// <paramref name="to"/>: one frame interval per sub-step, a direct jump once within 1.2 of
        /// one (<c>FrameStabilityContainer.applyFrameStability</c>), and every replay frame crossed
        /// visited TWICE, once when the replay handler clamps the clock onto it and once more as it
        /// steps past (<c>FramedReplayInputHandler.SetFrameFromTime</c>). Each tick carries whether it
        /// is at the destination, i.e. the container's <c>CatchUpPending</c> clear.
        /// </summary>
        private static IEnumerable<(double Time, bool Settled)> walk(IReadOnlyList<ReplayFrame> frames, double from, double to)
        {
            double current = from;
            int direction = Math.Sign(to - from);

            while (current != to)
            {
                double proposed = Math.Abs(to - current) > frame_ms * 1.2 ? current + direction * frame_ms : to;

                // A frame strictly between the two times stops the clock on itself, once and again.
                var crossed = frames.Select(f => f.Time).Where(t => direction < 0 ? t < current && t > proposed : t > current && t < proposed).Distinct();

                foreach (double t in direction < 0 ? crossed.OrderByDescending(t => t) : crossed.OrderBy(t => t))
                {
                    yield return (t, false);
                    yield return (t, false);
                }

                current = proposed;
                yield return (current, current == to);
            }
        }

        /// <summary>
        /// The engine ticker as it stood before backlog 424, written out by hand: a backwards tick
        /// rebuilds on the spot, then the due frames are fed and the engine ticked.
        /// </summary>
        private sealed class PerSubStepTicker
        {
            private int next;
            private double lastFed = double.NegativeInfinity;

            public void Tick(TypingEngine engine, IReadOnlyList<ReplayFrame> frames, double now)
            {
                if (now < lastFed)
                    next = ReplayEngineFeed.RebuildTo(engine, frames, now);

                lastFed = now;

                while (next < frames.Count && frames[next].Time <= now)
                {
                    if (frames[next] is TypeBeatReplayFrame frame)
                        ReplayEngineFeed.Apply(engine, frame);

                    next++;
                }

                engine.Update(now);
            }
        }

        private static void tick(ReplayFeedCursor cursor, TypingEngine engine, IReadOnlyList<ReplayFrame> frames, double now, bool settled)
        {
            if (cursor.Advance(engine, frames, now, 1, settled))
                engine.Update(now);
        }

        /// <summary>Prime the headers as the ticker does on attach, so the ticks before the first frame run under them.</summary>
        private static void prime(TypingEngine engine, IReadOnlyList<ReplayFrame> frames)
        {
            ReplayEngineFeed.ClearExtendedEras(engine);

            foreach (var frame in frames.OfType<TypeBeatReplayFrame>().Where(f => f.IsConfig || f.IsConfigExtended))
                ReplayEngineFeed.Apply(engine, frame);
        }

        private static string snapshot(TypingEngine engine) => ReplayRewindTest.EngineSnapshot(engine);

        #endregion

        /// <summary>
        /// THE PROPERTY, across the header matrix. Watch past the target, seek back, let the clock
        /// settle, then play on to the end. At the seek target and at the end, the deferred feed must
        /// hold exactly the state the per-sub-step ticker held, and the state of a fresh replay
        /// watched straight to that time; and it must have rebuilt once where the old ticker rebuilt
        /// on every backwards sub-step.
        /// </summary>
        [TestCaseSource(nameof(headerCases))]
        public void ADeferredRewindLandsOnTheSameStateAsRebuildingEverySubStep(Func<List<ReplayFrame>> header)
        {
            var map = beatmap();
            var frames = run(map, header()).Frames;

            foreach (double seekTarget in new[] { 0, 1500, 5100, 9010, 13333.3, 18500 })
            {
                var deferred = new TypingEngine(map);
                var perStep = new TypingEngine(map);
                prime(deferred, frames);
                prime(perStep, frames);

                var cursor = new ReplayFeedCursor();
                var old = new PerSubStepTicker();

                int deferredRebuilds = 0, perStepRebuilds = 0;
                deferred.Rewound += () => deferredRebuilds++;
                perStep.Rewound += () => perStepRebuilds++;

                void both(double time, bool settled)
                {
                    tick(cursor, deferred, frames, time, settled);
                    old.Tick(perStep, frames, time);
                }

                // Watched to a few seconds past the target (crossing a seal, keystrokes and line
                // starts), which is all the walk back needs: the old ticker's cost is quadratic in it.
                double seekFrom = Math.Min(seekTarget + 3500, mapEnd);

                foreach (var (time, settled) in walk(frames, 0, seekFrom))
                    both(time, settled);

                Assert.That(snapshot(deferred), Is.EqualTo(snapshot(perStep)), "the two tickers agree before any seek");

                // The walk back, then two paused ticks holding on the target.
                foreach (var (time, settled) in walk(frames, seekFrom, seekTarget))
                    both(time, settled);

                both(seekTarget, true);
                both(seekTarget, true);

                var fresh = new TypingEngine(map);
                prime(fresh, frames);
                ReplayEngineFeed.RebuildTo(fresh, frames, seekTarget);

                Assert.That(snapshot(deferred), Is.EqualTo(snapshot(perStep)), $"at the seek target {seekTarget}: deferred vs per-sub-step");
                Assert.That(snapshot(deferred), Is.EqualTo(snapshot(fresh)), $"at the seek target {seekTarget}: deferred vs a fresh replay to the same time");
                Assert.That(cursor.RebuildPending, Is.False);
                Assert.That(deferredRebuilds, Is.EqualTo(1), "one rebuild for the seek");
                Assert.That(perStepRebuilds, Is.GreaterThan(1), "the old ticker rebuilt on every backwards sub-step (fixture must walk)");

                // Play on to the end: the forward feed after the rebuild is the old one too.
                foreach (var (time, settled) in walk(frames, seekTarget, mapEnd))
                    both(time, settled);

                Assert.That(snapshot(deferred), Is.EqualTo(snapshot(perStep)), $"at the end after seeking to {seekTarget}");
                Assert.That(deferredRebuilds, Is.EqualTo(1));
            }
        }

        /// <summary>
        /// A walk the player redirects part-way (seeks back, then forwards again before the clock
        /// arrives) never reports itself settled at its low point. The first forward tick settles it
        /// there, which is where the per-sub-step ticker's last rebuild landed.
        /// </summary>
        [Test]
        public void AWalkRedirectedForwardsSettlesAtItsLowPoint()
        {
            var map = beatmap();
            var frames = run(map, headers(_ => true)).Frames;

            var deferred = new TypingEngine(map);
            var perStep = new TypingEngine(map);
            prime(deferred, frames);
            prime(perStep, frames);

            var cursor = new ReplayFeedCursor();
            var old = new PerSubStepTicker();
            int deferredRebuilds = 0;
            deferred.Rewound += () => deferredRebuilds++;

            void both(double time, bool settled)
            {
                tick(cursor, deferred, frames, time, settled);
                old.Tick(perStep, frames, time);
            }

            foreach (var (time, _) in walk(frames, 0, mapEnd))
                both(time, false);

            // Heading for 2000, but redirected to 12000 on reaching 8000: no tick of the walk is settled.
            foreach (var (time, _) in walk(frames, mapEnd, 8000))
                both(time, false);

            Assert.That(cursor.RebuildPending, Is.True, "still walking: nothing rebuilt yet");
            Assert.That(deferredRebuilds, Is.Zero);

            foreach (var (time, settled) in walk(frames, 8000, 12000))
                both(time, settled);

            Assert.That(snapshot(deferred), Is.EqualTo(snapshot(perStep)));
            Assert.That(deferredRebuilds, Is.EqualTo(1));
        }

        /// <summary>
        /// Without a frame-stable clock walking the seek (the editor's composer plays without frame
        /// stability, so its seek is one jump) every tick is settled, and a backwards tick rebuilds on
        /// the spot exactly as the old ticker did: no frame of latency is added.
        /// </summary>
        [Test]
        public void ASettledBackwardsTickRebuildsOnTheSpot()
        {
            var map = beatmap();
            var frames = run(map, headers(_ => true)).Frames;

            var engine = new TypingEngine(map);
            prime(engine, frames);
            var cursor = new ReplayFeedCursor();

            for (double t = 0; t <= mapEnd; t += frame_ms)
                tick(cursor, engine, frames, t, true);

            var fresh = new TypingEngine(map);
            prime(fresh, frames);
            int consumed = ReplayEngineFeed.RebuildTo(fresh, frames, 6000);

            Assert.That(cursor.Advance(engine, frames, 6000, 1), Is.True, "the engine is current on the backwards tick itself");
            Assert.That(snapshot(engine), Is.EqualTo(snapshot(fresh)));
            Assert.That(cursor.NextFrameIndex, Is.EqualTo(consumed));
        }

        /// <summary>
        /// The measurement behind backlog 424, kept as a pin on the real three-minute map: a full
        /// backwards walk costs ONE rebuild. The old ticker's count is the number of backwards
        /// sub-steps (about ten thousand here), each rebuilding a prefix of up to three minutes.
        /// </summary>
        [Test]
        public void AFullLengthRewindOfARealMapRebuildsOnce()
        {
            string path = StandaloneMaps.Require("Siames - The Wolf", "timing.json");
            Assert.That(TimingJsonLoader.TryLoad(path, out var lines), Is.True);

            var beatmap = OriginalTextFormatTest.BuildBeatmap(lines);
            var frames = new TypeBeatAutoGenerator(beatmap, syllableTiming: true, charTimedStretch: true, firstCharTiming: true,
                authoredSyllablesOnly: true, alignSubdivisionTargets: true).Generate().Frames;

            var map = new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "Siames", Title = "The Wolf", FolderPath = string.Empty, AudioFileName = "a.mp3", HasWordTiming = true },
                Lines = lines.ToList(),
                Granularity = TimingGranularity.Word,
            };

            var engine = new TypingEngine(map);
            prime(engine, frames);
            var cursor = new ReplayFeedCursor();
            int rebuilds = 0;
            engine.Rewound += () => rebuilds++;

            double end = map.LastLineEnd;

            foreach (var (time, settled) in walk(frames, 0, end))
                tick(cursor, engine, frames, time, settled);

            int backwardsTicks = 0;

            foreach (var (time, settled) in walk(frames, end, 10000))
            {
                tick(cursor, engine, frames, time, settled);
                backwardsTicks++;
            }

            var fresh = new TypingEngine(map);
            prime(fresh, frames);
            ReplayEngineFeed.RebuildTo(fresh, frames, 10000);

            Assert.That(backwardsTicks, Is.GreaterThan(10000), "the walk is the whole map");
            Assert.That(rebuilds, Is.EqualTo(1));
            Assert.That(snapshot(engine), Is.EqualTo(snapshot(fresh)));
        }
    }
}
