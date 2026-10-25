// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Framework.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Timing;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Drives the editor's Timing menu grid controls (<see cref="TimingMenuItems"/>), which replaced the
    /// toolbar row of buttons above the timeline.
    /// </summary>
    public static class TimingMenuTestExtensions
    {
        /// <summary>One of the Timing menu's toggles, by its label (see the constants on <see cref="TimingMenuItems"/>).</summary>
        public static ToggleMenuItem TimingToggle(this Editor editor, string label) =>
            editor.TimingMenu.Items.OfType<ToggleMenuItem>().Single(i => i.Text.Value.ToString() == label);

        /// <summary>Clicks a menu item the way the menu would: a disabled item does nothing.</summary>
        public static void Press(this MenuItem item)
        {
            if (!item.Action.Disabled)
                item.Action.Value?.Invoke();
        }

        /// <summary>Picks a grid subdivision from the Timing menu (negative = a beat multiple, as in <see cref="TimingMenuItems.NoteName"/>).</summary>
        public static void SelectSubdivision(this Editor editor, int option) =>
            editor.TimingMenu.Items.Single(i => i.Text.Value.ToString().StartsWith(TimingMenuItems.GRID_SUBDIVISION, System.StringComparison.Ordinal))
                  .Items.Single(i => i.Text.Value.ToString() == TimingMenuItems.NoteName(option)).Press();
    }
}
