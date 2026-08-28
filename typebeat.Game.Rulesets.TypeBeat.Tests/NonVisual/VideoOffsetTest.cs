// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Storyboards;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The setup tab's video offset syncs a background video to the song, and
    /// <see cref="StoryboardVideo.StartTime"/> is get-only, so every edit REPLACES the layer's video
    /// element. What is pinned here is that the replacements behave: the video layer keeps exactly one
    /// video at its head (<see cref="Storyboard.PrimaryVideo"/> is the first one), a FILE swap carries
    /// the mapper's sync forward instead of resetting it, and an offset never conjures a video onto a
    /// map that has none.
    /// </summary>
    [TestFixture]
    public class VideoOffsetTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        [Test]
        public void SettingAnOffsetReplacesTheElementInPlace()
        {
            var storyboard = new Storyboard();
            ResourcesSection.ApplyVideoChange(storyboard, "song.mp4");

            ResourcesSection.ApplyVideoOffsetChange(storyboard, -1500);

            Assert.Multiple(() =>
            {
                Assert.That(storyboard.PrimaryVideo!.Path, Is.EqualTo("song.mp4"));
                Assert.That(storyboard.PrimaryVideo!.StartTime, Is.EqualTo(-1500));
                Assert.That(videoElements(storyboard), Has.Count.EqualTo(1), "a leftover element would silently become the map's video");
            });

            ResourcesSection.ApplyVideoOffsetChange(storyboard, 1500);

            Assert.Multiple(() =>
            {
                Assert.That(storyboard.PrimaryVideo!.StartTime, Is.EqualTo(1500));
                Assert.That(videoElements(storyboard), Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void SwappingTheVideoFileKeepsTheOffset()
        {
            // Swapping is how a mapper replaces a clip with a re-encode of the same video; resetting
            // the sync they already dialled in would be a destructive edit nobody asked for.
            var storyboard = new Storyboard();
            ResourcesSection.ApplyVideoChange(storyboard, "song.mp4");
            ResourcesSection.ApplyVideoOffsetChange(storyboard, -1500);

            ResourcesSection.ApplyVideoChange(storyboard, "reencode.mp4");

            Assert.Multiple(() =>
            {
                Assert.That(storyboard.PrimaryVideo!.Path, Is.EqualTo("reencode.mp4"));
                Assert.That(storyboard.PrimaryVideo!.StartTime, Is.EqualTo(-1500));
                Assert.That(videoElements(storyboard), Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void ClearingTheVideoLeavesNothingToOffset()
        {
            var storyboard = new Storyboard();
            ResourcesSection.ApplyVideoChange(storyboard, "song.mp4");
            ResourcesSection.ApplyVideoOffsetChange(storyboard, 250);

            ResourcesSection.ApplyVideoChange(storyboard, null);
            Assert.That(storyboard.PrimaryVideo, Is.Null);

            // An offset on a map with no video must not synthesise one.
            ResourcesSection.ApplyVideoOffsetChange(storyboard, 250);

            Assert.Multiple(() =>
            {
                Assert.That(storyboard.PrimaryVideo, Is.Null);
                Assert.That(videoElements(storyboard), Is.Empty);
            });

            // And a video added back afterwards starts unoffset rather than inheriting a ghost value.
            ResourcesSection.ApplyVideoChange(storyboard, "other.mp4");
            Assert.That(storyboard.PrimaryVideo!.StartTime, Is.EqualTo(0));
        }

        [Test]
        public void OffsetSetInSetupReachesTheSavedFile()
        {
            // End to end over the save path the editor actually uses: setup mutates the storyboard,
            // the ruleset's native encoder writes it, the decoder reads it back.
            var storyboard = new Storyboard();
            ResourcesSection.ApplyVideoChange(storyboard, "song.mp4");
            ResourcesSection.ApplyVideoOffsetChange(storyboard, -1500);
            ResourcesSection.ApplyVideoChange(storyboard, "reencode.mp4");

            var reloaded = decodeStoryboard(encode(storyboard));

            Assert.Multiple(() =>
            {
                Assert.That(reloaded.PrimaryVideo!.Path, Is.EqualTo("reencode.mp4"));
                Assert.That(reloaded.PrimaryVideo!.StartTime, Is.EqualTo(-1500));
            });
        }

        private static List<StoryboardVideo> videoElements(Storyboard storyboard)
            => storyboard.GetLayer("Video").Elements.OfType<StoryboardVideo>().ToList();

        private static string encode(Storyboard storyboard)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Artist";
            beatmap.Metadata.Title = "Title";
            beatmap.Metadata.AudioFile = "song.mp4";
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Granularity = TimingGranularity.Line,
                Line = new LyricLine
                {
                    RawText = "hello world",
                    StartTime = 1000,
                    EndTime = 3000,
                    SingEndTime = 2800,
                    Units = new[] { new TimedUnit { Text = "hello world", StartTime = 1000, EndTime = 2800, Source = TimingSource.Explicit } },
                },
            });

            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(beatmap, storyboard, sw);

            return sb.ToString();
        }

        private static Storyboard decodeStoryboard(string text)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var reader = new typebeat.Game.IO.LineBufferedReader(stream);
            return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Storyboard>(reader).Decode(reader);
        }
    }
}
