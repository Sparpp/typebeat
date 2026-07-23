// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the LICENCE file in the repository root.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The timing clipboard: copying a line's (or word run's) internal timing and pasting it
    /// elsewhere REBASED: the repeated-chorus workflow. Boundaries must never move, pastes are
    /// timings-only (text stays the target's), overwrite applies regardless of word match, and
    /// every result stays monotonic inside the target window.
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
                Assert.That(target.SingEndTime, Is.EqualTo(5500)); // unit paste does not touch line fields
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
    }
}
