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
using typebeat.Game.Screens.Edit.Components.Menus;

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
            AddStep("enable overlay", () => Editor.TimingToggle(TimingMenuItems.OVERLAY_GRID).Press());
            AddUntilStep("overlay on", () => Editor.TimingToggle(TimingMenuItems.OVERLAY_GRID).State.Value);
            AddUntilStep("grid draws over lyrics", () => lyricGrid().IsPresent && lyricGrid().ChildrenOfType<Box>().Any(b => b.Alpha > 0));
            AddStep("hide overlay", () => Editor.TimingToggle(TimingMenuItems.OVERLAY_GRID).Press());
            AddUntilStep("lyric grid hidden", () => !lyricGrid().IsPresent);
            AddStep("show overlay again", () => Editor.TimingToggle(TimingMenuItems.OVERLAY_GRID).Press());
            AddUntilStep("grid reappears", () => lyricGrid().IsPresent && lyricGrid().ChildrenOfType<Box>().Any(b => b.Alpha > 0));
            AddAssert("snap still off", () => !Editor.TimingToggle(TimingMenuItems.SNAP_TO_GRID).State.Value);
            AddAssert("reference takes no input", () => Editor.ChildrenOfType<TimingGrid>().All(g => !g.HandlePositionalInput));
            AddStep("enable snapping", () => Editor.TimingToggle(TimingMenuItems.SNAP_TO_GRID).Press());
            AddUntilStep("snapping on", () => Editor.TimingToggle(TimingMenuItems.SNAP_TO_GRID).State.Value);
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
            AddStep("enable snapping", () => Editor.TimingToggle(TimingMenuItems.SNAP_TO_GRID).Press());
            dragWordEnd(1900, 1840);
            AddUntilStep("end on grid", () => Math.Abs(wordEnd() - EditorBeatmap.ControlPointInfo.GetClosestSnappedTime(1840, Editor.BeatDivisor)) < 0.001);
            AddStep("disable snapping", () => Editor.TimingToggle(TimingMenuItems.SNAP_TO_GRID).Press());
            dragWordEnd(() => wordEnd(), 1810);
            AddUntilStep("end follows cursor freely", () => Math.Abs(wordEnd() - 1810) < 0.01);
        }

        [Test]
        public void NoteLengthsStaySynchronizedAcrossTabs()
        {
            AddUntilStep("compose loaded", () => Editor.ChildrenOfType<LyricTimeline>().Any(s => s.IsLoaded));
            AddStep("choose half notes", () => Editor.SelectSubdivision(-2));
            AddAssert("half note selected", () => Editor.TimingMenu.CurrentSubdivision == -2);
            AddStep("open timing", () => Editor.Mode.Value = EditorScreenMode.Timing);
            AddUntilStep("timing loaded", () => Editor.ChildrenOfType<TimingScreen>().Any(s => s.IsLoaded));
            AddAssert("still half notes on the timing tab", () => Editor.TimingMenu.CurrentSubdivision == -2);
            AddStep("choose eighth notes", () => Editor.SelectSubdivision(2));
            AddAssert("beat divisor follows", () => Editor.BeatDivisor == 2);
            AddStep("back to compose", () => Editor.Mode.Value = EditorScreenMode.Compose);
            AddAssert("eighth note selected", () => Editor.TimingMenu.CurrentSubdivision == 2);
        }

        /// <summary>
        /// The grid controls are entries of the top bar's Timing menu (they used to be a toolbar row above
        /// the timeline): BPM / Offset, the subdivision, then the toggles, ahead of the menu's own entries.
        /// </summary>
        [Test]
        public void TimingMenuHoldsTheGridControls()
        {
            AddUntilStep("compose loaded", () => Editor.ChildrenOfType<LyricTimeline>().Any(s => s.IsLoaded));
            AddAssert("no toolbar row above the timeline", () => !Editor.ChildrenOfType<RoundedButton>().Any(b => b.Text.ToString().StartsWith("Snap to Grid", StringComparison.Ordinal)));
            AddAssert("timing menu lists the grid controls first", () =>
            {
                var timing = Editor.ChildrenOfType<EditorMenuBar>().Single().Items.Single(i => i.Text.Value.ToString() == "Timing");
                var labels = timing.Items.Take(6).Select(i => i.Text.Value.ToString()).ToArray();
                return labels[0] == TimingMenuItems.BPM_OFFSET
                       && labels[1].StartsWith(TimingMenuItems.GRID_SUBDIVISION, StringComparison.Ordinal)
                       && labels.Skip(2).SequenceEqual(new[]
                       {
                           TimingMenuItems.OVERLAY_GRID, TimingMenuItems.SNAP_TO_GRID, TimingMenuItems.SNAP_TO_CARET,
                           TimingMenuItems.METRONOME,
                       })
                       && timing.Items.All(i => i.Text.Value.ToString() != TimingMenuItems.VOCALS_WAVEFORM);
            });
            AddAssert("vocals waveform is under View", () =>
            {
                var view = Editor.ChildrenOfType<EditorMenuBar>().Single().Items.Single(i => i.Text.Value.ToString() == "View");
                return view.Items.Any(i => i == Editor.TimingMenu.VocalsWaveform);
            });
            AddStep("choose half notes", () => Editor.SelectSubdivision(-2));
            AddAssert("subdivision entry names the current note", () =>
                Editor.TimingMenu.Items[1].Text.Value.ToString() == $"{TimingMenuItems.GRID_SUBDIVISION}: 1/2 note");
            AddStep("open BPM / Offset", () => Editor.TimingMenu.Items[0].Press());
            AddUntilStep("timing screen opened", () => Editor.Mode.Value == EditorScreenMode.Timing);
        }

        [Test]
        public void MetronomeFollowsPlaybackAndPauses()
        {
            var ticks = new List<(int beat, double time)>();
            int count = 0;
            AddUntilStep("compose loaded", () => Editor.ChildrenOfType<LyricTimeline>().Any(s => s.IsLoaded));
            AddStep("listen for ticks", () => Editor.ChildrenOfType<EditorMetronome>().Single().Ticked += beat => ticks.Add((beat, EditorClock.CurrentTime)));
            AddStep("whole-note grid", () => Editor.SelectSubdivision(-4));
            AddStep("enable metronome", () => Editor.TimingToggle(TimingMenuItems.METRONOME).Press());
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
            AddUntilStep("timing tab loaded", () => Editor.ChildrenOfType<TimingScreen>().Any(s => s.IsLoaded));
            AddAssert("metronome stays enabled on the timing tab", () => Editor.TimingToggle(TimingMenuItems.METRONOME).State.Value);
            AddAssert("only one metronome across tabs", () => Editor.ChildrenOfType<EditorMetronome>().Count() == 1);
            AddStep("disable metronome", () => Editor.TimingToggle(TimingMenuItems.METRONOME).Press());
            AddStep("resume playback", () => EditorClock.Start());
            AddUntilStep("pass next beat", () => EditorClock.CurrentTime > 1400);
            AddAssert("disabled metronome silent", () => ticks.Count == count);
            AddStep("stop playback", () => EditorClock.Stop());
        }

        [Test]
        public void CaretSnapSharesTheTimingMenuAndStateAcrossTabs()
        {
            AddUntilStep("compose loaded", () => Editor.ChildrenOfType<LyricTimeline>().Any(s => s.IsLoaded));
            AddAssert("caret snap sits right after grid snap", () =>
            {
                var items = Editor.TimingMenu.Items.ToList();
                return items.IndexOf(Editor.TimingToggle(TimingMenuItems.SNAP_TO_CARET)) == items.IndexOf(Editor.TimingToggle(TimingMenuItems.SNAP_TO_GRID)) + 1
                       && !Editor.TimingToggle(TimingMenuItems.SNAP_TO_GRID).State.Value && Editor.TimingToggle(TimingMenuItems.SNAP_TO_CARET).State.Value;
            });
            AddAssert("old panel toggle removed", () => !Editor.ChildrenOfType<ActiveLineDetailPanel>().Single().ChildrenOfType<RoundedButton>().Any(b => b.Text.ToString().Contains("caret")));
            AddStep("disable caret snap", () => Editor.TimingToggle(TimingMenuItems.SNAP_TO_CARET).Press());
            AddAssert("lyric magnet disabled", () => !Editor.ChildrenOfType<LyricComposeScreen>().Single().EditState.SnapToCaret.Value);
            AddStep("open timing", () => Editor.Mode.Value = EditorScreenMode.Timing);
            AddUntilStep("timing tab loaded", () => Editor.ChildrenOfType<TimingScreen>().Any(s => s.IsLoaded));
            AddAssert("menu shows caret off", () => !Editor.TimingToggle(TimingMenuItems.SNAP_TO_CARET).State.Value);
            AddStep("enable caret snap in timing", () => Editor.TimingToggle(TimingMenuItems.SNAP_TO_CARET).Press());
            AddAssert("lyric magnet enabled", () => Editor.ChildrenOfType<LyricComposeScreen>().Single().EditState.SnapToCaret.Value);
            AddStep("back to compose", () => Editor.Mode.Value = EditorScreenMode.Compose);
            AddAssert("compose magnet follows", () => Editor.ChildrenOfType<LyricComposeScreen>().Single().EditState.SnapToCaret.Value);
            AddAssert("grid snap remains independent", () => !Editor.TimingToggle(TimingMenuItems.SNAP_TO_GRID).State.Value);
        }

        private TimingGrid lyricGrid() => Editor.ChildrenOfType<LyricTimeline>().Single().ChildrenOfType<TimingGrid>().Single();

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
