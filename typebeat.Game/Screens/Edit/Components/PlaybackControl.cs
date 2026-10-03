// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using osuTK;
using osuTK.Graphics;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays;
using osuTK.Input;

namespace typebeat.Game.Screens.Edit.Components
{
    /// <summary>
    /// The editor bottom bar's playback-speed section: a rounded slider over the editor clock's
    /// pitch-preserving tempo, with a compact typeable whole-percent box beside it.
    ///
    /// Space (and the existing transport shortcuts) is the only way to start/stop playback now; the
    /// old play/pause <see cref="IconButton"/> is gone, so the bottom bar shows no running state at all.
    /// </summary>
    public partial class PlaybackControl : BottomBarContainer
    {
        /// <summary>
        /// The lowest playback speed the audio backend accepts for a tempo (time-stretch) adjustment.
        ///
        /// The BASS FX tempo path (osu.Framework's <c>TrackBass</c>) throws for any aggregate tempo
        /// strictly below <c>0.05f</c> (5 percent). Because the framework compares against the widened
        /// float <c>0.05000000074505806</c> and 5 percent stored as a <see cref="double"/> is
        /// <c>0.05</c> exactly, 5 percent itself is still below that threshold and would throw. At
        /// whole-percent precision the smallest safe value is therefore 6 percent (<c>0.06</c>), which
        /// is what the slider's minimum is clamped to.
        /// </summary>
        public const double MIN_TEMPO = 0.06;

        public const double MAX_TEMPO = 2.0;

        /// <summary>Whole-percent precision: the value is stored as a fraction of one, so 0.01 = 1 percent.</summary>
        public const double TEMPO_PRECISION = 0.01;

        public const double DEFAULT_TEMPO = 1.0;

        private const float speed_box_width = 52;

        /// <summary>The text box's height; the slider nub is scaled to it (see <see cref="SpeedSlider"/>).</summary>
        private const float speed_box_height = 22;

        /// <summary>
        /// The nub is shrunk by this factor from its stock size (backlog 392 follow-up, owner: the drag
        /// button should be 40 percent smaller), so it no longer dwarfs the bar it rides.
        /// </summary>
        private const float nub_scale = 0.6f;

        /// <summary>
        /// The gap between the slider and the text box to its right (backlog 392 follow-up).
        /// </summary>
        private const float slider_box_gap = 8;

        private PlaybackSpeedControl playbackSpeedControl = null!;
        private SpeedSlider speedSlider = null!;
        private SpeedTextBox speedBox = null!;

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        private readonly BindableNumber<double> tempoAdjustment = new BindableDouble(DEFAULT_TEMPO)
        {
            MinValue = MIN_TEMPO,
            MaxValue = MAX_TEMPO,
            Precision = TEMPO_PRECISION,
            Default = DEFAULT_TEMPO,
        };

        /// <summary>The text currently shown in the speed box, kept in sync with <see cref="Tempo"/>.</summary>
        private readonly Bindable<string> speedText = new Bindable<string>();

        /// <summary>The single bindable the slider and the text box both read and write.</summary>
        public BindableNumber<double> Tempo => tempoAdjustment;

        public SpeedSlider Slider => speedSlider;

        public SpeedTextBox SpeedField => speedBox;

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider)
        {
            Background.Colour = colourProvider.Background4;

            Children = new Drawable[]
            {
                playbackSpeedControl = new PlaybackSpeedControl
                {
                    AutoSizeAxes = Axes.Y,
                    RelativeSizeAxes = Axes.X,
                    Padding = new MarginPadding { Left = 15, Right = 15 },
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    Direction = FillDirection.Vertical,
                    Children = new Drawable[]
                    {
                        new OsuSpriteText
                        {
                            Text = EditorStrings.PlaybackSpeed,
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Children = new Drawable[]
                            {
                                new Container
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Anchor = Anchor.CentreLeft,
                                    Origin = Anchor.CentreLeft,
                                    Padding = new MarginPadding { Right = speed_box_width + slider_box_gap },
                                    Child = speedSlider = new SpeedSlider(tempoAdjustment)
                                    {
                                        RelativeSizeAxes = Axes.X,
                                        // Centre the bar on the box's middle: the row is as tall as the
                                        // box, and the slider's own height is the (now smaller) nub, so
                                        // a centred slice of the row lands the line at the box's level
                                        // instead of riding high on its top edge.
                                        Anchor = Anchor.Centre,
                                        Origin = Anchor.Centre,
                                    },
                                },
                                speedBox = new SpeedTextBox
                                {
                                    Width = speed_box_width,
                                    // Wait for the box's own height (it sets it in its ctor) before the
                                    // row sizes to it, so the slider's centring uses the real height.
                                    // The nub is scaled to that height in SpeedSlider.Update.
                                    Height = speed_box_height,
                                    Anchor = Anchor.CentreRight,
                                    Origin = Anchor.CentreRight,
                                },
                            },
                        },
                    },
                },
            };

            speedBox.Current = speedText;
            speedBox.Revert = () => speedText.Value = formatPercent(tempoAdjustment.Value);
            speedBox.OnCommit += (_, _) => commitSpeed();

            editorClock.AudioAdjustments.AddAdjustment(AdjustableProperty.Tempo, tempoAdjustment);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            tempoAdjustment.BindValueChanged(_ => speedText.Value = formatPercent(tempoAdjustment.Value), true);
        }

        protected override void Dispose(bool isDisposing)
        {
            if (editorClock.IsNotNull())
                editorClock.AudioAdjustments.RemoveAdjustment(AdjustableProperty.Tempo, tempoAdjustment);

            base.Dispose(isDisposing);
        }

        protected override bool OnKeyDown(KeyDownEvent e)
        {
            if (e.Repeat)
                return false;

            switch (e.Key)
            {
                case Key.Space:
                    togglePause();
                    return true;
            }

            return base.OnKeyDown(e);
        }

        private void togglePause()
        {
            if (editorClock.IsRunning)
                editorClock.Stop();
            else
                editorClock.Start();
        }

        /// <summary>
        /// Applies the box's typed value: a whole number, optionally suffixed with a percent sign,
        /// clamped to the slider's range and rounded to a whole percent. Anything unparseable reverts
        /// to the current value with the standard input-error flash.
        /// </summary>
        private void commitSpeed()
        {
            if (tryParsePercent(speedBox.Text, out double percent))
            {
                percent = Math.Round(Math.Clamp(percent, MIN_TEMPO * 100, MAX_TEMPO * 100));
                tempoAdjustment.Value = percent / 100.0;
            }
            else
            {
                speedBox.FlashInputError();
            }

            speedText.Value = formatPercent(tempoAdjustment.Value);
        }

        private static bool tryParsePercent(string text, out double percent)
        {
            percent = 0;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            string trimmed = text.Trim();

            if (trimmed.EndsWith('%'))
                trimmed = trimmed[..^1].Trim();

            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return false;

            if (!double.IsFinite(value))
                return false;

            percent = value;
            return true;
        }

        private static string formatPercent(double value) => $"{Math.Round(value * 100):0}%";

        private partial class PlaybackSpeedControl : FillFlowContainer, IHasTooltip
        {
            public LocalisableString TooltipText { get; set; }
        }

        /// <summary>
        /// The rounded slider over the editor clock's tempo, matching the settings sliders' look:
        /// double-clicking the nub or right-clicking anywhere on it resets to the default 100 percent,
        /// and a faint tick marks where 100 percent sits on the track.
        /// </summary>
        public partial class SpeedSlider : RoundedSliderBar<double>
        {
            private readonly BindableNumber<double> tempo;
            private readonly Box defaultTick;

            /// <summary>The drag button, exposed for scene tests asserting its size.</summary>
            public Drawable DragNub => Nub;

            /// <summary>The filled (left) portion of the bar, exposed for scene tests asserting it reaches the nub.</summary>
            public Drawable FillBar => LeftBox;

            public SpeedSlider(BindableNumber<double> tempo)
            {
                this.tempo = tempo;

                Current = tempo;

                // A 40 percent smaller drag button (backlog 392 follow-up), by SCALE so its border
                // shrinks with it. Scaling is why the fill needs correcting below: RoundedSliderBar's
                // fill and track math reads Nub.DrawWidth, which deliberately EXCLUDES a drawable's own
                // Scale, so an unscaled-width fill stopped short of the smaller handle and the track
                // started late, leaving an exposed strip around it. Height and RangePadding follow the
                // scaled size so the ends still meet the nub.
                Nub.Scale = new Vector2(nub_scale);
                Height = Nub.HEIGHT * nub_scale;
                RangePadding = Nub.DEFAULT_EXPANDED_SIZE * nub_scale / 2;

                Add(defaultTick = new Box
                {
                    Width = 1.5f,
                    // Scaled with the bar so the 100 percent mark stays a tick crossed over the line
                    // rather than a full-height stroke on a now-shorter control.
                    Height = 6,
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.Centre,
                    Colour = Color4.White.Opacity(0.3f),
                });
            }

            protected override bool OnMouseDown(MouseDownEvent e)
            {
                if (e.Button == MouseButton.Right && !IsDragged && !Current.Disabled)
                {
                    ResetToDefault.Invoke();
                    return true;
                }

                return base.OnMouseDown(e);
            }

            protected override void UpdateAfterChildren()
            {
                base.UpdateAfterChildren();

                // RoundedSliderBar fills up to Nub.DrawWidth, which EXCLUDES the nub's own Scale; with
                // the nub scaled to 0.6 the filled bar stopped short of the handle and the darker track
                // started late, leaving a pale exposed strip around it. Recompute both against the
                // nub's true ON-SCREEN half-width (DrawWidth * Scale.X) so the fill meets the handle
                // and the track abuts it, exactly as it does unshrunk. Same formula the base uses, only
                // the half-width term is scaled.
                float half = Nub.DrawWidth * Nub.Scale.X / 2;
                float nubX = Nub.DrawPosition.X;

                LeftBox.Scale = new Vector2(Math.Clamp(RangePadding + nubX - half, 0, Math.Max(0, DrawWidth)), 1);
                RightBox.Scale = new Vector2(Math.Clamp(DrawWidth - nubX - RangePadding - half, 0, Math.Max(0, DrawWidth)), 1);

                double range = tempo.MaxValue - tempo.MinValue;
                double normalized = range > 0 ? (tempo.Default - tempo.MinValue) / range : 0;

                defaultTick.X = RangePadding + (float)(normalized * (DrawWidth - 2 * RangePadding));
            }

            protected override LocalisableString GetTooltipText(double value) => $"{value * 100:0}%";
        }

        /// <summary>
        /// The compact whole-percent entry box. It only takes focus on a click, and releases it on
        /// commit (Enter or blur) or Escape, so it never steals typing focus from the editor.
        /// </summary>
        public partial class SpeedTextBox : OsuTextBox
        {
            /// <summary>Invoked on Escape to discard the in-progress edit and restore the live value.</summary>
            public Action? Revert { get; set; }

            public SpeedTextBox()
            {
                Height = 22;
                TextContainer.Height = 0.7f;
                LengthLimit = 6;
                CommitOnFocusLost = true;
                ReleaseFocusOnCommit = true;
            }

            /// <summary>Triggers the standard input-error feedback (the red flash and the invalid sample).</summary>
            public void FlashInputError() => NotifyInputError();

            protected override bool OnKeyDown(KeyDownEvent e)
            {
                if (e.Key == Key.Escape && !e.Repeat)
                {
                    Revert?.Invoke();
                    KillFocus();
                    return true;
                }

                return base.OnKeyDown(e);
            }
        }
    }
}
