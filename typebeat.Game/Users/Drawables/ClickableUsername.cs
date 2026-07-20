// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Online;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Chat;

namespace typebeat.Game.Users.Drawables
{
    internal partial class ClickableUsername : OsuHoverContainer, IHasCustomTooltip<APIUser>
    {
        public ITooltip<APIUser?> GetCustomTooltip() => new ClickableAvatar.NoCardTooltip();

        public APIUser? TooltipContent { get; }

        private readonly APIUser user;

        [Resolved]
        private ILinkHandler? linkHandler { get; set; }

        public ClickableUsername(APIUser? user, FontUsage? font = null)
        {
            TooltipContent = this.user = user ?? new GuestUser();

            AutoSizeAxes = Axes.Both;

            Child = new OsuSpriteText
            {
                Text = this.user.Username,
                Font = font ?? OsuFont.Torus.With(size: 16, weight: FontWeight.SemiBold),
            };
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            if (user.Id != APIUser.SYSTEM_USER_ID)
                Action = () => linkHandler?.HandleLink(new LinkDetails(LinkAction.OpenUserProfile, user));
        }
    }
}
