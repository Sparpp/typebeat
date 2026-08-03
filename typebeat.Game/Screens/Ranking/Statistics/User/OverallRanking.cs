// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Extensions;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;
using typebeat.Game.Online;
using typebeat.Game.Scoring;

namespace typebeat.Game.Screens.Ranking.Statistics.User
{
    /// <summary>
    /// The results screen's "Overall Ranking" panel: how this score moved the local user's profile statistics.
    ///
    /// <para>
    /// THREE STATES, and the third one is why this is not simply a grid behind a spinner. The delta can be on its way
    /// (spinner), it can have arrived (grid), or it can be established that it is never arriving, and that last case is
    /// reachable in ordinary play: the refetch after submission can fail, or it can succeed with no "before" snapshot to
    /// compare against. Both of those used to leave the spinner up forever, so the panel promised a number that could not
    /// come. It now says which of the two happened, in words. It does not fill the grid with zeroes to look resolved,
    /// for the same reason the score panel's pp readout shows a dash rather than a 0 for an unpriced play.
    /// </para>
    /// </summary>
    public partial class OverallRanking : CompositeDrawable
    {
        private const float transition_duration = 300;

        public Bindable<ScoreBasedUserStatisticsUpdate?> DisplayedUpdate { get; } = new Bindable<ScoreBasedUserStatisticsUpdate?>();
        private readonly IBindable<ScoreBasedUserStatisticsUpdate?> latestGlobalStatisticsUpdate = new Bindable<ScoreBasedUserStatisticsUpdate?>();

        private readonly Bindable<UnavailableStatisticsUpdate?> displayedUnavailableUpdate = new Bindable<UnavailableStatisticsUpdate?>();
        private readonly IBindable<UnavailableStatisticsUpdate?> latestGlobalUnavailableUpdate = new Bindable<UnavailableStatisticsUpdate?>();

        private readonly ScoreInfo scoreInfo;

        private LoadingLayer loadingLayer = null!;
        private GridContainer content = null!;
        private OsuTextFlowContainer unavailableText = null!;

        public OverallRanking(ScoreInfo scoreInfo)
        {
            this.scoreInfo = scoreInfo;
        }

        [BackgroundDependencyLoader]
        private void load(UserStatisticsWatcher? userStatisticsWatcher)
        {
            AutoSizeAxes = Axes.Y;
            AutoSizeEasing = Easing.OutQuint;
            AutoSizeDuration = transition_duration;

            InternalChildren = new Drawable[]
            {
                loadingLayer = new LoadingLayer(withBox: false)
                {
                    RelativeSizeAxes = Axes.Both,
                },
                content = new GridContainer
                {
                    AlwaysPresent = true,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    ColumnDimensions = new[]
                    {
                        new Dimension(),
                        new Dimension(GridSizeMode.Absolute, 30),
                        new Dimension(),
                    },
                    RowDimensions = new[]
                    {
                        new Dimension(GridSizeMode.AutoSize),
                        new Dimension(GridSizeMode.Absolute, 10),
                        new Dimension(GridSizeMode.AutoSize),
                        new Dimension(GridSizeMode.Absolute, 10),
                        new Dimension(GridSizeMode.AutoSize),
                    },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            new GlobalRankChangeRow { StatisticsUpdate = { BindTarget = DisplayedUpdate } },
                            new SimpleStatisticTable.Spacer(),
                            new PerformancePointsChangeRow { StatisticsUpdate = { BindTarget = DisplayedUpdate } },
                        },
                        [],
                        new Drawable[]
                        {
                            new MaximumComboChangeRow { StatisticsUpdate = { BindTarget = DisplayedUpdate } },
                            new SimpleStatisticTable.Spacer(),
                            new AccuracyChangeRow { StatisticsUpdate = { BindTarget = DisplayedUpdate } },
                        },
                        [],
                        new Drawable[]
                        {
                            new RankedScoreChangeRow { StatisticsUpdate = { BindTarget = DisplayedUpdate } },
                            new SimpleStatisticTable.Spacer(),
                            new TotalScoreChangeRow { StatisticsUpdate = { BindTarget = DisplayedUpdate } },
                        }
                    }
                },
                unavailableText = new OsuTextFlowContainer(t => t.Font = OsuFont.Default.With(size: StatisticItem.FONT_SIZE))
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    TextAnchor = Anchor.TopCentre,
                    Alpha = 0,
                },
            };

            if (userStatisticsWatcher != null)
            {
                latestGlobalStatisticsUpdate.BindTo(userStatisticsWatcher.LatestUpdate);
                latestGlobalStatisticsUpdate.BindValueChanged(update =>
                {
                    if (update.NewValue?.Score.MatchesOnlineID(scoreInfo) == true)
                        DisplayedUpdate.Value = update.NewValue;
                }, true);

                latestGlobalUnavailableUpdate.BindTo(userStatisticsWatcher.LatestUnavailableUpdate);
                latestGlobalUnavailableUpdate.BindValueChanged(unavailable =>
                {
                    if (unavailable.NewValue?.Score.MatchesOnlineID(scoreInfo) == true)
                        displayedUnavailableUpdate.Value = unavailable.NewValue;
                }, true);
            }
            else
            {
                // the watcher is resolved optionally, so it can genuinely be absent (a test scene, or a game that failed to
                // cache it, which is what the whole feature did before task 76). Nothing would ever drive this panel then,
                // so resolve it here instead of spinning on a promise that no component exists to keep.
                displayedUnavailableUpdate.Value = new UnavailableStatisticsUpdate(scoreInfo, StatisticsUpdateUnavailableReason.FetchFailed);
            }
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            DisplayedUpdate.BindValueChanged(_ => updateState());
            displayedUnavailableUpdate.BindValueChanged(_ => updateState());

            updateState();
            FinishTransforms(true);
        }

        /// <summary>
        /// Settles the panel into exactly one of its three states. An arrived update always wins over an unavailability,
        /// so a late-but-real delta can still replace the message rather than being locked out by it.
        /// </summary>
        private void updateState()
        {
            if (DisplayedUpdate.Value != null)
            {
                loadingLayer.Hide();
                content.AlwaysPresent = true;
                content.FadeIn(transition_duration, Easing.OutQuint);
                unavailableText.FadeOut(transition_duration, Easing.OutQuint);
                return;
            }

            if (displayedUnavailableUpdate.Value != null)
            {
                unavailableText.Text = displayedUnavailableUpdate.Value.Reason == StatisticsUpdateUnavailableReason.NoPreviousStatistics
                    ? ResultsScreenStrings.PreviousStatisticsUnavailable
                    : ResultsScreenStrings.StatisticsUpdateUnavailable;

                loadingLayer.Hide();

                // drop the (invisible) grid out of layout so the panel collapses onto the message. Six blank rows around one
                // line of text reads like the panel is still loading, which is the impression this whole change exists to end.
                content.AlwaysPresent = false;
                content.FadeOut(transition_duration, Easing.OutQuint);
                unavailableText.FadeIn(transition_duration, Easing.OutQuint);
                return;
            }

            loadingLayer.Show();
            content.AlwaysPresent = true;
            content.FadeOut(transition_duration, Easing.OutQuint);
            unavailableText.FadeOut(transition_duration, Easing.OutQuint);
        }
    }
}
