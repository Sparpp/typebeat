// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Overlays.BeatmapSet.Buttons;
using osuTK;

namespace typebeat.Game.Overlays.BeatmapSet
{
    public partial class Details : FillFlowContainer
    {
        private readonly PreviewButton preview;
        private readonly BasicStats basic;

        private APIBeatmapSet beatmapSet;

        public APIBeatmapSet BeatmapSet
        {
            get => beatmapSet;
            set
            {
                if (value == beatmapSet) return;

                basic.BeatmapSet = preview.BeatmapSet = beatmapSet = value;
            }
        }

        private IBeatmapInfo beatmapInfo;

        public IBeatmapInfo BeatmapInfo
        {
            get => beatmapInfo;
            set
            {
                if (value == beatmapInfo) return;

                basic.BeatmapInfo = beatmapInfo = value;
            }
        }

        public Details()
        {
            Width = BeatmapSetOverlay.RIGHT_WIDTH;
            AutoSizeAxes = Axes.Y;
            Spacing = new Vector2(1f);

            Children = new Drawable[]
            {
                preview = new PreviewButton
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 42,
                },
                new DetailBox
                {
                    Child = basic = new BasicStats
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Padding = new MarginPadding { Vertical = 10 }
                    },
                },
            };
        }

        private partial class DetailBox : Container
        {
            private readonly Container content;
            private readonly Box background;

            protected override Container<Drawable> Content => content;

            public DetailBox()
            {
                RelativeSizeAxes = Axes.X;
                AutoSizeAxes = Axes.Y;

                InternalChildren = new Drawable[]
                {
                    background = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Alpha = 0.5f
                    },
                    content = new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Padding = new MarginPadding { Horizontal = 15 },
                    },
                };
            }

            [BackgroundDependencyLoader]
            private void load(OverlayColourProvider colourProvider)
            {
                background.Colour = colourProvider.Background6;
            }
        }
    }
}
