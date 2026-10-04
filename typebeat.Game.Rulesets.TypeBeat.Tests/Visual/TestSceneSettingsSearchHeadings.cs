// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Localisation;
using osu.Framework.Testing;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Headings placed among a settings subsection's children (type!beat's "Typing", "Carets",
    /// "Visual feedback" and so on) follow the settings search: a heading shows only while something
    /// under it matches, or when the heading itself matches, which shows its whole group.
    /// </summary>
    public partial class TestSceneSettingsSearchHeadings : OsuTestScene
    {
        private SearchContainer search = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create a subsection with headings", () => Child = search = new SearchContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Child = new HeadedSubsection(),
            });
        }

        [Test]
        public void TestNoSearchShowsEverything()
        {
            AddAssert("all headings shown", () => headingShown("Typing") && headingShown("Carets") && headingShown("Visual feedback"));
            AddAssert("all settings shown", () => new[] { "space skips word", "caret style", "caret smoothing", "syllable fade-in", "text pop-in" }.All(settingShown));
        }

        [Test]
        public void TestASettingShowsOnlyItsOwnHeading()
        {
            AddStep("search 'fade'", () => search.SearchTerm = "fade");

            AddAssert("its heading shows", () => headingShown("Visual feedback"));
            AddAssert("the setting shows", () => settingShown("syllable fade-in"));
            AddAssert("its unmatched sibling hides", () => !settingShown("text pop-in"));
            AddAssert("the other headings hide", () => !headingShown("Typing") && !headingShown("Carets"));
        }

        [Test]
        public void TestAHeadingShowsItsWholeGroup()
        {
            AddStep("search 'carets'", () => search.SearchTerm = "carets");

            AddAssert("the heading shows", () => headingShown("Carets"));
            AddAssert("with every setting under it", () => settingShown("caret style") && settingShown("caret smoothing"));
            AddAssert("other groups hide", () => !headingShown("Typing") && !headingShown("Visual feedback"));
        }

        [Test]
        public void TestNoMatchHidesEveryHeading()
        {
            AddStep("search 'zzz'", () => search.SearchTerm = "zzz");
            AddAssert("no heading shows", () => !headingShown("Typing") && !headingShown("Carets") && !headingShown("Visual feedback"));
        }

        [Test]
        public void TestClearingTheSearchBringsEverythingBack()
        {
            AddStep("search 'fade'", () => search.SearchTerm = "fade");
            AddStep("clear the search", () => search.SearchTerm = string.Empty);
            AddAssert("all headings shown again", () => headingShown("Typing") && headingShown("Carets") && headingShown("Visual feedback"));
        }

        private bool headingShown(string text) => shownOnScreen(search.ChildrenOfType<OsuSpriteText>().Single(t => t.Text.ToString() == text));

        private bool settingShown(string term) => shownOnScreen(search.ChildrenOfType<FilterableSetting>().Single(s => s.Term == term));

        /// <summary>Present itself and through every parent up to the search container.</summary>
        private bool shownOnScreen(Drawable drawable)
        {
            for (Drawable? d = drawable; d != null && d != search; d = d.Parent)
            {
                if (!d.IsPresent)
                    return false;
            }

            return true;
        }

        private partial class HeadedSubsection : SettingsSubsection
        {
            protected override LocalisableString Header => default;

            protected override Drawable CreateHeader() => Empty();

            public HeadedSubsection()
            {
                Children = new Drawable[]
                {
                    CreateSubsectionHeader("Typing"),
                    new FilterableSetting("space skips word"),
                    CreateSubsectionHeader("Carets"),
                    new FilterableSetting("caret style"),
                    new FilterableSetting("caret smoothing"),
                    CreateSubsectionHeader("Visual feedback"),
                    new FilterableSetting("syllable fade-in"),
                    new FilterableSetting("text pop-in"),
                };
            }
        }

        private partial class FilterableSetting : Box, IFilterable
        {
            public readonly string Term;

            public FilterableSetting(string term)
            {
                Term = term;
                RelativeSizeAxes = Axes.X;
                Height = 20;
            }

            public IEnumerable<LocalisableString> FilterTerms => new LocalisableString[] { Term };

            public bool MatchingFilter
            {
                set => Alpha = value ? 1 : 0;
            }

            public bool FilteringActive { get; set; }
        }
    }
}
