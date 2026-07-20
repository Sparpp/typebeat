// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Extensions.LocalisationExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
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
        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        [Resolved]
        private OsuColour colours { get; set; } = null!;

        private LinkFlowContainer textFlow = null!;

        private Bindable<bool> showMenuTips = null!;

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

        public void ShowNextTip()
        {
            if (!showMenuTips.Value)
            {
                this.FadeOut(100, Easing.OutQuint);
                return;
            }

            static void formatRegular(SpriteText t) => t.Font = OsuFont.GetFont(size: 16, weight: FontWeight.Regular);

            var tip = getRandomTip();

            textFlow.Clear();
            textFlow.AddIcon(FontAwesome.Solid.Lightbulb, icon =>
            {
                icon.Colour = colours.Pink0;
                icon.Size = new Vector2(16);
            });
            textFlow.AddText(" ");
            textFlow.AddParagraph(tip, formatRegular);

            this
                .FadeOut()
                .ScaleTo(0.9f)
                .Delay(600)
                .FadeInFromZero(800, Easing.OutQuint)
                .ScaleTo(1, 800, Easing.OutElasticHalf)
                .Delay(1000 + 80 * tip.ToString().Length)
                .Then()
                .FadeOutFromOne(2000, Easing.OutQuint);
        }

        private const int available_tips = 1;

        private LocalisableString getRandomTip()
        {
            int tipIndex = RNG.Next(0, available_tips);

            switch (tipIndex)
            {
                case 0:
                    return MenuTipStrings.EmbeddedWebContent;
            }

            return string.Empty;
        }
    }
}
