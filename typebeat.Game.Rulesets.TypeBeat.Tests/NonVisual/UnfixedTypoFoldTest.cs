// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 213: an UNCORRECTED TYPO is a MISS.
//
// A cell sealing while it still holds a wrong character resolves as TypeBeatResultMapping
// .UNFIXED_TYPO, the "good" key. Backlog 124 gave it that key so that a cell the player FINISHED
// wrongly could be told apart from one the line ran out of time on, and priced the difference: 50 of
// 300 in accuracy, and pp's typo term rather than its miss term. The field report that ended that
// reading was a stored score reading MISS 0 while carrying good: 2, i.e. two characters the player
// never typed right appearing in no column at all and costing half of what dropping them would.
//
// The fold is ONE-SIDED, and that is its whole shape: the WIRE does not move. The seal still writes
// the same key, so no realm, MessagePack or submission shape changes, stored rows stay comparable
// with new ones, and the typo-versus-timeout distinction survives in the data. Every CONSUMER
// reclassifies instead, which is the pattern backlog 140 used for the mistype's combo_break key:
//
//   accuracy   0 of 300 rather than 50 (UnfixedTypoWorthRule, era-gated so a stored row re-derives)
//   pp         misses = miss + good, typos = max(0, combo_break - good), notes untouched
//   display    the MISS column counts it (TypeBeatRuleset.GetDisplayResultFor)
//
// What the fold deliberately does NOT touch, because each was already right: COMPLETION and
// therefore RANK, which have counted an unfixed typo as untyped since backlog 126; HEALTH, which has
// drained it as a miss since backlog 125; and the combo-restore mechanics, though the incentive
// sharpens, since fixing a typo now recovers a full miss's worth of accuracy.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Ranking.Statistics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class UnfixedTypoFoldTest
    {
        private static ScoreInfo scored(int great, int unfixedTypos, int misses, int? mistypes = null)
        {
            var statistics = new Dictionary<HitResult, int> { [HitResult.Great] = great };

            if (unfixedTypos > 0)
                statistics[TypeBeatResultMapping.UNFIXED_TYPO] = unfixedTypos;

            if (misses > 0)
                statistics[HitResult.Miss] = misses;

            if (mistypes != null)
                statistics[TypeBeatScoreProcessor.MISTYPE_RESULT] = mistypes.Value;

            return new ScoreInfo
            {
                Ruleset = new TypeBeatRuleset().RulesetInfo,
                Statistics = statistics,
                MaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = great + unfixedTypos + misses },
            };
        }

        // -----------------------------------------------------------------------------------------
        // The wire does not move.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The load-bearing precondition for everything else here: the SEAL is untouched, so a cell
        /// left holding a wrong character still resolves as <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>
        /// and a cell nobody typed still resolves as a <see cref="TypeBeatResultMapping.SEAL_MISS"/>.
        /// Had the fold been done here instead, old rows and new ones would stop meaning the same
        /// thing and the distinction would be gone from the data as well as from the pricing.
        /// </summary>
        [Test]
        public void TheSealStillWritesTheTypoUnderItsOwnKey()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatResultMapping.UnresolvedCellResult(leftWrong: true, TypoRule.Deferred),
                    Is.EqualTo(TypeBeatResultMapping.UNFIXED_TYPO));
                Assert.That(TypeBeatResultMapping.UnresolvedCellResult(leftWrong: false, TypoRule.Deferred),
                    Is.EqualTo(TypeBeatResultMapping.SEAL_MISS));

                Assert.That(TypeBeatResultMapping.UNFIXED_TYPO, Is.Not.EqualTo(TypeBeatResultMapping.SEAL_MISS),
                    "the two keys must stay distinct: the fold is on the READING, not on the storage");
            });
        }

        // -----------------------------------------------------------------------------------------
        // Accuracy: 0 of 300, era-gated.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The accuracy weight table under both arms of <see cref="UnfixedTypoWorthRule"/>. Only the
        /// typo key moves, and it moves from Meh's 50 to Miss's 0; the four ordinary results are
        /// identical under both, which is what makes the arm safe to set unconditionally.
        /// </summary>
        [Test]
        public void TheWeightTableMovesOnlyTheTypoKey()
        {
            var live = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            var stored = new TypeBeatScoreProcessor(new TypeBeatRuleset()) { UnfixedTypoWorth = UnfixedTypoWorthRule.MehCredit };

            Assert.Multiple(() =>
            {
                Assert.That(live.UnfixedTypoWorth, Is.EqualTo(UnfixedTypoWorthRule.Nothing),
                    "live play takes the processor's own default, exactly as it does for the engine's era switches");

                Assert.That(live.GetBaseScoreForResult(TypeBeatResultMapping.UNFIXED_TYPO), Is.Zero);
                Assert.That(stored.GetBaseScoreForResult(TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(50));

                foreach (var result in new[] { HitResult.Great, HitResult.Ok, HitResult.Meh, HitResult.Miss })
                {
                    Assert.That(live.GetBaseScoreForResult(result), Is.EqualTo(stored.GetBaseScoreForResult(result)),
                        $"{result} must be untouched by the arm");
                }

                Assert.That(live.GetBaseScoreForResult(HitResult.Great), Is.EqualTo(300));
                Assert.That(live.GetBaseScoreForResult(HitResult.Ok), Is.EqualTo(100));
                Assert.That(live.GetBaseScoreForResult(HitResult.Meh), Is.EqualTo(50));
                Assert.That(live.GetBaseScoreForResult(HitResult.Miss), Is.Zero);

                // The DENOMINATOR is the cell's MAXIMUM result, which is a Great whatever the cell
                // resolved as. That is what makes accuracy genuinely fall: the cell stays in the
                // fraction and pays 0 of 300, rather than quietly leaving it.
                Assert.That(live.GetBaseScoreForResult(HitResult.Great), Is.EqualTo(300));
            });
        }

        // -----------------------------------------------------------------------------------------
        // pp: one flub, one term.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The pp derivation, on a play carrying both kinds of flub. <c>misses</c> gains the
        /// uncorrected cells and <c>typos</c> loses the keypresses that produced them, so the
        /// keypresses left in the typo term are exactly the ones the player CORRECTED. <c>notes</c>
        /// is untouched: an uncorrected typo is still one cell of the map.
        /// </summary>
        [Test]
        public void PpPricesAnUncorrectedTypoByTheMissTermAndTakesItOutOfTheTypoTerm()
        {
            var counts = PerformancePoints.CountNotes(new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 300,
                [HitResult.Ok] = 40,
                [HitResult.Meh] = 10,
                [TypeBeatResultMapping.UNFIXED_TYPO] = 7,
                [HitResult.Miss] = 43,
                [PerformancePoints.MISTYPE_RESULT] = 20,
            });

            Assert.Multiple(() =>
            {
                Assert.That(counts.Notes, Is.EqualTo(400), "the typo cells stay in the note count");
                Assert.That(counts.Misses, Is.EqualTo(50), "43 cells nobody typed plus 7 left holding a wrong character");
                Assert.That(counts.Typos, Is.EqualTo(13), "20 wrong keypresses, 7 of which were never corrected");

                // The alias must not be a second definition of the key.
                Assert.That(PerformancePoints.UNFIXED_TYPO_RESULT, Is.EqualTo(TypeBeatResultMapping.UNFIXED_TYPO));
                Assert.That(PerformancePoints.NOTE_RESULTS, Does.Contain(PerformancePoints.UNFIXED_TYPO_RESULT));
            });
        }

        /// <summary>
        /// The fold stated as an equality: a cell left holding a wrong character prices EXACTLY as a
        /// cell nobody typed. Both dictionaries describe a 400-cell map with one character missing;
        /// one stores that cell as a miss and the other as the typo it was, with the keypress it
        /// took. Since backlog 213 pp cannot tell them apart, which is what "an uncorrected typo is a
        /// miss" means at the formula.
        /// </summary>
        [Test]
        public void AnUncorrectedTypoPricesIdenticallyToADroppedCell()
        {
            var dropped = PerformancePoints.CountNotes(new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 399,
                [HitResult.Miss] = 1,
            });

            var leftWrong = PerformancePoints.CountNotes(new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 399,
                [TypeBeatResultMapping.UNFIXED_TYPO] = 1,
                [PerformancePoints.MISTYPE_RESULT] = 1,
            });

            Assert.Multiple(() =>
            {
                Assert.That(leftWrong, Is.EqualTo(dropped));
                Assert.That(PerformancePoints.ForPlay(4.2, leftWrong, 0.9, 380, null),
                    Is.EqualTo(PerformancePoints.ForPlay(4.2, dropped, 0.9, 380, null)));
            });
        }

        /// <summary>
        /// NO DOUBLE JEOPARDY, from the other side: a typo the player FIXED stays a typo event and
        /// nothing subtracts it, because its cell resolved as an ordinary hit and never reached the
        /// typo key at all. So the two shapes are priced differently, which is the incentive the
        /// whole change rests on.
        /// </summary>
        [Test]
        public void ACorrectedTypoIsStillPricedByTheTypoTerm()
        {
            var corrected = PerformancePoints.CountNotes(new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 399,
                [HitResult.Ok] = 1,
                [PerformancePoints.MISTYPE_RESULT] = 1,
            });

            Assert.Multiple(() =>
            {
                Assert.That(corrected.Notes, Is.EqualTo(400));
                Assert.That(corrected.Misses, Is.Zero);
                Assert.That(corrected.Typos, Is.EqualTo(1));

                // ...and it is worth strictly more than leaving the same flub standing, because the
                // typo term is exponent 4 where cleanliness is 10.
                var leftWrong = PerformancePoints.CountNotes(new Dictionary<HitResult, int>
                {
                    [HitResult.Great] = 399,
                    [TypeBeatResultMapping.UNFIXED_TYPO] = 1,
                    [PerformancePoints.MISTYPE_RESULT] = 1,
                });

                Assert.That(PerformancePoints.ForPlay(4.2, corrected, 0.99, 400, null),
                    Is.GreaterThan(PerformancePoints.ForPlay(4.2, leftWrong, 0.99, 400, null)));
            });
        }

        /// <summary>
        /// The clamp on the typo subtraction, which is load-bearing rather than defensive: the two
        /// counts arrive off the wire independently. A row stored before backlog 72 carries no
        /// <c>combo_break</c> key at all while carrying <c>good</c> cells, and a tamper-shaped
        /// dictionary can say anything; a negative typo count would go into
        /// <c>Math.Pow(typos, count_power)</c> under a FRACTIONAL power and come back NaN.
        /// </summary>
        [TestCase(0, 5, 0, TestName = "TheTypoSubtractionIsClamped(a pre-backlog-72 row with no combo_break key)")]
        [TestCase(3, 5, 0, TestName = "TheTypoSubtractionIsClamped(fewer keypresses stored than typo cells)")]
        [TestCase(5, 5, 0, TestName = "TheTypoSubtractionIsClamped(every keypress went uncorrected)")]
        [TestCase(9, 5, 4, TestName = "TheTypoSubtractionIsClamped(four of the nine were corrected)")]
        public void TheTypoSubtractionIsClamped(int mistypes, int unfixedTypos, int expectedTypos)
        {
            var statistics = new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 100,
                [TypeBeatResultMapping.UNFIXED_TYPO] = unfixedTypos,
            };

            if (mistypes > 0)
                statistics[PerformancePoints.MISTYPE_RESULT] = mistypes;

            var counts = PerformancePoints.CountNotes(statistics);

            Assert.Multiple(() =>
            {
                Assert.That(counts.Typos, Is.EqualTo(expectedTypos));
                Assert.That(counts.Typos, Is.GreaterThanOrEqualTo(0));
                Assert.That(counts.Misses, Is.EqualTo(unfixedTypos));
                Assert.That(counts.Notes, Is.EqualTo(100 + unfixedTypos));

                // The whole point of the clamp: a negative count would make this non-finite.
                Assert.That(PerformancePoints.ForPlay(4.2, counts, 0.9, 90, null), Is.GreaterThanOrEqualTo(0));
            });
        }

        // -----------------------------------------------------------------------------------------
        // Display: the miss column, and nothing of its own.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The display fold, on the very enumeration every score-statistics surface reads
        /// (<see cref="ScoreInfo.GetStatisticsForDisplay"/>: the results panels, the leaderboard
        /// tooltips and the beatmap-set score table). The typo takes no column of its own, its count
        /// lands in MISS, and the shown columns SUM to the judged cell count, which they had not done
        /// since backlog 124 gave the typo a key nothing displayed.
        ///
        /// <para>The fixture is the field report's shape: a play whose stored row carries misses 0
        /// and good 2, which used to read MISS 0 with two characters unaccounted for.</para>
        /// </summary>
        [Test]
        public void TheMissColumnCountsUncorrectedTyposToo()
        {
            var shown = scored(great: 98, unfixedTypos: 2, misses: 0, mistypes: 5).GetStatisticsForDisplay().ToList();

            Assert.Multiple(() =>
            {
                Assert.That(shown.Select(s => s.Result),
                    Is.EqualTo(new[] { HitResult.Great, HitResult.Ok, HitResult.Meh, HitResult.Miss }),
                    "the typo must not take a column of its own, or the fold would double-count it");

                Assert.That(shown.Single(s => s.Result == HitResult.Miss).Count, Is.EqualTo(2));
                Assert.That(shown.Single(s => s.Result == HitResult.Great).Count, Is.EqualTo(98));

                // The property the fold buys: nothing judged is invisible any more.
                Assert.That(shown.Sum(s => s.Count), Is.EqualTo(100));
            });
        }

        /// <summary>
        /// The same fold on a play carrying BOTH kinds, so the column is a sum and not a
        /// substitution, and the identity on everything else.
        /// </summary>
        [Test]
        public void TheMissColumnIsTheSumOfBothKinds()
        {
            var shown = scored(great: 90, unfixedTypos: 3, misses: 7).GetStatisticsForDisplay().ToList();
            var ruleset = new TypeBeatRuleset();

            Assert.Multiple(() =>
            {
                Assert.That(shown.Single(s => s.Result == HitResult.Miss).Count, Is.EqualTo(10));
                Assert.That(shown.Sum(s => s.Count), Is.EqualTo(100));

                Assert.That(ruleset.GetDisplayResultFor(TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(HitResult.Miss));

                foreach (var result in new[] { HitResult.Great, HitResult.Ok, HitResult.Meh, HitResult.Miss, TypeBeatScoreProcessor.MISTYPE_RESULT })
                    Assert.That(ruleset.GetDisplayResultFor(result), Is.EqualTo(result), $"{result} must display as itself");

                // Absent from the valid results is what stops the fold double-counting, so it is
                // pinned here as well as in MistypeStatTest, which pins it for backlog 140's reason.
                Assert.That(ruleset.GetValidHitResults(), Does.Not.Contain(TypeBeatResultMapping.UNFIXED_TYPO));
            });
        }

        /// <summary>
        /// The fold reaches OLD rows and new ones alike, which is the point of doing it on the
        /// reading rather than at the seal: the data did not move, so a row stored years ago is
        /// re-read under today's rule with no migration at all.
        /// </summary>
        [Test]
        public void TheResultsScreenMissedCharacterRowCountsUncorrectedTypos()
        {
            var rows = TypeBeatRuleset.CreateCompletionStatistics(scored(great: 90, unfixedTypos: 3, misses: 7, mistypes: 4));
            var missed = rows.OfType<SimpleStatisticItem<int>>().Single(r => r.Name == "Missed characters");
            var typos = rows.OfType<SimpleStatisticItem<int>>().Single(r => r.Name == "Typos");

            Assert.Multiple(() =>
            {
                Assert.That(missed.Value, Is.EqualTo(10), "7 dropped plus 3 left holding a wrong character");

                // TYPOS is untouched, and is a different statement: it counts wrong KEYPRESSES as
                // events, including the ones the player went back and fixed, where the row above
                // counts CELLS.
                Assert.That(typos.Value, Is.EqualTo(4));
            });
        }

        // -----------------------------------------------------------------------------------------
        // Unchanged by design.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// COMPLETION, and therefore RANK, do not move: backlog 126 already excluded an unfixed typo
        /// from completion's numerator, so the fold found this half done. Pinned because "grades are
        /// untouched, and accuracy-derived surfaces drop naturally" is a claim about this file's
        /// change that only a test can hold.
        /// </summary>
        [Test]
        public void CompletionAndRankAreUntouchedByTheFold()
        {
            var score = scored(great: 90, unfixedTypos: 3, misses: 7);

            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatScoreProcessor.CountsAsTyped(TypeBeatResultMapping.UNFIXED_TYPO), Is.False);
                Assert.That(TypeBeatScoreProcessor.CountsAsTyped(HitResult.Miss), Is.False);

                Assert.That(TypeBeatScoreProcessor.ComputeCompletion(score), Is.EqualTo(0.9).Within(1e-12));
                Assert.That(TypeBeatScoreProcessor.RankFromCompletion(TypeBeatScoreProcessor.ComputeCompletion(score)),
                    Is.EqualTo(ScoreRank.A));

                // The same play with the three typos stored as misses instead: identical, because
                // completion has never distinguished them.
                Assert.That(TypeBeatScoreProcessor.ComputeCompletion(scored(great: 90, unfixedTypos: 0, misses: 10)),
                    Is.EqualTo(TypeBeatScoreProcessor.ComputeCompletion(score)).Within(1e-12));
            });
        }
    }
}
