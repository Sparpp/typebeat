// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Localisation;
using osu.Framework.Testing;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Overlays.Settings.Sections;
using typebeat.Game.Rulesets;

namespace typebeat.Game.Overlays.FirstRunSetup
{
    [LocalisableDescription(typeof(FirstRunSetupOverlayStrings), nameof(FirstRunSetupOverlayStrings.Behaviour))]
    public partial class ScreenBehaviour : WizardScreen
    {
        private SearchContainer<SettingsSection> searchContainer = null!;

        [BackgroundDependencyLoader]
        private void load(OsuColour colours, RulesetStore rulesets)
        {
            Content.Children = new Drawable[]
            {
                new OsuTextFlowContainer(cp => cp.Font = OsuFont.Default.With(size: CONTENT_FONT_SIZE))
                {
                    Text = FirstRunSetupOverlayStrings.BehaviourDescription,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y
                },
                new GridContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    ColumnDimensions = new[]
                    {
                        new Dimension(),
                        new Dimension(GridSizeMode.Absolute, 10),
                        new Dimension(),
                    },
                    RowDimensions = new[]
                    {
                        new Dimension(GridSizeMode.AutoSize),
                    },
                    Content = new[]
                    {
                        new[]
                        {
                            new RoundedButton
                            {
                                Anchor = Anchor.TopLeft,
                                Origin = Anchor.TopLeft,
                                Text = FirstRunSetupOverlayStrings.NewDefaults,
                                RelativeSizeAxes = Axes.X,
                                Action = applyStandard,
                            },
                            Empty(),
                            new RoundedButton
                            {
                                Anchor = Anchor.TopRight,
                                Origin = Anchor.TopRight,
                                BackgroundColour = colours.DangerousButtonColour,
                                Text = FirstRunSetupOverlayStrings.ClassicDefaults,
                                RelativeSizeAxes = Axes.X,
                                Action = applyClassic
                            }
                        },
                    },
                },
                searchContainer = new SearchContainer<SettingsSection>
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    // This list should be kept in sync with SettingsOverlay.
                    ChildrenEnumerable = new SettingsSection[]
                    {
                        new GeneralSection(),
                        new SkinSection(),
                        // InputSection is intentionally omitted for now due to its sub-panel being a pain to set up.
                        new UserInterfaceSection(),
                        new GameplaySection(),
                    }.Concat(createRulesetSections(rulesets)).Concat(new SettingsSection[]
                    {
                        // ExperimentalSection is intentionally omitted: settings still on trial are
                        // not something to put in front of a player who has not played yet.
                        new AudioSection(),
                        new GraphicsSection(),
                        new MaintenanceSection()
                    }),
                    SearchTerm = SettingsItem<bool>.CLASSIC_DEFAULT_SEARCH_TERM,
                }
            };
        }

        /// <summary>
        /// One section per ruleset that has settings, built the way SettingsOverlay builds them.
        /// Upstream dropped ruleset settings from this screen when RulesetSection started taking
        /// a ruleset; type!beat keeps them, since its ruleset is the game.
        /// </summary>
        private static IEnumerable<SettingsSection> createRulesetSections(RulesetStore rulesets)
        {
            foreach (Ruleset ruleset in rulesets.AvailableRulesets.Select(info => info.CreateInstance()))
            {
                SettingsSubsection? section = null;

                try
                {
                    section = ruleset.CreateSettings();
                }
                catch (Exception e)
                {
                    RulesetStore.LogRulesetFailure(ruleset.RulesetInfo, e);
                }

                if (section != null)
                    yield return new RulesetSection(ruleset, section);
            }
        }

        private void applyClassic()
        {
            foreach (var i in searchContainer.ChildrenOfType<ISettingsItem>().Where(s => s.HasClassicDefault))
                i.ApplyClassicDefault();
        }

        private void applyStandard()
        {
            foreach (var i in searchContainer.ChildrenOfType<ISettingsItem>().Where(s => s.HasClassicDefault))
                i.ApplyDefault();
        }
    }
}
