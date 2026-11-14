// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Localisation;

namespace typebeat.Game.Graphics.UserInterface
{
    public class OsuMenuItem : MenuItem
    {
        public readonly MenuItemType Type;

        public Hotkey Hotkey { get; init; }

        public IconUsage Icon { get; init; }

        /// <summary>
        /// Shown while the item is hovered, disabled or not (a disabled item can say why). Settable,
        /// since what an item can do may change while its menu exists.
        /// </summary>
        public LocalisableString TooltipText { get; set; }

        public OsuMenuItem(LocalisableString text, MenuItemType type = MenuItemType.Standard)
            : this(text, type, null)
        {
        }

        public OsuMenuItem(LocalisableString text, MenuItemType type, Action? action)
            : base(text, action)
        {
            Type = type;
        }
    }
}
