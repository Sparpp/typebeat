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
using System.Linq;
using NUnit.Framework;
using osu.Framework.Localisation;
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
        private const double reference_pp = 127.065524; // pp[f.compute(4, 500, 0, 0.9, 500)]

        [Test]
        public void Compute_MatchesAnIndependentlyEvaluatedReferencePlay()
        {
            double pp = PerformancePoints.Compute(starRating: 4, notes: 500, misses: 0, accuracy: 0.9, maxCombo: 500, no_mods);

            Assert.That(pp, Is.EqualTo(reference_pp).Within(1e-5));
        }

        // THERE IS NO LENGTH REGION HERE, because there is no length factor: backlog 152 deleted it
        // and moved length pricing into the star rating, as an additive
        // 0.12*max(0, log10(cells/100)) bonus inside LyricDifficulty (LyricDifficultyTest pins the
        // clamp and the bonus there). pp sees a long map only through the SR_eff it is handed, so
        // the thing this file can still assert about length is that Compute does NOT read the note
        // count as a bonus: the spotless-play identity below spells the surviving factors out in
        // full and would fail the moment a length term came back.

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
            // THE MISS COUNT HAS MOVED THREE TIMES NOW, AND NOT ALWAYS IN THE SAME DIRECTION.
            // Backlog 96 squared the RATIO, which softened the term so far that the case only held
            // at 150 misses. Backlog 97 squared the COUNT, which hardened it so far that 150 misses
            // was a flat ZERO and the comparison went degenerate (any positive number beats zero, so
            // the test asserted nothing about the miss term at all); it was restated at 10 misses to
            // dodge the 23-miss cliff. Backlog 101 drops the power to 1.2, which moves that cliff
            // out to 178 and takes the 10-miss term back up from 0.107 to 0.725, and at 0.725 the
            // ACCURATE play wins: the crossover sits between 11 and 12 misses, so 10 no longer
            // tested the claim at all and would have failed.
            //
            // Restated at 25 misses, i.e. 5% of the map. Both plays price properly (the miss term is
            // 0.368), the sloppy one lands at ~129 against ~69, and the case is decided by the miss
            // term rather than by a clamp.
            double sloppyButClean = PerformancePoints.Compute(4, 500, misses: 0, accuracy: 0.60, maxCombo: 500, no_mods);
            double accurateButMissy = PerformancePoints.Compute(4, 500, misses: 25, accuracy: 0.93, maxCombo: 350, no_mods);

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
        public void Compute_OneNoteIsNoLongerDiscountedForBeingOneNote()
        {
            // A single perfect note on a 5-star map. This used to be TINY (24.0), and the length
            // floor was the entire reason: the term bottomed out at 0.1 and cut the play to a
            // tenth. Backlog 152 deleted the length factor, so a one-note map is now priced purely
            // by its rating, accuracy and combo, and this play is worth MORE than the 4-star
            // 500-note reference play at 90%. That inversion is deliberate and is not reachable: pp
            // is a pure function over primitives and this feeds it a rating no one-cell map could
            // ever carry, since the star rating is what knows how long a map is (LyricDifficulty's
            // own length bonus is zero below 100 cells, and a one-cell map's strain aggregate is
            // nowhere near 5 stars).
            double pp = PerformancePoints.Compute(5, notes: 1, misses: 0, accuracy: 1, maxCombo: 1, no_mods);

            Assert.Multiple(() =>
            {
                Assert.That(pp, Is.EqualTo(240.000000).Within(1e-5)); // pp[f.compute(5, 1, 0, 1, 1)]
                Assert.That(pp, Is.GreaterThan(reference_pp));
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
        public void Compute_TheTypoTermStaysInRangeForAnyTypoCount()
        {
            // Typos sit on BOTH sides of the TYPO TERM fraction and the base is CLAMPED at 0, so
            // however absurd the keypress count the result is a real number in [0, 1]. An absurd
            // count must price to zero, never to a negative base, a NaN, or (with a fractional
            // exponent on a negative base) an imaginary result. int.MaxValue is in the sweep for
            // TWO reasons: notes + typos would overflow an int there, and so would an int square,
            // whose true value is about 4.6e18. Math.Pow converts to double and the sum is taken in
            // double, so the ratio comes out at about 74 and the clamp turns it into a well-defined
            // zero. The NEGATIVE entry matters more than it used to: the count is clamped before it
            // reaches Math.Pow, and Math.Pow(-1, 1.2) is NaN rather than merely a wrong sign.
            foreach (int notes in new[] { 1, 10, 500 })
            foreach (int misses in new[] { 0, notes / 2, notes })
            foreach (int typos in new[] { -1, 0, 1, notes * 10, notes * 1000, int.MaxValue })
            {
                double pp = PerformancePoints.Compute(6, notes, misses, 0.9, notes, no_mods, typos);

                Assert.That(pp, Is.Not.NaN, $"notes={notes} miss={misses} typos={typos}");
                Assert.That(double.IsFinite(pp), Is.True, $"notes={notes} miss={misses} typos={typos}");
                Assert.That(pp, Is.GreaterThanOrEqualTo(0), $"notes={notes} miss={misses} typos={typos}");
                Assert.That(pp, Is.LessThan(reference_pp * 10), $"notes={notes} miss={misses} typos={typos}");
            }

            // Ten times the note count, spelled out. Even at the softened power of 1.2 this is far
            // past the cliff (5000^1.2 is 27464 against a denominator of 5500), so the base clamps
            // and the play prices to EXACTLY zero rather than to something merely small. That is the
            // clamp doing its job: unclamped the base would be about -3.99, and a fractional
            // exponent on it would not be a real number at all.
            double absurd = penaltyFactor(500, 0, 5000);

            Assert.Multiple(() =>
            {
                Assert.That(absurd, Is.Zero);
                Assert.That(absurd, Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - Math.Pow(5000.0, 1.2) / 5500.0), 4)).Within(1e-12)); // pp:const count_power=1.2 typo_exponent=4
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
                Assert.That(counts.Typos, Is.Zero);
            });
        }

        [Test]
        public void CountNotes_ReadsTyposFromTheComboBreakResultWithoutCountingThemAsNotes()
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
                Assert.That(counts.Typos, Is.EqualTo(137));

                // The stat's home is the score processor's; this must not be a second definition.
                Assert.That(PerformancePoints.MISTYPE_RESULT, Is.EqualTo(TypeBeatScoreProcessor.MISTYPE_RESULT));
            });
        }

        [Test]
        public void CountNotes_AnAbsentTypoKeyReadsAsZero()
            => Assert.That(PerformancePoints.CountNotes(new Dictionary<HitResult, int> { [HitResult.Great] = 100 }).Typos, Is.Zero);

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
                Assert.That(counts.Typos, Is.Zero);
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
            // so a 400-note map with 60 lines would read as 460 "notes". The note count sits under
            // both penalty terms, the combo ratio and Flashlight's bonus, and the most visible
            // casualty is the combo term: a genuine full combo would stop reading as one. On a
            // spotless play (this one) the penalty terms are exactly 1.0 either way, so the combo
            // ratio is the whole of what moves here now that backlog 152 has removed the length
            // factor that used to move with it.
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

        #region Typo pricing (backlog 72, rebalanced by backlog 89, 95, 96, 97 and 101)

        /// <summary>
        /// The two penalty terms in isolation. Nothing else in the formula reads misses or typos,
        /// so dividing a play's pp by the pp of the same play with neither is EXACTLY
        /// <c>max(0, 1 - miss^1.2/notes)^10 * max(0, 1 - typos^1.2/(notes+typos))^6</c>, with
        /// every other factor cancelling. Every expected number below is that product.
        /// </summary>
        private static double penaltyFactor(int notes, int misses, int typos)
        {
            double spotless = PerformancePoints.Compute(4, notes, 0, 0.9, notes, no_mods, typos: 0);

            return PerformancePoints.Compute(4, notes, misses, 0.9, notes, no_mods, typos) / spotless;
        }

        [Test]
        public void Compute_ReproducesTheDecidedRebalanceWorkedExamples()
        {
            // The two cases every rebalance since backlog 89 has been signed off on, stated as exact
            // values. Backlog 89 split the terms apart and SOFTENED both; 95 raised both exponents
            // and took that back; 96 squared the RATIO and softened them far past 89; 97 powered the
            // raw COUNT instead, at 2, which hardened them past every earlier generation and zeroed
            // both cases; 101 leaves the shape alone and drops that power to 1.2. Every value in the
            // chain is quoted so the direction is unmistakable, and these are deliberately the same
            // literals the server's PerformancePointsTest uses.
            Assert.Multiple(() =>
            {
                // BOTH counts were past their cliffs at a power of 2 (60^2 = 3600 against 500 notes,
                // 80^2 = 6400 against a denominator of 580), so this play was worth EXACTLY nothing.
                // At 1.2 it is a live number again: 60^1.2 = 136.4 against 500 and 80^1.2 = 190.6
                // against 580, giving 0.041726 and 0.089375. Against 0.000000 at a power of 2,
                // 0.770823 under the squared ratio, 0.114309 at the linear shape, 0.200678 after the
                // backlog-89 split and 0.125946 before it: a sloppy play is priced harshly again
                // rather than zeroed.
                Assert.That(penaltyFactor(notes: 500, misses: 60, typos: 80), Is.EqualTo(0.008341).Within(1e-6)); // pp[f.penalty(500, 60, 80)]

                // The near-clean case, which is the headline figure: the bases are 1 - 15.849/500 =
                // 0.96830 and 1 - 36.411/520 = 0.92998, giving 0.724618 and 0.646893. A play with
                // ten misses and twenty typos keeps 0.469 of a spotless one, against 0.000016 at
                // a power of 2, 0.987200 under the squared ratio and 0.645745 at the linear shape.
                // THAT IS THE POINT OF THE CHANGE: it lands almost exactly where backlog 95 had it.
                Assert.That(penaltyFactor(notes: 500, misses: 10, typos: 20), Is.EqualTo(0.542001).Within(1e-6)); // pp[f.penalty(500, 10, 20)]
            });
        }

        [Test]
        public void Compute_ZeroTyposLeavesThePlayPricedByItsMissesAlone()
        {
            // The property that makes the split legible: at zero typos the typo term is
            // EXACTLY 1.0, so the whole penalty is max(0, 1 - miss^1.2/notes)^10 and nothing else.
            // The sweep deliberately straddles the cliff, so the restatement is checked both where
            // it is a live number and where the clamp has taken over. It USED to straddle 23, which
            // backlog 101 moves out to 178, so 17 and 250 no longer sit either side of anything.
            foreach (int misses in new[] { 0, 1, 100, 177, 178, 500 })
            {
                double withArgument = PerformancePoints.Compute(4.2, 500, misses, 0.87, 400, no_mods, typos: 0);
                double withoutArgument = PerformancePoints.Compute(4.2, 500, misses, 0.87, 400, no_mods);

                Assert.That(withArgument, Is.EqualTo(withoutArgument), $"misses={misses}");
                Assert.That(penaltyFactor(500, misses, 0), Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - Math.Pow(misses, 1.2) / 500.0), 10)).Within(1e-12), // pp:const count_power=1.2 miss_exponent=10
                    $"misses={misses}");
            }
        }

        [Test]
        public void Compute_APlayWithNeitherAMissNorATypoIsUntouchedByEitherExponent()
        {
            // The cheapest proof that a rebalance of the two exponents is CONFINED to their terms:
            // both bases are exactly 1.0 at a count of zero, and 1.0 raised to any finite power is
            // exactly 1.0. A spotless play must therefore be BIT-identical across any such change,
            // not merely close, so it is asserted against the remaining factors spelled out rather
            // than against a recorded number. If this ever moves, something leaked out of the two
            // penalty terms.
            foreach (int notes in new[] { 1, 100, 500, 2137 })
            {
                double spotless = PerformancePoints.Compute(4, notes, 0, 0.9, notes, no_mods, typos: 0);
                double withoutEitherPenaltyTerm = 9.6 * Math.Pow(4, 2.00) * Math.Pow(0.9, 1.80); // pp:const scale=9.6 sr_exponent=2.00 accuracy_exponent=1.80

                Assert.That(spotless, Is.EqualTo(withoutEitherPenaltyTerm), $"notes={notes}");
            }
        }

        [Test]
        public void Compute_PricesMissesAndTyposIndependently()
        {
            // The whole point of the split. What a miss costs must not depend on the keypress count
            // and vice versa, so the penalty factorises: the RATIO between two miss counts is the
            // same whatever typo count both carry. Under the old combined term it was not.
            //
            // Every count here is BELOW its cliff on purpose. Past the cliff both plays price to
            // zero and the ratio is 0/0, which says nothing about factorisation either way. The
            // cliff has moved twice: 23 at count power 2, then 249 at 1.2, now 52 at 1.6. The sweep
            // runs to 51, the last count that prices at all.
            foreach (int typos in new[] { 0, 10, 30, 51 })
            {
                double clean = penaltyFactor(500, 0, typos);
                double missy = penaltyFactor(500, 10, typos);

                Assert.That(missy / clean, Is.EqualTo(Math.Pow(Math.Max(0.0, 1.0 - Math.Pow(10.0, 1.2) / 500.0), 10)).Within(1e-12), // pp:const count_power=1.2 miss_exponent=10
                    $"the miss term must not be diluted by {typos} typos");
            }

            // And the typo term likewise, read across two miss counts.
            Assert.That(penaltyFactor(500, 10, 20) / penaltyFactor(500, 10, 0),
                Is.EqualTo(penaltyFactor(500, 0, 20)).Within(1e-12));
        }

        [Test]
        public void Compute_TyposCostPpAndMonotonicallySo()
        {
            // Both counts sit under the typo cliff, because "many" has to stay STRICTLY above
            // zero for the last assertion to mean anything: past the cliff "still positive" would be
            // a claim about the clamp rather than about monotonicity. Backlog 97 pulled these down
            // to 5 and 15 to clear a cliff at 23, then out to 50 and 200 when it moved to 249. The
            // count power of 1.6 brings it back to 52, so they sit at 15 and 45.
            double clean = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, typos: 0);
            double few = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, typos: 15);
            double many = PerformancePoints.Compute(4, 500, 0, 0.9, 500, no_mods, typos: 45);

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
            // a weakness of the test: past notes^(1/1.6) misses (or the typo root) every count
            // prices to exactly the same zero, so a sweep running to 499 misses would be asserting
            // 0 < 0. Both sweeps and both held-fixed values therefore stay below their cliffs; the
            // behaviour AT and past the cliff has tests of its own below.
            //
            // The upper ends have tracked the cliffs through three count powers: 22 at 2, then 177
            // and 248 at 1.2, now 48 and 51 at 1.6. They sit one under each cliff on purpose, since
            // that is where a term wired up backwards would show.
            foreach (int typos in new[] { 0, 30 })
            {
                double previous = double.MaxValue;

                foreach (int misses in new[] { 0, 1, 10, 25, 40, 48 })
                {
                    double pp = PerformancePoints.Compute(4, 500, misses, 0.9, 500, no_mods, typos);

                    Assert.That(pp, Is.LessThan(previous), $"misses={misses} at typos={typos}");
                    previous = pp;
                }
            }

            foreach (int misses in new[] { 0, 25 })
            {
                double previous = double.MaxValue;

                foreach (int typos in new[] { 0, 1, 10, 25, 40, 51 })
                {
                    double pp = PerformancePoints.Compute(4, 500, misses, 0.9, 500, no_mods, typos);

                    Assert.That(pp, Is.LessThan(previous), $"typos={typos} at misses={misses}");
                    previous = pp;
                }
            }
        }

        [Test]
        public void Compute_TheMissPenaltyFallsOffACliffAtTheCountPowerRootOfTheNoteCount()
        {
            // THE DEFINING BEHAVIOUR OF THE POWERED COUNT, and the reason count_power is the lever a
            // rebalance pulls rather than the exponents. The base is 1 - miss^1.2/notes, which
            // reaches zero at miss = notes^(1/1.2) and would go NEGATIVE past it; Math.Max clamps
            // it, so the term is a cliff rather than a curve. On a 500-note map that is 177.48, so
            // 177 misses still price and 178 do not. Under backlog 97's power of 2 it was 22.36,
            // i.e. 23 misses or 4.6% of the map, against 35% of it now.
            //
            // THE THRESHOLDS ARE LIFTED INTO CONSTANTS so the pp tool can rewrite them. A cliff
            // sitting in a call argument is invisible to it, which is why the last two retunes moved
            // these three numbers by hand and why one of them was left describing the wrong power.
            const int cliff500 = 178; // pp[math.ceil(f.miss_cliff(500))]
            const int cliff2000 = 564; // pp[math.ceil(f.miss_cliff(2000))]
            const int cliff100 = 47; // pp[math.ceil(f.miss_cliff(100))]

            Assert.Multiple(() =>
            {
                Assert.That(penaltyFactor(500, cliff500 - 1, 0), Is.GreaterThan(0), "one below the cliff still prices");
                Assert.That(penaltyFactor(500, cliff500, 0), Is.Zero, "at the cliff the clamp takes over exactly");
                Assert.That(penaltyFactor(500, 500, 0), Is.Zero, "and it stays there rather than turning around");

                // THE CLIFF MOVES WITH THE MAP, which is what makes it a shape and not a constant:
                // notes^(1/1.2) is 563.45 on a 2000-note map and 46.42 on a 100-note one. It moves
                // far less STEEPLY than it did, though, and that is the second half of the argument
                // for 1.2: as a FRACTION of the map the cliff is notes^(1/1.2 - 1), which runs 46%
                // to 28% across this span where 1/sqrt(notes) ran 10% to 2.2%.
                Assert.That(penaltyFactor(2000, cliff2000 - 1, 0), Is.GreaterThan(0));
                Assert.That(penaltyFactor(2000, cliff2000, 0), Is.Zero);
                Assert.That(penaltyFactor(100, cliff100 - 1, 0), Is.GreaterThan(0));
                Assert.That(penaltyFactor(100, cliff100, 0), Is.Zero);
            });
        }

        [Test]
        public void Compute_TheTypoPenaltyFallsOffACliffAtThePositiveRootOfItsOwnEquation()
        {
            // The typo base is 1 - typos^1.2/(notes + typos), so the count is in the
            // denominator too and the zero moves out to the positive root of m^1.2 - m - notes = 0.
            // At the old power of 2 that had the closed form (1 + sqrt(1 + 4·notes))/2; at 1.2 it
            // has none and is solved numerically. It is 248.37 at 500 notes, 730.32 at 2000 and
            // 73.45 at 100: LATER than the miss cliff on every map, which is the typo term
            // staying the cheaper of the two failures.
            const int cliff500 = 249; // pp[math.ceil(f.typo_cliff(500))]
            const int cliff2000 = 731; // pp[math.ceil(f.typo_cliff(2000))]
            const int cliff100 = 74; // pp[math.ceil(f.typo_cliff(100))]
            const int missCliff500 = 178; // pp[math.ceil(f.miss_cliff(500))]

            Assert.Multiple(() =>
            {
                Assert.That(penaltyFactor(500, 0, cliff500 - 1), Is.GreaterThan(0), "one below the cliff still prices");
                Assert.That(penaltyFactor(500, 0, cliff500), Is.Zero, "at the cliff the clamp takes over exactly");
                Assert.That(penaltyFactor(500, 0, 5000), Is.Zero, "and it stays there however absurd the count");

                Assert.That(penaltyFactor(2000, 0, cliff2000 - 1), Is.GreaterThan(0));
                Assert.That(penaltyFactor(2000, 0, cliff2000), Is.Zero);
                Assert.That(penaltyFactor(100, 0, cliff100 - 1), Is.GreaterThan(0));
                Assert.That(penaltyFactor(100, 0, cliff100), Is.Zero);

                // The ordering, asserted rather than left to the six numbers above agreeing by luck:
                // whatever the power, the typo cliff is the LATER of the two, so a typo count
                // that would already have zeroed the same number of MISSES still prices.
                Assert.That(penaltyFactor(500, 0, missCliff500), Is.GreaterThan(0));
                Assert.That(penaltyFactor(500, missCliff500, 0), Is.Zero);
            });
        }

        [Test]
        public void Compute_APlayPastEitherCliffEarnsExactlyZeroPp()
        {
            // Not merely a small factor: the whole play is worth nothing, whatever its difficulty,
            // accuracy or combo. That is a deliberate consequence of the shape and not a rounding
            // artefact, so it is asserted on Compute itself rather than on the penalty factor.
            const int missCliff = 178; // pp[math.ceil(f.miss_cliff(500))]
            const int typoCliff = 249; // pp[math.ceil(f.typo_cliff(500))]

            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.Compute(6, 500, missCliff, 0.95, 500 - missCliff, no_mods), Is.Zero,
                    "the miss cliff");
                Assert.That(PerformancePoints.Compute(6, 500, 0, 0.95, 500, no_mods, typoCliff), Is.Zero,
                    "the typo cliff");

                // One below each, the same play is positive, so the zeros above are the clamp and
                // not some unrelated guard swallowing the play.
                Assert.That(PerformancePoints.Compute(6, 500, missCliff - 1, 0.95, 501 - missCliff, no_mods), Is.GreaterThan(0));
                Assert.That(PerformancePoints.Compute(6, 500, 0, 0.95, 500, no_mods, typoCliff - 1), Is.GreaterThan(0));
            });
        }

        #endregion

        #region Mod multipliers, driven by the REAL ruleset mods

        [Test]
        public void ModMultiplier_NoFailAndFletcherEachCostTenPercent()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModNoFail()), 300), Is.EqualTo(0.90).Within(1e-12)); // pp[f.no_fail_multiplier]
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModFletcher()), 300), Is.EqualTo(0.90).Within(1e-12)); // pp[f.fletcher_multiplier]
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModFlashlight()), 300),
                    Is.EqualTo(PerformancePoints.FlashlightMultiplier(300)).Within(1e-12));
            });
        }

        /// <summary>
        /// LITERATE CONTRIBUTES NOTHING HERE (backlog 144), and that is the whole point rather than
        /// an omission: <see cref="TypeBeatModLiterate"/> is IApplicableAfterBeatmapConversion, so
        /// it is priced through the star rating of the map it converts this one into
        /// (<see cref="PerformancePoints.StarsFor"/>), and a flat multiplier on top would be
        /// exactly the double count docs/pp.md forbids for DT/HT. It used to be a flat 1.06 here.
        ///
        /// <para>Asserted against the SAME value as a mod this table has never heard of, because
        /// that is precisely what it now is. The flat number was a poor description of the mod
        /// anyway: measured over the five reference maps the honest rate-1.0 rating moves between
        /// -0.8% and +6.3%, so Literate makes two of them EASIER where 1.06 paid every map 6%.</para>
        /// </summary>
        [Test]
        public void ModMultiplier_LiterateIsNeutralBecauseItIsPricedThroughTheStarRating()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModLiterate()), 300), Is.EqualTo(1.0).Within(1e-12));

                // Stacked with a mod that IS priced here, only that mod's value survives.
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModLiterate(), new TypeBeatModNoFail()), 300),
                    Is.EqualTo(PerformancePoints.ModMultiplier(mods(new TypeBeatModNoFail()), 300)).Within(1e-12));
            });
        }

        /// <summary>
        /// A mod carrying the RETIRED Rhythmic acronym. Backlog 147 deleted
        /// <c>TypeBeatModRhythmic</c>, so nothing in the ruleset can produce an <c>RH</c> any more
        /// and this stands in for the one thing that still can: a score row submitted while the mod
        /// was live, whose stored mods blob is read back and priced.
        /// </summary>
        private sealed class RetiredRhythmicMod : Mod
        {
            public override string Name => "Rhythmic";
            public override string Acronym => "RH";
            public override LocalisableString Description => "A stored acronym no client can select any more.";
        }

        /// <summary>
        /// Rhythmic (backlog 135) PAID 10%, and backlog 147 removed the mod from the client without
        /// removing that price. THIS IS THE PRODUCTION-SAFETY TEST, not a leftover: RH shipped, so
        /// rows carrying it exist, and pp is recomputed from a stored row's mods on every
        /// <c>PpBackfill</c> sweep and every recalc. Deleting the arm would silently reprice every
        /// one of those rows 10% down, and its server-side twin
        /// (<c>ModMultiplier.TotalScoreCeiling</c>) would then put each row's own stored total above
        /// its ceiling and store it UNRANKED.
        ///
        /// <para>It costs nothing to keep: no mod in <c>TypeBeatRuleset.GetModsFor</c> answers RH,
        /// so no play made under today's rules can reach the arm at all. This is still the marked
        /// site, so a retune through <c>pp.py set --rhythmic-multiplier</c> lands here.</para>
        /// </summary>
        [Test]
        public void ModMultiplier_StillPricesAStoredRhythmicAtTenPercent()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.ModMultiplier(mods(new RetiredRhythmicMod()), 300), Is.EqualTo(1.10).Within(1e-12)); // pp[f.rhythmic_multiplier]
                // Literate rides along contributing exactly nothing since backlog 144 (see the
                // Literate test above), so this pair is worth what Rhythmic alone is.
                Assert.That(PerformancePoints.ModMultiplier(mods(new RetiredRhythmicMod(), new TypeBeatModLiterate()), 300),
                    Is.EqualTo(1.100).Within(1e-12)); // pp[f.mod_multiplier(["RH", "LT"], 300)]
            });
        }

        /// <summary>
        /// The other half of the same fact: the ruleset no longer OFFERS Rhythmic, so no new play
        /// can earn the multiplier the test above keeps alive. Asserted on the acronym rather than
        /// on the type, because the type is gone.
        /// </summary>
        [Test]
        public void ModMultiplier_NoModTheRulesetOffersCarriesTheRhythmicAcronym()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.That(ruleset.AllMods.Select(m => m.Acronym), Has.No.Member("RH"));
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
                mods(new TypeBeatModNoFail(), new TypeBeatModFlashlight()), 500);

            Assert.Multiple(() =>
            {
                Assert.That(stacked, Is.EqualTo(0.90 * PerformancePoints.FlashlightMultiplier(500)).Within(1e-12)); // pp:const no_fail_multiplier=0.90

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
                // Backlog 101 moves this from 29.377848 (which is where 97 put it, from 96's
                // 69.935719 and 95's 59.280683), ONLY through the miss term: the play carries no
                // typos, so its typo term is exactly 1.0 whatever the power, and the whole
                // change is max(0, 1 - 5^1.2/300)^10 = 0.97700^10 replacing 0.91667^10. Five misses
                // is far under the 116-miss cliff on a 300-note map, so this prices comfortably.
                Assert.That(bare, Is.EqualTo(38.156643).Within(1e-5)); // pp[f.compute(3, 300, 5, 0.8, 250)]
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModNoFail())),
                    Is.EqualTo(bare * 0.90).Within(1e-9)); // pp:const no_fail_multiplier=0.90
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModFletcher())),
                    Is.EqualTo(bare * 0.90).Within(1e-9)); // pp:const fletcher_multiplier=0.90
                // Literate does not reach this function at all any more: it moves the star rating
                // that was passed IN, not the multiplier applied here (backlog 144).
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModLiterate())),
                    Is.EqualTo(bare).Within(1e-9));
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
            // docs/pp.md move with it. v7 = the backlog-101 drop of count_power from 2 to 1.2, which
            // had to bump because it reprices every stored row carrying even one miss or one typo
            // (upwards this time, and most of them away from the zero backlog 97 left them at).
            Assert.That(PerformancePoints.VERSION, Is.EqualTo(16)); // pp:version
        }

        #endregion

        #region The Half Time mirror penalty (backlog 90)

        // A base-rate HT play is priced by sr_ht AND by 1/(D·H), the reciprocal of what Double Time
        // is emergently worth on the same map, so the two rates are equal and opposite per map. The
        // buff guard is the interesting half. These cases are the SAME literals the server's
        // PerformancePointsTest uses, and the WireCompat parity test drives both halves together.

        /// <summary>What Double Time is emergently worth on a map, purely through SR^2.70.</summary>
        private static double doubleTimeFactor(double baseStars, double starsDoubleTime)
            => Math.Pow(starsDoubleTime / baseStars, 2.00); // pp:const sr_exponent=2.00

        /// <summary>What Half Time is emergently worth on a map, before the mirror penalty.</summary>
        private static double halfTimeFactor(double baseStars, double starsHalfTime)
            => Math.Pow(starsHalfTime / baseStars, 2.00); // pp:const sr_exponent=2.00

        [Test]
        public void HalfTimeMultiplier_IsTheReciprocalOfTheDoubleTimeFactorOnTheDecidedSpread()
        {
            // The parity fixture's own spread, and the numbers the change was decided on: DT is
            // already worth +111% here while HT only costs -34.5%, which is exactly the asymmetry
            // being closed.
            const double base_stars = 4.2, dt = 6.1, ht = 3.4;

            double d = doubleTimeFactor(base_stars, dt);
            double h = halfTimeFactor(base_stars, ht);
            double m = PerformancePoints.HalfTimeMultiplier(base_stars, dt, ht);

            Assert.Multiple(() =>
            {
                Assert.That(d, Is.EqualTo(2.109410).Within(1e-6), "the premise: Double Time is +111% on this map"); // pp[f.rate_factor(4.2, 6.1)]
                Assert.That(h, Is.EqualTo(0.655329).Within(1e-6), "and Half Time is only -34.5% before this change"); // pp[f.rate_factor(4.2, 3.4)]

                Assert.That(m, Is.EqualTo(1.0 / (d * h)).Within(1e-12), "the mirror is used, not the clamp");
                Assert.That(m, Is.EqualTo(0.723402).Within(1e-6)); // pp[f.half_time_multiplier(4.2, 6.1, 3.4)]

                // The whole point: HT's TOTAL rate factor is now exactly 1/D.
                Assert.That(m * h, Is.EqualTo(1.0 / d).Within(1e-12));
                Assert.That(m * h, Is.EqualTo(0.474066).Within(1e-6)); // pp[f.half_time_multiplier(4.2, 6.1, 3.4) * f.rate_factor(4.2, 3.4)]
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
                Assert.That(mirror * h, Is.EqualTo(0.871111).Within(1e-6), "and it would raise HT's factor six-fold"); // pp[1 / f.rate_factor(4.2, 4.5)]

                Assert.That(m, Is.EqualTo(0.70).Within(1e-12), "so the flat cut is used instead"); // pp[f.half_time_buff_clamp]

                // And the outcome is a NERF against what this play is worth today, not a buff.
                Assert.That(m * h, Is.LessThan(h));
                Assert.That(m * h, Is.EqualTo(0.158730).Within(1e-6)); // pp[f.half_time_buff_clamp * f.rate_factor(4.2, 2.0)]
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
                Assert.That(mirror, Is.EqualTo(0.923446).Within(1e-6)); // pp[f.half_time_multiplier(4.0, 4.5, 3.7)]

                Assert.That(m, Is.EqualTo(mirror).Within(1e-12));
                Assert.That(m, Is.Not.EqualTo(0.70).Within(1e-6), "a Math.Min would have collapsed this to the flat cut"); // pp[f.half_time_buff_clamp]
            });
        }

        [Test]
        public void HalfTimeMultiplier_TookTheFlatCutOnlyBecauseSrDtHadBeenTruncated()
        {
            // backlog 118. LyricDifficulty used to end in a flat clamp to 10 stars. It never touched
            // a base rating (the hardest ranked difficulty published reads 7.81) but it truncated
            // sr_dt on any map dense enough at 1.50x, and sr_dt is half of what decides this
            // multiplier. The three numbers below are Siames "The Wolf" measured through the real
            // formula: base 9.6708, sr_ht 6.5614, and sr_dt 16.3333 against the 10.0000 the ceiling
            // used to hand over. Truncated, the spread reads as CONCAVE and takes the flat cut; it
            // is an ordinary, milder per-map mirror once the rating runs free, so the flat cut was
            // firing on an artefact of the ceiling rather than on the shape of the map.
            const double base_stars = 9.6708, ht = 6.5614;

            double truncated = PerformancePoints.HalfTimeMultiplier(base_stars, 10.0, ht);
            double untruncated = PerformancePoints.HalfTimeMultiplier(base_stars, 16.3333, ht);

            Assert.Multiple(() =>
            {
                Assert.That(10.0 * ht, Is.LessThan(base_stars * base_stars), "truncated, the spread looks concave");
                Assert.That(truncated, Is.EqualTo(0.70).Within(1e-12), "so it takes the flat cut"); // pp[f.half_time_buff_clamp]

                Assert.That(16.3333 * ht, Is.GreaterThan(base_stars * base_stars), "untruncated it is convex, like most maps");
                Assert.That(untruncated, Is.EqualTo(0.761568).Within(1e-6)); // pp[f.half_time_multiplier(9.6708, 16.3333, 6.5614)]
                Assert.That(untruncated, Is.Not.EqualTo(0.70).Within(1e-6), "and needs no fallback at all"); // pp[f.half_time_buff_clamp]
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
