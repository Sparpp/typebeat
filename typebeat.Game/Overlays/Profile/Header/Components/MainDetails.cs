// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Extensions.LocalisationExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Leaderboards;
using typebeat.Game.Resources.Localisation.Web;
using typebeat.Game.Scoring;
using osuTK;

namespace typebeat.Game.Overlays.Profile.Header.Components
{
    public partial class MainDetails : CompositeDrawable
    {
        public const float BADGE_HEIGHT = 36;

        private readonly Dictionary<ScoreRank, ScoreRankInfo> scoreRankInfos = new Dictionary<ScoreRank, ScoreRankInfo>();
        private ProfileValueDisplay ppInfo = null!;
        private GlobalRankDisplay detailGlobalRank = null!;
        private ProfileValueDisplay detailCountryRank = null!;

        public readonly Bindable<UserProfileData?> User = new Bindable<UserProfileData?>();

        [BackgroundDependencyLoader]
        private void load()
        {
            AutoSizeAxes = Axes.Y;

            InternalChild = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                AutoSizeDuration = 200,
                AutoSizeEasing = Easing.OutQuint,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(0, 15),
                Children = new Drawable[]
                {
                    new GridContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        ColumnDimensions = new[]
                        {
                            new Dimension(GridSizeMode.AutoSize),
                            new Dimension(GridSizeMode.Absolute, 20),
                            new Dimension(),
                            new Dimension(GridSizeMode.AutoSize),
                        },
                        RowDimensions = new[]
                        {
                            new Dimension(GridSizeMode.AutoSize),
                        },
                        Content = new[]
                        {
                            new[]
                            {
                                detailGlobalRank = new GlobalRankDisplay(),
                                Empty(),
                                detailCountryRank = new ProfileValueDisplay(true)
                                {
                                    Title = UsersStrings.ShowRankCountrySimple,
                                },
                                // No matchmaking or daily challenge stats: neither mode exists here.
                                Empty(),
                            }
                        }
                    },
                    // No rank graph: the server keeps no rank history (no rank_history on the wire).
                    new Container
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Children = new Drawable[]
                        {
                            new FillFlowContainer
                            {
                                AutoSizeAxes = Axes.Both,
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Direction = FillDirection.Horizontal,
                                Spacing = new Vector2(10, 0),
                                // No medals count: there are no medals.
                                Children = new Drawable[]
                                {
                                    ppInfo = new ProfileValueDisplay
                                    {
                                        Title = "pp",
                                    },
                                    new TotalPlayTime
                                    {
                                        User = { BindTarget = User }
                                    },
                                }
                            },
                            new FillFlowContainer
                            {
                                AutoSizeAxes = Axes.Both,
                                Anchor = Anchor.CentreRight,
                                Origin = Anchor.CentreRight,
                                Direction = FillDirection.Horizontal,
                                Spacing = new Vector2(5),
                                // The server's grade counts fold the silver grades into SS and S (grade_counts carries
                                // ss/s/a only), so XH and SH would always read 0.
                                Children = new[]
                                {
                                    scoreRankInfos[ScoreRank.X] = new ScoreRankInfo(ScoreRank.X),
                                    scoreRankInfos[ScoreRank.S] = new ScoreRankInfo(ScoreRank.S),
                                    scoreRankInfos[ScoreRank.A] = new ScoreRankInfo(ScoreRank.A),
                                }
                            }
                        }
                    },
                }
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            User.BindValueChanged(e => updateDisplay(e.NewValue), true);
        }

        private void updateDisplay(UserProfileData? data)
        {
            var user = data?.User;

            ppInfo.Content.Text = user?.Statistics?.PP?.ToLocalisableString("#,##0") ?? (LocalisableString)"0";

            foreach (var scoreRankInfo in scoreRankInfos)
                scoreRankInfo.Value.RankCount = user?.Statistics?.GradesCount[scoreRankInfo.Key] ?? 0;

            detailGlobalRank.UserStatistics.Value = user?.Statistics;

            // No per-variant tooltips on the pp or country rank: ruleset variants (osu!mania's key counts) do not exist here.
            detailCountryRank.Content.Text = user?.Statistics?.CountryRank?.ToLocalisableString("\\##,##0") ?? (LocalisableString)"-";
        }

        private partial class ScoreRankInfo : CompositeDrawable
        {
            private readonly OsuSpriteText rankCount;

            public int RankCount
            {
                set => rankCount.Text = value.ToLocalisableString("#,##0");
            }

            public ScoreRankInfo(ScoreRank rank)
            {
                AutoSizeAxes = Axes.Both;
                InternalChild = new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Y,
                    Width = 44,
                    Direction = FillDirection.Vertical,
                    Children = new Drawable[]
                    {
                        new DrawableRank(rank)
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 22,
                        },
                        rankCount = new OsuSpriteText
                        {
                            Font = OsuFont.GetFont(size: 12, weight: FontWeight.Bold),
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre
                        }
                    }
                };
            }
        }
    }
}
