// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Rulesets;

namespace typebeat.Game.Overlays.Settings.Sections
{
    public partial class RulesetSection : SettingsSection
    {
        // There is only one ruleset, so the section carries its name directly rather than the
        // generic "Rulesets" heading (its subsection heading is blank for the same reason).
        public override LocalisableString Header => @"type!beat";

        public override Drawable CreateIcon() => new SpriteIcon
        {
            Icon = OsuIcon.Rulesets
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
        }
    }
}
