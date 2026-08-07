// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 74: the pp formula, priced CLIENT-SIDE.
//
// These pin the client half of a mirrored pair. The other half is the server's
// Typebeat.Web.Scoring.PerformancePoints, which is where a play's STORED pp actually comes from,
// and the two are held together by typebeat-web/tests/Typebeat.WireCompat's parity test (the only
// project that compiles both repos). This file exists so the client half fails on its own, in this
// repo's own gate, rather than only in the other repo's.
//
// Every expected value below is written as the formula spells it out, EXCEPT the handful of
// independently-computed reference numbers, which are there to catch a plausible-looking but wrong
// refactor of the formula itself. Those numbers are deliberately the SAME literals the server's
// PerformancePointsTest uses.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class PerformancePointsTest
    {
        private static readonly IReadOnlyList<Mod> no_mods = Array.Empty<Mod>();

        private static IReadOnlyList<Mod> mods(params Mod[] stack) => stack;

        /// <summary>A rate mod dialled to a specific speed (the slider snaps to 0.01).</summary>
        private static T at<T>(T mod, double rate) where T : ModRateAdjust
        {
            mod.SpeedChange.Value = rate;
            return mod;
        }

        /// <summary>A clean-ish reference play: 4 stars, 500 notes, no misses, 90% acc, full combo.</summary>
        private const double reference_pp = 219.337706;

        [Test]
        public void Compute_MatchesAnIndependentlyEvaluatedReferencePlay()
        {
            double pp = PerformancePoints.Compute(starRating: 4, notes: 500, misses: 0, accuracy: 0.9, maxCombo: 500, no_mods);

            Assert.That(pp, Is.EqualTo(reference_pp).Within(1e-5));
        }

        #region Length bonus

        // The raw term crosses ZERO at ~3.73 notes and the floor at ~5.18, so the clamp is what
        // stops a degenerate map computing zero or negative pp from its length alone.

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(3)]
        [TestCase(4)] // just past the zero crossing, but still far under the floor
        [TestCase(5)]
        public void LengthBonus_ClampsToTheFloorWhereTheRawTermWouldSinkBelowIt(int notes)
        {
            double raw = 1 + 0.70 * Math.Log10(Math.Max(notes, 1) / 100.0);

            Assert.That(PerformancePoints.LengthBonus(notes), Is.EqualTo(0.1).Within(1e-12),
                $"raw term at {notes} notes is {raw:0.####}");
        }

        [TestCase(6, 0.144706)]
        [TestCase(100, 1.0)]
        [TestCase(500, 1.489279)]
        [TestCase(1000, 1.7)]
        public void LengthBonus_IsTheLogBonusAboveTheFloor(int notes, double expected)
            => Assert.That(PerformancePoints.LengthBonus(notes), Is.EqualTo(expected).Within(1e-6));

        #endregion

        #region Flashlight

        // Unclamped the raw term dips BELOW 1.0 under ~46 notes, which would make a bonus mod a
        // penalty on short maps.

        [TestCase(1)]
        [TestCase(20)]
        [TestCase(45)]
        [TestCase(46)]
        public void FlashlightMultiplier_ClampsToOneOnShortMaps(int notes)
        {
            double raw = 1 + 0.02 + 0.06 * Math.Log10(notes / 100.0);

            Assert.Multiple(() =>
            {
                Assert.That(raw, Is.LessThan(1.0), "the raw term is below 1 here, which is what the clamp is for");
                Assert.That(PerformancePoints.FlashlightMultiplier(notes), Is.EqualTo(1.0).Within(1e-12));
            });
        }

        [TestCase(47, 1.000326)]
        [TestCase(100, 1.02)]
        [TestCase(500, 1.061938)]
        public void FlashlightMultiplier_GrowsWithLengthOnceAboveTheFloor(int notes, double expected)
            => Assert.That(PerformancePoints.FlashlightMultiplier(notes), Is.EqualTo(expected).Within(1e-6));

        #endregion

        #region Cleanliness

        [Test]
        public void Compute_GiveUpRunCollapsesTowardsZero()
        {
            // 1000 notes on a 4-star map, 900 of them missed: exactly the shape the miss term exists
            // to kill. It must not merely be "smaller", it must be negligible next to a clean play.
            double giveUp = PerformancePoints.Compute(4, notes: 1000, misses: 900, accuracy: 0.1, maxCombo: 10, no_mods);

            Assert.Multiple(() =>
            {
                Assert.That(giveUp, Is.GreaterThanOrEqualTo(0));
                Assert.That(giveUp, Is.LessThan(0.001));
            });
        }

        [Test]
        public void Compute_MissingEveryNoteIsExactlyZero()
            => Assert.That(PerformancePoints.Compute(6, notes: 400, misses: 400, accuracy: 0, maxCombo: 0, no_mods), Is.Zero);

        [Test]
        public void Compute_MissesDominateAccuracyAndCombo()
        {
            // Same map, same length. A sloppy-but-complete play beats a high-accuracy play that
            // dropped 10% of the map, which is the whole point of the 8.5 exponent.
            double sloppyButClean = PerformancePoints.Compute(4, 500, misses: 0, accuracy: 0.60, maxCombo: 500, no_mods);
            double accurateButMissy = PerformancePoints.Compute(4, 500, misses: 50, accuracy: 0.93, maxCombo: 450, no_mods);

            Assert.That(sloppyButClean, Is.GreaterThan(accurateButMissy));
        }

        #endregion

        #region Degenerate inputs: never NaN, never Infinity, never negative

        [Test]
        public void Compute_DegenerateInputsAreFiniteAndNonNegative()
        {
            double[] stars = { 0, -1, 0.0001, 10, double.NaN, double.PositiveInfinity };
            int[] noteCounts = { 0, 1, 2, 4, 100 };
            double[] accuracies = { 0, 0.5, 1, -1, 2, double.NaN };

            foreach (double sr in stars)
            foreach (int notes in noteCounts)
            foreach (double acc in accuracies)
            {
                // Combo and misses deliberately out of range in both directions.
                foreach (int misses in new[] { -5, 0, notes, notes + 7 })
                foreach (int combo in new[] { -3, 0, notes, notes + 9 })
                {
                    double pp = PerformancePoints.Compute(sr, notes, misses, acc, combo, no_mods);

                    Assert.That(pp, Is.Not.NaN, $"sr={sr} notes={notes} miss={misses} acc={acc} combo={combo}");
                    Assert.That(double.IsFinite(pp), Is.True, $"sr={sr} notes={notes} miss={misses} acc={acc} combo={combo}");
                    Assert.That(pp, Is.GreaterThanOrEqualTo(0), $"sr={sr} notes={notes} miss={misses} acc={acc} combo={combo}");
                }
            }
        }

        [Test]
        public void Compute_ZeroNotesEarnsNothing()
            => Assert.That(PerformancePoints.Compute(5, notes: 0, misses: 0, accuracy: 1, maxCombo: 0, no_mods), Is.Zero);

        [Test]
        public void Compute_OneNoteIsTinyButPositive()
        {
            // A single perfect note on a 5-star map: the length floor is what keeps this finite and
            // positive rather than zero or negative.
            double pp = PerformancePoints.Compute(5, notes: 1, misses: 0, accuracy: 1, maxCombo: 1, no_mods);

            Assert.Multiple(() =>
            {
                Assert.That(pp, Is.EqualTo(30.851693).Within(1e-5));
                Assert.That(pp, Is.LessThan(reference_pp));
            });
        }

        [Test]
        public void Compute_ClampsAComboAboveTheNoteCountRatherThanRewardingIt()
        {
            double honest = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods);
            double tampered = PerformancePoints.Compute(4, 500, 0, 0.9, 5000, no_mods);

            Assert.That(tampered, Is.EqualTo(honest).Within(1e-9));
        }

        [Test]
        public void Compute_TheMistypingTermStaysInRangeForAnyMistypeCount()
        {
            // Mistypes sit on BOTH sides of the MISTYPING fraction, so its base approaches 0 but
            // never goes negative, however absurd the keypress count. An absurd count must decay pp
            // towards zero, never produce a negative base, a NaN, or (with a fractional exponent on
            // a negative base) an imaginary result. int.MaxValue is in the sweep because
            // notes + mistypes would OVERFLOW an int there and flip the ratio's sign, which is why
            // the implementation takes that sum in double.
            foreach (int notes in new[] { 1, 10, 500 })
            foreach (int misses in new[] { 0, notes / 2, notes })
            foreach (int mistypes in new[] { -1, 0, 1, notes * 10, notes * 1000, int.MaxValue })
            {
                double pp = PerformancePoints.Compute(6, notes, misses, 0.9, notes, no_mods, mistypes);

                Assert.That(pp, Is.Not.NaN, $"notes={notes} miss={misses} mistypes={mistypes}");
                Assert.That(double.IsFinite(pp), Is.True, $"notes={notes} miss={misses} mistypes={mistypes}");
                Assert.That(pp, Is.GreaterThanOrEqualTo(0), $"notes={notes} miss={misses} mistypes={mistypes}");
                Assert.That(pp, Is.LessThan(reference_pp * 10), $"notes={notes} miss={misses} mistypes={mistypes}");
            }

            // Ten times the note count, spelled out: still a penalty, still a real number, and it
            // must land strictly between "free" and "nothing".
            double absurd = penaltyFactor(500, 0, 5000);

            Assert.Multiple(() =>
            {
                Assert.That(absurd, Is.GreaterThan(0));
                Assert.That(absurd, Is.LessThan(0.01));
                Assert.That(absurd, Is.EqualTo(Math.Pow(500.0 / 5500.0, 3.5)).Within(1e-12));
            });
        }

        #endregion

        #region CountNotes: notes = great + ok + meh + miss, EXCLUDING ignore_hit

        [Test]
        public void CountNotes_ExcludesLineContainersAndBonuses()
        {
            var statistics = new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 300,
                [HitResult.Ok] = 40,
                [HitResult.Meh] = 10,
                [HitResult.Miss] = 50,
                // The line containers. Counting these would inflate notes by 12 and dilute every factor.
                [HitResult.IgnoreHit] = 12,
                [HitResult.IgnoreMiss] = 3,
                // Not a typing-map judgement; still must not be counted as a note.
                [HitResult.LargeBonus] = 7,
            };

            var counts = PerformancePoints.CountNotes(statistics);

            Assert.Multiple(() =>
            {
                Assert.That(counts.Notes, Is.EqualTo(400));
                Assert.That(counts.Misses, Is.EqualTo(50));
                Assert.That(counts.Mistypes, Is.Zero);
            });
        }

        [Test]
        public void CountNotes_ReadsMistypesFromTheComboBreakResultWithoutCountingThemAsNotes()
        {
            var counts = PerformancePoints.CountNotes(new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 300,
                [HitResult.Ok] = 40,
                [HitResult.Meh] = 10,
                [HitResult.Miss] = 50,
                [PerformancePoints.MISTYPE_RESULT] = 137,
            });

            Assert.Multiple(() =>
            {
                // notes stays the map's CELL count. Letting keypresses in would inflate the LENGTH
                // bonus and shrink the COMBO denominator, paying a masher twice for mashing.
                Assert.That(counts.Notes, Is.EqualTo(400));
                Assert.That(counts.Misses, Is.EqualTo(50));
                Assert.That(counts.Mistypes, Is.EqualTo(137));

                // The stat's home is the score processor's; this must not be a second definition.
                Assert.That(PerformancePoints.MISTYPE_RESULT, Is.EqualTo(TypeBeatScoreProcessor.MISTYPE_RESULT));
            });
        }

        [Test]
        public void CountNotes_AnAbsentMistypeKeyReadsAsZero()
            => Assert.That(PerformancePoints.CountNotes(new Dictionary<HitResult, int> { [HitResult.Great] = 100 }).Mistypes, Is.Zero);

        [Test]
        public void CountNotes_NegativeCountsContributeNothing()
        {
            var counts = PerformancePoints.CountNotes(new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 100,
                [HitResult.Miss] = -50,
                [PerformancePoints.MISTYPE_RESULT] = -7,
            });

            Assert.Multiple(() =>
            {
                Assert.That(counts.Notes, Is.EqualTo(100));
                Assert.That(counts.Misses, Is.Zero);
                Assert.That(counts.Mistypes, Is.Zero);
            });
        }

        [Test]
        public void CountNotes_ReadsAFinishedScoreIdenticallyToLiveStatistics()
        {
            var statistics = new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 300,
                [HitResult.Miss] = 50,
                [PerformancePoints.MISTYPE_RESULT] = 9,
            };

            Assert.That(PerformancePoints.CountNotes(new ScoreInfo { Statistics = statistics }),
                Is.EqualTo(PerformancePoints.CountNotes(statistics)));
        }

        [Test]
        public void Compute_IgnoreHitInflationWouldChangeTheAnswer()
        {
            // The reason CountNotes has to exclude it. Line containers are one ignore_hit per LINE,
            // so a 400-note map with 60 lines would read as 460 "notes". Three of the six factors
            // take the note count as a denominator, and the most visible casualty is the combo term:
            // a genuine full combo would stop reading as one.
            double fullCombo = PerformancePoints.Compute(4, 400, 0, 0.85, 400, no_mods);
            double inflated = PerformancePoints.Compute(4, 460, 0, 0.85, 400, no_mods);

            Assert.Multiple(() =>
            {
                Assert.That(inflated, Is.LessThan(fullCombo));
                Assert.That((fullCombo - inflated) / fullCombo, Is.GreaterThan(0.03),
                    "counting the line containers would cost a full combo several percent of its pp");
            });
        }

        #endregion

        #region Mistype pricing (backlog 72, rebalanced by backlog 89)

        /// <summary>
        /// The two penalty terms in isolation. Nothing else in the formula reads misses or mistypes,
        /// so dividing a play's pp by the pp of the same play with neither is EXACTLY
        /// <c>(1 - miss/notes)^8.5 * (1 - mistypes/(notes+mistypes))^3.5</c>, with every other factor
        /// cancelling. Every expected number below is that product.
        /// </summary>
        private static double penaltyFactor(int notes, int misses, int mistypes)
        {
            double spotless = PerformancePoints.Compute(4, notes, 0, 0.9, notes, no_mods, mistypes: 0);

            return PerformancePoints.Compute(4, notes, misses, 0.9, notes, no_mods, mistypes) / spotless;
        }

        [Test]
        public void Compute_ReproducesTheDecidedRebalanceWorkedExamples()
        {
            // The two cases the rebalance (backlog 89) was signed off on, stated as exact values.
            // Both are WORTH MORE than under the old combined term, which is the deliberate
            // consequence of splitting: pulling mistypes out of the miss ratio softens that term by
            // more than the 7.5-to-8.5 rise tightens it. The old values are quoted so the direction
            // is unmistakable. Deliberately the same literals the server's PerformancePointsTest
            // uses.
            Assert.Multiple(() =>
            {
                // 0.880^8.5 * 0.862^3.5, against 0.759^7.5 = 0.125946 before.
                Assert.That(penaltyFactor(notes: 500, misses: 60, mistypes: 80), Is.EqualTo(0.200678).Within(1e-6));

                // 0.980^8.5 * 0.962^3.5, against 0.942^7.5 = 0.640391 before.
                Assert.That(penaltyFactor(notes: 500, misses: 10, mistypes: 20), Is.EqualTo(0.734184).Within(1e-6));
            });
        }

        [Test]
        public void Compute_ZeroMistypesLeavesThePlayPricedByItsMissesAlone()
        {
            // The property that makes the split legible: at zero mistypes the mistyping term is
            // EXACTLY 1.0, so the whole penalty is (1 - miss/notes)^8.5 and nothing else.
            foreach (int misses in new[] { 0, 1, 17, 250, 500 })
            {
                double withArgument = PerformancePoints.Compute(4.2, 500, misses, 0.87, 400, no_mods, mistypes: 0);
                double withoutArgument = PerformancePoints.Compute(4.2, 500, misses, 0.87, 400, no_mods);

                Assert.That(withArgument, Is.EqualTo(withoutArgument), $"misses={misses}");
                Assert.That(penaltyFactor(500, misses, 0), Is.EqualTo(Math.Pow(1.0 - misses / 500.0, 8.5)).Within(1e-12),
                    $"misses={misses}");
            }
        }

        [Test]
        public void Compute_PricesMissesAndMistypesIndependently()
        {
            // The whole point of the split. What a miss costs must not depend on the keypress count
            // and vice versa, so the penalty factorises: the RATIO between two miss counts is the
            // same whatever mistype count both carry. Under the old combined term it was not.
            foreach (int mistypes in new[] { 0, 20, 500, 5000 })
            {
                double clean = penaltyFactor(500, 0, mistypes);
                double missy = penaltyFactor(500, 60, mistypes);

                Assert.That(missy / clean, Is.EqualTo(Math.Pow(1.0 - 60 / 500.0, 8.5)).Within(1e-12),
                    $"the miss term must not be diluted by {mistypes} mistypes");
            }

            // And the mistyping term likewise, read across two miss counts.
            Assert.That(penaltyFactor(500, 60, 80) / penaltyFactor(500, 60, 0),
                Is.EqualTo(penaltyFactor(500, 0, 80)).Within(1e-12));
        }

        [Test]
        public void Compute_MistypesCostPpAndMonotonicallySo()
        {
            double clean = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, mistypes: 0);
            double few = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, mistypes: 10);
            double many = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, mistypes: 100);

            Assert.Multiple(() =>
            {
                Assert.That(few, Is.LessThan(clean), "this is the point of the stat: sloppy play stops farming pp");
                Assert.That(many, Is.LessThan(few));
                Assert.That(many, Is.GreaterThan(0));
            });
        }

        [Test]
        public void Compute_EachPenaltyIsMonotonicWhileTheOtherIsHeldFixed()
        {
            // Raising either count, with the other pinned, must move pp strictly DOWN. Both
            // directions, because the terms are now separate and either could be wired up backwards
            // on its own.
            foreach (int mistypes in new[] { 0, 137 })
            {
                double previous = double.MaxValue;

                foreach (int misses in new[] { 0, 1, 25, 100, 250, 499 })
                {
                    double pp = PerformancePoints.Compute(4, 500, misses, 0.9, 500, no_mods, mistypes);

                    Assert.That(pp, Is.LessThan(previous), $"misses={misses} at mistypes={mistypes}");
                    previous = pp;
                }
            }

            foreach (int misses in new[] { 0, 137 })
            {
                double previous = double.MaxValue;

                foreach (int mistypes in new[] { 0, 1, 25, 100, 500, 5000 })
                {
                    double pp = PerformancePoints.Compute(4, 500, misses, 0.9, 500, no_mods, mistypes);

                    Assert.That(pp, Is.LessThan(previous), $"mistypes={mistypes} at misses={misses}");
                    previous = pp;
                }
            }
        }

        #endregion

        #region Mod multipliers, driven by the REAL ruleset mods

        [Test]
        public void ModMultiplier_NoFailAndFletcherEachCostTenPercentAndLiterateAddsSix()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModNoFail()), 300), Is.EqualTo(0.90).Within(1e-12));
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModFletcher()), 300), Is.EqualTo(0.90).Within(1e-12));
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModLiterate()), 300), Is.EqualTo(1.06).Within(1e-12));
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModFlashlight()), 300),
                    Is.EqualTo(PerformancePoints.FlashlightMultiplier(300)).Within(1e-12));
            });
        }

        [Test]
        public void ModMultiplier_SuddenDeathMutedAndUnknownModsAreNeutral()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModSuddenDeath()), 300), Is.EqualTo(1.0).Within(1e-12));
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModMuted()), 300), Is.EqualTo(1.0).Within(1e-12));
                // A mod this table has not learned must never silently inflate or deflate a ranking.
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModMashing()), 300), Is.EqualTo(1.0).Within(1e-12));
            });
        }

        [Test]
        public void ModMultiplier_RateModsContributeNothing()
        {
            // The rate is priced through SR_eff alone; a flat DT/HT term here would double-count it.
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModDoubleTime()), 300), Is.EqualTo(1.0).Within(1e-12));
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModNightcore()), 300), Is.EqualTo(1.0).Within(1e-12));
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModHalfTime()), 300), Is.EqualTo(1.0).Within(1e-12));
                Assert.That(PerformancePoints.ModMultiplier(mods(at(new TypeBeatModDoubleTime(), 1.87)), 300), Is.EqualTo(1.0).Within(1e-12));
            });
        }

        [Test]
        public void ModMultiplier_StacksAndCollapsesDuplicates()
        {
            double stacked = PerformancePoints.ModMultiplier(
                mods(new TypeBeatModLiterate(), new TypeBeatModNoFail(), new TypeBeatModFlashlight()), 500);

            Assert.Multiple(() =>
            {
                Assert.That(stacked, Is.EqualTo(1.06 * 0.90 * PerformancePoints.FlashlightMultiplier(500)).Within(1e-12));

                // A duplicated acronym is tamper-shaped; it must be applied once, not squared.
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModNoFail(), new TypeBeatModNoFail()), 300),
                    Is.EqualTo(0.90).Within(1e-12));
            });
        }

        [Test]
        public void Compute_AppliesTheModMultiplierToTheWholeFormula()
        {
            double bare = PerformancePoints.Compute(3, 300, 5, 0.8, 250, no_mods);

            Assert.Multiple(() =>
            {
                Assert.That(bare, Is.EqualTo(60.794187).Within(1e-5));
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModNoFail())),
                    Is.EqualTo(bare * 0.90).Within(1e-9));
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModFletcher())),
                    Is.EqualTo(bare * 0.90).Within(1e-9));
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModLiterate())),
                    Is.EqualTo(bare * 1.06).Within(1e-9));
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModFlashlight())),
                    Is.EqualTo(bare * PerformancePoints.FlashlightMultiplier(300)).Within(1e-9));
            });
        }

        #endregion

        #region Rate eligibility: only the BASE rates earn pp

        [Test]
        public void EligibleRate_NoRateModIsPricedAtOne()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.EligibleRate(no_mods), Is.EqualTo(1.0));
                Assert.That(PerformancePoints.EligibleRate(null), Is.EqualTo(1.0));
                Assert.That(PerformancePoints.EligibleRate(mods(new TypeBeatModLiterate(), new TypeBeatModNoFail())), Is.EqualTo(1.0));
            });
        }

        [Test]
        public void EligibleRate_BaseRateModsPriceAtTheirSliderDefault()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.EligibleRate(mods(new TypeBeatModDoubleTime())), Is.EqualTo(1.50));
                Assert.That(PerformancePoints.EligibleRate(mods(new TypeBeatModNightcore())), Is.EqualTo(1.50));
                Assert.That(PerformancePoints.EligibleRate(mods(new TypeBeatModHalfTime())), Is.EqualTo(0.75));

                // The pp-eligible rates are not a second copy of 1.50 / 0.75 living in the formula;
                // they are the very defaults the sliders sit at.
                Assert.That(new TypeBeatModDoubleTime().SpeedChange.Default, Is.EqualTo(PerformancePoints.DOUBLE_TIME_BASE_RATE));
                Assert.That(new TypeBeatModNightcore().SpeedChange.Default, Is.EqualTo(PerformancePoints.DOUBLE_TIME_BASE_RATE));
                Assert.That(new TypeBeatModHalfTime().SpeedChange.Default, Is.EqualTo(PerformancePoints.HALF_TIME_BASE_RATE));
            });
        }

        [TestCase(1.01)]
        [TestCase(1.49)]
        [TestCase(1.51)]
        [TestCase(2.00)]
        public void EligibleRate_CustomDoubleTimeRatesEarnNothing(double rate)
            => Assert.That(PerformancePoints.EligibleRate(mods(at(new TypeBeatModDoubleTime(), rate))), Is.Null);

        [TestCase(0.50)]
        [TestCase(0.74)]
        [TestCase(0.99)]
        public void EligibleRate_CustomHalfTimeRatesEarnNothing(double rate)
            => Assert.That(PerformancePoints.EligibleRate(mods(at(new TypeBeatModHalfTime(), rate))), Is.Null);

        [Test]
        public void EligibleRate_TwoRateModsAtOnceIsRefusedRatherThanGuessedAt()
        {
            // Tamper-shaped by construction: the client makes DT / NC / HT mutually exclusive.
            Assert.That(PerformancePoints.EligibleRate(mods(new TypeBeatModDoubleTime(), new TypeBeatModHalfTime())), Is.Null);
        }

        [Test]
        public void TryGetBaseRate_KnowsExactlyTheThreeRateAcronyms()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.TryGetBaseRate("DT", out double dt), Is.True);
                Assert.That(dt, Is.EqualTo(1.50));
                Assert.That(PerformancePoints.TryGetBaseRate("nc", out double nc), Is.True);
                Assert.That(nc, Is.EqualTo(1.50));
                Assert.That(PerformancePoints.TryGetBaseRate("HT", out double ht), Is.True);
                Assert.That(ht, Is.EqualTo(0.75));

                foreach (string other in new[] { "", " ", "LT", "FL", "NF", "WU", "WD", "DC", "ZZ" })
                    Assert.That(PerformancePoints.TryGetBaseRate(other, out _), Is.False, other);

                Assert.That(PerformancePoints.TryGetBaseRate(null, out _), Is.False);
            });
        }

        #endregion

        #region ForPlay: the one entry point the HUD and any end-of-play consumer share

        [Test]
        public void ForPlay_IsComputeWithTheCountsUnpackedInTheRightOrder()
        {
            var counts = new PerformancePoints.NoteCounts(500, 12, 60);

            Assert.That(PerformancePoints.ForPlay(4.2, counts, 0.87, 400, no_mods),
                Is.EqualTo(PerformancePoints.Compute(4.2, 500, 12, 0.87, 400, no_mods, 60)).Within(1e-12));
        }

        [Test]
        public void ForPlay_PassesTheRateMultiplierThroughAndDefaultsItToOne()
        {
            var counts = new PerformancePoints.NoteCounts(500, 12, 60);

            double bare = PerformancePoints.ForPlay(4.2, counts, 0.87, 400, no_mods);

            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.ForPlay(4.2, counts, 0.87, 400, no_mods, 1), Is.EqualTo(bare));
                Assert.That(PerformancePoints.ForPlay(4.2, counts, 0.87, 400, no_mods, 0.7), Is.EqualTo(bare * 0.7).Within(1e-9));

                // Hostile values fall out through the same finite/positive guard as everything else.
                Assert.That(PerformancePoints.ForPlay(4.2, counts, 0.87, 400, no_mods, double.NaN), Is.EqualTo(0));
                Assert.That(PerformancePoints.ForPlay(4.2, counts, 0.87, 400, no_mods, -1), Is.EqualTo(0));
                Assert.That(PerformancePoints.ForPlay(4.2, counts, 0.87, 400, no_mods, double.PositiveInfinity), Is.EqualTo(0));
            });
        }

        [Test]
        public void Version_TracksTheServersFormulaGeneration()
        {
            // Pinned so a client shipped against generation N cannot quietly price plays the server
            // stores at generation N+1. If this moves, the server's PerformancePoints.VERSION and
            // docs/pp.md move with it. v3 = the backlog-90 Half Time mirror penalty, which had to
            // bump because it reprices every stored Half Time row.
            Assert.That(PerformancePoints.VERSION, Is.EqualTo(3));
        }

        #endregion

        #region The Half Time mirror penalty (backlog 90)

        // A base-rate HT play is priced by sr_ht AND by 1/(D·H), the reciprocal of what Double Time
        // is emergently worth on the same map, so the two rates are equal and opposite per map. The
        // buff guard is the interesting half. These cases are the SAME literals the server's
        // PerformancePointsTest uses, and the WireCompat parity test drives both halves together.

        /// <summary>What Double Time is emergently worth on a map, purely through SR^2.70.</summary>
        private static double doubleTimeFactor(double baseStars, double starsDoubleTime)
            => Math.Pow(starsDoubleTime / baseStars, 2.70);

        /// <summary>What Half Time is emergently worth on a map, before the mirror penalty.</summary>
        private static double halfTimeFactor(double baseStars, double starsHalfTime)
            => Math.Pow(starsHalfTime / baseStars, 2.70);

        [Test]
        public void HalfTimeMultiplier_IsTheReciprocalOfTheDoubleTimeFactorOnTheDecidedSpread()
        {
            // The parity fixture's own spread, and the numbers the change was decided on: DT is
            // already worth +174% here while HT only costs -43%, which is exactly the asymmetry
            // being closed.
            const double base_stars = 4.2, dt = 6.1, ht = 3.4;

            double d = doubleTimeFactor(base_stars, dt);
            double h = halfTimeFactor(base_stars, ht);
            double m = PerformancePoints.HalfTimeMultiplier(base_stars, dt, ht);

            Assert.Multiple(() =>
            {
                Assert.That(d, Is.EqualTo(2.739160).Within(1e-6), "the premise: Double Time is +174% on this map");
                Assert.That(h, Is.EqualTo(0.565223).Within(1e-6), "and Half Time is only -43% before this change");

                Assert.That(m, Is.EqualTo(1.0 / (d * h)).Within(1e-12), "the mirror is used, not the clamp");
                Assert.That(m, Is.EqualTo(0.645896).Within(1e-6));

                // The whole point: HT's TOTAL rate factor is now exactly 1/D.
                Assert.That(m * h, Is.EqualTo(1.0 / d).Within(1e-12));
                Assert.That(m * h, Is.EqualTo(0.365075).Within(1e-6));
            });
        }

        [Test]
        public void HalfTimeMultiplier_ClampsToAFlatCutWhereTheMirrorWouldBuffHalfTime()
        {
            // A map whose SR curve is concave in log-rate: sr_dt · sr_ht < sr_base², so slowing down
            // helps far more than speeding up hurts, and the unguarded mirror would REWARD Half Time
            // on exactly this map. This is what the guard exists for.
            const double base_stars = 4.2, dt = 4.5, ht = 2.0;

            double d = doubleTimeFactor(base_stars, dt);
            double h = halfTimeFactor(base_stars, ht);
            double mirror = 1.0 / (d * h);
            double m = PerformancePoints.HalfTimeMultiplier(base_stars, dt, ht);

            Assert.Multiple(() =>
            {
                Assert.That(dt * ht, Is.LessThan(base_stars * base_stars), "the premise of the concave case");
                Assert.That(mirror, Is.GreaterThan(1), "the unguarded mirror is a buff here");
                Assert.That(mirror * h, Is.EqualTo(0.830041).Within(1e-6), "and it would raise HT's factor six-fold");

                Assert.That(m, Is.EqualTo(0.70).Within(1e-12), "so the flat cut is used instead");

                // And the outcome is a NERF against what this play is worth today, not a buff.
                Assert.That(m * h, Is.LessThan(h));
                Assert.That(m * h, Is.EqualTo(0.094429).Within(1e-6));
            });
        }

        [Test]
        public void HalfTimeMultiplier_UsesAMildMirrorAsIsRatherThanDeepeningItToTheClamp()
        {
            // THE ANTI-Math.Min CASE. This spread's mirror sits strictly between 0.70 and 1.0: it is
            // a mild, correct nerf and must be applied exactly. Math.Min(mirror, 0.70) would return
            // 0.70 here and quietly throw away the per-map symmetry the term exists for.
            const double base_stars = 4.0, dt = 4.5, ht = 3.7;

            double mirror = 1.0 / (doubleTimeFactor(base_stars, dt) * halfTimeFactor(base_stars, ht));
            double m = PerformancePoints.HalfTimeMultiplier(base_stars, dt, ht);

            Assert.Multiple(() =>
            {
                Assert.That(mirror, Is.GreaterThan(0.70).And.LessThan(1.0), "the premise: a mild nerf, not a buff");
                Assert.That(mirror, Is.EqualTo(0.898060).Within(1e-6));

                Assert.That(m, Is.EqualTo(mirror).Within(1e-12));
                Assert.That(m, Is.Not.EqualTo(0.70).Within(1e-6), "a Math.Min would have collapsed this to the flat cut");
            });
        }

        [TestCase(0.0, 6.0, 3.0)]
        [TestCase(-4.0, 6.0, 3.0)]
        [TestCase(4.0, 0.0, 3.0)]
        [TestCase(4.0, -6.0, 3.0)]
        [TestCase(4.0, 6.0, 0.0)]
        [TestCase(4.0, 6.0, -3.0)]
        [TestCase(double.NaN, 6.0, 3.0)]
        [TestCase(4.0, double.NaN, 3.0)]
        [TestCase(4.0, 6.0, double.NaN)]
        [TestCase(double.PositiveInfinity, 6.0, 3.0)]
        [TestCase(4.0, double.PositiveInfinity, 3.0)]
        [TestCase(4.0, 6.0, double.PositiveInfinity)]
        public void HalfTimeMultiplier_IsZeroOnDegenerateRatingsRatherThanNaN(double baseStars, double dt, double ht)
        {
            // A negative rating under a fractional exponent is not merely wrong but non-real, and a
            // NaN multiplier would survive Compute's own guard by poisoning the product. The file's
            // rule is that a degenerate play yields 0, never NaN, Infinity or a negative.
            double m = PerformancePoints.HalfTimeMultiplier(baseStars, dt, ht);

            Assert.Multiple(() =>
            {
                Assert.That(double.IsFinite(m), Is.True);
                Assert.That(m, Is.EqualTo(0));
            });
        }

        [Test]
        public void HalfTimeMultiplier_IsFiniteAndNonNegativeOverAWideSpread()
        {
            double[] ratings = { 0, -1, 1e-9, 0.5, 1, 4.2, 10, 1e9, double.NaN, double.PositiveInfinity, double.NegativeInfinity };

            foreach (double baseStars in ratings)
            {
                foreach (double dt in ratings)
                {
                    foreach (double ht in ratings)
                    {
                        double m = PerformancePoints.HalfTimeMultiplier(baseStars, dt, ht);

                        string context = $"base={baseStars} dt={dt} ht={ht}";

                        Assert.That(double.IsFinite(m), Is.True, context);
                        Assert.That(m, Is.GreaterThanOrEqualTo(0), context);
                    }
                }
            }
        }

        #endregion
    }
}
