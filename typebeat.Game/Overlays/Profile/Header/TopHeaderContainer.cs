// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Online.API;
using typebeat.Game.Overlays.Profile.Header.Components;
using typebeat.Game.Users;
using typebeat.Game.Users.Drawables;
using osuTK;

namespace typebeat.Game.Overlays.Profile.Header
{
    public partial class TopHeaderContainer : CompositeDrawable
    {
        private const float content_height = 65;
        private const float vertical_padding = 10;

        public readonly Bindable<UserProfileData?> User = new Bindable<UserProfileData?>();

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        private UserCoverBackground cover = null!;
        private UpdateableAvatar avatar = null!;
        private OsuSpriteText usernameText = null!;
        private ExternalLinkButton openUserExternally = null!;
        private UpdateableFlag userFlag = null!;
        private OsuSpriteText userCountryText = null!;
        private ToggleCoverButton coverToggle = null!;

        private Bindable<bool> coverExpanded = null!;

        private FillFlowContainer flow = null!;

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider, OsuConfigManager configManager)
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;

            coverExpanded = configManager.GetBindable<bool>(OsuSetting.ProfileCoverExpanded);

            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = colourProvider.Background3,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Children = new Drawable[]
                    {
                        cover = new ProfileCoverBackground
                        {
                            RelativeSizeAxes = Axes.X,
                        },
                        new Container
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Children = new Drawable[]
                            {
                                flow = new FillFlowContainer
                                {
                                    Direction = FillDirection.Horizontal,
                                    Padding = new MarginPadding
                                    {
                                        Left = WaveOverlayContainer.HORIZONTAL_PADDING,
                                        Vertical = vertical_padding
                                    },
                                    Height = content_height + 2 * vertical_padding,
                                    RelativeSizeAxes = Axes.X,
                                    Children = new Drawable[]
                                    {
                                        avatar = new UpdateableAvatar(isInteractive: false, showGuestOnNull: false)
                                        {
                                            Anchor = Anchor.BottomLeft,
                                            Origin = Anchor.BottomLeft,
                                            Masking = true,
                                            EdgeEffect = new EdgeEffectParameters
                                            {
                                                Type = EdgeEffectType.Shadow,
                                                Offset = new Vector2(0, 1),
                                                Radius = 3,
                                                Colour = Colour4.Black.Opacity(0.25f),
                                            }
                                        },
                                        new FillFlowContainer
                                        {
                                            AutoSizeAxes = Axes.Both,
                                            Direction = FillDirection.Vertical,
                                            Anchor = Anchor.CentreLeft,
                                            Origin = Anchor.CentreLeft,
                                            Children = new Drawable[]
                                            {
                                                new FillFlowContainer
                                                {
                                                    AutoSizeAxes = Axes.Both,
                                                    Direction = FillDirection.Horizontal,
                                                    Spacing = new Vector2(5, 0),
                                                    Children = new Drawable[]
                                                    {
                                                        usernameText = new OsuSpriteText
                                                        {
                                                            Font = OsuFont.GetFont(size: 24, weight: FontWeight.Regular)
                                                        },
                                                        // No supporter tag, group badges or previous usernames: none are kept server-side.
                                                        openUserExternally = new ExternalLinkButton
                                                        {
                                                            Anchor = Anchor.CentreLeft,
                                                            Origin = Anchor.CentreLeft,
                                                        },
                                                    }
                                                },
                                                // No user title and no team: neither exists here.
                                                new FillFlowContainer
                                                {
                                                    Margin = new MarginPadding { Top = 3 },
                                                    AutoSizeAxes = Axes.Both,
                                                    Direction = FillDirection.Horizontal,
                                                    Spacing = new Vector2(4, 0),
                                                    Children = new Drawable[]
                                                    {
                                                        userFlag = new UpdateableFlag
                                                        {
                                                            Size = new Vector2(28, 20),
                                                        },
                                                        // Plain text rather than lazer's link to the country rankings, which the server does not serve.
                                                        userCountryText = new OsuSpriteText
                                                        {
                                                            Anchor = Anchor.CentreLeft,
                                                            Origin = Anchor.CentreLeft,
                                                            Font = OsuFont.GetFont(size: 14f, weight: FontWeight.Regular),
                                                        },
                                                    }
                                                },
                                            }
                                        },
                                    }
                                },
                                coverToggle = new ToggleCoverButton
                                {
                                    Anchor = Anchor.CentreRight,
                                    Origin = Anchor.CentreRight,
                                    Margin = new MarginPadding { Right = 10 },
                                    CoverExpanded = { BindTarget = coverExpanded }
                                }
                            },
                        },
                    },
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            User.BindValueChanged(user => updateUser(user.NewValue), true);
            coverExpanded.BindValueChanged(_ => updateCoverState(), true);
            FinishTransforms(true);
        }

        private void updateUser(UserProfileData? data)
        {
            var user = data?.User;

            cover.User = user;
            avatar.User = user;
            usernameText.Text = user?.Username ?? string.Empty;
            openUserExternally.Link = $@"{api.Endpoints.WebsiteUrl}/users/{user?.Id ?? 0}";
            userFlag.CountryCode = user?.CountryCode ?? default;
            userCountryText.Text = (user?.CountryCode ?? default).GetDescription();
        }

        private void updateCoverState()
        {
            const float transition_duration = 500;

            bool expanded = coverToggle.CoverExpanded.Value;

            cover.ResizeHeightTo(expanded ? 250 : 0, transition_duration, Easing.OutQuint);

            // Without this a very tiny slither of the cover will be visible even with a size of zero.
            // Integer masking woes, no doubt.
            if (expanded)
                cover.FadeIn(transition_duration, Easing.OutQuint);
            else
                cover.FadeOut(transition_duration, Easing.InQuint);

            avatar.ResizeTo(new Vector2(expanded ? 120 : content_height), transition_duration, Easing.OutQuint);
            avatar.TransformTo(nameof(avatar.CornerRadius), expanded ? 40f : 20f, transition_duration, Easing.OutQuint);
            flow.TransformTo(nameof(flow.Spacing), new Vector2(expanded ? 20f : 10f), transition_duration, Easing.OutQuint);
        }

        private partial class ProfileCoverBackground : UserCoverBackground
        {
            protected override double LoadDelay => 0;

            public ProfileCoverBackground()
            {
                Masking = true;
            }
        }
    }
}
