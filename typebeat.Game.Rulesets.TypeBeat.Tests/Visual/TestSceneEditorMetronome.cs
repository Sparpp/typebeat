// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Timing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Beatmaps.Timing;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Timing;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneEditorMetronome : OsuTestScene
    {
        [Resolved]
        private AudioManager audio { get; set; } = null!;

        private readonly EditorTimingSettings settings = new EditorTimingSettings();
        private readonly TestBeatSource source = new TestBeatSource();
        private readonly List<int> beats = new List<int>();

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
            dependencies.CacheAs(settings);
            dependencies.CacheAs<IBeatSyncProvider>(source);
            return dependencies;
        }

        [Test]
        public void ClassicSamplesAreAvailableToAudio()
        {
            AddAssert("classic beat sample loads", () => audio.Samples.Get("Editor/metronomelow") != null);
            AddAssert("classic downbeat sample loads", () => audio.Samples.Get("Editor/metronomehigh") != null);
        }

        [Test]
        public void FractionalTimingSectionsMeterAndSeek()
        {
            const double offset = 123.456;
            const double nextOffset = 2000.125;
            const double nextLength = 60000 / 137.123456;
            AddStep("set up metronome", () =>
            {
                Clear();
                beats.Clear();
                settings.Metronome.Value = true;
                source.ControlPoints.Clear();
                source.ControlPoints.Add(offset, new TimingControlPoint { BeatLength = 500 });
                source.ControlPoints.Add(nextOffset, new TimingControlPoint { BeatLength = nextLength, TimeSignature = TimeSignature.SimpleTriple });
                source.ManualClock.CurrentTime = 0;
                source.ManualClock.IsRunning = true;
                var metronome = new EditorMetronome();
                metronome.Ticked += beats.Add;
                Add(metronome);
            });
            AddStep("fractional downbeat", () => source.ManualClock.CurrentTime = offset);
            AddUntilStep("downbeat tick", () => beats.Count == 1);
            AddAssert("first beat accented", () => beats[0] == 0);
            AddStep("next quarter beat", () => source.ManualClock.CurrentTime = offset + 500);
            AddUntilStep("quarter beat tick", () => beats.Count == 2);
            AddAssert("second beat unaccented", () => beats[1] == 1);
            AddStep("seek between beats", () => source.ManualClock.CurrentTime = 1000);
            AddWaitStep("process seek", 2);
            AddAssert("no late click on seek", () => beats.Count == 2);
            AddStep("new timing section", () => source.ManualClock.CurrentTime = nextOffset);
            AddUntilStep("new downbeat tick", () => beats.Count == 3);
            AddAssert("section resets accent", () => beats[2] == 0);
            // Simulate a frame just after the beat, avoiding floating-point rounding at its exact boundary.
            AddStep("decimal BPM beat", () => source.ManualClock.CurrentTime = nextOffset + nextLength + 0.001);
            AddUntilStep("decimal BPM tick", () => beats.Count == 4);
            AddAssert("second beat in triple meter", () => beats[3] == 1);
            AddStep("next bar in triple meter", () => source.ManualClock.CurrentTime = nextOffset + nextLength * 3 + 0.001);
            AddUntilStep("triple downbeat tick", () => beats.Count == 5);
            AddAssert("third beat wraps to downbeat", () => beats[4] == 0);
            AddStep("pause", () => source.ManualClock.IsRunning = false);
            AddWaitStep("paused frames", 2);
            AddAssert("pause silent", () => beats.Count == 5);
            AddStep("disable and resume", () =>
            {
                settings.Metronome.Value = false;
                source.ManualClock.IsRunning = true;
                source.ManualClock.CurrentTime = nextOffset + nextLength * 4;
            });
            AddWaitStep("disabled frames", 2);
            AddAssert("disabled silent", () => beats.Count == 5);
            AddStep("enable with no timing points", () =>
            {
                source.ControlPoints.Clear();
                settings.Metronome.Value = true;
                source.ManualClock.CurrentTime = 0;
            });
            AddWaitStep("empty map frames", 2);
            AddAssert("no implicit BPM on empty map", () => beats.Count == 5);
        }

        private class TestBeatSource : IBeatSyncProvider
        {
            public readonly ManualClock ManualClock = new ManualClock();
            public IClock Clock => ManualClock;
            public ControlPointInfo ControlPoints { get; } = new ControlPointInfo();
            public ChannelAmplitudes CurrentAmplitudes => ChannelAmplitudes.Empty;
        }
    }
}
