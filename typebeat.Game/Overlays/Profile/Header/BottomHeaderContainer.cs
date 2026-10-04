// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Humanizer;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Resources.Localisation.Web;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Overlays.Profile.Header
{
    public partial class BottomHeaderContainer : CompositeDrawable
    {
        public readonly Bindable<UserProfileData?> User = new Bindable<UserProfileData?>();

        private LinkFlowContainer topLinkContainer = null!;

        public BottomHeaderContainer()
        {
            AutoSizeAxes = Axes.Y;
        }

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider)
        {
            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = colourProvider.Background4
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Padding = new MarginPadding { Horizontal = WaveOverlayContainer.HORIZONTAL_PADDING, Vertical = 10 },
                    Spacing = new Vector2(0, 10),
                    Children = new Drawable[]
                    {
                        topLinkContainer = new LinkFlowContainer(text => text.Font = text.Font.With(size: 12))
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                        },
                    }
                }
            };

            User.BindValueChanged(user => updateDisplay(user.NewValue?.User));
        }

        private void updateDisplay(APIUser? user)
        {
            topLinkContainer.Clear();

            if (user == null) return;

            if (user.JoinDate.ToUniversalTime().Year < 2008)
                topLinkContainer.AddText(UsersStrings.ShowFirstMembers);
            else
            {
                topLinkContainer.AddText("Joined ");
                topLinkContainer.AddText(new DrawableDate(user.JoinDate, italic: false), embolden);
            }

            addSpacer(topLinkContainer);

            if (user.WasRecentlyOnline)
            {
                topLinkContainer.AddText(UsersStrings.ShowLastvisitOnline);
                addSpacer(topLinkContainer);
            }
            else if (user.LastVisit.HasValue)
            {
                topLinkContainer.AddText("Last seen ");
                topLinkContainer.AddText(new DrawableDate(user.LastVisit.Value, italic: false), embolden);
            }

            // Join date and last visit are all this row has data for: the server keeps no play styles, forum posts,
            // location, interests, occupation or social links, and the comment count's lazer link has no page.
        }

        private void addSpacer(OsuTextFlowContainer textFlow) => textFlow.AddArbitraryDrawable(new Container { Width = 15 });

        private void embolden(SpriteText text) => text.Font = text.Font.With(weight: FontWeight.Bold);
    }
}
