// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 174 stage 2: syllable-span judgement. Characters belong to a SYLLABLE
// (TypingLine.Syllables, built always, mode or no mode), and under TypingEngine.SyllableTiming a
// keypress on a cell of a syllable is perfectly timed anywhere inside that syllable's sung span:
// judged delta 0 in [StartTime, EndTime] (edge-inclusive), signed distance to the nearer edge
// outside it, graded through the unchanged SyncWindows ladder and stored in JudgedDelta so
// points, combo, sync and results all keep working unmodified. Flag off (the default, a Release
// build, and every replay re-derivation) is byte-identical to the classic point-target rule.
//
// The hand-computed deltas, points and sync figures in this fixture are CROSS-CHECKS, worked out
// independently from the window constants (Line tier: Great [-250, 400], Ok [-600, 1000],
// Meh [-1200, 2000]) and the scoring rules (base 300/150/50, multiplier 1 + min(combo, 50) / 50
// read before the increment).

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
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class SyllableTimingTest
    {
        #region Fixture builders

        private static TimedUnit unit(string text, double start, double end, params double[] syllables)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end, SyllableBoundaries = syllables };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static LyricBeatmap map(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = lines,
            Granularity = TimingGranularity.Line,
        };

        /// <summary>
        /// "open door", flat ramps: o=1000, p=1250, e=1500, n=1750, ' '=2000, d=2000, o=2250,
        /// o=2500, r=2750. Natural syllables: o|pen (split at 1) and door (one). Groups therefore
        /// span [1000, 1250], [1250, 2000] (unit end) and [2000, 3000].
        /// </summary>
        private static LyricBeatmap openDoor() => map(line("open door", 1000, 60000, 3000,
            unit("open", 1000, 2000), unit("door", 2000, 3000)));

        /// <summary>
        /// "probably" with 2 mapper boundaries at 1250 and 1500 inside [1000, 1800]. The even
        /// index spread times the 8 chars 3|3|2 across the segments (j = 0..7: 1000, 1093.75,
        /// 1187.5, 1281.25, 1375, 1468.75, 1575, 1687.5), while the syllabifier says pro|ba|bly,
        /// 3|2|3. The whole point of the item is that the mapper's times win and the syllabifier's
        /// characters win.
        /// </summary>
        private static LyricBeatmap probably() => map(line("probably", 1000, 60000, 1800,
            unit("probably", 1000, 1800, 1250, 1500)));

        private static TypingEngine started(LyricBeatmap beatmap, bool syllableTiming)
        {
            var engine = new TypingEngine(beatmap) { SyllableTiming = syllableTiming };
            engine.Update(1000);
            Assert.AreEqual(0, engine.ActiveLineIndex);
            return engine;
        }

        private static List<CharJudgement> record(TypingEngine engine)
        {
            var judged = new List<CharJudgement>();
            engine.CharJudged += j => judged.Add(j);
            return judged;
        }

        #endregion

        #region Syllable structure

        [Test]
        public void NoSubtimingsGroupsTheCharsYouAlreadyTimed()
        {
            var tl = TypingLine.FromLyricLine(openDoor().Lines[0]);

            Assert.AreEqual(3, tl.Syllables.Count);

            // Edges are asserted against the cells' OWN targets, not recomputed values: this case
            // is exactly "group the chars you already timed", so nothing may move.
            Assert.AreEqual(new SyllableGroup(0, 1, tl.Cells[0].TargetTime, tl.Cells[1].TargetTime), tl.Syllables[0]);
            Assert.AreEqual(new SyllableGroup(1, 4, tl.Cells[1].TargetTime, 2000), tl.Syllables[1]);
            Assert.AreEqual(new SyllableGroup(5, 9, tl.Cells[5].TargetTime, 3000), tl.Syllables[2]);

            // A syllable ending a word runs to the unit's end, which is also the space cell's target.
            Assert.AreEqual(tl.Cells[4].TargetTime, tl.Syllables[1].EndTime, 1e-9);

            Assert.AreEqual(0, tl.SyllableIndexOf(0));
            Assert.AreEqual(1, tl.SyllableIndexOf(1));
            Assert.AreEqual(1, tl.SyllableIndexOf(3));
            Assert.AreEqual(-1, tl.SyllableIndexOf(4), "the inter-word space cell is in no syllable");
            Assert.AreEqual(2, tl.SyllableIndexOf(5));
            Assert.AreEqual(2, tl.SyllableIndexOf(8));
            Assert.AreEqual(-1, tl.SyllableIndexOf(-1));
            Assert.AreEqual(-1, tl.SyllableIndexOf(9));

            assertLineInvariants(tl);
        }

        [Test]
        public void MapperSubtimingsTakeTheSyllabifierCharSplit()
        {
            var tl = TypingLine.FromLyricLine(probably().Lines[0]);

            // The flat targets themselves have NOT moved (pinned against the current
            // syllableCharTarget spread: stage 2 adds groups, it does not retime cells).
            double[] expectedTargets = { 1000, 1093.75, 1187.5, 1281.25, 1375, 1468.75, 1575, 1687.5 };

            for (int i = 0; i < expectedTargets.Length; i++)
                Assert.AreEqual(expectedTargets[i], tl.Cells[i].TargetTime, 1e-9, $"cell {i} target must not move");

            // 2 boundaries = 3 groups whose edges are EXACTLY the boundary times, and whose char
            // assignment is the syllabifier's pro|ba|bly (3|2|3), NOT the old even 3|3|2 spread.
            Assert.AreEqual(3, tl.Syllables.Count);
            Assert.AreEqual(new SyllableGroup(0, 3, 1000, 1250), tl.Syllables[0]);
            Assert.AreEqual(new SyllableGroup(3, 5, 1250, 1500), tl.Syllables[1]);
            Assert.AreEqual(new SyllableGroup(5, 8, 1500, 1800), tl.Syllables[2]);

            Assert.AreEqual(2, tl.SyllableIndexOf(5), "cell 5 ('b' of |bly) belongs to the THIRD syllable under the syllabifier split");

            assertLineInvariants(tl);
        }

        [Test]
        public void OverForcedShortWordDegradesToFewerGroups()
        {
            // "go" cannot carry 4 syllables (3 boundaries): the syllabifier degrades to 2 groups,
            // so the first G - 1 = 1 boundary time is the interior edge and the last group runs to
            // the unit's end. The 2nd and 3rd boundary times are simply unused.
            var tl = TypingLine.FromLyricLine(map(line("go", 1000, 60000, 2000,
                unit("go", 1000, 2000, 1200, 1400, 1600))).Lines[0]);

            Assert.AreEqual(2, tl.Syllables.Count);
            Assert.AreEqual(new SyllableGroup(0, 1, 1000, 1200), tl.Syllables[0]);
            Assert.AreEqual(new SyllableGroup(1, 2, 1200, 2000), tl.Syllables[1]);

            assertLineInvariants(tl);
        }

        #endregion

        #region Flag off: byte-identical to the classic rule

        /// <summary>
        /// Every expected value here is the CURRENT (pre-change) engine's behaviour, hand-computed
        /// from the point deltas: this test would have passed before stage 2 landed.
        /// </summary>
        [Test]
        public void FlagOffJudgesExactlyAsBeforeWithoutSubtimings()
        {
            var engine = started(openDoor(), syllableTiming: false);
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('o', 1000)); // delta 0      -> Great, 300 * 1.00 = 300
            Assert.IsTrue(engine.ProcessKey('p', 1900)); // delta 650    -> Ok,    150 * 1.02 = 153
            Assert.IsTrue(engine.ProcessKey('e', 1900)); // delta 400    -> Great, 300 * 1.04 = 312
            Assert.IsTrue(engine.ProcessKey('n', 2700)); // delta 950    -> Ok,    150 * 1.06 = 159
            Assert.IsTrue(engine.ProcessKey(' ', 2700)); // untimed      -> Great, 300 * 1.08 = 324
            Assert.IsTrue(engine.ProcessKey('d', 900));  // delta -1100  -> Meh,    50 * 1.10 = 55

            Assert.AreEqual(new[] { JudgementType.Great, JudgementType.Ok, JudgementType.Great, JudgementType.Ok, JudgementType.Great, JudgementType.Meh },
                judged.Select(j => j.Type).ToArray());
            Assert.AreEqual(new double[] { 0, 650, 400, 950, 0, -1100 }, judged.Select(j => j.Delta).ToArray());

            Assert.AreEqual(1303, engine.Score);
            Assert.AreEqual(6, engine.MaxCombo);
            Assert.AreEqual(6, engine.Combo);

            var results = engine.BuildResults();
            Assert.AreEqual(3, results.Counts[JudgementType.Great]);
            Assert.AreEqual(2, results.Counts[JudgementType.Ok]);
            Assert.AreEqual(1, results.Counts[JudgementType.Meh]);

            // 8 timed cells (the space is out under the default Untimed rule; 3 are still untyped
            // and contribute 0): q = 1, 1 - 650/2000, 1 - 400/2000, 1 - 950/2000, 1 - 1100/1200.
            double expectedSync = 100 * (1 + (1 - 650 / 2000.0) + (1 - 400 / 2000.0) + (1 - 950 / 2000.0) + (1 - 1100 / 1200.0)) / 8;
            Assert.AreEqual(expectedSync, results.SyncPercent, 1e-9);
        }

        /// <summary>
        /// Same guarantee over a map WITH mapper subtimings: the piecewise char spread is
        /// untouched, and flag-off judgement still reads the point targets it always read.
        /// </summary>
        [Test]
        public void FlagOffJudgesExactlyAsBeforeWithSubtimings()
        {
            var engine = started(probably(), syllableTiming: false);
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('p', 1000));    // delta 0      -> Great, 300
            Assert.IsTrue(engine.ProcessKey('r', 1500));    // delta 406.25 -> Ok,    153
            Assert.IsTrue(engine.ProcessKey('o', 1187.5));  // Great, 312
            Assert.IsTrue(engine.ProcessKey('b', 1281.25)); // Great, 318
            Assert.IsTrue(engine.ProcessKey('a', 1375));    // Great, 324
            Assert.IsTrue(engine.ProcessKey('b', 1468.75)); // Great, 330
            Assert.IsTrue(engine.ProcessKey('l', 1575));    // Great, 336
            Assert.IsTrue(engine.ProcessKey('y', 1687.5));  // Great, 342

            Assert.AreEqual(406.25, judged[1].Delta, 1e-9);
            Assert.AreEqual(JudgementType.Ok, judged[1].Type);
            Assert.AreEqual(7, judged.Count(j => j.Type == JudgementType.Great));

            Assert.AreEqual(2415, engine.Score);
            Assert.AreEqual(8, engine.MaxCombo);
        }

        #endregion

        #region Flag on: the syllable span is the window

        [Test]
        public void AnyCharOfASyllableIsPerfectWhileItIsSung()
        {
            var engine = started(openDoor(), syllableTiming: true);
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('o', 1000)); // in [1000, 1250]: delta 0, Great, 300
            // TWO DIFFERENT CHARS of the "pen" syllable pressed at the same in-span time: both
            // delta 0, both Great, where the point rule would have graded p 650 late and e 400 late.
            Assert.IsTrue(engine.ProcessKey('p', 1900)); // in [1250, 2000]: delta 0, Great, 306
            Assert.IsTrue(engine.ProcessKey('e', 1900)); // in [1250, 2000]: delta 0, Great, 312
            // Outside the span the ladder grades DISTANCE FROM THE EDGE. Cross-checks:
            // n at 2700 is 700 past the syllable's end 2000 -> Ok (400 < 700 <= 1000), 159.
            Assert.IsTrue(engine.ProcessKey('n', 2700));
            Assert.IsTrue(engine.ProcessKey(' ', 2700)); // no group; untimed space, delta 0, Great, 324
            // d at 900 is 1100 before the "door" syllable's start 2000 -> Meh (600 < 1100 <= 1200), 55.
            Assert.IsTrue(engine.ProcessKey('d', 900));

            Assert.AreEqual(new[] { JudgementType.Great, JudgementType.Great, JudgementType.Great, JudgementType.Ok, JudgementType.Great, JudgementType.Meh },
                judged.Select(j => j.Type).ToArray());
            Assert.AreEqual(new double[] { 0, 0, 0, 700, 0, -1100 }, judged.Select(j => j.Delta).ToArray());

            // The span delta is what is STORED, so every readout that re-reads JudgedDelta (sync
            // tint, sync percent, results) agrees with the judgement it was handed.
            Assert.AreEqual(0, engine.Lines[0].Cells[1].JudgedDelta!.Value, 1e-9);
            Assert.AreEqual(0, engine.Lines[0].Cells[2].JudgedDelta!.Value, 1e-9);
            Assert.AreEqual(700, engine.Lines[0].Cells[3].JudgedDelta!.Value, 1e-9);
            Assert.AreEqual(-1100, engine.Lines[0].Cells[5].JudgedDelta!.Value, 1e-9);

            Assert.AreEqual(300 + 306 + 312 + 159 + 324 + 55, engine.Score);
            Assert.AreEqual(6, engine.MaxCombo);
        }

        [Test]
        public void SpanEdgesAreInclusive()
        {
            var engine = started(openDoor(), syllableTiming: true);
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('o', 1250)); // exactly ON the end edge of [1000, 1250]
            Assert.IsTrue(engine.ProcessKey('p', 1250)); // exactly ON the start edge of [1250, 2000]

            Assert.AreEqual(0, judged[0].Delta, 1e-9);
            Assert.AreEqual(0, judged[1].Delta, 1e-9);
            Assert.AreEqual(JudgementType.Great, judged[0].Type);
            Assert.AreEqual(JudgementType.Great, judged[1].Type);
        }

        /// <summary>
        /// A wrong (typed-through) key on a syllable cell carries the same SPAN delta in its
        /// CharJudgement, so the stage's feedback agrees with what a correct press there would
        /// have been judged on.
        /// </summary>
        [Test]
        public void WrongCharCarriesTheSpanDelta()
        {
            var engine = started(openDoor(), syllableTiming: true);
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('x', 1900)); // wrong char on cell 0; span [1000, 1250], 650 past the end

            Assert.AreEqual(JudgementType.WrongChar, judged[0].Type);
            Assert.AreEqual(650, judged[0].Delta, 1e-9);
        }

        /// <summary>
        /// Under mapper subtimings a cell's flat-ramp target can sit OUTSIDE its syllable's span
        /// when the syllabifier's char split disagrees with the old even spread ("probably": cell 5
        /// is timed at 1468.75 inside segment 2 but belongs to syllable 3, [1500, 1800]). Autoplay
        /// presses at the target, so the press judges on the 31.25 ms distance to the edge: still
        /// comfortably Great, which is what keeps autoplay perfect with no generator change.
        /// </summary>
        [Test]
        public void TargetTimePressesStayGreatWhenTargetSitsOutsideTheSpan()
        {
            var engine = started(probably(), syllableTiming: true);
            var judged = record(engine);

            foreach (var cell in engine.Lines[0].Cells)
                Assert.IsTrue(engine.ProcessKey(cell.Expected, cell.TargetTime));

            Assert.IsTrue(judged.All(j => j.Type == JudgementType.Great), "every target-time press judges Great");
            Assert.AreEqual(-31.25, judged[5].Delta, 1e-9, "cell 5's target is 31.25 ms before its syllable starts");
            Assert.AreEqual(0, judged[4].Delta, 1e-9);
        }

        [Test]
        public void SpaceCellKeepsThePointDeltaUnderTheTimedEra()
        {
            var engine = started(openDoor(), syllableTiming: true);
            engine.SpaceTiming = SpaceTimingRule.Timed;
            var judged = record(engine);

            Assert.IsTrue(engine.ProcessKey('o', 1000));
            Assert.IsTrue(engine.ProcessKey('p', 1300));
            Assert.IsTrue(engine.ProcessKey('e', 1500));
            Assert.IsTrue(engine.ProcessKey('n', 1750));
            // The space cell is in NO syllable, so with spaces back inside the timing challenge
            // (the pre-148 era, replay re-derivation only) it is judged on its classic POINT
            // delta: 2500 - 2000 = 500 -> Ok.
            Assert.IsTrue(engine.ProcessKey(' ', 2500));

            Assert.AreEqual(-1, engine.Lines[0].SyllableIndexOf(4));
            Assert.AreEqual(JudgementType.Ok, judged[4].Type);
            Assert.AreEqual(500, judged[4].Delta, 1e-9);
            Assert.AreEqual(500, engine.Lines[0].Cells[4].JudgedDelta!.Value, 1e-9);
        }

        #endregion

        #region Line-level invariants, incl. real maps

        private static void assertLineInvariants(TypingLine tl)
        {
            var groups = tl.Syllables;
            double prevEnd = double.NegativeInfinity;
            int prevCellEnd = 0;

            for (int g = 0; g < groups.Count; g++)
            {
                var grp = groups[g];

                Assert.Less(grp.StartCell, grp.EndCellExclusive, $"group {g} owns at least one cell");
                Assert.GreaterOrEqual(grp.StartCell, prevCellEnd, $"group {g} cell range must not overlap its predecessor");
                Assert.LessOrEqual(grp.StartTime, grp.EndTime, $"group {g} span must not be inverted");
                Assert.GreaterOrEqual(grp.StartTime, prevEnd, $"group {g} times must be monotonic across the line");

                prevEnd = grp.EndTime;
                prevCellEnd = grp.EndCellExclusive;
            }

            for (int i = 0; i < tl.Cells.Count; i++)
            {
                var cell = tl.Cells[i];
                int s = tl.SyllableIndexOf(i);

                if (cell.IsTypeable && cell.Expected != ' ')
                {
                    Assert.GreaterOrEqual(s, 0, $"typeable non-space cell {i} ('{cell.Expected}') must be in a group");
                    Assert.GreaterOrEqual(i, groups[s].StartCell, $"cell {i} inside its group's range");
                    Assert.Less(i, groups[s].EndCellExclusive, $"cell {i} inside its group's range");
                }
                else
                    Assert.AreEqual(-1, s, $"cell {i} ('{cell.Expected}') must be in no group");
            }
        }

        private static string requireOsu(string fileName)
        {
            string dir = Environment.GetEnvironmentVariable("TYPEBEAT_GAP_OSU_DIR") ?? string.Empty;

            if (string.IsNullOrEmpty(dir))
                Assert.Ignore("TYPEBEAT_GAP_OSU_DIR is not set; skipping real-map syllable pin.");

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
        /// Every line of a real packaged map (wii-shop carries authored subtimings at Syllable
        /// granularity, the other two have none) satisfies the structural invariants, in both the
        /// default and the Literate flattening.
        /// </summary>
        [TestCase("wii-shop.osu")]
        [TestCase("immortal-flame.osu")]
        [TestCase("neon-rain.osu")]
        public void SyllableInvariantsHoldOnRealMap(string fileName)
        {
            var lineObjects = realPipelineLineObjects(requireOsu(fileName));
            var granularity = lineObjects[0].Granularity;

            foreach (bool literate in new[] { false, true })
            {
                foreach (var lineObject in lineObjects)
                    assertLineInvariants(TypingLine.FromLyricLine(lineObject.Line, granularity, literate));
            }
        }

        /// <summary>
        /// Autoplay under SyllableTiming still perfects a real map with NO generator change: the
        /// first char of a syllable is pressed at the span's start (edge-inclusive) and every other
        /// press lands inside or within a few ms of its span, so every press judges Great.
        /// </summary>
        [TestCase("wii-shop.osu")]
        [TestCase("immortal-flame.osu")]
        [TestCase("neon-rain.osu")]
        public void AutoplayIsAllGreatUnderSyllableTiming(string fileName)
        {
            const double frame_ms = 1000.0 / 60;

            var lineObjects = realPipelineLineObjects(requireOsu(fileName));

            var beatmap = new TypeBeatBeatmap();

            foreach (var lineObject in lineObjects)
                beatmap.HitObjects.Add(lineObject);

            var lyricBeatmap = new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "a", Title = fileName, FolderPath = string.Empty, AudioFileName = "a.mp3" },
                Lines = lineObjects.Select(h => h.Line).ToList(),
                Granularity = lineObjects[0].Granularity,
            };

            var frames = new TypeBeatAutoGenerator(beatmap).Generate().Frames.Cast<TypeBeatReplayFrame>().ToList();
            Assert.That(frames, Is.Not.Empty);

            var engine = new TypingEngine(lyricBeatmap) { SyllableTiming = true };

            int next = 0;
            double end = lyricBeatmap.LastLineEnd + 10000;

            for (double now = 0; now <= end; now += frame_ms)
            {
                while (next < frames.Count && frames[next].Time <= now)
                {
                    var frame = frames[next];
                    engine.Update(frame.Time);

                    Assert.IsTrue(engine.ProcessKey(frame.Character, frame.Time),
                        $"'{frame.Character}' @ {frame.Time} must reach a live cell (line {engine.ActiveLineIndex}, caret {engine.CaretIndex})");

                    next++;
                }

                engine.Update(now);
            }

            Assert.AreEqual(frames.Count, next, "every generated frame must be consumed before the map ends");
            Assert.IsTrue(engine.IsFinished);

            var results = engine.BuildResults();
            Assert.AreEqual(frames.Count, results.Counts[JudgementType.Great], "every autoplay press judges Great");
            Assert.AreEqual(0, results.Counts[JudgementType.Ok]);
            Assert.AreEqual(0, results.Counts[JudgementType.Meh]);
            Assert.AreEqual(0, results.Counts[JudgementType.Premature]);
            Assert.AreEqual(0, results.Counts[JudgementType.Lagging]);
            Assert.AreEqual(0, engine.Lines.Sum(l => l.Cells.Count(c => c.State == CellState.Missed)));
        }

        #endregion
    }
}
