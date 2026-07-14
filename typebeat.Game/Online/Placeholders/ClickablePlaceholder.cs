// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.UserInterface;

namespace typebeat.Game.Online.Placeholders
{
    public partial class ClickablePlaceholder : Placeholder
    {
        public Action Action;

        public ClickablePlaceholder(LocalisableString actionMessage, IconUsage icon)
        {
            OsuAnimatedButton button;
            OsuTextFlowContainer textFlow;

            AddArbitraryDrawable(button = new OsuAnimatedButton
            {
                AutoSizeAxes = osu.Framework.Graphics.Axes.Both,
                Action = () => Action?.Invoke()
            });

            button.Add(textFlow = new OsuTextFlowContainer(cp => cp.Font = cp.Font.With(size: TEXT_SIZE))
            {
                AutoSizeAxes = osu.Framework.Graphics.Axes.Both,
                Margin = new osu.Framework.Graphics.MarginPadding(5)
            });

            textFlow.AddIcon(icon, i =>
            {
                i.Padding = new osu.Framework.Graphics.MarginPadding { Right = 10 };
            });

            textFlow.AddText(actionMessage);
        }
    }
}
