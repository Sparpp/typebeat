// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Localisation;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Overlays.Profile.Sections.Historical;
using typebeat.Game.Overlays.Profile.Sections.Ranks;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.Profile.Sections
{
    public partial class HistoricalSection : ProfileSection
    {
        public override LocalisableString Title => UsersStrings.ShowExtraHistoricalTitle;

        public override string Identifier => @"historical";

        public HistoricalSection()
        {
            Children = new Drawable[]
            {
                new PlayHistorySubsection(User),
                new PaginatedMostPlayedBeatmapContainer(User),
                new PaginatedScoreContainer(ScoreType.Recent, User, UsersStrings.ShowExtraHistoricalRecentPlaysTitle),
                new ReplaysSubsection(User)
            };
        }
    }
}
