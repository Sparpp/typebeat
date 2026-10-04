// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Extensions.LocalisationExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Extensions;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Resources.Localisation.Web;
using osuTK;

namespace typebeat.Game.Overlays.BeatmapSet
{
    public partial class BasicStats : Container
    {
        private readonly Statistic length;

        private IBeatmapSetInfo beatmapSet;

        public IBeatmapSetInfo BeatmapSet
        {
            get => beatmapSet;
            set
            {
                if (value == beatmapSet) return;

                beatmapSet = value;

                updateDisplay();
            }
        }

        private IBeatmapInfo beatmapInfo;

        public IBeatmapInfo BeatmapInfo
        {
            get => beatmapInfo;
            set
            {
                if (value == beatmapInfo) return;

                beatmapInfo = value;

                updateDisplay();
            }
        }

        private void updateDisplay()
        {
            if (beatmapInfo == null)
            {
                length.Value = string.Empty;
            }
            else
            {
                length.Value = TimeSpan.FromMilliseconds(beatmapInfo.Length).ToFormattedDuration();

                if (beatmapInfo is not IBeatmapOnlineInfo onlineInfo) return;

                length.TooltipText = BeatmapsetsStrings.ShowStatsTotalLength(TimeSpan.FromMilliseconds(onlineInfo.HitLength).ToFormattedDuration());
            }
        }

        public BasicStats()
        {
            Child = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Horizontal,
                Children = new[]
                {
                    length = new Statistic(BeatmapStatisticsIconType.Length)
                    {
                        Width = 1f,
                        TooltipText = default,
                    },
                },
            };
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            updateDisplay();
        }

        private partial class Statistic : Container, IHasTooltip
        {
            private readonly OsuSpriteText value;

            public LocalisableString TooltipText { get; set; }

            public LocalisableString Value
            {
                get => value.Text;
                set => this.value.Text = value;
            }

            public Statistic(BeatmapStatisticsIconType icon)
            {
                RelativeSizeAxes = Axes.X;
                Height = 24f;

                Children = new Drawable[]
                {
                    new Container
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        AutoSizeAxes = Axes.X,
                        RelativeSizeAxes = Axes.Y,
                        Children = new Drawable[]
                        {
                            new SpriteIcon
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.Centre,
                                Icon = FontAwesome.Solid.Square,
                                Size = new Vector2(12),
                                Rotation = 45,
                                Colour = Color4Extensions.FromHex(@"441288"),
                            },
                            new SpriteIcon
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.Centre,
                                Icon = FontAwesome.Regular.Circle,
                                Size = new Vector2(10),
                                Rotation = 0,
                                Colour = Color4Extensions.FromHex(@"f7dd55"),
                            },
                            new BeatmapStatisticIcon(icon)
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.Centre,
                                Size = new Vector2(10),
                                Colour = Color4Extensions.FromHex(@"f7dd55"),
                                Scale = new Vector2(0.8f),
                            },
                            value = new OsuSpriteText
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Margin = new MarginPadding { Left = 10 },
                                Font = OsuFont.GetFont(size: 12, weight: FontWeight.Bold),
                            },
                        },
                    },
                };
            }

            [BackgroundDependencyLoader]
            private void load(OsuColour colour)
            {
                value.Colour = colour.Yellow;
            }
        }
    }
}
