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
using typebeat.Game.Storyboards;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the editor SAVE path: an editor beatmap of <see cref="TypeBeatHitObject"/>s, encoded
    /// by <see cref="TypeBeatBeatmapEncoder"/> and decoded back by <see cref="LyricBeatmapDecoder"/>,
    /// must reproduce the same lyric lines. This is the round-trip the editor's save + undo/redo
    /// both rely on.
    /// </summary>
    [TestFixture]
    public class TypeBeatBeatmapEncoderTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        [Test]
        public void RealSpectatorEditorRoundTrip()
        {
            string timingPath = StandaloneMaps.Require("Friday Pilots Club - Spectator", "timing.json");
            Assert.That(TimingJsonLoader.TryLoad(timingPath, out var lines), Is.True);

            var source = buildBeatmap(lines, "Friday Pilots Club", "Spectator", "audio.mp3");

            var reloaded = roundTrip(source);
            var expected = source.HitObjects.OfType<TypeBeatHitObject>().ToList();
            var actual = reloaded.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(actual.Count, Is.EqualTo(expected.Count));
            Assert.That(reloaded.BeatmapInfo.Ruleset.ShortName, Is.EqualTo("typebeat"));
            Assert.That(reloaded.Metadata.Artist, Is.EqualTo("Friday Pilots Club"));
            Assert.That(reloaded.Metadata.Title, Is.EqualTo("Spectator"));

            for (int i = 0; i < expected.Count; i++)
                assertLinesEqual(expected[i].Line, actual[i], i);
        }

        [Test]
        public void EditedTimesSurviveRoundTrip()
        {
            // Two simple lines; shift and re-word as an editor would, then round-trip.
            var lines = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = "hello world",
                    StartTime = 1000,
                    EndTime = 3000,
                    SingEndTime = 2800,
                    // Overlapping-vocals grace: derived from raw overruns at import time, but the
                    // editor model only holds clamped unit times — must persist explicitly.
                    SealGraceMs = 600,
                    Units = new[]
                    {
                        new TimedUnit { Text = "hello", StartTime = 1000, EndTime = 1900, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "world", StartTime = 1900, EndTime = 2800, Source = TimingSource.Explicit },
                    },
                },
                new LyricLine
                {
                    RawText = "yeaaaaaaaah",
                    StartTime = 3000,
                    EndTime = 6000,
                    SingEndTime = 5500,
                    Units = new[] { new TimedUnit { Text = "yeaaaaaaaah", StartTime = 3000, EndTime = 5500, Source = TimingSource.Explicit } },
                },
            };

            var source = buildBeatmap(lines, "Artist", "Title", "song.mp3");
            var actual = roundTrip(source).HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(actual.Count, Is.EqualTo(2));
            Assert.That(actual[1].Line.RawText, Is.EqualTo("yeaaaaaaaah"));
            assertLinesEqual(lines[0], actual[0], 0);
            assertLinesEqual(lines[1], actual[1], 1);
        }

        [Test]
        public void IntroBeatdropSurvivesRoundTrip()
        {
            var source = buildBeatmap(singleLine(), "Artist", "Title", "song.mp3");
            source.IntroBeatdropTime = 45210;

            var reloaded = roundTrip(source);

            Assert.That(reloaded.IntroBeatdropTime, Is.EqualTo(45210));

            // A second pass must be byte-stable (the editor's undo stack diffs encoded states).
            var sb1 = new StringBuilder();
            using (var sw = new StringWriter(sb1))
                TypeBeatBeatmapEncoder.Encode(source, sw);

            var sb2 = new StringBuilder();
            using (var sw = new StringWriter(sb2))
                TypeBeatBeatmapEncoder.Encode(reloaded, sw);

            Assert.That(sb2.ToString(), Is.EqualTo(sb1.ToString()));
        }

        [Test]
        public void UnsetIntroBeatdropStaysUnset()
        {
            var source = buildBeatmap(singleLine(), "Artist", "Title", "song.mp3");

            Assert.That(roundTrip(source).IntroBeatdropTime, Is.Null);
        }

        [Test]
        public void BackgroundAndVideoSurviveRoundTrip()
        {
            var source = buildBeatmap(singleLine(), "Artist", "Title", "song.mp4");
            source.Metadata.BackgroundFile = "bg.jpg";

            var storyboard = new Storyboard();
            storyboard.GetLayer("Video").Elements.Add(new StoryboardVideo(StoryboardElementSource.Beatmap, "song.mp4", 0));

            string encoded = encode(source, storyboard);

            // Background round-trips through the inherited legacy [Events] beatmap parsing...
            var reloaded = decode(encoded);
            Assert.That(reloaded.Metadata.BackgroundFile, Is.EqualTo("bg.jpg"));

            // ...and the video through the registered legacy storyboard decoder.
            var reloadedStoryboard = decodeStoryboard(encoded);
            Assert.That(reloadedStoryboard.PrimaryVideo, Is.Not.Null);
            Assert.That(reloadedStoryboard.PrimaryVideo!.Path, Is.EqualTo("song.mp4"));

            // Lyric lines are unaffected by the [Events] section.
            Assert.That(reloaded.HitObjects, Has.Count.EqualTo(1));
        }

        [Test]
        public void NoEventsSectionWhenUnset()
        {
            var source = buildBeatmap(singleLine(), "Artist", "Title", "song.mp3");

            Assert.That(encode(source, new Storyboard()), Does.Not.Contain("[Events]"));
        }

        [Test]
        public void DifficultyNameSurvivesRoundTrip()
        {
            // A set can hold several difficulties only if each encodes its own Version (the osu
            // identity key). A named difficulty must survive the encode/decode round-trip.
            var named = buildBeatmap(singleLine(), "Artist", "Title", "song.mp3");
            named.BeatmapInfo.DifficultyName = "Hard";

            string encoded = encode(named, null);
            Assert.That(encoded, Does.Contain("Version:Hard"));
            Assert.That(decode(encoded).BeatmapInfo.DifficultyName, Is.EqualTo("Hard"));

            // A blank difficulty name falls back to the format's default marker (never "Version:").
            var blank = buildBeatmap(singleLine(), "Artist", "Title", "song.mp3");
            blank.BeatmapInfo.DifficultyName = "";
            Assert.That(encode(blank, null), Does.Contain("Version:type!beat"));
        }

        [Test]
        public void MetadataTagsSurviveRoundTrip()
        {
            // The editor's Tags field must reach the submitted .osu (was hardcoded, so tags the
            // author set in-game never reached the website's beatmapsets.tags on upload).
            var source = buildBeatmap(singleLine(), "Artist", "Title", "song.mp3");
            source.Metadata.Tags = "cover acoustic slow";

            string encoded = encode(source, null);
            Assert.That(encoded, Does.Contain("Tags:cover acoustic slow"));
            Assert.That(decode(encoded).Metadata.Tags, Is.EqualTo("cover acoustic slow"));

            // Unset tags produce NO default tags — an empty Tags line, decoding to empty.
            var untagged = buildBeatmap(singleLine(), "Artist", "Title", "song.mp3");
            string untaggedOsu = encode(untagged, null);
            Assert.That(untaggedOsu, Does.Not.Contain("typebeat lyrics typing"));
            Assert.That(decode(untaggedOsu).Metadata.Tags, Is.Empty);
        }

        [Test]
        public void OriginalTitleAndArtistSurviveRoundTrip()
        {
            // The editor's romanised (Title/Artist) and original (TitleUnicode/ArtistUnicode)
            // fields are DISTINCT text boxes — the encoder was duplicating the romanised value
            // into both, so an author's entered original text never reached the website.
            var source = buildBeatmap(singleLine(), "Ultra Soul", "Neon Nights", "song.mp3");
            source.Metadata.ArtistUnicode = "ウルトラソウル";
            source.Metadata.TitleUnicode = "ネオンナイツ";

            string encoded = encode(source, null);

            Assert.Multiple(() =>
            {
                Assert.That(encoded, Does.Contain("Title:Neon Nights"));
                Assert.That(encoded, Does.Contain("TitleUnicode:ネオンナイツ"));
                Assert.That(encoded, Does.Contain("Artist:Ultra Soul"));
                Assert.That(encoded, Does.Contain("ArtistUnicode:ウルトラソウル"));
            });

            var reloaded = decode(encoded);

            Assert.Multiple(() =>
            {
                Assert.That(reloaded.Metadata.Title, Is.EqualTo("Neon Nights"));
                Assert.That(reloaded.Metadata.TitleUnicode, Is.EqualTo("ネオンナイツ"));
                Assert.That(reloaded.Metadata.Artist, Is.EqualTo("Ultra Soul"));
                Assert.That(reloaded.Metadata.ArtistUnicode, Is.EqualTo("ウルトラソウル"));
            });

            // No separate original set — TitleUnicode/ArtistUnicode fall back to the romanised
            // value rather than writing (and round-tripping) a blank line.
            var noOriginal = buildBeatmap(singleLine(), "Artist", "Title", "song.mp3");
            string noOriginalOsu = encode(noOriginal, null);

            Assert.Multiple(() =>
            {
                Assert.That(noOriginalOsu, Does.Contain("TitleUnicode:Title"));
                Assert.That(noOriginalOsu, Does.Contain("ArtistUnicode:Artist"));
            });
        }

        private static List<LyricLine> singleLine() => new List<LyricLine>
        {
            new LyricLine
            {
                RawText = "hello world",
                StartTime = 1000,
                EndTime = 3000,
                SingEndTime = 2800,
                Units = new[]
                {
                    new TimedUnit { Text = "hello", StartTime = 1000, EndTime = 1900, Source = TimingSource.Explicit },
                    new TimedUnit { Text = "world", StartTime = 1900, EndTime = 2800, Source = TimingSource.Explicit },
                },
            },
        };

        private static Beatmap buildBeatmap(IReadOnlyList<LyricLine> lines, string artist, string title, string audio)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = artist;
            beatmap.Metadata.Title = title;
            beatmap.Metadata.AudioFile = audio;

            bool anyWords = lines.Any(l => l.Units.Count > 1);

            for (int i = 0; i < lines.Count; i++)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
                    Granularity = anyWords ? TimingGranularity.Word : TimingGranularity.Line,
                });
            }

            return beatmap;
        }

        private static Beatmap roundTrip(Beatmap source) => decode(encode(source, null));

        private static string encode(Beatmap source, Storyboard? storyboard)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(source, storyboard, sw);

            return sb.ToString();
        }

        private static Beatmap decode(string text)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var reader = new typebeat.Game.IO.LineBufferedReader(stream);
            return (Beatmap)typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }

        private static Storyboard decodeStoryboard(string text)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var reader = new typebeat.Game.IO.LineBufferedReader(stream);
            return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Storyboard>(reader).Decode(reader);
        }

        private static void assertLinesEqual(LyricLine expected, TypeBeatHitObject actual, int index)
        {
            var line = actual.Line;

            Assert.That(line.RawText, Is.EqualTo(expected.RawText), $"line {index} text");
            Assert.That(line.StartTime, Is.EqualTo(expected.StartTime), $"line {index} StartTime");
            Assert.That(line.EndTime, Is.EqualTo(expected.EndTime), $"line {index} EndTime");
            Assert.That(line.SingEndTime, Is.EqualTo(expected.SingEndTime), $"line {index} SingEndTime");
            Assert.That(line.SealGraceMs, Is.EqualTo(expected.SealGraceMs), $"line {index} SealGraceMs");
            Assert.That(line.Estimated, Is.EqualTo(expected.Estimated), $"line {index} Estimated");
            Assert.That(line.Units.Count, Is.EqualTo(expected.Units.Count), $"line {index} unit count");

            for (int u = 0; u < expected.Units.Count; u++)
            {
                Assert.That(line.Units[u].Text, Is.EqualTo(expected.Units[u].Text), $"line {index} unit {u} text");
                Assert.That(line.Units[u].StartTime, Is.EqualTo(expected.Units[u].StartTime).Within(1e-9), $"line {index} unit {u} start");
                Assert.That(line.Units[u].EndTime, Is.EqualTo(expected.Units[u].EndTime).Within(1e-9), $"line {index} unit {u} end");
            }
        }
    }
}
