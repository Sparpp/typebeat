// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Chat;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the typebeat:// scheme rename: OS-registered protocol links (Program.cs → IPC → HandleLink)
    /// and website links must parse into the intended <see cref="LinkAction"/>s.
    /// </summary>
    [TestFixture]
    public class MessageFormatterSchemeTest
    {
        [SetUp]
        public void SetUp() => MessageFormatter.WebsiteRootUrl = "https://typebeat.mingda.sh";

        [Test]
        public void GameProtocolIsTypebeatScheme()
        {
            Assert.That(OsuGameBase.OSU_PROTOCOL, Is.EqualTo("typebeat://"));
        }

        [TestCase("typebeat://b/252238", LinkAction.OpenBeatmap, "252238")]
        [TestCase("typebeat://s/2626", LinkAction.OpenBeatmapSet, "2626")]
        [TestCase("typebeat://dl/2626", LinkAction.OpenBeatmapSet, "2626")]
        [TestCase("typebeat://chan/#lobby", LinkAction.OpenChannel, "#lobby")]
        public void TypebeatSchemeLinkParses(string url, LinkAction expectedAction, string expectedArgument)
        {
            var details = MessageFormatter.GetLinkDetails(url);

            Assert.That(details.Action, Is.EqualTo(expectedAction));
            Assert.That(details.Argument, Is.EqualTo(expectedArgument));
        }

        [Test]
        public void TypebeatSchemeUserIdLinkParses()
        {
            var details = MessageFormatter.GetLinkDetails("typebeat://u/42");

            Assert.That(details.Action, Is.EqualTo(LinkAction.OpenUserProfile));
            Assert.That(details.Argument, Is.InstanceOf<APIUser>());
            Assert.That(((APIUser)details.Argument).Id, Is.EqualTo(42));
        }

        [Test]
        public void TypebeatSchemeUsernameLinkParses()
        {
            var details = MessageFormatter.GetLinkDetails("typebeat://u/mingda");

            Assert.That(details.Action, Is.EqualTo(LinkAction.OpenUserProfile));
            Assert.That(details.Argument, Is.InstanceOf<APIUser>());
            Assert.That(((APIUser)details.Argument).Username, Is.EqualTo("mingda"));
        }

        [Test]
        public void LegacyOsuSchemeIsNoLongerOurs()
        {
            Assert.That(MessageFormatter.GetLinkDetails("osu://s/2626").Action, Is.EqualTo(LinkAction.External));
        }

        [TestCase("https://typebeat.mingda.sh/beatmapsets/2626", LinkAction.OpenBeatmapSet, "2626")]
        [TestCase("https://typebeat.mingda.sh/beatmaps/252238", LinkAction.OpenBeatmap, "252238")]
        [TestCase("https://typebeat.mingda.sh/wiki/Welcome", LinkAction.OpenWiki, "Welcome")]
        public void WebsiteLinkParses(string url, LinkAction expectedAction, string expectedArgument)
        {
            var details = MessageFormatter.GetLinkDetails(url);

            Assert.That(details.Action, Is.EqualTo(expectedAction));
            Assert.That(details.Argument, Is.EqualTo(expectedArgument));
        }

        [Test]
        public void WebsiteUserLinkParses()
        {
            var details = MessageFormatter.GetLinkDetails("https://typebeat.mingda.sh/users/42");

            Assert.That(details.Action, Is.EqualTo(LinkAction.OpenUserProfile));
            Assert.That(details.Argument, Is.InstanceOf<APIUser>());
            Assert.That(((APIUser)details.Argument).Id, Is.EqualTo(42));
        }
    }
}
