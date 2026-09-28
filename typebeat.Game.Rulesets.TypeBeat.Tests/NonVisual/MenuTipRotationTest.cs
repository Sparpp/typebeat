// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Screens.Menu;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The menu tip rotation is data now, so adding a tip is appending an entry. These are the rules
    /// every entry has to keep for that to stay a one line change.
    /// </summary>
    [TestFixture]
    public class MenuTipRotationTest
    {
        [Test]
        public void EveryTipInTheRotationIsWellFormed()
        {
            Assert.That(MenuTipDisplay.Tips, Is.Not.Empty);

            for (int i = 0; i < MenuTipDisplay.Tips.Count; i++)
            {
                var tip = MenuTipDisplay.Tips[i];

                Assert.That(tip.Text.ToString(), Is.Not.Empty, $"tip {i} has no text");
                Assert.That(tip.Icon.Icon, Is.Not.EqualTo('\0'), $"tip {i} has no icon");

                // A tip that goes somewhere goes somewhere absolute and secure: the URL is handed
                // straight to the external link handler.
                if (tip.Url != null)
                    Assert.That(tip.Url, Does.StartWith("https://"), $"tip {i} has a URL that is not an absolute https one");
            }
        }

        [Test]
        public void TheDiscordTipIsTheInvite()
        {
            Assert.That(MenuTipDisplay.DISCORD_URL, Is.EqualTo("https://discord.gg/yAR2PDPgBB"));

            var discord = MenuTipDisplay.Tips.Single(tip => tip.Url == MenuTipDisplay.DISCORD_URL);

            Assert.That(discord.Text.ToString(), Is.EqualTo("Join the type!beat Discord"));
            Assert.That(discord.Icon.Icon, Is.EqualTo(FontAwesome.Brands.Discord.Icon));
            Assert.That(discord.Icon.Family, Is.EqualTo(FontAwesome.Brands.Discord.Family));
        }

        /// <summary>
        /// The tip this replaced advertised osu! from the type!beat main menu. It is not coming back.
        /// </summary>
        [Test]
        public void NoTipAdvertisesOsu()
        {
            Assert.That(MenuTipDisplay.Tips.Select(tip => tip.Text.ToString()), Has.None.Contains("osu!"));
        }
    }
}
