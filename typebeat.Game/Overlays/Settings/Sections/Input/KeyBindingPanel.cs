// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Graphics;
using typebeat.Game.Input.Bindings;
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
            AddSection(new GlobalKeyBindingsSection(FontAwesome.Solid.Globe, InputSettingsStrings.GlobalKeyBindingHeader)
            {
                Children = new[]
                {
                    new GlobalKeyBindingsSubsection(CommonStrings.General, GlobalActionCategory.General),
                    new GlobalKeyBindingsSubsection(InputSettingsStrings.AudioSection, GlobalActionCategory.AudioControl),
                    new GlobalKeyBindingsSubsection(InputSettingsStrings.OverlaysSection, GlobalActionCategory.Overlays),
                }
            });

            AddSection(new GlobalKeyBindingsSection(OsuIcon.GameplayC, GameplaySettingsStrings.GameplaySectionHeader)
            {
                Children = new[]
                {
                    new GlobalKeyBindingsSubsection(CommonStrings.General, GlobalActionCategory.InGame),
                    new GlobalKeyBindingsSubsection(InputSettingsStrings.ReplaySection, GlobalActionCategory.Replay),
                }
            });

            // The ruleset section was dropped once, when its only rows were the two vestigial
            // buttons. It is back because backlog 183 gave type!beat rebindable rows worth showing:
            // the two word-level typing gestures (erase word, select back to typo).
            foreach (var ruleset in rulesets.AvailableRulesets)
                AddSection(new RulesetBindingsSection(ruleset));

            AddSection(new GlobalKeyBindingsSection(OsuIcon.Beatmap, InputSettingsStrings.SongSelectSection)
            {
                Children = new[]
                {
                    new GlobalKeyBindingsSubsection(CommonStrings.General, GlobalActionCategory.SongSelect),
                }
            });

            // Upstream also adds each ruleset's own editor bindings here (RulesetEditorBindingsSubsection).
            // type!beat has no hit object composer to use them, and Ruleset.AllVariants leaves the editor
            // variant out, so those subsections would be empty.
            AddSection(new GlobalKeyBindingsSection(OsuIcon.EditorSelect, InputSettingsStrings.EditorSection)
            {
                Children = new[]
                {
                    // type!beat's editor is stripped to lyric authoring: only the actions it actually
                    // wires are shown here (the rest of type!beat's editor bindings stay registered but are
                    // inert: no blueprint selection, control points, beat grid, design/timing/verify).
                    new GlobalKeyBindingsSubsection(CommonStrings.General, GlobalActionCategory.Editor, new[]
                    {
                        GlobalAction.EditorComposeMode,
                        GlobalAction.EditorSetupMode,
                        GlobalAction.EditorTestGameplay,
                        GlobalAction.EditorSeekToPreviousHitObject,
                        GlobalAction.EditorSeekToNextHitObject,
                        GlobalAction.EditorDiscardUnsavedChanges,
                        GlobalAction.EditorTogglePause,
                        GlobalAction.EditorPlayFromStart,
                        GlobalAction.EditorSeekBackwards,
                        GlobalAction.EditorSeekForwards,
                        GlobalAction.EditorSeekToStart,
                        GlobalAction.EditorSeekToEnd,
                    }),
                    new GlobalKeyBindingsSubsection(InputSettingsStrings.EditorTestPlaySection, GlobalActionCategory.EditorTestPlay),
                }
            });
        }
    }
}
