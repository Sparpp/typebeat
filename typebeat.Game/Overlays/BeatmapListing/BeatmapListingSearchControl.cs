// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input.Events;
using typebeat.Game.Beatmaps.Drawables;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Resources.Localisation.Web;
using typebeat.Game.Rulesets;
using typebeat.Game.Scoring;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Overlays.BeatmapListing
{
    public partial class BeatmapListingSearchControl : CompositeDrawable
    {
        /// <summary>
        /// Any time the text box receives key events (even while masked).
        /// </summary>
        public Action? TypingStarted;

        public Bindable<string> Query => textBox.Current;

        // Every row shown is a filter the server honours over stored data, offering only the values it has data for.
        // Mode (one ruleset) and genre (not stored) have no row, and stay as inert bindables at their "any" values so
        // the request and the filter control keep their osu! shape.
        public BindableList<SearchGeneral> General => generalFilter.Current;

        public Bindable<RulesetInfo> Ruleset { get; } = new Bindable<RulesetInfo>(new RulesetInfo());

        public Bindable<SearchCategory> Category => categoryFilter.Current;

        public Bindable<SearchGenre> Genre { get; } = new Bindable<SearchGenre>();

        public Bindable<SearchLanguage> Language => languageFilter.Current;

        public BindableList<SearchExtra> Extra => extraFilter.Current;

        public BindableList<ScoreRank> Ranks => ranksFilter.Current;

        public Bindable<SearchPlayed> Played => playedFilter.Current;

        public Bindable<SearchExplicit> ExplicitContent => explicitContentFilter.Current;

        public APIBeatmapSet? BeatmapSet
        {
            set
            {
                if (value == null || string.IsNullOrEmpty(value.Covers.Cover))
                {
                    beatmapCover.FadeOut(600, Easing.OutQuint);
                    return;
                }

                beatmapCover.OnlineInfo = value;
                beatmapCover.FadeTo(0.1f, 200, Easing.OutQuint);
            }
        }

        private readonly BeatmapSearchTextBox textBox;
        private readonly BeatmapSearchMultipleSelectionFilterRow<SearchGeneral> generalFilter;
        private readonly BeatmapSearchFilterRow<SearchCategory> categoryFilter;
        private readonly BeatmapSearchFilterRow<SearchLanguage> languageFilter;
        private readonly BeatmapSearchMultipleSelectionFilterRow<SearchExtra> extraFilter;
        private readonly BeatmapSearchScoreFilterRow ranksFilter;
        private readonly BeatmapSearchFilterRow<SearchPlayed> playedFilter;
        private readonly BeatmapSearchFilterRow<SearchExplicit> explicitContentFilter;

        private readonly Box background;
        private readonly UpdateableOnlineBeatmapSetCover beatmapCover;

        public BeatmapListingSearchControl()
        {
            AutoSizeAxes = Axes.Y;
            RelativeSizeAxes = Axes.X;
            AddRangeInternal(new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both
                },
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    Child = beatmapCover = new TopSearchBeatmapSetCover
                    {
                        RelativeSizeAxes = Axes.Both,
                        Alpha = 0,
                    }
                },
                new Container
                {
                    AutoSizeAxes = Axes.Y,
                    RelativeSizeAxes = Axes.X,
                    Padding = new MarginPadding
                    {
                        Vertical = 20,
                        Horizontal = WaveOverlayContainer.HORIZONTAL_PADDING,
                    },
                    Child = new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Y,
                        RelativeSizeAxes = Axes.X,
                        Direction = FillDirection.Vertical,
                        Spacing = new Vector2(0, 20),
                        Children = new Drawable[]
                        {
                            textBox = new BeatmapSearchTextBox
                            {
                                RelativeSizeAxes = Axes.X,
                                TextChanged = () => TypingStarted?.Invoke(),
                            },
                            new ReverseChildIDFillFlowContainer<Drawable>
                            {
                                AutoSizeAxes = Axes.Y,
                                RelativeSizeAxes = Axes.X,
                                Direction = FillDirection.Vertical,
                                Padding = new MarginPadding { Horizontal = 10 },
                                Spacing = new Vector2(5),
                                Children = new Drawable[]
                                {
                                    // Subscribed mappers only: recommended difficulty, converts, spotlights and featured artists have no data here.
                                    generalFilter = new BeatmapSearchMultipleSelectionFilterRow<SearchGeneral>(BeatmapsStrings.ListingSearchFiltersGeneral, new[] { SearchGeneral.Follows }),
                                    // No qualified or loved status exists; "has leaderboard" and "WIP" would repeat ranked and pending.
                                    categoryFilter = new BeatmapSearchFilterRow<SearchCategory>(BeatmapsStrings.ListingSearchFiltersStatus, new[]
                                    {
                                        SearchCategory.Any, SearchCategory.Ranked, SearchCategory.Favourites, SearchCategory.Pending, SearchCategory.Graveyard, SearchCategory.Mine
                                    }),
                                    languageFilter = new BeatmapSearchFilterRow<SearchLanguage>(BeatmapsStrings.ListingSearchFiltersLanguage),
                                    // Video only: storyboards are not recorded.
                                    extraFilter = new BeatmapSearchMultipleSelectionFilterRow<SearchExtra>(BeatmapsStrings.ListingSearchFiltersExtra, new[] { SearchExtra.Video }),
                                    ranksFilter = new BeatmapSearchScoreFilterRow(),
                                    playedFilter = new BeatmapSearchFilterRow<SearchPlayed>(BeatmapsStrings.ListingSearchFiltersPlayed),
                                    explicitContentFilter = new BeatmapSearchFilterRow<SearchExplicit>(BeatmapsStrings.ListingSearchFiltersNsfw),
                                }
                            }
                        }
                    }
                }
            });

            categoryFilter.Current.Value = SearchCategory.Any;
        }

        private IBindable<bool> allowExplicitContent = null!;

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider, OsuConfigManager config)
        {
            background.Colour = colourProvider.Dark6;

            allowExplicitContent = config.GetBindable<bool>(OsuSetting.ShowOnlineExplicitContent);
            allowExplicitContent.BindValueChanged(allow =>
            {
                ExplicitContent.Value = allow.NewValue ? SearchExplicit.Show : SearchExplicit.Hide;
            }, true);
        }

        public void TakeFocus() => textBox.TakeFocus();

        private partial class BeatmapSearchTextBox : BasicSearchTextBox
        {
            /// <summary>
            /// Any time the text box receives key events (even while masked).
            /// </summary>
            public Action? TextChanged;

            protected override Color4 SelectionColour => Color4.Gray;

            public BeatmapSearchTextBox()
            {
                PlaceholderText = BeatmapsStrings.ListingSearchPrompt;
            }

            protected override bool OnKeyDown(KeyDownEvent e)
            {
                if (!base.OnKeyDown(e))
                    return false;

                TextChanged?.Invoke();
                return true;
            }

            public override bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
            {
                if (!base.OnPressed(e))
                    return false;

                TextChanged?.Invoke();
                return true;
            }
        }

        private partial class TopSearchBeatmapSetCover : UpdateableOnlineBeatmapSetCover
        {
            protected override bool TransformImmediately => true;
        }
    }
}
