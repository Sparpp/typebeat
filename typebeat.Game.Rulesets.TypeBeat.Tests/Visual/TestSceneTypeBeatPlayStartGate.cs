// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Play;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// ONLY THE EDITOR'S GAMEPLAY TEST DECLARES A PLAY START. The declaration
    /// (<c>TypingEngine.SetPlayStart</c>) makes the seal GRANT every character that was due before the
    /// play began as a perfect hit, which is right for a test play started at the mapper's playhead
    /// and free accuracy anywhere a score is submitted. So both plays here start their clock at the
    /// same late time, past the map's first two lines, and differ only in
    /// <c>DrawableRuleset.IsEditorGameplayTest</c>: the normal play is charged for the skipped lines,
    /// the editor test is not.
    /// </summary>
    public partial class TestSceneTypeBeatPlayStartGate : PlayerTestScene
    {
        /// <summary>Where both plays' clocks start: after lines 0 and 1, before line 2.</summary>
        private const double late_start = 4000;

        private bool editorGameplayTest;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        protected override bool HasCustomSteps => true;

        protected override TestPlayer CreatePlayer(Ruleset ruleset) => new LateStartPlayer(editorGameplayTest);

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>(),
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "PlayStartGate";

            addLine(beatmap, 0, "ab", 1000, 2000);
            addLine(beatmap, 1, "cd", 2000, 3000);
            addLine(beatmap, 2, "ef", 6000, 7000);

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end)
        {
            var line = new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = end,
                Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = end } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Line = line,
                Granularity = TimingGranularity.Line,
            });
        }

        [Test]
        public void TestANormalPlayWithALateClockStartIsChargedForWhatItSkipped()
        {
            CreateTest(() => AddStep("a normal play", () => editorGameplayTest = false));

            AddUntilStep("the clock started late", () => Player.GameplayClockContainer.CurrentTime >= late_start);
            AddAssert("nothing declared a play start", () => playfield.Engine.PlayStartTime, () => Is.Null);

            AddUntilStep("both skipped lines sealed", () => playfield.Engine.NextUnsealedLineIndex == 2);
            AddUntilStep("their four characters judged", () => basicJudgements() >= 4);

            AddAssert("every one of them a miss", () => count(HitResult.Miss), () => Is.EqualTo(4));
            AddAssert("so accuracy is not free", () => Player.ScoreProcessor.Accuracy.Value, () => Is.LessThan(1));
        }

        [Test]
        public void TestTheEditorGameplayTestStillGrantsWhatItSkipped()
        {
            CreateTest(() => AddStep("an editor gameplay test", () => editorGameplayTest = true));

            AddUntilStep("the clock started late", () => Player.GameplayClockContainer.CurrentTime >= late_start);
            AddAssert("it declared the clock's start", () => playfield.Engine.PlayStartTime, () => Is.EqualTo(late_start));

            AddUntilStep("both skipped lines sealed", () => playfield.Engine.NextUnsealedLineIndex == 2);
            AddUntilStep("their four characters judged", () => basicJudgements() >= 4);

            AddAssert("none of them a miss", () => count(HitResult.Miss), () => Is.Zero);
            AddAssert("so accuracy is untouched", () => Player.ScoreProcessor.Accuracy.Value, () => Is.EqualTo(1));
        }

        private int count(HitResult result) => Player.ScoreProcessor.Statistics.GetValueOrDefault(result);

        private int basicJudgements() => Player.ScoreProcessor.Statistics.Where(s => s.Key.IsBasic()).Sum(s => s.Value);

        /// <summary>
        /// A player whose clock is reset to <see cref="late_start"/> exactly the way
        /// <c>EditorPlayer</c> resets its own to the mapper's playhead, and which marks its ruleset as
        /// the editor's test play only when asked to, so the flag is the one thing the two tests vary.
        /// </summary>
        private partial class LateStartPlayer : TestPlayer
        {
            private readonly bool editorGameplayTest;

            public LateStartPlayer(bool editorGameplayTest)
                : base(false, false)
            {
                this.editorGameplayTest = editorGameplayTest;
            }

            protected override GameplayClockContainer CreateGameplayClockContainer(WorkingBeatmap beatmap, double gameplayStart)
            {
                DrawableRuleset.IsEditorGameplayTest = editorGameplayTest;

                var container = new MasterGameplayClockContainer(beatmap, gameplayStart);
                container.Reset(late_start);
                return container;
            }

            // No recorder, as EditorPlayer has none. Starting this late seals the skipped lines on
            // the ruleset's very first frame, before a recorder would have finished loading, and a
            // normal play never starts late enough to do that.
            protected override void PrepareReplay()
            {
            }
        }
    }
}
