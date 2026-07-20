// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Input.Events;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Cursor;
using typebeat.Game.Localisation;
using typebeat.Game.Online;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Chat;
using osuTK;

namespace typebeat.Game.Users.Drawables
{
    public partial class ClickableAvatar : OsuClickableContainer, IHasCustomTooltip<APIUser?>
    {
        public ITooltip<APIUser?> GetCustomTooltip() => showCardOnHover ? new UserCardTooltip() : new NoCardTooltip();

        public APIUser? TooltipContent { get; }

        private readonly APIUser? user;

        private readonly bool showCardOnHover;

        [Resolved]
        private ILinkHandler? linkHandler { get; set; }

        /// <summary>
        /// A clickable avatar for the specified user, with UI sounds included.
        /// Clicking opens the user's profile on the website.
        /// </summary>
        /// <param name="user">The user. A null value will get a placeholder avatar.</param>
        /// <param name="showCardOnHover">If set to true, the <see cref="UserGridPanel"/> will be shown for the tooltip</param>
        public ClickableAvatar(APIUser? user = null, bool showCardOnHover = false)
        {
            this.showCardOnHover = showCardOnHover;

            TooltipContent = this.user = user ?? new GuestUser();
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            if (user?.Id != APIUser.SYSTEM_USER_ID)
                Action = () => linkHandler?.HandleLink(new LinkDetails(LinkAction.OpenUserProfile, user!));

            LoadComponentAsync(new DrawableAvatar(user), Add);
        }

        protected override bool OnClick(ClickEvent e)
        {
            if (!Enabled.Value)
                return false;

            return base.OnClick(e);
        }

        public partial class NoCardTooltip : VisibilityContainer, ITooltip<APIUser?>
        {
            private readonly OsuTooltipContainer.OsuTooltip tooltip;

            public NoCardTooltip()
            {
                tooltip = new OsuTooltipContainer.OsuTooltip();
                tooltip.SetContent(ContextMenuStrings.ViewProfile);
                Child = tooltip;
            }

            protected override void PopIn() => tooltip.Show();
            protected override void PopOut() => tooltip.Hide();

            public void Move(Vector2 pos) => Position = pos;

            public void SetContent(APIUser? content)
            {
            }
        }
    }
}
