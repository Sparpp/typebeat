// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 182 through the REAL input stack: Ctrl+Backspace erases the previous word and Ctrl+A
    /// selects back to the nearest unfixed typo, both driven here as actual key presses on a live
    /// <see cref="typebeat.Game.Screens.Play.Player"/> with the standard record target.
    ///
    /// <para>Two things are being pinned that the headless <c>WordInputTest</c> cannot reach. The
    /// GESTURES themselves: which keys the handler carves out of the Ctrl fall-through, that the
    /// selection is UI state the next effective input consumes, and that Gatekeeper swallows both
    /// exactly as it swallows the plain backspace. And the REPLAY: because both gestures decompose
    /// into ordinary engine calls, the recording is a plain run of backspace frames plus at most one
    /// character frame, at the one timestamp the live engine judged them at, and re-deriving that
    /// recording through the legacy encode/decode has to reproduce the live run exactly. No new frame
    /// vocabulary, no new era bit, and equal-time frames whose ORDER the .osr format preserves (it
    /// stores integral deltas, a run of zeroes here, and its decoder keeps same-time frames in
    /// written order).</para>
    /// </summary>
    public partial class TestSceneTypeBeatWordInput : PlayerTestScene
    {
        private LyricLine recordedLine = null!;
        private LyricLine trailingLine = null!;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;

        private TypingEngine engine => playfield.Engine;

        private List<TypeBeatReplayFrame> frames => Player.GameplayState.Score.Replay.Frames.OfType<TypeBeatReplayFrame>().ToList();

        private LyricLineDisplay activeDisplay => playfield.ChildrenOfType<LyricLineDisplay>().Single(d => d.Line == engine.Lines[0]);

        /// <summary>
        /// "ab cd ef": cells a b ' ' c d ' ' e f, so the gaps are 2 and 5 and the words start at 0, 3
        /// and 6. Three words, because the point of Ctrl+Backspace is that it takes exactly one. The
        /// windows are deliberately enormous so nothing here is timing-sensitive.
        ///
        /// <para>A second, far-away line follows it purely so the first one can be SEALED without
        /// finishing the map (see <see cref="TestTheSelectionDropsWhenTheLineDeactivates"/>): the run
        /// then lands in the dead zone before the next cue, with nothing active, which is the state
        /// that has to drop a held selection.</para>
        /// </summary>
        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "WordInput";

            recordedLine = new LyricLine
            {
                RawText = "ab cd ef",
                StartTime = 0,
                EndTime = 600000,
                SingEndTime = 300000,
                Units = new[]
                {
                    new TimedUnit { Text = "ab", StartTime = 0, EndTime = 100000 },
                    new TimedUnit { Text = "cd", StartTime = 100000, EndTime = 200000 },
                    new TimedUnit { Text = "ef", StartTime = 200000, EndTime = 300000 },
                },
            };

            trailingLine = new LyricLine
            {
                RawText = "g",
                StartTime = 700000,
                EndTime = 1200000,
                SingEndTime = 800000,
                Units = new[] { new TimedUnit { Text = "g", StartTime = 750000, EndTime = 800000 } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = recordedLine.StartTime,
                LineIndex = 0,
                Line = recordedLine,
                Granularity = TimingGranularity.Word,
            });

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = trailingLine.StartTime,
                LineIndex = 1,
                Line = trailingLine,
                Granularity = TimingGranularity.Word,
            });

            return beatmap;
        }

        // -----------------------------------------------------------------------------------------
        // Ctrl+Backspace
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The headline gesture: from the end of a fully typed line, one press per word walks the
        /// caret back word by word, and a press at the head of the line does nothing at all (and,
        /// crucially, records nothing: the composed loop never calls the engine).
        /// </summary>
        [Test]
        public void TestCtrlBackspaceWalksBackOneWordPerPress()
        {
            waitForLine();

            type("ab cd ef");
            AddAssert("line complete", () => engine.IsLineComplete);

            ctrl(Key.BackSpace);
            AddAssert("\"ef\" is gone", () => engine.CaretIndex == 6 && cell(6).State == CellState.Untyped && cell(7).State == CellState.Untyped);

            ctrl(Key.BackSpace);
            AddAssert("the gap before it and \"cd\" are gone", () =>
                engine.CaretIndex == 3
                && cell(5).State == CellState.Untyped
                && cell(3).State == CellState.Untyped
                && cell(2).State == CellState.Correct);

            ctrl(Key.BackSpace);
            AddAssert("the whole line is open again", () => engine.CaretIndex == 0 && engine.Lines[0].Cells.All(c => c.State == CellState.Untyped));

            int recorded = 0;
            AddStep("capture frame count", () => recorded = frames.Count);
            ctrl(Key.BackSpace);
            AddAssert("a press at the head of the line recorded nothing", () => frames.Count == recorded);
            AddAssert("and changed nothing", () => engine.CaretIndex == 0);
        }

        /// <summary>
        /// The gesture is CARVED OUT of the Ctrl fall-through, not bolted onto the plain key: a plain
        /// backspace still erases exactly one cell, and Ctrl+Backspace erases the word, from the same
        /// caret position.
        /// </summary>
        [Test]
        public void TestPlainBackspaceStillErasesOneCell()
        {
            waitForLine();

            type("ab cd");
            AddStep("press Backspace", () => InputManager.Key(Key.BackSpace));
            AddAssert("one cell erased", () => engine.CaretIndex == 4);

            ctrl(Key.BackSpace);
            AddAssert("the rest of the word erased", () => engine.CaretIndex == 3);
        }

        // -----------------------------------------------------------------------------------------
        // Ctrl+A and the consume paths
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// A typo two cells back, a Ctrl+A to offer the run holding it, and a LETTER to consume the
        /// offer: the selection collapses to the anchor and the key is typed there through the normal
        /// judged path. The selection is highlighted on the active line's display while it is held
        /// and gone the moment it is consumed.
        /// </summary>
        [Test]
        public void TestCtrlASelectsBackToTheTypoAndALetterConsumesIt()
        {
            waitForLine();

            type("a");
            AddStep("press X (wrong for 'b')", () => InputManager.Key(Key.X));
            type(" c");

            AddAssert("the typo landed", () => cell(1).State == CellState.Wrong && engine.CaretIndex == 4);

            ctrl(Key.A);

            AddAssert("the run back to the typo's word is selected", () =>
                playfield.CurrentRetypeSelection is TypeBeatPlayfield.RetypeSelection { LineIndex: 0, StartCell: 0, EndCell: 4 });
            AddAssert("and highlighted", () =>
                activeDisplay.SelectionStart == 0 && activeDisplay.SelectionEnd == 4 && activeDisplay.SelectionVisible);

            AddStep("press A (the first char of the selected run)", () => InputManager.Key(Key.A));

            AddAssert("the selection collapsed and the key landed on its anchor", () =>
                engine.CaretIndex == 1
                && cell(0).State == CellState.Correct
                && engine.Lines[0].Cells.Skip(1).All(c => c.State == CellState.Untyped));
            AddAssert("the selection is gone", () => playfield.CurrentRetypeSelection == null && !activeDisplay.SelectionVisible);

            AddStep("retype the word", () =>
            {
                InputManager.Key(Key.B);
                InputManager.Key(Key.Space);
            });
            AddAssert("the typo is fixed", () => cell(1).State == CellState.Correct && engine.CaretIndex == 3);
        }

        /// <summary>
        /// A plain BACKSPACE over a selection collapses it and types nothing, which is the other
        /// consume path: the player asked to throw the run away rather than to retype it in place.
        /// </summary>
        [Test]
        public void TestPlainBackspaceCollapsesASelectionWithoutTyping()
        {
            waitForLine();

            type("a");
            AddStep("press X (wrong for 'b')", () => InputManager.Key(Key.X));
            type(" c");

            ctrl(Key.A);
            AddAssert("selection held", () => playfield.CurrentRetypeSelection != null);

            AddStep("press Backspace", () => InputManager.Key(Key.BackSpace));

            AddAssert("the whole selection was erased and nothing typed", () =>
                engine.CaretIndex == 0 && engine.Lines[0].Cells.All(c => c.State == CellState.Untyped));
            AddAssert("the selection is gone", () => playfield.CurrentRetypeSelection == null);
        }

        /// <summary>
        /// A typo on the WORD GAP (backlog 181) is a typo for this gesture too, and it anchors on the
        /// gap rather than dragging the good word in front of it into the selection.
        /// </summary>
        [Test]
        public void TestAGapTypoAnchorsOnTheGap()
        {
            waitForLine();

            type("ab");
            AddStep("press X on the word gap", () => InputManager.Key(Key.X));
            type("c");

            AddAssert("the gap holds the typo", () => cell(2).State == CellState.Wrong && cell(2).TypedChar == 'x');

            ctrl(Key.A);

            AddAssert("only the gap onwards is selected", () =>
                playfield.CurrentRetypeSelection is TypeBeatPlayfield.RetypeSelection { StartCell: 2, EndCell: 4 });
        }

        /// <summary>
        /// With no typo behind the caret Ctrl+A is a no-op: nothing is selected, nothing is erased,
        /// and (because the key is still swallowed) nothing else in the game acts on it either.
        /// </summary>
        [Test]
        public void TestCtrlAWithNothingWrongIsANoOp()
        {
            waitForLine();

            type("ab cd");

            int recorded = 0;
            AddStep("capture frame count", () => recorded = frames.Count);

            ctrl(Key.A);

            AddAssert("nothing selected", () => playfield.CurrentRetypeSelection == null);
            AddAssert("nothing recorded", () => frames.Count == recorded);
            AddAssert("nothing typed", () => engine.CaretIndex == 5);
        }

        /// <summary>
        /// The selection is a gesture held open on the ACTIVE line, so the seal that ends that line
        /// drops it, highlight and all. Forced here by ticking the engine past the line's deadline,
        /// which is what the clock does on its own a few minutes into this (deliberately enormous)
        /// window; the line is typed out first so the seal resolves one unfixed typo rather than a
        /// screenful of misses.
        /// </summary>
        [Test]
        public void TestTheSelectionDropsWhenTheLineDeactivates()
        {
            waitForLine();

            type("a");
            AddStep("press X (wrong for 'b')", () => InputManager.Key(Key.X));
            type(" cd ef");

            ctrl(Key.A);
            AddAssert("selection held", () => playfield.CurrentRetypeSelection != null && activeDisplay.SelectionVisible);

            AddStep("run the engine past the line's deadline", () => engine.Update(700000));
            AddUntilStep("the line deactivated", () => engine.ActiveLineIndex == -1);

            AddAssert("the selection dropped", () => playfield.CurrentRetypeSelection == null);
            AddAssert("and so did its highlight", () => !activeDisplay.SelectionVisible);
        }

        /// <summary>
        /// Gatekeeper swallows both gestures whole, exactly as it swallows the plain backspace and
        /// for the same reason: with no wrong character able to land there is nothing to erase back
        /// over and nothing to select. Swallowed rather than passed on, so neither key starts
        /// triggering its meaning elsewhere in the game just because the model is strict.
        /// </summary>
        [Test]
        public void TestGatekeeperSwallowsBothGestures()
        {
            waitForLine();

            AddStep("switch to the Gatekeeper (strict) model", () => engine.AllowWrongInput = false);

            type("ab cd");

            int recorded = 0;
            AddStep("capture frame count", () => recorded = frames.Count);

            ctrl(Key.BackSpace);
            ctrl(Key.A);

            AddAssert("neither gesture touched the engine", () =>
                engine.CaretIndex == 5 && engine.Lines[0].Cells.Take(5).All(c => c.State == CellState.Correct));
            AddAssert("neither gesture recorded anything", () => frames.Count == recorded);
            AddAssert("no selection exists", () => playfield.CurrentRetypeSelection == null);
        }

        // -----------------------------------------------------------------------------------------
        // The replay: live == replay, with no new frame vocabulary
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The determinism contract for both gestures at once. A run containing a Ctrl+Backspace
        /// burst AND a Ctrl+A consume records as nothing but the existing frames (0x08 backspaces and
        /// one character), and re-deriving those frames through the legacy .osr encode/decode
        /// reproduces the live engine cell for cell and total for total.
        ///
        /// <para>The burst's frames all carry ONE timestamp, the time the live engine was advanced to
        /// and judged at, so an equal-time run is exactly what has to survive the round trip: it does,
        /// because the format stores integral deltas (zeroes) and its decoder never reorders frames of
        /// equal time.</para>
        /// </summary>
        [Test]
        public void TestBothGesturesRecordAndReDeriveExactly()
        {
            waitForLine();

            type("ab cd ");

            // A Ctrl+Backspace burst: three erases (the gap, 'd', 'c') out of one key press.
            ctrl(Key.BackSpace);
            AddAssert("the gap and \"cd\" went", () => engine.CaretIndex == 3);

            type("cd ");
            AddStep("press X (wrong for 'e')", () => InputManager.Key(Key.X));
            type("f");

            // A Ctrl+A consume: two erases back to the head of "ef", then the letter at the anchor.
            ctrl(Key.A);
            AddAssert("the typo's word is selected", () =>
                playfield.CurrentRetypeSelection is TypeBeatPlayfield.RetypeSelection { StartCell: 6, EndCell: 8 });
            AddStep("press E (consumes the selection)", () => InputManager.Key(Key.E));

            AddAssert("the typo is fixed in place", () =>
                cell(6).State == CellState.Correct && cell(6).TypedChar == 'e' && engine.CaretIndex == 7);

            AddAssert("nothing but existing frame kinds was recorded", () =>
                frames.All(f => f.IsConfig || f.IsBackspace || f.Character is >= 'a' and <= 'z' or ' '));

            AddAssert("the frame sequence is the calls the engine actually took", () =>
                string.Concat(frames.Select(f => f.Character)) == "\0ab cd \b\b\bcd xf\b\be");

            AddAssert("times are integral and monotonic", () =>
                frames.All(f => f.Time == Math.Round(f.Time))
                && frames.Zip(frames.Skip(1)).All(pair => pair.First.Time <= pair.Second.Time));

            AddAssert("each burst's frames share the one timestamp the engine judged them at", () =>
            {
                var all = frames;

                // Counting the CONFIG header at 0: the Ctrl+Backspace burst is frames 7..9, and the
                // Ctrl+A consume is 15..17 (its two erases plus the letter that landed at the anchor,
                // all produced by the single press that consumed the selection).
                return all[7].Time == all[8].Time && all[8].Time == all[9].Time
                       && all[15].Time == all[16].Time && all[16].Time == all[17].Time;
            });

            AddAssert("the recorded run re-derives to the live one", () =>
            {
                var live = engine;
                var replayed = reDerive();

                bool cellsMatch = live.Lines[0].Cells.Zip(replayed.Lines[0].Cells)
                                      .All(pair => pair.First.State == pair.Second.State
                                                   && pair.First.TypedChar == pair.Second.TypedChar
                                                   && Nullable.Equals(pair.First.JudgedDelta, pair.Second.JudgedDelta));

                return cellsMatch
                       && replayed.CaretIndex == live.CaretIndex
                       && replayed.Score == live.Score
                       && replayed.MaxCombo == live.MaxCombo
                       && replayed.Combo == live.Combo
                       && replayed.Mistypes == live.Mistypes
                       && replayed.LiveAccuracy == live.LiveAccuracy;
            });
        }

        #region Harness

        private void waitForLine() => AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0);

        private TypingCell cell(int index) => engine.Lines[0].Cells[index];

        /// <summary>Type a run of characters as real key presses (letters and spaces only).</summary>
        private void type(string characters) => AddStep($"type \"{characters}\"", () =>
        {
            foreach (char c in characters)
                InputManager.Key(c == ' ' ? Key.Space : Key.A + (c - 'a'));
        });

        /// <summary>One press of <paramref name="key"/> with Control held, released again after.</summary>
        private void ctrl(Key key) => AddStep($"press Ctrl+{key}", () =>
        {
            InputManager.PressKey(Key.ControlLeft);
            InputManager.Key(key);
            InputManager.ReleaseKey(Key.ControlLeft);
        });

        /// <summary>
        /// Re-derive the recorded run into a fresh engine THROUGH the legacy frame mapping (encode,
        /// then decode the way <c>LegacyScoreDecoder.convertFrame</c> does), fed by
        /// <see cref="ReplayEngineFeed.Apply"/>, which is the one definition of how a recorded frame
        /// reaches an engine. Nothing about the two gestures is reconstructed here: they left behind
        /// ordinary frames, so this is the ordinary playback path.
        /// </summary>
        private TypingEngine reDerive()
        {
            var dummy = new Beatmap();

            var replayed = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata
                {
                    Artist = "Test",
                    Title = "WordInput",
                    FolderPath = string.Empty,
                    AudioFileName = string.Empty,
                    HasWordTiming = true,
                },
                Lines = new List<LyricLine> { recordedLine, trailingLine },
                Granularity = TimingGranularity.Word,
            });

            foreach (var frame in frames)
            {
                var legacy = frame.ToLegacy(dummy);
                double storedTime = Math.Round(legacy.Time);

                var decoded = new TypeBeatReplayFrame();
                decoded.FromLegacy(new LegacyReplayFrame(storedTime, legacy.MouseX, legacy.MouseY, legacy.ButtonState), dummy);
                decoded.Time = storedTime;

                ReplayEngineFeed.Apply(replayed, decoded);
            }

            return replayed;
        }

        #endregion
    }
}
