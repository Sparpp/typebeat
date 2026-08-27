// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Real-map pins for autoplay. Runs the REAL data path on the user's actual installed maps:
// stored .osu text -> LyricBeatmapDecoder -> TypeBeatBeatmapConverter -> TypeBeatAutoGenerator ->
// TypingEngine, driven the way the playfield's engine ticker drives it. The map files are the
// player's own installs (copied out of the game data files store), so they are NOT in this repo;
// the directory is supplied via the TYPEBEAT_GAP_OSU_DIR environment variable (shared with
// InstrumentalGapsRealMapTest) and the pins Assert.Ignore when it is unset.
//
// These pins exist because of backlog 51: autoplay failed "Busta Rhymes Goes To The Wii Shop
// Channel" ~15.7s in. Only real maps have the FRACTIONAL line boundaries the bug needed, and only
// this one had an all-freestyle line long enough to hide the resulting one-cell drift until it hit
// a space cell and mashed the wrong key 16 times in a row.

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
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class AutoplayRealMapTest
    {
        /// <summary>Display-frame period the engine ticker runs at; presses interleave with it.</summary>
        private const double frame_ms = 1000.0 / 60;

        private static string requireOsu(string fileName)
        {
            string dir = Environment.GetEnvironmentVariable("TYPEBEAT_GAP_OSU_DIR") ?? string.Empty;

            if (string.IsNullOrEmpty(dir))
                Assert.Ignore("TYPEBEAT_GAP_OSU_DIR is not set; skipping real-map autoplay pin.");

            string path = Path.Combine(dir, fileName);

            if (!File.Exists(path))
                Assert.Ignore($"Real map file not present (expected {path}); skipping pin.");

            return path;
        }

        private static IReadOnlyList<TypeBeatHitObject> realPipelineLineObjects(string osuPath)
        {
            LyricBeatmapDecoder.Register();

            typebeat.Game.Beatmaps.Beatmap decoded;

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(osuPath))))
            using (var reader = new LineBufferedReader(stream))
                decoded = typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<typebeat.Game.Beatmaps.Beatmap>(reader).Decode(reader);

            var converter = new TypeBeatBeatmapConverter(decoded, new TypeBeatRuleset());
            Assert.That(converter.CanConvert(), Is.True);
            var converted = converter.Convert(CancellationToken.None);

            var lineObjects = converted.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();
            Assert.That(lineObjects, Is.Not.Empty, "converted map has typebeat objects");

            for (int i = 0; i < lineObjects.Count; i++)
                lineObjects[i].LineIndex = i;

            return lineObjects;
        }

        /// <summary>
        /// Autoplay must PERFECT any real map: every generated press reaches a live cell, nothing is
        /// rejected, nothing is missed, and the health bar never drops (a wrong-key streak of
        /// <see cref="TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK"/> fails the play outright).
        ///
        /// <para>Run on BOTH carets since backlog 218. The generator clamps every press into its
        /// line's own typeable window, so no press is ever earlier than that line's ActivationTime
        /// and the rush bound (which opens 1500 ms before it) can never refuse the caret the press
        /// needs. That is an argument, and this is the pin: a bound that arrived late would strand
        /// autoplay on the previous line and refuse the press outright, on the real fractional
        /// boundaries only real maps have.</para>
        /// </summary>
        [Test]
        public void AutoplayPerfectsRealMap(
            [Values("wii-shop.osu", "immortal-flame.osu", "neon-rain.osu")] string fileName,
            [Values] bool flexible)
        {
            var lineObjects = realPipelineLineObjects(requireOsu(fileName));

            var map = new TypeBeatBeatmap();

            foreach (var lineObject in lineObjects)
                map.HitObjects.Add(lineObject);

            var lyricBeatmap = new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "a", Title = fileName, FolderPath = string.Empty, AudioFileName = "a.mp3" },
                Lines = lineObjects.Select(h => h.Line).ToList(),
                Granularity = lineObjects[0].Granularity,
            };

            var frames = new TypeBeatAutoGenerator(map).Generate().Frames.Cast<TypeBeatReplayFrame>().ToList();
            Assert.That(frames, Is.Not.Empty);

            // flexible: the shipped stack (unpinned caret, the line-start snap and the bounded rush);
            // otherwise the pinned engine, which is both the classic era and the Fletcher mod's.
            var engine = new TypingEngine(lyricBeatmap) { FletcherEnabled = flexible, FlexibleLineSnap = flexible, BoundedRush = flexible };

            int next = 0;
            double end = lyricBeatmap.LastLineEnd + 10000;

            // Exactly TypeBeatPlayfield.EngineTicker: per-display-frame Updates, with each due
            // replay frame applied as Update(frameTime) + the keystroke.
            for (double now = 0; now <= end; now += frame_ms)
            {
                while (next < frames.Count && frames[next].Time <= now)
                {
                    var frame = frames[next];
                    engine.Update(frame.Time);

                    Assert.IsTrue(engine.ProcessKey(frame.Character, frame.Time),
                        $"'{frame.Character}' @ {frame.Time} must reach a live cell (line {engine.ActiveLineIndex}, caret {engine.CaretIndex})");
                    Assert.AreEqual(0, engine.ConsecutiveWrongKeys,
                        $"'{frame.Character}' @ {frame.Time} was rejected (line {engine.ActiveLineIndex}, caret {engine.CaretIndex})");

                    next++;
                }

                engine.Update(now);
            }

            Assert.AreEqual(frames.Count, next, "every generated frame must be consumed before the map ends");
            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(1.0, engine.LiveAccuracy, "no wrong keys");
            Assert.AreEqual(0, engine.Mistypes, "autoplay must submit a zero mistype count (backlog 72)");

            int missed = engine.Lines.Sum(l => l.Cells.Count(c => c.State == CellState.Missed));
            Assert.AreEqual(0, missed, "autoplay must not miss a single cell");
        }
    }
}
