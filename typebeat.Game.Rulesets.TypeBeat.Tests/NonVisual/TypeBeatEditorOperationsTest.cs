// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The editor mutation core: every operation must (a) respect the immutable-model rebuild
    /// pattern, (b) maintain the boundary invariant EndTime_i == StartTime_(i+1), and (c) be
    /// RELOAD-STABLE — encoding the edited beatmap and decoding it back reproduces exactly the
    /// state the editor showed (the format derives line EndTimes, so any drift here would make
    /// saved edits silently change on reopen).
    /// </summary>
    [TestFixture]
    public class TypeBeatEditorOperationsTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        /// <summary>
        /// Three word-timed lines: [1000..3000], [3000..6000], [6000..8000 (singEnd 7000, tail-capped)].
        /// </summary>
        private static EditorBeatmap createBeatmap()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Test";
            beatmap.Metadata.AudioFile = "audio.mp3";

            addLine(beatmap, 0, "alpha beta", 1000, 3000, 2800, (1000, 1800), (1900, 2800));
            addLine(beatmap, 1, "gamma delta", 3000, 6000, 5500, (3000, 4200), (4300, 5500));
            addLine(beatmap, 2, "omega", 6000, 8000, 7000, (6000, 7000));

            return new EditorBeatmap(beatmap);
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end, double singEnd, params (double s, double e)[] words)
        {
            string[] tokens = text.Split(' ');

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Line = new LyricLine
                {
                    RawText = text,
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = singEnd,
                    Units = words.Select((w, i) => new TimedUnit
                    {
                        Text = tokens[i],
                        StartTime = w.s,
                        EndTime = w.e,
                        Source = TimingSource.Explicit,
                    }).ToArray(),
                },
                Granularity = TimingGranularity.Word,
            });
        }

        private static TypeBeatHitObject lineAt(EditorBeatmap editorBeatmap, int index)
            => TypeBeatEditorOperations.OrderedLines(editorBeatmap)[index];

        /// <summary>Encode → decode → compare every persisted field against the in-memory state.</summary>
        private static void assertReloadStable(EditorBeatmap editorBeatmap)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(editorBeatmap, sw);

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
            using var reader = new typebeat.Game.IO.LineBufferedReader(stream);
            var decoded = typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);

            var expected = TypeBeatEditorOperations.OrderedLines(editorBeatmap);
            var actual = decoded.HitObjects.OfType<TypeBeatHitObject>().OrderBy(o => o.LineIndex).ToList();

            Assert.That(actual.Count, Is.EqualTo(expected.Count), "line count drifted on reload");

            for (int i = 0; i < expected.Count; i++)
            {
                var e = expected[i].Line;
                var a = actual[i].Line;

                Assert.That(a.RawText, Is.EqualTo(e.RawText), $"line {i} text");
                Assert.That(a.StartTime, Is.EqualTo(e.StartTime).Within(1e-6), $"line {i} start");
                Assert.That(a.EndTime, Is.EqualTo(e.EndTime).Within(1e-6), $"line {i} end");
                Assert.That(a.SingEndTime, Is.EqualTo(e.SingEndTime).Within(1e-6), $"line {i} singEnd");
                Assert.That(a.SealGraceMs, Is.EqualTo(e.SealGraceMs).Within(1e-6), $"line {i} sealGrace");
                Assert.That(a.Units.Count, Is.EqualTo(e.Units.Count), $"line {i} unit count");

                for (int u = 0; u < e.Units.Count; u++)
                {
                    Assert.That(a.Units[u].Text, Is.EqualTo(e.Units[u].Text), $"line {i} unit {u} text");
                    Assert.That(a.Units[u].StartTime, Is.EqualTo(e.Units[u].StartTime).Within(1e-6), $"line {i} unit {u} start");
                    Assert.That(a.Units[u].EndTime, Is.EqualTo(e.Units[u].EndTime).Within(1e-6), $"line {i} unit {u} end");
                }
            }
        }

        [Test]
        public void BoundaryDragMovesBothNeighbours()
        {
            var editorBeatmap = createBeatmap();

            TypeBeatEditorOperations.SetLineStart(editorBeatmap, lineAt(editorBeatmap, 1), 3400);

            Assert.That(lineAt(editorBeatmap, 0).Line.EndTime, Is.EqualTo(3400));
            Assert.That(lineAt(editorBeatmap, 1).Line.StartTime, Is.EqualTo(3400));
            Assert.That(lineAt(editorBeatmap, 1).StartTime, Is.EqualTo(3400)); // hit object mirror

            // Units of line 1 that started before the new boundary were clamped up.
            Assert.That(lineAt(editorBeatmap, 1).Line.Units[0].StartTime, Is.EqualTo(3400));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void BoundaryDragClampsAgainstNeighbourSpans()
        {
            var editorBeatmap = createBeatmap();

            // Try to drag line 1's start before line 0's start — clamps to min span after it.
            TypeBeatEditorOperations.SetLineStart(editorBeatmap, lineAt(editorBeatmap, 1), 500);
            Assert.That(lineAt(editorBeatmap, 1).Line.StartTime, Is.EqualTo(1000 + TypeBeatEditorOperations.MIN_SPAN_MS));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void SingEndOnLastLineKeepsWindowReloadStable()
        {
            var editorBeatmap = createBeatmap();

            // Pull the last line's sung end far earlier: the typeable window must cap at
            // singEnd + tail or reload would shrink it.
            TypeBeatEditorOperations.SetSingEnd(editorBeatmap, lineAt(editorBeatmap, 2), 6200);

            Assert.That(lineAt(editorBeatmap, 2).Line.SingEndTime, Is.EqualTo(6200));
            Assert.That(lineAt(editorBeatmap, 2).Line.EndTime, Is.EqualTo(8000)); // 8000 <= 6200 + 3000

            // 8000 stays within [6050, 6050 + tail] — no clamp needed, and reload agrees.
            TypeBeatEditorOperations.SetSingEnd(editorBeatmap, lineAt(editorBeatmap, 2), 6050);
            Assert.That(lineAt(editorBeatmap, 2).Line.EndTime, Is.EqualTo(8000));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void UnitRetimeClampsAgainstNeighboursAndBecomesExplicit()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 0);

            // Drag "beta" (unit 1) to overlap "alpha" (ends 1800): clamps to alpha's end.
            TypeBeatEditorOperations.SetUnitTiming(editorBeatmap, line, 1, 1500, 2600);

            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(1800));
            Assert.That(line.Line.Units[1].EndTime, Is.EqualTo(2600));
            Assert.That(line.Line.Units[1].Source, Is.EqualTo(TimingSource.Explicit));
            Assert.That(line.Line.Units[1].Confidence, Is.EqualTo(1));
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(1000)); // untouched neighbour

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void MoveUnitPreservesWidthAndStopsAtNeighbour()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 1); // "gamma delta": gamma [3000,4200], delta [4300,5500]

            double gammaWidth = line.Line.Units[0].EndTime - line.Line.Units[0].StartTime; // 1200

            // Try to shove gamma far right into delta. It must keep its width and stop against
            // delta's start (4300), NOT get squashed.
            TypeBeatEditorOperations.MoveUnit(editorBeatmap, line, 0, 9000);

            var gamma = line.Line.Units[0];
            Assert.That(gamma.EndTime - gamma.StartTime, Is.EqualTo(gammaWidth).Within(1e-9), "width preserved");
            Assert.That(gamma.EndTime, Is.EqualTo(4300), "stopped at the next word's start");
            Assert.That(gamma.StartTime, Is.EqualTo(4300 - gammaWidth), "start moved with it");

            // And leftward into the line start.
            TypeBeatEditorOperations.MoveUnit(editorBeatmap, line, 0, -1000);
            gamma = line.Line.Units[0];
            Assert.That(gamma.EndTime - gamma.StartTime, Is.EqualTo(gammaWidth).Within(1e-9), "width preserved");
            Assert.That(gamma.StartTime, Is.EqualTo(line.Line.StartTime), "stopped at the line start");

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void EditUnitGroupMovesAllByUniformDeltaAndStopsTogether()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 1); // gamma [3000,4200], delta [4300,5500]; line [3000,6000]

            int[] idx = { 0, 1 };
            double[] os = { 3000, 4300 };
            double[] oe = { 4200, 5500 };

            // Small move: both words shift by exactly +200 (widths and the gap between them preserved).
            TypeBeatEditorOperations.EditUnitGroup(editorBeatmap, line, idx, os, oe, 200, TypeBeatEditorOperations.UnitGroupEdit.Move);
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(3200));
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(4400));
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(4500));
            Assert.That(line.Line.Units[1].EndTime, Is.EqualTo(5700));

            // Excessive move: the whole group stops together when the trailing word hits the line
            // end (6000). Delta is clamped ONCE to +500 and applied uniformly — nothing squashes.
            TypeBeatEditorOperations.EditUnitGroup(editorBeatmap, line, idx, os, oe, 2000, TypeBeatEditorOperations.UnitGroupEdit.Move);
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(3500));
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(4700));
            Assert.That(line.Line.Units[1].EndTime, Is.EqualTo(6000), "trailing word stopped at line end");
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(4800), "leading gap preserved (uniform +500)");

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void EditUnitGroupResizeEndMovesEdgesByUniformDelta()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 1); // gamma [3000,4200], delta [4300,5500]

            int[] idx = { 0, 1 };
            double[] os = { 3000, 4300 };
            double[] oe = { 4200, 5500 };

            // Drag the end edge far right. gamma's end is blocked by delta's (fixed) start at 4300,
            // so the shared uniform delta is clamped to +100 and BOTH ends move by exactly that —
            // the amount is the (clamped) mouse distance, not each edge clipped to the cursor.
            TypeBeatEditorOperations.EditUnitGroup(editorBeatmap, line, idx, os, oe, 2000, TypeBeatEditorOperations.UnitGroupEdit.ResizeEnd);

            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(3000), "starts untouched by ResizeEnd");
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(4300), "gamma end stopped at delta start (+100)");
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(4300), "starts untouched by ResizeEnd");
            Assert.That(line.Line.Units[1].EndTime, Is.EqualTo(5600), "delta end moved by the same +100");

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void TextEditSameTokenCountKeepsWordTiming()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 0);

            Assert.That(TypeBeatEditorOperations.SetLineText(editorBeatmap, line, "Yeaaaah, Betaaa!"), Is.True);

            // Normalized (punctuation stripped, lowered diacritics; case preserved by Normalize rules)
            Assert.That(line.Line.RawText.Split(' ').Length, Is.EqualTo(2));
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(1000)); // timings preserved
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(1800));
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(1900));
            Assert.That(line.Line.Units[0].Text, Is.EqualTo(line.Line.RawText.Split(' ')[0]));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void TextEditDifferentTokenCountRedistributes()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 0);

            Assert.That(TypeBeatEditorOperations.SetLineText(editorBeatmap, line, "one two three four"), Is.True);

            Assert.That(line.Line.Units.Count, Is.EqualTo(4)); // token invariant maintained
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(1000));
            Assert.That(line.Line.Units[3].EndTime, Is.EqualTo(2800).Within(1e-6)); // spans the sung window
            Assert.That(line.Line.Units.Select(u => u.StartTime), Is.Ordered);

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void TextEditNormalizingEmptyIsRejected()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 0);

            Assert.That(TypeBeatEditorOperations.SetLineText(editorBeatmap, line, "(backing only)"), Is.False);
            Assert.That(line.Line.RawText, Is.EqualTo("alpha beta")); // unchanged
        }

        [Test]
        public void SplitThenMergeRestoresLineCount()
        {
            var editorBeatmap = createBeatmap();

            TypeBeatEditorOperations.SplitLine(editorBeatmap, lineAt(editorBeatmap, 1), 1);

            Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap), Has.Count.EqualTo(4));
            Assert.That(lineAt(editorBeatmap, 1).Line.RawText, Is.EqualTo("gamma"));
            Assert.That(lineAt(editorBeatmap, 2).Line.RawText, Is.EqualTo("delta"));
            Assert.That(lineAt(editorBeatmap, 1).Line.EndTime, Is.EqualTo(lineAt(editorBeatmap, 2).Line.StartTime)); // boundary invariant
            Assert.That(lineAt(editorBeatmap, 2).Line.StartTime, Is.EqualTo(4300)); // delta's word start
            Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap).Select(o => o.LineIndex), Is.EqualTo(new[] { 0, 1, 2, 3 })); // renumbered

            assertReloadStable(editorBeatmap);

            TypeBeatEditorOperations.MergeWithNext(editorBeatmap, lineAt(editorBeatmap, 1));

            Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap), Has.Count.EqualTo(3));
            Assert.That(lineAt(editorBeatmap, 1).Line.RawText, Is.EqualTo("gamma delta"));
            Assert.That(lineAt(editorBeatmap, 1).Line.EndTime, Is.EqualTo(6000));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void AddAndDeleteLineMaintainBoundaries()
        {
            var editorBeatmap = createBeatmap();

            // Insert into the gap-free middle: previous line's window shrinks to the new start.
            var added = TypeBeatEditorOperations.AddLine(editorBeatmap, 4500, "inserted words");

            Assert.That(added, Is.Not.Null);
            Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap), Has.Count.EqualTo(4));
            Assert.That(lineAt(editorBeatmap, 1).Line.EndTime, Is.EqualTo(4500));
            Assert.That(added!.Line.EndTime, Is.EqualTo(6000)); // runs to the next line's start
            Assert.That(added.Line.Units.Count, Is.EqualTo(2));

            assertReloadStable(editorBeatmap);

            TypeBeatEditorOperations.DeleteLine(editorBeatmap, added);

            Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap), Has.Count.EqualTo(3));
            Assert.That(lineAt(editorBeatmap, 1).Line.EndTime, Is.EqualTo(6000)); // window restored

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void DeleteLastLineCapsInheritedWindow()
        {
            var editorBeatmap = createBeatmap();

            TypeBeatEditorOperations.DeleteLine(editorBeatmap, lineAt(editorBeatmap, 2));

            // Line 1 (singEnd 5500) inherits the freed span; 8000 <= singEnd + tail = 8500, so kept as-is.
            Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap), Has.Count.EqualTo(2));
            Assert.That(lineAt(editorBeatmap, 1).Line.EndTime, Is.EqualTo(8000));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void DeleteLastLineWithLongTailClampsToSingEndPlusTail()
        {
            // A last line far past its predecessor's vocals: the freed span exceeds what the
            // format can represent (reload derives last EndTime = min(song_end, singEnd + tail)).
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Test";
            beatmap.Metadata.AudioFile = "audio.mp3";

            addLine(beatmap, 0, "alpha", 1000, 5000, 4000, (1000, 4000));
            addLine(beatmap, 1, "omega", 5000, 30000, 29000, (5000, 29000));

            var editorBeatmap = new EditorBeatmap(beatmap);

            TypeBeatEditorOperations.DeleteLine(editorBeatmap, lineAt(editorBeatmap, 1));

            // Inherited 30000 clamps to singEnd(4000) + tail(3000) = 7000 — exactly what reload derives.
            Assert.That(lineAt(editorBeatmap, 0).Line.EndTime, Is.EqualTo(4000 + TypeBeatEditorOperations.LAST_LINE_TAIL_MS));

            assertReloadStable(editorBeatmap);
        }

        /// <summary>Line-granularity fixture: no persisted word data — units are loader interpolations.</summary>
        private static EditorBeatmap createLineGranularityBeatmap()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Line";
            beatmap.Metadata.AudioFile = "audio.mp3";

            // Build exactly what the loader would produce (interpolated units) by round-tripping
            // through TryParse of a words-less timing json.
            const string json = "{\"version\":2,\"song_end_ms\":9000,\"lines\":["
                                + "{\"text\":\"hello world\",\"start_ms\":1000,\"end_ms\":2800},"
                                + "{\"text\":\"second line here\",\"start_ms\":3000,\"end_ms\":5500},"
                                + "{\"text\":\"omega\",\"start_ms\":6000,\"end_ms\":7000}]}";

            Assert.That(TimingJsonLoader.TryParse(json, out var lines), Is.True);

            for (int i = 0; i < lines.Count; i++)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
                    Granularity = TimingGranularity.Line,
                });
            }

            return new EditorBeatmap(beatmap);
        }

        [Test]
        public void LineGranularityOpsReDeriveUnitsAndStayReloadStable()
        {
            var editorBeatmap = createLineGranularityBeatmap();

            // Boundary drag: reload re-interpolates units — the editor must too.
            TypeBeatEditorOperations.SetLineStart(editorBeatmap, lineAt(editorBeatmap, 1), 3400);
            assertReloadStable(editorBeatmap);

            // Sing end move changes the interpolation domain.
            TypeBeatEditorOperations.SetSingEnd(editorBeatmap, lineAt(editorBeatmap, 1), 5000);
            assertReloadStable(editorBeatmap);

            // Text edit with SAME token count on a Line map must still re-interpolate
            // (interpolation weights are text-dependent).
            Assert.That(TypeBeatEditorOperations.SetLineText(editorBeatmap, lineAt(editorBeatmap, 0), "yeaaaaaaaah world"), Is.True);
            assertReloadStable(editorBeatmap);

            TypeBeatEditorOperations.SplitLine(editorBeatmap, lineAt(editorBeatmap, 1), 1);
            assertReloadStable(editorBeatmap);

            TypeBeatEditorOperations.MergeWithNext(editorBeatmap, lineAt(editorBeatmap, 1));
            assertReloadStable(editorBeatmap);

            // None of the above authored word timing — the map must still be Line granularity.
            Assert.That(lineAt(editorBeatmap, 0).Granularity, Is.EqualTo(TimingGranularity.Line));
        }

        [Test]
        public void HandTimingAWordPromotesLineMapToWordGranularity()
        {
            var editorBeatmap = createLineGranularityBeatmap();
            var line = lineAt(editorBeatmap, 0);

            TypeBeatEditorOperations.SetUnitTiming(editorBeatmap, line, 0, 1100, 1600);

            // Without the promotion the encoder would omit words[] and the edit would vanish on save.
            Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap).Select(o => o.Granularity),
                Is.All.EqualTo(TimingGranularity.Word));
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(1100));
            Assert.That(line.Line.Units[0].Source, Is.EqualTo(TimingSource.Explicit));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void StampUnitStartSetsStartAndClearsEstimated()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 1);

            TypeBeatEditorOperations.StampUnitStart(editorBeatmap, line, 1, 4500);

            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(4500));
            Assert.That(line.Line.Units[1].EndTime, Is.EqualTo(5500)); // kept
            Assert.That(line.Line.Estimated, Is.False);

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void SubMinSpanWindowsNeverThrow()
        {
            // A map whose lines and words are packed tighter than MIN_SPAN_MS — legal aligner
            // output (BuildLines enforces only non-decreasing order). Every retime op must be a
            // safe no-op on a degenerate window, not a Math.Clamp(min>max) crash.
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Tight";
            beatmap.Metadata.AudioFile = "audio.mp3";

            // Line 0 [1000..1020] (20ms, sub-MIN_SPAN), line 1 [1020..2000], last line to 4000.
            addLine(beatmap, 0, "a", 1000, 1020, 1015, (1000, 1015));
            // Middle unit boxed into a <30ms window: alpha ends 1795, gamma starts 1815.
            addLine(beatmap, 1, "alpha beta gamma", 1020, 2000, 1900, (1020, 1795), (1795, 1815), (1815, 1900));
            addLine(beatmap, 2, "omega", 2000, 4000, 3000, (2000, 3000));

            var editorBeatmap = new EditorBeatmap(beatmap);

            Assert.DoesNotThrow(() =>
            {
                // Boundary drag onto a sub-span line.
                TypeBeatEditorOperations.SetLineStart(editorBeatmap, lineAt(editorBeatmap, 1), 1010);
                // Sung-end on a sub-span (non-last) line.
                TypeBeatEditorOperations.SetSingEnd(editorBeatmap, lineAt(editorBeatmap, 0), 1005);
                // Retime the boxed-in middle unit (window < 30ms) — the reported crash path.
                TypeBeatEditorOperations.SetUnitTiming(editorBeatmap, lineAt(editorBeatmap, 1), 1, 1700, 1900);
                // Tap-stamp the same boxed-in unit.
                TypeBeatEditorOperations.StampUnitStart(editorBeatmap, lineAt(editorBeatmap, 1), 1, 1805);
            });

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void AddLineNearExistingStartIsRejected()
        {
            var editorBeatmap = createBeatmap(); // lines at 1000, 3000, 6000

            // Within MIN_SPAN_MS before an existing start -> rejected (no overlap).
            Assert.That(TypeBeatEditorOperations.AddLine(editorBeatmap, 5990, "x"), Is.Null);
            // Within MIN_SPAN_MS after an existing start -> rejected.
            Assert.That(TypeBeatEditorOperations.AddLine(editorBeatmap, 3010, "x"), Is.Null);
            // Exactly on an existing start -> rejected (no duplicate start_ms / zero-width line).
            Assert.That(TypeBeatEditorOperations.AddLine(editorBeatmap, 3000, "x"), Is.Null);

            Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap), Has.Count.EqualTo(3));

            // A genuinely free slot IS accepted, keeps the boundary invariant, and reloads stable.
            var added = TypeBeatEditorOperations.AddLine(editorBeatmap, 4500, "inserted");
            Assert.That(added, Is.Not.Null);
            Assert.That(lineAt(editorBeatmap, 1).Line.EndTime, Is.EqualTo(4500));
            Assert.That(added!.Line.EndTime, Is.EqualTo(6000)); // true next line's start
            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void EverySequencedEditRemainsReloadStable()
        {
            var editorBeatmap = createBeatmap();

            TypeBeatEditorOperations.SetLineStart(editorBeatmap, lineAt(editorBeatmap, 1), 3600);
            TypeBeatEditorOperations.SetUnitTiming(editorBeatmap, lineAt(editorBeatmap, 1), 0, 3700, 4100);
            TypeBeatEditorOperations.SetLineText(editorBeatmap, lineAt(editorBeatmap, 0), "yeaaaaaaaah beta");
            TypeBeatEditorOperations.SplitLine(editorBeatmap, lineAt(editorBeatmap, 1), 1);
            TypeBeatEditorOperations.SetSingEnd(editorBeatmap, lineAt(editorBeatmap, 3), 6800);
            TypeBeatEditorOperations.ShiftAllTimes(editorBeatmap, 120);
            TypeBeatEditorOperations.MergeWithNext(editorBeatmap, lineAt(editorBeatmap, 1));

            assertReloadStable(editorBeatmap);
        }
    }
}
