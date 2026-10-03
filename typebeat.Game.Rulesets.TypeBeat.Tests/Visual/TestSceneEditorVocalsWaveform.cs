// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Models;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Compose.Components.Timeline;
using typebeat.Game.Storyboards;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The editor's Vocals Waveform toolbar toggle (backlog 392): it must SWAP the compose timeline's
    /// waveform between the full mix and the map's isolated vocals stem, and it must be a VIEW-ONLY
    /// change (the track and the editor clock keep running the full mix). A map with no stem has the
    /// toggle disabled, and one with no stem at all cannot turn the vocals view on.
    /// </summary>
    public abstract partial class TestSceneEditorVocalsWaveform : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        /// <summary>Whether the map built for this scene carries a vocals stem file.</summary>
        protected abstract bool HasStem { get; }

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Granularity = TimingGranularity.Word,
                Line = new LyricLine
                {
                    RawText = "hello world",
                    StartTime = 1000,
                    EndTime = 3000,
                    SingEndTime = 3000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "hello", StartTime = 1000, EndTime = 1900 },
                        new TimedUnit { Text = "world", StartTime = 2000, EndTime = 3000 },
                    },
                },
            });
            return beatmap;
        }

        protected override WorkingBeatmap CreateWorkingBeatmap(IBeatmap beatmap, Storyboard? storyboard = null)
        {
            var set = new BeatmapSetInfo();
            beatmap.BeatmapInfo.BeatmapSet = set;

            if (HasStem)
                set.Files.Add(new RealmNamedFileUsage(new RealmFile { Hash = "vocalsstem" }, VocalsStem.WAV_FILENAME));

            return new StemWorkingBeatmap(beatmap, storyboard, Clock, Audio);
        }

        protected TimelineWaveformGraph graph() => Editor.ChildrenOfType<TimelineWaveformGraph>().Single();

        protected RoundedButton toggle() =>
            Editor.ChildrenOfType<RoundedButton>().First(b => b.Text.ToString().StartsWith("Vocals Waveform", StringComparison.Ordinal));

        protected void click(string text)
        {
            var button = Editor.ChildrenOfType<RoundedButton>().First(b => b.Text.ToString() == text && b.IsPresent);
            InputManager.MoveMouseTo(button);
            InputManager.Click(MouseButton.Left);
        }

        /// <summary>A working beatmap whose stem stream is a real tone, so the stem waveform decodes.</summary>
        private class StemWorkingBeatmap : ClockBackedTestWorkingBeatmap
        {
            public StemWorkingBeatmap(IBeatmap beatmap, Storyboard? storyboard, osu.Framework.Timing.IFrameBasedClock referenceClock, AudioManager audio)
                : base(beatmap, storyboard, referenceClock, audio)
            {
            }

            protected override Stream? GetVocalsStream(string vocalsStemFilename) => new MemoryStream(toneWav());
        }

        /// <summary>A short 16 kHz mono WAV tone, stand-in for the aligner's Demucs stem.</summary>
        private static byte[] toneWav()
        {
            const int rate = 16000;
            int frames = rate / 4;
            var pcm = new byte[frames * 2];

            for (int i = 0; i < frames; i++)
            {
                short value = (short)Math.Round(Math.Sin(2 * Math.PI * 440 * i / rate) * 0.2 * short.MaxValue);
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

    /// <summary>A map that carries a vocals stem: the toggle is live and swaps the waveform.</summary>
    public partial class TestSceneEditorVocalsWaveformWithStem : TestSceneEditorVocalsWaveform
    {
        protected override bool HasStem => true;

        [Test]
        public void ToggleSwapsTheWaveformToTheVocalsStem()
        {
            AddUntilStep("compose timeline loaded", () => Editor.ChildrenOfType<TimelineWaveformGraph>().Any(g => g.IsLoaded));

            Waveform? full = null;
            Waveform? stem = null;

            AddStep("read the two sources", () =>
            {
                full = Beatmap.Value.Waveform;
                stem = Beatmap.Value.VocalsStemWaveform;
            });
            AddAssert("the stem decoded", () => stem != null && !ReferenceEquals(stem, full));
            AddAssert("graph starts on the full mix", () => ReferenceEquals(graph().Waveform, full));

            AddStep("enable vocals waveform", () => click("Vocals Waveform: Off"));
            AddUntilStep("graph swapped to the stem", () => ReferenceEquals(graph().Waveform, stem));

            AddStep("disable vocals waveform", () => click("Vocals Waveform: On"));
            AddUntilStep("graph back on the full mix", () => ReferenceEquals(graph().Waveform, full));
        }

        [Test]
        public void TogglingIsViewOnlyAndLeavesTrackAndClockAlone()
        {
            AddUntilStep("compose timeline loaded", () => Editor.ChildrenOfType<TimelineWaveformGraph>().Any(g => g.IsLoaded));

            WorkingBeatmap? working = null;
            Track? track = null;
            bool running = false;
            double time = 0;

            AddStep("capture track and clock state", () =>
            {
                working = Beatmap.Value;
                track = Beatmap.Value.Track;
                running = EditorClock.IsRunning;
                time = EditorClock.CurrentTime;
            });

            AddStep("enable vocals waveform", () => click("Vocals Waveform: Off"));
            AddUntilStep("toggle is on", () => toggle().Text.ToString() == "Vocals Waveform: On");

            AddAssert("same working beatmap", () => ReferenceEquals(Beatmap.Value, working));
            AddAssert("same track", () => ReferenceEquals(Beatmap.Value.Track, track));
            AddAssert("clock not started", () => EditorClock.IsRunning == running);
            AddAssert("clock time moved", () => EditorClock.CurrentTime == time);
        }
    }

    /// <summary>A map with no vocals stem: the toggle is disabled and cannot turn the vocals view on.</summary>
    public partial class TestSceneEditorVocalsWaveformWithoutStem : TestSceneEditorVocalsWaveform
    {
        protected override bool HasStem => false;

        [Test]
        public void ToggleIsDisabledAndTheVocalsViewCannotBeTurnedOn()
        {
            AddUntilStep("compose timeline loaded", () => Editor.ChildrenOfType<TimelineWaveformGraph>().Any(g => g.IsLoaded));
            AddUntilStep("toggle loaded", () => toggle().IsLoaded);

            AddAssert("no stem waveform", () => Beatmap.Value.VocalsStemWaveform == null);
            AddAssert("toggle disabled", () => !toggle().Enabled.Value);

            // The disabled button is inert: a click on it leaves the graph on the full mix.
            AddStep("click the disabled toggle", () => click("Vocals Waveform: Off"));
            AddAssert("graph still on the full mix", () => ReferenceEquals(graph().Waveform, Beatmap.Value.Waveform));
            AddAssert("toggle still reads off", () => toggle().Text.ToString() == "Vocals Waveform: Off");
        }
    }
}
