// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Utils;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using osuTK;
using typebeat.Game.Localisation;

namespace typebeat.Game.Screens.Menu
{
    public partial class MenuTipDisplay : CompositeDrawable
    {
        /// <summary>
        /// The type!beat Discord invite.
        /// </summary>
        public const string DISCORD_URL = @"https://discord.gg/yAR2PDPgBB";

        /// <summary>
        /// One tip in the menu rotation.
        /// </summary>
        /// <param name="Text">The line shown to the player.</param>
        /// <param name="Icon">The icon shown in front of it.</param>
        /// <param name="Url">
        /// Where the tip goes when clicked, or <c>null</c> for a plain text tip. A tip with a URL is
        /// rendered as a link and lingers long enough to be aimed at and clicked.
        /// </param>
        public readonly record struct MenuTip(LocalisableString Text, IconUsage Icon, string? Url);

        /// <summary>
        /// The whole rotation, one entry per tip. Adding a tip is appending an entry here: nothing
        /// else counts them, and a tip carrying a URL needs no other wiring to become clickable.
        /// </summary>
        public static readonly IReadOnlyList<MenuTip> Tips = new[]
        {
            // FontAwesome's brand mark rather than OsuIcon.Discord: that one is a glyph of the
            // resource package's own icon font, while this is the mark already used for a player's
            // Discord handle on the profile header.
            new MenuTip(MenuTipStrings.JoinDiscord, FontAwesome.Brands.Discord, DISCORD_URL),
        };

        private const double appear_delay = 600;
        private const double appear_duration = 800;
        private const double disappear_duration = 2000;

        private const double linger_base_ms = 1000;
        private const double linger_per_character_ms = 80;

        /// <summary>
        /// How long a LINK tip sits at full opacity before it starts to go. A plain tip only has to
        /// be read, so reading time is all it gets; a link has to be read, decided on, and then hit
        /// with the mouse, and the reading-time linger was over before that was possible.
        /// </summary>
        private const double link_linger_ms = 8000;

        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        [Resolved]
        private OsuColour colours { get; set; } = null!;

        private LinkFlowContainer textFlow = null!;

        private Bindable<bool> showMenuTips = null!;

        private bool currentTipIsLink;
        private bool fadeHeld;

        [BackgroundDependencyLoader]
        private void load()
        {
            AutoSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    CornerExponent = 2.5f,
                    CornerRadius = 10,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            Colour = Color4Extensions.FromHex("#171A1C"),
                            RelativeSizeAxes = Axes.Both,
                            Alpha = 0.75f,
                        },
                    }
                },
                textFlow = new LinkFlowContainer
                {
                    Width = 600,
                    AutoSizeAxes = Axes.Y,
                    TextAnchor = Anchor.TopCentre,
                    Spacing = new Vector2(0, 2),
                    Margin = new MarginPadding(10)
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            showMenuTips = config.GetBindable<bool>(OsuSetting.MenuTips);
            showMenuTips.BindValueChanged(_ => ShowNextTip(), true);
        }

        public void ShowNextTip() => ShowTip(RNG.Next(0, Tips.Count));

        /// <summary>
        /// Show one specific tip from <see cref="Tips"/>. <see cref="ShowNextTip"/> picks at random;
        /// this exists so a caller (a test, most of all) can pin the tip it wants.
        /// </summary>
        public void ShowTip(int index)
        {
            if (!showMenuTips.Value)
            {
                this.FadeOut(100, Easing.OutQuint);
                return;
            }

            static void formatRegular(SpriteText t) => t.Font = OsuFont.GetFont(size: 16, weight: FontWeight.Regular);

            var tip = Tips[index];

            currentTipIsLink = tip.Url != null;
            fadeHeld = false;

            textFlow.Clear();
            textFlow.AddIcon(tip.Icon, icon =>
            {
                icon.Colour = colours.Pink0;
                icon.Size = new Vector2(16);
            });
            textFlow.AddText(" ");

            // Both arms add to the icon's own line rather than opening a paragraph, so the icon reads
            // as part of the tip (and, for a link, sits next to the thing being clicked).
            if (tip.Url != null)
            {
                // LinkAction.External, so the click goes through the resolved link handler, which is
                // OsuGame: the external link confirmation and OpenUrlExternally come with it.
                textFlow.AddLink(tip.Text, tip.Url, formatRegular);
            }
            else
                textFlow.AddText(tip.Text, formatRegular);

            this
                .FadeOut()
                .ScaleTo(0.9f)
                .Delay(appear_delay)
                .FadeInFromZero(appear_duration, Easing.OutQuint)
                .ScaleTo(1, appear_duration, Easing.OutElasticHalf)
                .Delay(lingerFor(tip))
                .Then()
                .FadeOutFromOne(disappear_duration, Easing.OutQuint);
        }

        private static double lingerFor(MenuTip tip) =>
            tip.Url != null ? link_linger_ms : linger_base_ms + linger_per_character_ms * tip.Text.ToString().Length;

        protected override bool OnHover(HoverEvent e)
        {
            holdFade();
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            releaseFade();
            base.OnHoverLost(e);
        }

        /// <summary>
        /// Stop a link tip from fading while the player is pointing at it.
        /// </summary>
        /// <remarks>
        /// This is also what keeps the click safe. A drawable stops taking positional input once its
        /// alpha reaches zero, so a click during the fade-out would otherwise be a coin flip on how
        /// far the fade had got. A click is always preceded by a hover over the same drawable, and
        /// the hover cancels the fade and restores full opacity, so by the time the click arrives the
        /// tip is fully opaque again. Nothing is cleared but the alpha transforms: the scale part of
        /// the appear animation is left to finish on its own.
        /// </remarks>
        private void holdFade()
        {
            if (!currentTipIsLink || fadeHeld)
                return;

            fadeHeld = true;

            ClearTransforms(targetMember: nameof(Alpha));
            this.FadeIn(120, Easing.OutQuint);
        }

        /// <summary>
        /// Re-arm the fade once the player points somewhere else, giving them the full linger again
        /// from the moment they leave.
        /// </summary>
        private void releaseFade()
        {
            // Only a hold is released. Hover is also lost when the fade finishes on its own (alpha
            // zero stops positional input), and that must not restart the whole animation.
            if (!fadeHeld)
                return;

            fadeHeld = false;

            ClearTransforms(targetMember: nameof(Alpha));
            this.Delay(link_linger_ms).FadeOutFromOne(disappear_duration, Easing.OutQuint);
        }
    }
}
