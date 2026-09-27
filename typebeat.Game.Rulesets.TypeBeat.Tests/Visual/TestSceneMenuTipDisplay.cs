// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osuTK;
using osuTK.Input;
using typebeat.Game.Configuration;
using typebeat.Game.Online;
using typebeat.Game.Online.Chat;
using typebeat.Game.Screens.Menu;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The menu tip is the game's one piece of Discord signage, so what matters is that it is really
    /// clickable: that the link it compiles is the invite (through the external link handler, not a
    /// raw browser call), and that the fade-out which used to take the tip away in a second or two
    /// holds while the player is pointing at it.
    /// </summary>
    [TestFixture]
    public partial class TestSceneMenuTipDisplay : OsuManualInputManagerTestScene
    {
        [Cached(typeof(ILinkHandler))]
        private readonly RecordingLinkHandler linkHandler = new RecordingLinkHandler();

        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        private MenuTipDisplay display = null!;

        private static int discordTipIndex => MenuTipDisplay.Tips
                                                            .Select((tip, index) => (tip, index))
                                                            .Single(entry => entry.tip.Url == MenuTipDisplay.DISCORD_URL).index;

        private bool fadeQueued() => display.Transforms.Any(t => t.TargetMember == nameof(Drawable.Alpha));

        [SetUpSteps]
        public void SetUpSteps()
        {
            // The mouse carries over between tests in a fixture, and pointing at the tip is now a
            // behaviour, so every test starts with the pointer somewhere else.
            AddStep("point away from the tip", () => InputManager.MoveMouseTo(Vector2.Zero));
            AddStep("forget handled links", () => linkHandler.Handled.Clear());
            AddStep("enable menu tips", () => config.SetValue(OsuSetting.MenuTips, true));
            AddStep("create display", () => Child = display = new MenuTipDisplay
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
            });

            // The display shows a random tip of its own accord on load; pin the one under test.
            AddStep("show the discord tip", () => display.ShowTip(discordTipIndex));
            AddUntilStep("tip is on screen", () => display.Alpha > 0);
        }

        [Test]
        public void TestClickOpensTheInviteExternally()
        {
            AddStep("click the link", () =>
            {
                var compiler = display.ChildrenOfType<DrawableLinkCompiler>().Single();

                InputManager.MoveMouseTo(compiler.Parts.First());
                InputManager.Click(MouseButton.Left);
            });

            AddAssert("one link handled", () => linkHandler.Handled.Count, () => Is.EqualTo(1));
            AddAssert("handled as an external link", () => linkHandler.Handled.Single().Action, () => Is.EqualTo(LinkAction.External));
            AddAssert("the link is the invite", () => linkHandler.Handled.Single().Argument.ToString(), () => Is.EqualTo("https://discord.gg/yAR2PDPgBB"));
        }

        /// <summary>
        /// Hovering cancels the linger and the fade-out outright rather than slowing them, which is
        /// what makes a click landing mid fade-out safe: the hover that must precede it has already
        /// put the tip back at full opacity.
        /// </summary>
        [Test]
        public void TestHoverHoldsTheFade()
        {
            AddAssert("a fade is queued to begin with", fadeQueued);

            AddStep("point at the tip", () => InputManager.MoveMouseTo(display));
            AddUntilStep("held opaque with no fade queued", () => display.Alpha == 1 && !fadeQueued());

            AddStep("point away", () => InputManager.MoveMouseTo(Vector2.Zero));
            AddUntilStep("fade re-armed", fadeQueued);
        }

        /// <summary>
        /// A click during the fade-out must not be swallowed. The tip is left to fade for a while,
        /// then pointed at: the hold restores it, and the click that follows still reaches the link.
        /// </summary>
        [Test]
        public void TestClickDuringFadeOutStillOpensTheInvite()
        {
            AddStep("start the fade-out now", () =>
            {
                // Skip the linger rather than waiting it out: the same fade the linger ends in, started early.
                display.ClearTransforms();
                display.FadeOutFromOne(2000, Easing.OutQuint);
            });

            AddUntilStep("tip is fading but still there", () => fadeQueued() && display.Alpha is > 0 and < 1);

            AddStep("point at the link and click", () =>
            {
                var compiler = display.ChildrenOfType<DrawableLinkCompiler>().Single();

                InputManager.MoveMouseTo(compiler.Parts.First());
                InputManager.Click(MouseButton.Left);
            });

            AddAssert("the invite was handled", () => linkHandler.Handled.Single().Argument.ToString(), () => Is.EqualTo(MenuTipDisplay.DISCORD_URL));
            AddUntilStep("and the tip is opaque again", () => display.Alpha == 1);
        }

        private class RecordingLinkHandler : ILinkHandler
        {
            public readonly List<LinkDetails> Handled = new List<LinkDetails>();

            public void HandleLink(string url) => HandleLink(new LinkDetails(LinkAction.External, url));

            public void HandleLink(LinkDetails link) => Handled.Add(link);
        }
    }
}
