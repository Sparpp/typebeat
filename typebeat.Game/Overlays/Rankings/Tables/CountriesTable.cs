// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using System;
using typebeat.Game.Users;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Extensions.LocalisationExtensions;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.Rankings.Tables
{
    public partial class CountriesTable : RankingsTable<CountryStatistics>
    {
        public CountriesTable(int page, IReadOnlyList<CountryStatistics> rankings)
            : base(page, rankings)
        {
        }

        protected override RankingsTableColumn[] CreateAdditionalHeaders() => new[]
        {
            new RankingsTableColumn(RankingsStrings.StatActiveUsers, Anchor.Centre, new Dimension(GridSizeMode.AutoSize)),
            new RankingsTableColumn(RankingsStrings.StatPlayCount, Anchor.Centre, new Dimension(GridSizeMode.AutoSize)),
            new RankingsTableColumn(RankingsStrings.StatRankedScore, Anchor.Centre, new Dimension(GridSizeMode.AutoSize)),
            new RankingsTableColumn(RankingsStrings.StatAverageScore, Anchor.Centre, new Dimension(GridSizeMode.AutoSize)),
            new RankingsTableColumn(RankingsStrings.StatPerformance, Anchor.Centre, new Dimension(GridSizeMode.AutoSize), true),
            new RankingsTableColumn(RankingsStrings.StatAveragePerformance, Anchor.Centre, new Dimension(GridSizeMode.AutoSize)),
        };

        protected override CountryCode GetCountryCode(CountryStatistics item) => item.Code;

        protected override Drawable[] CreateFlagContent(CountryStatistics item) => [new CountryName(item.Code)];

        protected override Drawable[] CreateAdditionalContent(CountryStatistics item) => new Drawable[]
        {
            new ColouredRowText
            {
                Text = item.ActiveUsers.ToLocalisableString(@"N0")
            },
            new ColouredRowText
            {
                Text = item.PlayCount.ToLocalisableString(@"N0")
            },
            new ColouredRowText
            {
                Text = item.RankedScore.ToLocalisableString(@"N0")
            },
            new ColouredRowText
            {
                Text = (item.RankedScore / Math.Max(item.ActiveUsers, 1)).ToLocalisableString(@"N0")
            },
            new RowText
            {
                Text = item.Performance.ToLocalisableString(@"N0")
            },
            new ColouredRowText
            {
                Text = (item.Performance / Math.Max(item.ActiveUsers, 1)).ToLocalisableString(@"N0")
            }
        };

        private partial class CountryName : LinkFlowContainer
        {
            [Resolved]
            private RankingsOverlay? rankings { get; set; }

            public CountryName(CountryCode countryCode)
                : base(t => t.Font = OsuFont.GetFont(size: 12))
            {
                AutoSizeAxes = Axes.X;
                RelativeSizeAxes = Axes.Y;
                TextAnchor = Anchor.CentreLeft;

                if (countryCode != CountryCode.Unknown)
                    AddLink(countryCode.GetDescription(), () => rankings?.ShowCountry(countryCode));
            }
        }
    }
}
