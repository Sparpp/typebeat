// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osuTK;
using osuTK.Graphics;
using typebeat.Game.Overlays;

namespace typebeat.Game.Graphics.UserInterfaceV2
{
    /// <summary>
    /// <see cref="SwitchButton"/>'s pill with three rest positions instead of two: a nub that sits left,
    /// centre or right for the first, second and third member of <typeparamref name="T"/> (in declaration
    /// order), which must have exactly three members. Clicking a third of the pill selects that position;
    /// <see cref="Cycle"/> steps forward and wraps (what a click on the rest of a form row does).
    ///
    /// <para>Same colours, border and timings as <see cref="SwitchButton"/>: hollow with a light border
    /// at the first position, filled with the highlight colour at the other two, highlighted on hover and
    /// darkened while disabled. The first position plays the switch's "off" sample, the others its "on".</para>
    /// </summary>
    public partial class TriStateSwitch<T> : CompositeDrawable, IHasCurrentValue<T>
        where T : struct, Enum
    {
        public const float WIDTH = 56;

        private const float nub_inset = 4;
        private const float nub_size = 8;

        /// <summary>The three positions, left to right.</summary>
        public static readonly T[] VALUES = Enum.GetValues<T>();

        private readonly BindableWithCurrent<T> current = new BindableWithCurrent<T>();

        public Bindable<T> Current
        {
            get => current.Current;
            set => current.Current = value;
        }

        private readonly Box fill;
        private readonly Container content;
        private readonly Circle nub;

        [Resolved]
        private OverlayColourProvider colourProvider { get; set; } = null!;

        private Sample? sampleOn;
        private Sample? sampleOff;

        public TriStateSwitch()
        {
            if (VALUES.Length != 3)
                throw new InvalidOperationException($"{typeof(T).Name} has {VALUES.Length} members; a three-position switch needs exactly three.");

            Size = new Vector2(WIDTH, 16);

            InternalChild = content = new CircularContainer
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                RelativeSizeAxes = Axes.Both,
                BorderColour = Color4.White,
                BorderThickness = 3.2f,
                Masking = true,
                CornerExponent = 2.5f,
                Children = new Drawable[]
                {
                    fill = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        AlwaysPresent = true,
                    },
                    // The nub's left edge travels this container's width, so at the third position
                    // its right edge sits the same inset from the pill's end as its left edge does
                    // from the start at the first.
                    new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding { Left = nub_inset, Right = nub_inset + nub_size },
                        Child = nub = new Circle
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            RelativePositionAxes = Axes.X,
                            Size = new Vector2(nub_size),
                        },
                    },
                }
            };
        }

        /// <summary>Which of the three positions <paramref name="value"/> rests at (0 left, 1 centre, 2 right).</summary>
        public static int PositionOf(T value) => Math.Max(0, Array.IndexOf(VALUES, value));

        /// <summary>The position a click at <paramref name="x"/> (local, across a pill <paramref name="width"/> wide) selects. Pure.</summary>
        public static int PositionAt(float x, float width) => width <= 0 ? 0 : Math.Clamp((int)(x / width * 3), 0, 2);

        [BackgroundDependencyLoader(true)]
        private void load(AudioManager? audio)
        {
            sampleOn = audio?.Samples.Get(@"UI/check-on");
            sampleOff = audio?.Samples.Get(@"UI/check-off");
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Current.BindDisabledChanged(_ => updateState());
            Current.BindValueChanged(_ => updateState(), true);

            FinishTransforms(true);
        }

        /// <summary>Steps to the next position, wrapping from the last back to the first, as a user change.</summary>
        public void Cycle() => select((PositionOf(Current.Value) + 1) % VALUES.Length);

        protected override bool OnClick(ClickEvent e)
        {
            select(PositionAt(e.MousePosition.X, DrawWidth));
            return true;
        }

        protected override bool OnHover(HoverEvent e)
        {
            updateState();
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            updateState();
            base.OnHoverLost(e);
        }

        private void select(int position)
        {
            if (Current.Disabled)
                return;

            var value = VALUES[position];

            if (EqualityComparer<T>.Default.Equals(value, Current.Value))
                return;

            Current.Value = value;
            PlaySample(position);
        }

        /// <summary>Plays the sound of arriving at <paramref name="position"/>: "off" for the first, "on" for the others.</summary>
        public void PlaySample(int position)
        {
            if (position > 0)
                sampleOn?.Play();
            else
                sampleOff?.Play();
        }

        private void updateState()
        {
            int position = PositionOf(Current.Value);
            bool on = position > 0;

            Color4 fillColour = colourProvider.Background5.Opacity(0);
            Color4 borderColour = colourProvider.Light4;

            if (IsHovered)
                borderColour = colourProvider.Highlight1;
            else if (on)
                borderColour = colourProvider.Highlight1.Darken(0.1f);

            if (on)
                fillColour = borderColour;

            // Light on the hollow pill, dark on the filled one, so the nub always reads against its track.
            Color4 nubColour = on ? colourProvider.Background5 : borderColour;

            if (Current.Disabled)
            {
                fillColour = fillColour.Darken(0.4f);
                borderColour = borderColour.Darken(0.4f);
                nubColour = nubColour.Darken(0.4f);
            }

            fill.FadeColour(fillColour, 250, Easing.OutQuint);
            nub.FadeColour(nubColour, 250, Easing.OutQuint);

            content.TransformTo(nameof(BorderColour), (ColourInfo)borderColour, 250, Easing.OutQuint);

            nub.MoveToX(position / 2f, 200, Easing.OutElasticQuarter);
        }
    }
}
