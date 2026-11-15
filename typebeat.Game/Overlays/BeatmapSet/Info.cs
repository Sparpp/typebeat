// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
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
                language.Metadata = songLanguages(b.NewValue);
                mapperTags.Metadata = b.NewValue?.Tags ?? string.Empty;
            });
            Beatmap.BindValueChanged(b => successRate.Beatmap = b.NewValue);
        }

        /// <summary>
        /// The set's song languages as lazer's language metadata, one entry per language (backlog 472: a set may
        /// declare several). The server sends them as <c>song_languages</c> (with the single <c>song_language</c> from
        /// an older server as the fallback, see <see cref="APIBeatmapSet.SongLanguageList"/>), canonical names rather
        /// than osu's <c>{id, name}</c> object. A name that is one of <see cref="SearchLanguage"/>'s members keeps its
        /// id, so it links to the listing's language filter; a type!beat language osu's list lacks (Dutch, say) gets
        /// no id and shows as plain text under its own name instead of reading as Unspecified. A set with none shows
        /// Unspecified, as it always did.
        /// </summary>
        private static BeatmapSetOnlineLanguage[] songLanguages(APIBeatmapSet? set)
        {
            var languages = set?.SongLanguageList ?? Array.Empty<BeatmapLanguage>();

            if (languages.Count == 0)
                return [new BeatmapSetOnlineLanguage { Id = (int)SearchLanguage.Unspecified, Name = SearchLanguage.Unspecified.ToString() }];

            return languages.Select(l => Enum.TryParse(l.ToString(), out SearchLanguage searchLanguage)
                                ? new BeatmapSetOnlineLanguage { Id = (int)searchLanguage, Name = searchLanguage.ToString() }
                                : new BeatmapSetOnlineLanguage { Id = -1, Name = l.GetDescription() })
                            .ToArray();
        }

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider)
        {
            successRateBackground.Colour = colourProvider.Background4;
            background.Colour = colourProvider.Background5;
        }
    }
}
