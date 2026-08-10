// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The mash-fail rule end-to-end through Player: 13 consecutive rejected wrong keys drain
    /// the health processor to zero and fail the play; an accepted char ends the streak and
    /// recovers a little HP (the bar is now a genuine osu HP pool, not a streak mirror, so
    /// recovery is a judgement increment rather than a snap back to full). Keys are fed straight
    /// to the engine (its events drive the playfield wiring), which is the same synchronous path
    /// raw keyboard input takes.
    ///
    /// <para>Loaded with <see cref="TypeBeatModGatekeeper"/>, because the streak only ever accrues
    /// on the REJECTION path and backlog 107 moved that path behind the mod. That the guard has
    /// therefore left ordinary play is deliberate and is pinned the other way round by
    /// <see cref="TestSceneTypeBeatGatekeeper"/>.</para>
    /// </summary>
    public partial class TestSceneTypeBeatFail : PlayerTestScene
    {
        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        // This scene tests the fail path itself, so the base must NOT auto-append NoFail (which it
        // now does because the ruleset provides a NoFail mod).
        protected override bool AllowFail => true;

        protected override bool HasCustomSteps => true;

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var line = new LyricLine
            {
                RawText = "ab",
                StartTime = 0,
                EndTime = 600000,
                SingEndTime = 300000,
                Units = new[] { new TimedUnit { Text = "ab", StartTime = 0, EndTime = 300000 } },
            };

            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>
                {
                    new TypeBeatHitObject
                    {
                        StartTime = 0,
                        LineIndex = 0,
                        Line = line,
                        Granularity = TimingGranularity.Line,
                    },
                },
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            return beatmap;
        }

        [Test]
        public void TestThirteenConsecutiveWrongKeysFail()
        {
            AddStep("load player with Gatekeeper", () => LoadPlayer(new Mod[] { new TypeBeatModGatekeeper() }));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddAssert("the mod put the engine in strict mode", () => !playfield.Engine.AllowWrongInput);

            AddUntilStep("line active", () => playfield.Engine.ActiveLineIndex == 0);

            AddStep("mash 12 wrong keys", () =>
            {
                for (int i = 0; i < TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK - 1; i++)
                    playfield.Engine.ProcessKey('x', 1000 + i);
            });

            AddAssert("streak at 12", () => playfield.Engine.ConsecutiveWrongKeys == TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK - 1);
            AddAssert("health nearly empty", () => Player.GameplayState.HealthProcessor.Health.Value < 0.1);
            AddAssert("not failed yet", () => !Player.GameplayState.HasFailed);

            double healthBeforeRecovery = 0;
            AddStep("capture health", () => healthBeforeRecovery = Player.GameplayState.HealthProcessor.Health.Value);
            AddStep("correct key ends the streak", () => playfield.Engine.ProcessKey('a', 2000));
            AddAssert("health recovered", () => Player.GameplayState.HealthProcessor.Health.Value > healthBeforeRecovery);
            AddAssert("streak reset", () => playfield.Engine.ConsecutiveWrongKeys == 0);

            AddStep("mash 13 wrong keys", () =>
            {
                for (int i = 0; i < TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK; i++)
                    playfield.Engine.ProcessKey('x', 3000 + i);
            });

            AddUntilStep("player failed", () => Player.GameplayState.HasFailed);
        }
    }
}
