// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Import;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JapaneseEditorAlignmentPerformanceTest
    {
        [TestCase("一人", "hitori", true)]
        [TestCase("一人で", "hitoride", true)]
        [TestCase("一人で", "ichininde", true)]
        [TestCase("一人", "hitoride", false)]
        [TestCase("東京", "toukyou", true)]
        [TestCase("地獄", "jigoku", true)]
        [TestCase("は", "wa", true)]
        [TestCase("へ", "e", true)]
        [TestCase("を", "o", true)]
        [TestCase("＆", "to", false)]
        [TestCase("２", "ni", false)]
        [TestCase("東京", "osaka", false)]
        public void ForwardDictionaryKeepsAlternateReadingsAndCompoundBoundaries(string surface, string reading, bool matches)
            => Assert.That(JapaneseLyricGenerator.HasDictionaryReading(surface, reading), Is.EqualTo(matches));

        [Test]
        public void RepeatedCandidateCutsReuseTheAlignmentIncludingUnmappableCuts()
        {
            for (int i = 0; i < 10; i++)
                for (int cut = 1; cut < "jigokugoku!".Length; cut++)
                    OriginalLyricAlignment.SourceCut("地獄国！", "jigokugoku!", cut, "japanese");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
                for (int cut = 1; cut < "jigokugoku!".Length; cut++)
                    OriginalLyricAlignment.SourceCut("地獄国！", "jigokugoku!", cut, "japanese");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.LessThan(1024), "querying a known word must not repeat dictionary parsing or allocate reading partitions");
        }

        [Test]
        public void RebuildingJapaneseOriginalDisplayDoesNotRepeatReadingSearches()
        {
            var unit = new TimedUnit
            {
                Text = "jigokugoku!", Original = "地獄国！", StartTime = 1000, EndTime = 2000,
                SyllableSplits = new[] { 2, 4, 6, 8 }, SyllableBoundaries = new[] { 1200d, 1400d, 1600d, 1800d },
            };
            var line = new LyricLine { RawText = unit.Text, Original = unit.Original, Units = new[] { unit }, StartTime = 1000, EndTime = 3000, SingEndTime = 2000 };
            for (int i = 0; i < 10; i++)
                TypeBeatEditorOperations.OriginalPipeDisplayText(line, "japanese");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
                TypeBeatEditorOperations.OriginalPipeDisplayText(line, "japanese");
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.LessThan(1024 * 1024), "only the display string and projection should be rebuilt");
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(line, "japanese"), Is.EqualTo("地|獄|国！"));
        }

        [Test]
        public void CachedReadingsKeepDifferentAuthoredSpellingsAndLanguagesIndependent()
        {
            Assert.That(OriginalLyricAlignment.SourceCut("しか", "shika", 3, "japanese"), Is.EqualTo(1));
            Assert.That(OriginalLyricAlignment.SourceCut("しか", "sika", 3, "japanese"), Is.Null);
            Assert.That(OriginalLyricAlignment.SourceCut("しか", "sika", 2, "japanese"), Is.EqualTo(1));
            Assert.That(OriginalLyricAlignment.SourceCut("日本", "nihon", 2, "japanese"), Is.EqualTo(1));
            Assert.That(OriginalLyricAlignment.SourceCut("日本", "nihon", 2, "english"), Is.Null);
            Assert.That(JapaneseCharacterReadings.Split("国", "ごく"), Is.EqualTo(new[] { "ごく" }));
            Assert.That(JapaneseCharacterReadings.Split("国", "こく"), Is.EqualTo(new[] { "こく" }));
            Assert.That(OriginalLyricAlignment.SourceCut("しか", "shika", 3, "japanese"), Is.EqualTo(1));
        }

        [Test]
        public void CachedAlignmentDoesNotKeepOldSubdivisionTimes()
        {
            TimedUnit unit(double time) => new TimedUnit
            {
                Text = "shika", Original = "しか", StartTime = 0, EndTime = 2000,
                SyllableSplits = new[] { 3 }, SyllableBoundaries = new[] { time },
            };
            Assert.That(OriginalLyricSubdivisions.Project(unit(700), "japanese").SyllableBoundaries, Is.EqualTo(new[] { 700d }));
            Assert.That(OriginalLyricSubdivisions.Project(unit(1200), "japanese").SyllableBoundaries, Is.EqualTo(new[] { 1200d }));
        }

        [Test]
        public void EditorAndDifficultyWorkersCanShareCharacterReadings()
        {
            Parallel.For(0, 16, _ =>
            {
                Assert.That(OriginalLyricAlignment.SourceCut("地獄国！", "jigokugoku!", 6, "japanese"), Is.EqualTo(2));
                Assert.That(OriginalLyricAlignment.SourceCut("地獄国！", "jigokugoku!", 4, "japanese"), Is.Null);
                Assert.That(string.Concat(JapaneseCharacterReadings.Split("地獄国", "じごくごく")), Is.EqualTo("じごくごく"));
            });
        }

        [Test]
        public void ConcurrentColdLookupsShareOneDictionarySearch()
        {
            var cache = new JapaneseTextCache<string, string>(2);
            int searches = 0;
            string lookup(string key)
            {
                Interlocked.Increment(ref searches);
                return key.ToUpperInvariant();
            }
            Parallel.For(0, 32, _ => Assert.That(cache.GetOrAdd("shared", lookup), Is.EqualTo("SHARED")));
            Assert.That(searches, Is.EqualTo(1));
        }

        [Test]
        public void TextCacheEvictsOldSongsWithoutDroppingRecentlyUsedReadings()
        {
            var cache = new JapaneseTextCache<string, int>(2);
            int searches = 0;
            int lookup(string _) => ++searches;
            Assert.That(cache.GetOrAdd("first", lookup), Is.EqualTo(1));
            Assert.That(cache.GetOrAdd("second", lookup), Is.EqualTo(2));
            Assert.That(cache.GetOrAdd("first", lookup), Is.EqualTo(1));
            Assert.That(cache.GetOrAdd("third", lookup), Is.EqualTo(3));
            Assert.That(cache.GetOrAdd("first", lookup), Is.EqualTo(1));
            Assert.That(cache.GetOrAdd("second", lookup), Is.EqualTo(4));
        }
    }
}
