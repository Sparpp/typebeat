// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The type!beat .osu keys are spelled once, in <see cref="TypeBeatOsuKeys"/>. Both client writers
    /// (the native <see cref="LyricOsuFormat"/> behind imports and editor saves, and the
    /// <see cref="LegacyBeatmapEncoder"/> behind the .osz export) emit the same bytes for the same
    /// metadata, and the decoder still reads the older spelling the legacy encoder used to write
    /// (a space after the colon) exactly as it reads the shared one.
    /// </summary>
    [TestFixture]
    public class TypeBeatOsuKeysTest
    {
        private static readonly string[] keys =
        {
            TypeBeatOsuKeys.LYRIC_FONT,
            TypeBeatOsuKeys.LYRIC_FONT_FILE,
            TypeBeatOsuKeys.LANGUAGE,
            TypeBeatOsuKeys.AUDIO_GAIN,
        };

        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        [Test]
        public void BothEncodersWriteTheSameLines()
        {
            var native = typeBeatLines(encodeNative(buildBeatmap(withHitObjects: true)));
            var legacy = typeBeatLines(encodeLegacy(buildBeatmap(withHitObjects: false)));

            Assert.Multiple(() =>
            {
                Assert.That(legacy, Is.EqualTo(native), "the two writers must emit the same bytes for the same metadata");
                Assert.That(native, Is.EqualTo(new[]
                {
                    "LyricFont: Noto Sans JP",
                    "LyricFontFile: fonts/noto.ttf",
                    "Language:japanese",
                    "AudioGain:2.5",
                }), "the shared spelling is the native writer's, which the stored maps already carry");
            });
        }

        [Test]
        public void NeitherEncoderWritesAnUnsetKey()
        {
            Assert.Multiple(() =>
            {
                Assert.That(typeBeatLines(encodeNative(buildBeatmap(withHitObjects: true, set: false))), Is.Empty);
                Assert.That(typeBeatLines(encodeLegacy(buildBeatmap(withHitObjects: false, set: false))), Is.Empty);
            });
        }

        [Test]
        public void TheDecoderReadsBothSpellingsIdentically()
        {
            string shared = encodeNative(buildBeatmap(withHitObjects: true));
            string spaced = shared.Replace("Language:japanese", "Language: japanese")
                                  .Replace("AudioGain:2.5", "AudioGain: 2.5")
                                  .Replace("LyricFont: Noto Sans JP", "LyricFont:Noto Sans JP")
                                  .Replace("LyricFontFile: fonts/noto.ttf", "LyricFontFile:fonts/noto.ttf");

            Assert.That(spaced, Is.Not.EqualTo(shared), "the variant must actually differ for the test to mean anything");

            var a = decode(shared).Metadata;
            var b = decode(spaced).Metadata;

            Assert.Multiple(() =>
            {
                Assert.That(b.Language, Is.EqualTo(BeatmapLanguage.Japanese));
                Assert.That(b.AudioGain, Is.EqualTo(2.5));
                Assert.That(b.LyricFont, Is.EqualTo("Noto Sans JP"));
                Assert.That(b.LyricFontFile, Is.EqualTo("fonts/noto.ttf"));

                Assert.That(b.Language, Is.EqualTo(a.Language));
                Assert.That(b.AudioGain, Is.EqualTo(a.AudioGain));
                Assert.That(b.LyricFont, Is.EqualTo(a.LyricFont));
                Assert.That(b.LyricFontFile, Is.EqualTo(a.LyricFontFile));
            });
        }

        [Test]
        public void ALegacyExportDecodesToTheSameMetadata()
        {
            var exported = decode(encodeLegacy(buildBeatmap(withHitObjects: false))).Metadata;

            Assert.Multiple(() =>
            {
                Assert.That(exported.Language, Is.EqualTo(BeatmapLanguage.Japanese));
                Assert.That(exported.AudioGain, Is.EqualTo(2.5));
                Assert.That(exported.LyricFont, Is.EqualTo("Noto Sans JP"));
                Assert.That(exported.LyricFontFile, Is.EqualTo("fonts/noto.ttf"));
            });
        }

        // ---- helpers ----

        /// <summary>The type!beat key lines of an encoding, in file order.</summary>
        private static List<string> typeBeatLines(string encoded)
            => encoded.Split('\n')
                      .Select(l => l.TrimEnd('\r'))
                      .Where(l => l.Contains(':') && keys.Contains(l[..l.IndexOf(':')].Trim()))
                      .ToList();

        private static Beatmap buildBeatmap(bool withHitObjects, bool set = true)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Synth Rider";
            beatmap.Metadata.Title = "Neon Nights";
            beatmap.Metadata.AudioFile = "audio.mp3";

            if (set)
            {
                beatmap.Metadata.Language = BeatmapLanguage.Japanese;
                beatmap.Metadata.AudioGain = 2.5;
                beatmap.Metadata.LyricFont = "Noto Sans JP";
                beatmap.Metadata.LyricFontFile = "fonts/noto.ttf";
            }

            // The legacy encoder cannot serialise TypeBeatHitObjects (they carry no position), and these
            // are header assertions, so only the native encoding carries a line.
            if (withHitObjects)
            {
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

        private static string encodeNative(Beatmap source)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(source, null, sw);

            return sb.ToString();
        }

        private static string encodeLegacy(Beatmap source)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                new LegacyBeatmapEncoder(source, null, null).Encode(sw);

            return sb.ToString();
        }

        private static Beatmap decode(string text)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var reader = new typebeat.Game.IO.LineBufferedReader(stream);
            return (Beatmap)typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }
    }
}
