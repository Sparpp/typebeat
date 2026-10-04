// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Overlays.Profile.Header;
using typebeat.Game.Overlays.Profile.Header.Components;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.Profile
{
    public partial class ProfileHeader : TabControlOverlayHeader<LocalisableString>
    {
        public Bindable<UserProfileData?> User = new Bindable<UserProfileData?>();

        private CentreHeaderContainer centreHeaderContainer;
        private DetailHeaderContainer detailHeaderContainer;

        public ProfileHeader()
        {
            ContentSidePadding = WaveOverlayContainer.HORIZONTAL_PADDING;

            TabControl.AddItem(LayoutStrings.HeaderUsersShow);

            // todo: pending implementation.
            // TabControl.AddItem(LayoutStrings.HeaderUsersModding);

            // Haphazardly guaranteed by OverlayHeader constructor (see CreateBackground / CreateContent).
            Debug.Assert(centreHeaderContainer != null);
            Debug.Assert(detailHeaderContainer != null);
        }

        protected override Drawable CreateBackground() => Empty();

        protected override Drawable CreateContent() => new FillFlowContainer
        {
            RelativeSizeAxes = Axes.X,
            AutoSizeAxes = Axes.Y,
            Direction = FillDirection.Vertical,
            Children = new Drawable[]
            {
                new TopHeaderContainer
                {
                    RelativeSizeAxes = Axes.X,
                    User = { BindTarget = User },
                },
                // No tournament banners, badges or score-processing notice: the server keeps no banners or badges,
                // and prices every play inside its submission, so there is never a processing backlog to announce.
                detailHeaderContainer = new DetailHeaderContainer
                {
                    RelativeSizeAxes = Axes.X,
                    User = { BindTarget = User },
                },
                centreHeaderContainer = new CentreHeaderContainer
                {
                    RelativeSizeAxes = Axes.X,
                    User = { BindTarget = User },
                },
                new BottomHeaderContainer
                {
                    RelativeSizeAxes = Axes.X,
                    User = { BindTarget = User },
                },
            }
        };

        protected override OverlayTitle CreateTitle() => new ProfileHeaderTitle();

        // No ruleset selector: type!beat has the one ruleset.
        protected override Drawable CreateTabControlContent() => Empty();

        private partial class ProfileHeaderTitle : OverlayTitle
        {
            public ProfileHeaderTitle()
            {
                Title = PageTitleStrings.MainUsersControllerDefault;
                Icon = OsuIcon.Player;
            }
        }
    }
}
