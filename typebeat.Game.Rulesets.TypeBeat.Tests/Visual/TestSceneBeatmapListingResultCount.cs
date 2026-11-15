// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.BeatmapListing;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 474: the beatmap listing shows how many sets the whole search matches ("1,234 results") in the sort row,
    /// from the search response's <c>total</c>, and follows it as the query and filters change. A response without the
    /// field (a server from before it) hides the count instead of claiming "0 results".
    /// </summary>
    [TestFixture]
    public partial class TestSceneBeatmapListingResultCount : OsuTestScene
    {
        private BeatmapListingOverlay overlay = null!;

        private DummyAPIAccess dummyApi => (DummyAPIAccess)API;

        // What the mocked search answers with: the next response's total and how many cards come back with it.
        private int? nextTotal;
        private int nextPageCount;
        private int searchesAnswered;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("mock the search", () =>
            {
                nextTotal = 1234;
                nextPageCount = 3;
                searchesAnswered = 0;

                dummyApi.SetState(APIState.Online);
                dummyApi.HandleRequest = request =>
                {
                    if (request is not SearchBeatmapSetsRequest search)
                        return false;

                    search.TriggerSuccess(new SearchBeatmapSetsResponse
                    {
                        BeatmapSets = Enumerable.Range(0, nextPageCount).Select(_ => CreateAPIBeatmapSet()).ToArray(),
                        Total = nextTotal,
                    });
                    searchesAnswered++;
                    return true;
                };
            });

            AddStep("show the listing", () => Child = overlay = new BeatmapListingOverlay { State = { Value = Visibility.Visible } });
        }

        [Test]
        public void TestCountFollowsTheSearch()
        {
            searchFor("stars>5", total: 1234, cards: 3);
            AddUntilStep("shows 1,234 results", () => shownCount(), () => Is.EqualTo("1,234 results"));

            searchFor("stars>9", total: 1, cards: 1);
            AddUntilStep("one is singular", () => shownCount(), () => Is.EqualTo("1 result"));

            searchFor("stars>99", total: 0, cards: 0);
            AddUntilStep("none is 0 results", () => shownCount(), () => Is.EqualTo("0 results"));

            AddStep("switch the category filter", () =>
            {
                nextTotal = 42;
                nextPageCount = 2;
                overlay.ChildrenOfType<BeatmapListingSearchControl>().Single().Category.Value = SearchCategory.Ranked;
            });
            AddUntilStep("a filter change updates it", () => shownCount(), () => Is.EqualTo("42 results"));
        }

        [Test]
        public void TestAServerWithoutATotalHidesTheCount()
        {
            searchFor("stars>5", total: 1234, cards: 3);
            AddUntilStep("count shown", () => shownCount(), () => Is.EqualTo("1,234 results"));

            searchFor("stars>6", total: null, cards: 3);
            AddUntilStep("count hidden", () => shownCount(), () => Is.Null);
        }

        [Test]
        public void TestWording()
        {
            AddAssert("0", () => BeatmapListingFilterControl.FormatResultCount(0), () => Is.EqualTo("0 results"));
            AddAssert("1", () => BeatmapListingFilterControl.FormatResultCount(1), () => Is.EqualTo("1 result"));
            AddAssert("2", () => BeatmapListingFilterControl.FormatResultCount(2), () => Is.EqualTo("2 results"));
            AddAssert("grouped", () => BeatmapListingFilterControl.FormatResultCount(1_234_567), () => Is.EqualTo("1,234,567 results"));
        }

        [Test]
        public void TestTheWireField()
        {
            // An older server's answer has no "total": it must read as unknown, never as 0.
            AddAssert("absent reads as unknown",
                () => JsonConvert.DeserializeObject<SearchBeatmapSetsResponse>(@"{""beatmapsets"": [], ""cursor"": null}")!.Total, () => Is.Null);
            AddAssert("present reads as sent",
                () => JsonConvert.DeserializeObject<SearchBeatmapSetsResponse>(@"{""beatmapsets"": [], ""cursor"": null, ""total"": 1234}")!.Total, () => Is.EqualTo(1234));
        }

        private void searchFor(string query, int? total, int cards)
        {
            int answeredBefore = 0;

            AddStep($"search \"{query}\" ({total?.ToString() ?? "no"} total)", () =>
            {
                nextTotal = total;
                nextPageCount = cards;
                answeredBefore = searchesAnswered;
                overlay.ShowWithSearch(query);
            });
            AddUntilStep("search answered", () => searchesAnswered, () => Is.GreaterThan(answeredBefore));
        }

        /// <summary>The fully shown result count in the sort row, or null while none is shown.</summary>
        private string? shownCount()
        {
            var count = overlay.ChildrenOfType<BeatmapListingFilterControl>().Single()
                               .ChildrenOfType<OsuSpriteText>().Single(t => t.Name == BeatmapListingFilterControl.RESULT_COUNT_NAME);

            return count.Alpha == 1 ? count.Text.ToString() : null;
        }
    }
}
