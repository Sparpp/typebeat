// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Localisation;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.Dashboard
{
    public partial class DashboardOverlayHeader : TabControlOverlayHeader<DashboardOverlayTabs>
    {
        protected override OverlayTitle CreateTitle() => new DashboardTitle();

        private partial class DashboardTitle : OverlayTitle
        {
            public DashboardTitle()
            {
                Title = PageTitleStrings.MainHomeControllerIndex;
                Description = NamedOverlayComponentStrings.DashboardDescription;
                Icon = OsuIcon.Global;
            }
        }
    }

    public enum DashboardOverlayTabs
    {
        [LocalisableDescription(typeof(FriendsStrings), nameof(FriendsStrings.TitleCompact))]
        Friends,

        [LocalisableDescription(typeof(UserInterfaceStrings), nameof(UserInterfaceStrings.UserSearch))]
        UserSearch
    }
}
