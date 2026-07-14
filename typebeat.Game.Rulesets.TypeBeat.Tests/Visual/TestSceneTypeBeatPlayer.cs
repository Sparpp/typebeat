// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// End-to-end headless Player smoke: the full type!beat gameplay pipeline (converter,
    /// DrawableRuleset, never-failing health processor, engine ticking on the real gameplay
    /// clock) loads a typebeat beatmap, the engine runs the map to completion with no input,
    /// and every hit object (line + nested cells) resolves through the scoring bridge so the
    /// ScoreProcessor reaches its completed state (the results-screen precondition).
    /// </summary>
    public partial class TestSceneTypeBeatPlayer : PlayerTestScene
    {
        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>(),
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "Song";

            addLine(beatmap, 0, "ab", 0, 1200, 1200);
            addLine(beatmap, 1, "cd", 1200, 2400, 2400);

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
                // Single-token lines: one unit spanning the sung window.
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
        public void TestUntouchedRunCompletesScoring()
        {
            AddUntilStep("gameplay started", () => Player.GameplayClockContainer.CurrentTime > 0);
            AddUntilStep("engine finished (all lines sealed)", () => playfield.Engine.IsFinished);
            AddUntilStep("all judgements flushed to score processor", () => Player.ScoreProcessor.HasCompleted.Value);
            AddAssert("health never failed", () => !Player.GameplayState.HasFailed);
            AddAssert("engine recorded 4 missed cells", () => playfield.Engine.BuildResults().Counts[Gameplay.JudgementType.Miss] == 4);
        }
    }
}
