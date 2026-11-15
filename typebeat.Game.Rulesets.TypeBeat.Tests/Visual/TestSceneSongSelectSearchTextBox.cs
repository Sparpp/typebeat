// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Overlays;
using typebeat.Game.Screens.Select;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneSongSelectSearchTextBox : OsuTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

        private FilterControl.SongSelectSearchTextBox search = null!;

        [Test]
        public void TestMatchesLabelFitsBelowTheQuery()
        {
            AddStep("create map search", () => Child = search = new FilterControl.SongSelectSearchTextBox
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Width = 480,
                HoldFocus = false,
                StatusText = "56 matches",
            });
            AddUntilStep("matches label is loaded", () => search.IsLoaded
                && search.ChildrenOfType<OsuSpriteText>().Any(sprite => sprite.Text.ToString() == "56 matches"));

            OsuSpriteText matchesLabel() => search.ChildrenOfType<OsuSpriteText>()
                .Single(sprite => sprite.Text.ToString().EndsWith("matches", StringComparison.Ordinal));

            AddAssert("matches label fits below the query", () =>
                matchesLabel().ScreenSpaceDrawQuad.AABBFloat.Bottom <= search.ScreenSpaceDrawQuad.AABBFloat.Bottom + 1);

            AddStep("update search results", () => search.StatusText = "123456 matches");
            AddUntilStep("new count is shown", () => matchesLabel().Text.ToString() == "123456 matches");

            AddStep("enter a search query", () => search.Current.Value = "hello");
            AddUntilStep("query characters shown", () => search.ChildrenOfType<OsuSpriteText>()
                .Count(sprite => sprite.Text.ToString().Length == 1) == 5);
        }
    }
}
