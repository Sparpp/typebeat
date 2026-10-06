// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps.Drawables;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.BeatmapSet;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The set overlay's difficulty line reads like song select's: the selected difficulty's star rating pill, then its name.
    /// </summary>
    [TestFixture]
    public partial class TestSceneBeatmapPickerStarRating : OsuTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Pink);

        private static readonly APIBeatmap easy = new APIBeatmap { OnlineID = 1, DifficultyName = "Easy", StarRating = 2.25 };
        private static readonly APIBeatmap hard = new APIBeatmap { OnlineID = 2, DifficultyName = "Hard", StarRating = 5.5 };

        private BeatmapPicker picker = null!;

        private StarRatingDisplay? pill => picker.ChildrenOfType<StarRatingDisplay>().SingleOrDefault(d => d.Parent != picker.Difficulties);

        private OsuSpriteText name(string difficulty) => picker.ChildrenOfType<OsuSpriteText>().Single(t => t.Text.ToString() == difficulty);

        [Test]
        public void TestStarRatingLeadsTheDifficultyName()
        {
            // The picker lists only the current ruleset's difficulties.
            AddStep("set ruleset", () => Ruleset.Value = new TypeBeatRuleset().RulesetInfo);
            AddStep("load picker", () => Child = picker = new BeatmapPicker { Width = 600 });

            // After load, as in the overlay, which sets it once the lookup answers (its updateDisplay needs the injected ruleset).
            AddStep("show the set", () => picker.BeatmapSet = new APIBeatmapSet { OnlineID = 266, Beatmaps = new[] { easy, hard } });

            AddUntilStep("pill shows the selected difficulty's stars", () => pill?.Current.Value.Stars, () => Is.EqualTo(2.25));
            AddAssert("pill sits left of the name", () => pill!.ScreenSpaceDrawQuad.TopRight.X, () => Is.LessThanOrEqualTo(name("Easy").ScreenSpaceDrawQuad.TopLeft.X));
            AddAssert("name is optically centred against the pill", () => pill!.ScreenSpaceDrawQuad.Centre.Y - name("Easy").ScreenSpaceDrawQuad.Centre.Y, () => Is.EqualTo(1).Within(0.1f));

            AddStep("select the hard difficulty", () => picker.Beatmap.Value = hard);
            AddUntilStep("pill follows the selection", () => pill?.Current.Value.Stars, () => Is.EqualTo(5.5));
            AddAssert("no separate stars text", () => picker.ChildrenOfType<OsuSpriteText>().Any(t => t.Text.ToString().Contains("Star Difficulty")), () => Is.False);
        }
    }
}
