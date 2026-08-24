// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using typebeat.Game.Localisation;
using typebeat.Game.Rulesets;

namespace typebeat.Game.Overlays.Settings.Sections.Input
{
    public partial class KeyBindingPanel : SettingsSubPanel
    {
        protected override Drawable CreateHeader() => new SettingsHeader(InputSettingsStrings.KeyBindingPanelHeader, InputSettingsStrings.KeyBindingPanelDescription);

        [BackgroundDependencyLoader(permitNulls: true)]
        private void load(RulesetStore rulesets)
        {
            AddSection(new GlobalKeyBindingsSection());

            // The ruleset section was dropped once, when its only rows were the two vestigial
            // buttons. It is back because backlog 183 gave type!beat rebindable rows worth showing:
            // the two word-level typing gestures (erase word, select back to typo).
            foreach (var ruleset in rulesets.AvailableRulesets)
                AddSection(new RulesetBindingsSection(ruleset));
        }
    }
}
