// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using typebeat.Game.Graphics;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Users.Drawables
{
    [LongRunningLoad]
    public partial class DrawableAvatar : Sprite
    {
        private readonly IUser user;

        /// <summary>
        /// A simple, non-interactable avatar sprite for the specified user.
        /// </summary>
        /// <param name="user">The user. A null value will get a placeholder avatar.</param>
        public DrawableAvatar(IUser user = null)
        {
            this.user = user;

            RelativeSizeAxes = Axes.Both;
            FillMode = FillMode.Fit;
            Anchor = Anchor.Centre;
            Origin = Anchor.Centre;
        }

        [BackgroundDependencyLoader]
        private void load(LargeTextureStore textures, OnlineAssetCachingStore onlineTextures)
        {
            // Only a server-provided avatar_url is ever fetched. The upstream fallback built a
            // ppy CDN URL from the user id, which our TrustedDomainOnlineStore rightly blocks
            // (with a user-visible notification) — a payload missing avatar_url shows the local
            // guest avatar instead of generating doomed cross-domain lookups.
            // The disk cache is safe here because the server gives an avatar a new URL whenever it
            // changes, so a cached file never hides a newer avatar.
            if (user != null && user.OnlineID > 1 && (user as APIUser)?.AvatarUrl is string avatarUrl)
                Texture = onlineTextures.Get(avatarUrl);

            Texture ??= textures.Get(@"Online/avatar-guest");
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            this.FadeInFromZero(300, Easing.OutQuint);
        }
    }
}
