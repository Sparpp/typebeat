// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Audio.Track;
using System.Reflection;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Audio;
using typebeat.Game.Screens.Edit.Compose.Components.Timeline;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The timeline's waveform resamples once per SETTLED size rather than on every frame of a zoom tween,
    /// and never asks for more points than the decoded song has (backlog 372: a zoom was queueing a
    /// full-song resample per frame, and the deepest zoom was upsampling to several times the source).
    /// </summary>
    public partial class TestSceneTimelineWaveformGraph : OsuTestScene
    {
        private CountingGraph graph = null!;

        private void createGraph(float width)
        {
            AddStep("create graph", () => Child = graph = new CountingGraph
            {
                Width = width,
                Height = 100,
                Waveform = new Waveform(new MemoryStream(tone(seconds: 2))),
            });
            AddUntilStep("first resample landed", () => graph.Regenerations.Count > 0);
            AddWaitStep("settle", 5);
        }

        [Test]
        public void TestAZoomTweenResamplesOnceAtTheSettledWidth()
        {
            int startedBefore = 0;

            createGraph(400);
            AddStep("tween the width like a zoom", () =>
            {
                startedBefore = graph.ResamplesStarted;
                graph.ResizeWidthTo(1600, 200, Easing.OutQuint);
            });
            AddUntilStep("tween finished", () => !graph.Transforms.Any());
            AddWaitStep("settle", 10);
            AddAssert("one resample started for the whole tween", () => graph.ResamplesStarted - startedBefore, () => Is.EqualTo(1));
            AddAssert("landed at the settled width", () => graph.Regenerations.Last(), () => Is.EqualTo(1600));
        }

        [Test]
        public void TestPointCountIsCappedAtTheDecodedSong()
        {
            int sourcePoints = 0;

            createGraph(400);
            AddStep("read the source's point count", () => sourcePoints = graph.Waveform!.GetPoints().Length);
            AddStep("widen far past the source", () => graph.Width = sourcePoints * 5);
            AddUntilStep("resampled at the source's count", () => graph.Regenerations.Last(), () => Is.EqualTo(sourcePoints));
        }

        [Test]
        public void TestAZoomThatStaysPastTheCapDoesNotResample()
        {
            int sourcePoints = 0;
            int startedBefore = 0;

            createGraph(400);
            AddStep("read the source's point count", () => sourcePoints = graph.Waveform!.GetPoints().Length);
            AddStep("widen past the source", () => graph.Width = sourcePoints * 2);
            AddUntilStep("resampled at the source's count", () => graph.Regenerations.Last(), () => Is.EqualTo(sourcePoints));
            AddWaitStep("settle", 10);
            AddStep("widen further, still past the source", () =>
            {
                startedBefore = graph.ResamplesStarted;
                graph.Width = sourcePoints * 5;
            });
            AddWaitStep("settle", 10);
            AddAssert("no resample started", () => graph.ResamplesStarted - startedBefore, () => Is.Zero);
            // A whole-pixel width: the graph resamples to the width rounded UP, and the source's point
            // count can be odd (it is since framework 2026.918.0, which rewrote waveform generation).
            AddStep("narrow back under the source", () => graph.Width = sourcePoints / 2);
            AddUntilStep("resampled at the new width", () => graph.Regenerations.Last(), () => Is.EqualTo(sourcePoints / 2));
        }

        /// <summary>
        /// Records the point count of every resample that lands, and counts every resample that STARTS: the
        /// framework gives each one a fresh cancellation source, and one that is superseded still costs a
        /// full resample on the thread pool, so landing alone would undercount the work.
        /// </summary>
        private partial class CountingGraph : TimelineWaveformGraph
        {
            private static readonly FieldInfo cancel_source = typeof(WaveformGraph).GetField("cancelSource", BindingFlags.NonPublic | BindingFlags.Instance)
                                                              ?? throw new InvalidOperationException("the framework's waveform graph no longer has the field this test counts resamples by");

            public readonly List<int> Regenerations = new List<int>();

            public int ResamplesStarted { get; private set; }

            private object? lastCancelSource;

            protected override void Update()
            {
                base.Update();

                object? current = cancel_source.GetValue(this);

                if (lastCancelSource != null && !ReferenceEquals(current, lastCancelSource))
                    ResamplesStarted++;

                lastCancelSource = current;
            }

            protected override void OnWaveformRegenerated(Waveform waveform)
            {
                base.OnWaveformRegenerated(waveform);
                Regenerations.Add(waveform.GetPoints().Length);
            }
        }

        /// <summary>A mono 440 Hz tone as an in-memory 16 bit wav.</summary>
        private static byte[] tone(int seconds)
        {
            const int rate = 44100;
            int frames = rate * seconds;
            var pcm = new byte[frames * 2];

            for (int i = 0; i < frames; i++)
            {
                short value = (short)Math.Round(Math.Sin(2 * Math.PI * 440 * i / rate) * 0.5 * short.MaxValue);
                pcm[i * 2] = (byte)(value & 0xff);
                pcm[i * 2 + 1] = (byte)((value >> 8) & 0xff);
            }

            var wav = new MemoryStream();
            var writer = new BinaryWriter(wav);

            writer.Write("RIFF".ToCharArray());
            writer.Write(36 + pcm.Length);
            writer.Write("WAVE".ToCharArray());
            writer.Write("fmt ".ToCharArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data".ToCharArray());
            writer.Write(pcm.Length);
            writer.Write(pcm);
            writer.Flush();

            return wav.ToArray();
        }
    }
}
