// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The word paste with NO word selected (backlog 344): the copied words are INSERTED at the
    /// playhead as new words, text, spans, sub-word timing and originals together. No existing word
    /// moves; a run longer than its room is scaled into it; under <see cref="TypeBeatEditorOperations.MIN_SPAN_MS"/>
    /// per word of room, outside the line, on a line-timed map or from a payload with no text it is
    /// refused and changes nothing. Every landed paste is checked to reopen exactly as it was left.
    /// </summary>
    [TestFixture]
    public class PasteInsertTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        /// <summary>
        /// The owner's fixture. Line 0 "eat apple now" [1000..5000]: eat 1000..1400, apple 1500..2700
        /// cut once at 1800 ("ap|ple", authored), now 3000..3400. Line 1 (the LAST line) "red green
        /// pie" [5000..9000]: red 5000..5300, green 5400..5900, pie 6000..6300.
        /// </summary>
        private static EditorBeatmap createBeatmap(TimingGranularity granularity = TimingGranularity.Syllable, string? appleOriginal = null)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Paste";
            beatmap.Metadata.Title = "Insert";
            beatmap.Metadata.AudioFile = "audio.mp3";

            bool cut = granularity == TimingGranularity.Syllable;

            beatmap.HitObjects.Add(line(0, 1000, 5000, granularity,
                unit("eat", 1000, 1400),
                new TimedUnit
                {
                    Text = "apple", Original = appleOriginal, StartTime = 1500, EndTime = 2700, Source = TimingSource.Explicit, Confidence = 1,
                    SyllableBoundaries = cut ? new[] { 1800d } : Array.Empty<double>(),
                    SyllableSplits = cut ? new[] { 2 } : Array.Empty<int>(),
                },
                unit("now", 3000, 3400)));

            beatmap.HitObjects.Add(line(1, 5000, 9000, granularity, unit("red", 5000, 5300), unit("green", 5400, 5900), unit("pie", 6000, 6300)));

            return new EditorBeatmap(beatmap);
        }

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end, Source = TimingSource.Explicit, Confidence = 1 };

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

        /// <summary>Copies words of line <paramref name="from"/> through the string clipboard, as the editor does.</summary>
        private static LyricTimingClipboard.UnitTimingsPayload copy(EditorBeatmap editorBeatmap, int from, params int[] indices)
        {
            var (lines, units) = LyricTimingClipboard.TryParse(LyricTimingClipboard.Serialize(TypeBeatEditorOperations.CopyUnitTimings(lineAt(editorBeatmap, from), indices)!));
            Assert.That(lines, Is.Null);
            return units!;
        }

        private static (double, double) span(TimedUnit unit) => (unit.StartTime, unit.EndTime);

        private static WordPasteResult insert(EditorBeatmap editorBeatmap, double time, LyricTimingClipboard.UnitTimingsPayload payload)
            => TypeBeatEditorOperations.PasteWordsAtTime(editorBeatmap, lineAt(editorBeatmap, 1), time, payload);

        [Test]
        public void InTheGapAfterAWordTheRunStartsAtThePlayheadAndIsScaledIntoTheGap()
        {
            var editorBeatmap = createBeatmap();

            Assert.That(insert(editorBeatmap, 5350, copy(editorBeatmap, 0, 1)), Is.EqualTo(new WordPasteResult(WordPasteOutcome.Inserted, 1, 1)));

            var target = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.RawText, Is.EqualTo("red apple green pie"));

                // 50 of room (up to green) for a 1200 pattern: scaled by 1/24, the cut 300 in lands 12.5 in.
                Assert.That(span(target.Units[1]), Is.EqualTo((5350d, 5400d)));
                Assert.That(target.Units[1].Text, Is.EqualTo("apple"));
                Assert.That(target.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 5362.5 }));
                Assert.That(target.Units[1].SyllableSplits, Is.EqualTo(new[] { 2 }));
                Assert.That(target.Units[1].Source, Is.EqualTo(TimingSource.Explicit));

                // No other word moved, and the last word did not, so neither did the line's ends.
                Assert.That(span(target.Units[0]), Is.EqualTo((5000d, 5300d)));
                Assert.That(span(target.Units[2]), Is.EqualTo((5400d, 5900d)));
                Assert.That(span(target.Units[3]), Is.EqualTo((6000d, 6300d)));
                Assert.That((target.StartTime, target.EndTime, target.SingEndTime), Is.EqualTo((5000d, 9000d, 6300d)));
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void InsideAWordTheRunGoesInAfterItAndNeverSplitsIt()
        {
            var editorBeatmap = createBeatmap();

            Assert.That(insert(editorBeatmap, 5600, copy(editorBeatmap, 0, 1)), Is.EqualTo(new WordPasteResult(WordPasteOutcome.Inserted, 2, 1)));

            var target = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.RawText, Is.EqualTo("red green apple pie"));
                Assert.That(span(target.Units[1]), Is.EqualTo((5400d, 5900d)), "green is whole");

                // From green's end to pie's start: 100 of room, scaled by 1/12, the cut lands 25 in.
                Assert.That(span(target.Units[2]), Is.EqualTo((5900d, 6000d)));
                Assert.That(target.Units[2].SyllableBoundaries, Is.EqualTo(new[] { 5925d }));
                Assert.That(span(target.Units[3]), Is.EqualTo((6000d, 6300d)));
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void AtTheTailOfTheLastLineTheRunLandsFullLengthAndTheLineGrows()
        {
            var editorBeatmap = createBeatmap();

            Assert.That(insert(editorBeatmap, 6500, copy(editorBeatmap, 0, 1)), Is.EqualTo(new WordPasteResult(WordPasteOutcome.Inserted, 3, 1)));

            var target = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.RawText, Is.EqualTo("red green pie apple"));
                Assert.That(span(target.Units[3]), Is.EqualTo((6500d, 7700d)));
                Assert.That(target.Units[3].SyllableBoundaries, Is.EqualTo(new[] { 6800d }));

                // The sung end follows the new last word, and the window keeps the tail it carried
                // (9000 - 6300 = 2700, inside LAST_LINE_TAIL_MS): it grew past its old 9000.
                Assert.That(target.SingEndTime, Is.EqualTo(7700));
                Assert.That(target.EndTime, Is.EqualTo(7700 + 2700));
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void AtTheTailOfAnInteriorLineTheNextLineIsTheWall()
        {
            var editorBeatmap = createBeatmap();

            // Line 0 ends where line 1 starts (5000): 1000 of room from 4000 for the 1200 apple.
            Assert.That(TypeBeatEditorOperations.PasteWordsAtTime(editorBeatmap, lineAt(editorBeatmap, 0), 4000, copy(editorBeatmap, 0, 1)).Outcome,
                Is.EqualTo(WordPasteOutcome.Inserted));

            var target = lineAt(editorBeatmap, 0).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.RawText, Is.EqualTo("eat apple now apple"));
                Assert.That(span(target.Units[3]), Is.EqualTo((4000d, 5000d)));
                Assert.That(target.Units[3].SyllableBoundaries, Is.EqualTo(new[] { 4250d }));
                Assert.That((target.EndTime, target.SingEndTime), Is.EqualTo((5000d, 5000d)));
                Assert.That(lineAt(editorBeatmap, 1).Line.StartTime, Is.EqualTo(5000), "the next line did not move");
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void AMultiWordRunKeepsItsRhythm()
        {
            var editorBeatmap = createBeatmap();

            // eat 0..400, apple 500..1700 cut at 800.
            Assert.That(insert(editorBeatmap, 6500, copy(editorBeatmap, 0, 0, 1)), Is.EqualTo(new WordPasteResult(WordPasteOutcome.Inserted, 3, 2)));

            var target = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.RawText, Is.EqualTo("red green pie eat apple"));
                Assert.That(span(target.Units[3]), Is.EqualTo((6500d, 6900d)));
                Assert.That(span(target.Units[4]), Is.EqualTo((7000d, 8200d)));
                Assert.That(target.Units[4].SyllableBoundaries, Is.EqualTo(new[] { 7300d }));
                Assert.That(target.SingEndTime, Is.EqualTo(8200));
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void AScaledMultiWordRunKeepsItsProportions()
        {
            var editorBeatmap = createBeatmap();

            // Three words need at least 15ms, so 14ms of room is refused. At 5315 there is
            // 85ms of room for two words. The run eat+apple is 1700 long, so
            // eat 0..400 lands 0..20 and apple 500..1700 lands 25..85.
            Assert.That(insert(editorBeatmap, 5386, copy(editorBeatmap, 0, 0, 1, 2)).Outcome, Is.EqualTo(WordPasteOutcome.InsertNoRoom));
            Assert.That(insert(editorBeatmap, 5315, copy(editorBeatmap, 0, 0, 1)).Outcome, Is.EqualTo(WordPasteOutcome.Inserted));

            var target = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.RawText, Is.EqualTo("red eat apple green pie"));
                Assert.That(span(target.Units[1]), Is.EqualTo((5315d, 5335d)));
                Assert.That(span(target.Units[2]), Is.EqualTo((5340d, 5400d)));
                Assert.That(span(target.Units[3]), Is.EqualTo((5400d, 5900d)));
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void AtTheHeadOfTheLineTheRunGoesInFrontOfTheFirstWord()
        {
            var editorBeatmap = createBeatmap();
            var target = lineAt(editorBeatmap, 1);

            // Give the line a head: red starts 200 after the line does.
            target.Line = new LyricLine
            {
                RawText = "red green pie", StartTime = 5000, EndTime = 9000, SingEndTime = 6300,
                Units = new[] { unit("red", 5200, 5300), unit("green", 5400, 5900), unit("pie", 6000, 6300) },
            };

            Assert.That(insert(editorBeatmap, 5000, copy(editorBeatmap, 0, 0)), Is.EqualTo(new WordPasteResult(WordPasteOutcome.Inserted, 0, 1)));

            Assert.Multiple(() =>
            {
                Assert.That(target.Line.RawText, Is.EqualTo("eat red green pie"));
                Assert.That(span(target.Line.Units[0]), Is.EqualTo((5000d, 5200d)), "400 scaled into the 200 before red");
                Assert.That(span(target.Line.Units[1]), Is.EqualTo((5200d, 5300d)));
            });

            assertReopensAsLeft(editorBeatmap);
        }

        [Test]
        public void TheInsertedWordsKeepTheirOriginals()
        {
            var editorBeatmap = createBeatmap(appleOriginal: "りんご");

            Assert.That(insert(editorBeatmap, 6500, copy(editorBeatmap, 0, 0, 1)).Outcome, Is.EqualTo(WordPasteOutcome.Inserted));

            var target = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.Units[3].Original, Is.Null, "eat had none");
                Assert.That(target.Units[4].Original, Is.EqualTo("りんご"));
                Assert.That(target.Original, Is.Null, "the line's caption is not rebuilt by a word edit");
            });

            var reopened = assertReopensAsLeft(editorBeatmap);
            Assert.That(reopened[1].Line.Units.Select(u => u.Original), Is.EqualTo(new[] { null, null, null, null, "りんご" }));
        }

        [Test]
        public void AnUnromanisedWordKeepsItsPlaceAroundTheRun()
        {
            var editorBeatmap = createBeatmap();
            var target = lineAt(editorBeatmap, 1);

            // One unromanised word sung 6350..6450, after pie (position 3).
            target.Line = new LyricLine
            {
                RawText = target.Line.RawText, StartTime = 5000, EndTime = 9000, SingEndTime = 6300, Units = target.Line.Units,
                UnromanisedWords = new[] { new UnromanisedWord(3, "林檎", 6350, 6450) },
            };

            // A run going in at 6500 is behind it: it keeps its place, in front of the run.
            Assert.That(insert(editorBeatmap, 6500, copy(editorBeatmap, 0, 0)).Outcome, Is.EqualTo(WordPasteOutcome.Inserted));
            Assert.That(target.Line.UnromanisedWords.Single().Position, Is.EqualTo(3));

            // A run going in before it pushes it back by the run's length.
            Assert.That(insert(editorBeatmap, 5350, copy(editorBeatmap, 0, 0)).Outcome, Is.EqualTo(WordPasteOutcome.Inserted));
            Assert.Multiple(() =>
            {
                Assert.That(target.Line.RawText, Is.EqualTo("red eat green pie eat"));
                Assert.That(target.Line.UnromanisedWords.Single().Position, Is.EqualTo(4));
            });
        }

        [TestCase(4000, WordPasteOutcome.OutsideLine, TestName = "Refused before the line")]
        [TestCase(5396, WordPasteOutcome.InsertNoRoom, TestName = "Refused with 4 of room")]
        [TestCase(5300, WordPasteOutcome.InsertNoRoom, TestName = "Refused on a word start in a packed run")]
        public void ARefusalChangesNothingAndLeavesNoUndoStep(double time, WordPasteOutcome expected)
        {
            var editorBeatmap = createBeatmap();
            var target = lineAt(editorBeatmap, 1);

            // red ends 40 short of green, so at 5396 there are 4 of room. For 5300 the words are
            // packed edge to edge: the playhead is on green's start, so the run would go after green,
            // where pie starts at once.
            target.Line = new LyricLine
            {
                RawText = "red green pie", StartTime = 5000, EndTime = 9000, SingEndTime = 6300,
                Units = time == 5300
                    ? new[] { unit("red", 5000, 5300), unit("green", 5300, 6000), unit("pie", 6000, 6300) }
                    : new[] { unit("red", 5000, 5360), unit("green", 5400, 5900), unit("pie", 6000, 6300) },
            };

            var changeHandler = new RulesetBeatmapChangeHandler(editorBeatmap, new TypeBeatRuleset());
            var before = target.Line;
            string encoded = encode(editorBeatmap);

            Assert.Multiple(() =>
            {
                Assert.That(insert(editorBeatmap, time, copy(editorBeatmap, 0, 1)).Outcome, Is.EqualTo(expected));
                Assert.That(target.Line, Is.SameAs(before));
                Assert.That(encode(editorBeatmap), Is.EqualTo(encoded));
                Assert.That(changeHandler.CanUndo.Value, Is.False);
            });
        }

        [Test]
        public void TheRefusalThresholdIsPerCopiedWord()
        {
            var editorBeatmap = createBeatmap();

            // 8 of room: one word (5) goes in, two (10) do not.
            Assert.That(insert(editorBeatmap, 5392, copy(editorBeatmap, 0, 0, 1)).Outcome, Is.EqualTo(WordPasteOutcome.InsertNoRoom));
            Assert.That(insert(editorBeatmap, 5392, copy(editorBeatmap, 0, 0)).Outcome, Is.EqualTo(WordPasteOutcome.Inserted));
        }

        [Test]
        public void ALineTimedMapIsRefused()
        {
            var editorBeatmap = createBeatmap(TimingGranularity.Line);
            var before = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(insert(editorBeatmap, 6500, copy(editorBeatmap, 0, 1)).Outcome, Is.EqualTo(WordPasteOutcome.LineGranularity));
                Assert.That(lineAt(editorBeatmap, 1).Line, Is.SameAs(before));
            });
        }

        [Test]
        public void APayloadWithoutTextIsRefused()
        {
            var editorBeatmap = createBeatmap();
            var before = lineAt(editorBeatmap, 1).Line;
            var (_, old) = LyricTimingClipboard.TryParse("{\"type\":\"typebeat-unit-timings\",\"units\":[{\"start\":0,\"end\":1200,\"chars\":0}]}");

            Assert.Multiple(() =>
            {
                Assert.That(insert(editorBeatmap, 6500, old!).Outcome, Is.EqualTo(WordPasteOutcome.NoText));
                Assert.That(lineAt(editorBeatmap, 1).Line, Is.SameAs(before));
            });
        }

        [Test]
        public void UndoRestoresTheTextAndTheTimingTogether()
        {
            var editorBeatmap = createBeatmap();
            var changeHandler = new RulesetBeatmapChangeHandler(editorBeatmap, new TypeBeatRuleset());
            string encoded = encode(editorBeatmap);

            insert(editorBeatmap, 6500, copy(editorBeatmap, 0, 1));
            Assert.That(lineAt(editorBeatmap, 1).Line.RawText, Is.EqualTo("red green pie apple"));

            changeHandler.RestoreState(-1);

            var target = lineAt(editorBeatmap, 1).Line;

            Assert.Multiple(() =>
            {
                Assert.That(target.RawText, Is.EqualTo("red green pie"));
                Assert.That(target.Units.Select(span), Is.EqualTo(new[] { (5000d, 5300d), (5400d, 5900d), (6000d, 6300d) }));
                Assert.That((target.EndTime, target.SingEndTime), Is.EqualTo((9000d, 6300d)));
                Assert.That(encode(editorBeatmap), Is.EqualTo(encoded));
                Assert.That(changeHandler.CanUndo.Value, Is.False, "the insertion was one step");
            });
        }

        private static string encode(EditorBeatmap editorBeatmap)
        {
            var sb = new System.Text.StringBuilder();

            using (var writer = new System.IO.StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(editorBeatmap, writer);

            return sb.ToString();
        }

        /// <summary>
        /// Saves and reopens the map, and checks every line comes back exactly as the editor holds
        /// it: text, window, sung end, and each word's text, original, span, cuts, split and rests.
        /// </summary>
        private static System.Collections.Generic.List<TypeBeatHitObject> assertReopensAsLeft(EditorBeatmap editorBeatmap)
        {
            var reopened = SyllableSplitTest.DecodeOsu(encode(editorBeatmap));
            var held = TypeBeatEditorOperations.OrderedLines(editorBeatmap);

            Assert.That(reopened, Has.Count.EqualTo(held.Count));

            for (int i = 0; i < held.Count; i++)
            {
                var a = held[i].Line;
                var b = reopened[i].Line;

                Assert.Multiple(() =>
                {
                    Assert.That((b.RawText, b.StartTime, b.EndTime, b.SingEndTime), Is.EqualTo((a.RawText, a.StartTime, a.EndTime, a.SingEndTime)), $"line {i}");
                    Assert.That(b.Units.Select(u => (u.Text, u.Original, u.StartTime, u.EndTime)), Is.EqualTo(a.Units.Select(u => (u.Text, u.Original, u.StartTime, u.EndTime))), $"line {i} words");
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
