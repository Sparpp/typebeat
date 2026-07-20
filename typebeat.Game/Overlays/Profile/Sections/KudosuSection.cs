// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using typebeat.Game.Overlays.Profile.Sections.Kudosu;
using osu.Framework.Localisation;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.Profile.Sections
{
    public partial class KudosuSection : ProfileSection
    {
        public override LocalisableString Title => UsersStrings.ShowExtraKudosuTitle;

        public override string Identifier => @"kudosu";

        public KudosuSection()
        {
            Children = new Drawable[]
            {
                new KudosuInfo(User),
                new PaginatedKudosuHistoryContainer(User),
            };
        }
    }
}
