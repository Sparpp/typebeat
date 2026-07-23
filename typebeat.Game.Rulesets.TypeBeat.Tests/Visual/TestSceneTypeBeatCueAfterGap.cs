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
    /// Regression test for the missing approach cue after a gap. A line activates at the very
    /// moment its cue window opens (activation IS cue-open, <see cref="TypingEngine.CUE_LEAD_MS"/>).
    /// In continuous maps the previous line is still active through that window and carries the
    /// cue via ActiveLineIndex + 1, but when the previous line ENDS EARLY, the next line
    /// self-activates with nobody before it, and the stage's unconditional ActiveLineIndex + 1
    /// targeting skipped its cue entirely: the bar never rendered a single frame.
    /// </summary>
    public partial class TestSceneTypeBeatCueAfterGap : PlayerTestScene
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
            beatmap.BeatmapInfo.Metadata.Title = "CueAfterGap";

            // The editor's shared-boundary shape after "line 0's vocals end early": the 0/1
            // boundary sits at 2000, while line 1's VOCALS start at 5000, so line 1
            // self-activates at 3500 (first target - CUE_LEAD_MS) with no line active before
            // it. That is the path whose cue the stage used to skip. A continuous pair after
            // it guards the unchanged behavior.
            addLine(beatmap, 0, "ab", 1000, 2000, 2000, 1000);
            addLine(beatmap, 1, "cd", 2000, 6200, 6200, 5000);
            addLine(beatmap, 2, "ef", 6200, 7400, 7400, 6200);

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end, double singEnd, double vocalStart)
        {
            var line = new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = new[] { new TimedUnit { Text = text, StartTime = vocalStart, EndTime = singEnd } },
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
        public void TestCueShownForLineAfterGap()
        {
            AddUntilStep("gameplay started", () => Player.GameplayClockContainer.CurrentTime > 0);

            // The gap-activation path: line 1 becomes active during its own lead-in (3500, well
            // before its first vocal at 5000), with line 0 already sealed and nothing else
            // active before it.
            AddUntilStep("line 1 active in its own lead-in", () =>
                playfield.Engine.ActiveLineIndex == 1
                && Player.GameplayClockContainer.CurrentTime < 4800);

            // THE regression: the approach bar must render FOR LINE 1, the line that just
            // self-activated, during its lead-in. Pre-fix, targeting ActiveLineIndex + 1
            // pointed the cue at line 2 instead (whose own window only opens at 4700), so the
            // player got no cue for the line they were about to type. Alpha alone cannot catch
            // that, hence the target-line assertion, bounded before line 2's window opens.
            AddUntilStep("approach cue shown for line 1", () =>
                stage.ApproachCueVisible
                && stage.ApproachCueTargetLine == 1
                && Player.GameplayClockContainer.CurrentTime < 4600);

            // Sanity: the early-ending line sealed normally (missed, combo reset is expected for
            // an untyped line) and gameplay is still live.
            AddAssert("line 0 sealed", () => playfield.Engine.NextUnsealedLineIndex >= 1);
            AddAssert("engine not finished", () => !playfield.Engine.IsFinished);
        }
    }
}
