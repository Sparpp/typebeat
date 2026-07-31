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

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The mapper-declared song language (task 58). Two things are pinned here:
    ///
    ///  * THE WIRE SHAPE. The language reaches the website on exactly one channel, the
    ///    <c>[Metadata] Language:</c> line inside the uploaded package, so the canonical spelling
    ///    the encoder writes and the decoder reads is a cross-repo contract with the server's
    ///    <c>Typebeat.Web/Packages/BeatmapLanguages.cs</c> (which is what
    ///    <c>tests/Typebeat.WireCompat</c> checks from the other side).
    ///  * THE SILENCE. An unspecified map must encode BYTE-IDENTICALLY to how it encoded before
    ///    this field existed. <see cref="TypeBeatRuleset.NativeEncodingsEquivalentForStatus"/>
    ///    compares encodings to decide whether a map is still the one that was ranked, so an
    ///    unconditionally emitted line would locally-modify every installed beatmap on first save.
    /// </summary>
    [TestFixture]
    public class BeatmapLanguageTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        // ---- canonical names ----

        [Test]
        public void UnspecifiedIsZero_SoRealmsDefaultMeansNotChosen()
        {
            // Load-bearing: BeatmapMetadata persists the enum through an int, and realm fills a
            // newly added column with 0 on every pre-existing row.
            Assert.That((int)BeatmapLanguage.Unspecified, Is.Zero);
        }

        [Test]
        public void UnspecifiedHasNoCanonicalName()
        {
            Assert.That(BeatmapLanguage.Unspecified.ToCanonicalName(), Is.Empty);
        }

        [Test]
        public void EveryRealLanguageRoundTripsThroughItsCanonicalName()
        {
            foreach (var language in Enum.GetValues<BeatmapLanguage>().Where(l => l != BeatmapLanguage.Unspecified))
            {
                string name = language.ToCanonicalName();

                Assert.Multiple(() =>
                {
                    Assert.That(name, Is.EqualTo(name.ToLowerInvariant()), $"{language} must be lowercase on the wire");
                    Assert.That(BeatmapLanguageExtensions.FromCanonicalName(name), Is.EqualTo(language));
                });
            }
        }

        [TestCase("Japanese", BeatmapLanguage.Japanese)]
        [TestCase("JAPANESE", BeatmapLanguage.Japanese)]
        [TestCase("  japanese  ", BeatmapLanguage.Japanese)]
        [TestCase("instrumental", BeatmapLanguage.Instrumental)]
        public void FromCanonicalName_IsForgiving(string input, BeatmapLanguage expected)
        {
            Assert.That(BeatmapLanguageExtensions.FromCanonicalName(input), Is.EqualTo(expected));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        [TestCase("klingon")]
        [TestCase("42")]
        [TestCase("Unspecified")]
        public void FromCanonicalName_IsTotal_UnknownBecomesUnspecified(string? input)
        {
            // A file written by a newer client naming a language this build has never heard of
            // must decode, not throw. ("Unspecified" itself is included: the encoder never writes
            // it, and reading it back as "not chosen" is the only sane interpretation.)
            Assert.That(BeatmapLanguageExtensions.FromCanonicalName(input), Is.EqualTo(BeatmapLanguage.Unspecified));
        }

        [Test]
        public void NumericEnumTextIsNotAcceptedAsALanguage()
        {
            // Enum.TryParse would happily turn "2" into a member; the wire contract is names only.
            Assert.That(BeatmapLanguageExtensions.FromCanonicalName("2"), Is.EqualTo(BeatmapLanguage.Unspecified));
        }

        // ---- metadata plumbing ----

        [Test]
        public void MetadataDefaultsToUnspecified()
        {
            Assert.That(new BeatmapMetadata().Language, Is.EqualTo(BeatmapLanguage.Unspecified));
        }

        [Test]
        public void MetadataStoresThroughItsIntBackingField()
        {
            var metadata = new BeatmapMetadata { Language = BeatmapLanguage.Korean };

            Assert.Multiple(() =>
            {
                Assert.That(metadata.LanguageInt, Is.EqualTo((int)BeatmapLanguage.Korean));
                Assert.That(metadata.Language, Is.EqualTo(BeatmapLanguage.Korean));
            });
        }

        [Test]
        public void DeepCloneCarriesTheLanguage()
        {
            // The editor clones metadata on entry; a field missing from DeepClone silently reverts
            // on every edit session.
            var metadata = new BeatmapMetadata { Language = BeatmapLanguage.Swedish };

            Assert.That(metadata.DeepClone().Language, Is.EqualTo(BeatmapLanguage.Swedish));
        }

        // ---- encode / decode ----

        [Test]
        public void EncoderWritesTheLanguageLine_AndTheDecoderReadsItBack()
        {
            var source = buildBeatmap(BeatmapLanguage.Japanese);
            string encoded = encode(source);

            Assert.Multiple(() =>
            {
                Assert.That(encoded, Does.Contain("\nLanguage:japanese"));
                Assert.That(decode(encoded).Metadata.Language, Is.EqualTo(BeatmapLanguage.Japanese));
            });
        }

        [Test]
        public void UnspecifiedWritesNoLineAtAll()
        {
            // The map-hash stability guarantee: adding this field must not change the encoding of
            // any map that has not opted into it.
            string withLanguage = encode(buildBeatmap(BeatmapLanguage.English));
            string without = encode(buildBeatmap(BeatmapLanguage.Unspecified));

            Assert.Multiple(() =>
            {
                Assert.That(without, Does.Not.Contain("Language:"));
                Assert.That(withLanguage.Replace("Language:english\n", string.Empty).Replace("Language:english\r\n", string.Empty),
                    Is.EqualTo(without),
                    "the Language line must be the ONLY difference an opted-in map introduces");
            });
        }

        [Test]
        public void EveryLanguageSurvivesAFullRoundTrip()
        {
            foreach (var language in Enum.GetValues<BeatmapLanguage>())
                Assert.That(decode(encode(buildBeatmap(language))).Metadata.Language, Is.EqualTo(language), language.ToString());
        }

        [Test]
        public void DecodingAnUnknownLanguageValueDoesNotThrow()
        {
            string encoded = encode(buildBeatmap(BeatmapLanguage.Unspecified))
                .Replace("Tags:", "Language:klingon\nTags:");

            Assert.That(decode(encoded).Metadata.Language, Is.EqualTo(BeatmapLanguage.Unspecified));
        }

        [Test]
        public void LegacyEncoderMatchesTheNativeSpelling()
        {
            // Both encoders exist; only the native one runs today, but a divergence in the value
            // spelling would silently produce packages the server folds to "unset". No hit objects:
            // the legacy encoder cannot serialise TypeBeatHitObjects (they carry no position), and
            // this is a [Metadata] assertion.
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Language = BeatmapLanguage.Russian;

            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                new typebeat.Game.Beatmaps.Formats.LegacyBeatmapEncoder(beatmap, null, null).Encode(sw);

            Assert.That(sb.ToString(), Does.Contain("Language: russian"));
        }

        // ---- helpers ----

        private static Beatmap buildBeatmap(BeatmapLanguage language)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Synth Rider";
            beatmap.Metadata.Title = "Neon Nights";
            beatmap.Metadata.AudioFile = "audio.mp3";
            beatmap.Metadata.Language = language;

            var lines = new List<LyricLine>
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

            for (int i = 0; i < lines.Count; i++)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
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

        private static Beatmap decode(string text)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var reader = new typebeat.Game.IO.LineBufferedReader(stream);
            return (Beatmap)typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }
    }
}
