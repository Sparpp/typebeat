// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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
        public void DirectLineReplacementRefreshesTheSharedOrderAndRevision()
        {
            var beatmap = map();
            var snapshot = EditorLineSnapshot.For(beatmap);
            var first = snapshot.Lines;
            var edited = first[0];
            int revision = snapshot.Revision;
            edited.Line = new LyricLine
            {
                RawText = "later",
                StartTime = 7000,
                EndTime = 8000,
                SingEndTime = 7900,
                Units = new[] { new TimedUnit { Text = "later", StartTime = 7000, EndTime = 7900 } },
            };

            Assert.That(snapshot.Lines[2], Is.SameAs(edited));
            Assert.That(snapshot.Lines, Is.Not.SameAs(first));
            Assert.That(snapshot.Revision, Is.GreaterThan(revision));
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
