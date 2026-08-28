// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Rulesets;

namespace typebeat.Game.Overlays.Settings.Sections
{
    /// <summary>
    /// Settings that are still being proven out: they work, but their shape (or whether they stay at
    /// all) is not settled yet. The section sits directly under the ruleset's own so the two read as
    /// "the settled type!beat settings, then the ones on trial". It holds nothing itself; each ruleset
    /// hands over whichever of its settings it considers experimental via
    /// <see cref="Ruleset.CreateExperimentalSettings"/>.
    /// </summary>
    public partial class ExperimentalSection : SettingsSection
    {
        public override LocalisableString Header => @"Experimental";

        public override Drawable CreateIcon() => new SpriteIcon
        {
            // No OsuIcon reads as "experimental", so this reaches straight for FontAwesome the way
            // AfToggleSection does. It must stay distinct from the other sections' icons, since the
            // sidebar strip is icons only.
            Icon = FontAwesome.Solid.Flask
        };

        [BackgroundDependencyLoader]
        private void load(RulesetStore rulesets)
        {
            foreach (Ruleset ruleset in rulesets.AvailableRulesets.Select(info => info.CreateInstance()))
            {
                try
                {
                    SettingsSubsection? section = ruleset.CreateExperimentalSettings();

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
