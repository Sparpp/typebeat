// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Line-list selection semantics (the "pick a section to time" surface): plain click selects
    /// one, Ctrl+click toggles, Shift+click ranges from a FIXED anchor, and the last-clicked line
    /// always stays the primary (what the detail panel edits).
    /// </summary>
    [TestFixture]
    public class LyricLineSelectionTest
    {
        private static List<TypeBeatHitObject> lines(int count)
        {
            var result = new List<TypeBeatHitObject>();

            for (int i = 0; i < count; i++)
            {
                double start = 1000 * (i + 1);

                result.Add(new TypeBeatHitObject
                {
                    StartTime = start,
                    LineIndex = i,
                    Line = new LyricLine
                    {
                        RawText = $"line{i}",
                        StartTime = start,
                        EndTime = start + 1000,
                        SingEndTime = start + 900,
                        Units = new[] { new TimedUnit { Text = $"line{i}", StartTime = start, EndTime = start + 900 } },
                    },
                    Granularity = TimingGranularity.Word,
                });
            }

            return result;
        }

        [Test]
        public void TestPlainClickIsSingleSelection()
        {
            var all = lines(5);
            var state = new LyricEditState();

            state.SelectLine(all[2]);

            Assert.That(state.SelectedLine.Value, Is.SameAs(all[2]));
            Assert.That(state.MultiSelectedLines, Is.Empty, "a plain click leaves no multi-selection");
            Assert.That(state.SelectedLinesInOrder(all), Is.EqualTo(new[] { all[2] }));

            // Plain-clicking again replaces (never grows) the selection.
            state.SelectLine(all[4]);
            Assert.That(state.SelectedLinesInOrder(all), Is.EqualTo(new[] { all[4] }));
        }

        [Test]
        public void TestCtrlClickTogglesAndSeedsFromPrimary()
        {
            var all = lines(5);
            var state = new LyricEditState();

            state.SelectLine(all[1]);
            state.ToggleLine(all[3]);

            // The first toggle adds to what was visibly selected, rather than starting from nothing.
            Assert.That(state.SelectedLinesInOrder(all), Is.EqualTo(new[] { all[1], all[3] }));
            Assert.That(state.SelectedLine.Value, Is.SameAs(all[3]), "the clicked line becomes primary");

            state.ToggleLine(all[3]);
            Assert.That(state.SelectedLinesInOrder(all), Is.EqualTo(new[] { all[1] }));
            Assert.That(state.SelectedLine.Value, Is.SameAs(all[1]), "removing the primary promotes a survivor");
        }

        [Test]
        public void TestShiftClickRangesFromFixedAnchor()
        {
            var all = lines(6);
            var state = new LyricEditState();

            state.SelectLine(all[1]);
            state.SelectLineRange(all, all[4]);

            Assert.That(state.SelectedLinesInOrder(all), Is.EqualTo(new[] { all[1], all[2], all[3], all[4] }));
            Assert.That(state.SelectedLine.Value, Is.SameAs(all[4]));
            Assert.That(state.RangeAnchor, Is.SameAs(all[1]), "the anchor must not walk with the click");

            // Shift-clicking again re-ranges from the SAME anchor: the run shrinks rather than
            // extending from where the previous shift+click landed.
            state.SelectLineRange(all, all[2]);
            Assert.That(state.SelectedLinesInOrder(all), Is.EqualTo(new[] { all[1], all[2] }));

            // Ranging backwards past the anchor is symmetric.
            state.SelectLineRange(all, all[0]);
            Assert.That(state.SelectedLinesInOrder(all), Is.EqualTo(new[] { all[0], all[1] }));
        }

        [Test]
        public void TestShiftClickAfterCtrlClickAnchorsAtTheCtrlClick()
        {
            var all = lines(6);
            var state = new LyricEditState();

            state.SelectLine(all[0]);
            state.ToggleLine(all[3]);
            state.SelectLineRange(all, all[5]);

            // The ctrl+click moved the anchor, so the range runs 3..5 (and replaces the set).
            Assert.That(state.SelectedLinesInOrder(all), Is.EqualTo(new[] { all[3], all[4], all[5] }));
        }

        [Test]
        public void TestShiftClickWithNoPriorSelectionSelectsOne()
        {
            var all = lines(4);
            var state = new LyricEditState();

            state.SelectLineRange(all, all[2]);

            Assert.That(state.SelectedLinesInOrder(all), Is.EqualTo(new[] { all[2] }));
            Assert.That(state.SelectedLine.Value, Is.SameAs(all[2]));
        }

        [Test]
        public void TestPlainClickAndEscapeClearTheSection()
        {
            var all = lines(5);
            var state = new LyricEditState();

            state.SelectLine(all[0]);
            state.SelectLineRange(all, all[3]);
            Assert.That(state.MultiSelectedLines, Has.Count.EqualTo(4));

            state.SelectLine(all[2]);
            Assert.That(state.MultiSelectedLines, Is.Empty, "a plain click collapses the section");

            state.SelectLine(all[0]);
            state.SelectLineRange(all, all[3]);
            state.ClearMultiLineSelection();
            Assert.That(state.MultiSelectedLines, Is.Empty);
            Assert.That(state.RangeAnchor, Is.Null, "clearing drops the anchor with the section");
        }

        [Test]
        public void TestRebindAfterUndoMapsByLineIndex()
        {
            var all = lines(5);
            var state = new LyricEditState();

            state.SelectLine(all[1]);
            state.SelectLineRange(all, all[3]);

            // Undo/redo rebuilds every hit object instance; the selection must survive by index.
            var rebuilt = lines(5);
            state.RebindMultiSelection(rebuilt, o => rebuilt.Contains(o));

            Assert.That(state.SelectedLinesInOrder(rebuilt), Is.EqualTo(new[] { rebuilt[1], rebuilt[2], rebuilt[3] }));
            Assert.That(state.RangeAnchor, Is.SameAs(rebuilt[1]));
        }

        [Test]
        public void TestRebindDropsVanishedLines()
        {
            var all = lines(5);
            var state = new LyricEditState();

            state.SelectLine(all[1]);
            state.SelectLineRange(all, all[3]);

            var survivors = all.Where(o => o.LineIndex != 2).ToList();
            state.RebindMultiSelection(survivors, o => survivors.Contains(o));

            Assert.That(state.SelectedLinesInOrder(survivors), Is.EqualTo(new[] { all[1], all[3] }));
        }
    }
}
