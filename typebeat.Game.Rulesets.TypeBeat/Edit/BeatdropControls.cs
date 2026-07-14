// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Components.Timelines.Summary.Parts;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// Compose-mode strip for the map's optional intro beatdrop timestamp: a millisecond field,
    /// a stamp-at-playhead button (matching the editor's T/Enter stamping idiom) and a clear
    /// button. The value drives intro track timing on game startup — see IntroScreen.
    /// </summary>
    public partial class BeatdropToolbar : CompositeDrawable
    {
        [Resolved]
        private EditorBeatmap editorBeatmap { get; set; } = null!;

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        private OsuTextBox valueBox = null!;

        public BeatdropToolbar()
        {
            RelativeSizeAxes = Axes.X;
            Height = 34;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = TypeBeatStyle.PanelBackground,
                    Alpha = 0.6f,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(6, 0),
                    Padding = new MarginPadding { Horizontal = 8, Vertical = 3 },
                    Children = new Drawable[]
                    {
                        new OsuSpriteText
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Font = TypeBeatStyle.Mono(13),
                            Colour = TypeBeatStyle.UntypedChar,
                            Text = "intro beatdrop (ms)",
                        },
                        valueBox = new OsuTextBox
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Width = 110,
                            Height = 28,
                            CommitOnFocusLost = true,
                            PlaceholderText = "unset",
                        },
                        new RoundedButton
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Width = 130,
                            Height = 28,
                            Text = "set @ playhead",
                            Action = () => editorBeatmap.IntroBeatdrop.Value = Math.Round(editorClock.CurrentTime),
                        },
                        new RoundedButton
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Width = 80,
                            Height = 28,
                            Text = "clear",
                            Action = () => editorBeatmap.IntroBeatdrop.Value = null,
                        },
                    },
                },
            };

            valueBox.OnCommit += (_, _) => commitText();
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            editorBeatmap.IntroBeatdrop.BindValueChanged(
                drop => valueBox.Text = drop.NewValue is double d ? d.ToString("0", CultureInfo.InvariantCulture) : string.Empty, true);
        }

        private void commitText()
        {
            string text = valueBox.Text.Trim();

            if (text.Length == 0)
            {
                editorBeatmap.IntroBeatdrop.Value = null;
                return;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed >= 0)
                editorBeatmap.IntroBeatdrop.Value = Math.Round(parsed);
            else
            {
                // Invalid input: restore the current value's display.
                var current = editorBeatmap.IntroBeatdrop.Value;
                valueBox.Text = current is double d ? d.ToString("0", CultureInfo.InvariantCulture) : string.Empty;
            }
        }
    }

    /// <summary>
    /// Full-height marker on the compose waveform timeline at the intro beatdrop timestamp.
    /// Hidden while the beatdrop is unset.
    /// </summary>
    public partial class BeatdropMarkerPart : TimelinePart
    {
        private readonly Box marker;

        public BeatdropMarkerPart()
        {
            RelativeSizeAxes = Axes.Both;

            marker = new Box
            {
                RelativePositionAxes = Axes.X,
                RelativeSizeAxes = Axes.Y,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopCentre,
                Width = 3,
                Colour = TypeBeatStyle.ErrorChar,
                Alpha = 0,
            };
        }

        protected override void LoadBeatmap(EditorBeatmap beatmap)
        {
            base.LoadBeatmap(beatmap);
            Add(marker);
        }

        protected override void Update()
        {
            base.Update();

            // Position is milliseconds; TimelinePart's RelativeChildSize maps it to track fraction.
            marker.Alpha = EditorBeatmap.IntroBeatdrop.Value.HasValue ? 0.9f : 0;
            marker.X = (float)(EditorBeatmap.IntroBeatdrop.Value ?? 0);
        }
    }
}
