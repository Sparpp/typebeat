// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using typebeat.Game.Localisation;

namespace typebeat.Game.Overlays.Settings.Sections.Input
{
    public partial class KeyBindingPanel : SettingsSubPanel
    {
        protected override Drawable CreateHeader() => new SettingsHeader(InputSettingsStrings.KeyBindingPanelHeader, InputSettingsStrings.KeyBindingPanelDescription);

        [BackgroundDependencyLoader(permitNulls: true)]
        private void load()
        {
            // Per-ruleset binding sections are not surfaced: the sole ruleset's variant bindings
            // are the two default buttons, which are not worth a section of their own.
            AddSection(new GlobalKeyBindingsSection());
        }
    }
}
