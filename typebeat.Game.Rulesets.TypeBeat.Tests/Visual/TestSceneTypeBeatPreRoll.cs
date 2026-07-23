// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Discriminating regression test for the "black playfield" bug: with a map whose first
    /// line starts at 5s, early gameplay must be a live PRE-ROLL: engine not finished,
    /// nothing sealed, and the upcoming line VISIBLY displayed. A mis-clocked lyric subtree
    /// (engine fed app-time instead of gameplay time) insta-seals every line and fades the
    /// stage out, which the completion-oriented player test cannot distinguish from success.
    /// </summary>
    public partial class TestSceneTypeBeatPreRoll : PlayerTestScene
    {
        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;

        private LyricStage stage => Player.ChildrenOfType<LyricStage>().Single();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>(),
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "PreRoll";

            addLine(beatmap, 0, "ab", 5000, 6200, 6200);
            addLine(beatmap, 1, "cd", 6200, 7400, 7400);

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end, double singEnd)
        {
            var line = new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = singEnd } },
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
        public void TestPreRollIsAliveAndVisible()
        {
            AddUntilStep("gameplay started", () => Player.GameplayClockContainer.CurrentTime > 0);

            // A mis-clocked engine seals everything within one frame of load.
            AddAssert("engine not finished", () => !playfield.Engine.IsFinished);
            AddAssert("pre-roll: no active line yet", () => playfield.Engine.ActiveLineIndex == -1);
            AddAssert("nothing sealed as missed", () => playfield.Engine.BuildResults().Counts[JudgementType.Miss] == 0);

            // The stage must actually RENDER: upcoming line 0 shown dimmed during pre-roll.
            AddUntilStep("upcoming line display visible", () =>
            {
                var d = stage.DisplayAt(0);
                return d != null && d.Alpha > 0.1f && d.DrawSize.X > 0 && d.DrawSize.Y > 0;
            });
            AddAssert("stage has non-zero draw size", () => stage.DrawSize.X > 0 && stage.DrawSize.Y > 0);
            AddAssert("still not finished after display check", () => !playfield.Engine.IsFinished);
        }
    }
}
