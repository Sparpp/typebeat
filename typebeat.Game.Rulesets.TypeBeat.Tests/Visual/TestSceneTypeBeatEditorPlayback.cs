// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Framework.Utils;
using osuTK;
using osuTK.Input;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit.Components;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The editor bottom bar's playback-speed section: a rounded slider plus a typeable whole-percent
    /// box, with no play/pause button (Space and the transport shortcuts toggle playback).
    /// </summary>
    public partial class TestSceneTypeBeatEditorPlayback : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>(),
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Editor";
            beatmap.BeatmapInfo.Metadata.Title = "Playback";

            addLine(beatmap, 0, "hello world", 1000, 3000, 3000);
            addLine(beatmap, 1, "second line", 3000, 5000, 5000);

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
        public void TestSectionHasNoPlayButton()
        {
            AddUntilStep("playback section present", () => playback().IsLoaded);

            AddAssert("no play/pause icon button", () => !playback().ChildrenOfType<IconButton>().Any());
            AddAssert("slider present", () => playback().Slider.IsLoaded);
            AddAssert("speed box present", () => playback().SpeedField.IsLoaded);
            AddAssert("starts at 100 percent", () =>
                playback().Tempo.Value == 1.0 && playback().SpeedField.Text == "100%");
        }

        /// <summary>
        /// The section's layout, as the owner asked for it (backlog 392 follow-up): the slider line
        /// sits at the MIDDLE of the text box's height, the line reaches further right than it used
        /// to (and the box sits past its right end, not under it), and the drag button is 40 percent
        /// smaller than stock. Pinned so a later spacing tweak cannot quietly undo either.
        /// </summary>
        [Test]
        public void TestSliderLineIsInlineWithTheBoxAndTheNubIsSmaller()
        {
            AddUntilStep("playback section present", () => playback().IsLoaded);

            AddAssert("slider line is level with the box's vertical middle", () =>
            {
                float lineY = playback().Slider.ScreenSpaceDrawQuad.Centre.Y;
                float boxCentreY = playback().SpeedField.ScreenSpaceDrawQuad.Centre.Y;
                return Precision.AlmostEquals(lineY, boxCentreY, 1.5);
            });

            AddAssert("the slider ends before the box's left edge", () =>
                playback().Slider.ScreenSpaceDrawQuad.TopRight.X < playback().SpeedField.ScreenSpaceDrawQuad.TopLeft.X);

            // Screen-space size, not DrawWidth: DrawWidth excludes the nub's own Scale, so it would
            // read the stock 50 regardless. The mock clock matches the game's layout scale here.
            AddAssert("the drag button is 40 percent smaller than stock", () =>
                Precision.AlmostEquals(playback().Slider.DragNub.ScreenSpaceDrawQuad.Width, typebeat.Game.Graphics.UserInterface.Nub.DEFAULT_EXPANDED_SIZE * 0.6f, 1.0)
                && Precision.AlmostEquals(playback().Slider.DragNub.ScreenSpaceDrawQuad.Height, typebeat.Game.Graphics.UserInterface.Nub.HEIGHT * 0.6f, 1.0));

            // The fill must MEET the (smaller) handle, not stop short of it: RoundedSliderBar's fill
            // math reads Nub.DrawWidth, which excludes the nub's Scale, so before the override the
            // filled bar ended well before the handle and the dark track started late, leaving a pale
            // exposed strip around it. Pin that the filled region's right edge reaches into the handle.
            AddAssert("the filled bar reaches the drag button", () =>
            {
                float fillRight = playback().Slider.FillBar.ScreenSpaceDrawQuad.TopRight.X;
                var nub = playback().Slider.DragNub.ScreenSpaceDrawQuad;
                return fillRight > nub.TopLeft.X && fillRight < nub.BottomRight.X;
            });
        }

        [Test]
        public void TestSliderEndsAndDefault()
        {
            AddUntilStep("playback section present", () => playback().IsLoaded);

            clickSliderAtFraction(0.98f);
            AddUntilStep("slider right end is 200 percent", () =>
                playback().Tempo.Value == PlaybackControl.MAX_TEMPO && playback().SpeedField.Text == "200%");

            clickSliderAtFraction(0.02f);
            AddUntilStep("slider left end is the audio floor", () =>
                playback().Tempo.Value == PlaybackControl.MIN_TEMPO
                && playback().SpeedField.Text == $"{PlaybackControl.MIN_TEMPO * 100:0}%");

            AddAssert("the floor is 6 percent, not 1, because BASS tempo throws below it", () =>
                PlaybackControl.MIN_TEMPO == 0.06);

            AddStep("right-click the slider", () =>
            {
                InputManager.MoveMouseTo(slider());
                InputManager.PressButton(MouseButton.Right);
                InputManager.ReleaseButton(MouseButton.Right);
            });
            AddUntilStep("right-click resets to 100 percent", () =>
                playback().Tempo.Value == 1.0 && playback().SpeedField.Text == "100%");
        }

        [Test]
        public void TestTypedValuesClampRoundAndRevert()
        {
            AddUntilStep("playback section present", () => playback().IsLoaded);

            enterSpeed("150");
            AddUntilStep("150 means 150 percent", () =>
                playback().Tempo.Value == 1.5 && playback().SpeedField.Text == "150%");

            enterSpeed("150%");
            AddUntilStep("a percent sign is accepted", () =>
                playback().Tempo.Value == 1.5 && playback().SpeedField.Text == "150%");

            enterSpeed("0");
            AddUntilStep("0 clamps up to the floor", () =>
                playback().Tempo.Value == PlaybackControl.MIN_TEMPO);

            enterSpeed("999");
            AddUntilStep("999 clamps down to 200", () =>
                playback().Tempo.Value == PlaybackControl.MAX_TEMPO && playback().SpeedField.Text == "200%");

            enterSpeed("abc");
            AddUntilStep("garbage reverts to the current value", () =>
                playback().Tempo.Value == PlaybackControl.MAX_TEMPO && playback().SpeedField.Text == "200%");
        }

        [Test]
        public void TestFieldAndSliderMirror()
        {
            AddUntilStep("playback section present", () => playback().IsLoaded);

            enterSpeed("150");
            AddUntilStep("typing drives the slider's bindable", () =>
                playback().Slider.Current.Value == 1.5);

            clickSliderAtFraction(0.5f);
            AddUntilStep("dragging drives the field text", () =>
                playback().SpeedField.Text == $"{playback().Tempo.Value * 100:0}%");
            AddAssert("and the slider still reads the same bindable", () =>
                Precision.AlmostEquals(playback().Slider.Current.Value, playback().Tempo.Value, 1e-6));
        }

        [Test]
        public void TestEditorClockRateFollowsTheTempo()
        {
            AddUntilStep("playback section present", () => playback().IsLoaded);

            AddAssert("clock runs at 1x initially", () => Precision.AlmostEquals(((IAdjustableClock)EditorClock).Rate, 1.0, 1e-3));

            enterSpeed("200");
            AddUntilStep("clock rate follows to 2x", () => Precision.AlmostEquals(((IAdjustableClock)EditorClock).Rate, 2.0, 1e-3));

            enterSpeed("150");
            AddUntilStep("clock rate follows to 1.5x", () => Precision.AlmostEquals(((IAdjustableClock)EditorClock).Rate, 1.5, 1e-3));
        }

        [Test]
        public void TestSpaceStillTogglesPlayback()
        {
            AddUntilStep("playback section present", () => playback().IsLoaded);

            AddStep("pause", () => EditorClock.Stop());
            AddAssert("paused", () => !EditorClock.IsRunning);

            AddStep("press space", () => InputManager.Key(Key.Space));
            AddUntilStep("playing", () => EditorClock.IsRunning);

            AddStep("press space again", () => InputManager.Key(Key.Space));
            AddUntilStep("paused again", () => !EditorClock.IsRunning);
        }

        private PlaybackControl playback() => Editor.ChildrenOfType<PlaybackControl>().Single();

        private PlaybackControl.SpeedSlider slider() => playback().Slider;

        /// <summary>Click on the slider's track at a fraction of its width (0 = left edge, 1 = right edge).</summary>
        private void clickSliderAtFraction(float fraction)
        {
            AddStep($"click slider at {fraction:0.00}", () =>
            {
                var q = slider().ScreenSpaceDrawQuad;
                InputManager.MoveMouseTo(q.TopLeft + new Vector2(q.Width * fraction, q.Height * 0.5f));
                InputManager.Click(MouseButton.Left);
            });
        }

        /// <summary>Focus the speed box, type a value and commit it with Enter.</summary>
        private void enterSpeed(string text)
        {
            AddStep($"enter '{text}'", () =>
            {
                InputManager.MoveMouseTo(playback().SpeedField);
                InputManager.Click(MouseButton.Left);
                playback().SpeedField.Text = text;
                InputManager.Key(Key.Enter);
            });
        }
    }
}
