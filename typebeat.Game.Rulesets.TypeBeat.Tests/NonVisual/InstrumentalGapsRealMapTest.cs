// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Real-map pins for the mid-song instrumental skip. Runs the REAL gameplay data path on the
// user's actual installed maps: stored .osu text -> LyricBeatmapDecoder -> TypeBeatBeatmapConverter
// -> TypingEngine (built exactly as DrawableTypeBeatRuleset.createEngine does) -> InstrumentalGaps.
// The map files are the player's own installs (copied out of the game data files store), so they
// are NOT in this repo; the directory is supplied via the TYPEBEAT_GAP_OSU_DIR environment
// variable and the pins Assert.Ignore when it is unset.
//
// These pins exist because of a two-attempt regression: synthetic test lines were built with a
// timeline hole between one line's EndTime and the next line's StartTime, a shape the real
// decoder NEVER produces (TimingJsonLoader.BuildLines sets a non-last line's EndTime to the next
// line's StartMs, so real windows are contiguous). Every gap computation anchored on
// "EndTime + grace" therefore worked in tests and returned zero sections on every real map.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class InstrumentalGapsRealMapTest
    {
        private static string requireOsu(string fileName)
        {
            string dir = Environment.GetEnvironmentVariable("TYPEBEAT_GAP_OSU_DIR") ?? string.Empty;

            if (string.IsNullOrEmpty(dir))
                Assert.Ignore("TYPEBEAT_GAP_OSU_DIR is not set; skipping real-map instrumental-gap pin.");

            string path = Path.Combine(dir, fileName);

            if (!File.Exists(path))
                Assert.Ignore($"Real map file not present (expected {path}); skipping pin.");

            return path;
        }

        /// <summary>
        /// The real gameplay pipeline from stored map text to the engine's typing lines:
        /// decode (LyricBeatmapDecoder via the registered magic), convert
        /// (TypeBeatBeatmapConverter, as GetPlayableBeatmap does), then build the engine the
        /// exact way DrawableTypeBeatRuleset.createEngine does.
        /// </summary>
        private static IReadOnlyList<TypingLine> realPipelineLines(string osuPath)
        {
            LyricBeatmapDecoder.Register();

            typebeat.Game.Beatmaps.Beatmap decoded;

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(osuPath))))
            using (var reader = new LineBufferedReader(stream))
                decoded = typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<typebeat.Game.Beatmaps.Beatmap>(reader).Decode(reader);

            Assert.That(decoded.HitObjects, Is.Not.Empty, "decoded map has hit objects");

            var converter = new TypeBeatBeatmapConverter(decoded, new TypeBeatRuleset());
            Assert.That(converter.CanConvert(), Is.True);
            var converted = converter.Convert(CancellationToken.None);

            var lineObjects = converted.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();
            Assert.That(lineObjects, Is.Not.Empty, "converted map has typebeat objects");

            for (int i = 0; i < lineObjects.Count; i++)
                lineObjects[i].LineIndex = i;

            TimingGranularity granularity = lineObjects[0].Granularity;

            var lyricBeatmap = new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata
                {
                    Artist = decoded.Metadata.Artist,
                    Title = decoded.Metadata.Title,
                    FolderPath = string.Empty,
                    AudioFileName = decoded.Metadata.AudioFile,
                    HasWordTiming = granularity != TimingGranularity.Line,
                },
                Lines = lineObjects.Select(h => h.Line).ToList(),
                Granularity = granularity,
            };

            return new TypingEngine(lyricBeatmap).Lines;
        }

        private static IReadOnlyList<InstrumentalGap> computeAndDump(string osuPath)
        {
            var lines = realPipelineLines(osuPath);

            TestContext.Out.WriteLine($"== {Path.GetFileName(osuPath)}: {lines.Count} lines ==");

            for (int i = 0; i < lines.Count - 1; i++)
            {
                double perceived = lines[i + 1].FirstVocalTime - lines[i].SingEndTime;

                if (perceived < 4000)
                    continue;

                TestContext.Out.WriteLine(
                    $"pair {i}->{i + 1}: perceived={perceived:F0}ms  prevSingEnd={lines[i].SingEndTime:F0}  prevEnd={lines[i].EndTime:F0}  " +
                    $"prevGrace={lines[i].SealGraceMs:F0}  nextStart={lines[i + 1].StartTime:F0}  nextActivation={lines[i + 1].ActivationTime:F0}  " +
                    $"nextFirstVocal={lines[i + 1].FirstVocalTime:F0}");
            }

            var gaps = InstrumentalGaps.Compute(lines);

            foreach (var g in gaps)
                TestContext.Out.WriteLine($"QUALIFIED: start={g.GapStartTime:F0} activation={g.ActivationTime:F0} skipTarget={g.SkipTarget:F0}");

            return gaps;
        }

        /// <summary>
        /// Real-decoder invariant the skip must survive: a non-last line's EndTime IS the next
        /// line's StartTime (contiguous windows, no timeline hole). Anchoring a skip window on
        /// "EndTime + grace" therefore always lands at/after the next line's activation.
        /// </summary>
        [TestCase("immortal-flame.osu")]
        [TestCase("neon-rain.osu")]
        public void RealDecodeProducesContiguousLineWindows(string fileName)
        {
            var lines = realPipelineLines(requireOsu(fileName));

            for (int i = 0; i < lines.Count - 1; i++)
                Assert.That(lines[i].EndTime, Is.EqualTo(lines[i + 1].StartTime).Within(1e-6), $"line {i} EndTime == line {i + 1} StartTime");
        }

        [Test]
        public void ImmortalFlameHasQualifyingSkippableGaps()
        {
            var gaps = computeAndDump(requireOsu("immortal-flame.osu"));

            Assert.That(gaps, Is.Not.Empty, "Immortal Flame must expose at least one mid-song skip");

            foreach (var g in gaps)
            {
                Assert.That(g.SkipTarget - g.GapStartTime, Is.GreaterThanOrEqualTo(InstrumentalGaps.MIN_SKIP_WINDOW_MS),
                    "skip period must be usable (overlay would expire otherwise)");
                Assert.That(g.ActivationTime - g.SkipTarget, Is.EqualTo(InstrumentalGaps.SKIP_LEAD_MS).Within(1e-6));
            }
        }

        [Test]
        public void NeonRainHasQualifyingSkippableGaps()
        {
            var gaps = computeAndDump(requireOsu("neon-rain.osu"));

            Assert.That(gaps, Is.Not.Empty, "NEON RAIN must expose at least one mid-song skip");

            foreach (var g in gaps)
                Assert.That(g.SkipTarget - g.GapStartTime, Is.GreaterThanOrEqualTo(InstrumentalGaps.MIN_SKIP_WINDOW_MS));
        }
    }
}
