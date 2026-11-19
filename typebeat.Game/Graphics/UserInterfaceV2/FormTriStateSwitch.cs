// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osuTK;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Overlays;

namespace typebeat.Game.Graphics.UserInterfaceV2
{
    /// <summary>
    /// <see cref="FormCheckBox"/>'s row for a three-way choice: caption and hint on the left, a
    /// <see cref="TriStateSwitch{T}"/> on the right, and the current member's description (its
    /// <see cref="System.ComponentModel.DescriptionAttribute"/>) just left of the switch.
    /// <typeparamref name="T"/> must have exactly three members.
    ///
    /// <para>A click on the switch selects the third it lands on; a click anywhere else on the row
    /// steps to the next option, wrapping, as a click on a checkbox row toggles it.</para>
    /// </summary>
    public partial class FormTriStateSwitch<T> : CompositeDrawable, IHasCurrentValue<T>, IFormControl
        where T : struct, Enum
    {
        public Bindable<T> Current
        {
            get => current.Current;
            set => current.Current = value;
        }

        private readonly BindableWithCurrent<T> current = new BindableWithCurrent<T>();

        /// <summary>
        /// Caption describing this control, displayed on the left.
        /// </summary>
        public LocalisableString Caption { get; init; }

        /// <summary>
        /// Hint text containing an extended description of this control, displayed in a tooltip when hovering the caption.
        /// </summary>
        public LocalisableString HintText { get; init; }

        private FormControlBackground background = null!;
        private FormFieldCaption caption = null!;
        private OsuSpriteText valueLabel = null!;

        private TriStateSwitch<T> switchButton = null!;

        [Resolved]
        private OverlayColourProvider colourProvider { get; set; } = null!;

        public FormTriStateSwitch()
        {
            if (TriStateSwitch<T>.VALUES.Length != 3)
                throw new InvalidOperationException($"{typeof(T).Name} has {TriStateSwitch<T>.VALUES.Length} members; a three-position switch needs exactly three.");
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;

            InternalChildren = new Drawable[]
            {
                background = new FormControlBackground(),
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = new MarginPadding(9),
                    Child = new GridContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        ColumnDimensions = new[]
                        {
                            new Dimension(),
                            new Dimension(GridSizeMode.AutoSize),
                        },
                        RowDimensions = new[] { new Dimension(GridSizeMode.AutoSize) },
                        Content = new[]
                        {
                            new Drawable[]
                            {
                                caption = new FormFieldCaption
                                {
                                    Anchor = Anchor.CentreLeft,
                                    Origin = Anchor.CentreLeft,
                                    Caption = Caption,
                                    TooltipText = HintText,
                                },
                                new FillFlowContainer
                                {
                                    Anchor = Anchor.CentreRight,
                                    Origin = Anchor.CentreRight,
                                    AutoSizeAxes = Axes.Both,
                                    Direction = FillDirection.Horizontal,
                                    Spacing = new Vector2(8, 0),
                                    Margin = new MarginPadding { Left = 5 },
                                    Children = new Drawable[]
                                    {
                                        valueLabel = new OsuSpriteText
                                        {
                                            Anchor = Anchor.CentreRight,
                                            Origin = Anchor.CentreRight,
                                            Font = OsuFont.Style.Caption1,
                                        },
                                        switchButton = new TriStateSwitch<T>
                                        {
                                            Anchor = Anchor.CentreRight,
                                            Origin = Anchor.CentreRight,
                                            Current = Current,
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            current.BindValueChanged(_ =>
            {
                updateState();
                background.FlashOnCommit();

                ValueChanged?.Invoke();
            });
            current.BindDisabledChanged(_ => updateState(), true);
        }

        protected override bool OnHover(HoverEvent e)
        {
            updateState();
            return true;
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            base.OnHoverLost(e);
            updateState();
        }

        protected override bool OnClick(ClickEvent e)
        {
            switchButton.Cycle();
            return true;
        }

        private void updateState()
        {
            valueLabel.Text = Current.Value.GetLocalisableDescription();
            caption.Colour = Current.Disabled ? colourProvider.Background1 : colourProvider.Content2;
            valueLabel.Colour = Current.Disabled ? colourProvider.Background1 : colourProvider.Content1;

            if (IsDisabled)
                background.VisualStyle = VisualStyle.Disabled;
            else if (IsHovered)
                background.VisualStyle = VisualStyle.Hovered;
            else
                background.VisualStyle = VisualStyle.Normal;
        }

        public IEnumerable<LocalisableString> FilterTerms => new[] { Caption, HintText };

        public event Action? ValueChanged;

        public bool IsDefault => Current.IsDefault;

        public void SetDefault() => Current.SetDefault();

        public bool IsDisabled => Current.Disabled;

        public float MainDrawHeight => DrawHeight;
    }
}
