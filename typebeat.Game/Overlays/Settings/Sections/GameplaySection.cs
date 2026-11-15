// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Settings.Sections.Gameplay;
using typebeat.Game.Rulesets;

namespace typebeat.Game.Overlays.Settings.Sections
{
    /// <summary>
    /// Settings > Gameplay. Besides the game-wide gameplay subsections it hosts each ruleset's own
    /// settings (<see cref="Ruleset.CreateSettings"/>), which used to get a section and a sidebar
    /// entry of their own under this one (backlog 479). They come FIRST: for type!beat they are the
    /// game's main settings (typing, lyric text, carets, visual feedback), and the generic subsections
    /// after them are mostly what the osu! base left behind.
    /// </summary>
    public partial class GameplaySection : SettingsSection
    {
        public override LocalisableString Header => GameplaySettingsStrings.GameplaySectionHeader;

        public override Drawable CreateIcon() => new SpriteIcon
        {
            Icon = OsuIcon.GameplayC
        };

        [BackgroundDependencyLoader]
        private void load(RulesetStore rulesets)
        {
            foreach (Ruleset ruleset in rulesets.AvailableRulesets.Select(info => info.CreateInstance()))
            {
                try
                {
                    SettingsSubsection? section = ruleset.CreateSettings();

                    if (section != null)
                        Add(section);
                }
                catch (Exception e)
                {
                    RulesetStore.LogRulesetFailure(ruleset.RulesetInfo, e);
                }
            }

            AddRange(new Drawable[]
            {
                new GeneralSettings(),
                new BackgroundSettings(),
                new BeatmapSettings(),
                new HUDSettings(),
            });

            // Its only setting is the Windows key toggle (backlog 482).
            if (RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
                Add(new InputSettings());
        }
    }
}
