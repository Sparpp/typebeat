// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Input.Events;
using osu.Framework.Input.States;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Edit.GameplayTest;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 363 in the editor's TEST PLAY. The editor player's ruleset is built by the live factory
    /// (<c>Player</c> asks the ruleset for its drawable ruleset, whose engine is
    /// <c>DrawableTypeBeatRuleset.createEngine</c>'s), so the run is on authored syllables only, and the
    /// autoplay toggle (Tab) attaches a generated replay the watch path primes from. Autoplay carries
    /// the era in its own header, so on a line nobody subdivided it stays perfect AND the stack shows
    /// no phantom syllable mark while it plays.
    /// </summary>
    public partial class TestSceneTypeBeatEditorAutoplay : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Editor";
            beatmap.BeatmapInfo.Metadata.Title = "Autoplay";

            var line = new LyricLine
            {
                RawText = "never gonna",
                StartTime = 1000,
                EndTime = 4000,
                SingEndTime = 3000,
                Units = new[]
                {
                    new TimedUnit { Text = "never", StartTime = 1000, EndTime = 2000, Source = TimingSource.Explicit },
                    new TimedUnit { Text = "gonna", StartTime = 2000, EndTime = 3000, Source = TimingSource.Explicit },
                },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = line.StartTime, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word });

            return beatmap;
        }

        [TestCase(0.5)]
        [TestCase(1.5)]
        public void EditorCountersUseTheSelectedPlaybackRate(double editorRate)
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddStep("choose editor playback speed", () =>
            {
                EditorClock.Stop();
                EditorClock.Seek(0);
                EditorClock.PlaybackRate.Value = editorRate;
            });
            AddStep("start test play", () => Editor.TestGameplay());
            AddUntilStep("player and counters loaded", () => editorPlayer()?.IsLoaded == true
                && playfield()?.ChildrenOfType<TypeBeatPpCounter>().SingleOrDefault()?.IsLoaded == true);
            AddAssert("audio retains the selected playback speed", () => editorPlayer()!.TestPlayback.Rate == editorRate);
            AddStep("toggle autoplay", () =>
                ((osu.Framework.Input.Bindings.IKeyBindingHandler<GlobalAction>)editorPlayer()!).OnPressed(
                    new KeyBindingPressEvent<GlobalAction>(new InputState(), GlobalAction.EditorTestPlayToggleAutoplay)));
            AddUntilStep("every cell typed", () =>
                playfield()!.Engine.Lines[0].Cells.Where(c => c.IsTypeable).All(c => c.State == CellState.Correct));
            double expectedRate = editorRate;
            string expectedPp = "", expectedWpm = "";
            AddStep("calculate expected counters at the selected rate", () =>
            {
                var state = editorPlayer()!.GameplayState;
                var source = state.Beatmap.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line).ToArray();
                // A map stretched to the editor's tempo should have the same current price as
                // its preview. Use the results surface so changes to pp's length factor remain
                // covered without depending on a particular ForPlay overload.
                var pricedMap = new TypeBeatBeatmap
                {
                    HitObjects = source.Select(line => new TypeBeatHitObject
                    {
                        Granularity = TimingGranularity.Word,
                        Line = new LyricLine
                        {
                            RawText = line.RawText,
                            StartTime = line.StartTime / expectedRate,
                            EndTime = line.EndTime / expectedRate,
                            SingEndTime = line.SingEndTime / expectedRate,
                            Units = line.Units.Select(unit => new TimedUnit
                            {
                                Text = unit.Text,
                                StartTime = unit.StartTime / expectedRate,
                                EndTime = unit.EndTime / expectedRate,
                                Source = unit.Source,
                            }).ToArray(),
                        },
                    }).ToList(),
                };
                pricedMap.BeatmapInfo.Status = BeatmapOnlineStatus.Ranked;
                var expectedScore = new ScoreInfo
                {
                    BeatmapInfo = pricedMap.BeatmapInfo,
                    Ruleset = new TypeBeatRuleset().RulesetInfo,
                    Passed = true,
                    Rank = state.ScoreProcessor.Rank.Value,
                    Accuracy = state.ScoreProcessor.Accuracy.Value,
                    MaxCombo = state.ScoreProcessor.HighestCombo.Value,
                    Statistics = state.ScoreProcessor.Statistics.ToDictionary(pair => pair.Key, pair => pair.Value),
                    Mods = state.Mods.ToArray(),
                };
                expectedPp = PerformancePointsDisplay.Format(PerformancePointsDisplay.ForScore(expectedScore, pricedMap));

                var reference = new TypingEngine(new LyricBeatmap
                {
                    Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Preview", FolderPath = "", AudioFileName = "" },
                    Granularity = TimingGranularity.Word,
                    Lines = source,
                });
                var replay = editorPlayer()!.ChildrenOfType<DrawableTypeBeatRuleset>().Single().ReplayScore!.Replay!;
                ReplayEngineFeed.RebuildTo(reference, replay.Frames, source.Max(l => l.EndTime), expectedRate);
                expectedWpm = reference.LiveRollingWpm.ToString("0");
            });
            AddUntilStep("pp preview uses the expected rate", () =>
                playfield()!.ChildrenOfType<TypeBeatPpCounter>().Single().DisplayedText == expectedPp);
            AddUntilStep("wpm preview uses the expected rate", () =>
                playfield()!.ChildrenOfType<TypeBeatWpmCounter>().Single().DisplayedText == expectedWpm);
        }

        private EditorPlayer? editorPlayer() => Stack.ChildrenOfType<EditorPlayer>().SingleOrDefault();

        private TypeBeatPlayfield? playfield() => Stack.ChildrenOfType<TypeBeatPlayfield>().SingleOrDefault();

        [Test]
        public void TestAutoplayInTheTestPlayIsPerfectOnAnUnsubdividedWord()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("park the playhead at the start", () =>
            {
                EditorClock.Stop();
                EditorClock.Seek(0);
            });

            AddStep("start the test play", () => Editor.TestGameplay());
            AddUntilStep("player entered", () => editorPlayer()?.IsLoaded == true && playfield() != null);

            AddAssert("the test play's engine is the live factory's", () => playfield()!.Engine.AuthoredSyllablesOnly && playfield()!.Engine.AlignSubdivisionTargets);

            AddStep("toggle autoplay", () =>
                ((osu.Framework.Input.Bindings.IKeyBindingHandler<GlobalAction>)editorPlayer()!).OnPressed(
                    new KeyBindingPressEvent<GlobalAction>(new InputState(), GlobalAction.EditorTestPlayToggleAutoplay)));

            AddAssert("a replay is attached", () => editorPlayer()!.ChildrenOfType<DrawableTypeBeatRuleset>().Single().ReplayScore != null);

            AddUntilStep("every cell typed", () =>
                playfield()?.Engine.Lines[0].Cells.Where(c => c.IsTypeable).All(c => c.State == CellState.Correct) == true);

            AddAssert("still on the live grouping and targets", () => playfield()!.Engine.AuthoredSyllablesOnly && playfield()!.Engine.AlignSubdivisionTargets);
            AddAssert("every press dead on its span", () =>
                playfield()!.Engine.Lines[0].Cells.Where(c => c.IsTypeable).All(c => Math.Abs(c.JudgedDelta!.Value) <= 0.5));
            AddAssert("no phantom mark on the stack", () =>
                playfield()!.ChildrenOfType<LyricStage>().Single().DisplayAt(0)!.SyllableMarkerCount == 0);

            AddStep("apply an older replay's target era", () => ReplayEngineFeed.Apply(playfield()!.Engine,
                TypeBeatReplayFrame.CreateExtendedConfigFrame(0, authoredSyllablesOnly: true)));
            AddAssert("older targets are active", () => !playfield()!.Engine.AlignSubdivisionTargets);
            AddStep("return to manual play", () =>
                ((osu.Framework.Input.Bindings.IKeyBindingHandler<GlobalAction>)editorPlayer()!).OnPressed(
                    new KeyBindingPressEvent<GlobalAction>(new InputState(), GlobalAction.EditorTestPlayToggleAutoplay)));
            AddUntilStep("live targets restored after detach", () => playfield()!.Engine.AlignSubdivisionTargets);
        }
    }
}
