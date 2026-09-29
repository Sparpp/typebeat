// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the LICENCE file in the repository root.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The timing clipboard: copying a line's (or word run's) internal timing and pasting it
    /// elsewhere REBASED: the repeated-chorus workflow. Boundaries must never move, the TIMING
    /// paste keeps the target's text, overwrite applies regardless of word match, and every result
    /// stays monotonic inside the target window. Since backlog 339 the default LINE paste puts the
    /// copied words down too (<see cref="TypeBeatEditorOperations.PasteLine"/>), which the last
    /// section covers.
    /// </summary>
    [TestFixture]
    public class LyricTimingClipboardTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        /// <summary>
        /// Three word-timed lines: [1000..3000] singEnd 2800, [3000..6000] singEnd 5500,
        /// [6000..8000] singEnd 7000 (last).
        /// </summary>
        private static EditorBeatmap createBeatmap()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Clip";
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
                        Confidence = 1,
                    }).ToArray(),
                },
                Granularity = TimingGranularity.Word,
            });
        }

        private static TypeBeatHitObject line(EditorBeatmap editorBeatmap, int index)
            => TypeBeatEditorOperations.OrderedLines(editorBeatmap)[index];

        /// <summary>
        /// Gives one word of a line the SUB-WORD timing the fixture cannot express: subdivision
        /// boundaries, an authored char split, authored rests. Written straight onto the model, since
        /// the syllable ops have their own fixture. The line's Granularity is left at the fixture's
        /// Word: the encoder persists sub-word timing for anything above Line, and a paste is
        /// supposed to raise it to Syllable by itself.
        /// </summary>
        private static void subdivide(EditorBeatmap editorBeatmap, int lineIndex, int unitIndex,
                                      double[]? boundaries = null, int[]? splits = null, WordPause[]? rests = null)
        {
            var hitObject = line(editorBeatmap, lineIndex);
            var lyric = hitObject.Line;
            var units = lyric.Units.ToArray();
            var unit = units[unitIndex];

            units[unitIndex] = new TimedUnit
            {
                Text = unit.Text,
                StartTime = unit.StartTime,
                EndTime = unit.EndTime,
                Source = TimingSource.Explicit,
                Confidence = 1,
                SyllableBoundaries = boundaries ?? Array.Empty<double>(),
                SyllableSplits = splits ?? Array.Empty<int>(),
                Pauses = rests ?? Array.Empty<WordPause>(),
            };

            hitObject.Line = new LyricLine
            {
                RawText = lyric.RawText,
                StartTime = lyric.StartTime,
                EndTime = lyric.EndTime,
                SingEndTime = lyric.SingEndTime,
                Units = units,
            };
        }

        // ---- serialization ----

        [Test]
        public void LinePayload_RoundTripsThroughTheStringClipboard()
        {
            var editorBeatmap = createBeatmap();
            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });

            string serialized = LyricTimingClipboard.Serialize(payload);
            var (lines, units) = LyricTimingClipboard.TryParse(serialized);

            Assert.Multiple(() =>
            {
                Assert.That(units, Is.Null);
                Assert.That(lines, Is.Not.Null);
                Assert.That(lines!.Lines, Has.Count.EqualTo(1));
                Assert.That(lines.Lines[0].SingEndOffset, Is.EqualTo(1800));
                Assert.That(lines.Lines[0].Units.Select(u => (u.Start, u.End)), Is.EqualTo(new[] { (0d, 800d), (900d, 1800d) }));
            });
        }

        [Test]
        public void TryParse_RejectsForeignContent()
        {
            Assert.Multiple(() =>
            {
                Assert.That(LyricTimingClipboard.TryParse(null), Is.EqualTo(((LyricTimingClipboard.LineTimingsPayload?)null, (LyricTimingClipboard.UnitTimingsPayload?)null)));
                Assert.That(LyricTimingClipboard.TryParse("plain text").lines, Is.Null);
                Assert.That(LyricTimingClipboard.TryParse("{\"type\":\"something-else\"}").lines, Is.Null);
                Assert.That(LyricTimingClipboard.TryParse("{not json").units, Is.Null);
            });
        }

        // ---- line paste ----

        [Test]
        public void PasteOntoLine_RebasesOntoTargetStart_WithoutMovingBoundaries()
        {
            var editorBeatmap = createBeatmap();

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 1) }, payload);

            var target = line(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                // Chorus rebase: line 0's internal pattern at line 1's own start.
                Assert.That(target.StartTime, Is.EqualTo(3000)); // boundary untouched
                Assert.That(target.EndTime, Is.EqualTo(6000));   // boundary untouched
                Assert.That(target.RawText, Is.EqualTo("gamma delta")); // timings-only; text is the target's
                Assert.That(target.Units.Select(u => (u.StartTime, u.EndTime)), Is.EqualTo(new[] { (3000d, 3800d), (3900d, 4800d) }));
                Assert.That(target.SingEndTime, Is.EqualTo(4800));
                Assert.That(target.Units.All(u => u.Source == TimingSource.Explicit), Is.True);
                Assert.That(target.Estimated, Is.False);
            });
        }

        [Test]
        public void PasteOneLine_BroadcastsToEveryTarget()
        {
            var editorBeatmap = createBeatmap();

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap,
                new[] { line(editorBeatmap, 1), line(editorBeatmap, 2) }, payload);

            var second = line(editorBeatmap, 1).Line;
            var last = line(editorBeatmap, 2).Line;

            Assert.Multiple(() =>
            {
                Assert.That(second.Units[0].StartTime, Is.EqualTo(3000));

                // The last line has ONE word for a two-span pattern: surplus spans drop.
                Assert.That(last.Units, Has.Count.EqualTo(1));
                Assert.That((last.Units[0].StartTime, last.Units[0].EndTime), Is.EqualTo((6000d, 6800d)));
                Assert.That(last.SingEndTime, Is.EqualTo(7800)); // 6000 + 1800 rebased

                // Last-line reload invariant: EndTime within [singEnd, singEnd + tail].
                Assert.That(last.EndTime, Is.GreaterThanOrEqualTo(last.SingEndTime));
                Assert.That(last.EndTime, Is.LessThanOrEqualTo(last.SingEndTime + TypeBeatEditorOperations.LAST_LINE_TAIL_MS));
            });
        }

        [Test]
        public void PasteZip_PairsPositionally_LeavesExtraTargetsUntouched()
        {
            var editorBeatmap = createBeatmap();

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0), line(editorBeatmap, 1) });

            var untouchedBefore = line(editorBeatmap, 2).Line;
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 1), line(editorBeatmap, 2) }, payload);

            var first = line(editorBeatmap, 1).Line;
            var second = line(editorBeatmap, 2).Line;

            Assert.Multiple(() =>
            {
                // target[0] ← source[0] (line0's pattern), target[1] ← source[1] (line1's pattern).
                Assert.That(first.Units.Select(u => (u.StartTime, u.EndTime)), Is.EqualTo(new[] { (3000d, 3800d), (3900d, 4800d) }));

                // line1's pattern rebased at 6000: span (0,1200) → (6000,7200); the second span drops (one word).
                Assert.That((second.Units[0].StartTime, second.Units[0].EndTime), Is.EqualTo((6000d, 7200d)));
                Assert.That(untouchedBefore, Is.Not.SameAs(second)); // instance rebuilt; sanity that paste touched it
            });
        }

        [Test]
        public void PasteWithMoreWordsThanSpans_InterpolatesTheLeftovers()
        {
            var editorBeatmap = createBeatmap();

            // Source: the one-word line (span rel (0,1000), singEnd offset 1000).
            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 2) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 0) }, payload);

            var target = line(editorBeatmap, 0).Line;

            Assert.Multiple(() =>
            {
                // Word 0 takes the span; word 1 is synthesized after it (Interpolated, non-degenerate).
                Assert.That((target.Units[0].StartTime, target.Units[0].EndTime), Is.EqualTo((1000d, 2000d)));
                Assert.That(target.Units[0].Source, Is.EqualTo(TimingSource.Explicit));

                Assert.That(target.Units[1].StartTime, Is.EqualTo(2000));
                Assert.That(target.Units[1].EndTime, Is.GreaterThanOrEqualTo(2000 + TypeBeatEditorOperations.MIN_SPAN_MS));
                Assert.That(target.Units[1].EndTime, Is.LessThanOrEqualTo(target.EndTime));
                Assert.That(target.Units[1].Source, Is.EqualTo(TimingSource.Interpolated));
            });
        }

        [Test]
        public void PastePatternLongerThanTargetWindow_ClampsMonotonically()
        {
            var editorBeatmap = createBeatmap();

            // line1's pattern (spans up to +2500) pasted onto line0 (window 1000..3000).
            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 1) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 0) }, payload);

            var target = line(editorBeatmap, 0).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.EndTime, Is.EqualTo(3000)); // boundary still untouched
                Assert.That(target.Units[0].StartTime, Is.EqualTo(1000));

                // Monotonic, fully inside the window.
                double previousEnd = target.StartTime;

                foreach (var unit in target.Units)
                {
                    Assert.That(unit.StartTime, Is.GreaterThanOrEqualTo(previousEnd));
                    Assert.That(unit.EndTime, Is.GreaterThanOrEqualTo(unit.StartTime));
                    Assert.That(unit.EndTime, Is.LessThanOrEqualTo(target.EndTime));
                    previousEnd = unit.EndTime;
                }
            });
        }

        // ---- unit-run paste ----

        [Test]
        public void UnitRun_PastesAnchoredAtTheFocusedWord()
        {
            var editorBeatmap = createBeatmap();

            // Copy line0's two words: pattern (0,800),(900,1800) rel to first word's start.
            var run = TypeBeatEditorOperations.CopyUnitTimings(line(editorBeatmap, 0), new[] { 0, 1 })!;
            Assert.That(run.Units.Select(u => (u.Start, u.End)), Is.EqualTo(new[] { (0d, 800d), (900d, 1800d) }));

            // Paste into line1 anchored at word 0 (current start 3000).
            TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, line(editorBeatmap, 1), 0, run);

            var target = line(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.Units.Select(u => (u.StartTime, u.EndTime)), Is.EqualTo(new[] { (3000d, 3800d), (3900d, 4800d) }));
                Assert.That(target.Units.All(u => u.Source == TimingSource.Explicit), Is.True);
                // A unit paste touches no line field of its own, but this run reached the LAST word
                // and overwrote its end, and end_ms is auto-derived from that end.
                Assert.That(target.SingEndTime, Is.EqualTo(4800));
                Assert.That(target.EndTime, Is.EqualTo(6000)); // the typeable window is untouched
                Assert.That(target.Estimated, Is.False);
            });
        }

        [Test]
        public void UnitRun_OvershootClampsInsideTheLineWindow()
        {
            var editorBeatmap = createBeatmap();

            // line1's wide pattern (spans (0,1200),(1300,2500)) pasted into line0 at word 0
            // (anchor 1000): raw ends at 3500, past line0's window end 3000.
            var run = TypeBeatEditorOperations.CopyUnitTimings(line(editorBeatmap, 1), new[] { 0, 1 })!;
            TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, line(editorBeatmap, 0), 0, run);

            var target = line(editorBeatmap, 0).Line;

            Assert.Multiple(() =>
            {
                Assert.That((target.Units[0].StartTime, target.Units[0].EndTime), Is.EqualTo((1000d, 2200d)));
                Assert.That((target.Units[1].StartTime, target.Units[1].EndTime), Is.EqualTo((2300d, 3000d))); // end clamped to window
            });
        }

        [Test]
        public void UnitRun_SurplusSpansPastTheLastWord_AreDropped()
        {
            var editorBeatmap = createBeatmap();

            var run = TypeBeatEditorOperations.CopyUnitTimings(line(editorBeatmap, 0), new[] { 0, 1 })!;

            // Anchor at line1's LAST word; only the first span fits.
            TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, line(editorBeatmap, 1), 1, run);

            var target = line(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That((target.Units[0].StartTime, target.Units[0].EndTime), Is.EqualTo((3000d, 4200d))); // untouched
                Assert.That((target.Units[1].StartTime, target.Units[1].EndTime), Is.EqualTo((4300d, 5100d))); // anchor + (0,800)
            });
        }

        [Test]
        public void CopyUnitTimings_NonContiguousSelection_CollapsesGaps()
        {
            var editorBeatmap = createBeatmap();

            // Selecting words 0 and 1 of line 1 out of order / with duplicates behaves as sorted-distinct.
            var run = TypeBeatEditorOperations.CopyUnitTimings(line(editorBeatmap, 1), new[] { 1, 0, 1 })!;

            Assert.That(run.Units.Select(u => (u.Start, u.End)), Is.EqualTo(new[] { (0d, 1200d), (1300d, 2500d) }));
        }

        // ---- sub-word timing: subdivisions, authored splits, authored rests ----
        //
        // A line paste used to drop every one of them. The source's were never captured, and the
        // target's own were destroyed by the retime clamp (they sat at their old absolute times while
        // the word moved to the source's span), so the chorus workflow came back undivided. The
        // policy now is on PasteLineTimings: boundaries always travel for a positionally mapped word,
        // the two CHAR-INDEXED halves travel only when the two words have the same character count.

        [Test]
        public void LinePayloadCapturesSubWordTiming()
        {
            var editorBeatmap = createBeatmap();

            // "alpha" [1000..1800] cut "al|ph|a"; "beta" [1900..2800] with one rest after "be".
            subdivide(editorBeatmap, 0, 0, boundaries: new[] { 1300d, 1600d }, splits: new[] { 2, 4 });
            subdivide(editorBeatmap, 0, 1, rests: new[] { new WordPause(2200, 2350, 2) });

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });
            var (parsed, _) = LyricTimingClipboard.TryParse(LyricTimingClipboard.Serialize(payload));
            var words = parsed!.Lines[0].Units;

            Assert.Multiple(() =>
            {
                // Every offset is LINE-relative, exactly like the spans beside it (line start 1000).
                Assert.That(words[0].Boundaries, Is.EqualTo(new[] { 300d, 600d }));
                Assert.That(words[0].Splits, Is.EqualTo(new[] { 2, 4 }));
                Assert.That(words[0].Chars, Is.EqualTo(5), "\"alpha\"");
                Assert.That(words[0].Rests, Is.Null);

                Assert.That(words[1].Boundaries, Is.Null, "\"beta\" is undivided");
                Assert.That(words[1].Splits, Is.Null);
                Assert.That(words[1].Chars, Is.EqualTo(4), "\"beta\"");
                Assert.That(words[1].Rests!.Select(r => (r.Start, r.End, r.SplitChar)), Is.EqualTo(new[] { (1200d, 1350d, 2) }));
            });
        }

        [Test]
        public void PayloadWithoutTheSubWordFields_StillParses()
        {
            // A payload serialized before the fields existed: every one of them is optional, so it
            // reads back as a plain span pattern rather than failing the whole paste.
            var (lines, _) = LyricTimingClipboard.TryParse(
                "{\"type\":\"typebeat-line-timings\",\"lines\":[{\"sing_end\":1800,\"units\":[{\"start\":0,\"end\":800}]}]}");

            Assert.Multiple(() =>
            {
                Assert.That(lines, Is.Not.Null);
                Assert.That(lines!.Lines[0].Units[0].End, Is.EqualTo(800));
                Assert.That(lines.Lines[0].Units[0].Chars, Is.Zero);
                Assert.That(lines.Lines[0].Units[0].Boundaries, Is.Null);
                Assert.That(lines.Lines[0].Units[0].Splits, Is.Null);
                Assert.That(lines.Lines[0].Units[0].Rests, Is.Null);
            });
        }

        [Test]
        public void PasteCarriesSubdivisionsRebasedOntoTheTargetWord()
        {
            var editorBeatmap = createBeatmap();

            // Source line 0: "alpha" cut "al|ph|a" at 1300/1600, "beta" cut "be|ta" at 2300.
            subdivide(editorBeatmap, 0, 0, boundaries: new[] { 1300d, 1600d }, splits: new[] { 2, 4 });
            subdivide(editorBeatmap, 0, 1, boundaries: new[] { 2300d }, splits: new[] { 2 });

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 1) }, payload);

            var target = line(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                // Word 0: span 1000..1800 rebased to 3000..3800, so each boundary keeps its distance
                // into the word (300 and 600 in).
                Assert.That(target.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3300d, 3600d }));

                // "gamma" is 5 chars like "alpha", so the authored cut travels verbatim.
                Assert.That(target.Units[0].SyllableSplits, Is.EqualTo(new[] { 2, 4 }));
                Assert.That(SyllableSegments.SegmentTexts("gamma", SyllableSegments.SplitsFor(target.Units[0])),
                    Is.EqualTo(new[] { "ga", "mm", "a" }));

                // Word 1: span 1900..2800 rebased to 3900..4800, boundary 400 in.
                Assert.That(target.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 4300d }));

                // "delta" is 5 chars where "beta" was 4, so the CHAR INDEX does not travel: the word
                // falls back to the derived split, which can never point past its own end.
                Assert.That(target.Units[1].SyllableSplits, Is.Empty);
                Assert.That(SyllableSegments.SplitsFor(target.Units[1]), Is.EqualTo(SyllableSegments.Derived("delta", 2)));
            });
        }

        [Test]
        public void PasteReplacesTheTargetsOwnSubdivisionsRatherThanKeepingThem()
        {
            var editorBeatmap = createBeatmap();

            subdivide(editorBeatmap, 0, 0, boundaries: new[] { 1300d, 1600d }, splits: new[] { 2, 4 });

            // The target word has a subdivision of its OWN at 3100, inside its old span 3000..4200
            // AND inside the span it is about to be given (3000..3800). Carrying the target's own
            // boundaries through the retime clamp is exactly how this used to survive as a stale
            // leftover while the source's two were never captured at all.
            subdivide(editorBeatmap, 1, 0, boundaries: new[] { 3100d }, splits: new[] { 1 });

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 1) }, payload);

            var word = line(editorBeatmap, 1).Line.Units[0];

            Assert.Multiple(() =>
            {
                Assert.That(word.SyllableBoundaries, Is.EqualTo(new[] { 3300d, 3600d }));
                Assert.That(word.SyllableBoundaries, Has.No.Member(3100d), "the target's own boundary is overwritten, not kept");
                Assert.That(word.SyllableSplits, Is.EqualTo(new[] { 2, 4 }));
            });
        }

        [Test]
        public void PasteLeavesNoSubWordTimingOnAnInterpolatedLeftoverWord()
        {
            var editorBeatmap = createBeatmap();

            // Source: the one-word line, "omega" cut "om|ega" at 6400.
            subdivide(editorBeatmap, 2, 0, boundaries: new[] { 6400d }, splits: new[] { 2 });

            // Target word 1 ("beta") has a subdivision of its own; it is a LEFTOVER (one span, two
            // words), so it has no source word to take timing from and must keep none of its own.
            subdivide(editorBeatmap, 0, 1, boundaries: new[] { 2300d }, splits: new[] { 2 });

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 2) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 0) }, payload);

            var target = line(editorBeatmap, 0).Line;

            Assert.Multiple(() =>
            {
                // Word 0 is mapped: "alpha" is 5 chars like "omega", so the cut travels.
                Assert.That(target.Units[0].StartTime, Is.EqualTo(1000));
                Assert.That(target.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1400d }));
                Assert.That(target.Units[0].SyllableSplits, Is.EqualTo(new[] { 2 }));

                Assert.That(target.Units[1].Source, Is.EqualTo(TimingSource.Interpolated));
                Assert.That(target.Units[1].SyllableBoundaries, Is.Empty);
                Assert.That(target.Units[1].SyllableSplits, Is.Empty);
            });
        }

        [Test]
        public void PasteDropsSurplusSourceWordsSubWordTimingWithTheirSpans()
        {
            var editorBeatmap = createBeatmap();

            subdivide(editorBeatmap, 0, 0, boundaries: new[] { 1300d, 1600d }, splits: new[] { 2, 4 });
            subdivide(editorBeatmap, 0, 1, boundaries: new[] { 2300d }, splits: new[] { 2 });

            // Two source words onto the one-word last line: source word 1 drops whole.
            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 2) }, payload);

            var target = line(editorBeatmap, 2).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.Units, Has.Count.EqualTo(1));
                Assert.That(target.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 6300d, 6600d }));
                Assert.That(target.Units[0].SyllableSplits, Is.EqualTo(new[] { 2, 4 }), "\"omega\" is 5 chars like \"alpha\"");

                // Every boundary strictly inside its own word, the invariant TimedUnit declares.
                Assert.That(target.Units[0].SyllableBoundaries.All(b => b > target.Units[0].StartTime && b < target.Units[0].EndTime), Is.True);
            });
        }

        [Test]
        public void SubdivisionsTheTargetWindowCannotHoldAreDropped()
        {
            var editorBeatmap = createBeatmap();

            // "gamma" [3000..4200] cut at 3600; "delta" [4300..5500] cut at 5400, near its end.
            subdivide(editorBeatmap, 1, 0, boundaries: new[] { 3600d }, splits: new[] { 3 });
            subdivide(editorBeatmap, 1, 1, boundaries: new[] { 5400d }, splits: new[] { 3 });

            // Line 1's wide pattern onto line 0, whose window ends at 3000: word 1 is clamped to
            // 2300..3000, and its boundary would land at 3400, past the clamped end.
            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 1) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 0) }, payload);

            var target = line(editorBeatmap, 0).Line;

            Assert.Multiple(() =>
            {
                // Word 0 fits: span 1000..2200, boundary 600 in.
                Assert.That(target.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1600d }));
                Assert.That(target.Units[0].SyllableSplits, Is.EqualTo(new[] { 3 }), "\"alpha\" is 5 chars like \"gamma\"");

                Assert.That((target.Units[1].StartTime, target.Units[1].EndTime), Is.EqualTo((2300d, 3000d)));
                Assert.That(target.Units[1].SyllableBoundaries, Is.Empty, "the clamped span cannot hold it");

                // The split named a boundary that no longer exists, so it goes with it rather than
                // pairing with the wrong segment.
                Assert.That(target.Units[1].SyllableSplits, Is.Empty);
            });
        }

        [Test]
        public void PasteCarriesAuthoredRestsOnlyWhenTheCharacterCountMatches()
        {
            var editorBeatmap = createBeatmap();

            subdivide(editorBeatmap, 0, 0, rests: new[] { new WordPause(1400, 1500, 2) }); // "be"-style cut in "alpha"
            subdivide(editorBeatmap, 0, 1, rests: new[] { new WordPause(2200, 2350, 2) }); // in "beta", 4 chars

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 1) }, payload);

            var target = line(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                // "gamma" matches "alpha" at 5 chars: the rest travels, rebased with the span.
                Assert.That(target.Units[0].Pauses.Select(p => (p.StartTime, p.EndTime, p.SplitChar)),
                    Is.EqualTo(new[] { (3400d, 3500d, 2) }));

                // "delta" (5) against "beta" (4): a rest has no derived form, so it is dropped rather
                // than landing on a character it was not authored for.
                Assert.That(target.Units[1].Pauses, Is.Empty);
            });
        }

        [Test]
        public void PasteMovesGranularityWithTheSubdivisionsItBringsAndTakesAway()
        {
            var editorBeatmap = createBeatmap();

            subdivide(editorBeatmap, 0, 0, boundaries: new[] { 1300d }, splits: new[] { 2 });
            Assert.That(line(editorBeatmap, 1).Granularity, Is.EqualTo(TimingGranularity.Word), "fixture");

            var subdivided = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 1) }, subdivided);

            Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap).Select(o => o.Granularity),
                Is.All.EqualTo(TimingGranularity.Syllable), "a subdivision arrived, so the encoder has to persist it");

            // Now stamp the undivided last line's pattern over both subdivided lines: the map holds no
            // boundary anywhere, so it falls back to Word, never below (the units are still Explicit).
            var plain = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 2) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 0), line(editorBeatmap, 1) }, plain);

            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap).SelectMany(o => o.Line.Units).All(u => u.SyllableBoundaries.Count == 0), Is.True);
                Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap).Select(o => o.Granularity), Is.All.EqualTo(TimingGranularity.Word));
            });
        }

        [Test]
        public void UndoRestoresTheTargetLinesOwnSubdivisions()
        {
            var editorBeatmap = createBeatmap();

            subdivide(editorBeatmap, 0, 0, boundaries: new[] { 1300d, 1600d }, splits: new[] { 2, 4 });
            subdivide(editorBeatmap, 1, 0, boundaries: new[] { 3500d }, splits: new[] { 1 });

            // The real editor handler: every state is the map through this ruleset's own encoder, and
            // undo decodes one back over the hit objects. Sub-word timing only survives that round
            // trip above Line granularity, so the paste's own granularity sync is load-bearing here.
            foreach (var o in TypeBeatEditorOperations.OrderedLines(editorBeatmap))
                o.Granularity = TimingGranularity.Syllable;

            var changeHandler = new RulesetBeatmapChangeHandler(editorBeatmap, new TypeBeatRuleset());

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });
            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 1) }, payload);

            Assert.That(line(editorBeatmap, 1).Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3300d, 3600d }), "pasted");
            Assert.That(changeHandler.CanUndo.Value, Is.True);

            changeHandler.RestoreState(-1);

            var restored = line(editorBeatmap, 1).Line.Units[0];

            Assert.Multiple(() =>
            {
                Assert.That(restored.SyllableBoundaries, Is.EqualTo(new[] { 3500d }));
                Assert.That(restored.SyllableSplits, Is.EqualTo(new[] { 1 }));
                Assert.That((restored.StartTime, restored.EndTime), Is.EqualTo((3000d, 4200d)));
            });

            changeHandler.RestoreState(1);

            Assert.That(line(editorBeatmap, 1).Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3300d, 3600d }), "redo");
        }

        [Test]
        public void UnitRunPayloadStillCarriesNoSubWordTiming()
        {
            var editorBeatmap = createBeatmap();

            subdivide(editorBeatmap, 0, 0, boundaries: new[] { 1300d }, splits: new[] { 2 });

            var run = TypeBeatEditorOperations.CopyUnitTimings(line(editorBeatmap, 0), new[] { 0, 1 })!;

            // Deliberate divergence from the LINE paste: a unit run is anchored wherever the caret
            // sits, so there is no positional correspondence to justify moving a char index.
            Assert.Multiple(() =>
            {
                Assert.That(run.Units.All(u => u.Boundaries == null && u.Splits == null && u.Rests == null && u.Chars == 0), Is.True);

                // And the target word keeps its own, re-clamped: "gamma" 3000..4200 with a boundary at
                // 3500 takes the pattern's 3000..3800, which still holds it.
                subdivide(editorBeatmap, 1, 0, boundaries: new[] { 3500d }, splits: new[] { 1 });
                TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, line(editorBeatmap, 1), 0, run);

                Assert.That(line(editorBeatmap, 1).Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3500d }));
                Assert.That(line(editorBeatmap, 1).Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 1 }));
            });
        }

        // ---- backlog 339: the line paste carries the words ----

        /// <summary>The fixture's words and spans of one line, for whole-line comparisons.</summary>
        private static (string, double, double)[] shape(LyricLine lyric) => lyric.Units.Select(u => (u.Text, u.StartTime, u.EndTime)).ToArray();

        /// <summary>Replaces one line's text and words outright (the fixture cannot express a freestyle token).</summary>
        private static void setWords(EditorBeatmap editorBeatmap, int lineIndex, params (string text, double s, double e)[] words)
        {
            var hitObject = line(editorBeatmap, lineIndex);
            var lyric = hitObject.Line;

            hitObject.Line = new LyricLine
            {
                RawText = string.Join(' ', words.Select(w => w.text)),
                StartTime = lyric.StartTime,
                EndTime = lyric.EndTime,
                SingEndTime = words[^1].e,
                Units = words.Select(w => new TimedUnit { Text = w.text, StartTime = w.s, EndTime = w.e, Source = TimingSource.Explicit, Confidence = 1 }).ToArray(),
            };
        }

        private const string old_payload = "{\"type\":\"typebeat-line-timings\",\"lines\":[{\"sing_end\":1800,\"units\":[{\"start\":0,\"end\":800,\"chars\":5},{\"start\":900,\"end\":1800,\"chars\":4}]}]}";

        [Test]
        public void LinePayloadCarriesTheWordsBehindAVersionMarker()
        {
            var editorBeatmap = createBeatmap();
            line(editorBeatmap, 0).Line = new LyricLine
            {
                RawText = line(editorBeatmap, 0).Line.RawText,
                StartTime = 1000,
                EndTime = 3000,
                SingEndTime = 2800,
                Units = line(editorBeatmap, 0).Line.Units,
                SealGraceMs = 250,
                Estimated = true,
            };

            string json = LyricTimingClipboard.Serialize(TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) }));
            var (parsed, _) = LyricTimingClipboard.TryParse(json);

            Assert.Multiple(() =>
            {
                // Same discriminator as ever: an older build still reads it as timings.
                Assert.That(json, Does.StartWith("{\"type\":\"typebeat-line-timings\",\"version\":2,"));
                Assert.That(parsed!.CarriesText, Is.True);
                Assert.That(parsed.Lines[0].Units.Select(u => u.Text), Is.EqualTo(new[] { "alpha", "beta" }));
                Assert.That(parsed.Lines[0].RawText, Is.EqualTo("alpha beta"));
                Assert.That(parsed.Lines[0].SealGraceMs, Is.EqualTo(250));
                Assert.That(parsed.Lines[0].Estimated, Is.True);
                Assert.That(parsed.Lines[0].Granularity, Is.EqualTo(TimingGranularity.Word));
                Assert.That(parsed.PlainText(), Is.EqualTo("alpha beta"));

                // A payload from before the text existed carries none, and neither does one that has
                // words but no version marker.
                var (old, _) = LyricTimingClipboard.TryParse(old_payload);
                Assert.That(old!.CarriesText, Is.False);
                Assert.That(old.PlainText(), Is.Null);

                var (unmarked, _) = LyricTimingClipboard.TryParse(json.Replace("\"version\":2,", string.Empty));
                Assert.That(unmarked!.CarriesText, Is.False);
            });
        }

        [Test]
        public void PasteLine_ReplacesTheWordsAndTakesTheCopiedTimingExactly()
        {
            var editorBeatmap = createBeatmap();

            // "al|pha" [1000..1800] cut at 1300 after "al"; "beta" [1900..2800] with a rest after "be".
            subdivide(editorBeatmap, 0, 0, boundaries: new[] { 1300d }, splits: new[] { 2 });
            subdivide(editorBeatmap, 0, 1, rests: new[] { new WordPause(2200, 2350, 2) });

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });
            TypeBeatEditorOperations.PasteLine(editorBeatmap, new[] { line(editorBeatmap, 1) }, payload);

            var target = line(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.RawText, Is.EqualTo("alpha beta"));
                Assert.That(target.StartTime, Is.EqualTo(3000), "start kept");
                Assert.That(target.EndTime, Is.EqualTo(6000), "interior end kept: the next line's start is the wall");
                Assert.That(line(editorBeatmap, 2).Line.StartTime, Is.EqualTo(6000), "nothing cascades");

                // Rebased by +2000 (source start 1000, target start 3000).
                Assert.That(shape(target), Is.EqualTo(new[] { ("alpha", 3000d, 3800d), ("beta", 3900d, 4800d) }));
                Assert.That(target.SingEndTime, Is.EqualTo(4800));
                Assert.That(target.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3300d }));
                Assert.That(target.Units[0].SyllableSplits, Is.EqualTo(new[] { 2 }));
                Assert.That(target.Units[1].Pauses.Select(p => (p.StartTime, p.EndTime, p.SplitChar)), Is.EqualTo(new[] { (4200d, 4350d, 2) }));
                Assert.That(target.Units.All(u => u.Source == TimingSource.Explicit), Is.True);
            });
        }

        [Test]
        public void PasteLine_ADifferentWordCountReplacesTheWholeWordList()
        {
            var editorBeatmap = createBeatmap();

            // One word ("omega", rel 0..1000) over two, and two words over one (the LAST line).
            var one = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 2) });
            var two = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });

            TypeBeatEditorOperations.PasteLine(editorBeatmap, new[] { line(editorBeatmap, 1) }, one);
            TypeBeatEditorOperations.PasteLine(editorBeatmap, new[] { line(editorBeatmap, 2) }, two);

            var second = line(editorBeatmap, 1).Line;
            var last = line(editorBeatmap, 2).Line;

            Assert.Multiple(() =>
            {
                Assert.That(second.RawText, Is.EqualTo("omega"));
                Assert.That(shape(second), Is.EqualTo(new[] { ("omega", 3000d, 4000d) }));
                Assert.That((second.StartTime, second.SingEndTime, second.EndTime), Is.EqualTo((3000d, 4000d, 6000d)));

                // The last line's end follows the pasted words (backlog 336): sung end 6000 + 1800,
                // plus the 1000 ms tail the line already carried (8000 - 7000).
                Assert.That(last.RawText, Is.EqualTo("alpha beta"));
                Assert.That(shape(last), Is.EqualTo(new[] { ("alpha", 6000d, 6800d), ("beta", 6900d, 7800d) }));
                Assert.That((last.StartTime, last.SingEndTime, last.EndTime), Is.EqualTo((6000d, 7800d, 8800d)));
            });
        }

        [Test]
        public void PasteLine_ALongerPatternClampsMonotonicallyIntoTheWindow()
        {
            var editorBeatmap = createBeatmap();

            // "gamma delta" (rel 0..1200, 1300..2500, sung end 2500) onto line 0's 1000..3000.
            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 1) });
            TypeBeatEditorOperations.PasteLine(editorBeatmap, new[] { line(editorBeatmap, 0) }, payload);

            var target = line(editorBeatmap, 0).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.RawText, Is.EqualTo("gamma delta"));
                Assert.That(shape(target), Is.EqualTo(new[] { ("gamma", 1000d, 2200d), ("delta", 2300d, 3000d) }));
                Assert.That((target.StartTime, target.SingEndTime, target.EndTime), Is.EqualTo((1000d, 3000d, 3000d)));
                Assert.That(line(editorBeatmap, 1).Line.StartTime, Is.EqualTo(3000), "the wall did not move");
            });
        }

        [Test]
        public void PasteLine_BroadcastsOneLineToThreeTargets()
        {
            var editorBeatmap = createBeatmap();

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });
            TypeBeatEditorOperations.PasteLine(editorBeatmap, TypeBeatEditorOperations.OrderedLines(editorBeatmap), payload);

            var lines = TypeBeatEditorOperations.OrderedLines(editorBeatmap).Select(o => o.Line).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(lines.Select(l => l.RawText), Is.All.EqualTo("alpha beta"));
                Assert.That(shape(lines[0]), Is.EqualTo(new[] { ("alpha", 1000d, 1800d), ("beta", 1900d, 2800d) }));
                Assert.That(shape(lines[1]), Is.EqualTo(new[] { ("alpha", 3000d, 3800d), ("beta", 3900d, 4800d) }));
                Assert.That(shape(lines[2]), Is.EqualTo(new[] { ("alpha", 6000d, 6800d), ("beta", 6900d, 7800d) }));
                Assert.That(lines.Select(l => l.StartTime), Is.EqualTo(new[] { 1000d, 3000d, 6000d }));
                Assert.That(lines[0].EndTime, Is.EqualTo(3000));
                Assert.That(lines[1].EndTime, Is.EqualTo(6000));
            });
        }

        [Test]
        public void PasteLine_ZipsTwoLinesOntoTwo()
        {
            var editorBeatmap = createBeatmap();

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 1), line(editorBeatmap, 2) });
            TypeBeatEditorOperations.PasteLine(editorBeatmap, new[] { line(editorBeatmap, 0), line(editorBeatmap, 1) }, payload);

            var first = line(editorBeatmap, 0).Line;
            var second = line(editorBeatmap, 1).Line;
            var untouched = line(editorBeatmap, 2).Line;

            Assert.Multiple(() =>
            {
                Assert.That(first.RawText, Is.EqualTo("gamma delta"));
                Assert.That(shape(first), Is.EqualTo(new[] { ("gamma", 1000d, 2200d), ("delta", 2300d, 3000d) }));
                Assert.That(second.RawText, Is.EqualTo("omega"));
                Assert.That(shape(second), Is.EqualTo(new[] { ("omega", 3000d, 4000d) }));
                Assert.That(untouched.RawText, Is.EqualTo("omega"));
                Assert.That(shape(untouched), Is.EqualTo(new[] { ("omega", 6000d, 7000d) }));
            });
        }

        [Test]
        public void PasteLine_IsOneUndoStepRestoringWordsAndTimingTogether()
        {
            var editorBeatmap = createBeatmap();
            var changeHandler = new RulesetBeatmapChangeHandler(editorBeatmap, new TypeBeatRuleset());

            var before = line(editorBeatmap, 1).Line;
            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 2) });
            TypeBeatEditorOperations.PasteLine(editorBeatmap, new[] { line(editorBeatmap, 1), line(editorBeatmap, 0) }, payload);

            Assert.That(line(editorBeatmap, 1).Line.RawText, Is.EqualTo("omega"), "pasted");

            changeHandler.RestoreState(-1);

            Assert.Multiple(() =>
            {
                Assert.That(changeHandler.CanUndo.Value, Is.False, "the whole paste was one step");
                Assert.That(line(editorBeatmap, 0).Line.RawText, Is.EqualTo("alpha beta"));
                Assert.That(line(editorBeatmap, 1).Line.RawText, Is.EqualTo(before.RawText));
                Assert.That(shape(line(editorBeatmap, 1).Line), Is.EqualTo(shape(before)));
                Assert.That(line(editorBeatmap, 1).Line.SingEndTime, Is.EqualTo(before.SingEndTime));
            });
        }

        [Test]
        public void AnOldPayloadWithoutTextPastesTimingOnlyUnderBothGestures()
        {
            var (old, _) = LyricTimingClipboard.TryParse(old_payload);

            var viaDefault = createBeatmap();
            TypeBeatEditorOperations.PasteLine(viaDefault, new[] { line(viaDefault, 1) }, old!);

            var viaTimings = createBeatmap();
            TypeBeatEditorOperations.PasteLineTimings(viaTimings, new[] { line(viaTimings, 1) }, old!);

            Assert.Multiple(() =>
            {
                Assert.That(line(viaDefault, 1).Line.RawText, Is.EqualTo("gamma delta"), "the target keeps its words");
                Assert.That(shape(line(viaDefault, 1).Line), Is.EqualTo(new[] { ("gamma", 3000d, 3800d), ("delta", 3900d, 4800d) }));
                Assert.That(shape(line(viaDefault, 1).Line), Is.EqualTo(shape(line(viaTimings, 1).Line)));
                Assert.That(line(viaDefault, 1).Line.SingEndTime, Is.EqualTo(line(viaTimings, 1).Line.SingEndTime));

                // And with nothing selected it cannot become a line of its own: it has no words.
                Assert.That(TypeBeatEditorOperations.InsertCopiedLine(viaDefault, 9000, old!.Lines[0]), Is.Null);
                Assert.That(TypeBeatEditorOperations.OrderedLines(viaDefault), Has.Count.EqualTo(3));
            });
        }

        [Test]
        public void TheTimingPasteIgnoresTheTextANewPayloadCarries()
        {
            var editorBeatmap = createBeatmap();

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 2) });
            Assert.That(payload.CarriesText, Is.True, "fixture");

            TypeBeatEditorOperations.PasteLineTimings(editorBeatmap, new[] { line(editorBeatmap, 1) }, payload);

            var target = line(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.RawText, Is.EqualTo("gamma delta"));
                Assert.That(target.Units.Select(u => u.Text), Is.EqualTo(new[] { "gamma", "delta" }));
                Assert.That((target.Units[0].StartTime, target.Units[0].EndTime), Is.EqualTo((3000d, 4000d)));
            });
        }

        [Test]
        public void WithNoTargetTheCopiedLineIsInsertedAtThePlayhead()
        {
            var editorBeatmap = createBeatmap();
            var changeHandler = new RulesetBeatmapChangeHandler(editorBeatmap, new TypeBeatRuleset());

            var source = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) }).Lines[0];

            // Inside line 1's window: the new line takes 4500..6000 and line 1 now ends at 4500.
            var added = TypeBeatEditorOperations.InsertCopiedLine(editorBeatmap, 4500, source);
            var lines = TypeBeatEditorOperations.OrderedLines(editorBeatmap);

            Assert.Multiple(() =>
            {
                Assert.That(added, Is.Not.Null);
                Assert.That(lines, Has.Count.EqualTo(4));
                Assert.That(lines[2], Is.SameAs(added));
                Assert.That(lines[1].Line.EndTime, Is.EqualTo(4500), "the boundary invariant: the previous line ends where it starts");
                Assert.That(added!.Line.RawText, Is.EqualTo("alpha beta"));
                Assert.That((added.Line.StartTime, added.Line.EndTime), Is.EqualTo((4500d, 6000d)));

                // Rebased by +3500; "beta" (6400..7300) clamps against the next line's start.
                Assert.That(shape(added.Line), Is.EqualTo(new[] { ("alpha", 4500d, 5300d), ("beta", 5400d, 6000d) }));
                Assert.That(added.Line.SingEndTime, Is.EqualTo(6000));
                Assert.That(lines.Select(o => o.LineIndex), Is.EqualTo(new[] { 0, 1, 2, 3 }));
            });

            changeHandler.RestoreState(-1);

            Assert.Multiple(() =>
            {
                Assert.That(changeHandler.CanUndo.Value, Is.False, "insert and paste were one step");
                Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap), Has.Count.EqualTo(3));
                Assert.That(line(editorBeatmap, 1).Line.EndTime, Is.EqualTo(6000));
            });
        }

        [Test]
        public void AnAppendedCopiedLineBecomesTheLastLineAndItsEndFollowsItsWords()
        {
            var editorBeatmap = createBeatmap();

            var source = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) }).Lines[0];
            var added = TypeBeatEditorOperations.InsertCopiedLine(editorBeatmap, 9000, source);

            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap)[^1], Is.SameAs(added));
                Assert.That(line(editorBeatmap, 2).Line.EndTime, Is.EqualTo(9000));
                Assert.That(shape(added!.Line), Is.EqualTo(new[] { ("alpha", 9000d, 9800d), ("beta", 9900d, 10800d) }));
                Assert.That(added.Line.SingEndTime, Is.EqualTo(10800));
                Assert.That(added.Line.EndTime, Is.GreaterThanOrEqualTo(added.Line.SingEndTime));
                Assert.That(added.Line.EndTime, Is.LessThanOrEqualTo(added.Line.SingEndTime + TypeBeatEditorOperations.LAST_LINE_TAIL_MS));
            });
        }

        [TestCase(3000)]
        [TestCase(3010)]
        [TestCase(2980)]
        public void AnInsertTooCloseToALineStartIsRefusedAndChangesNothing(double time)
        {
            var editorBeatmap = createBeatmap();
            var changeHandler = new RulesetBeatmapChangeHandler(editorBeatmap, new TypeBeatRuleset());

            var source = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) }).Lines[0];

            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatEditorOperations.CanAddLineAt(editorBeatmap, time), Is.False);
                Assert.That(TypeBeatEditorOperations.InsertCopiedLine(editorBeatmap, time, source), Is.Null);
                Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap), Has.Count.EqualTo(3));
                Assert.That(changeHandler.CanUndo.Value, Is.False);
            });
        }

        [Test]
        public void FreestyleCellsAndAuthoredRestsRoundTripThroughThePaste()
        {
            var editorBeatmap = createBeatmap();

            setWords(editorBeatmap, 0, ("yeah", 1000, 1400), ("&&&", 1500, 2100), ("please", 2200, 2800));
            subdivide(editorBeatmap, 0, 2, rests: new[] { new WordPause(2400, 2500, 2) });

            var source = line(editorBeatmap, 0).Line;
            string json = LyricTimingClipboard.Serialize(TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) }));
            var (payload, _) = LyricTimingClipboard.TryParse(json);

            TypeBeatEditorOperations.PasteLine(editorBeatmap, new[] { line(editorBeatmap, 1) }, payload!);
            var added = TypeBeatEditorOperations.InsertCopiedLine(editorBeatmap, 9000, payload!.Lines[0]);

            var pasted = line(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                // The freestyle token survives verbatim on both paths (AddLine's Normalize never sees it).
                Assert.That(payload.PlainText(), Is.EqualTo("yeah &&& please"));
                Assert.That(pasted.RawText, Is.EqualTo(source.RawText));
                Assert.That(added!.Line.RawText, Is.EqualTo(source.RawText));
                Assert.That(shape(pasted), Is.EqualTo(source.Units.Select(u => (u.Text, u.StartTime + 2000, u.EndTime + 2000)).ToArray()));
                Assert.That(shape(added.Line), Is.EqualTo(source.Units.Select(u => (u.Text, u.StartTime + 8000, u.EndTime + 8000)).ToArray()));

                // The rest travels with its word, rebased like the span.
                Assert.That(pasted.Units[2].Pauses.Select(p => (p.StartTime, p.EndTime, p.SplitChar)), Is.EqualTo(new[] { (4400d, 4500d, 2) }));
                Assert.That(added.Line.Units[2].Pauses.Select(p => (p.StartTime, p.EndTime, p.SplitChar)), Is.EqualTo(new[] { (10400d, 10500d, 2) }));
            });
        }

        [Test]
        public void ALineGranularitySourceLandsInterpolatedAndDoesNotPromoteTheMap()
        {
            var editorBeatmap = createBeatmap();

            foreach (var o in TypeBeatEditorOperations.OrderedLines(editorBeatmap))
            {
                o.Granularity = TimingGranularity.Line;
                o.Line = new LyricLine
                {
                    RawText = o.Line.RawText,
                    StartTime = o.Line.StartTime,
                    EndTime = o.Line.EndTime,
                    SingEndTime = o.Line.SingEndTime,
                    Units = o.Line.Units.Select(u => new TimedUnit { Text = u.Text, StartTime = u.StartTime, EndTime = u.EndTime, Source = TimingSource.Interpolated }).ToArray(),
                };
            }

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { line(editorBeatmap, 0) });
            TypeBeatEditorOperations.PasteLine(editorBeatmap, new[] { line(editorBeatmap, 1) }, payload);

            Assert.Multiple(() =>
            {
                Assert.That(line(editorBeatmap, 1).Line.RawText, Is.EqualTo("alpha beta"));
                Assert.That(line(editorBeatmap, 1).Line.Units.All(u => u.Source == TimingSource.Interpolated), Is.True);
                Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap).Select(o => o.Granularity), Is.All.EqualTo(TimingGranularity.Line));
            });
        }
    }
}
