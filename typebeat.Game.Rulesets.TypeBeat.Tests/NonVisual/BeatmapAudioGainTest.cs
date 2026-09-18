// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using typebeat.Game.Audio.Effects;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The map's own track gain: the bar under the editor's audio track picker, the
    /// <c>[Metadata] AudioGain:</c> line it writes, and the amplifier that line drives.
    ///
    /// <para>Two things are pinned here the way the language field's are. THE SILENCE: a map at the
    /// default gain must encode BYTE-IDENTICALLY to how it encoded before this field existed, because
    /// the ruleset compares encodings to decide whether a map is still the one that was ranked - an
    /// unconditional line would locally modify every installed beatmap on first save. And THE RANGE:
    /// the value a hand-edited file can ask for is clamped to what the amplifier will accept, so a
    /// broken file cannot turn a map into a wall of noise or a silent one.</para>
    ///
    /// <para>The conversion between the linear multiplier the format stores and the dB the effect takes
    /// is pinned too, since that is the whole of what a mapper's "150%" means.</para>
    /// </summary>
    [TestFixture]
    public class BeatmapAudioGainTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        // ---- default and plumbing ----

        [Test]
        public void MetadataDefaultsToNoGainChange()
        {
            Assert.That(new BeatmapMetadata().AudioGain, Is.EqualTo(1), "a map that never touched the bar plays its file as imported");
        }

        [Test]
        public void DeepCloneCarriesTheGain()
        {
            // The editor clones metadata on entry and the resource swaps clone it per difficulty; a
            // field missing from DeepClone silently reverts on every edit session.
            var metadata = new BeatmapMetadata { AudioGain = 1.75 };

            Assert.That(metadata.DeepClone().AudioGain, Is.EqualTo(1.75));
        }

        // ---- the wire shape ----

        [Test]
        public void EncoderWritesTheGainLineAndTheDecoderReadsItBack()
        {
            string encoded = encode(buildBeatmap(1.5));

            Assert.Multiple(() =>
            {
                Assert.That(encoded, Does.Contain("AudioGain:1.5"));
                Assert.That(decode(encoded).Metadata.AudioGain, Is.EqualTo(1.5));
            });
        }

        [Test]
        public void NoGainChangeWritesNoLineAtAll()
        {
            string withGain = encode(buildBeatmap(2));
            string without = encode(buildBeatmap(BeatmapMetadata.DEFAULT_AUDIO_GAIN));

            Assert.Multiple(() =>
            {
                Assert.That(without, Does.Not.Contain("AudioGain:"));
                Assert.That(withGain.Replace("AudioGain:2\n", string.Empty).Replace("AudioGain:2\r\n", string.Empty),
                    Is.EqualTo(without),
                    "the AudioGain line must be the ONLY difference an opted-in map introduces");
            });
        }

        [Test]
        public void TheFractionalGainSurvivesARoundTrip()
        {
            // The bar works in hundredths, so the file has to be finer than that: 1.25 must not come
            // back as 1.2 or 1, which would walk the mapper's setting down with every save.
            foreach (double gain in new[] { 0.25, 0.5, 1.1, 1.25, 1.33, 1.75, 3.99 })
                Assert.That(decode(encode(buildBeatmap(gain))).Metadata.AudioGain, Is.EqualTo(gain), gain.ToString());
        }

        [Test]
        public void AnUnparseableOrOutOfRangeValueFallsBackInsteadOfFailing()
        {
            string baseline = encode(buildBeatmap(BeatmapMetadata.DEFAULT_AUDIO_GAIN));

            Assert.Multiple(() =>
            {
                // Absent: every file written before the key existed.
                Assert.That(decode(baseline).Metadata.AudioGain, Is.EqualTo(BeatmapMetadata.DEFAULT_AUDIO_GAIN));

                // Nonsense is ignored rather than throwing the map away.
                Assert.That(decode(baseline.Replace("Tags:", "AudioGain: loud\nTags:")).Metadata.AudioGain,
                    Is.EqualTo(BeatmapMetadata.DEFAULT_AUDIO_GAIN));

                // And a hand-edited request beyond the amplifier's range is clamped to it.
                Assert.That(decode(baseline.Replace("Tags:", "AudioGain: 400\nTags:")).Metadata.AudioGain,
                    Is.EqualTo(BeatmapMetadata.MAX_AUDIO_GAIN));
                Assert.That(decode(baseline.Replace("Tags:", "AudioGain: -3\nTags:")).Metadata.AudioGain, Is.Zero);
            });
        }

        // ---- multiplier <-> decibels ----

        [Test]
        public void TheMultiplierAndTheDecibelsAgreeAtThePointsThatMatter()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AudioGain.multiplierToDb(1), Is.EqualTo(0).Within(1e-9), "no gain change is 0 dB, which is bypass");
                Assert.That(AudioGain.multiplierToDb(2), Is.EqualTo(6.0206).Within(1e-3), "twice the amplitude is +6 dB");
                Assert.That(AudioGain.multiplierToDb(0.5), Is.EqualTo(-6.0206).Within(1e-3));
                Assert.That(AudioGain.multiplierToDb(BeatmapMetadata.MAX_AUDIO_GAIN), Is.EqualTo(AudioGain.MAX_GAIN_DB).Within(1e-9),
                    "the format's ceiling is exactly the amplifier's");
                Assert.That(AudioGain.multiplierToDb(0), Is.EqualTo(AudioGain.MIN_GAIN_DB), "silence is the amplifier's floor, not a division by zero");
                Assert.That(AudioGain.multiplierToDb(double.NaN), Is.EqualTo(AudioGain.MIN_GAIN_DB));
                Assert.That(AudioGain.dbToMultiplier(AudioGain.multiplierToDb(1.5)), Is.EqualTo(1.5).Within(1e-9));
            });
        }

        [Test]
        public void ClippingIsFullScaleOnTheLoudestSample()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AudioGain.WouldClip(0.8, 1), Is.False, "a map at its own gain is not clipping: the file is what it is");
                Assert.That(AudioGain.WouldClip(0.8, 1.25), Is.False, "0.8 x 1.25 = 1.0 exactly, so the peaks just reach full scale");
                Assert.That(AudioGain.WouldClip(0.8, 1.26), Is.True, "and past that the surplus is cut off, which is what the red light means");
                Assert.That(AudioGain.WouldClip(0.5, 2), Is.False, "a quietly mastered song has room to be lifted");
                Assert.That(AudioGain.WouldClip(1, 1), Is.False, "an already-full-scale song clips only once it is amplified");
                Assert.That(AudioGain.WouldClip(0, BeatmapMetadata.MAX_AUDIO_GAIN), Is.False, "nothing to amplify is not clipping");
                Assert.That(AudioGain.PeakOf(null), Is.Zero, "a track that cannot be read measures nothing, and the indicator says so rather than reassuring");
            });
        }

        // ---- helpers ----

        private static Beatmap buildBeatmap(double audioGain)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Synth Rider";
            beatmap.Metadata.Title = "Neon Nights";
            beatmap.Metadata.AudioFile = "audio.mp3";
            beatmap.Metadata.AudioGain = audioGain;

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
