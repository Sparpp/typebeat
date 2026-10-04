// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Extensions.LocalisationExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Resources.Localisation.Web;
using typebeat.Game.Scoring;
using typebeat.Game.Users;
using typebeat.Game.Utils;

namespace typebeat.Game.Overlays.Profile.Header.Components
{
    public partial class GlobalRankDisplay : CompositeDrawable
    {
        public Bindable<UserStatistics?> UserStatistics = new Bindable<UserStatistics?>();

        private ProfileValueDisplay info = null!;

        [Resolved]
        private OverlayColourProvider colourProvider { get; set; } = null!;

        public GlobalRankDisplay()
        {
            AutoSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChild = info = new ProfileValueDisplay(big: true)
            {
                Title = UsersStrings.ShowRankGlobalSimple
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            UserStatistics.BindValueChanged(_ => updateState(), true);
        }

        /// <remarks>
        /// No tooltip: lazer's lists the highest rank ever held and the per-variant ranks, and the server keeps neither a
        /// rank history nor ruleset variants. The tier colour reads <c>global_rank_percent</c>, served off the pp board.
        /// </remarks>
        private void updateState()
        {
            info.Content.Text = UserStatistics.Value?.GlobalRank?.ToLocalisableString("\\##,##0") ?? (LocalisableString)"-";

            var tier = getRankingTier();
            info.Content.Colour = tier == null ? colourProvider.Content2 : OsuColour.ForRankingTier(tier.Value);
            info.Content.Font = info.Content.Font.With(weight: tier == null || tier == RankingTier.Iron ? FontWeight.Regular : FontWeight.Bold);
        }

        /// <seealso href="https://github.com/ppy/osu-web/blob/6fcd85eb006ce7699d6f747597435c01344b2d2d/resources/js/profile-page/rank.tsx#L19-L46"/>
        private RankingTier? getRankingTier()
        {
            var stats = UserStatistics.Value;

            int? rank = stats?.GlobalRank;
            float? percent = stats?.GlobalRankPercent;

            if (rank == null || percent == null)
                return null;

            if (rank <= 100)
                return RankingTier.Lustrous;

            if (percent < 0.0005)
                return RankingTier.Radiant;

            if (percent < 0.0015)
                return RankingTier.Rhodium;

            if (percent < 0.005)
                return RankingTier.Platinum;

            if (percent < 0.015)
                return RankingTier.Gold;

            if (percent < 0.05)
                return RankingTier.Silver;

            if (percent < 0.15)
                return RankingTier.Bronze;

            if (percent < 0.5)
                return RankingTier.Iron;

            return null;
        }

    }
}
