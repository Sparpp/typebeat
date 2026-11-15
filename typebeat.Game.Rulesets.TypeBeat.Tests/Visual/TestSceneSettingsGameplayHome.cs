// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Overlays.Settings.Sections;
using typebeat.Game.Overlays.Settings.Sections.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 479: the ruleset's settings live inside Settings &gt; Gameplay rather than in a
    /// "Gameplay -&gt; type!beat" section and sidebar entry of their own, and the visual feedback and
    /// timing tint controls left Experimental for them. Checked on the real overlay: where each control
    /// ends up, and that navigation and the search still reach them there.
    /// </summary>
    public partial class TestSceneSettingsGameplayHome : OsuTestScene
    {
        /// <summary>Every control that moved out of Experimental, by the caption a player sees.</summary>
        private static readonly string[] moved_captions =
        {
            "Show sync metric",
            "Pace bar reveal",
            "Colour typed-letter bars by judgement",
            "Red screen edges before forced line changes",
            TypeBeatSettingsSubsection.TIMING_TINT_CAPTION,
            TypeBeatSettingsSubsection.TIMING_TINT_MIN_CAPTION,
            TypeBeatSettingsSubsection.TIMING_TINT_MAX_CAPTION,
            TypeBeatSettingsSubsection.TIMING_TINT_EARLY_COLOUR_CAPTION,
            TypeBeatSettingsSubsection.TIMING_TINT_LATE_COLOUR_CAPTION,
        };

        /// <summary>
        /// Backlog 483's legacy osu! settings that did nothing in type!beat or misled, by the caption a
        /// player saw. The Gameplay "Beatmap" and "General" groups went with them.
        /// </summary>
        private static readonly string[] retired_captions =
        {
            "Beatmap skins",
            "Beatmap colours",
            "Beatmap hitsounds",
            "Fade playfield to red when health is low",
            "Score display mode",
            "Interface voices",
            "Background source",
            "Show converted beatmaps",
            "Disable taps during gameplay",
        };

        private SettingsOverlay settings = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("open the settings", () =>
            {
                Child = settings = new SettingsOverlay
                {
                    State = { Value = Visibility.Visible },
                };
            });

            AddUntilStep("sections loaded", () => settings.SectionsContainer.Any() && settings.ChildrenOfType<TypeBeatSettingsSubsection>().SingleOrDefault()?.IsLoaded == true);
            AddUntilStep("sidebar loaded", () => settings.ChildrenOfType<SidebarIconButton>().Count() == settings.SectionsContainer.Children.Count);
        }

        [Test]
        public void TestThereIsNoSeparateRulesetSection()
        {
            AddAssert("no section is the ruleset's own", () => settings.SectionsContainer.All(s => s.Header.ToString() != new TypeBeatRuleset().RulesetInfo.Name));
            AddAssert("no sidebar entry is the ruleset's own",
                () => settings.ChildrenOfType<SidebarIconButton>().All(b => b.Section.Header.ToString() != new TypeBeatRuleset().RulesetInfo.Name));
            AddAssert("Gameplay is followed directly by Experimental", () =>
            {
                var sections = settings.SectionsContainer.ToList();
                return sections.IndexOf(gameplay) + 1 == sections.IndexOf(experimental);
            });
        }

        [Test]
        public void TestGameplayHostsTheRulesetSettingsFirst()
        {
            AddAssert("the ruleset's settings are inside Gameplay", () => settings.ChildrenOfType<TypeBeatSettingsSubsection>().Single().FindClosestParent<SettingsSection>() == gameplay);
            AddAssert("ahead of Gameplay's own subsections, below only the presets",
                () => gameplay.ChildrenOfType<SettingsSubsection>().First(s => s is not PresetSettings) is TypeBeatSettingsSubsection);
            AddAssert("settled controls are under Gameplay", () => captionsIn(gameplay).Contains("Space to skip current word"));
        }

        [Test]
        public void TestTheMovedControlsLiveUnderGameplayNotExperimental()
        {
            AddAssert("all under Gameplay", () => moved_captions.All(captionsIn(gameplay).Contains));
            AddAssert("none under Experimental", () => !moved_captions.Any(captionsIn(experimental).Contains));
            AddAssert("each appears once in the whole overlay", () => moved_captions.All(c => captionsIn(settings).Count(x => x == c) == 1));
            AddAssert("the aligner stays in Experimental", () => captionsIn(experimental).Contains("High-accuracy alignment (about 4x slower per import)")
                                                                 && !captionsIn(gameplay).Contains("High-accuracy alignment (about 4x slower per import)"));
        }

        [Test]
        public void TestTheRetiredLegacySettingsAreGone()
        {
            AddAssert("none is offered anywhere", () => !retired_captions.Any(captionsIn(settings).Contains));
            AddAssert("the storyboard / video switch that shared their group stays under Gameplay",
                () => captionsIn(gameplay).Count(c => c == GraphicsSettingsStrings.StoryboardVideo.ToString()) == 1);
        }

        [Test]
        public void TestShowAtControlReachesTheRulesetSettingsInGameplay()
        {
            AddStep("scroll away to the bottom", () => settings.SectionsContainer.ScrollTo(settings.SectionsContainer.Last()));
            AddUntilStep("away from Gameplay", () => settings.CurrentSection.Value != gameplay);

            AddStep("show the ruleset's settings", () => settings.ShowAtControl<TypeBeatSettingsSubsection>());
            AddUntilStep("Gameplay is the current section", () => settings.CurrentSection.Value == gameplay);
        }

        [Test]
        public void TestShowAtControlStillReachesExperimental()
        {
            AddStep("show Experimental", () => settings.ShowAtControl<ExperimentalSection>());
            AddUntilStep("Experimental is the current section", () => settings.CurrentSection.Value == experimental);
        }

        [Test]
        public void TestSearchFindsAMovedControlUnderGameplay()
        {
            search("sync metric");

            AddUntilStep("the sync metric shows", () => shown(control("Show sync metric")));
            AddUntilStep("inside Gameplay", () => shown(gameplay) && control("Show sync metric").FindClosestParent<SettingsSection>() == gameplay);
            AddUntilStep("Experimental has nothing to show", () => !shown(experimental));

            search("tint typed");

            AddUntilStep("the timing tint switch shows", () => shown(control(TypeBeatSettingsSubsection.TIMING_TINT_CAPTION)));
            AddUntilStep("and its unmatched sibling hides", () => !shown(control(TypeBeatSettingsSubsection.TIMING_TINT_LATE_COLOUR_CAPTION)));
        }

        [Test]
        public void TestSearchingTheRulesetNameShowsItsSettings()
        {
            search(new TypeBeatRuleset().RulesetInfo.Name);

            AddUntilStep("the ruleset's settings show in Gameplay", () => shown(gameplay) && shown(control("Space to skip current word")) && shown(control("Show sync metric")));
        }

        private GameplaySection gameplay => settings.SectionsContainer.OfType<GameplaySection>().Single();

        private ExperimentalSection experimental => settings.SectionsContainer.OfType<ExperimentalSection>().Single();

        // The key binding sub-panel is a SettingsPanel with a search box of its own.
        private void search(string term) => AddStep($"search '{term}'", () => settings.ChildrenOfType<SettingsSearchTextBox>().Single(t => t.FindClosestParent<SettingsPanel>() == settings).Current.Value = term);

        /// <summary>The settings row for <paramref name="caption"/>, wherever it is.</summary>
        private Drawable control(string caption) => settings.ChildrenOfType<SettingsItemV2>().Single(i => i.Control.FilterTerms.Any(t => t.ToString() == caption));

        private static string[] captionsIn(Drawable root) =>
            root.ChildrenOfType<SettingsItemV2>().SelectMany(i => i.Control.FilterTerms).Select(t => t.ToString()).ToArray();

        /// <summary>Present itself and through every parent up to the overlay.</summary>
        private bool shown(Drawable drawable)
        {
            for (Drawable? d = drawable; d != null && d != settings; d = d.Parent)
            {
                if (!d.IsPresent)
                    return false;
            }

            return true;
        }
    }
}
