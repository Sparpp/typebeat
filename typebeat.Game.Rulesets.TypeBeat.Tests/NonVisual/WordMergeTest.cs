// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class WordMergeTest
    {
        [TestCase(0)]
        [TestCase(200)]
        public void MergePreservesCharacterTimingSubdivisionsPausesAndOriginals(double gap)
        {
            var left = new TimedUnit
            {
                Text = "konnichi", Original = "こんにち", StartTime = 1000, EndTime = 4000,
                SyllableBoundaries = new[] { 1800d }, SyllableSplits = new[] { 3 },
                Pauses = new[] { new WordPause(2400, 2800, 5) }, Confidence = 0.9,
            };
            var right = new TimedUnit
            {
                Text = "sekai", Original = "せかい", StartTime = 4000 + gap, EndTime = 8000,
                SyllableBoundaries = new[] { 6000d }, SyllableSplits = new[] { 2 },
                Pauses = new[] { new WordPause(6600, 7000, 3) }, Confidence = 0.7,
            };
            var map = create(left, right);
            var hitObject = first(map);
            var before = hitObject.Line;
            var targets = TypingLine.CellTargetsFor(left, left.Text.Length).Concat(TypingLine.CellTargetsFor(right, right.Text.Length)).ToArray();

            Assert.That(TypeBeatEditorOperations.MergeWords(map, hitObject, 0), Is.True);
            var merged = hitObject.Line.Units.Single();
            Assert.That(hitObject.Line.RawText, Is.EqualTo("konnichisekai"));
            Assert.That(merged.Original, Is.EqualTo("こんにちせかい"));
            Assert.That(hitObject.Line.Original, Is.EqualTo("こんにちせかい"));
            Assert.That((merged.StartTime, merged.EndTime), Is.EqualTo((1000d, 8000d)));
            Assert.That(merged.Source, Is.EqualTo(TimingSource.Explicit));
            Assert.That(merged.Confidence, Is.EqualTo(0.7));
            Assert.That(hitObject.Line.EndTime, Is.EqualTo(before.EndTime));
            Assert.That(hitObject.Line.SingEndTime, Is.EqualTo(before.SingEndTime));
            Assert.That(TypingLine.CellTargetsFor(merged, merged.Text.Length), Is.EqualTo(targets).Within(0.000001));
            Assert.That(PolyglotLine.Derive(hitObject.Line, "japanese").Line.RawText, Is.EqualTo("こんにちせかい"));

            var loaded = OriginalTextFormatTest.Decode(OriginalTextFormatTest.Encode(map)).HitObjects.OfType<TypeBeatHitObject>().Single();
            Assert.That(loaded.Line.RawText, Is.EqualTo(hitObject.Line.RawText));
            Assert.That(loaded.Line.Original, Is.EqualTo(hitObject.Line.Original));
            Assert.That(loaded.Line.Units.Single().Original, Is.EqualTo(merged.Original));
            Assert.That(loaded.Line.Units.Single().SyllableBoundaries, Is.EqualTo(merged.SyllableBoundaries));
            Assert.That(loaded.Line.Units.Single().SyllableSplits, Is.EqualTo(merged.SyllableSplits));
            Assert.That(loaded.Line.Units.Single().Pauses, Is.EqualTo(merged.Pauses));
            Assert.That(TypingLine.CellTargetsFor(loaded.Line.Units.Single(), merged.Text.Length), Is.EqualTo(targets).Within(0.000001));
        }

        [TestCase(0)]
        [TestCase(200)]
        public void PlainWordsKeepTheirOriginalCharacterTargets(double gap)
        {
            var left = unit("hello", 1000, 2000);
            var right = unit("world", 2000 + gap, 4000);
            var map = create(left, right);
            var targets = TypingLine.CellTargetsFor(left, 5).Concat(TypingLine.CellTargetsFor(right, 5)).ToArray();
            Assert.That(TypeBeatEditorOperations.MergeWords(map, first(map), 0), Is.True);
            var merged = first(map).Line.Units.Single();
            Assert.That(merged.Original, Is.Null);
            Assert.That(TypingLine.CellTargetsFor(merged, 10), Is.EqualTo(targets).Within(0.000001));
        }

        [Test]
        public void RepeatedMergesKeepAllThreeWordOnsets()
        {
            var map = create(unit("we", 1000, 1500), unit("sing", 1600, 3000), unit("now", 3000, 4000));
            var targets = first(map).Line.Units.SelectMany(u => TypingLine.CellTargetsFor(u, u.Text.Length)).ToArray();
            Assert.That(TypeBeatEditorOperations.MergeWords(map, first(map), 1), Is.True);
            Assert.That(TypeBeatEditorOperations.MergeWords(map, first(map), 0), Is.True);
            Assert.That(first(map).Line.RawText, Is.EqualTo("wesingnow"));
            Assert.That(TypingLine.CellTargetsFor(first(map).Line.Units.Single(), 9), Is.EqualTo(targets).Within(0.000001));
        }

        [Test]
        public void SelectionMergeKeepsGapsTouchingSeamsOriginalsAndUnselectedWords()
        {
            var words = new[]
            {
                unit("before", 0, 500), unit("we", 1000, 1500, "私"),
                unit("sing", 1600, 3000, "歌う"), unit("now", 3000, 4000, "今"), unit("after", 4500, 5000),
            };
            var map = create(words);
            var hitObject = first(map);
            var before = hitObject.Line;
            var targets = words.Skip(1).Take(3).SelectMany(u => TypingLine.CellTargetsFor(u, u.Text.Length)).ToArray();

            Assert.That(TypeBeatEditorOperations.MergeSelectedWords(map, hitObject, new[] { 3, 2, 1, 2 }), Is.EqualTo(new[] { 1 }));

            var merged = hitObject.Line.Units[1];
            Assert.Multiple(() =>
            {
                Assert.That(hitObject.Line.RawText, Is.EqualTo("before wesingnow after"));
                Assert.That(hitObject.Line.Units[0], Is.SameAs(words[0]));
                Assert.That(hitObject.Line.Units[2], Is.SameAs(words[4]));
                Assert.That(merged.Pauses, Is.EqualTo(new[] { new WordPause(1500, 1600, 2) }));
                Assert.That(merged.SyllableBoundaries, Is.EqualTo(new[] { 3000d }));
                Assert.That(merged.SyllableSplits, Is.EqualTo(new[] { 6 }));
                Assert.That(merged.Original, Is.EqualTo("私歌う今"));
                Assert.That(hitObject.Line.Original, Is.EqualTo("before 私歌う今 after"));
                Assert.That((hitObject.Line.StartTime, hitObject.Line.SingEndTime, hitObject.Line.EndTime),
                    Is.EqualTo((before.StartTime, before.SingEndTime, before.EndTime)));
                Assert.That(TypingLine.CellTargetsFor(merged, 9), Is.EqualTo(targets).Within(0.000001));
            });

            var loaded = OriginalTextFormatTest.Decode(OriginalTextFormatTest.Encode(map)).HitObjects.OfType<TypeBeatHitObject>().Single();
            Assert.That(loaded.Line.Units[1].Pauses, Is.EqualTo(merged.Pauses));
            Assert.That(loaded.Line.Units[1].SyllableSplits, Is.EqualTo(merged.SyllableSplits));
            Assert.That(TypingLine.CellTargetsFor(loaded.Line.Units[1], 9), Is.EqualTo(targets).Within(0.000001));
        }

        [Test]
        public void DisjointSelectedRunsLeaveInterveningAndIsolatedWordsAlone()
        {
            var words = Enumerable.Range(0, 7).Select(i => unit("word" + i, i * 1000, (i + 1) * 1000)).ToArray();
            var map = create(words);

            Assert.That(TypeBeatEditorOperations.MergeSelectedWords(map, first(map), new[] { 0, 1, 3, 4, 6 }), Is.EqualTo(new[] { 0, 2, 4 }));
            Assert.That(first(map).Line.RawText, Is.EqualTo("word0word1 word2 word3word4 word5 word6"));
            Assert.That(first(map).Line.Units[1], Is.SameAs(words[2]));
            Assert.That(first(map).Line.Units[3], Is.SameAs(words[5]));
            Assert.That(first(map).Line.Units[4], Is.SameAs(words[6]));
        }

        [Test]
        public void ARefusedSeamLeavesTheEntireSelectionUnchanged()
        {
            var map = create(unit("hello", 1000, 2500), unit("world", 2000, 3000), unit("now", 3000, 4000));
            var before = first(map).Line;

            Assert.That(TypeBeatEditorOperations.MergeSelectedWords(map, first(map), new[] { 0, 1, 2 }), Is.Empty);
            Assert.That(first(map).Line, Is.SameAs(before), "the valid right join is not committed when the left join overlaps");
        }

        [Test]
        public void ASelectionWithNoAdjacentPairDoesNotChangeTheMap()
        {
            var map = create(unit("hello", 1000, 2000), unit("world", 2000, 3000), unit("now", 3000, 4000));
            var before = first(map).Line;

            foreach (var selection in new[] { Array.Empty<int>(), new[] { 1 }, new[] { 0, 2 }, new[] { -1, 0 }, new[] { 0, 3 } })
                Assert.That(TypeBeatEditorOperations.MergeSelectedWords(map, first(map), selection), Is.Empty);

            Assert.That(first(map).Line, Is.SameAs(before));
        }

        [Test]
        public void OverlappingWordsAreNotMergedIntoAmbiguousTiming()
        {
            var map = create(unit("hello", 1000, 2500), unit("world", 2000, 4000));
            var before = first(map).Line;
            Assert.That(TypeBeatEditorOperations.MergeWords(map, first(map), 0), Is.False);
            Assert.That(first(map).Line, Is.SameAs(before));
        }

        [Test]
        public void OriginalOnOnlyOneSideKeepsTheOtherSpellingAndFollowingPendingWord()
        {
            var map = create(unit("hello", 1000, 2000), unit("sekai", 2000, 4000, "せかい"),
                unit("now", 4500, 5000));
            var hitObject = first(map);
            hitObject.Line = new LyricLine
            {
                RawText = hitObject.Line.RawText, StartTime = 1000, EndTime = 6000, SingEndTime = 5000,
                Original = "independently authored caption", Units = hitObject.Line.Units,
                UnromanisedWords = new[] { new UnromanisedWord(2, "君", 4000, 4500) },
            };
            var untouched = hitObject.Line.Units[2];
            Assert.That(TypeBeatEditorOperations.MergeWords(map, hitObject, 0), Is.True);
            Assert.That(hitObject.Line.Units[0].Original, Is.EqualTo("helloせかい"));
            Assert.That(hitObject.Line.Units[1], Is.SameAs(untouched));
            Assert.That(hitObject.Line.Original, Is.EqualTo("independently authored caption"));
            Assert.That(hitObject.Line.UnromanisedWords.Single().Position, Is.EqualTo(1));
        }

        [TestCase(-1)]
        [TestCase(1)]
        [TestCase(2)]
        public void InvalidIndicesDoNotChangeTheMap(int index)
        {
            var map = create(unit("hello", 1000, 2000), unit("world", 2000, 4000));
            var line = first(map).Line;
            Assert.That(TypeBeatEditorOperations.MergeWords(map, first(map), index), Is.False);
            Assert.That(first(map).Line, Is.SameAs(line));
        }

        [Test]
        public void UnromanisedWordBetweenTheTwoIsNotSilentlySkipped()
        {
            var map = create(unit("hello", 1000, 2000), unit("world", 2500, 4000));
            var hitObject = first(map);
            hitObject.Line = new LyricLine
            {
                RawText = "hello world", StartTime = 1000, EndTime = 5000, SingEndTime = 4000,
                Units = hitObject.Line.Units,
                UnromanisedWords = new[] { new UnromanisedWord(1, "君", 2000, 2500) },
            };
            var before = hitObject.Line;
            Assert.That(TypeBeatEditorOperations.MergeWords(map, hitObject, 0), Is.False);
            Assert.That(hitObject.Line, Is.SameAs(before));
        }

        private static TimedUnit unit(string text, double start, double end, string? original = null)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end, Original = original };

        private static EditorBeatmap create(params TimedUnit[] units)
        {
            var line = new LyricLine
            {
                RawText = string.Join(' ', units.Select(u => u.Text)), StartTime = units[0].StartTime,
                EndTime = units[^1].EndTime + 1000, SingEndTime = units[^1].EndTime, Units = units,
                Original = units.Any(u => u.Original != null) ? TypeBeatEditorOperations.JoinedOriginal(units, Array.Empty<UnromanisedWord>()) : null,
            };
            return new EditorBeatmap(OriginalTextFormatTest.BuildBeatmap(new[] { line }));
        }

        private static TypeBeatHitObject first(EditorBeatmap map) => map.HitObjects.OfType<TypeBeatHitObject>().Single();
    }
}
