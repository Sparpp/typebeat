// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// The track beside a held letter: it fills white from the left while held, freezes on release,
    /// flashes the tail's judgement and then blurs away.
    /// </summary>
    public partial class HoldBar : CompositeDrawable
    {
        // The ticks the fill has passed while held; they wear the fill's colour from then on.
        private readonly HashSet<int> litTicks = new HashSet<int>();

        /// <summary>How long a finished track takes to blur and fade out.</summary>
        public const double HOLD_BLUR_DURATION_MS = 200;

        /// <summary>The blur's sigma at the default font size.</summary>
        public const float HOLD_BLUR_SIGMA = 4;

        /// <summary>The held part of the track.</summary>
        public static readonly Color4 HoldFill = Color4.White;

        /// <summary>The track's colour once a never-opened hold has settled.</summary>
        public static readonly Color4 MISSED_TRACK = TypeBeatStyle.ErrorChar.Opacity(0.45f);

        // Ticks sit beside the masked track (they are taller than it), inside the blur container.
        private readonly Container<Box> ticks;

        private Vector2 tickSize = new Vector2(LyricLineDisplay.HOLD_TICK_WIDTH, LyricLineDisplay.HOLD_TICK_HEIGHT);

        private readonly Box track;
        private readonly Box fill;

        // Carries the blur-away, apart from this bar's own alpha, which Flashlight and Recite drive.
        private readonly BufferedContainer inner;

        public HoldBar()
        {
            InternalChild = inner = new BufferedContainer()
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Children = new Drawable[]
                {
                    new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Masking = true,
                        CornerRadius = 1,
                        Children = new Drawable[]
                        {
                            track = new Box { RelativeSizeAxes = Axes.Both, Colour = TypeBeatStyle.HoldTrack },
                            fill = new Box
                            {
                                RelativeSizeAxes = Axes.Both,
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Width = 0,
                                Colour = HoldFill,
                            },
                        },
                    },
                    ticks = new Container<Box> { RelativeSizeAxes = Axes.Both },
                },
            };
        }

        /// <summary>The sigma a finished track blurs to; scaled by the display's size ratio.</summary>
        public float BlurSigma { get; set; } = HOLD_BLUR_SIGMA;

        protected override void Update()
        {
            base.Update();

            // Pad the buffer past the thin bar so neither the blur nor a tick is clipped.
            float padY = System.Math.Max(0, (tickSize.Y - DrawHeight) / 2) + BlurSigma * 3;
            float padX = tickSize.X / 2 + BlurSigma * 3;
            inner.Padding = new MarginPadding { Top = padY, Bottom = padY, Left = padX, Right = padX };
            inner.Size = DrawSize + new Vector2(padX * 2, padY * 2);
        }

        private void blurAway()
        {
            inner.BlurTo(new Vector2(BlurSigma), HOLD_BLUR_DURATION_MS, Easing.OutQuad);

            // A faded container stops updating its children, so settle their colours once it is gone.
            inner.FadeOut(HOLD_BLUR_DURATION_MS, Easing.OutQuad).OnComplete(_ =>
            {
                track.FinishTransforms();
                fill.FinishTransforms();

                foreach (var tick in ticks)
                    tick.FinishTransforms();
            });
        }

        /// <summary>The held fraction, 0..1.</summary>
        public float Fill
        {
            get => fill.Width;
            set => fill.Width = System.Math.Clamp(value, 0f, 1f);
        }

        public Vector2 TickSize
        {
            get => tickSize;
            set
            {
                tickSize = value;

                foreach (var tick in ticks)
                    tick.Size = value;
            }
        }

        /// <summary>One waiting tick per entry of <paramref name="fractions"/>, each its share of the hold span.</summary>
        public void SetTicks(IReadOnlyList<float> fractions)
        {
            ticks.Clear();
            litTicks.Clear();

            foreach (float fraction in fractions)
            {
                ticks.Add(new Box
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.Centre,
                    RelativePositionAxes = Axes.X,
                    X = fraction,
                    Size = tickSize,
                    Colour = TypeBeatStyle.UntypedChar,
                });
            }
        }

        /// <summary>Tick <paramref name="index"/> was hit: it takes the fill's colour, with no flash or pop.</summary>
        public void LightTick(int index)
        {
            if (index < 0 || index >= ticks.Children.Count)
                return;

            var tick = ticks.Children[index];
            tick.ClearTransforms();
            tick.Colour = fill.Colour;
            litTicks.Add(index);
        }

        /// <summary>Tick <paramref name="index"/> was missed.</summary>
        public void MissTick(int index)
        {
            if (index < 0 || index >= ticks.Children.Count)
                return;

            var tick = ticks.Children[index];
            tick.ClearTransforms();
            tick.Colour = MISSED_TRACK;
            litTicks.Remove(index);
        }

        /// <summary>
        /// The held part (the fill and the ticks it passed) settles on <paramref name="colour"/>; the fill keeps
        /// the width it froze at. The unheld rest of the track does not flash: in the fill's own colour that
        /// read as the fill snapping to full and pulling back.
        /// </summary>
        public void ShowResult(Color4 colour)
        {
            fill.FadeColour(colour, 300, Easing.OutQuint);

            foreach (int index in litTicks)
                ticks.Children[index].FadeColour(colour, 300, Easing.OutQuint);

            blurAway();
        }

        /// <summary>The hold never opened: the bar stays empty on a dimmed miss-coloured track.</summary>
        public void ShowMissed()
        {
            fill.ClearTransforms();
            fill.Width = 0;

            inner.ClearTransforms();
            track.ClearTransforms();
            track.Colour = TypeBeatStyle.ErrorChar;
            track.FadeColour(MISSED_TRACK, 300, Easing.OutQuint);
            blurAway();
        }

        /// <summary>Back to the empty, unjudged look (after a rewind).</summary>
        public void Reset()
        {
            track.ClearTransforms();
            fill.ClearTransforms();
            track.Colour = TypeBeatStyle.HoldTrack;
            fill.Colour = HoldFill;
            fill.Width = 0;
            inner.ClearTransforms();
            inner.BlurSigma = Vector2.Zero;
            inner.Alpha = 1;

            litTicks.Clear();

            foreach (var tick in ticks)
            {
                tick.ClearTransforms();
                tick.Colour = TypeBeatStyle.UntypedChar;
            }
        }
    }
}
