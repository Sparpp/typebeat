// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using System;
using NUnit.Framework;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit.Timing;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Tests.Visual;
using osuTK.Input;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneEditorBpm : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.ControlPointInfo.Add(123.456, new TimingControlPoint { BeatLength = 500 });
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Granularity = TimingGranularity.Word,
                Line = new LyricLine
                {
                    RawText = "hello world",
                    StartTime = 1000,
                    EndTime = 3000,
                    SingEndTime = 3000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "hello", StartTime = 1000, EndTime = 1900 },
                        new TimedUnit { Text = "world", StartTime = 2000, EndTime = 3000 },
                    },
                },
            });
            return beatmap;
        }

        [Test]
        public void TimingControlsCommitAndUndoWithWaveformGrid()
        {
            AddStep("open timing", () => Editor.Mode.Value = EditorScreenMode.Timing);
            AddUntilStep("timing loaded", () => Editor.ChildrenOfType<TimingScreen>().Any(s => s.IsLoaded));
            AddUntilStep("BPM field loaded", () => Editor.ChildrenOfType<FormTextBox>().Any(b => b.Caption.ToString() == "BPM" && b.IsLoaded));
            AddStep("enter decimal BPM", () => field("BPM").Current.Value = "137.123456");
            AddStep("apply", () => click("Apply"));
            AddUntilStep("decimal BPM applied", () => Math.Abs(EditorBeatmap.ControlPointInfo.TimingPoints.Single().BPM - 137.123456) < 1e-8);
            AddAssert("lyric timings stay put", () => ((TypeBeatHitObject)EditorBeatmap.HitObjects[0]).Line.StartTime == 1000);
            AddStep("undo", () => Editor.Undo());
            AddUntilStep("BPM restored", () => EditorBeatmap.ControlPointInfo.TimingPoints.Single().BPM == 120);
            AddUntilStep("field follows undo", () => field("BPM").Current.Value == "120");
        }

        [Test]
        public void OverlayAndSnapAreSeparateModes()
        {
            AddUntilStep("compose loaded", () => Editor.ChildrenOfType<LyricTimeline>().Any(s => s.IsLoaded));
            AddAssert("lyric grid starts hidden", () => lyricGrid().Alpha == 0);
            AddStep("enable overlay", () => click("Overlay Grid: Off"));
            AddUntilStep("overlay on", () => Editor.ChildrenOfType<RoundedButton>().Any(b => b.Text.ToString() == "Overlay Grid: On"));
            AddUntilStep("grid draws over lyrics", () => lyricGrid().IsPresent && lyricGrid().ChildrenOfType<Box>().Any(b => b.Alpha > 0));
            AddStep("hide overlay", () => click("Overlay Grid: On"));
            AddUntilStep("lyric grid hidden", () => !lyricGrid().IsPresent);
            AddStep("show overlay again", () => click("Overlay Grid: Off"));
            AddUntilStep("grid reappears", () => lyricGrid().IsPresent && lyricGrid().ChildrenOfType<Box>().Any(b => b.Alpha > 0));
            AddAssert("snap still off", () => Editor.ChildrenOfType<RoundedButton>().Any(b => b.Text.ToString() == "Snap to Grid: Off"));
            AddAssert("reference takes no input", () => Editor.ChildrenOfType<TimingGrid>().All(g => !g.HandlePositionalInput));
            AddStep("enable snapping", () => click("Snap to Grid: Off"));
            AddUntilStep("snapping on", () => Editor.ChildrenOfType<RoundedButton>().Any(b => b.Text.ToString() == "Snap to Grid: On"));
        }

        [Test]
        public void EmptyMapCanCreateItsFirstTimingPoint()
        {
            AddStep("remove timing", () => EditorBeatmap.ControlPointInfo.Clear());
            AddStep("open timing", () => Editor.Mode.Value = EditorScreenMode.Timing);
            AddUntilStep("timing fields loaded", () => Editor.ChildrenOfType<TimingScreen>().Any(s => s.ChildrenOfType<FormTextBox>().Count(b => b.IsLoaded) == 3));
            AddAssert("default BPM", () => field("BPM").Current.Value == "120");
            AddAssert("default meter", () => field("Beats per bar").Current.Value == "4");
            AddStep("create first point", () => click("Apply"));
            AddUntilStep("timing exists", () => EditorBeatmap.ControlPointInfo.TimingPoints.Count == 1);
        }

        [Test]
        public void WordEdgeDragSnapsOnlyWhenEnabled()
        {
            AddUntilStep("word strip loaded", () => Editor.ChildrenOfType<LyricTimeline>().Any(s => s.IsLoaded));
            AddStep("select lyric line", () => Editor.ChildrenOfType<LyricComposeScreen>().Single().EditState.SelectedLine.Value = (TypeBeatHitObject)EditorBeatmap.HitObjects[0]);
            AddStep("enable snapping", () => click("Snap to Grid: Off"));
            dragWordEnd(1900, 1840);
            AddUntilStep("end on grid", () => Math.Abs(wordEnd() - EditorBeatmap.ControlPointInfo.GetClosestSnappedTime(1840, Editor.BeatDivisor)) < 0.001);
            AddStep("disable snapping", () => click("Snap to Grid: On"));
            dragWordEnd(() => wordEnd(), 1810);
            AddUntilStep("end follows cursor freely", () => Math.Abs(wordEnd() - 1810) < 0.01);
        }

        [Test]
        public void NoteLengthsStaySynchronizedAcrossTabs()
        {
            AddUntilStep("grid controls loaded", () => Editor.ChildrenOfType<LyricComposeScreen>().Single().ChildrenOfType<TimingGridControls>().Any(c => c.IsLoaded));
            AddStep("choose half notes", () => subdivision(EditorScreenMode.Compose).Current.Value = -2);
            AddStep("open timing", () => Editor.Mode.Value = EditorScreenMode.Timing);
            AddUntilStep("timing toolbar loaded", () => Editor.ChildrenOfType<TimingScreen>().Any(s => s.ChildrenOfType<TimingGridControls>().Any(c => c.IsLoaded)));
            AddAssert("half note selected", () => subdivision(EditorScreenMode.Timing).Current.Value == -2);
            AddStep("choose eighth notes", () => subdivision(EditorScreenMode.Timing).Current.Value = 2);
            AddAssert("beat divisor follows", () => Editor.BeatDivisor == 2);
            AddStep("back to compose", () => Editor.Mode.Value = EditorScreenMode.Compose);
            AddAssert("eighth note selected", () => subdivision(EditorScreenMode.Compose).Current.Value == 2);
        }

        [Test]
        public void DropdownFitsToolbarAndSelectsAboveWaveform()
        {
            AddUntilStep("compose toolbar loaded", () => Editor.ChildrenOfType<LyricComposeScreen>().Single().ChildrenOfType<TimingGridControls>().Any(c => c.IsLoaded));
            AddAssert("header fits toolbar", () =>
            {
                var dropdown = subdivision(EditorScreenMode.Compose);
                var header = dropdown.ChildrenOfType<DropdownHeader>().Single();
                var toolbar = Editor.ChildrenOfType<LyricComposeScreen>().Single().ChildrenOfType<TimingGridControls>().Single();
                return header.ScreenSpaceDrawQuad.AABBFloat.Bottom <= toolbar.ScreenSpaceDrawQuad.AABBFloat.Bottom;
            });
            AddStep("open note menu", () =>
            {
                InputManager.MoveMouseTo(subdivision(EditorScreenMode.Compose).ChildrenOfType<DropdownHeader>().Single());
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("menu open", () => subdivision(EditorScreenMode.Compose).ChildrenOfType<OsuDropdown<int>.OsuDropdownMenu>().Single().State == MenuState.Open);
            AddAssert("menu item overlaps waveform", () =>
            {
                var label = subdivision(EditorScreenMode.Compose).ChildrenOfType<OsuSpriteText>().First(t => t.Text.ToString() == "1/2 note");
                var waveform = Editor.ChildrenOfType<LyricComposeScreen>().Single().TimelineArea.Timeline.ScreenSpaceDrawQuad.AABBFloat;
                return label.ScreenSpaceDrawQuad.AABBFloat.Centre.Y > waveform.Top && label.ScreenSpaceDrawQuad.AABBFloat.Centre.Y < waveform.Bottom;
            });
            AddStep("select half note over waveform", () =>
            {
                var label = subdivision(EditorScreenMode.Compose).ChildrenOfType<OsuDropdown<int>.OsuDropdownMenu>().Single().ChildrenOfType<OsuSpriteText>().First(t => t.Text.ToString() == "1/2 note");
                InputManager.MoveMouseTo(label);
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("half note selected", () => subdivision(EditorScreenMode.Compose).Current.Value == -2);
            AddAssert("menu closed", () => subdivision(EditorScreenMode.Compose).ChildrenOfType<OsuDropdown<int>.OsuDropdownMenu>().Single().State == MenuState.Closed);
        }

        [Test]
        public void MetronomeFollowsPlaybackAndPauses()
        {
            var ticks = new List<(int beat, double time)>();
            int count = 0;
            AddUntilStep("toolbar loaded", () => Editor.ChildrenOfType<TimingGridControls>().Any(c => c.IsLoaded));
            AddStep("listen for ticks", () => Editor.ChildrenOfType<EditorMetronome>().Single().Ticked += beat => ticks.Add((beat, EditorClock.CurrentTime)));
            AddStep("whole-note grid", () => subdivision(EditorScreenMode.Compose).Current.Value = -4);
            AddStep("enable metronome", () => click("Metronome: Off"));
            AddStep("play from start", () => { EditorClock.Seek(0); EditorClock.Start(); });
            AddUntilStep("hear two beats", () => ticks.Count >= 2);
            AddAssert("ticks follow BPM and fractional offset", () => ticks.All(t =>
            {
                double index = Math.Round((t.time - 123.456) / 500);
                return t.beat == (int)index % 4 && Math.Abs(t.time - (123.456 + index * 500)) < 16;
            }));
            AddStep("pause playback", () => { EditorClock.Stop(); count = ticks.Count; });
            AddWaitStep("wait while paused", 5);
            AddAssert("pause is silent", () => ticks.Count == count);
            AddStep("open timing", () => Editor.Mode.Value = EditorScreenMode.Timing);
            AddUntilStep("timing metronome stays enabled", () => Editor.ChildrenOfType<TimingScreen>().Any(s => s.ChildrenOfType<RoundedButton>().Any(b => b.Text.ToString() == "Metronome: On")));
            AddAssert("only one metronome across tabs", () => Editor.ChildrenOfType<EditorMetronome>().Count() == 1);
            AddStep("disable metronome", () => click("Metronome: On"));
            AddStep("resume playback", () => EditorClock.Start());
            AddUntilStep("pass next beat", () => EditorClock.CurrentTime > 1400);
            AddAssert("disabled metronome silent", () => ticks.Count == count);
            AddStep("stop playback", () => EditorClock.Stop());
        }

        [Test]
        public void CaretSnapSharesToolbarAndStateAcrossTabs()
        {
            AddUntilStep("compose toolbar loaded", () => Editor.ChildrenOfType<LyricComposeScreen>().Single().ChildrenOfType<TimingGridControls>().Any(c => c.IsLoaded));
            AddAssert("caret snap next to grid snap with matching UI", () =>
            {
                var toolbar = Editor.ChildrenOfType<LyricComposeScreen>().Single().ChildrenOfType<TimingGridControls>().Single();
                var grid = toolbar.ChildrenOfType<RoundedButton>().Single(b => b.Text.ToString() == "Snap to Grid: Off");
                var caret = toolbar.ChildrenOfType<RoundedButton>().Single(b => b.Text.ToString() == "Snap to Caret: On");
                return caret.GetType() == grid.GetType() && caret.Size == grid.Size && Math.Abs(caret.X - grid.X - grid.Width - 8) < 0.01;
            });
            AddAssert("old panel toggle removed", () => !Editor.ChildrenOfType<ActiveLineDetailPanel>().Single().ChildrenOfType<RoundedButton>().Any(b => b.Text.ToString().Contains("caret")));
            AddStep("disable caret snap", () => click("Snap to Caret: On"));
            AddAssert("lyric magnet disabled", () => !Editor.ChildrenOfType<LyricComposeScreen>().Single().EditState.SnapToCaret.Value);
            AddStep("open timing", () => Editor.Mode.Value = EditorScreenMode.Timing);
            AddUntilStep("timing toolbar shows caret off", () => Editor.ChildrenOfType<TimingScreen>().Any(s => s.ChildrenOfType<RoundedButton>().Any(b => b.Text.ToString() == "Snap to Caret: Off")));
            AddStep("enable caret snap in timing", () => click("Snap to Caret: Off"));
            AddAssert("lyric magnet enabled", () => Editor.ChildrenOfType<LyricComposeScreen>().Single().EditState.SnapToCaret.Value);
            AddStep("back to compose", () => Editor.Mode.Value = EditorScreenMode.Compose);
            AddAssert("compose toggle follows", () => Editor.ChildrenOfType<LyricComposeScreen>().Single().ChildrenOfType<RoundedButton>().Any(b => b.Text.ToString() == "Snap to Caret: On"));
            AddAssert("grid snap remains independent", () => Editor.ChildrenOfType<LyricComposeScreen>().Single().ChildrenOfType<RoundedButton>().Any(b => b.Text.ToString() == "Snap to Grid: Off"));
        }

        private TimingGrid lyricGrid() => Editor.ChildrenOfType<LyricTimeline>().Single().ChildrenOfType<TimingGrid>().Single();

        private OsuDropdown<int> subdivision(EditorScreenMode mode) => Editor.ChildrenOfType<EditorScreenWithTimeline>().Single(s => s.Type == mode)
            .ChildrenOfType<TimingGridControls>().Single().ChildrenOfType<OsuDropdown<int>>().Single();

        private double wordEnd() => ((TypeBeatHitObject)EditorBeatmap.HitObjects[0]).Line.Units[0].EndTime;
        private void dragWordEnd(double from, double to) => dragWordEnd(() => from, to);
        private void dragWordEnd(Func<double> from, double to)
        {
            Vector2 at(double time)
            {
                var strip = Editor.ChildrenOfType<LyricTimeline>().Single();
                return strip.ToScreenSpace(new Vector2(strip.PositionOf(time), strip.DrawHeight / 2));
            }
            AddStep("hover edge", () => InputManager.MoveMouseTo(at(from() - 1)));
            AddStep("press edge", () => InputManager.PressButton(MouseButton.Left));
            AddStep("start drag", () => InputManager.MoveMouseTo(at(from() - 150)));
            AddStep("drag edge", () => InputManager.MoveMouseTo(at(to)));
            AddStep("release edge", () => InputManager.ReleaseButton(MouseButton.Left));
        }

        private FormTextBox field(string caption) => Editor.ChildrenOfType<TimingScreen>().Single().ChildrenOfType<FormTextBox>().Single(b => b.Caption.ToString() == caption);

        private void click(string text)
        {
            var button = Editor.ChildrenOfType<RoundedButton>().First(b => b.Text.ToString() == text && b.IsPresent);
            InputManager.MoveMouseTo(button);
            InputManager.Click(MouseButton.Left);
        }
    }
}
