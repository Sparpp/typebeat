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
    /// The WORD paste (backlog 343): a copied word carries its text and sub-word timing, the SAME
    /// word elsewhere takes that timing (a different word keeps the conservative span-only rule),
    /// the run is scaled into the room it has so no neighbour moves, and a paste with no word
    /// selected never lands on word zero (backlog 344 inserts it at the playhead instead). Every paste is checked to reopen
    /// exactly as it was left.
    /// </summary>
    [TestFixture]
    public class WordPasteTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        /// <summary>
        /// The owner's reproduction. Line 0 "eat apple now" [1000..5000]: "apple" 1500..2700 cut once
        /// at 1800, authored split after char 2 ("ap|ple"). Line 1 (the LAST line) "red apple pie"
        /// [5000..9000], interpolated: red 5000..5300, apple 5300..5800, pie 5800..6100.
        /// </summary>
        private static EditorBeatmap createBeatmap(TimingGranularity granularity = TimingGranularity.Syllable, bool subdivideSource = true)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Word";
            beatmap.Metadata.Title = "Paste";
            beatmap.Metadata.AudioFile = "audio.mp3";

            beatmap.HitObjects.Add(line(0, 1000, 5000, granularity,
                new TimedUnit { Text = "eat", StartTime = 1000, EndTime = 1400, Source = TimingSource.Explicit, Confidence = 1 },
                new TimedUnit
                {
                    Text = "apple", StartTime = 1500, EndTime = 2700, Source = TimingSource.Explicit, Confidence = 1,
                    SyllableBoundaries = subdivideSource ? new[] { 1800d } : System.Array.Empty<double>(),
                    SyllableSplits = subdivideSource ? new[] { 2 } : System.Array.Empty<int>(),
                },
                new TimedUnit { Text = "now", StartTime = 3000, EndTime = 3400, Source = TimingSource.Explicit, Confidence = 1 }));

            beatmap.HitObjects.Add(line(1, 5000, 9000, granularity,
                new TimedUnit { Text = "red", StartTime = 5000, EndTime = 5300, Source = TimingSource.Interpolated, Confidence = 0.5 },
                new TimedUnit { Text = "apple", StartTime = 5300, EndTime = 5800, Source = TimingSource.Interpolated, Confidence = 0.5 },
                new TimedUnit { Text = "pie", StartTime = 5800, EndTime = 6100, Source = TimingSource.Interpolated, Confidence = 0.5 }));

            return new EditorBeatmap(beatmap);
        }

        private static TypeBeatHitObject line(int index, double start, double end, TimingGranularity granularity, params TimedUnit[] units)
            => new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Granularity = granularity,
                Line = new LyricLine
                {
                    RawText = string.Join(' ', units.Select(u => u.Text)),
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = units[^1].EndTime,
                    Units = units,
                },
            };

        private static TypeBeatHitObject lineAt(EditorBeatmap editorBeatmap, int index) => TypeBeatEditorOperations.OrderedLines(editorBeatmap)[index];

        /// <summary>Copies line 0's "apple" through the string clipboard, as the editor does.</summary>
        private static LyricTimingClipboard.UnitTimingsPayload copyApple(EditorBeatmap editorBeatmap)
            => parse(LyricTimingClipboard.Serialize(TypeBeatEditorOperations.CopyUnitTimings(lineAt(editorBeatmap, 0), new[] { 1 })!));

        private static LyricTimingClipboard.UnitTimingsPayload parse(string json)
        {
            var (lines, units) = LyricTimingClipboard.TryParse(json);
            Assert.That(lines, Is.Null);
            Assert.That(units, Is.Not.Null);
            return units!;
        }

        private static (double, double) span(TimedUnit unit) => (unit.StartTime, unit.EndTime);

        [Test]
        public void TheCopiedWordCarriesItsTextAndSubWordTiming()
        {
            var payload = copyApple(createBeatmap());
            var apple = payload.Units.Single();

            Assert.Multiple(() =>
            {
                Assert.That(apple.Text, Is.EqualTo("apple"));
                Assert.That(apple.Chars, Is.EqualTo(5));
                Assert.That((apple.Start, apple.End), Is.EqualTo((0d, 1200d)));
                Assert.That(apple.Boundaries, Is.EqualTo(new[] { 300d }), "an offset from the run's anchor (1500)");
                Assert.That(apple.Splits, Is.EqualTo(new[] { 2 }));
            });
        }

        [Test]
        public void TheSameWordTakesTheSubdivisionScaledIntoItsRoomAndNoNeighbourMoves()
        {
            var editorBeatmap = createBeatmap();
            var payload = copyApple(editorBeatmap);

            Assert.That(TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, lineAt(editorBeatmap, 1), 1, payload), Is.True);

            var target = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                // The 1200 pattern has 500 of room (up to pie's start), so it is scaled by 5/12: the
                // cut 300 in lands 125 in.
                Assert.That(span(target.Units[1]), Is.EqualTo((5300d, 5800d)));
                Assert.That(target.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 5425d }));
                Assert.That(target.Units[1].SyllableSplits, Is.EqualTo(new[] { 2 }));
                Assert.That(target.Units[1].Source, Is.EqualTo(TimingSource.Explicit));
                Assert.That(target.Units[1].Text, Is.EqualTo("apple"));

                // Nothing else moved: red and pie keep their spans and their source.
                Assert.That(span(target.Units[0]), Is.EqualTo((5000d, 5300d)));
                Assert.That(span(target.Units[2]), Is.EqualTo((5800d, 6100d)));
                Assert.That(target.Units[0].Source, Is.EqualTo(TimingSource.Interpolated));
                Assert.That(target.Units[2].Source, Is.EqualTo(TimingSource.Interpolated));
                Assert.That((target.StartTime, target.EndTime, target.SingEndTime), Is.EqualTo((5000d, 9000d, 6100d)), "the last word did not move");
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void ADifferentWordIsRetimedConservatively()
        {
            var editorBeatmap = createBeatmap();
            var payload = copyApple(editorBeatmap);

            // "pie" is the last word of the LAST line: no wall on its right, so the pattern lands at
            // its own length and the line follows its end (backlog 336).
            Assert.That(TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, lineAt(editorBeatmap, 1), 2, payload), Is.True);

            var target = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(span(target.Units[2]), Is.EqualTo((5800d, 7000d)));
                Assert.That(target.Units[2].SyllableBoundaries, Is.Empty, "apple's cut does not land on pie");
                Assert.That(target.Units[2].SyllableSplits, Is.Empty);
                Assert.That(target.Units[2].Source, Is.EqualTo(TimingSource.Explicit));

                Assert.That(span(target.Units[0]), Is.EqualTo((5000d, 5300d)));
                Assert.That(span(target.Units[1]), Is.EqualTo((5300d, 5800d)));
                Assert.That(target.Units[1].SyllableBoundaries, Is.Empty);

                // The sung end follows the word; the typeable end keeps the tail the line carried
                // (9000 - 6100 = 2900, inside LAST_LINE_TAIL_MS).
                Assert.That(target.SingEndTime, Is.EqualTo(7000));
                Assert.That(target.EndTime, Is.EqualTo(7000 + 2900));
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void InARunOnlyTheWordThatMatchesByTextTakesItsSubWordTiming()
        {
            var editorBeatmap = createBeatmap();

            // The target's middle word differs only in case; "red" carries a cut of its own.
            var target = lineAt(editorBeatmap, 1);
            target.Line = new LyricLine
            {
                RawText = "red APPLE pie",
                StartTime = 5000,
                EndTime = 9000,
                SingEndTime = 6100,
                Units = new[]
                {
                    new TimedUnit { Text = "red", StartTime = 5000, EndTime = 5300, Source = TimingSource.Explicit, Confidence = 1, SyllableBoundaries = new[] { 5150d } },
                    new TimedUnit { Text = "APPLE", StartTime = 5300, EndTime = 5800, Source = TimingSource.Explicit, Confidence = 1 },
                    new TimedUnit { Text = "pie", StartTime = 5800, EndTime = 6100, Source = TimingSource.Explicit, Confidence = 1 },
                },
            };

            // eat 0..400, apple 500..1700 cut at 800 ("ap|ple"), now 2000..2400.
            var run = parse(LyricTimingClipboard.Serialize(TypeBeatEditorOperations.CopyUnitTimings(lineAt(editorBeatmap, 0), new[] { 0, 1, 2 })!));

            Assert.That(TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, target, 0, run), Is.True);

            var pasted = target.Line;

            Assert.Multiple(() =>
            {
                // "red" is not "eat": the span only, its own cut kept at its own time.
                Assert.That(span(pasted.Units[0]), Is.EqualTo((5000d, 5400d)));
                Assert.That(pasted.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 5150d }));

                // "APPLE" is "apple": the cut and the split travel, and the spelling stays the target's.
                Assert.That(span(pasted.Units[1]), Is.EqualTo((5500d, 6700d)));
                Assert.That(pasted.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 5800d }));
                Assert.That(pasted.Units[1].SyllableSplits, Is.EqualTo(new[] { 2 }));
                Assert.That(pasted.Units[1].Text, Is.EqualTo("APPLE"));

                // "pie" is not "now": the span only.
                Assert.That(span(pasted.Units[2]), Is.EqualTo((7000d, 7400d)));
                Assert.That(pasted.Units[2].SyllableBoundaries, Is.Empty);
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void AnOldPayloadWithoutTextPastesSpansOnly()
        {
            var editorBeatmap = createBeatmap();

            // What CopyUnitTimings wrote before backlog 343: spans, and a zero char count.
            var old = parse("{\"type\":\"typebeat-unit-timings\",\"units\":[{\"start\":0,\"end\":1200,\"chars\":0}]}");

            Assert.Multiple(() =>
            {
                Assert.That(old.Units[0].Text, Is.Null);
                Assert.That(TypeBeatEditorOperations.PasteWordsAtTime(editorBeatmap, lineAt(editorBeatmap, 1), 6500, old).Outcome,
                    Is.EqualTo(WordPasteOutcome.NoText), "no words to insert (backlog 344)");
            });

            Assert.That(TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, lineAt(editorBeatmap, 1), 1, old), Is.True);

            var target = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                // Still fitted into the room: pie is not crushed.
                Assert.That(span(target.Units[1]), Is.EqualTo((5300d, 5800d)));
                Assert.That(target.Units[1].SyllableBoundaries, Is.Empty);
                Assert.That(span(target.Units[2]), Is.EqualTo((5800d, 6100d)));
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void APasteAtTheLastWordOfTheLastLineGrowsTheLine()
        {
            var editorBeatmap = createBeatmap();
            var run = parse("{\"type\":\"typebeat-unit-timings\",\"units\":[{\"start\":0,\"end\":2600,\"chars\":3,\"text\":\"pie\"}]}");

            Assert.That(TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, lineAt(editorBeatmap, 1), 2, run), Is.True);

            var target = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(span(target.Units[2]), Is.EqualTo((5800d, 8400d)));
                Assert.That(target.SingEndTime, Is.EqualTo(8400));
                Assert.That(target.EndTime, Is.EqualTo(8400 + 2900), "the window grew past its old 9000 with its last word");
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void AtTheSongsEndTheLastLineIsFittedTooAndTheLineGrowsToTheWall()
        {
            var editorBeatmap = createBeatmap();

            // A song that ends before the line's old end never squeezes it, so the wall is 9000 and
            // pie has 3200 of room for a 4000 pattern: scaled by 0.8.
            TypeBeatEditorOperations.SetTrackLengthSource(editorBeatmap, () => 8000);
            var run = parse("{\"type\":\"typebeat-unit-timings\",\"units\":[{\"start\":0,\"end\":4000,\"chars\":3,\"text\":\"pie\",\"syllables\":[1000]}]}");

            Assert.That(TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, lineAt(editorBeatmap, 1), 2, run), Is.True);

            var target = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(span(target.Units[2]), Is.EqualTo((5800d, 9000d)));
                Assert.That(target.Units[2].SyllableBoundaries, Is.EqualTo(new[] { 6600d }));
                Assert.That((target.SingEndTime, target.EndTime), Is.EqualTo((9000d, 9000d)));
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void APastedSubdivisionOnAWordMapRaisesItToSyllableAndSurvivesReopen()
        {
            // A Word map with no cut anywhere, and an "apple" copied (with its cut) from another map.
            var editorBeatmap = createBeatmap(TimingGranularity.Word, subdivideSource: false);
            var run = parse("{\"type\":\"typebeat-unit-timings\",\"units\":[{\"start\":0,\"end\":400,\"chars\":5,\"text\":\"apple\",\"syllables\":[200],\"split_chars\":[2]}]}");

            Assert.That(TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, lineAt(editorBeatmap, 1), 1, run), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(lineAt(editorBeatmap, 1).Line.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 5500d }));
                Assert.That(TypeBeatEditorOperations.OrderedLines(editorBeatmap).Select(o => o.Granularity), Is.All.EqualTo(TimingGranularity.Syllable));
            });

            var reopened = assertReopensAsLeft(editorBeatmap);
            Assert.That(reopened[1].Line.Units[1].SyllableSplits, Is.EqualTo(new[] { 2 }));
        }

        /// <summary>
        /// THE RECONCILED NO-SELECTION DISPATCH (backlog 344). 343 landed a no-selection paste on the
        /// first word spelled like the copied one; 344 gives that gesture its own meaning, insertion at
        /// the playhead, and the text match is DROPPED rather than kept as a fallback. Pinned both
        /// ways: with the playhead inside the line the copy is inserted even though the line has its
        /// own "apple" (which is not touched), and with the playhead outside the line the paste is
        /// refused even though that "apple" is there to match.
        /// </summary>
        [Test]
        public void WithNoWordSelectedTheCopyIsInsertedAndTheMatchingWordIsNotRetimed()
        {
            var editorBeatmap = createBeatmap();
            var apple = copyApple(editorBeatmap);
            var target = lineAt(editorBeatmap, 1);
            var before = target.Line;

            var outside = TypeBeatEditorOperations.PasteWords(editorBeatmap, target, -1, 4000, apple);

            Assert.Multiple(() =>
            {
                Assert.That(outside.Outcome, Is.EqualTo(WordPasteOutcome.OutsideLine), "no text match to fall back on");
                Assert.That(target.Line, Is.SameAs(before));
            });

            var inside = TypeBeatEditorOperations.PasteWords(editorBeatmap, target, -1, 6500, apple);
            var pasted = target.Line;

            Assert.Multiple(() =>
            {
                Assert.That(inside, Is.EqualTo(new WordPasteResult(WordPasteOutcome.Inserted, 3, 1)));
                Assert.That(pasted.RawText, Is.EqualTo("red apple pie apple"));
                Assert.That(span(pasted.Units[1]), Is.EqualTo((5300d, 5800d)), "the line's own apple was not re-timed");
                Assert.That(pasted.Units[1].SyllableBoundaries, Is.Empty);
                Assert.That(pasted.Units[1].Source, Is.EqualTo(TimingSource.Interpolated));
                Assert.That(span(pasted.Units[3]), Is.EqualTo((6500d, 7700d)));
            });
        }

        [Test]
        public void ASelectedWordIsRetimedAndAStaleSelectionIsNoSelection()
        {
            var editorBeatmap = createBeatmap();
            var apple = copyApple(editorBeatmap);
            var target = lineAt(editorBeatmap, 1);

            Assert.That(TypeBeatEditorOperations.PasteWords(editorBeatmap, target, 2, 4000, apple), Is.EqualTo(new WordPasteResult(WordPasteOutcome.Retimed, 2, 1)),
                "a selected word wins, wherever the playhead is");
            Assert.That(target.Line.RawText, Is.EqualTo("red apple pie"));

            Assert.That(TypeBeatEditorOperations.PasteWords(editorBeatmap, target, 7, 4000, apple).Outcome, Is.EqualTo(WordPasteOutcome.OutsideLine),
                "an index naming no word is no selection: the insertion arm, which refuses outside the line");
        }

        [Test]
        public void ARunWithNoRoomIsRefused()
        {
            var editorBeatmap = createBeatmap();
            var target = lineAt(editorBeatmap, 1);

            // A zero-width "apple" butted against "pie": nowhere to lay anything down.
            target.Line = new LyricLine
            {
                RawText = "red apple pie",
                StartTime = 5000,
                EndTime = 9000,
                SingEndTime = 6100,
                Units = new[]
                {
                    new TimedUnit { Text = "red", StartTime = 5000, EndTime = 5800, Source = TimingSource.Explicit, Confidence = 1 },
                    new TimedUnit { Text = "apple", StartTime = 5800, EndTime = 5800, Source = TimingSource.Explicit, Confidence = 1 },
                    new TimedUnit { Text = "pie", StartTime = 5800, EndTime = 6100, Source = TimingSource.Explicit, Confidence = 1 },
                },
            };

            var before = target.Line;

            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, target, 1, copyApple(editorBeatmap)), Is.False);
                Assert.That(target.Line, Is.SameAs(before));
            });
        }

        [Test]
        public void TheWordPasteIsOneUndoStep()
        {
            var editorBeatmap = createBeatmap();
            var changeHandler = new RulesetBeatmapChangeHandler(editorBeatmap, new TypeBeatRuleset());

            TypeBeatEditorOperations.PasteUnitTimings(editorBeatmap, lineAt(editorBeatmap, 1), 1, copyApple(editorBeatmap));
            Assert.That(lineAt(editorBeatmap, 1).Line.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 5425d }));

            changeHandler.RestoreState(-1);

            Assert.Multiple(() =>
            {
                Assert.That(lineAt(editorBeatmap, 1).Line.Units[1].SyllableBoundaries, Is.Empty);
                Assert.That(span(lineAt(editorBeatmap, 1).Line.Units[1]), Is.EqualTo((5300d, 5800d)));
            });
        }

        /// <summary>
        /// Saves and reopens the map, and checks every line comes back exactly as the editor holds
        /// it: window, sung end, and each word's text, span, cuts, split and rests.
        /// </summary>
        private static System.Collections.Generic.List<TypeBeatHitObject> assertReopensAsLeft(EditorBeatmap editorBeatmap)
        {
            var sb = new System.Text.StringBuilder();

            using (var writer = new System.IO.StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(editorBeatmap, writer);

            var reopened = SyllableSplitTest.DecodeOsu(sb.ToString());
            var held = TypeBeatEditorOperations.OrderedLines(editorBeatmap);

            Assert.That(reopened, Has.Count.EqualTo(held.Count));

            for (int i = 0; i < held.Count; i++)
            {
                var a = held[i].Line;
                var b = reopened[i].Line;

                Assert.Multiple(() =>
                {
                    Assert.That((b.RawText, b.StartTime, b.EndTime, b.SingEndTime), Is.EqualTo((a.RawText, a.StartTime, a.EndTime, a.SingEndTime)), $"line {i}");
                    Assert.That(b.Units.Select(u => (u.Text, u.StartTime, u.EndTime)), Is.EqualTo(a.Units.Select(u => (u.Text, u.StartTime, u.EndTime))), $"line {i} words");
                    Assert.That(b.Units.Select(u => string.Join(",", u.SyllableBoundaries)), Is.EqualTo(a.Units.Select(u => string.Join(",", u.SyllableBoundaries))), $"line {i} cuts");
                    Assert.That(b.Units.Select(u => string.Join(",", u.SyllableSplits)), Is.EqualTo(a.Units.Select(u => string.Join(",", u.SyllableSplits))), $"line {i} splits");
                    Assert.That(b.Units.Select(u => u.Pauses.Count), Is.EqualTo(a.Units.Select(u => u.Pauses.Count)), $"line {i} rests");
                    Assert.That(reopened[i].Granularity, Is.EqualTo(held[i].Granularity), $"line {i} granularity");
                });
            }

            return reopened;
        }
    }
}
