// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Overlays.BeatmapListing;

namespace typebeat.Game.Overlays.BeatmapSet
{
    /// <summary>
    /// The set's description, source, language and mapper tags, beside the selected difficulty's success rate.
    /// Lazer's nominators, genre and user tags are not shown: the server has no nomination step, no genre and no
    /// user tagging.
    /// </summary>
    public partial class Info : Container
    {
        private const float metadata_width = 185;
        private const float spacing = 20;
        private const float base_height = 300;

        private readonly Box successRateBackground;
        private readonly Box background;

        public readonly Bindable<APIBeatmapSet> BeatmapSet = new Bindable<APIBeatmapSet>();
        public readonly Bindable<APIBeatmap> Beatmap = new Bindable<APIBeatmap>();

        public Info()
        {
            MetadataSectionDescription description;
            MetadataSectionSource source;
            MetadataSectionLanguage language;
            MetadataSectionMapperTags mapperTags;
            SuccessRate successRate;

            RelativeSizeAxes = Axes.X;
            Height = base_height;

            Children = new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both
                },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Padding = new MarginPadding { Top = 15, Horizontal = WaveOverlayContainer.HORIZONTAL_PADDING },
                    Children = new Drawable[]
                    {
                        new Container
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding { Right = metadata_width + BeatmapSetOverlay.RIGHT_WIDTH + spacing * 2 },
                            Child = new Container
                            {
                                RelativeSizeAxes = Axes.Both,
                                Child = description = new MetadataSectionDescription(),
                            },
                        },
                        new OsuScrollContainer
                        {
                            Anchor = Anchor.TopRight,
                            Origin = Anchor.TopRight,
                            RelativeSizeAxes = Axes.Y,
                            Width = metadata_width,
                            Padding = new MarginPadding { Left = 10 },
                            Margin = new MarginPadding { Right = BeatmapSetOverlay.RIGHT_WIDTH + spacing },
                            Masking = true,
                            ScrollbarOverlapsContent = false,
                            Child = new FillFlowContainer
                            {
                                RelativeSizeAxes = Axes.X,
                                AutoSizeAxes = Axes.Y,
                                Direction = FillDirection.Full,
                                Padding = new MarginPadding { Right = 5 },
                                Children = new Drawable[]
                                {
                                    source = new MetadataSectionSource(),
                                    language = new MetadataSectionLanguage(),
                                    mapperTags = new MetadataSectionMapperTags(),
                                },
                            },
                        },
                        new Container
                        {
                            Anchor = Anchor.TopRight,
                            Origin = Anchor.TopRight,
                            RelativeSizeAxes = Axes.Y,
                            Width = BeatmapSetOverlay.RIGHT_WIDTH,
                            Children = new Drawable[]
                            {
                                successRateBackground = new Box
                                {
                                    RelativeSizeAxes = Axes.Both,
                                },
                                successRate = new SuccessRate
                                {
                                    RelativeSizeAxes = Axes.Both,
                                    Padding = new MarginPadding { Top = 20, Horizontal = 15 },
                                },
                            },
                        },
                    },
                },
            };

            BeatmapSet.BindValueChanged(b =>
            {
                description.Metadata = b.NewValue?.Description ?? string.Empty;
                source.Metadata = b.NewValue?.Source ?? string.Empty;
                language.Metadata = songLanguage(b.NewValue);
                mapperTags.Metadata = b.NewValue?.Tags ?? string.Empty;
            });
            Beatmap.BindValueChanged(b => successRate.Beatmap = b.NewValue);
        }

        /// <summary>
        /// The set's song language as lazer's language metadata. The server sends it as <c>song_language</c>, a canonical
        /// name (osu's language list, lower-cased) or empty when unknown, rather than osu's <c>{id, name}</c> object, and
        /// the names are <see cref="SearchLanguage"/>'s members.
        /// </summary>
        private static BeatmapSetOnlineLanguage songLanguage(APIBeatmapSet? set)
        {
            var parsed = Enum.TryParse(set?.SongLanguage, ignoreCase: true, out SearchLanguage language) ? language : SearchLanguage.Unspecified;

            return new BeatmapSetOnlineLanguage { Id = (int)parsed, Name = parsed.ToString() };
        }

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider)
        {
            successRateBackground.Colour = colourProvider.Background4;
            background.Colour = colourProvider.Background5;
        }
    }
}
