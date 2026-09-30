// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Screens.Edit.Compose.Components.Timeline;
using osuTK;

namespace typebeat.Game.Screens.Edit.Timing
{
    /// <summary>Editor BPM/offset authoring based on lazer timing control points.</summary>
    public partial class TimingScreen : EditorScreenWithTimeline
    {
        [Resolved]
        private EditorClock clock { get; set; } = null!;

        private FormDropdown<TimingControlPoint?> points = null!;
        private FormTextBox bpm = null!;
        private FormTextBox offset = null!;
        private FormTextBox meter = null!;
        private OsuSpriteText status = null!;
        private readonly List<double> taps = new List<double>();
        private TimingControlPoint[] lastPoints = Array.Empty<TimingControlPoint>();
        private bool refreshing;

        public TimingScreen() : base(EditorScreenMode.Timing)
        {
        }

        protected override void ConfigureTimeline(TimelineArea timelineArea)
        {
            timelineArea.Timeline.AlwaysShowControlPoints = true;
            timelineArea.Timeline.WaveformOpacityOverride = 1;
        }

        protected override Drawable CreateMainContent()
        {
            points = new PointDropdown { Caption = "Timing section" };
            bpm = new FormTextBox { Caption = "BPM", HintText = "Beats per minute (1–10000). Decimal values are supported.", SelectAllOnFocus = true };
            offset = new FormTextBox { Caption = "Offset (ms)", HintText = "Song time of the first beat in this section. Decimal and negative offsets are supported.", SelectAllOnFocus = true };
            meter = new FormTextBox { Caption = "Beats per bar", SelectAllOnFocus = true };
            status = new OsuSpriteText { Font = OsuFont.Default.With(size: 16) };
            points.Current.BindValueChanged(v =>
            {
                if (!refreshing)
                    display(v.NewValue);
            });
            bpm.OnCommit += (_, changed) => { if (changed) apply(); };
            offset.OnCommit += (_, changed) => { if (changed) apply(); };
            meter.OnCommit += (_, changed) => { if (changed) apply(); };

            display(null);

            return new OsuScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Padding = new MarginPadding(20),
                    Spacing = new Vector2(0, 10),
                    Children = new Drawable[]
                    {
                        new OsuSpriteText { Text = "Set BPM and align the beat grid to the waveform", Font = OsuFont.Default.With(size: 24) },
                        points,
                        offset,
                        row(button("Use current time", () => { offset.Current.Value = clock.CurrentTimeAccurate.ToString("R", CultureInfo.InvariantCulture); apply(); }),
                            button("Offset −1 ms", () => adjust(offset, -1)), button("Offset +1 ms", () => adjust(offset, 1))),
                        bpm,
                        row(button("BPM −0.1", () => adjust(bpm, -0.1)), button("BPM +0.1", () => adjust(bpm, 0.1)),
                            button("Tap BPM", tap), button("Reset taps", () => taps.Clear())),
                        meter,
                        row(button("Apply", apply), button("Add at playhead", add), button("Delete section", delete)),
                        status,
                        new OsuSpriteText { Text = "BPM edits change the reference grid. Lyric timings stay where you placed them.", Font = OsuFont.Default.With(size: 16) },
                    },
                },
            };
        }

        protected override void Update()
        {
            base.Update();
            if (points == null)
                return;
            var current = EditorBeatmap.ControlPointInfo.TimingPoints.ToArray();
            if (!current.SequenceEqual(lastPoints, ReferenceEqualityComparer.Instance))
                refresh(points.Current.Value);
        }

        private void refresh(TimingControlPoint? preferred)
        {
            refreshing = true;
            lastPoints = EditorBeatmap.ControlPointInfo.TimingPoints.ToArray();
            points.Items = lastPoints;
            points.Current.Value = lastPoints.FirstOrDefault(p => ReferenceEquals(p, preferred))
                                   ?? lastPoints.FirstOrDefault(p => p.Time == preferred?.Time)
                                   ?? lastPoints.FirstOrDefault();
            refreshing = false;
            display(points.Current.Value);
        }

        private void display(TimingControlPoint? point)
        {
            bpm.Current.Value = (point?.BPM ?? 120).ToString("R", CultureInfo.InvariantCulture);
            offset.Current.Value = (point?.Time ?? clock.CurrentTimeAccurate).ToString("R", CultureInfo.InvariantCulture);
            meter.Current.Value = (point?.TimeSignature.Numerator ?? 4).ToString(CultureInfo.InvariantCulture);
            taps.Clear();
        }

        private static bool number(FormTextBox box, out double value)
            => double.TryParse(box.Current.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

        private void apply()
        {
            if (!number(bpm, out double tempo) || !number(offset, out double time)
                || !int.TryParse(meter.Current.Value, out int beats) || tempo < 1 || tempo > 10000 || beats < 1 || beats > 32)
            {
                status.Text = "Enter a valid BPM (1–10000), finite offset, and beats per bar (1–32).";
                return;
            }
            try
            {
                var point = TimingPointOperations.Set(EditorBeatmap, points.Current.Value, time, tempo, beats);
                refresh(point);
                status.Text = "Timing saved. Use the waveform to check alignment.";
            }
            catch (InvalidOperationException e)
            {
                status.Text = e.Message;
            }
        }

        private void adjust(FormTextBox field, double amount)
        {
            if (number(field, out double value))
            {
                field.Current.Value = (value + amount).ToString("R", CultureInfo.InvariantCulture);
                apply();
            }
        }

        private void add()
        {
            double time = clock.CurrentTimeAccurate;
            var active = EditorBeatmap.ControlPointInfo.TimingPointAt(time);
            try
            {
                refresh(TimingPointOperations.Set(EditorBeatmap, null, time, EditorBeatmap.ControlPointInfo.TimingPoints.Count == 0 ? 120 : active.BPM, active.TimeSignature.Numerator));
                status.Text = "Added a timing section at the playhead.";
            }
            catch (InvalidOperationException e)
            {
                status.Text = e.Message;
            }
        }

        private void delete()
        {
            if (points.Current.Value is TimingControlPoint point)
            {
                TimingPointOperations.Delete(EditorBeatmap, point);
                refresh(null);
            }
        }

        private void tap()
        {
            // Use wall-clock spacing so tapping works during playback and while paused.
            double now = System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (taps.Count > 0 && now - taps[^1] > 2000)
                taps.Clear();
            taps.Add(now);
            if (taps.Count > 16)
                taps.RemoveAt(0);
            if (taps.Count > 1)
                bpm.Current.Value = (60000 * (taps.Count - 1) / (now - taps[0])).ToString("0.######", CultureInfo.InvariantCulture);
            status.Text = "Tap along to the beat, then press Apply to keep the BPM.";
        }

        private static RoundedButton button(string text, Action action) => new RoundedButton { Text = text, Width = 180, Height = 32, Action = action };
        private static Drawable row(params Drawable[] children) => new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X, AutoSizeAxes = Axes.Y, Direction = FillDirection.Full,
            Spacing = new Vector2(8), Children = children,
        };

        private partial class PointDropdown : FormDropdown<TimingControlPoint?>
        {
            protected override osu.Framework.Localisation.LocalisableString GenerateItemText(TimingControlPoint? item)
                => item == null ? "No timing sections" : FormattableString.Invariant($"{item.Time:0.######} ms · {item.BPM:0.######} BPM · {item.TimeSignature}");
        }
    }
}
