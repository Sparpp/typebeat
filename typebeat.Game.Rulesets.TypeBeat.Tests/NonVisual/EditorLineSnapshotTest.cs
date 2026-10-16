// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class EditorLineSnapshotTest
    {
        private static EditorBeatmap map(int count = 3)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;

            for (int i = 0; i < count; i++)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    LineIndex = i,
                    StartTime = i * 1000,
                    Line = new LyricLine
                    {
                        RawText = "word",
                        StartTime = i * 1000,
                        EndTime = (i + 1) * 1000,
                        SingEndTime = i * 1000 + 900,
                        Units = new[] { new TimedUnit { Text = "word", StartTime = i * 1000, EndTime = i * 1000 + 900 } },
                    },
                    Granularity = TimingGranularity.Word,
                });
            }

            return new EditorBeatmap(beatmap);
        }

        /// <summary>Three word-timed lines of explicit words, enough for every word and line edit.</summary>
        private static EditorBeatmap wordTimedMap()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;

            addLine(beatmap, 0, "alpha beta", 1000, 3000, 2800, (1000, 1800), (1900, 2800));
            addLine(beatmap, 1, "gamma delta", 3000, 6000, 5500, (3000, 4200), (4300, 5500));
            addLine(beatmap, 2, "omega", 6000, 8000, 7000, (6000, 7000));

            return new EditorBeatmap(beatmap);
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end, double singEnd, params (double Start, double End)[] words)
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
                        StartTime = w.Start,
                        EndTime = w.End,
                        Source = TimingSource.Explicit,
                        Confidence = 1,
                    }).ToArray(),
                },
                Granularity = TimingGranularity.Word,
            });
        }

        /// <summary>The repository root: the closest directory above the test directory holding the ruleset project.</summary>
        private static string repositoryRoot()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "typebeat.Game.Rulesets.TypeBeat", "typebeat.Game.Rulesets.TypeBeat.csproj")))
                    return dir.FullName;
            }

            Assert.Fail("the repository root was not found above the test directory");
            return null!;
        }

        [Test]
        public void SharedSnapshotIsReusedUntilMapChanges()
        {
            var beatmap = map();
            var snapshot = EditorLineSnapshot.For(beatmap);
            var first = snapshot.Lines;
            Assert.That(EditorLineSnapshot.For(beatmap), Is.SameAs(snapshot));
            Assert.That(snapshot.Lines, Is.SameAs(first));

            TypeBeatEditorOperations.SetLineText(beatmap, first[0], "new text");
            Assert.That(snapshot.Lines, Is.Not.SameAs(first));
            Assert.That(snapshot.Lines[0].Line.RawText, Is.EqualTo("new text"));
            Assert.That(snapshot.Revision, Is.GreaterThan(0));

            var beforeRemoval = snapshot.Lines;
            TypeBeatEditorOperations.DeleteLine(beatmap, beforeRemoval[1]);
            Assert.That(snapshot.Lines, Has.Count.EqualTo(2));
            Assert.That(snapshot.Lines, Is.EqualTo(TypeBeatEditorOperations.OrderedLines(beatmap)));

            var added = TypeBeatEditorOperations.AddLine(beatmap, 1500, "inserted");
            Assert.That(added, Is.Not.Null);
            Assert.That(snapshot.Lines, Has.Count.EqualTo(3));
            Assert.That(snapshot.Lines, Is.EqualTo(TypeBeatEditorOperations.OrderedLines(beatmap)));
        }

        [Test]
        public void ALineReplacedThroughAnUpdateIsReordered()
        {
            var beatmap = map();
            var snapshot = EditorLineSnapshot.For(beatmap);
            var first = snapshot.Lines;
            var edited = first[0];
            int revision = snapshot.Revision;

            beatmap.BeginChange();
            edited.Line = new LyricLine
            {
                RawText = "later",
                StartTime = 7000,
                EndTime = 8000,
                SingEndTime = 7900,
                Units = new[] { new TimedUnit { Text = "later", StartTime = 7000, EndTime = 7900 } },
            };
            beatmap.Update(edited);
            beatmap.EndChange();

            Assert.That(snapshot.Lines[2], Is.SameAs(edited));
            Assert.That(snapshot.Lines, Is.Not.SameAs(first));
            Assert.That(snapshot.Revision, Is.GreaterThan(revision));
        }

        [Test]
        public void ALineIndexChangeReordersATie()
        {
            var beatmap = map();
            var snapshot = EditorLineSnapshot.For(beatmap);
            var lines = snapshot.Lines.ToArray();

            // Two lines at the same start are ordered by index, so swapping the indices swaps them.
            beatmap.BeginChange();
            lines[1].Line = lines[0].Line;
            beatmap.Update(lines[1]);
            beatmap.EndChange();
            Assert.That(snapshot.Lines.Take(2), Is.EqualTo(new[] { lines[0], lines[1] }));

            beatmap.BeginChange();
            (lines[0].LineIndex, lines[1].LineIndex) = (lines[1].LineIndex, lines[0].LineIndex);
            beatmap.Update(lines[0]);
            beatmap.Update(lines[1]);
            beatmap.EndChange();

            Assert.That(snapshot.Lines.Take(2), Is.EqualTo(new[] { lines[1], lines[0] }));
        }

        [Test]
        public void AddedAndRemovedLinesAreSeen()
        {
            var beatmap = map();
            var snapshot = EditorLineSnapshot.For(beatmap);
            var initial = snapshot.Lines.ToArray();

            var added = new TypeBeatHitObject
            {
                StartTime = -500,
                LineIndex = 3,
                Line = new LyricLine
                {
                    RawText = "early",
                    StartTime = -500,
                    EndTime = 0,
                    SingEndTime = -100,
                    Units = new[] { new TimedUnit { Text = "early", StartTime = -500, EndTime = -100 } },
                },
                Granularity = TimingGranularity.Word,
            };
            beatmap.Add(added);

            Assert.That(snapshot.Lines[0], Is.SameAs(added));

            beatmap.Remove(initial[1]);

            Assert.That(snapshot.Lines, Is.EqualTo(new[] { added, initial[0], initial[2] }));
        }

        [Test]
        public void AnUnchangedMapIsReadWithoutAllocating()
        {
            var beatmap = map();
            var snapshot = EditorLineSnapshot.For(beatmap);
            var first = snapshot.Lines;
            int revision = snapshot.Revision;

            // Warm the path before counting.
            for (int i = 0; i < 10; i++)
                _ = EditorLineSnapshot.For(beatmap).Lines;

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 1000; i++)
                _ = EditorLineSnapshot.For(beatmap).Lines;

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Lines, Is.SameAs(first));
                Assert.That(snapshot.Revision, Is.EqualTo(revision));
                Assert.That(allocated, Is.Zero, "an unchanged frame allocates nothing");
                Assert.That(first, Is.EqualTo(TypeBeatEditorOperations.OrderedLines(beatmap)));
            });
        }

        /// <summary>
        /// Every editor operation that replaces a line, run once on a fresh word-timed map. Each must
        /// reach the snapshot through the beatmap's change events, since the snapshot no longer
        /// compares the lines itself.
        /// </summary>
        private static IEnumerable<TestCaseData> lineEdits()
        {
            yield return edit("shift all times", (b, l) => TypeBeatEditorOperations.ShiftAllTimes(b, 250));
            yield return edit("line start", (b, l) => TypeBeatEditorOperations.SetLineStart(b, l[1], 3100));
            yield return edit("sung end", (b, l) => TypeBeatEditorOperations.SetSingEnd(b, l[0], 2700));
            yield return edit("word timing", (b, l) => TypeBeatEditorOperations.SetUnitTiming(b, l[0], 0, 1100, 1800));
            yield return edit("word group", (b, l) => TypeBeatEditorOperations.EditUnitGroup(b, l[0], new[] { 0, 1 }, new[] { 1000d, 1900d }, new[] { 1800d, 2800d }, -50,
                TypeBeatEditorOperations.UnitGroupEdit.ResizeEnd));
            yield return edit("line text", (b, l) => Assert.That(TypeBeatEditorOperations.SetLineText(b, l[0], "alpha beta gamma"), Is.True));
            yield return edit("word text", (b, l) => Assert.That(TypeBeatEditorOperations.SetWordText(b, l[0], 0, "alfa"), Is.True));
            yield return edit("add word", (b, l) => Assert.That(TypeBeatEditorOperations.AddWord(b, l[0], 0), Is.True));
            yield return edit("remove word", (b, l) => Assert.That(TypeBeatEditorOperations.RemoveWord(b, l[0], 1), Is.True));
            yield return edit("merge words", (b, l) => Assert.That(TypeBeatEditorOperations.MergeWords(b, l[0], 0), Is.True));
            yield return edit("split line", (b, l) => TypeBeatEditorOperations.SplitLine(b, l[0], 1));
            yield return edit("merge lines", (b, l) => TypeBeatEditorOperations.MergeWithNext(b, l[0]));
            yield return edit("add line", (b, l) => Assert.That(TypeBeatEditorOperations.AddLine(b, 2900), Is.Not.Null));
            yield return edit("delete line", (b, l) => TypeBeatEditorOperations.DeleteLine(b, l[1]));
            yield return edit("syllable boundary", (b, l) => Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(b, l[0], 0), Is.Not.Null));
            yield return edit("word pause", (b, l) => Assert.That(TypeBeatEditorOperations.InsertWordPause(b, l[0], 0, 1400), Is.Not.Null));
            yield return edit("paste line timings", (b, l) => TypeBeatEditorOperations.PasteLineTimings(b, new[] { l[1] }, TypeBeatEditorOperations.CopyLineTimings(new[] { l[0] })));
            yield return edit("paste line", (b, l) => TypeBeatEditorOperations.PasteLine(b, new[] { l[1] }, TypeBeatEditorOperations.CopyLineTimings(new[] { l[0] })));
            yield return edit("paste word timings", (b, l) => Assert.That(TypeBeatEditorOperations.PasteUnitTimings(b, l[1], 0, TypeBeatEditorOperations.CopyUnitTimings(l[0], new[] { 0, 1 })!),
                Is.True));

            static TestCaseData edit(string name, Action<EditorBeatmap, IReadOnlyList<TypeBeatHitObject>> apply) => new TestCaseData(apply).SetName("line edit refreshes the snapshot: " + name);
        }

        [TestCaseSource(nameof(lineEdits))]
        public void EveryLineEditRefreshesTheSnapshot(Action<EditorBeatmap, IReadOnlyList<TypeBeatHitObject>> apply)
        {
            var beatmap = wordTimedMap();
            var snapshot = EditorLineSnapshot.For(beatmap);
            var before = snapshot.Lines;
            var linesBefore = before.Select(h => h.Line).ToArray();
            int revision = snapshot.Revision;

            apply(beatmap, TypeBeatEditorOperations.OrderedLines(beatmap));

            var after = snapshot.Lines;

            Assert.Multiple(() =>
            {
                Assert.That(after.Select(h => h.Line), Is.Not.EqualTo(linesBefore), "the edit replaced a line");
                Assert.That(snapshot.Revision, Is.GreaterThan(revision));
                Assert.That(after, Is.Not.SameAs(before));
                Assert.That(after, Is.EqualTo(TypeBeatEditorOperations.OrderedLines(beatmap)));
            });
        }

        /// <summary>
        /// The other half of the contract: no editor code assigns a line except the one helper that
        /// also queues the update. A new edit that wrote <c>Line</c> directly would leave every
        /// display surface showing the old line.
        /// </summary>
        [Test]
        public void EditorCodeReplacesLinesOnlyThroughTheUpdatingHelper()
        {
            string editDir = Path.Combine(repositoryRoot(), "typebeat.Game.Rulesets.TypeBeat", "Edit");
            var assignment = new Regex(@"\.Line\s*=[^=>]");

            var found = Directory.EnumerateFiles(editDir, "*.cs")
                                 .SelectMany(file => File.ReadLines(file).Select(line => (File: Path.GetFileName(file), Line: line.Trim())))
                                 .Where(l => !l.Line.StartsWith("//", StringComparison.Ordinal) && assignment.IsMatch(l.Line))
                                 .Select(l => $"{l.File}: {l.Line}")
                                 .ToArray();

            Assert.That(found, Is.EqualTo(new[] { "TypeBeatEditorOperations.cs: hitObject.Line = line;" }));
        }

        [Test]
        public void RepeatedDisplayReadsAvoidWholeMapSortAllocations()
        {
            var beatmap = map(500);
            var snapshot = EditorLineSnapshot.For(beatmap);
            _ = snapshot.Lines;
            _ = TypeBeatEditorOperations.OrderedLines(beatmap);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                _ = TypeBeatEditorOperations.OrderedLines(beatmap);
            long sortingBytes = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                _ = EditorLineSnapshot.For(beatmap).Lines;
            long cachedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

            TestContext.WriteLine($"100 display reads, 500 lines: sorting {sortingBytes:N0} bytes, cached {cachedBytes:N0} bytes.");
            Assert.That(cachedBytes, Is.LessThan(sortingBytes / 10));
        }
    }
}
