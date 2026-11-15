// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osuTK;

namespace typebeat.Game.Overlays.Dashboard.Friends
{
    public partial class UserListToolbar : CompositeDrawable
    {
        public Bindable<UserSortCriteria> SortCriteria => sortControl.Current;

        public Bindable<OverlayPanelDisplayStyle> DisplayStyle => styleControl.Current;

        private readonly UserSortTabControl sortControl;
        private readonly OverlayPanelDisplayStyleControl styleControl;

        public UserListToolbar(bool supportsBrickMode = true, bool supportsSort = true)
        {
            AutoSizeAxes = Axes.Both;

            AddInternal(new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(10, 0),
                Children = new Drawable[]
                {
                    sortControl = new UserSortTabControl
                    {
                        Alpha = supportsSort ? 1 : 0,
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                    },
                    styleControl = new OverlayPanelDisplayStyleControl(supportsBrickMode)
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                    }
                }
            });
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // The sort and the display style used to persist (DashboardSortMode, DashboardDisplayStyle,
            // removed in backlog 483); every toolbar now starts on what they defaulted to.
            SortCriteria.Value = UserSortCriteria.LastVisit;
            DisplayStyle.Value = OverlayPanelDisplayStyle.Card;
        }
    }
}
