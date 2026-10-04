// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Objects.Drawables;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// What the hit error meter reads, through the live drawable ruleset: each char result's
    /// <c>TimeOffset</c> is the delta the ENGINE graded (so a Great lands in the Great band even when
    /// it was typed far from the character's point target, inside its sung syllable), and the hit
    /// windows the meter draws its bands from are the engine's effective, mod-scaled ladder.
    /// </summary>
    public partial class TestSceneTypeBeatHitErrorOffsets : OsuTestScene
    {
        private const string text = "abcd";

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;

        private DrawableTypeBeatCharObject charDrawable(int cellIndex)
            => drawableRuleset.ChildrenOfType<DrawableTypeBeatCharObject>().Single(d => d.HitObject.CellIndex == cellIndex);

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create drawable ruleset", () =>
            {
                var ruleset = new TypeBeatRuleset();

                // One syllable group sung over five minutes, so the cells' point targets sit a minute
                // or more apart while the whole span grades as on time.
                var line = new LyricLine
                {
                    RawText = text,
                    StartTime = 0,
                    EndTime = 600000,
                    SingEndTime = 300000,
                    Units = new[] { new TimedUnit { Text = text, StartTime = 0, EndTime = 300000 } },
                };

                var beatmap = new Beatmap
                {
                    HitObjects = new List<Rulesets.Objects.HitObject>
                    {
                        new TypeBeatHitObject
                        {
                            StartTime = line.StartTime,
                            LineIndex = 0,
                            Line = line,
                            Granularity = TimingGranularity.Word,
                        },
                    },
                };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());

                Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable);
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
        }

        [Test]
        public void TestAPressInsideTheSungSpanLandsOnTheCentre()
        {
            AddStep("type 'a' on the span's start", () => engine.ProcessKey('a', 0));
            AddStep("type 'b' deep inside the span", () => engine.ProcessKey('b', 200000));

            AddUntilStep("'b' judged", () => charDrawable(1).Judged);

            AddAssert("the engine graded it dead on", () => engine.Lines[0].Cells[1].JudgedDelta == 0);
            AddAssert("it scored a Great", () => charDrawable(1).Result.Type == HitResult.Great);
            AddAssert("and its offset is the graded one, not the distance from its point target",
                () => charDrawable(1).Result.TimeOffset == 0 && Math.Abs(200000 - charDrawable(1).HitObject.StartTime) > 1000);
        }

        [Test]
        public void TestAPressPastTheSpanCarriesItsGradedDelta()
        {
            AddStep("type up to 'c'", () =>
            {
                engine.ProcessKey('a', 0);
                engine.ProcessKey('b', 100000);
            });
            AddStep("type 'c' 250 ms after the span ends", () => engine.ProcessKey('c', 300250));

            AddUntilStep("'c' judged", () => charDrawable(2).Judged);

            AddAssert("it scored an Ok", () => charDrawable(2).Result.Type == HitResult.Ok);
            AddAssert("its offset is exactly the engine's delta", () => charDrawable(2).Result.TimeOffset == engine.Lines[0].Cells[2].JudgedDelta);
            AddAssert("which is 250", () => charDrawable(2).Result.TimeOffset == 250);
        }

        [Test]
        public void TestTheMeterBandsFollowTheEnginesScaledWindows()
        {
            AddAssert("default ladder to start with", () => drawableRuleset.FirstAvailableHitWindows.WindowFor(HitResult.Great) == SyncWindows.Default.GreatLate);

            AddStep("double the engine's windows", () => engine.WindowScale = 2);

            AddAssert("the bands double with them", () => drawableRuleset.FirstAvailableHitWindows.WindowFor(HitResult.Great) == engine.Windows.GreatLate
                                                          && engine.Windows.GreatLate == SyncWindows.Default.GreatLate * 2);
            AddAssert("on every tier", () => drawableRuleset.FirstAvailableHitWindows.WindowFor(HitResult.Meh) == engine.Windows.MehLate);
        }
    }
}
