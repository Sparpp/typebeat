// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;

namespace typebeat.Game.Overlays.Settings.Sections.Gameplay
{
    public partial class BeatmapSettings : SettingsSubsection
    {
        protected override LocalisableString Header => GameplaySettingsStrings.BeatmapHeader;

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            Children = new Drawable[]
            {
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = SkinSettingsStrings.BeatmapSkins,
                    Current = config.GetBindable<bool>(OsuSetting.BeatmapSkins)
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = SkinSettingsStrings.BeatmapColours,
                    Current = config.GetBindable<bool>(OsuSetting.BeatmapColours)
                })
                {
                    Keywords = new[] { "combo", "override", "color" },
                },
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = SkinSettingsStrings.BeatmapHitsounds,
                    Current = config.GetBindable<bool>(OsuSetting.BeatmapHitsounds)
                })
                {
                    Keywords = new[] { "samples", "override" },
                },
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = GraphicsSettingsStrings.StoryboardVideo,
                    Current = config.GetBindable<bool>(OsuSetting.ShowStoryboard)
                }),
                // Combo colour normalisation is not surfaced here any more; it remains adjustable
                // from the in-game visual settings panel.
            };
        }
    }
}
