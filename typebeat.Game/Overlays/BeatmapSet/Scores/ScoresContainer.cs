// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Diagnostics;
using System.Linq;
using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Beatmaps;
using typebeat.Game.Extensions;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Rulesets;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Play.Leaderboards;
using osuTK;
using APIUser = typebeat.Game.Online.API.Requests.Responses.APIUser;

namespace typebeat.Game.Overlays.BeatmapSet.Scores
{
    public partial class ScoresContainer : BeatmapSetLayoutSection
    {
        private const int spacing = 15;

        public readonly Bindable<APIBeatmap> Beatmap = new Bindable<APIBeatmap>();
        private readonly IBindable<APIUser> user = new Bindable<APIUser>();

        private readonly Box background;
        private readonly ScoreTable scoreTable;
        private readonly FillFlowContainer topScoresContainer;
        private readonly LoadingLayer loading;
        private readonly NoScoresPlaceholder noScoresPlaceholder;

        [Resolved]
        private IAPIProvider api { get; set; }

        [Resolved]
        private RulesetStore rulesets { get; set; }

        private GetScoresRequest getScoresRequest;

        private CancellationTokenSource loadCancellationSource;

        protected APIScoresCollection Scores
        {
            set => Schedule(() =>
            {
                loadCancellationSource?.Cancel();
                loadCancellationSource = new CancellationTokenSource();

                topScoresContainer.Clear();
                scoreTable.ClearScores();
                scoreTable.Hide();

                loading.Hide();
                loading.FinishTransforms();

                if (value?.Scores.Any() != true)
                    return;

                var apiBeatmap = Beatmap.Value;

                Debug.Assert(apiBeatmap != null);

                // TODO: temporary. should be removed once `OrderByTotalScore` can accept `IScoreInfo`.
                var beatmapInfo = new BeatmapInfo
                {
#pragma warning disable 618
                    MaxCombo = apiBeatmap.MaxCombo,
#pragma warning restore 618
                    Status = apiBeatmap.Status,
                    MD5Hash = apiBeatmap.MD5Hash
                };

                var scores = value.Scores.Select(s => s.ToScoreInfo(rulesets, beatmapInfo)).OrderByTotalScore().ToArray();
                var topScore = scores.First();

                scoreTable.DisplayScores(scores, apiBeatmap.Status.GrantsPerformancePoints());
                scoreTable.Show();

                var userScore = value.UserScore;
                var userScoreInfo = userScore?.Score.ToScoreInfo(rulesets, beatmapInfo);

                topScoresContainer.Add(new DrawableTopScore(topScore));

                if (userScoreInfo != null && userScoreInfo.OnlineID != topScore.OnlineID)
                    topScoresContainer.Add(new DrawableTopScore(userScoreInfo, userScore.Position));
            });
        }

        public ScoresContainer()
        {
            AddRange(new Drawable[]
            {
                background = new Box
                {
                    RelativeSizeAxes = Axes.Both,
                },
                new FillFlowContainer
                {
                    Anchor = Anchor.TopCentre,
                    Origin = Anchor.TopCentre,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Padding = new MarginPadding { Horizontal = WaveOverlayContainer.HORIZONTAL_PADDING },
                    Margin = new MarginPadding { Vertical = 20 },
                    Children = new Drawable[]
                    {
                        // No scope selector (country / friends / team) or mod filter: the server always answers the global
                        // board and ignores both, so neither could change what is shown. That also retires lazer's
                        // supporter-only and no-team placeholders, which only those two controls could reach.
                        new Container
                        {
                            AutoSizeAxes = Axes.Y,
                            RelativeSizeAxes = Axes.X,
                            Children = new Drawable[]
                            {
                                noScoresPlaceholder = new NoScoresPlaceholder
                                {
                                    Anchor = Anchor.TopCentre,
                                    Origin = Anchor.TopCentre,
                                    Alpha = 0,
                                    AlwaysPresent = true,
                                    Margin = new MarginPadding { Vertical = 10 }
                                },
                                new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Vertical,
                                    Spacing = new Vector2(0, spacing),
                                    Children = new Drawable[]
                                    {
                                        topScoresContainer = new FillFlowContainer
                                        {
                                            RelativeSizeAxes = Axes.X,
                                            AutoSizeAxes = Axes.Y,
                                            Direction = FillDirection.Vertical,
                                            Spacing = new Vector2(0, 5),
                                        },
                                        scoreTable = new ScoreTable
                                        {
                                            Anchor = Anchor.TopCentre,
                                            Origin = Anchor.TopCentre,
                                        }
                                    }
                                },
                            }
                        }
                    },
                },
                loading = new LoadingLayer()
            });
        }

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider)
        {
            background.Colour = colourProvider.Background5;

            user.BindTo(api.LocalUser);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Beatmap.BindValueChanged(_ => getScores());
            user.BindValueChanged(_ => getScores(), true);
        }

        private void getScores()
        {
            getScoresRequest?.Cancel();
            getScoresRequest = null;

            noScoresPlaceholder.Hide();

            if (Beatmap.Value == null || Beatmap.Value.OnlineID <= 0 || (Beatmap.Value.Status <= BeatmapOnlineStatus.Pending))
            {
                Scores = null;
                Hide();
                return;
            }

            Show();
            loading.Show();

            getScoresRequest = new GetScoresRequest(Beatmap.Value, Beatmap.Value.Ruleset);
            getScoresRequest.Success += scores =>
            {
                Scores = scores;

                if (!scores.Scores.Any())
                    noScoresPlaceholder.ShowWithScope(BeatmapLeaderboardScope.Global);
            };

            api.Queue(getScoresRequest);
        }
    }
}
