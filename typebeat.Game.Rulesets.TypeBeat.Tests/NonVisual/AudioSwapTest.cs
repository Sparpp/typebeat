// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit.Setup;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Swapping a map's audio file in the editor's setup tab must change the audio file and NOTHING
    /// else. The audio a map was imported with is routinely the wrong one (a rip with a lead-in, a
    /// live take, a mislabelled mix), and re-importing to fix it throws away every hand-authored
    /// lyric timing, which is the expensive part of a map.
    ///
    /// So the two things pinned here are that a swap leaves the encoded <c>[Lyrics]</c> payload
    /// BYTE-identical, and that it does not quietly rename the map from the replacement file's tags.
    /// </summary>
    [TestFixture]
    public class AudioSwapTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        [Test]
        public void SwappingAudioLeavesTheLyricPayloadByteIdentical()
        {
            var beatmap = buildBeatmap();
            string before = encode(beatmap);

            swap(beatmap, "audio(1).wav");

            string after = encode(beatmap);

            Assert.Multiple(() =>
            {
                Assert.That(lyricsSection(after), Is.EqualTo(lyricsSection(before)), "the [Lyrics] payload must not move by a single byte");
                Assert.That(generalLine(before, "AudioFilename"), Is.EqualTo("song.mp3"));
                Assert.That(generalLine(after, "AudioFilename"), Is.EqualTo("audio(1).wav"), "the audio filename is the one thing that should change");
            });
        }

        [Test]
        public void SwappedMapStillDecodesToTheSameLines()
        {
            var beatmap = buildBeatmap();
            var expected = decode(encode(beatmap)).HitObjects.OfType<TypeBeatHitObject>().ToList();

            swap(beatmap, "audio(1).wav");

            var reloaded = decode(encode(beatmap));
            var actual = reloaded.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(reloaded.Metadata.AudioFile, Is.EqualTo("audio(1).wav"));
            Assert.That(actual, Has.Count.EqualTo(expected.Count));

            for (int i = 0; i < expected.Count; i++)
                assertLinesEqual(expected[i].Line, actual[i].Line, i);
        }

        [Test]
        public void SwappingAudioDoesNotRenameTheMap()
        {
            var beatmap = buildBeatmap();

            // Tags that a rip of the same song plausibly carries, and which must not win over what
            // the mapper authored.
            swap(beatmap, "audio(1).wav", tagArtist: "Various Artists", tagTitle: "Track 07");

            Assert.Multiple(() =>
            {
                Assert.That(beatmap.Metadata.Artist, Is.EqualTo("Friday Pilots Club"));
                Assert.That(beatmap.Metadata.ArtistUnicode, Is.EqualTo("Friday Pilots Club"));
                Assert.That(beatmap.Metadata.Title, Is.EqualTo("Spectator"));
                Assert.That(beatmap.Metadata.TitleUnicode, Is.EqualTo("Spectator"));
                Assert.That(beatmap.Metadata.AudioFile, Is.EqualTo("audio(1).wav"));
            });
        }

        [Test]
        public void FirstAudioOnAMapWithNoTrackStillSeedsMetadataFromTags()
        {
            // The blank map an audio-only import (or a brand new beatmap) produces: the file's tags
            // are the only thing that knows what the song is, so they are still allowed to fill it in.
            var metadata = new BeatmapMetadata();

            ResourcesSection.ApplyAudioTrackChange(metadata, "audio.mp3", "Friday Pilots Club", "Spectator");

            Assert.Multiple(() =>
            {
                Assert.That(metadata.AudioFile, Is.EqualTo("audio.mp3"));
                Assert.That(metadata.Artist, Is.EqualTo("Friday Pilots Club"));
                Assert.That(metadata.ArtistUnicode, Is.EqualTo("Friday Pilots Club"));
                Assert.That(metadata.Title, Is.EqualTo("Spectator"));
                Assert.That(metadata.TitleUnicode, Is.EqualTo("Spectator"));
            });
        }

        [Test]
        public void UntaggedReplacementFileDoesNotBlankTheMapsMetadata()
        {
            var beatmap = buildBeatmap();

            // A .wav exported from a DAW typically carries no tags at all.
            swap(beatmap, "audio(1).wav", tagArtist: null, tagTitle: null);

            Assert.Multiple(() =>
            {
                Assert.That(beatmap.Metadata.Artist, Is.EqualTo("Friday Pilots Club"));
                Assert.That(beatmap.Metadata.Title, Is.EqualTo("Spectator"));
                Assert.That(beatmap.Metadata.AudioFile, Is.EqualTo("audio(1).wav"));
            });
        }

        /// <summary>Runs the exact metadata write the setup tab's audio chooser performs.</summary>
        private static void swap(Beatmap beatmap, string newAudioFilename, string? tagArtist = "Various Artists", string? tagTitle = "Track 07")
            => ResourcesSection.ApplyAudioTrackChange(beatmap.BeatmapInfo.Metadata, newAudioFilename, tagArtist, tagTitle);

        /// <summary>
        /// A map carrying every optional field the [Lyrics] payload can hold (word units, syllable
        /// subdivisions, sub-1 confidence, a seal grace, an estimated line), so "byte-identical"
        /// is a claim about all of them and not just start times.
        /// </summary>
        private static Beatmap buildBeatmap()
        {
            var lines = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = "watching from the back row",
                    StartTime = 12_345.5,
                    EndTime = 16_000,
                    SingEndTime = 15_820.25,
                    SealGraceMs = 640,
                    Units = new[]
                    {
                        new TimedUnit { Text = "watching", StartTime = 12_345.5, EndTime = 13_100, Source = TimingSource.Explicit, Confidence = 0.42, SyllableBoundaries = new[] { 12_700.0 } },
                        new TimedUnit { Text = "from", StartTime = 13_100, EndTime = 13_640, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "the", StartTime = 13_640, EndTime = 14_010, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "back", StartTime = 14_010, EndTime = 14_900, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "row", StartTime = 14_900, EndTime = 15_820.25, Source = TimingSource.Explicit, Confidence = 0.9 },
                    },
                },
                new LyricLine
                {
                    RawText = "never on the stage",
                    StartTime = 16_000,
                    EndTime = 19_500,
                    SingEndTime = 19_012.75,
                    Estimated = true,
                    Units = new[]
                    {
                        new TimedUnit { Text = "never", StartTime = 16_000, EndTime = 16_880, Source = TimingSource.Explicit, SyllableBoundaries = new[] { 16_440.0 } },
                        new TimedUnit { Text = "on", StartTime = 16_880, EndTime = 17_300, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "the", StartTime = 17_300, EndTime = 17_910, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "stage", StartTime = 17_910, EndTime = 19_012.75, Source = TimingSource.Explicit },
                    },
                },
            };

            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Friday Pilots Club";
            beatmap.Metadata.ArtistUnicode = "Friday Pilots Club";
            beatmap.Metadata.Title = "Spectator";
            beatmap.Metadata.TitleUnicode = "Spectator";
            beatmap.Metadata.AudioFile = "song.mp3";

            for (int i = 0; i < lines.Count; i++)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
                    Granularity = TimingGranularity.Syllable,
                });
            }

            return beatmap;
        }

        private static string encode(Beatmap source)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(source, null, sw);

            return sb.ToString();
        }

        private static Beatmap decode(string text)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var reader = new typebeat.Game.IO.LineBufferedReader(stream);
            return (Beatmap)typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }

        /// <summary>Everything from the [Lyrics] header to the end of the file: the timing payload.</summary>
        private static string lyricsSection(string osu)
        {
            int index = osu.IndexOf("[Lyrics]", StringComparison.Ordinal);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), "encoded map has no [Lyrics] section");
            return osu.Substring(index);
        }

        private static string generalLine(string osu, string key)
            => osu.Split('\n').Select(l => l.TrimEnd('\r')).First(l => l.StartsWith(key + ":", StringComparison.Ordinal)).Substring(key.Length + 1).Trim();

        private static void assertLinesEqual(LyricLine expected, LyricLine actual, int index)
        {
            Assert.That(actual.RawText, Is.EqualTo(expected.RawText), $"line {index} text");
            Assert.That(actual.StartTime, Is.EqualTo(expected.StartTime), $"line {index} StartTime");
            Assert.That(actual.EndTime, Is.EqualTo(expected.EndTime), $"line {index} EndTime");
            Assert.That(actual.SingEndTime, Is.EqualTo(expected.SingEndTime), $"line {index} SingEndTime");
            Assert.That(actual.SealGraceMs, Is.EqualTo(expected.SealGraceMs), $"line {index} SealGraceMs");
            Assert.That(actual.Estimated, Is.EqualTo(expected.Estimated), $"line {index} Estimated");
            Assert.That(actual.Units, Has.Count.EqualTo(expected.Units.Count), $"line {index} unit count");

            for (int u = 0; u < expected.Units.Count; u++)
            {
                Assert.That(actual.Units[u].Text, Is.EqualTo(expected.Units[u].Text), $"line {index} unit {u} text");
                Assert.That(actual.Units[u].StartTime, Is.EqualTo(expected.Units[u].StartTime).Within(1e-9), $"line {index} unit {u} start");
                Assert.That(actual.Units[u].EndTime, Is.EqualTo(expected.Units[u].EndTime).Within(1e-9), $"line {index} unit {u} end");
                Assert.That(actual.Units[u].SyllableBoundaries, Is.EqualTo(expected.Units[u].SyllableBoundaries).Within(1e-9), $"line {index} unit {u} syllables");
            }
        }
    }
}
