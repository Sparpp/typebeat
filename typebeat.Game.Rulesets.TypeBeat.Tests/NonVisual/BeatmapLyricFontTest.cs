// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The map's own lyric font (backlog 291): the <c>[General] LyricFont:</c> and
    /// <c>LyricFontFile:</c> lines, through both encoders and back through the decoder.
    ///
    /// <para>The same two things are pinned here that <see cref="BeatmapAudioGainTest"/> pins for
    /// the gain. THE SILENCE: a map with no font chosen must encode BYTE-IDENTICALLY to how it
    /// encoded before these keys existed, because the ruleset compares encodings to decide whether
    /// a map is still the one that was ranked. And THE PAIR OF ENCODERS: the native format
    /// (BeatmapManager.Save) and the legacy .osu (the .osz export) write the same map, so a map
    /// that leaves through one and returns through the other must come back with the font it left
    /// with, spelled identically.</para>
    /// </summary>
    [TestFixture]
    public class BeatmapLyricFontTest
    {
        private const string test_family = "Comic Neue";
        private const string test_file = "lyricfont.ttf";

        [SetUp]
        public void SetUp()
        {
            LyricBeatmapDecoder.Register();
            typebeat.Game.Beatmaps.Formats.LegacyBeatmapDecoder.Register();
        }

        // ---- default and plumbing ----

        [Test]
        public void MetadataDefaultsToNoFont()
        {
            Assert.Multiple(() =>
            {
                Assert.That(new BeatmapMetadata().LyricFont, Is.Empty, "a map that never chose a font declares none");
                Assert.That(new BeatmapMetadata().LyricFontFile, Is.Empty);
            });
        }

        [Test]
        public void DeepCloneCarriesBothFields()
        {
            // The editor clones metadata on entry; a field missing from DeepClone silently reverts
            // on every edit session.
            var metadata = new BeatmapMetadata { LyricFont = test_family, LyricFontFile = test_file };
            var clone = metadata.DeepClone();

            Assert.Multiple(() =>
            {
                Assert.That(clone.LyricFont, Is.EqualTo(test_family));
                Assert.That(clone.LyricFontFile, Is.EqualTo(test_file));
            });
        }

        // ---- the wire shape ----

        [Test]
        public void NativeEncoderRoundTripsBothKeys()
        {
            string encoded = encode(buildBeatmap(test_family, test_file));
            var decoded = decode(encoded).Metadata;

            Assert.Multiple(() =>
            {
                Assert.That(encoded, Does.Contain($"LyricFont: {test_family}"));
                Assert.That(encoded, Does.Contain($"LyricFontFile: {test_file}"));
                Assert.That(decoded.LyricFont, Is.EqualTo(test_family));
                Assert.That(decoded.LyricFontFile, Is.EqualTo(test_file));
            });
        }

        [Test]
        public void LegacyEncoderRoundTripsBothKeys()
        {
            string encoded = legacyEncode(buildBeatmap(test_family, test_file, withHitObjects: false));
            var decoded = decode(encoded).Metadata;

            Assert.Multiple(() =>
            {
                Assert.That(decoded.LyricFont, Is.EqualTo(test_family));
                Assert.That(decoded.LyricFontFile, Is.EqualTo(test_file));
            });
        }

        [Test]
        public void AFontWithoutABundledFileRoundTripsAlone()
        {
            var decoded = decode(encode(buildBeatmap(test_family, string.Empty))).Metadata;

            Assert.Multiple(() =>
            {
                Assert.That(decoded.LyricFont, Is.EqualTo(test_family));
                Assert.That(decoded.LyricFontFile, Is.Empty, "no LyricFontFile line may be invented");
            });
        }

        [Test]
        public void NoFontWritesNoLinesAtAll()
        {
            string withFont = encode(buildBeatmap(test_family, test_file));
            string without = encode(buildBeatmap(string.Empty, string.Empty));

            Assert.Multiple(() =>
            {
                Assert.That(without, Does.Not.Contain("LyricFont"));
                Assert.That(withFont.Replace($"LyricFont: {test_family}\n", string.Empty)
                                    .Replace($"LyricFont: {test_family}\r\n", string.Empty)
                                    .Replace($"LyricFontFile: {test_file}\n", string.Empty)
                                    .Replace($"LyricFontFile: {test_file}\r\n", string.Empty),
                    Is.EqualTo(without),
                    "the two font lines must be the ONLY difference an opted-in map introduces");
                Assert.That(legacyEncode(buildBeatmap(string.Empty, string.Empty, withHitObjects: false)), Does.Not.Contain("LyricFont"));
            });
        }

        [Test]
        public void BothEncodersSpellTheKeysTheSame()
        {
            // TWO ENCODERS, ONE PAIR OF KEYS: a map that leaves through the .osz exporter and comes
            // back through an import has to come back with the font it left with.
            string native = encode(buildBeatmap(test_family, test_file));
            string legacy = legacyEncode(buildBeatmap(test_family, test_file, withHitObjects: false));

            Assert.Multiple(() =>
            {
                Assert.That(valueIn(legacy, "LyricFont"), Is.EqualTo(valueIn(native, "LyricFont")).And.EqualTo(test_family));
                Assert.That(valueIn(legacy, "LyricFontFile"), Is.EqualTo(valueIn(native, "LyricFontFile")).And.EqualTo(test_file));
            });
        }

        [Test]
        public void AnAbsentKeyLeavesTheDefaultInPlace()
        {
            // Every file written before these keys existed.
            var decoded = decode(encode(buildBeatmap(string.Empty, string.Empty))).Metadata;

            Assert.Multiple(() =>
            {
                Assert.That(decoded.LyricFont, Is.Empty);
                Assert.That(decoded.LyricFontFile, Is.Empty);
            });
        }

        // ---- helpers (the same shape BeatmapAudioGainTest uses) ----

        private static Beatmap buildBeatmap(string lyricFont, string lyricFontFile, bool withHitObjects = true)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Synth Rider";
            beatmap.Metadata.Title = "Neon Nights";
            beatmap.Metadata.AudioFile = "audio.mp3";
            beatmap.Metadata.LyricFont = lyricFont;
            beatmap.Metadata.LyricFontFile = lyricFontFile;

            var line = new LyricLine
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
            };

            // The legacy encoder writes hit objects through the osu! ruleset's own shape, which a
            // lyric object has none of, so the comparisons that go through it use a map with no objects.
            if (withHitObjects)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = line.StartTime,
                    LineIndex = 0,
                    Line = line,
                    Granularity = TimingGranularity.Word,
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

        private static string legacyEncode(Beatmap source)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                new typebeat.Game.Beatmaps.Formats.LegacyBeatmapEncoder(source, null, null).Encode(sw);

            return sb.ToString();
        }

        /// <summary>The value an encoding wrote on the given key's line, whatever spacing it used.</summary>
        private static string valueIn(string encoded, string key)
        {
            foreach (string line in encoded.Split('\n'))
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith(key + ":", StringComparison.Ordinal) || trimmed.StartsWith(key + " :", StringComparison.Ordinal))
                    return trimmed[(trimmed.IndexOf(':') + 1)..].Trim();
            }

            return string.Empty;
        }

        private static Beatmap decode(string text)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var reader = new typebeat.Game.IO.LineBufferedReader(stream);
            return (Beatmap)typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }
    }
}
