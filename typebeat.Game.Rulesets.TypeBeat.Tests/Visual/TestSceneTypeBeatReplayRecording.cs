// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
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
    /// End-to-end recording coverage: real key presses through the whole input stack (a live
    /// <see cref="typebeat.Game.Screens.Play.Player"/> with the standard record target) must land
    /// in the score's replay as one frame per EFFECTIVE input, in order, with integral times that
    /// are exactly the times the engine judged at. Feeding the recorded frames into a fresh
    /// <see cref="TypingEngine"/> (the same call sequence replay playback makes) must then
    /// reproduce the live engine's state, which is the determinism contract that makes both replay
    /// watching and future score recalculation possible.
    /// </summary>
    public partial class TestSceneTypeBeatReplayRecording : PlayerTestScene
    {
        private LyricLine recordedLine = null!;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;

        private List<TypeBeatReplayFrame> frames => Player.GameplayState.Score.Replay.Frames.OfType<TypeBeatReplayFrame>().ToList();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "ReplayRecording";

            // 'z' first proves the vestigial Z action binding does not eat recorded typing. The
            // window is deliberately huge so the test is timing-insensitive.
            recordedLine = new LyricLine
            {
                RawText = "za b",
                StartTime = 0,
                EndTime = 600000,
                SingEndTime = 300000,
                Units = new[]
                {
                    new TimedUnit { Text = "za", StartTime = 0, EndTime = 150000 },
                    new TimedUnit { Text = "b", StartTime = 150000, EndTime = 300000 },
                },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = recordedLine.StartTime,
                LineIndex = 0,
                Line = recordedLine,
                Granularity = TimingGranularity.Word,
            });

            return beatmap;
        }

        [Test]
        public void TestKeystrokesAreRecordedAndReproduceTheLiveRun()
        {
            AddUntilStep("line 0 active", () => playfield.Engine.ActiveLineIndex == 0);

            AddAssert("typing wrong chars through is the default", () => playfield.Engine.AllowWrongInput);

            // Switch to the Gatekeeper (strict) model, which is what this test is about: a rejected
            // key IS recorded (it is an effective engine call) while a gated-off backspace is not.
            AddStep("switch to the strict model", () => playfield.Engine.AllowWrongInput = false);

            AddStep("press Z (correct)", () => InputManager.Key(Key.Z));
            AddStep("press X (wrong, rejected)", () => InputManager.Key(Key.X));
            AddStep("press A (correct)", () => InputManager.Key(Key.A));

            // Strict play never writes an erasable char, so backspace is gated off at the key
            // handler: no engine call, and nothing reaches the recorder.
            int framesBeforeBackspace = 0;
            AddStep("capture frame count", () => framesBeforeBackspace = frames.Count);
            AddStep("press Backspace (gated off)", () => InputManager.Key(Key.BackSpace));
            AddAssert("backspace recorded nothing", () => frames.Count == framesBeforeBackspace);
            AddAssert("backspace changed no engine state", () =>
                playfield.Engine.CaretIndex == 2
                && playfield.Engine.Lines[0].Cells[1].State == CellState.Correct
                && playfield.Engine.Lines[0].Cells[1].TypedChar == 'a');

            AddStep("press Space (correct)", () => InputManager.Key(Key.Space));
            AddStep("press B (correct)", () => InputManager.Key(Key.B));

            AddAssert("line complete", () => playfield.Engine.IsLineComplete);

            // One config header + one frame per effective input, in press order.
            AddAssert("frame sequence recorded", () =>
                string.Concat(frames.Select(f => f.Character)) == "\0zxa b");

            assertCommonRecordingInvariants();
        }

        /// <summary>
        /// With allow-wrong-input on, a wrong char lands in the cell and backspace is live again:
        /// the erase must reach the engine AND be recorded as a 0x08 frame, so the replay still
        /// reproduces the run exactly.
        /// </summary>
        [Test]
        public void TestBackspaceIsRecordedWhenWrongInputIsAllowed()
        {
            AddUntilStep("line 0 active", () => playfield.Engine.ActiveLineIndex == 0);

            AddStep("allow wrong input", () => playfield.Engine.AllowWrongInput = true);

            AddStep("press Z (correct)", () => InputManager.Key(Key.Z));
            AddStep("press Q (wrong, typed through)", () => InputManager.Key(Key.Q));
            AddAssert("wrong char landed in the cell", () =>
                playfield.Engine.Lines[0].Cells[1].State == CellState.Wrong
                && playfield.Engine.Lines[0].Cells[1].TypedChar == 'q');

            AddStep("press Backspace (erases 'q')", () => InputManager.Key(Key.BackSpace));
            AddAssert("cell reopened", () =>
                playfield.Engine.Lines[0].Cells[1].State == CellState.Untyped
                && playfield.Engine.CaretIndex == 1);

            AddStep("press A (correct)", () => InputManager.Key(Key.A));
            AddStep("press Space (correct)", () => InputManager.Key(Key.Space));
            AddStep("press B (correct)", () => InputManager.Key(Key.B));

            AddAssert("line complete", () => playfield.Engine.IsLineComplete);

            AddAssert("frame sequence records the erase", () =>
                string.Concat(frames.Select(f => f.Character)) == "\0zq\ba b");

            AddAssert("config frame carries allow-wrong-input on", () => frames[0].IsConfig && frames[0].AllowWrongInput);

            assertCommonRecordingInvariants();
        }

        private void assertCommonRecordingInvariants()
        {
            AddAssert("config frame leads and captures allow-wrong-input", () =>
                frames[0].IsConfig && frames[0].AllowWrongInput == playfield.Engine.AllowWrongInput);

            AddAssert("times are integral and monotonic", () =>
                frames.All(f => f.Time == Math.Round(f.Time))
                && frames.Zip(frames.Skip(1)).All(pair => pair.First.Time <= pair.Second.Time));

            // The recorded time IS the judged time: cell 'z' was judged at target + delta.
            AddAssert("frame time matches the engine's judged time", () =>
            {
                var cell = playfield.Engine.Lines[0].Cells[0];
                return frames[1].Time == cell.TargetTime + cell.JudgedDelta!.Value;
            });

            // Determinism: replaying the recorded frames into a fresh engine (the exact call
            // sequence the replay feeder makes) reproduces the live engine's state.
            AddAssert("recorded frames reproduce the live engine state", () =>
            {
                var live = playfield.Engine;

                var replayed = new TypingEngine(new LyricBeatmap
                {
                    Metadata = new LyricBeatmapMetadata
                    {
                        Artist = "Test",
                        Title = "ReplayRecording",
                        FolderPath = string.Empty,
                        AudioFileName = string.Empty,
                        HasWordTiming = true,
                    },
                    Lines = new List<LyricLine> { recordedLine },
                    Granularity = TimingGranularity.Word,
                });

                foreach (var frame in frames)
                {
                    if (frame.IsConfig)
                    {
                        replayed.AllowWrongInput = frame.AllowWrongInput;
                        continue;
                    }

                    replayed.Update(frame.Time);

                    if (frame.IsBackspace)
                        replayed.ProcessBackspace();
                    else
                        replayed.ProcessKey(frame.Character, frame.Time);
                }

                bool cellsMatch = live.Lines[0].Cells.Zip(replayed.Lines[0].Cells)
                                      .All(pair => pair.First.State == pair.Second.State
                                                   && pair.First.TypedChar == pair.Second.TypedChar
                                                   && Nullable.Equals(pair.First.JudgedDelta, pair.Second.JudgedDelta));

                return cellsMatch
                       && replayed.Score == live.Score
                       && replayed.MaxCombo == live.MaxCombo
                       && replayed.CaretIndex == live.CaretIndex
                       && replayed.ConsecutiveWrongKeys == live.ConsecutiveWrongKeys
                       && replayed.LiveAccuracy == live.LiveAccuracy;
            });
        }
    }
}
