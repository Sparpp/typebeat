// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Input.States;
using osu.Framework.Testing;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Screens.Edit.Components;
using typebeat.Game.Screens.Play;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatEditor
    {
        [TestCase(0.25, false)]
        [TestCase(0.5, false)]
        [TestCase(0.75, false)]
        [TestCase(1, false)]
        [TestCase(0.5, true)]
        public void TestGameplayInheritsTheEditorPlaybackSpeedAndRestoresIt(double editorRate, bool doubleTime)
        {
            double modRate = doubleTime ? 1.5 : 1;
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddStep("select the editor playback speed", () =>
            {
                Editor.Mods.Value = doubleTime ? new Mod[] { new TypeBeatModDoubleTime() } : Array.Empty<Mod>();
                EditorClock.Stop();
                EditorClock.Seek(1500);
                Editor.ChildrenOfType<PlaybackControl>().Single().Tempo.Value = editorRate;
            });
            AddAssert("editor state captures its selected speed", () => Editor.GetState().PlaybackRate == editorRate);
            AddAssert("editor audio uses that tempo", () => Math.Abs(EditorClock.AudioAdjustments.AggregateTempo.Value - editorRate) < 1e-6);
            AddStep("start Test Play", () => Editor.TestGameplay());
            AddUntilStep("Test Play clock runs at the inherited rate", () => editorPlayer()?.IsLoaded == true
                && editorPlayer()!.ChildrenOfType<MasterGameplayClockContainer>().Any(clock =>
                    Math.Abs(clock.Rate - editorRate * modRate) < 1e-6));
            AddAssert("editor context records the selected speed", () => editorPlayer()!.TestPlayback.Rate == editorRate);
            AddAssert("editor tempo stays separate from gameplay mods", () =>
            {
                var clock = editorPlayer()!.ChildrenOfType<MasterGameplayClockContainer>().Single();
                return Math.Abs(clock.AdjustmentsFromPlayback.AggregateTempo.Value - editorRate) < 1e-6
                       && Math.Abs(clock.GetTrueGameplayRate() - modRate) < 1e-6;
            });
            AddStep("return to the editor", () =>
                ((IKeyBindingHandler<GlobalAction>)editorPlayer()!).OnPressed(
                    new KeyBindingPressEvent<GlobalAction>(new InputState(), GlobalAction.EditorTestPlayQuickExitToInitialTime)));
            AddUntilStep("editor resumed with its speed", () => editorPlayer() == null
                && EditorClock.PlaybackRate.Value == editorRate
                && Editor.ChildrenOfType<PlaybackControl>().Single().Tempo.Value == editorRate);
            AddStep("clear gameplay mods", () => Editor.Mods.Value = Array.Empty<Mod>());
        }
    }
}
