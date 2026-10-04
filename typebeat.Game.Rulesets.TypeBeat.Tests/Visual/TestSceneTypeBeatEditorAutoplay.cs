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
using typebeat.Game.Rulesets.TypeBeat.UI;
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

            // A SECOND line, deliberately: the assertions below need a live play. This fixture's first line is
            // the map's whole content otherwise, so autoplay's final press would also be the map's last event
            // and the run would seal at once (EarlyFinish, carried in the header the toggle attaches - see
            // AutoplayFinishesOnItsLastPress). EditorPlayer then exits the test play RESULTS_DISPLAY_DELAY
            // after ScoreProcessor.HasCompleted, taking the playfield with it, and the era-carry teardown
            // below has nothing left to read. With a second line still to be typed the run does not finish,
            // so the tail runs while the play is up. Line 2 is never typed; it only has to exist.
            var trailing = new LyricLine
            {
                RawText = "let you down",
                StartTime = 4500,
                EndTime = 7000,
                SingEndTime = 6000,
                Units = new[]
                {
                    new TimedUnit { Text = "let", StartTime = 4500, EndTime = 5000, Source = TimingSource.Explicit },
                    new TimedUnit { Text = "you", StartTime = 5000, EndTime = 5500, Source = TimingSource.Explicit },
                    new TimedUnit { Text = "down", StartTime = 5500, EndTime = 6000, Source = TimingSource.Explicit },
                },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = trailing.StartTime, LineIndex = 1, Line = trailing, Granularity = TimingGranularity.Word });

            return beatmap;
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
