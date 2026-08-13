// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// End-to-end autoplay: the Autoplay mod's generated replay is attached via the standard
    /// <c>SetReplayScore</c> path (see <c>TestPlayer.PrepareReplay</c>) and the playfield's replay
    /// feeder drives the engine to a perfect play: every typeable cell Correct, no misses, no
    /// wrong keys, 100% accuracy.
    /// </summary>
    public partial class TestSceneTypeBeatAutoplay : PlayerTestScene
    {
        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        protected override bool Autoplay => true;

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "Autoplay";

            addLine(beatmap, 0, "ab", 0, 2000, 1500, 500, 1500);
            addLine(beatmap, 1, "cd", 2000, 4000, 3500, 2500, 3500);

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end, double singEnd, double unitStart, double unitEnd)
        {
            var line = new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = new[] { new TimedUnit { Text = text, StartTime = unitStart, EndTime = unitEnd } },
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
        public void TestAutoplayPlaysPerfectly()
        {
            AddAssert("replay attached", () => Player.DrawableRuleset.ReplayScore != null);
            AddAssert("replay has one frame per typeable cell", () => Player.DrawableRuleset.ReplayScore!.Replay.Frames.Count == 4);

            AddUntilStep("engine finished", () => playfield.Engine.IsFinished);

            AddAssert("all typeable cells typed correctly", () =>
                playfield.Engine.Lines.SelectMany(l => l.Cells).Where(c => c.IsTypeable).All(c => c.State == CellState.Correct));

            AddAssert("no wrong keys", () => playfield.Engine.LiveAccuracy == 1);

            AddUntilStep("no misses in osu results", () =>
                Player.Results.Count(r => r.Type == HitResult.Great) == 4
                && Player.Results.All(r => r.Type != HitResult.Miss));
        }
    }
}
