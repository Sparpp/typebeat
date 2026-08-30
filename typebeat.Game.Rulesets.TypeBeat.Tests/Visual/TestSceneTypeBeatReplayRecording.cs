// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
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
    ///
    /// <para>Since backlog 180 it also covers the one live mod stack that records a DIFFERENT
    /// judgement era: Hard Rock reverts to the classic point-target rule, so its CONFIG frame
    /// carries flags bit 2 clear. That is why the player is loaded per test rather than by the base
    /// fixture's automatic step (<see cref="HasCustomSteps"/>), and why the shared invariants take
    /// the era as an argument.</para>
    /// </summary>
    public partial class TestSceneTypeBeatReplayRecording : PlayerTestScene
    {
        private LyricLine recordedLine = null!;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        // Each test loads its own player, because one of them loads it under Hard Rock.
        protected override bool HasCustomSteps => true;

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
            CreateTest();

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

            assertCommonRecordingInvariants(syllableEra: true);
        }

        /// <summary>
        /// With allow-wrong-input on, a wrong char lands in the cell and backspace is live again:
        /// the erase must reach the engine AND be recorded as a 0x08 frame, so the replay still
        /// reproduces the run exactly.
        /// </summary>
        [Test]
        public void TestBackspaceIsRecordedWhenWrongInputIsAllowed()
        {
            CreateTest();

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

            assertCommonRecordingInvariants(syllableEra: true);
        }

        /// <summary>
        /// Backlog 180, end to end: a Hard Rock run really is judged on the classic per-character
        /// point targets, and the CONFIG frame it records says so (flags bit 2 CLEAR), so every
        /// future re-derivation of it grades the run the way the player's fingers were graded. The
        /// mod list is the only difference from
        /// <see cref="TestBackspaceIsRecordedWhenWrongInputIsAllowed"/>.
        /// </summary>
        [Test]
        public void TestHardRockRecordsAndReproducesTheClassicEra()
        {
            AddStep("load player under Hard Rock", () => LoadPlayer(new Mod[] { new TypeBeatModHardRock() }));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);

            AddUntilStep("line 0 active", () => playfield.Engine.ActiveLineIndex == 0);

            AddAssert("Hard Rock is live", () => Player.GameplayState.Mods.OfType<TypeBeatModHardRock>().Any());
            AddAssert("the engine judges on point targets", () => !playfield.Engine.SyllableTiming);
            AddAssert("the halved ladder is applied too", () =>
                playfield.Engine.WindowScale == TypeBeatModHardRock.WINDOW_SCALE);

            AddStep("allow wrong input", () => playfield.Engine.AllowWrongInput = true);

            AddStep("press Z (correct)", () => InputManager.Key(Key.Z));
            AddStep("press A (correct)", () => InputManager.Key(Key.A));
            AddStep("press Space (correct)", () => InputManager.Key(Key.Space));
            AddStep("press B (correct)", () => InputManager.Key(Key.B));

            AddAssert("line complete", () => playfield.Engine.IsLineComplete);
            AddAssert("frame sequence recorded", () => string.Concat(frames.Select(f => f.Character)) == "\0za b");

            // The load-bearing difference, on a cell whose point target is nowhere near the press:
            // 'a' is timed at 75000 and is typed within a second of the line starting, yet it sits
            // inside the span "za" is sung over ([0, 150000]). The syllable rule would call that
            // delta 0; Hard Rock prices the whole 75 seconds, which is emphatically not 0.
            AddAssert("an in-span press is judged tens of seconds off its point target", () =>
            {
                var line = playfield.Engine.Lines[0];
                var span = line.Syllables[line.SyllableIndexOf(1)];
                double pressed = frames[2].Time;

                return pressed >= span.StartTime
                       && pressed <= span.EndTime
                       && line.Cells[1].JudgedDelta == pressed - line.Cells[1].TargetTime
                       && line.Cells[1].JudgedDelta < -1000;
            });

            assertCommonRecordingInvariants(syllableEra: false);
        }

        /// <summary>
        /// <paramref name="syllableEra"/> is the judgement rule the run was played under, which
        /// since backlog 180 is a property of the MOD STACK: every stack but Hard Rock judges on
        /// syllable spans, Hard Rock on classic point targets. It decides what the CONFIG frame must
        /// say and what ladder the re-derivation below has to use.
        /// </summary>
        private void assertCommonRecordingInvariants(bool syllableEra)
        {
            AddAssert("config frame leads and captures allow-wrong-input", () =>
                frames[0].IsConfig && frames[0].AllowWrongInput == playfield.Engine.AllowWrongInput);

            AddAssert("times are integral and monotonic", () =>
                frames.All(f => f.Time == Math.Round(f.Time))
                && frames.Zip(frames.Skip(1)).All(pair => pair.First.Time <= pair.Second.Time));

            // The config frame is the ERA carrier (backlog 179, flags bit 2), and since backlog 180
            // the era is whatever the mod stack selected: the header has to say which, or every
            // re-derivation of this run would judge it on a rule it was not played under.
            AddAssert($"config frame records the {(syllableEra ? "syllable-span" : "classic point-target")} era", () =>
                frames[0].IsConfig
                && frames[0].SyllableTiming == syllableEra
                && playfield.Engine.SyllableTiming == syllableEra);

            // Backlog 181's era bit (flags bit 3), which unlike the one above is NOT a property of
            // the mod stack: live play types a wrong letter on a word gap through under every stack,
            // Hard Rock included, because it is the input model rather than a judgement window. A
            // replay written before it exists carries the bit clear and keeps its rejections.
            AddAssert("config frame records the gap-typo input model", () =>
                frames[0].IsConfig
                && frames[0].WrongInputOnWordGaps
                && playfield.Engine.WrongInputOnWordGaps);

            // Backlog 184's era bit (flags bit 4), on the same terms as the one above: live play owes
            // a space at every gap under every mod stack, and a replay written before it carries the
            // bit clear so its misplaced spaces keep the caret they were played with.
            AddAssert("config frame records the strict-space rules", () =>
                frames[0].IsConfig
                && frames[0].StrictSpaces
                && playfield.Engine.StrictSpaces);

            // Backlog 247's era bit (flags bit 8), stamped for EVERY stack, Hard Rock included,
            // where it is inert (SyllableTiming is off there): recording it uniformly is the same
            // convention bits 3, 4, 6 and 7 follow, and a replay written before it exists carries
            // the bit clear so its burst first chars keep the whole span they were paid on.
            AddAssert("config frame records the first-char hybrid", () =>
                frames[0].IsConfig
                && frames[0].FirstCharTiming
                && playfield.Engine.FirstCharTiming);

            // The recorded time IS the time the cell was judged at. Under the live rule that no
            // longer reads as "target + delta": 'z' OPENS its syllable, so since backlog 247 its
            // judged delta is the recorded time's distance from the span's start (here equal to the
            // point delta, because the span opens on the cell's own target), and Hard Rock prices
            // the same press off the point target outright. The cell that tells the two eras apart
            // is 'a', the span's NON-first cell: in the syllable era an in-span press on it judges
            // 0, and under Hard Rock its distance from a point target tens of seconds away (the
            // Hard Rock test pins that half). The determinism assertion below is what turns either
            // into a check, by re-deriving every delta from these times alone.
            AddAssert("the recorded press landed inside its syllable and was judged on the era's rule", () =>
            {
                var line = playfield.Engine.Lines[0];
                var span = line.Syllables[line.SyllableIndexOf(0)];

                bool inSpan = frames[1].Time >= span.StartTime && frames[1].Time <= span.EndTime;

                return inSpan
                       && line.Cells[0].JudgedDelta == frames[1].Time - (syllableEra ? span.StartTime : line.Cells[0].TargetTime)
                       && line.Cells[1].JudgedDelta == (syllableEra ? 0 : frames.First(f => f.Character == 'a').Time - line.Cells[1].TargetTime);
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

                // The ladder comes from the score's mods, exactly as the headless scorer takes it,
                // and it is not carried by any frame: only the ERA is.
                if (!syllableEra)
                    replayed.WindowScale *= TypeBeatModHardRock.WINDOW_SCALE;

                foreach (var frame in frames)
                {
                    if (frame.IsConfig)
                    {
                        replayed.AllowWrongInput = frame.AllowWrongInput;
                        // Backlog 179: the judgement ERA travels in the same header. A fresh engine
                        // defaults to the classic point-target rule, so without this the replayed
                        // deltas would be the ones this run was NOT judged under, which is exactly
                        // the divergence the JudgedDelta comparison below exists to catch.
                        replayed.SyllableTiming = frame.SyllableTiming;
                        // Backlog 181: the input MODEL travels in the same header, and it has to be
                        // applied for the same reason, one step harder. A fresh engine rejects a
                        // wrong key on a word gap, so a run that typed one through would replay with
                        // its caret a cell behind from that keystroke on.
                        replayed.WrongInputOnWordGaps = frame.WrongInputOnWordGaps;
                        // Backlog 247: the first-char hybrid travels in the same header (bit 8). A
                        // fresh engine defaults to the whole-span rule, so without this the replayed
                        // delta of every syllable-opening press would be the one this run was NOT
                        // judged under.
                        replayed.FirstCharTiming = frame.FirstCharTiming;
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
