// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Localisation;
using osu.Framework.Threading;
using typebeat.Game.Beatmaps.Drawables.Cards;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Resources.Localisation.Web;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Overlays.BeatmapListing
{
    public partial class BeatmapListingFilterControl : CompositeDrawable
    {
        /// <summary>
        /// Fired when a search finishes.
        /// </summary>
        public Action<SearchResult> SearchFinished;

        /// <summary>
        /// Fired when search criteria change.
        /// </summary>
        public Action SearchStarted;

        /// <summary>
        /// Any time the search text box receives key events (even while masked).
        /// </summary>
        public Action TypingStarted;

        /// <summary>
        /// True when pagination has reached the end of available results.
        /// </summary>
        private bool noMoreResults;

        /// <summary>
        /// The current page fetched of results (zero index).
        /// </summary>
        public int CurrentPage { get; private set; }

        /// <summary>
        /// The currently selected <see cref="BeatmapCardSize"/>.
        /// </summary>
        public IBindable<BeatmapCardSize> CardSize => cardSize;

        private readonly Bindable<BeatmapCardSize> cardSize = new Bindable<BeatmapCardSize>();

        private readonly BeatmapListingSearchControl searchControl;
        private readonly BeatmapListingSortTabControl sortControl;
        private readonly Box sortControlBackground;
        private readonly OsuSpriteText resultCount;

        /// <summary>The result count sprite's name, for tests to find it by.</summary>
        public const string RESULT_COUNT_NAME = @"result count";

        private ScheduledDelegate queryChangedDebounce;

        private SearchBeatmapSetsRequest getSetsRequest;
        private SearchBeatmapSetsResponse lastResponse;

        [Resolved]
        private IAPIProvider api { get; set; }

        private IBindable<APIUser> apiUser;

        public BeatmapListingFilterControl()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;

            InternalChild = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 10),
                Children = new Drawable[]
                {
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Masking = true,
                        EdgeEffect = new EdgeEffectParameters
                        {
                            Colour = Color4.Black.Opacity(0.25f),
                            Type = EdgeEffectType.Shadow,
                            Radius = 3,
                            Offset = new Vector2(0f, 1f),
                        },
                        Child = searchControl = new BeatmapListingSearchControl
                        {
                            TypingStarted = () => TypingStarted?.Invoke()
                        }
                    },
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        Height = 40,
                        Children = new Drawable[]
                        {
                            sortControlBackground = new Box
                            {
                                RelativeSizeAxes = Axes.Both
                            },
                            sortControl = new BeatmapListingSortTabControl
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Margin = new MarginPadding { Left = 20 }
                            },
                            new FillFlowContainer
                            {
                                Anchor = Anchor.CentreRight,
                                Origin = Anchor.CentreRight,
                                AutoSizeAxes = Axes.Both,
                                Direction = FillDirection.Horizontal,
                                Spacing = new Vector2(15, 0),
                                Margin = new MarginPadding { Right = 20 },
                                Children = new Drawable[]
                                {
                                    // How many sets the whole search matches (every page), beside the card size
                                    // toggle and in the "Sort by" label's type. Hidden until the first answer,
                                    // and for a server that does not send a total.
                                    resultCount = new OsuSpriteText
                                    {
                                        Name = RESULT_COUNT_NAME,
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                        Font = OsuFont.GetFont(size: 12, weight: FontWeight.SemiBold),
                                        Alpha = 0,
                                    },
                                    new BeatmapListingCardSizeTabControl
                                    {
                                        Anchor = Anchor.CentreLeft,
                                        Origin = Anchor.CentreLeft,
                                        Current = { BindTarget = CardSize }
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }

        [Resolved]
        private OsuConfigManager config { get; set; }

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider)
        {
            sortControlBackground.Colour = colourProvider.Background4;
            resultCount.Colour = colourProvider.Content2;
        }

        public void Search(string query)
            => Schedule(() => searchControl.Query.Value = query);

        public void FilterGenre(SearchGenre genre)
            => Schedule(() => searchControl.Genre.Value = genre);

        public void FilterLanguage(SearchLanguage language)
            => Schedule(() => searchControl.Language.Value = language);

        protected override void LoadComplete()
        {
            base.LoadComplete();

            config.BindWith(OsuSetting.BeatmapListingCardSize, cardSize);

            searchControl.Query.BindValueChanged(_ =>
            {
                resetSortControl();
                queueUpdateSearch(true);
            });

            searchControl.Category.BindValueChanged(_ =>
            {
                resetSortControl();
                queueUpdateSearch();
            });

            searchControl.General.CollectionChanged += (_, _) => queueUpdateSearch();
            searchControl.Ruleset.BindValueChanged(_ => queueUpdateSearch());
            searchControl.Genre.BindValueChanged(_ => queueUpdateSearch());
            searchControl.Language.BindValueChanged(_ => queueUpdateSearch());
            searchControl.Extra.CollectionChanged += (_, _) => queueUpdateSearch();
            searchControl.Ranks.CollectionChanged += (_, _) => queueUpdateSearch();
            searchControl.Played.BindValueChanged(_ => queueUpdateSearch());
            searchControl.ExplicitContent.BindValueChanged(_ => queueUpdateSearch());

            sortControl.Current.BindValueChanged(_ => queueUpdateSearch());
            sortControl.SortDirection.BindValueChanged(_ => queueUpdateSearch());

            apiUser = api.LocalUser.GetBoundCopy();
            apiUser.BindValueChanged(_ => queueUpdateSearch());
        }

        public void TakeFocus() => searchControl.TakeFocus();

        /// <summary>
        /// Fetch the next page of results. May result in a no-op if a fetch is already in progress, or if there are no results left.
        /// </summary>
        public void FetchNextPage()
        {
            // there may be no results left.
            if (noMoreResults)
                return;

            // there may already be an active request.
            if (getSetsRequest != null)
                return;

            if (lastResponse != null)
                CurrentPage++;

            performRequest();
        }

        private void resetSortControl() => sortControl.Reset(searchControl.Category.Value, !string.IsNullOrEmpty(searchControl.Query.Value));

        private void queueUpdateSearch(bool queryTextChanged = false)
        {
            SearchStarted?.Invoke();

            resetSearch();

            // The shown count belongs to the previous criteria now: dim it until the new search answers
            // rather than blanking it, so typing a query does not make the row flicker.
            if (resultCount.Alpha > 0)
                resultCount.FadeTo(0.5f, 200, Easing.OutQuint);

            // no sign-in gate: the search is a public read (SearchBeatmapSetsRequest.AllowsAnonymous).
            queryChangedDebounce = Scheduler.AddDelayed(() =>
            {
                resetSearch();
                FetchNextPage();
            }, queryTextChanged ? 500 : 100);
        }

        private void performRequest()
        {
            getSetsRequest = new SearchBeatmapSetsRequest(
                searchControl.Query.Value,
                searchControl.Ruleset.Value,
                lastResponse?.Cursor,
                searchControl.General,
                searchControl.Category.Value,
                sortControl.Current.Value,
                sortControl.SortDirection.Value,
                searchControl.Genre.Value,
                searchControl.Language.Value,
                searchControl.Extra,
                searchControl.Ranks,
                searchControl.Played.Value,
                searchControl.ExplicitContent.Value);

            getSetsRequest.Success += response =>
            {
                var sets = response.BeatmapSets.ToList();

                // If the previous request returned a null cursor, the API is indicating we can't paginate further (maybe there are no more beatmaps left).
                if (sets.Count == 0 || response.Cursor == null)
                    noMoreResults = true;

                if (CurrentPage == 0)
                {
                    searchControl.BeatmapSet = sets.FirstOrDefault();

                    // The total is the whole search's, so later pages repeat it; the first page sets it.
                    updateResultCount(response.Total);
                }

                lastResponse = response;
                getSetsRequest = null;

                // No supporter-only filters: the server keeps no supporter status and answers the played and rank
                // filters for every signed-in player.
                var resultsReturned = SearchResult.ResultsReturned(sets);
                SearchFinished?.Invoke(resultsReturned);
            };

            api.Queue(getSetsRequest);
        }

        private void updateResultCount(int? total)
        {
            if (total is not int count)
            {
                // An older server without the field: no count rather than a wrong one.
                resultCount.FadeOut(200, Easing.OutQuint);
                return;
            }

            resultCount.Text = FormatResultCount(count);
            resultCount.FadeIn(200, Easing.OutQuint);
        }

        /// <summary>"1 result", "0 results", "1,234 results": the same wording and grouping as the website listing.</summary>
        public static string FormatResultCount(int count)
            => $@"{count.ToString(@"N0", CultureInfo.InvariantCulture)} {(count == 1 ? @"result" : @"results")}";

        private void resetSearch()
        {
            noMoreResults = false;
            CurrentPage = 0;

            lastResponse = null;

            getSetsRequest?.Cancel();
            getSetsRequest = null;

            queryChangedDebounce?.Cancel();
        }

        protected override void Dispose(bool isDisposing)
        {
            resetSearch();

            base.Dispose(isDisposing);
        }

        /// <summary>
        /// Indicates the type of result of a user-requested beatmap search.
        /// </summary>
        public enum SearchResultType
        {
            /// <summary>
            /// Actual results have been returned from API.
            /// </summary>
            ResultsReturned,
        }

        /// <summary>
        /// Describes the result of a user-requested beatmap search.
        /// </summary>
        public struct SearchResult
        {
            public SearchResultType Type { get; private set; }

            /// <summary>
            /// Contains the beatmap sets returned from API.
            /// Valid for read if and only if <see cref="Type"/> is <see cref="SearchResultType.ResultsReturned"/>.
            /// </summary>
            public List<APIBeatmapSet> Results { get; private set; }

            public static SearchResult ResultsReturned(List<APIBeatmapSet> results) => new SearchResult
            {
                Type = SearchResultType.ResultsReturned,
                Results = results,
            };
        }
    }
}
