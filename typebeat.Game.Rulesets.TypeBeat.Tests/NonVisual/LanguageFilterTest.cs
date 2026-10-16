// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Screens.Select;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Song select's <c>lang:</c>, read as the website's search box reads it: equality only, a language's name or
    /// one of the site's aliases, any case. Anything else stays search text, and a map with no language set never
    /// matches. WireCompat's <c>SearchOperatorParityTest</c> holds the two parsers against each other on every alias.
    /// </summary>
    [TestFixture]
    public class LanguageFilterTest
    {
        [TestCase("lang:japanese", BeatmapLanguage.Japanese)]
        [TestCase("lang=Japanese", BeatmapLanguage.Japanese)]
        [TestCase("LANG:JP", BeatmapLanguage.Japanese)]
        [TestCase("language:en", BeatmapLanguage.English)]
        [TestCase("lang:\"ko\"", BeatmapLanguage.Korean)]
        [TestCase("lang:none", BeatmapLanguage.Instrumental)]
        [TestCase("lang:various", BeatmapLanguage.Other)]
        public void MatchesExactlyThatLanguage(string query, BeatmapLanguage expected)
        {
            var criteria = parse(query);

            Assert.That(criteria.SearchText?.Trim(), Is.Null.Or.Empty, $"the game did not parse {query}");

            foreach (var language in System.Enum.GetValues<BeatmapLanguage>())
                Assert.That(matches(criteria, language), Is.EqualTo(language == expected), $"{query} on {language}");
        }

        [TestCase("lang!=japanese")]
        [TestCase("lang!:japanese")]
        [TestCase("lang>japanese")]
        [TestCase("lang:klingon")]
        [TestCase("lang:unspecified")]
        [TestCase("lang:2")]
        [TestCase("lang:")]
        [TestCase("lang:\"japanese\"!")]
        [TestCase("lang:english,japanese")]
        [TestCase("lang:korean,english")]
        public void AnythingElseStaysSearchText(string query)
        {
            var criteria = parse(query);

            Assert.That(criteria.SearchText.Trim(), Is.EqualTo(query));
            Assert.That(criteria.Languages, Is.Empty);
        }

        [Test]
        public void TwoDifferentLanguagesMatchNothing()
        {
            var criteria = parse("lang:en lang:jp");

            foreach (var language in System.Enum.GetValues<BeatmapLanguage>())
                Assert.That(matches(criteria, language), Is.False, language.ToString());
        }

        private static FilterCriteria parse(string query)
        {
            var criteria = new FilterCriteria();
            FilterQueryParser.ApplyQueries(criteria, query);
            return criteria;
        }

        private static bool matches(FilterCriteria criteria, BeatmapLanguage language)
            => BeatmapCarouselFilterMatching.CheckCriteriaMatch(new BeatmapInfo { Metadata = { Language = language } }, criteria);
    }
}
