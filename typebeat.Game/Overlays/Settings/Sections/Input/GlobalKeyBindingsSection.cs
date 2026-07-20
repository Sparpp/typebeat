// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Localisation;

namespace typebeat.Game.Overlays.Settings.Sections.Input
{
    public partial class GlobalKeyBindingsSection : SettingsSection
    {
        public override Drawable CreateIcon() => new SpriteIcon
        {
            Icon = FontAwesome.Solid.Globe
        };

        public override LocalisableString Header => InputSettingsStrings.GlobalKeyBindingHeader;

        [BackgroundDependencyLoader]
        private void load()
        {
            AddRange(new[]
            {
                new GlobalKeyBindingsSubsection(string.Empty, GlobalActionCategory.General),
                new GlobalKeyBindingsSubsection(InputSettingsStrings.OverlaysSection, GlobalActionCategory.Overlays),
                new GlobalKeyBindingsSubsection(InputSettingsStrings.AudioSection, GlobalActionCategory.AudioControl),
                new GlobalKeyBindingsSubsection(InputSettingsStrings.SongSelectSection, GlobalActionCategory.SongSelect),
                new GlobalKeyBindingsSubsection(InputSettingsStrings.InGameSection, GlobalActionCategory.InGame),
                new GlobalKeyBindingsSubsection(InputSettingsStrings.ReplaySection, GlobalActionCategory.Replay),
                // type!beat's editor is stripped to lyric authoring — only the actions it actually
                // wires are shown here (the rest of type!beat's editor bindings stay registered but are
                // inert: no blueprint selection, control points, beat grid, design/timing/verify).
                new GlobalKeyBindingsSubsection(InputSettingsStrings.EditorSection, GlobalActionCategory.Editor, new[]
                {
                    GlobalAction.EditorComposeMode,
                    GlobalAction.EditorSetupMode,
                    GlobalAction.EditorTestGameplay,
                    GlobalAction.EditorSeekToPreviousHitObject,
                    GlobalAction.EditorSeekToNextHitObject,
                    GlobalAction.EditorDiscardUnsavedChanges,
                }),
                new GlobalKeyBindingsSubsection(InputSettingsStrings.EditorTestPlaySection, GlobalActionCategory.EditorTestPlay),
            });
        }
    }
}
