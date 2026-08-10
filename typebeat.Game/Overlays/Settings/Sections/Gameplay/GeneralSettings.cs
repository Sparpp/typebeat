// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Localisation;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;
using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Overlays.Settings.Sections.Gameplay
{
    public partial class GeneralSettings : SettingsSubsection
    {
        protected override LocalisableString Header => CommonStrings.General;

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            Children = new Drawable[]
            {
                new SettingsItemV2(new FormEnumDropdown<ScoringMode>
                {
                    Caption = GameplaySettingsStrings.ScoreDisplayMode,
                    Current = config.GetBindable<ScoringMode>(OsuSetting.ScoreDisplayMode),
                })
                {
                    Keywords = new[] { "scoring" },
                    ApplyClassicDefault = c => ((IHasCurrentValue<ScoringMode>)c).Current.Value = ScoringMode.Classic,
                },
                // Hit lighting and star fountains are not surfaced; both keys keep their
                // configured defaults (OsuSetting.HitLighting, OsuSetting.StarFountains).
            };
        }
    }
}
