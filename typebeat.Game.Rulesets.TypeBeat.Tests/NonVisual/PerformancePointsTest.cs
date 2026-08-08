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
        private const double reference_pp = 219.337706; // pp[f.compute(4, 500, 0, 0.9, 500)]

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
            double raw = 1 + 0.70 * Math.Log10(Math.Max(notes, 1) / 100.0); // pp:const length_weight=0.70 reference_notes=100.0

            Assert.That(PerformancePoints.LengthBonus(notes), Is.EqualTo(0.1).Within(1e-12), // pp[f.length_floor]
                $"raw term at {notes} notes is {raw:0.####}");
        }

        [TestCase(6, 0.144706)] // pp[f.length_bonus(6)]
        [TestCase(100, 1.0)] // pp[f.length_bonus(100)]
        [TestCase(500, 1.489279)] // pp[f.length_bonus(500)]
        [TestCase(1000, 1.7)] // pp[f.length_bonus(1000)]
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
            double raw = 1 + 0.02 + 0.06 * Math.Log10(notes / 100.0); // pp:const flashlight_offset=0.02 flashlight_weight=0.06 reference_notes=100.0

            Assert.Multiple(() =>
            {
                Assert.That(raw, Is.LessThan(1.0), "the raw term is below 1 here, which is what the clamp is for");
                Assert.That(PerformancePoints.FlashlightMultiplier(notes), Is.EqualTo(1.0).Within(1e-12)); // pp[f.flashlight_floor]
            });
        }

        [TestCase(47, 1.000326)] // pp[f.flashlight(47)]
        [TestCase(100, 1.02)] // pp[f.flashlight(100)]
        [TestCase(500, 1.061938)] // pp[f.flashlight(500)]
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
            // dropped part of the map, which is what the 10 exponent is for.
            //
            // THE MISS COUNT MOVED, TWICE, AND FOR OPPOSITE REASONS. Backlog 96 squared the RATIO,
            // which softened the term so far that the case only held at 150 misses. Backlog 97
            // squares the COUNT instead, which hardens it so far that 150 misses is now a flat ZERO
            // and the comparison would be degenerate: any positive number beats it, so the test
            // would assert nothing about the miss term at all. Restated at 10 misses, which is 2% of
            // the map, well BELOW the 23-miss cliff (sqrt(500) = 22.36) and therefore a live
            // comparison of two positive numbers: the accurate play keeps 0.107 of its pp and lands
            // at ~20, the sloppy one at ~129. The crossover now sits between 4 and 5 misses.
            double sloppyButClean = PerformancePoints.Compute(4, 500, misses: 0, accuracy: 0.60, maxCombo: 500, no_mods);
            double accurateButMissy = PerformancePoints.Compute(4, 500, misses: 10, accuracy: 0.93, maxCombo: 350, no_mods);

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
                Assert.That(pp, Is.EqualTo(30.851693).Within(1e-5)); // pp[f.compute(5, 1, 0, 1, 1)]
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
            // Mistypes sit on BOTH sides of the MISTYPING fraction and the base is CLAMPED at 0, so
            // however absurd the keypress count the result is a real number in [0, 1]. An absurd
            // count must price to zero, never to a negative base, a NaN, or (with a fractional
            // exponent on a negative base) an imaginary result. int.MaxValue is in the sweep for
            // TWO reasons now: notes + mistypes would overflow an int there, and so would
            // mistypes * mistypes, whose true square is about 4.6e18. Both are taken in double, so
            // the ratio comes out at about 2.1e9 and the clamp turns it into a well-defined zero.
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

            // Ten times the note count, spelled out. Under the squared COUNT this is far past the
            // cliff (5000^2 is 25 million against a denominator of 5500), so the base clamps and the
            // play prices to EXACTLY zero rather than to something merely small. That is the clamp
            // doing its job: unclamped the base would be about -4544, and a fractional exponent on
            // it would not be a real number at all.
            double absurd = penaltyFactor(500, 0, 5000);

            Assert.Multiple(() =>
            {
                Assert.That(absurd, Is.Zero);
                Assert.That(absurd, Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - 5000.0 * 5000.0 / 5500.0), 6)).Within(1e-12)); // pp:const mistype_exponent=6
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

        #region Mistype pricing (backlog 72, rebalanced by backlog 89, 95, 96 and 97)

        /// <summary>
        /// The two penalty terms in isolation. Nothing else in the formula reads misses or mistypes,
        /// so dividing a play's pp by the pp of the same play with neither is EXACTLY
        /// <c>max(0, 1 - miss²/notes)^10 * max(0, 1 - mistypes²/(notes+mistypes))^6</c>, with every
        /// other factor cancelling. Every expected number below is that product.
        /// </summary>
        private static double penaltyFactor(int notes, int misses, int mistypes)
        {
            double spotless = PerformancePoints.Compute(4, notes, 0, 0.9, notes, no_mods, mistypes: 0);

            return PerformancePoints.Compute(4, notes, misses, 0.9, notes, no_mods, mistypes) / spotless;
        }

        [Test]
        public void Compute_ReproducesTheDecidedRebalanceWorkedExamples()
        {
            // The two cases every rebalance since backlog 89 has been signed off on, stated as exact
            // values. Backlog 89 split the terms apart and SOFTENED both; 95 raised both exponents
            // and took that back; 96 squared the RATIO and softened them far past 89; 97 squares the
            // raw COUNT instead, which hardens them past every earlier generation. Every value in
            // the chain is quoted so the direction is unmistakable, and these are deliberately the
            // same literals the server's PerformancePointsTest uses.
            Assert.Multiple(() =>
            {
                // BOTH bases clamp here: 60^2 = 3600 against 500 notes, and 80^2 = 6400 against a
                // denominator of 580. So this play is worth EXACTLY nothing, against 0.770823 under
                // the squared ratio, 0.114309 at the linear shape, 0.200678 after the backlog-89
                // split and 0.125946 before it. A sloppy play now earns zero, and that is intended.
                Assert.That(penaltyFactor(notes: 500, misses: 60, mistypes: 80), Is.EqualTo(0.000000).Within(1e-6)); // pp[f.penalty(500, 60, 80)]

                // Both counts are under their cliffs here, so this one still prices: the bases are
                // 1 - 100/500 = 0.8 and 1 - 400/520 = 0.2308, giving 0.8^10 = 0.107374 and
                // 0.2308^6 = 1.5103e-4. Against 0.987200 under the squared ratio and 0.645745 at the
                // linear shape, a play with ten misses and twenty mistypes now keeps 1.6e-5 of a
                // spotless one, i.e. essentially nothing. THIS IS THE HEADLINE FIGURE OF THE CHANGE.
                Assert.That(penaltyFactor(notes: 500, misses: 10, mistypes: 20), Is.EqualTo(0.000016).Within(1e-6)); // pp[f.penalty(500, 10, 20)]
            });
        }

        [Test]
        public void Compute_ZeroMistypesLeavesThePlayPricedByItsMissesAlone()
        {
            // The property that makes the split legible: at zero mistypes the mistyping term is
            // EXACTLY 1.0, so the whole penalty is max(0, 1 - miss²/notes)^10 and nothing else. The
            // sweep deliberately straddles the 23-miss cliff, so the restatement is checked both
            // where it is a live number and where the clamp has taken over.
            foreach (int misses in new[] { 0, 1, 17, 250, 500 })
            {
                double withArgument = PerformancePoints.Compute(4.2, 500, misses, 0.87, 400, no_mods, mistypes: 0);
                double withoutArgument = PerformancePoints.Compute(4.2, 500, misses, 0.87, 400, no_mods);

                Assert.That(withArgument, Is.EqualTo(withoutArgument), $"misses={misses}");
                Assert.That(penaltyFactor(500, misses, 0), Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - (double)misses * misses / 500.0), 10)).Within(1e-12), // pp:const miss_exponent=10
                    $"misses={misses}");
            }
        }

        [Test]
        public void Compute_APlayWithNeitherAMissNorAMistypeIsUntouchedByEitherExponent()
        {
            // The cheapest proof that a rebalance of the two exponents is CONFINED to their terms:
            // both bases are exactly 1.0 at a count of zero, and 1.0 raised to any finite power is
            // exactly 1.0. A spotless play must therefore be BIT-identical across any such change,
            // not merely close, so it is asserted against the remaining factors spelled out rather
            // than against a recorded number. If this ever moves, something leaked out of the two
            // penalty terms.
            foreach (int notes in new[] { 1, 100, 500, 2137 })
            {
                double spotless = PerformancePoints.Compute(4, notes, 0, 0.9, notes, no_mods, mistypes: 0);
                double withoutEitherPenaltyTerm = 4.0 * Math.Pow(4, 2.70) * PerformancePoints.LengthBonus(notes) * Math.Pow(0.9, 1.30); // pp:const scale=4.0 sr_exponent=2.70 accuracy_exponent=1.30

                Assert.That(spotless, Is.EqualTo(withoutEitherPenaltyTerm), $"notes={notes}");
            }
        }

        [Test]
        public void Compute_PricesMissesAndMistypesIndependently()
        {
            // The whole point of the split. What a miss costs must not depend on the keypress count
            // and vice versa, so the penalty factorises: the RATIO between two miss counts is the
            // same whatever mistype count both carry. Under the old combined term it was not.
            //
            // Every count here is BELOW its cliff on purpose. Past the cliff both plays price to
            // zero and the ratio is 0/0, which says nothing about factorisation either way, so the
            // sweeps that used to run to 500 and 5000 mistypes at 60 misses have been pulled back to
            // where the claim is testable.
            foreach (int mistypes in new[] { 0, 5, 15, 20 })
            {
                double clean = penaltyFactor(500, 0, mistypes);
                double missy = penaltyFactor(500, 10, mistypes);

                Assert.That(missy / clean, Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - 10.0 * 10.0 / 500.0), 10)).Within(1e-12), // pp:const miss_exponent=10
                    $"the miss term must not be diluted by {mistypes} mistypes");
            }

            // And the mistyping term likewise, read across two miss counts.
            Assert.That(penaltyFactor(500, 10, 20) / penaltyFactor(500, 10, 0),
                Is.EqualTo(penaltyFactor(500, 0, 20)).Within(1e-12));
        }

        [Test]
        public void Compute_MistypesCostPpAndMonotonicallySo()
        {
            // Both counts sit under the 23-mistype cliff, because "many" has to stay STRICTLY above
            // zero for the last assertion to mean anything: 100 mistypes, which this used to use,
            // is now a flat zero and would turn "still positive" into a claim about the clamp rather
            // than about monotonicity.
            double clean = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, mistypes: 0);
            double few = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, mistypes: 5);
            double many = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, mistypes: 15);

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
            // directions, because the terms are separate and either could be wired up backwards on
            // its own.
            //
            // STRICTLY is only true UNDER THE CLIFF, and that is a property of the clamp rather than
            // a weakness of the test: past sqrt(notes) misses (or the mistype root) every count
            // prices to exactly the same zero, so a sweep running to 499 misses would be asserting
            // 0 < 0. Both sweeps and both held-fixed values therefore stay below their cliffs; the
            // behaviour AT and past the cliff has tests of its own below.
            foreach (int mistypes in new[] { 0, 15 })
            {
                double previous = double.MaxValue;

                foreach (int misses in new[] { 0, 1, 5, 10, 15, 22 })
                {
                    double pp = PerformancePoints.Compute(4, 500, misses, 0.9, 500, no_mods, mistypes);

                    Assert.That(pp, Is.LessThan(previous), $"misses={misses} at mistypes={mistypes}");
                    previous = pp;
                }
            }

            foreach (int misses in new[] { 0, 15 })
            {
                double previous = double.MaxValue;

                foreach (int mistypes in new[] { 0, 1, 5, 10, 15, 22 })
                {
                    double pp = PerformancePoints.Compute(4, 500, misses, 0.9, 500, no_mods, mistypes);

                    Assert.That(pp, Is.LessThan(previous), $"mistypes={mistypes} at misses={misses}");
                    previous = pp;
                }
            }
        }

        [Test]
        public void Compute_TheMissPenaltyFallsOffACliffAtTheSquareRootOfTheNoteCount()
        {
            // THE DEFINING BEHAVIOUR OF THE SQUARED COUNT. The base is 1 - miss²/notes, which
            // reaches zero at miss = sqrt(notes) and would go NEGATIVE past it; Math.Max clamps it,
            // so the term is a cliff rather than a curve. sqrt(500) is 22.36, so 22 misses still
            // price and 23 do not.
            Assert.Multiple(() =>
            {
                Assert.That(penaltyFactor(500, 22, 0), Is.GreaterThan(0), "one below the cliff still prices");
                Assert.That(penaltyFactor(500, 23, 0), Is.Zero, "at the cliff the clamp takes over exactly");
                Assert.That(penaltyFactor(500, 400, 0), Is.Zero, "and it stays there rather than turning around");

                // THE CLIFF MOVES WITH sqrt(notes), which is what makes it a shape and not a
                // constant: on a 2000-note map it sits at 44.72, so 44 prices and 45 does not, and
                // on a 100-note map it sits at exactly 10.
                Assert.That(penaltyFactor(2000, 44, 0), Is.GreaterThan(0));
                Assert.That(penaltyFactor(2000, 45, 0), Is.Zero);
                Assert.That(penaltyFactor(100, 9, 0), Is.GreaterThan(0));
                Assert.That(penaltyFactor(100, 10, 0), Is.Zero);
            });
        }

        [Test]
        public void Compute_TheMistypePenaltyFallsOffACliffAtThePositiveRootOfItsOwnQuadratic()
        {
            // The mistype base is 1 - mistypes²/(notes + mistypes), so the count is in the
            // denominator too and the zero moves out to the positive root of m² - m - notes = 0,
            // i.e. (1 + sqrt(1 + 4·notes))/2. That is 22.87 at 500 notes, 45.22 at 2000 and 10.51 at
            // 100: LATER than the miss cliff on every map, which is the mistype term staying the
            // cheaper of the two failures.
            Assert.Multiple(() =>
            {
                Assert.That(penaltyFactor(500, 0, 22), Is.GreaterThan(0), "one below the cliff still prices");
                Assert.That(penaltyFactor(500, 0, 23), Is.Zero, "at the cliff the clamp takes over exactly");
                Assert.That(penaltyFactor(500, 0, 5000), Is.Zero, "and it stays there however absurd the count");

                Assert.That(penaltyFactor(2000, 0, 45), Is.GreaterThan(0));
                Assert.That(penaltyFactor(2000, 0, 46), Is.Zero);
                Assert.That(penaltyFactor(100, 0, 10), Is.GreaterThan(0));
                Assert.That(penaltyFactor(100, 0, 11), Is.Zero);
            });
        }

        [Test]
        public void Compute_APlayPastEitherCliffEarnsExactlyZeroPp()
        {
            // Not merely a small factor: the whole play is worth nothing, whatever its difficulty,
            // accuracy or combo. That is a deliberate consequence of the shape and not a rounding
            // artefact, so it is asserted on Compute itself rather than on the penalty factor.
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.Compute(6, 500, 23, 0.95, 477, no_mods), Is.Zero, "23 misses is the miss cliff");
                Assert.That(PerformancePoints.Compute(6, 500, 0, 0.95, 500, no_mods, 23), Is.Zero, "23 mistypes is the mistype cliff");

                // One below each, the same play is positive, so the zeros above are the clamp and
                // not some unrelated guard swallowing the play.
                Assert.That(PerformancePoints.Compute(6, 500, 22, 0.95, 478, no_mods), Is.GreaterThan(0));
                Assert.That(PerformancePoints.Compute(6, 500, 0, 0.95, 500, no_mods, 22), Is.GreaterThan(0));
            });
        }

        #endregion

        #region Mod multipliers, driven by the REAL ruleset mods

        [Test]
        public void ModMultiplier_NoFailAndFletcherEachCostTenPercentAndLiterateAddsSix()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModNoFail()), 300), Is.EqualTo(0.90).Within(1e-12)); // pp[f.no_fail_multiplier]
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModFletcher()), 300), Is.EqualTo(0.90).Within(1e-12)); // pp[f.fletcher_multiplier]
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModLiterate()), 300), Is.EqualTo(1.06).Within(1e-12)); // pp[f.literate_multiplier]
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
                Assert.That(stacked, Is.EqualTo(1.06 * 0.90 * PerformancePoints.FlashlightMultiplier(500)).Within(1e-12)); // pp:const literate_multiplier=1.06 no_fail_multiplier=0.90

                // A duplicated acronym is tamper-shaped; it must be applied once, not squared.
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModNoFail(), new TypeBeatModNoFail()), 300),
                    Is.EqualTo(0.90).Within(1e-12)); // pp[f.no_fail_multiplier]
            });
        }

        [Test]
        public void Compute_AppliesTheModMultiplierToTheWholeFormula()
        {
            double bare = PerformancePoints.Compute(3, 300, 5, 0.8, 250, no_mods);

            Assert.Multiple(() =>
            {
                // Backlog 97 moves this from 69.935719 (and backlog 96 had moved it there from
                // 59.280683), ONLY through the miss term: the play carries no mistypes, so its
                // mistyping term is exactly 1.0 whatever the shape, and the whole change is
                // max(0, 1 - 25/300)^10 = 0.9167^10 replacing (1 - (5/300)^2)^10. Five misses is
                // well under the 17-miss cliff on a 300-note map, so this still prices.
                Assert.That(bare, Is.EqualTo(29.377848).Within(1e-5)); // pp[f.compute(3, 300, 5, 0.8, 250)]
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModNoFail())),
                    Is.EqualTo(bare * 0.90).Within(1e-9)); // pp:const no_fail_multiplier=0.90
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModFletcher())),
                    Is.EqualTo(bare * 0.90).Within(1e-9)); // pp:const fletcher_multiplier=0.90
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModLiterate())),
                    Is.EqualTo(bare * 1.06).Within(1e-9)); // pp:const literate_multiplier=1.06
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
                Assert.That(PerformancePoints.EligibleRate(mods(new TypeBeatModDoubleTime())), Is.EqualTo(1.50)); // pp[f.double_time_base_rate]
                Assert.That(PerformancePoints.EligibleRate(mods(new TypeBeatModNightcore())), Is.EqualTo(1.50)); // pp[f.double_time_base_rate]
                Assert.That(PerformancePoints.EligibleRate(mods(new TypeBeatModHalfTime())), Is.EqualTo(0.75)); // pp[f.half_time_base_rate]

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
                Assert.That(dt, Is.EqualTo(1.50)); // pp[f.double_time_base_rate]
                Assert.That(PerformancePoints.TryGetBaseRate("nc", out double nc), Is.True);
                Assert.That(nc, Is.EqualTo(1.50)); // pp[f.double_time_base_rate]
                Assert.That(PerformancePoints.TryGetBaseRate("HT", out double ht), Is.True);
                Assert.That(ht, Is.EqualTo(0.75)); // pp[f.half_time_base_rate]

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
            // docs/pp.md move with it. v6 = the backlog-97 squaring of both penalty COUNTS, which
            // had to bump because it reprices every stored row carrying even one miss or one
            // mistype (downwards, and to zero for most of them).
            Assert.That(PerformancePoints.VERSION, Is.EqualTo(6)); // pp:version
        }

        #endregion

        #region The Half Time mirror penalty (backlog 90)

        // A base-rate HT play is priced by sr_ht AND by 1/(D·H), the reciprocal of what Double Time
        // is emergently worth on the same map, so the two rates are equal and opposite per map. The
        // buff guard is the interesting half. These cases are the SAME literals the server's
        // PerformancePointsTest uses, and the WireCompat parity test drives both halves together.

        /// <summary>What Double Time is emergently worth on a map, purely through SR^2.70.</summary>
        private static double doubleTimeFactor(double baseStars, double starsDoubleTime)
            => Math.Pow(starsDoubleTime / baseStars, 2.70); // pp:const sr_exponent=2.70

        /// <summary>What Half Time is emergently worth on a map, before the mirror penalty.</summary>
        private static double halfTimeFactor(double baseStars, double starsHalfTime)
            => Math.Pow(starsHalfTime / baseStars, 2.70); // pp:const sr_exponent=2.70

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
                Assert.That(d, Is.EqualTo(2.739160).Within(1e-6), "the premise: Double Time is +174% on this map"); // pp[f.rate_factor(4.2, 6.1)]
                Assert.That(h, Is.EqualTo(0.565223).Within(1e-6), "and Half Time is only -43% before this change"); // pp[f.rate_factor(4.2, 3.4)]

                Assert.That(m, Is.EqualTo(1.0 / (d * h)).Within(1e-12), "the mirror is used, not the clamp");
                Assert.That(m, Is.EqualTo(0.645896).Within(1e-6)); // pp[f.half_time_multiplier(4.2, 6.1, 3.4)]

                // The whole point: HT's TOTAL rate factor is now exactly 1/D.
                Assert.That(m * h, Is.EqualTo(1.0 / d).Within(1e-12));
                Assert.That(m * h, Is.EqualTo(0.365075).Within(1e-6)); // pp[f.half_time_multiplier(4.2, 6.1, 3.4) * f.rate_factor(4.2, 3.4)]
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
                Assert.That(mirror * h, Is.EqualTo(0.830041).Within(1e-6), "and it would raise HT's factor six-fold"); // pp[1 / f.rate_factor(4.2, 4.5)]

                Assert.That(m, Is.EqualTo(0.70).Within(1e-12), "so the flat cut is used instead"); // pp[f.half_time_buff_clamp]

                // And the outcome is a NERF against what this play is worth today, not a buff.
                Assert.That(m * h, Is.LessThan(h));
                Assert.That(m * h, Is.EqualTo(0.094429).Within(1e-6)); // pp[f.half_time_buff_clamp * f.rate_factor(4.2, 2.0)]
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
                Assert.That(mirror, Is.EqualTo(0.898060).Within(1e-6)); // pp[f.half_time_multiplier(4.0, 4.5, 3.7)]

                Assert.That(m, Is.EqualTo(mirror).Within(1e-12));
                Assert.That(m, Is.Not.EqualTo(0.70).Within(1e-6), "a Math.Min would have collapsed this to the flat cut"); // pp[f.half_time_buff_clamp]
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
