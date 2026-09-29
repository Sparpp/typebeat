// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Screens.Select;
using typebeat.Game.Utils;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 342: song select's <c>stars=</c> compares the rating floored to two decimals, and 4.1 * 100 is
    /// 409.99999999999994 in doubles, so a map rated exactly 4.10 floored to 4.09, matched nothing under
    /// <c>stars=4.1</c> and was DISPLAYED as 4.09. <see cref="FormatUtils.FLOOR_EPSILON"/> fixes the floor both
    /// paths share. The site's search mirrors it and WireCompat's <c>SearchOperatorParityTest</c> pins the pair.
    /// </summary>
    [TestFixture]
    public class StarFloorFilterTest
    {
        [TestCase("stars=4.1", 4.1, true)]
        [TestCase("stars=4.10", 4.1, true)]
        [TestCase("stars=4.09", 4.1, false)]
        [TestCase("stars!=4.1", 4.1, false)]
        [TestCase("stars=4.1", 4.1049, true)]
        [TestCase("stars=4.1", 4.09999999999999, true)] // 1e-14 under: float-noise distance, lifted
        [TestCase("stars=4.1", 4.0999999999, false)] // 1e-10 under: past the 1e-11 reach, still a 4.09
        [TestCase("stars=4.09", 4.0999999999, true)]
        [TestCase("stars=0.29", 0.29, true)]
        [TestCase("stars=0.57", 0.57, true)]
        [TestCase("stars=4.07", 4.0712, true)]
        [TestCase("stars=4", 3.9999999, false)]
        public void CarouselMatcher_StarEquality(string query, double rating, bool expected)
            => Assert.That(matches(query, rating), Is.EqualTo(expected), $"{query} on {rating.ToString("R", CultureInfo.InvariantCulture)}");

        [Test]
        public void EveryExactHundredth_IsMatchedByItsOwnOperand_AndDisplaysAsItself()
        {
            var failures = Enumerable.Range(0, 1001).SelectMany(k =>
            {
                double rating = k / 100.0;
                string operand = rating.ToString("0.00", CultureInfo.InvariantCulture);
                var problems = new List<string>();

                if (!matches("stars=" + operand, rating))
                    problems.Add($"stars={operand} misses {operand}");
                if (rating.FloorToDecimalDigits(2) != rating)
                    problems.Add($"{operand} floors to {rating.FloorToDecimalDigits(2).ToString("R", CultureInfo.InvariantCulture)}");
                if (rating.FormatStarRating().ToString() != operand)
                    problems.Add($"{operand} displays as {rating.FormatStarRating()}");

                return problems;
            }).ToList();

            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
        }

        [Test]
        public void TheFloorStillNeverRoundsUpARealShortfall()
        {
            Assert.Multiple(() =>
            {
                // FormatStarRating's own rule: a 6.9999 must never read as 7.00.
                Assert.That(6.9999.FormatStarRating().ToString(), Is.EqualTo("6.99"));
                Assert.That(4.0999999.FloorToDecimalDigits(2), Is.EqualTo(4.09));
                Assert.That(0.899999.FloorToDecimalDigits(4), Is.EqualTo(0.8999));

                // And the accuracy caller: a ratio exactly on a hundredth of a percent reads as itself.
                Assert.That((9.0 / 10).FloorToDecimalDigits(4), Is.EqualTo(0.9));
                Assert.That((1.0 / 3).FloorToDecimalDigits(4), Is.EqualTo(0.3333));
            });
        }

        private static bool matches(string query, double rating)
        {
            var criteria = new FilterCriteria();
            FilterQueryParser.ApplyQueries(criteria, query);
            Assert.That(criteria.SearchText?.Trim(), Is.Null.Or.Empty, $"the game did not parse {query}");

            return BeatmapCarouselFilterMatching.CheckCriteriaMatch(new BeatmapInfo { StarRating = rating }, criteria);
        }
    }
}
