// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using osuTK;

namespace typebeat.Game.Overlays.Profile.Sections
{
    public abstract partial class PaginatedProfileSubsection<TModel> : ProfileSubsection
    {
        /// <summary>
        /// The number of items displayed per page.
        /// </summary>
        protected virtual int ItemsPerPage => 50;

        /// <summary>
        /// The number of items displayed initially.
        /// </summary>
        protected virtual int InitialItemsCount => 5;

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        protected PaginationParameters? CurrentPage { get; private set; }

        protected ReverseChildIDFillFlowContainer<Drawable> ItemsContainer { get; private set; } = null!;

        private APIRequest<List<TModel>>? retrievalRequest;
        private CancellationTokenSource? loadCancellation;

        private ShowMoreButton moreButton = null!;
        private OsuSpriteText missing = null!;
        private OsuHoverContainer failed = null!;
        private readonly LocalisableString? missingText;

        protected PaginatedProfileSubsection(Bindable<UserProfileData?> user, LocalisableString? headerText = null, LocalisableString? missingText = null)
            : base(user, headerText, CounterVisibilityState.AlwaysVisible)
        {
            this.missingText = missingText;
        }

        protected override Drawable CreateContent() => new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Children = new Drawable[]
            {
                // reverse ID flow is required for correct Z-ordering of the items (last item should be front-most).
                // particularly important in PaginatedBeatmapContainer, as it uses beatmap cards, which have expandable overhanging content.
                ItemsContainer = new ReverseChildIDFillFlowContainer<Drawable>
                {
                    AutoSizeAxes = Axes.Y,
                    RelativeSizeAxes = Axes.X,
                    Spacing = new Vector2(0, 2),
                    // ensure the container and its contents are in front of the "more" button.
                    Depth = float.MinValue
                },
                moreButton = new ShowMoreButton
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    Alpha = 0,
                    Margin = new MarginPadding { Top = 10 },
                    Action = showMore,
                },
                missing = new OsuSpriteText
                {
                    Font = OsuFont.GetFont(size: 15),
                    Text = missingText ?? string.Empty,
                    Alpha = 0,
                },
                // The outcome a failed fetch would otherwise never state. It is a SEPARATE drawable from
                // `missing` on purpose: "this player has no records here" and "we could not find out" are
                // different answers, and `missing` is additionally optional (most subsections pass no
                // missingText at all), so folding the two together would leave most failures silent.
                failed = new OsuHoverContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Alpha = 0,
                    Action = retry,
                    Child = new OsuSpriteText
                    {
                        Font = OsuFont.GetFont(size: 15),
                        Text = UserProfileOverlayStrings.CouldNotLoadSection,
                    }
                }
            }
        };

        protected override void LoadComplete()
        {
            base.LoadComplete();
            User.BindValueChanged(onUserChanged, true);
        }

        private void onUserChanged(ValueChangedEvent<UserProfileData?> e)
        {
            loadCancellation?.Cancel();

            retrievalRequest?.Cancel();

            // the field only ever holds a request whose outcome is still wanted. Dropping the reference the
            // instant it is cancelled is what lets the callbacks in showMore() discriminate: a cancelled
            // request DOES reach its Failure arm in this framework (Cancel() is Fail(OperationCanceled),
            // running the same TriggerFailure path as a 404), so without this, switching profiles would
            // paint "couldn't load this section" over the healthy fetch that had just replaced it.
            retrievalRequest = null;

            CurrentPage = null;
            ItemsContainer.Clear();

            // both outcomes of the PREVIOUS user are stale now. Neither is cleared anywhere else on this
            // path (UpdateItems only hides them once its async load lands), so without this the new user's
            // section reads as empty, or as failed, for as long as their fetch is in flight.
            missing.Hide();
            failed.Hide();

            if (e.NewValue?.User != null)
            {
                showMore();
                SetCount(GetCount(e.NewValue.User));
            }
        }

        private void showMore()
        {
            if (User.Value == null)
                return;

            loadCancellation = new CancellationTokenSource();

            // the cursor as it stood BEFORE this page was claimed, so a failure can hand it back (see
            // pageLoadFailed). Retrying otherwise asks for the page AFTER the one that never arrived,
            // silently swallowing a page of the player's scores.
            var pageBefore = CurrentPage;

            CurrentPage = CurrentPage?.TakeNext(ItemsPerPage) ?? new PaginationParameters(InitialItemsCount);

            var req = CreateRequest(User.Value, new PaginationParameters(CurrentPage.Value.Offset, CurrentPage.Value.Limit + 1));

            req.Success += items =>
            {
                if (retrievalRequest != req) return;

                retrievalRequest = null;
                UpdateItems(items, loadCancellation);
            };

            req.Failure += e =>
            {
                if (retrievalRequest != req) return;

                retrievalRequest = null;
                pageLoadFailed(pageBefore, e);
            };

            retrievalRequest = req;

            api.Queue(req);
        }

        /// <summary>
        /// Resolves a page that will never arrive into a stated outcome with a way out.
        /// </summary>
        /// <remarks>
        /// Without this the section degrades quietly rather than loudly (<see cref="moreButton"/> starts at
        /// <c>Alpha = 0</c>, so nothing is left spinning), but quietly is the problem: the "no records"
        /// placeholder only ever renders from <see cref="UpdateItems"/>, which a failure never reaches, so a
        /// dropped connection is indistinguishable from a player who genuinely has nothing here.
        /// </remarks>
        /// <param name="pageBefore">The pagination cursor to restore, so a retry re-asks for the failed page.</param>
        /// <param name="exception">What the request failed with; logged, never shown.</param>
        private void pageLoadFailed(PaginationParameters? pageBefore, Exception exception) => Schedule(() =>
        {
            CurrentPage = pageBefore;

            // the network target rather than Logger.Error: a dropped connection is not a bug report, and an
            // error would pop a notification on top of a surface that is already saying the same thing.
            Logger.Log($@"Failed to load {GetType().ReadableName()} for user {User.Value?.User.Id}: {exception}", LoggingTarget.Network);

            // a failure on page 2+ leaves the pages that DID arrive on screen; only the button that asks for
            // more is replaced, because pressing it again is exactly what the retry does.
            moreButton.IsLoading = false;
            moreButton.Hide();

            missing.Hide();
            failed.Show();
        });

        private void retry()
        {
            failed.Hide();
            moreButton.IsLoading = true;
            moreButton.Show();

            showMore();
        }

        protected virtual void UpdateItems(List<TModel> items, CancellationTokenSource cancellationTokenSource) => Schedule(() =>
        {
            if (!items.Any() && CurrentPage?.Offset == 0)
            {
                moreButton.Hide();
                moreButton.IsLoading = false;

                if (missingText.HasValue)
                    missing.Show();

                return;
            }

            bool hasMore = items.Count > CurrentPage?.Limit;

            if (hasMore)
                items.RemoveAt(items.Count - 1);

            OnItemsReceived(items);

            LoadComponentsAsync(items.Select(CreateDrawableItem).Where(d => d != null).Cast<Drawable>(), drawables =>
            {
                missing.Hide();

                moreButton.FadeTo(hasMore ? 1 : 0);
                moreButton.IsLoading = false;

                ItemsContainer.AddRange(drawables);
            }, cancellationTokenSource.Token);
        });

        protected virtual int GetCount(APIUser user) => 0;

        protected virtual void OnItemsReceived(List<TModel> items)
        {
        }

        protected abstract APIRequest<List<TModel>> CreateRequest(UserProfileData user, PaginationParameters pagination);

        protected abstract Drawable? CreateDrawableItem(TModel model);

        protected override void Dispose(bool isDisposing)
        {
            retrievalRequest?.Cancel();
            loadCancellation?.Cancel();
            base.Dispose(isDisposing);
        }
    }
}
