// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Testing;
using osuTK;
using osuTK.Graphics;
using typebeat.Game.Configuration;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Tests.Visual;
using typebeat.Game.Users;
using typebeat.Game.Users.Drawables;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The avatar-with-flag badge (backlog 333): the country the server now really sends, badged in
    /// the avatar's bottom-right corner at a third of its height, and NOTHING at all for an unknown
    /// country (the '?' tile is only for places where a flag tile is the point). Asserted
    /// geometrically on the drawn quads, so the layout is pinned without anyone looking at it.
    /// </summary>
    [TestFixture]
    public partial class TestSceneAvatarWithFlag : OsuTestScene
    {
        private const float avatar_size = 90;

        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        private AvatarWithFlag avatar = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("show flags", () => config.SetValue(OsuSetting.HideCountryFlags, false));
        }

        private void createAvatar(CountryCode country) => AddStep($"create avatar ({country})", () => Child = avatar = new AvatarWithFlag(
            new Box { Colour = Color4.SlateGray }, country)
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            Size = new Vector2(avatar_size),
        });

        [Test]
        public void TestRealCountryIsBadgedBottomRightAtAThird()
        {
            createAvatar(CountryCode.JP);
            AddUntilStep("loaded", () => avatar.IsLoaded);

            AddAssert("badge shows", () => avatar.BadgeVisible);
            AddAssert("the flag is Japan's", () => avatar.Flag.CountryCode, () => Is.EqualTo(CountryCode.JP));

            AddAssert("badge is a third of the avatar's height", () => badgeRect().Height / avatarRect().Height,
                () => Is.EqualTo(AvatarWithFlag.FLAG_HEIGHT_RATIO).Within(0.01f));
            AddAssert("badge keeps the flag aspect", () => badgeRect().Width / badgeRect().Height,
                () => Is.EqualTo(AvatarWithFlag.FLAG_ASPECT).Within(0.01f));

            AddAssert("badge lies inside the avatar", () => avatarRect().Contains(badgeRect()));
            AddAssert("badge sits in the bottom-right corner", () =>
            {
                var a = avatarRect();
                var b = badgeRect();
                return b.Centre.X > a.Centre.X && b.Top > a.Centre.Y;
            });
            AddAssert("inset from the right edge by the inset ratio", () => avatarRect().Right - badgeRect().Right,
                () => Is.EqualTo(AvatarWithFlag.FLAG_INSET_RATIO * avatarRect().Width).Within(0.5f));
            AddAssert("inset from the bottom edge by the inset ratio", () => avatarRect().Bottom - badgeRect().Bottom,
                () => Is.EqualTo(AvatarWithFlag.FLAG_INSET_RATIO * avatarRect().Height).Within(0.5f));
        }

        [Test]
        public void TestUnknownCountryShowsNoBadgeAndNoQuestionMark()
        {
            createAvatar(CountryCode.Unknown);
            AddUntilStep("loaded", () => avatar.IsLoaded);

            AddAssert("badge hidden", () => avatar.BadgeVisible, () => Is.False);
            AddAssert("nothing of the flag is drawn", () => avatar.Badge.IsPresent, () => Is.False);
        }

        [Test]
        public void TestCountryArrivingLaterShowsTheBadge()
        {
            createAvatar(CountryCode.Unknown);
            AddUntilStep("loaded", () => avatar.IsLoaded);
            AddAssert("badge hidden", () => avatar.BadgeVisible, () => Is.False);

            AddStep("country becomes Kenya", () => avatar.CountryCode = CountryCode.KE);
            AddAssert("badge shows", () => avatar.BadgeVisible);
            AddAssert("the flag is Kenya's", () => avatar.Flag.CountryCode, () => Is.EqualTo(CountryCode.KE));

            AddStep("country cleared", () => avatar.CountryCode = CountryCode.Unknown);
            AddAssert("badge hidden again", () => avatar.BadgeVisible, () => Is.False);
        }

        [Test]
        public void TestHideCountryFlagsSettingHidesTheBadge()
        {
            createAvatar(CountryCode.BR);
            AddUntilStep("loaded", () => avatar.IsLoaded);
            AddAssert("badge shows", () => avatar.BadgeVisible);

            AddStep("hide flags", () => config.SetValue(OsuSetting.HideCountryFlags, true));
            AddAssert("badge hidden rather than a '?' tile", () => avatar.BadgeVisible, () => Is.False);

            AddStep("show flags", () => config.SetValue(OsuSetting.HideCountryFlags, false));
            AddAssert("badge back", () => avatar.BadgeVisible);
        }

        [Test]
        public void TestListPanelBadgesTheAvatarInsteadOfAStandaloneFlag()
        {
            UserListPanel panel = null!;

            AddStep("create list panel", () => Child = panel = new UserListPanel(new APIUser
            {
                Id = 3,
                Username = "flagged",
                CountryCode = CountryCode.JP,
            })
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Width = 300,
            });
            AddUntilStep("loaded", () => panel.IsLoaded);

            AddAssert("one badged avatar, showing", () => panel.ChildrenOfType<AvatarWithFlag>().Single().BadgeVisible);
            AddAssert("no flag outside the badge", () => panel.ChildrenOfType<UpdateableFlag>().Count(), () => Is.EqualTo(1));
        }

        private RectangleF avatarRect() => avatar.Avatar.ScreenSpaceDrawQuad.AABBFloat;

        private RectangleF badgeRect() => avatar.Badge.ScreenSpaceDrawQuad.AABBFloat;
    }
}
