// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
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

        /// <summary>
        /// Backlog 72, end to end through the real bridge: a rejected wrong key raises no cell
        /// judgement, so the MISTYPE count is written straight onto the score processor. Two things
        /// have to hold at once. It must reach <c>Statistics</c> (before this, a mistyped play was
        /// indistinguishable from a clean one once submitted), and the play must still END: mistypes
        /// deliberately do not travel through <c>ApplyResult</c>, because that increments
        /// <c>JudgedHits</c>, which is compared for EQUALITY against the map's object count to
        /// decide the run is over, and one extra applied result would hang the run forever.
        ///
        /// <para>Driven on the REJECTION model (backlog 107 made typing-through the default), which
        /// is where the claim above actually bites: a rejected key is the only wrong keypress that
        /// reaches the score processor without a judgement of its own. The default model's version
        /// of this is <c>TestSceneTypeBeatGatekeeper.TestDefaultModelTypesWrongCharsThrough</c>.</para>
        /// </summary>
        [Test]
        public void TestMistypesPersistWithoutBlockingCompletion()
        {
            AddUntilStep("gameplay started", () => Player.GameplayClockContainer.CurrentTime > 0);
            AddUntilStep("a line is active", () => playfield.Engine.LineIsActive);

            // Set on the engine rather than through the mod: this scene loads a bare Player and the
            // point being made is about the engine path, not about how the mod is applied (which
            // TestSceneTypeBeatGatekeeper pins through the real mod pipeline).
            AddStep("reject wrong keys (Gatekeeper model)", () => playfield.Engine.AllowWrongInput = false);

            AddStep("press three wrong keys", () =>
            {
                for (int i = 0; i < 3; i++)
                    playfield.Engine.ProcessKey('z', Player.GameplayClockContainer.CurrentTime);
            });

            AddAssert("all three were rejected", () => playfield.Engine.Mistypes == 3 && playfield.Engine.ConsecutiveWrongKeys == 3);
            AddAssert("combo broke", () => Player.ScoreProcessor.Combo.Value == 0);

            AddUntilStep("engine finished (all lines sealed)", () => playfield.Engine.IsFinished);
            AddUntilStep("all judgements flushed to score processor", () => Player.ScoreProcessor.HasCompleted.Value);

            AddAssert("mistypes persisted on the score", () => Player.ScoreProcessor.Statistics.GetValueOrDefault(HitResult.ComboBreak) == 3);
            AddAssert("maximum statistics untouched", () => Player.ScoreProcessor.MaximumStatistics.GetValueOrDefault(HitResult.ComboBreak) == 0);
            AddAssert("completion still reads only the cells", () => TypeBeatScoreProcessor.ComputeCompletion(Player.ScoreProcessor.Statistics) == 0);
        }
    }
}
