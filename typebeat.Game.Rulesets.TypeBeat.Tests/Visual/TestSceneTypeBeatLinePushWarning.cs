// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Testing;
using osu.Framework.Timing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatLinePushWarning : OsuTestScene
    {
        private DrawableTypeBeatRuleset drawableRuleset = null!;
        private ManualClock songClock = null!;
        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)drawableRuleset.Playfield;
        private TypingEngine engine => playfield.Engine;
        private LinePushWarning warning => drawableRuleset.Overlays.ChildrenOfType<LinePushWarning>().Single();
        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        [TearDownSteps]
        public void RestoreSettings() => AddStep("restore warning defaults", () =>
        {
            config.SetValue(TypeBeatRulesetSetting.LinePushWarningEdges, false);
            config.SetValue(TypeBeatRulesetSetting.LyricOffsetMs, 0d);
            config.SetValue(TypeBeatRulesetSetting.ManualNewlines, true);
        });

        private void load(double grace = 0, bool pinned = false, bool manual = true, bool enabled = true)
        {
            AddStep("create gameplay with screen-edge warning", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.LinePushWarningEdges, enabled);
                config.SetValue(TypeBeatRulesetSetting.LyricOffsetMs, 0d);
                config.SetValue(TypeBeatRulesetSetting.ManualNewlines, manual);
                var ruleset = new TypeBeatRuleset();
                var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;
                addLine(beatmap, 0, "ab", 1000, 6000, 1000, 2000, grace);
                addLine(beatmap, 1, "cd", 6000, 30000, 6100, 9000, 0);
                Mod[] mods = pinned ? new Mod[] { new TypeBeatModFletcher() } : Array.Empty<Mod>();
                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, mods);
                songClock = new ManualClock { CurrentTime = 0 };
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(songClock),
                    Child = drawableRuleset = new DrawableTypeBeatRuleset(ruleset, playable, mods),
                };
            });
            AddUntilStep("warning loaded", () => drawableRuleset.IsLoaded && warning.IsLoaded);
        }

        private static void addLine(Beatmap map, int index, string text, double start, double end, double unitStart, double unitEnd, double grace)
        {
            map.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Granularity = TimingGranularity.Word,
                Line = new LyricLine
                {
                    RawText = text, StartTime = start, EndTime = end, SingEndTime = unitEnd, SealGraceMs = grace,
                    Units = new[] { new TimedUnit { Text = text, StartTime = unitStart, EndTime = unitEnd } },
                },
            });
        }

        private void at(string caption, double time) => AddStep(caption, () => songClock.CurrentTime = time);
        private bool alphaIs(float value) => Math.Abs(warning.Alpha - value) < 0.0001f;

        [TestCase(0)]
        [TestCase(700)]
        public void EdgesIntensifyTowardActualCutoffAndFadeAfterPush(double grace)
        {
            load(grace);
            double opens = 6000 + grace;
            double cutoff = opens + TypingEngine.FLETCHER_DRAG_GRACE_MS;
            at("before borrowed time", opens - 1);
            AddUntilStep("no early warning", () => engine.ActiveLineIndex == 0 && alphaIs(0));
            at("countdown begins", opens);
            AddUntilStep("countdown begins transparent", () => alphaIs(0));
            at("quarterway to cutoff", opens + 375);
            AddUntilStep("edges fade in gradually", () => alphaIs(0.1f));
            at("halfway to cutoff", opens + 750);
            AddUntilStep("urgency increases", () => alphaIs(0.2f));
            at("just before actual push", cutoff - 1);
            AddUntilStep("warning remains until actual cutoff", () => warning.Alpha > 0.399f && engine.ActiveLineIndex == 0);
            at("actual cutoff", cutoff);
            AddUntilStep("line advances without snapping the edges off", () => engine.ActiveLineIndex == 1 && warning.Alpha > 0.399f);
            AddWaitStep("release follows the paused song clock", 3);
            AddAssert("frozen clock cannot advance the release", () => warning.Alpha > 0.399f);
            at("halfway through release", cutoff + 75);
            AddUntilStep("red edges fade smoothly", () => warning.Alpha > 0.09f && warning.Alpha < 0.11f);
            at("release finishes", cutoff + 150);
            AddUntilStep("edges finish fading quickly", () => alphaIs(0));
            AddAssert("uses the same visual as osu low-health warning", () => warning is ScreenEdgeWarning && warning.Colour.Equals((osu.Framework.Graphics.Colour.ColourInfo)new OsuColour().Red));
        }

        [Test]
        public void ToggleIsLiveAndDoesNotChangeTypingState()
        {
            load(enabled: false);
            at("halfway through grace", 6750);
            AddUntilStep("option starts off", () => engine.ActiveLineIndex == 0 && alphaIs(0));
            AddStep("enable warning", () => config.SetValue(TypeBeatRulesetSetting.LinePushWarningEdges, true));
            AddUntilStep("warning reflects current countdown", () => alphaIs(0.2f));
            AddStep("disable warning", () => config.SetValue(TypeBeatRulesetSetting.LinePushWarningEdges, false));
            AddUntilStep("warning clears immediately", () => alphaIs(0));
            AddAssert("toggle cannot advance the caret or mark letters", () => engine.ActiveLineIndex == 0 && engine.CaretIndex == 0
                && engine.Lines[0].Cells.All(cell => !cell.IsTypeable || cell.State == CellState.Untyped));
        }

        [Test]
        public void ManualFinishedLineWarnsAndVoluntaryAdvanceFadesIt()
        {
            load();
            at("first word", 1000);
            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
            AddStep("finish line and wait for manual newline", () =>
            {
                engine.ProcessKey('a', songClock.CurrentTime);
                engine.ProcessKey('b', songClock.CurrentTime);
            });
            at("halfway through grace", 6750);
            AddUntilStep("parked complete line still warns", () => engine.IsLineComplete && alphaIs(0.2f));
            AddStep("advance voluntarily", () => engine.ProcessKey(' ', songClock.CurrentTime));
            AddUntilStep("moving on begins a smooth release", () => engine.ActiveLineIndex == 1 && alphaIs(0.2f));
            at("partway through voluntary release", 6800);
            AddUntilStep("voluntary release is gradual too", () => warning.Alpha > 0 && warning.Alpha < 0.2f);
            at("voluntary release finishes", 6900);
            AddUntilStep("voluntary release finishes quickly", () => alphaIs(0));
        }

        [Test]
        public void PinnedCaretHasNoBorrowedTimeWarning()
        {
            load(pinned: true);
            at("before immediate handoff", 5750);
            AddUntilStep("pinned line cannot warn", () => engine.ActiveLineIndex == 0 && engine.DragCutoffAt == null && alphaIs(0));
            at("pinned handoff", 6000);
            AddUntilStep("no warning after immediate handoff", () => engine.ActiveLineIndex == 1 && alphaIs(0));
        }

        [Test]
        public void LyricOffsetAndSeekingUseTheTypingClock()
        {
            load();
            AddStep("delay lyrics by 200ms", () => config.SetValue(TypeBeatRulesetSetting.LyricOffsetMs, 200d));
            at("audio halfway through grace", 6750);
            AddUntilStep("warning uses offset lyric time", () => alphaIs(ScreenEdgeWarning.MAX_ALPHA * 550 / 1500));
            at("seek back before warning", 6100);
            AddUntilStep("seek reconstructs transparency", () => alphaIs(0));
            at("seek forward into warning", 6950);
            AddUntilStep("seek reconstructs current urgency", () => alphaIs(0.2f));
            AddWaitStep("frozen clock stays stable", 3);
            AddAssert("pause cannot advance the fade", () => alphaIs(0.2f));
            AddAssert("overlay covers the screen independently of lyric bounds", () => Vector2.Distance(warning.DrawSize, drawableRuleset.DrawSize) < 0.01f);
            AddAssert("only the edges contain the gradient", () => warning.ChildrenOfType<Box>().Count() == 2
                && warning.ChildrenOfType<Box>().All(box => box.Width == 0.2f));
        }
    }
}
