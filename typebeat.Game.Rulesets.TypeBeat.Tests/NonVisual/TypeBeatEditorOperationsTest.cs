// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
    /// RELOAD-STABLE: encoding the edited beatmap and decoding it back reproduces exactly the
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

        /// <summary>The map exactly as it would be SAVED.</summary>
        private static string encode(EditorBeatmap editorBeatmap)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(editorBeatmap, sw);

            return sb.ToString();
        }

        /// <summary>Encode → decode → compare every persisted field against the in-memory state.</summary>
        private static void assertReloadStable(EditorBeatmap editorBeatmap)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(encode(editorBeatmap)));
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

            // Try to drag line 1's start before line 0's start; clamps to min span after it.
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

            // 8000 stays within [6050, 6050 + tail]; no clamp needed, and reload agrees.
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
            // end (6000). Delta is clamped ONCE to +500 and applied uniformly; nothing squashes.
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
            // so the shared uniform delta is clamped to +100 and BOTH ends move by exactly that;
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

            // Normalized: diacritics folded, case AND supported punctuation preserved (the stored
            // line is the author's form; the marks are derived away from the typed stream, not here).
            Assert.That(line.Line.RawText, Is.EqualTo("Yeaaaah, Betaaa!"));
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
        public void TextEditRoundTripsPunctuationThroughSaveAndReload()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 0);

            // A hyphenated word is ONE authored token (so the line keeps its two-word timing) but
            // TWO words in the stream the player types without the Literate mod.
            Assert.That(TypeBeatEditorOperations.SetLineText(editorBeatmap, line, "The bad-cat, sat!"), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("The bad-cat, sat!"));
            Assert.That(line.Line.Units.Count, Is.EqualTo(3));
            Assert.That(Typeability.ToDefaultStream(line.Line.RawText), Is.EqualTo("the bad cat sat"));

            // Encode + decode must give the authored form straight back, marks and case included.
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

            // Inherited 30000 clamps to singEnd(4000) + tail(3000) = 7000, exactly what reload derives.
            Assert.That(lineAt(editorBeatmap, 0).Line.EndTime, Is.EqualTo(4000 + TypeBeatEditorOperations.LAST_LINE_TAIL_MS));

            assertReloadStable(editorBeatmap);
        }

        /// <summary>Line-granularity fixture: no persisted word data; units are loader interpolations.</summary>
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

            // Boundary drag: reload re-interpolates units; the editor must too.
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

            // None of the above authored word timing; the map must still be Line granularity.
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
        public void TapCommitDerivesEveryLineEndFromItsWidenedLastWord()
        {
            var editorBeatmap = createBeatmap();
            var lines = TypeBeatEditorOperations.OrderedLines(editorBeatmap).Select(o => o.Line).ToList();
            var queue = TapTimingBuilder.BuildQueue(lines);

            // Five words, five taps: alpha beta / gamma delta / omega.
            var built = TapTimingBuilder.Build(lines, queue, new[] { 1000d, 1500, 2000, 2500, 2900 });
            TypeBeatEditorOperations.ReplaceLines(editorBeatmap, built, TypeBeatEditorOperations.InferGranularity(built));

            var after = TypeBeatEditorOperations.OrderedLines(editorBeatmap);

            // "alpha" (5 chars) was tapped 500ms wide, so 100ms a character, and the line's last
            // word "beta" (4 chars) is 400ms rather than a sliver or a whole 500ms.
            Assert.That(after[0].Line.Units[1].StartTime, Is.EqualTo(1500));
            Assert.That(after[0].Line.Units[1].EndTime, Is.EqualTo(1900));

            // Backlog 246: end_ms is the last word's end. The tap pass writes whole lines through
            // ReplaceLines, so the derivation has to hold by construction here, not by the
            // syncSingEndToLastUnit guard the single-unit ops go through.
            foreach (var o in after)
                Assert.That(o.Line.SingEndTime, Is.EqualTo(o.Line.Units[^1].EndTime).Within(1e-6), "end_ms follows the last word");

            // And the last line's typeable window is re-derived from that sung end, the way reload
            // computes it, so the map reopens showing what the mapper just tapped.
            Assert.That(after[^1].Line.EndTime,
                Is.EqualTo(after[^1].Line.SingEndTime + TypeBeatEditorOperations.LAST_LINE_TAIL_MS).Within(1e-6));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void SubMinSpanWindowsNeverThrow()
        {
            // A map whose lines and words are packed tighter than MIN_SPAN_MS: legal aligner
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
                // Retime the boxed-in middle unit (window < 30ms); the reported crash path.
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

        #region Word insertion / removal

        [Test]
        public void AddWordTakesTheGapAfterTheSelectedWordAndRemoveGivesItBack()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 0); // "alpha beta": alpha [1000,1800], beta [1900,2800]

            Assert.That(TypeBeatEditorOperations.AddWord(editorBeatmap, line, 0), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("alpha word beta"));
            Assert.That(line.Line.Units.Count, Is.EqualTo(3));
            Assert.That(line.Line.Units.Select(u => u.Text), Is.EqualTo(new[] { "alpha", "word", "beta" }));

            // The free gap between alpha and beta, so neither neighbour moved.
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(1800));
            Assert.That(line.Line.Units[1].EndTime, Is.EqualTo(1900));
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(1800));
            Assert.That(line.Line.Units[2].StartTime, Is.EqualTo(1900));
            Assert.That(line.Line.Units[1].Source, Is.EqualTo(TimingSource.Explicit));
            Assert.That(line.Line.Units.Select(u => u.StartTime), Is.Ordered);

            assertReloadStable(editorBeatmap);

            // Removing it again restores the line exactly (the word only ever held free space).
            Assert.That(TypeBeatEditorOperations.RemoveWord(editorBeatmap, line, 1), Is.True);
            Assert.That(line.Line.RawText, Is.EqualTo("alpha beta"));
            Assert.That(line.Line.Units.Select(u => u.StartTime), Is.EqualTo(new[] { 1000d, 1900d }));
            Assert.That(line.Line.Units.Select(u => u.EndTime), Is.EqualTo(new[] { 1800d, 2800d }));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void AddWordAppendsAtTheLineEndWhenNothingIsSelected()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 1); // "gamma delta": delta [4300,5500], line ends 6000

            // -1 == no word focused: the word lands at the end of the line.
            Assert.That(TypeBeatEditorOperations.AddWord(editorBeatmap, line, -1), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("gamma delta word"));
            Assert.That(line.Line.Units[2].StartTime, Is.EqualTo(5500));
            Assert.That(line.Line.Units[2].EndTime, Is.EqualTo(6000)); // the free tail, 500ms < delta's 1200
            Assert.That(line.Line.Units.Select(u => u.StartTime), Is.Ordered);

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void AddWordCapsAnAppendAtTheAnchorsOwnDuration()
        {
            // A short last word with a long typeable tail: taking the WHOLE gap would hand the new
            // word a multi-second span, so it is capped at the anchor's own duration.
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Tail";
            beatmap.Metadata.AudioFile = "audio.mp3";

            addLine(beatmap, 0, "alpha", 1000, 5000, 2000, (1000, 1500));

            var editorBeatmap = new EditorBeatmap(beatmap);
            var line = lineAt(editorBeatmap, 0);

            Assert.That(TypeBeatEditorOperations.AddWord(editorBeatmap, line, -1), Is.True);
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(1500));
            Assert.That(line.Line.Units[1].EndTime, Is.EqualTo(2000)); // 500ms == alpha's duration, not the 3500ms tail

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void AddWordBisectsTheAnchorWhenWordsArePackedEdgeToEdge()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Packed";
            beatmap.Metadata.AudioFile = "audio.mp3";

            addLine(beatmap, 0, "alpha beta", 1000, 3000, 2800, (1000, 1900), (1900, 2800));

            var editorBeatmap = new EditorBeatmap(beatmap);
            var line = lineAt(editorBeatmap, 0);

            // Give alpha a subdivision in its SECOND half, which the bisection carves away.
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(editorBeatmap, line, 0), Is.Not.Null);
            TypeBeatEditorOperations.SetSyllableBoundary(editorBeatmap, line, 0, 0, 1700);
            Assert.That(line.Granularity, Is.EqualTo(TimingGranularity.Syllable));

            Assert.That(TypeBeatEditorOperations.AddWord(editorBeatmap, line, 0), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("alpha word beta"));
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(1450)); // anchor halved
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(1450));
            Assert.That(line.Line.Units[1].EndTime, Is.EqualTo(1900));
            Assert.That(line.Line.Units[2].StartTime, Is.EqualTo(1900)); // beta untouched
            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.Empty, "the boundary fell outside the shortened anchor");
            Assert.That(line.Granularity, Is.EqualTo(TimingGranularity.Word), "no subdivision left anywhere");

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void AddWordRefusesWhenThereIsNoRoom()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Tight";
            beatmap.Metadata.AudioFile = "audio.mp3";

            // Words packed edge to edge AND too short to halve into two MIN_SPAN words.
            addLine(beatmap, 0, "a b", 1000, 1100, 1080, (1000, 1040), (1040, 1080));

            var editorBeatmap = new EditorBeatmap(beatmap);
            var line = lineAt(editorBeatmap, 0);

            Assert.That(TypeBeatEditorOperations.AddWord(editorBeatmap, line, 0), Is.False, "no gap and an unsplittable anchor");
            Assert.That(TypeBeatEditorOperations.AddWord(editorBeatmap, line, -1), Is.False, "20ms of tail is under MIN_SPAN_MS");
            Assert.That(line.Line.RawText, Is.EqualTo("a b"));
            Assert.That(line.Line.Units.Count, Is.EqualTo(2));
        }

        [Test]
        public void AddWordRejectsTextThatIsNotASingleToken()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 0);

            Assert.That(TypeBeatEditorOperations.AddWord(editorBeatmap, line, 0, "(backing only)"), Is.False);
            Assert.That(TypeBeatEditorOperations.AddWord(editorBeatmap, line, 0, "two words"), Is.False);
            Assert.That(line.Line.RawText, Is.EqualTo("alpha beta"));
        }

        [Test]
        public void RemoveWordDropsItsTimingAndSubdivisionsAndLeavesAGap()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 1); // "gamma delta": gamma [3000,4200], delta [4300,5500]

            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(editorBeatmap, line, 1), Is.Not.Null);
            Assert.That(line.Granularity, Is.EqualTo(TimingGranularity.Syllable));

            Assert.That(TypeBeatEditorOperations.RemoveWord(editorBeatmap, line, 1), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("gamma"));
            Assert.That(line.Line.Units.Count, Is.EqualTo(1));
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(3000)); // survivor untouched
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(4200));
            // The TYPEABLE window does not shrink (the freed span stays a gap the player may still
            // be typing in), but the SUNG end is auto-derived from the last word, and removing the
            // tail word made "gamma" the last word: the vocal now stops where it does.
            Assert.That(line.Line.SingEndTime, Is.EqualTo(4200));
            Assert.That(line.Line.EndTime, Is.EqualTo(6000));
            Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap).Select(o => o.Granularity),
                Is.All.EqualTo(TimingGranularity.Word), "the map's last subdivision went with the word");

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void RemoveFirstWordKeepsTheLineBoundary()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 0);

            Assert.That(TypeBeatEditorOperations.RemoveWord(editorBeatmap, line, 0), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("beta"));
            Assert.That(line.Line.StartTime, Is.EqualTo(1000), "the line boundary is a separate degree of freedom");
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(1900));
            Assert.That(lineAt(editorBeatmap, 1).Line.StartTime, Is.EqualTo(3000)); // neighbours untouched

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void RemoveWordRefusesTheOnlyWordOfALine()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 2); // "omega", a single word

            // An empty line cannot exist in the format; the line delete is the way out.
            Assert.That(TypeBeatEditorOperations.RemoveWord(editorBeatmap, line, 0), Is.False);
            Assert.That(line.Line.RawText, Is.EqualTo("omega"));
            Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap), Has.Count.EqualTo(3));

            // Out of range is a no-op too.
            Assert.That(TypeBeatEditorOperations.RemoveWord(editorBeatmap, lineAt(editorBeatmap, 0), 5), Is.False);
            Assert.That(TypeBeatEditorOperations.RemoveWord(editorBeatmap, lineAt(editorBeatmap, 0), -1), Is.False);
        }

        [Test]
        public void RemoveWordNeverDemotesAnAuthoredMapBelowWordGranularity()
        {
            // A Word map whose ONLY hand-timed unit is the one being removed. Demoting to Line
            // would make the encoder omit words[] and silently discard the rest of the timing.
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Mixed";
            beatmap.Metadata.AudioFile = "audio.mp3";

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Line = new LyricLine
                {
                    RawText = "alpha beta",
                    StartTime = 1000,
                    EndTime = 4000,
                    SingEndTime = 2800,
                    Units = new[]
                    {
                        new TimedUnit { Text = "alpha", StartTime = 1000, EndTime = 1800, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "beta", StartTime = 1900, EndTime = 2800, Source = TimingSource.Interpolated },
                    },
                },
                Granularity = TimingGranularity.Word,
            });

            var editorBeatmap = new EditorBeatmap(beatmap);
            var line = lineAt(editorBeatmap, 0);

            Assert.That(TypeBeatEditorOperations.RemoveWord(editorBeatmap, line, 0), Is.True);

            Assert.That(line.Granularity, Is.EqualTo(TimingGranularity.Word));
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(1900), "the surviving word keeps its authored span");

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void WordOpsOnALineGranularityMapReInterpolateAndStayReloadStable()
        {
            var editorBeatmap = createLineGranularityBeatmap();
            var line = lineAt(editorBeatmap, 0); // "hello world"

            Assert.That(TypeBeatEditorOperations.AddWord(editorBeatmap, line, 0), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("hello word world"));
            Assert.That(line.Line.Units.Count, Is.EqualTo(3));
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(line.Line.StartTime));
            Assert.That(line.Line.Units[^1].EndTime, Is.EqualTo(line.Line.SingEndTime).Within(1e-6));
            Assert.That(line.Line.Units.Select(u => u.StartTime), Is.Ordered);
            Assert.That(line.Line.Units.Select(u => u.Source), Is.All.EqualTo(TimingSource.Interpolated));
            Assert.That(line.Granularity, Is.EqualTo(TimingGranularity.Line), "no word timing was authored");

            assertReloadStable(editorBeatmap);

            Assert.That(TypeBeatEditorOperations.RemoveWord(editorBeatmap, line, 1), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("hello world"));
            Assert.That(line.Line.Units.Count, Is.EqualTo(2));
            Assert.That(line.Granularity, Is.EqualTo(TimingGranularity.Line));

            assertReloadStable(editorBeatmap);
        }

        #endregion

        #region Shared word boundary (the Shift+drag gesture on a touching edge)

        /// <summary>
        /// Same three lines as <see cref="createBeatmap"/>, except line 0's two words TOUCH
        /// (alpha [1000, 2000], beta [2000, 2800]) and start out Interpolated, so "the drag made
        /// them Explicit" is not vacuous. Line 1 keeps its real gap (gamma ends 4200, delta starts
        /// 4300) and is the non-touching case; line 2 is unchanged so reload derives the same
        /// tail-capped window.
        /// </summary>
        private static EditorBeatmap createTouchingBeatmap(double[]? alphaSyllables = null, double[]? betaSyllables = null)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Test";
            beatmap.Metadata.AudioFile = "audio.mp3";

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Line = new LyricLine
                {
                    RawText = "alpha beta",
                    StartTime = 1000,
                    EndTime = 3000,
                    SingEndTime = 2800,
                    Units = new[]
                    {
                        new TimedUnit
                        {
                            Text = "alpha",
                            StartTime = 1000,
                            EndTime = 2000,
                            Source = TimingSource.Interpolated,
                            SyllableBoundaries = alphaSyllables ?? System.Array.Empty<double>(),
                        },
                        new TimedUnit
                        {
                            Text = "beta",
                            StartTime = 2000,
                            EndTime = 2800,
                            Source = TimingSource.Interpolated,
                            SyllableBoundaries = betaSyllables ?? System.Array.Empty<double>(),
                        },
                    },
                },
                Granularity = alphaSyllables != null || betaSyllables != null ? TimingGranularity.Syllable : TimingGranularity.Word,
            });

            addLine(beatmap, 1, "gamma delta", 3000, 6000, 5500, (3000, 4200), (4300, 5500));
            addLine(beatmap, 2, "omega", 6000, 8000, 7000, (6000, 7000));

            return new EditorBeatmap(beatmap);
        }

        [Test]
        public void SharedUnitBoundaryMovesBothWordsAndBecomesExplicit()
        {
            var editorBeatmap = createTouchingBeatmap();
            var line = lineAt(editorBeatmap, 0);

            // The boundary alpha and beta share (2000) is dragged right to 2300: alpha grows and
            // beta shrinks by the same amount, so they stay touching with no gap opened.
            TypeBeatEditorOperations.SetSharedUnitBoundary(editorBeatmap, line, 0, 2300);

            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(1000), "the far edges never move");
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(2300));
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(2300));
            Assert.That(line.Line.Units[1].EndTime, Is.EqualTo(2800), "the far edges never move");
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(line.Line.Units[1].StartTime), "still touching");

            Assert.That(line.Line.Units[0].Source, Is.EqualTo(TimingSource.Explicit));
            Assert.That(line.Line.Units[1].Source, Is.EqualTo(TimingSource.Explicit));
            Assert.That(line.Line.Estimated, Is.False);

            // And leftward, from the same shared edge.
            TypeBeatEditorOperations.SetSharedUnitBoundary(editorBeatmap, line, 0, 1400);
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(1400));
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(1400));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void SharedUnitBoundaryIsOneUndoStep()
        {
            var editorBeatmap = createTouchingBeatmap();
            var line = lineAt(editorBeatmap, 0);

            int changes = 0;
            editorBeatmap.TransactionEnded += () => changes++;

            TypeBeatEditorOperations.SetSharedUnitBoundary(editorBeatmap, line, 0, 2300);

            // Both words are written inside ONE outer transaction: the nested applyUnit calls must
            // not each surface as their own undo step.
            Assert.That(changes, Is.EqualTo(1), "both sides landed in a single transaction");
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(2300));
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(2300));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void SharedUnitBoundaryClampsToBothMinSpans()
        {
            var editorBeatmap = createTouchingBeatmap();
            var line = lineAt(editorBeatmap, 0);

            // Dragged far left: the boundary stops MIN_SPAN after alpha's start (1030), so alpha
            // keeps a legal span rather than collapsing.
            TypeBeatEditorOperations.SetSharedUnitBoundary(editorBeatmap, line, 0, -5000);
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(1000 + TypeBeatEditorOperations.MIN_SPAN_MS));
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(1000 + TypeBeatEditorOperations.MIN_SPAN_MS));

            // Dragged far right: MIN_SPAN before beta's end (2770).
            TypeBeatEditorOperations.SetSharedUnitBoundary(editorBeatmap, line, 0, 99999);
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(2800 - TypeBeatEditorOperations.MIN_SPAN_MS));
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(2800 - TypeBeatEditorOperations.MIN_SPAN_MS));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void SharedUnitBoundaryIgnoresWordsThatDoNotTouch()
        {
            var editorBeatmap = createTouchingBeatmap();
            var line = lineAt(editorBeatmap, 1); // gamma [3000, 4200], delta [4300, 5500]: a real gap.
            var before = line.Line;

            TypeBeatEditorOperations.SetSharedUnitBoundary(editorBeatmap, line, 0, 4250);

            // A gap is legal data (a breath, an instrumental beat); the gesture must not close it.
            Assert.That(line.Line, Is.SameAs(before));
        }

        [Test]
        public void SharedUnitBoundaryIgnoresOutOfRangeIndices()
        {
            var editorBeatmap = createTouchingBeatmap();
            var line = lineAt(editorBeatmap, 0);
            var before = line.Line;

            // The LAST unit has no unit to its right, so it owns no shared boundary.
            TypeBeatEditorOperations.SetSharedUnitBoundary(editorBeatmap, line, 1, 2300);
            Assert.That(line.Line, Is.SameAs(before));

            TypeBeatEditorOperations.SetSharedUnitBoundary(editorBeatmap, line, -1, 2300);
            Assert.That(line.Line, Is.SameAs(before));

            TypeBeatEditorOperations.SetSharedUnitBoundary(editorBeatmap, line, 7, 2300);
            Assert.That(line.Line, Is.SameAs(before));
        }

        [Test]
        public void SharedUnitBoundaryIgnoresAPairTooNarrowToSplit()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Test";
            beatmap.Metadata.AudioFile = "audio.mp3";

            // Touching, but the pair spans 50ms: less than two MIN_SPAN_MS words fit, so there is
            // no boundary position that leaves both legal.
            addLine(beatmap, 0, "alpha beta", 1000, 3000, 2800, (1000, 1020), (1020, 1050));

            var editorBeatmap = new EditorBeatmap(beatmap);
            var line = lineAt(editorBeatmap, 0);
            var before = line.Line;

            TypeBeatEditorOperations.SetSharedUnitBoundary(editorBeatmap, line, 0, 1035);

            Assert.That(line.Line, Is.SameAs(before));
        }

        [Test]
        public void SharedUnitBoundaryClampsSyllablesIntoBothNewSpans()
        {
            // alpha [1000, 2000] subdivided at 1500; beta [2000, 2800] subdivided at 2400.
            var editorBeatmap = createTouchingBeatmap(new double[] { 1500 }, new double[] { 2400 });
            var line = lineAt(editorBeatmap, 0);

            // Boundary right to 2600: alpha grows over beta's old subdivision, which is now outside
            // beta's [2600, 2800] span and is dropped, while alpha's own 1500 stays inside.
            TypeBeatEditorOperations.SetSharedUnitBoundary(editorBeatmap, line, 0, 2600);

            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1500d }), "still inside alpha");
            Assert.That(line.Line.Units[1].SyllableBoundaries, Is.Empty, "beta shrank past its subdivision");

            assertReloadStable(editorBeatmap);
        }

        #endregion

        [Test]
        public void EverySequencedEditRemainsReloadStable()
        {
            var editorBeatmap = createBeatmap();

            TypeBeatEditorOperations.SetLineStart(editorBeatmap, lineAt(editorBeatmap, 1), 3600);
            TypeBeatEditorOperations.SetUnitTiming(editorBeatmap, lineAt(editorBeatmap, 1), 0, 3700, 4100);
            TypeBeatEditorOperations.SetLineText(editorBeatmap, lineAt(editorBeatmap, 0), "yeaaaaaaaah beta");
            TypeBeatEditorOperations.SplitLine(editorBeatmap, lineAt(editorBeatmap, 1), 1);
            TypeBeatEditorOperations.SetSingEnd(editorBeatmap, lineAt(editorBeatmap, 3), 6800);
            TypeBeatEditorOperations.AddWord(editorBeatmap, lineAt(editorBeatmap, 0), 0);
            TypeBeatEditorOperations.RemoveWord(editorBeatmap, lineAt(editorBeatmap, 3), 0);
            TypeBeatEditorOperations.ShiftAllTimes(editorBeatmap, 120);
            TypeBeatEditorOperations.MergeWithNext(editorBeatmap, lineAt(editorBeatmap, 1));

            assertReloadStable(editorBeatmap);
        }

        #region Auto-derived sung end (backlog 246: the sung-end flag is gone)

        /// <summary>
        /// The same sheet as <see cref="createBeatmap"/>, except line 1 carries TRAILING VOCALS: its
        /// stored end_ms (5800) sits 300ms past its last word's end (5500). That is the shape an
        /// aligner estimate, or a flag dragged before backlog 246, leaves in real map data, and it is
        /// exactly the state the auto-derivation must not quietly overwrite.
        /// </summary>
        private static EditorBeatmap createTrailingVocalBeatmap()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Test";
            beatmap.Metadata.AudioFile = "audio.mp3";

            addLine(beatmap, 0, "alpha beta", 1000, 3000, 2800, (1000, 1800), (1900, 2800));
            addLine(beatmap, 1, "gamma delta", 3000, 6000, 5800, (3000, 4200), (4300, 5500));
            addLine(beatmap, 2, "omega", 6000, 8000, 7000, (6000, 7000));

            return new EditorBeatmap(beatmap);
        }

        /// <summary>
        /// (a) The sung end IS the last word's end: dragging that word's right edge, the gesture the
        /// removed blue flag used to own, carries end_ms with it, in both directions and through the
        /// rigid-move path too. A non-last line's typeable window does not move with it.
        /// </summary>
        [Test]
        public void RetimingTheLastWordCarriesTheLineSungEnd()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 1); // "gamma delta", delta [4300, 5500], end_ms 5500

            TypeBeatEditorOperations.SetUnitEnd(editorBeatmap, line, 1, 4300, 5800);

            Assert.That(line.Line.Units[1].EndTime, Is.EqualTo(5800));
            Assert.That(line.Line.SingEndTime, Is.EqualTo(5800), "end_ms follows the last word right");
            Assert.That(line.Line.EndTime, Is.EqualTo(6000), "a non-last line's typeable window is not a sung end");

            TypeBeatEditorOperations.SetUnitEnd(editorBeatmap, line, 1, 4300, 5000);
            Assert.That(line.Line.SingEndTime, Is.EqualTo(5000), "and left again");

            // Not only the edge drag: a rigid move of the last word moves its end too.
            TypeBeatEditorOperations.MoveUnit(editorBeatmap, line, 1, 4500);
            Assert.That(line.Line.Units[1].EndTime, Is.EqualTo(5200));
            Assert.That(line.Line.SingEndTime, Is.EqualTo(5200));

            assertReloadStable(editorBeatmap);
        }

        /// <summary>
        /// The LAST line's typeable window is reload-derived as min(song_end, singEnd + tail), so it
        /// is re-derived alongside an auto-synced sung end, exactly as the old flag drag did.
        /// </summary>
        [Test]
        public void RetimingTheLastLinesLastWordKeepsItsWindowReloadStable()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 2); // "omega" [6000, 7000], window to 8000

            // 8000 sits inside [6200, 6200 + 3000], so the window is kept exactly as it is.
            TypeBeatEditorOperations.SetUnitEnd(editorBeatmap, line, 0, 6000, 6200);
            Assert.That(line.Line.SingEndTime, Is.EqualTo(6200));
            Assert.That(line.Line.EndTime, Is.EqualTo(8000));

            assertReloadStable(editorBeatmap);

            // A last line whose window runs far past its vocals: pulling the last word back must cap
            // the window at singEnd + tail, or reload would derive a shorter one than the editor showed.
            var longTail = new Beatmap();
            longTail.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            longTail.Metadata.Artist = "Op";
            longTail.Metadata.Title = "Test";
            longTail.Metadata.AudioFile = "audio.mp3";
            addLine(longTail, 0, "alpha", 1000, 6000, 2800, (1000, 2800));
            addLine(longTail, 1, "omega tail", 6000, 30000, 20000, (6000, 12000), (13000, 20000));

            var withTail = new EditorBeatmap(longTail);
            var last = lineAt(withTail, 1);

            TypeBeatEditorOperations.SetUnitEnd(withTail, last, 1, 13000, 14000);

            Assert.That(last.Line.SingEndTime, Is.EqualTo(14000));
            Assert.That(last.Line.EndTime, Is.EqualTo(14000 + TypeBeatEditorOperations.LAST_LINE_TAIL_MS));

            assertReloadStable(withTail);
        }

        /// <summary>
        /// (b) The other half of the rule, and the important half. An edit that does NOT move the
        /// last word must leave a trailing-vocal end_ms exactly as the map stores it: end_ms is what
        /// InstrumentalGaps perceives an instrumental stretch from, and the server mirrors those
        /// rules for the play-time anti-cheat gate, so a silent rewrite would re-rank honest plays.
        /// </summary>
        [Test]
        public void AnEditThatMissesTheLastWordLeavesTrailingVocalEndMsAlone()
        {
            var editorBeatmap = createTrailingVocalBeatmap();
            var line = lineAt(editorBeatmap, 1); // end_ms 5800, last word ends 5500

            // (i) retime an INTERIOR word.
            TypeBeatEditorOperations.SetUnitEnd(editorBeatmap, line, 0, 3100, 4000);
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(4000));
            Assert.That(line.Line.SingEndTime, Is.EqualTo(5800), "an interior retime is not a sung-end decision");

            // (ii) retype the line (same word count, so every word keeps its timing).
            Assert.That(TypeBeatEditorOperations.SetLineText(editorBeatmap, line, "gamma deltas"), Is.True);
            Assert.That(line.Line.RawText, Is.EqualTo("gamma deltas"));
            Assert.That(line.Line.SingEndTime, Is.EqualTo(5800), "a text commit is not a sung-end decision");

            // (iii) drag the line boundary that starts it.
            TypeBeatEditorOperations.SetLineStart(editorBeatmap, line, 3050);
            Assert.That(line.Line.SingEndTime, Is.EqualTo(5800));

            // (iv) bake a global offset: everything moves RIGIDLY, the gap between the last word and
            // the sung end is preserved rather than collapsed.
            TypeBeatEditorOperations.ShiftAllTimes(editorBeatmap, 100);
            Assert.That(line.Line.Units[^1].EndTime, Is.EqualTo(5600));
            Assert.That(line.Line.SingEndTime, Is.EqualTo(5900));

            assertReloadStable(editorBeatmap);
        }

        /// <summary>
        /// (d) The consequence that matters on disk: a map whose last word was never re-timed saves
        /// its end_ms values verbatim, byte for byte, no matter what else the mapper edited.
        /// </summary>
        [Test]
        public void EndMsRoundTripsByteIdenticallyWhenTheLastWordWasNeverTouched()
        {
            var editorBeatmap = createTrailingVocalBeatmap();
            string[] before = lineEndMsFields(encode(editorBeatmap));

            Assert.That(before, Is.EqualTo(new[] { "\"end_ms\":2800", "\"end_ms\":5800", "\"end_ms\":7000" }));

            TypeBeatEditorOperations.SetUnitEnd(editorBeatmap, lineAt(editorBeatmap, 1), 0, 3100, 4000);
            TypeBeatEditorOperations.SetLineText(editorBeatmap, lineAt(editorBeatmap, 1), "gamma deltas");
            TypeBeatEditorOperations.SetLineStart(editorBeatmap, lineAt(editorBeatmap, 1), 3050);

            Assert.That(lineEndMsFields(encode(editorBeatmap)), Is.EqualTo(before), "no line's end_ms moved");
        }

        /// <summary>
        /// The LINE-level end_ms fields of an encoded map, in order. Word units carry an end_ms of
        /// their own, distinguishable because a word always writes "score" straight after it.
        /// </summary>
        private static string[] lineEndMsFields(string encoded)
            => Regex.Matches(encoded, "\"end_ms\":[0-9.eE+-]+(?:,\"score\")?")
                    .Select(m => m.Value)
                    .Where(v => !v.EndsWith(",\"score\"", StringComparison.Ordinal))
                    .ToArray();

        /// <summary>
        /// (c) The Line-granularity lever. Such a map has no word timing to author, so dragging its
        /// LAST block's end is the whole-line re-spread the flag used to perform, and it must land on
        /// exactly what SetSingEnd produced: same units, same end_ms, and NO promotion to Word.
        /// </summary>
        [Test]
        public void OnALineMapTheLastBlockEndDragIsTheOldSungEndRespread()
        {
            var viaBlock = createLineGranularityBeatmap();
            var viaFlag = createLineGranularityBeatmap();

            var dragged = lineAt(viaBlock, 1); // "second line here", three interpolated units to 5500
            int last = dragged.Line.Units.Count - 1;
            double interiorEndBefore = dragged.Line.Units[0].EndTime;

            TypeBeatEditorOperations.SetUnitEnd(viaBlock, dragged, last, dragged.Line.Units[last].StartTime, 5000);
            TypeBeatEditorOperations.SetSingEnd(viaFlag, lineAt(viaFlag, 1), 5000);

            var a = dragged.Line;
            var b = lineAt(viaFlag, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(a.SingEndTime, Is.EqualTo(b.SingEndTime));
                Assert.That(a.EndTime, Is.EqualTo(b.EndTime));
                Assert.That(a.Units.Select(u => (u.StartTime, u.EndTime)), Is.EqualTo(b.Units.Select(u => (u.StartTime, u.EndTime))));
                Assert.That(dragged.Granularity, Is.EqualTo(TimingGranularity.Line),
                    "the line lever does not author word timing, so the map is not promoted");
            });

            // The re-spread is real: EVERY unit moved, not just the dragged one.
            Assert.That(a.SingEndTime, Is.EqualTo(5000));
            Assert.That(a.Units[^1].EndTime, Is.EqualTo(5000));
            Assert.That(a.Units[0].EndTime, Is.Not.EqualTo(interiorEndBefore), "the interior words re-interpolated too");

            assertReloadStable(viaBlock);
        }

        /// <summary>
        /// The contrast that makes the branch above a branch: on a WORD map the same gesture is a
        /// plain word resize, so the interior words stay exactly where the mapper timed them.
        /// </summary>
        [Test]
        public void OnAWordMapTheLastBlockEndDragMovesOnlyThatWord()
        {
            var editorBeatmap = createBeatmap();
            var line = lineAt(editorBeatmap, 1);

            TypeBeatEditorOperations.SetUnitEnd(editorBeatmap, line, 1, 4300, 5800);

            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(3000), "interior word untouched");
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(4200));
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(4300));
            Assert.That(line.Line.SingEndTime, Is.EqualTo(5800));
        }

        #endregion
    }
}
