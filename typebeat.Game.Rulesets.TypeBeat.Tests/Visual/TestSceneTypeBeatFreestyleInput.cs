// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Timing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatFreestyleInput : OsuManualInputManagerTestScene
    {
        private ManualClock songClock = null!;
        private DrawableTypeBeatRuleset drawableRuleset = null!;
        private JudgementType? lastJudgement;
        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private void load(string first, string second)
        {
            AddStep("create timed lines with word skipping and manual newlines", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.SpaceSkipsWord, true);
                config.SetValue(TypeBeatRulesetSetting.ManualNewlines, true);
                var ruleset = new TypeBeatRuleset();
                var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;
                for (int i = 0; i < 2; i++)
                {
                    string text = i == 0 ? first : second;
                    double start = i == 0 ? 0 : 4000;
                    var line = new LyricLine
                    {
                        RawText = text, StartTime = start, EndTime = i == 0 ? 4000 : 6000, SingEndTime = start + 500,
                        Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = start + 500 } },
                    };
                    beatmap.HitObjects.Add(new TypeBeatHitObject
                    {
                        StartTime = start, LineIndex = i, Line = line, Granularity = TimingGranularity.Word,
                    });
                }
                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());
                songClock = new ManualClock { CurrentTime = 0 };
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(songClock),
                    Child = drawableRuleset = new DrawableTypeBeatRuleset(ruleset, playable),
                };
                lastJudgement = null;
            });
            AddUntilStep("first line active", () => drawableRuleset.IsLoaded && engine.ActiveLineIndex == 0);
            AddStep("observe actual input judgements", () => engine.CharJudged += result => lastJudgement = result.Type);
        }

        [TestCase(2700, false)]
        [TestCase(2900, true)]
        [TestCase(3800, true)]
        public void EarlyFreestyleSpaceUsesTheLiveInputHandler(double time, bool accepted)
        {
            load("a", "&&b");
            AddStep("finish the first line", () => InputManager.Key(Key.A));
            AddStep("move song into the next entry window", () => songClock.CurrentTime = time);
            AddStep("manually enter the next line", () => InputManager.Key(Key.Enter));
            AddAssert("parked untouched ahead of the song", () => engine.ActiveLineIndex == 1 && engine.ActiveLineUntouched
                && !engine.SongIsOnTheCaretsLine && !engine.AwaitingEntry);
            AddStep("press space on the first freestyle slot", () => InputManager.Key(Key.Space));
            AddAssert("space is consumed only inside the freestyle window", () => accepted
                ? engine.CaretIndex == 1 && engine.Lines[1].Cells[0].State == CellState.Correct && engine.Lines[1].Cells[0].TypedChar == ' '
                : engine.CaretIndex == 0 && engine.Lines[1].Cells[0].State == CellState.Untyped);
            AddAssert("space never abandons either freestyle slot", () => engine.Lines[1].Cells[1].State == CellState.Untyped);
            if (time == 3800)
                AddAssert("200ms early receives Great", () => engine.Lines[1].Cells[0].JudgedDelta == -200 && lastJudgement == JudgementType.Great);
        }

        [Test]
        public void WrongCharacterCannotStartTheLineAfterFreestyle()
        {
            load("&", "cd");
            AddStep("fill freestyle with space", () => InputManager.Key(Key.Space));
            AddAssert("freestyle is complete", () => engine.IsLineComplete && engine.Lines[0].Cells[0].TypedChar == ' ');
            AddStep("open the next entry window", () => songClock.CurrentTime = 3800);
            AddStep("press an incorrect character", () => InputManager.Key(Key.Z));
            AddAssert("caret waits on the freestyle line", () => engine.ActiveLineIndex == 0 && engine.IsLineComplete
                && engine.Lines[1].Cells[0].State == CellState.Untyped);
            AddStep("press the correct next character", () => InputManager.Key(Key.C));
            AddAssert("correct character starts and fills the next line", () => engine.ActiveLineIndex == 1 && engine.CaretIndex == 1
                && engine.Lines[1].Cells[0].State == CellState.Correct);
        }
    }
}
