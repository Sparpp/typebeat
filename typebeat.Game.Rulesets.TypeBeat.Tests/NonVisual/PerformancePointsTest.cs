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
        private const double reference_pp = 198.674292; // pp[f.compute(4, 500, 0, 0.9, 500)]

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
            //
            // SINCE BACKLOG 270 IT IS NOT EXACTLY ZERO. The miss term still clamps (900^1.2 is 3506
            // against 1000 notes), so the PRODUCT half is exactly 0, but the combo bonus is ADDED to
            // that product rather than multiplied into it, and a run of 10 on a 4-star map collects
            // 10/1000 of 12.5 * (4 - 1) = 0.375 pp. That is the shape working as intended rather
            // than a leak: the bonus is what a run is worth, and this play held one, briefly.
            double giveUp = PerformancePoints.Compute(4, notes: 1000, misses: 900, accuracy: 0.1, maxCombo: 10, no_mods);
            double runOfTen = 10.0 / 1000.0 * fullComboBonus(4);

            Assert.Multiple(() =>
            {
                Assert.That(giveUp, Is.EqualTo(runOfTen), "the product half is an exact zero, so the bonus is the whole of it");
                Assert.That(giveUp, Is.EqualTo(0.375).Within(1e-12));
                Assert.That(giveUp, Is.LessThan(reference_pp / 100));
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
            // 0.368) and the case is decided by the miss term rather than by a clamp.
            //
            // THE SLOPPY PLAY'S ACCURACY HAS MOVED TOO, from 0.60 to 0.85, and for the same class of
            // reason: backlog 227 put a soft knee at 80% on the accuracy term, which multiplies a
            // 60% play by 0.00034. The comparison would then be decided by the KNEE (the sloppy play
            // prices at 0.03) and would say nothing whatever about the miss exponent. At 0.85 both
            // plays sit above the knee, where it costs 12% and 0.5% respectively.
            //
            // BACKLOG 270 NARROWS THE GAP WITHOUT CLOSING IT, which is the point of the change and
            // is why the case is restated rather than deleted. Under v20 the missy play was ALSO
            // charged a combo multiplier for its broken run (0.70 of the map, worth 0.716 of the
            // term) and landed at ~44 against ~130. Combo is now an additive bonus, so the missy
            // play keeps its core pp and collects 0.7 of the 37.5 a full combo is worth: ~90
            // against ~168. The miss term is still what decides it, which is the claim.
            double sloppyButClean = PerformancePoints.Compute(4, 500, misses: 0, accuracy: 0.85, maxCombo: 500, no_mods);
            double accurateButMissy = PerformancePoints.Compute(4, 500, misses: 25, accuracy: 0.93, maxCombo: 350, no_mods);

            Assert.That(sloppyButClean, Is.GreaterThan(accurateButMissy));
        }

        #endregion

        #region Accuracy: the gentle exponent and the soft knee

        [Test]
        public void Compute_TheAccuracyKneeIsExactlyAHalfOnTheKneeItself()
        {
            // THE PROPERTY THE WHOLE DIAL RESTS ON, and the one that survives any retune of the
            // WIDTH: at accuracy == acc_knee the argument to the exponential is exactly 0, exp(0) is
            // exactly 1.0, and 1/(1 + 1) is exactly 0.5. A play sitting ON the knee is therefore
            // priced at exactly half of what the exponent alone would give it, whatever the width is
            // set to, which is the accuracy term's version of "an FC is exactly 1.0 at every
            // combo_bonus_slope". Asserted bit-exactly rather than with a tolerance, and grouped the
            // way Compute groups it (the exponent times the knee) because double multiplication is
            // not associative.
            foreach (int notes in new[] { 1, 100, 500, 2137 })
            {
                double onTheKnee = PerformancePoints.Compute(4, notes, 0, 0.80, notes, no_mods, typos: 0); // pp:const acc_knee=0.80
                double halfTheExponentAlone = 12.4 * Math.Pow(4, 2.00) * (Math.Pow(0.80, 1.80) * 0.5); // pp:const scale=12.4 sr_exponent=2.00 acc_knee=0.80 accuracy_exponent=1.80
                // A full combo, so the additive bonus (backlog 270) is the whole of what a full
                // combo is worth at 4 stars. It is spelled out rather than cancelled because it
                // does NOT cancel: it is ADDED to the product, so an identity about the product has
                // to carry it explicitly.
                double bonus = fullComboBonus(4);

                Assert.That(onTheKnee, Is.EqualTo(halfTheExponentAlone + bonus), $"notes={notes}");
            }
        }

        [Test]
        public void Compute_IsStrictlyIncreasingInAccuracySoTheKneeCannotReorderTwoPlays()
        {
            // The knee RESPREADS the accuracy axis and never permutes it: the logistic is strictly
            // increasing in the accuracy and so is acc^1.80, so their product is too. Swept across
            // the whole range at 0.005, straddling the knee, on a play that is not spotless so
            // every other factor of the product is a fixed positive number and only the timing
            // term moves.
            //
            // SWEPT AT NO COMBO (backlog 270), and not to dodge anything: the combo bonus is
            // ADDED and is accuracy-independent, so it cannot reorder two plays on this axis by
            // construction. What it CAN do is hide them from each other in double: at the bottom
            // of the range the product runs to about 1e-16, and adding a constant 32 pp to that
            // makes several consecutive steps equal, so a STRICT claim would be about float
            // spacing rather than about the knee. The bonus arm is asserted separately below, as
            // the non-decreasing statement that is honest there.
            double previous = -1;

            for (int step = 0; step <= 200; step++)
            {
                double accuracy = step / 200.0;
                double pp = PerformancePoints.Compute(4.2, 500, 25, accuracy, maxCombo: 0, no_mods, typos: 12);

                Assert.That(pp, Is.GreaterThan(previous), $"accuracy={accuracy}");
                previous = pp;
            }

            double previousWithRun = -1;

            for (int step = 0; step <= 200; step++)
            {
                double accuracy = step / 200.0;
                double pp = PerformancePoints.Compute(4.2, 500, 25, accuracy, 400, no_mods, typos: 12);

                Assert.That(pp, Is.GreaterThanOrEqualTo(previousWithRun), $"accuracy={accuracy} with a run");
                previousWithRun = pp;
            }
        }

        [Test]
        public void Compute_TheAccuracyKneeIsFiniteAtBothEndsOfTheRange()
        {
            // NO CLAMP GUARDS THE KNEE AND NONE IS NEEDED: accuracy is clamped into [0, 1] before
            // the term sees it, so at a width of 0.025 the argument to Math.Exp runs between -8 and
            // +32 and the factor stays strictly inside (0, 1). The two ends are the only places an
            // unclamped logistic could overflow, so they are pinned rather than assumed.
            foreach (double accuracy in new[] { 0.0, 1.0 })
            {
                double pp = PerformancePoints.Compute(4, 500, 0, accuracy, 500, no_mods, typos: 0);

                Assert.Multiple(() =>
                {
                    Assert.That(pp, Is.Not.NaN, $"accuracy={accuracy}");
                    Assert.That(double.IsFinite(pp), Is.True, $"accuracy={accuracy}");
                    Assert.That(pp, Is.GreaterThanOrEqualTo(0), $"accuracy={accuracy}");
                });
            }

            // A perfect play is not FREE of the knee, merely barely touched by it (1/(1 + e^-8),
            // i.e. 0.9997), which is the point of putting the cliff at 80% instead of raising the
            // exponent: the top of the range keeps what it had.
            //
            // THE CLAIM IS ABOUT THE PRODUCT, so the additive combo bonus is taken back off first
            // (backlog 270). This play is a full combo on a 4-star map, so it collects the whole of
            // 12.5 * (4 - 1) on top of a product that is by construction just UNDER 12.4 * 4^2;
            // left in, the total would sit above that ceiling and the comparison would say nothing
            // about the knee at all.
            double perfect = PerformancePoints.Compute(4, 500, 0, 1.0, 500, no_mods, typos: 0);
            double exponentAlone = 12.4 * Math.Pow(4, 2.00); // pp:const scale=12.4 sr_exponent=2.00
            double product = perfect - fullComboBonus(4);

            Assert.Multiple(() =>
            {
                Assert.That(product, Is.LessThan(exponentAlone));
                Assert.That(product, Is.GreaterThan(exponentAlone * 0.999));
            });
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
            // own length bonus is zero below 100 cells, and a one-cell map has no window the feats
            // model can score at all).
            double pp = PerformancePoints.Compute(5, notes: 1, misses: 0, accuracy: 1, maxCombo: 1, no_mods);

            Assert.Multiple(() =>
            {
                Assert.That(pp, Is.EqualTo(359.896041).Within(1e-5)); // pp[f.compute(5, 1, 0, 1, 1)]
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
            // both penalty terms, the combo RATIO and Flashlight's bonus, and the most visible
            // casualty is the combo: a genuine full combo would stop reading as one. On a spotless
            // play (this one) the penalty terms are exactly 1.0 either way, so the combo ratio is
            // the whole of what moves here now that backlog 152 has removed the length factor that
            // used to move with it.
            //
            // THE BOUND IS RESTATED AT 2% RATHER THAN 3% (backlog 270), honestly and not to make a
            // failing test pass. Combo used to be a FACTOR of the whole play, so a ratio of 400/460
            // cost 13.0% of the pp under v20's log-bent term and 29.5% under the plain ratio before
            // it. As an ADDITIVE bonus it can only ever cost the bonus' own share: the product half
            // is identical at both note counts, so the whole difference is 60/460 of what a full
            // combo is worth at 4 stars, i.e. 4.89 pp against a play worth 167.9, which is 2.9%.
            // Still several times any plausible rounding, and still an answer the inflation would
            // change.
            double fullCombo = PerformancePoints.Compute(4, 400, 0, 0.85, 400, no_mods);
            double inflated = PerformancePoints.Compute(4, 460, 0, 0.85, 400, no_mods);

            Assert.Multiple(() =>
            {
                Assert.That(inflated, Is.LessThan(fullCombo));
                Assert.That((fullCombo - inflated) / fullCombo, Is.GreaterThan(0.02),
                    "counting the line containers would cost a full combo several percent of its pp");

                // And it is EXACTLY the bonus that moved, which is the sharper statement the
                // additive shape makes available: nothing else in this play reads the note count.
                Assert.That(fullCombo - inflated,
                    Is.EqualTo((1 - 400.0 / 460.0) * fullComboBonus(4)).Within(1e-9));
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
            double spotless = PerformancePoints.Compute(4, notes, 0, 0.9, maxCombo: 0, no_mods, typos: 0);

            return PerformancePoints.Compute(4, notes, misses, 0.9, maxCombo: 0, no_mods, typos) / spotless;
        }

        /// <summary>What a FULL combo adds to a play at this rating (backlog 270).</summary>
        private static double fullComboBonus(double starRating)
            => 12.5 * (starRating - 1.0); // pp:const combo_bonus_slope=12.5 combo_bonus_zero=1.0

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

                // The timing term carries the accuracy SOFT KNEE as a second factor since backlog
                // 227, so this identity has to carry it too: at 90% accuracy the knee is
                // 1/(1 + e^-4) = 0.98201379. It is GROUPED exactly as Compute groups it (the
                // exponent times the knee, and the rest around that product) because the assertion
                // below is bit-exact and double multiplication is not associative.
                double knee = 1.0 / (1.0 + Math.Exp(-(0.9 - 0.80) / 0.025)); // pp:const acc_knee=0.80 acc_knee_width=0.025
                double withoutEitherPenaltyTerm = 12.4 * Math.Pow(4, 2.00) * (Math.Pow(0.9, 1.80) * knee); // pp:const scale=12.4 sr_exponent=2.00 accuracy_exponent=1.80

                // A FULL COMBO, so the additive bonus (backlog 270) is on top of that product and
                // has to be carried explicitly: it does not cancel, and it is the same number at
                // every note count because the ratio is exactly 1.0.
                Assert.That(spotless, Is.EqualTo(withoutEitherPenaltyTerm + fullComboBonus(4)), $"notes={notes}");
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
            // Not merely a small factor: the PRODUCT half of the play is worth nothing, whatever
            // its difficulty or accuracy. That is a deliberate consequence of the shape and not a
            // rounding artefact, so it is asserted on Compute itself rather than on the penalty
            // factor.
            //
            // SINCE BACKLOG 270 "EXACTLY ZERO" NEEDS A ZERO COMBO TOO, and that is the change
            // rather than a dodge: the bonus is ADDED to the clamped product, so a play past the
            // cliff that still held a run is worth exactly that run's bonus and nothing else. Both
            // facts are pinned, the second immediately below.
            const int missCliff = 178; // pp[math.ceil(f.miss_cliff(500))]
            const int typoCliff = 249; // pp[math.ceil(f.typo_cliff(500))]

            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.Compute(6, 500, missCliff, 0.95, maxCombo: 0, no_mods), Is.Zero,
                    "the miss cliff");
                Assert.That(PerformancePoints.Compute(6, 500, 0, 0.95, maxCombo: 0, no_mods, typoCliff), Is.Zero,
                    "the typo cliff");

                // With a run, the same two plays are worth exactly the bonus that run earns, which
                // is a strictly stronger claim than "zero" was: it also says nothing of the clamped
                // product leaked through.
                double run = (500.0 - missCliff) / 500.0 * fullComboBonus(6);

                Assert.That(PerformancePoints.Compute(6, 500, missCliff, 0.95, 500 - missCliff, no_mods),
                    Is.EqualTo(run), "the miss cliff, with a run");
                Assert.That(PerformancePoints.Compute(6, 500, 0, 0.95, 500, no_mods, typoCliff),
                    Is.EqualTo(fullComboBonus(6)), "the typo cliff, with a full combo");

                // One below each, the same play is positive, so the zeros above are the clamp and
                // not some unrelated guard swallowing the play.
                Assert.That(PerformancePoints.Compute(6, 500, missCliff - 1, 0.95, maxCombo: 0, no_mods), Is.GreaterThan(0));
                Assert.That(PerformancePoints.Compute(6, 500, 0, 0.95, maxCombo: 0, no_mods, typoCliff - 1), Is.GreaterThan(0));
            });
        }

        #endregion

        #region Mod multipliers, driven by the REAL ruleset mods

        [Test]
        public void ModMultiplier_NoFailAndRetiredFletcherEachCostTenPercent()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModNoFail()), 300), Is.EqualTo(0.90).Within(1e-12)); // pp[f.no_fail_multiplier]
                // "FT" is the RETIRED mod (backlog 208 made its freedoms the default and gave the
                // name to the mod that pins the caret instead), and its price is still charged,
                // because stored rows carry the acronym and pp is keyed on the acronym string.
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModLegacyFletcher()), 300), Is.EqualTo(0.90).Within(1e-12)); // pp[f.fletcher_multiplier]
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModFlashlight()), 300),
                    Is.EqualTo(PerformancePoints.FlashlightMultiplier(300)).Within(1e-12));
            });
        }

        /// <summary>
        /// Recite (<c>RE</c>) and the mod named Fletcher TODAY (<c>FC</c>) join the pp table at
        /// backlog 270; both were unpriced before it, which is why this test is an INVERSION of the
        /// "FC is neutral" line that used to sit above. Neither converts the map (Recite hides the
        /// lyric until it is sung, Fletcher pins the caret back to the line the song is on), so
        /// neither has a rating of its own to be priced through and each takes a flat term, exactly
        /// as Easy and Hard Rock do.
        ///
        /// <para>THE VALUES EQUALLING THEIR SCORE MULTIPLIERS IS A COINCIDENCE, not a derivation
        /// rule: the user chose 1.07 and 1.02 here and
        /// <see cref="TypeBeatScoreMultiplierCalculator"/> happens to carry the same two numbers.
        /// Easy is 0.75 here against 0.5x score, Hard Rock 1.25 against 1.10x, No Fail 0.90 against
        /// 0.5x and Flashlight length-scaled against a flat 1.12x, so the two tables agree on
        /// nothing else and must never be read off each other.</para>
        ///
        /// <para><c>FC</c> is NOT <c>FT</c>: the retired acronym means the OPPOSITE thing (an
        /// unpinned caret, back when that was the mod rather than the default) and keeps its own
        /// 0.90, pinned above.</para>
        /// </summary>
        [Test]
        public void ModMultiplier_ReciteAndFletcherAreEachPricedFlat()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModRecite()), 300), Is.EqualTo(1.07).Within(1e-12)); // pp[f.recite_multiplier]
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModFletcher()), 300), Is.EqualTo(1.02).Within(1e-12)); // pp[f.fletcher_strict_multiplier]

                // Stacked, because the multiplier is a product and a missing arm hides inside a
                // single-mod test whenever the neutral answer happens to be right.
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModRecite(), new TypeBeatModFletcher()), 300),
                    Is.EqualTo(1.0914).Within(1e-12)); // pp[f.mod_multiplier(["RE", "FC"], 300)]

                // The acronyms these arms key on, read off the mods themselves rather than retyped:
                // the pp table is keyed on the STRING, so a renamed acronym silently unprices the mod.
                Assert.That(new TypeBeatModRecite().Acronym, Is.EqualTo("RE"));
                Assert.That(new TypeBeatModFletcher().Acronym, Is.EqualTo("FC"));
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
        /// Rhythmic (backlog 135) PAID 10% until backlog 270, which deleted the entry. This test is
        /// therefore an INVERSION of the production-safety pin that used to stand here, and the
        /// reason it survives inverted is the same one: RH shipped, so a stored row carries it, and
        /// pp is recomputed from that row's mods on every <c>PpBackfill</c> sweep and every recalc.
        /// One row reprices 10% down at v21, which is what a VERSION bump is for, and the deletion
        /// has to land on BOTH sides of the mirror or the in-game counter and the stored value
        /// diverge by that 10% for ever.
        ///
        /// <para>The old note's other argument was <c>ModMultiplier.TotalScoreCeiling</c>, which is
        /// the SERVER'S SCORE-SIDE TABLE in a different file entirely: it still prices <c>"RH"</c>
        /// at 1.10 and is untouched, so the row's own stored total stays under its ceiling and
        /// stays ranked. This file's table and that one were never the same thing.</para>
        /// </summary>
        [Test]
        public void ModMultiplier_NoLongerPricesAStoredRhythmic()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.ModMultiplier(mods(new RetiredRhythmicMod()), 300), Is.EqualTo(1.0).Within(1e-12));
                // Priced exactly as an acronym this table has never heard of, which is what it now
                // is, and stacked with a mod that IS priced only that mod's value survives.
                Assert.That(PerformancePoints.ModMultiplier(mods(new RetiredRhythmicMod(), new TypeBeatModNoFail()), 300),
                    Is.EqualTo(PerformancePoints.ModMultiplier(mods(new TypeBeatModNoFail()), 300)).Within(1e-12));
            });
        }

        /// <summary>
        /// The other half of the same fact: the ruleset no longer OFFERS Rhythmic, so the only
        /// thing the acronym can ever reach is a stored row. Asserted on the acronym rather than on
        /// the type, because the type is gone.
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

        /// <summary>
        /// THE MOD MULTIPLIER SCALES THE PRODUCT AND NOT THE COMBO BONUS (backlog 270). It used to
        /// distribute over the whole of pp, because pp WAS a product; combo is an additive bonus
        /// now and sits outside every factor, the mod multiplier included, so a modded play is
        /// <c>product * modMult + bonus</c> and not <c>(product + bonus) * modMult</c>.
        ///
        /// <para>That is the one placement mistake the shape invites, so the bonus is subtracted
        /// out explicitly here rather than being allowed to cancel: putting it inside the product
        /// would leave every full-combo pin in this file green and only this assertion and the
        /// WireCompat parity pin red.</para>
        /// </summary>
        [Test]
        public void Compute_AppliesTheModMultiplierToTheProductAndNotToTheComboBonus()
        {
            double bare = PerformancePoints.Compute(3, 300, 5, 0.8, 250, no_mods);
            // Grouped exactly as Compute groups it: the ratio, then the clamped slope times the
            // rating above the zero point. A run of 250 on a 300-note 3-star map.
            double comboBonus = 250.0 / 300.0 * fullComboBonus(3);
            double bareProduct = bare - comboBonus;

            Assert.Multiple(() =>
            {
                // Backlog 101 moves this from 29.377848 (which is where 97 put it, from 96's
                // 69.935719 and 95's 59.280683), ONLY through the miss term: the play carries no
                // typos, so its typo term is exactly 1.0 whatever the power, and the whole
                // change is max(0, 1 - 5^1.2/300)^10 = 0.97700^10 replacing 0.91667^10. Five misses
                // is far under the 116-miss cliff on a 300-note map, so this prices comfortably.
                Assert.That(bare, Is.EqualTo(50.424483).Within(1e-5)); // pp[f.compute(3, 300, 5, 0.8, 250)]
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModNoFail())),
                    Is.EqualTo(bareProduct * 0.90 + comboBonus).Within(1e-9)); // pp:const no_fail_multiplier=0.90
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModLegacyFletcher())),
                    Is.EqualTo(bareProduct * 0.90 + comboBonus).Within(1e-9)); // pp:const fletcher_multiplier=0.90
                // Literate does not reach this function at all any more: it moves the star rating
                // that was passed IN, not the multiplier applied here (backlog 144).
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModLiterate())),
                    Is.EqualTo(bare).Within(1e-9));
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModFlashlight())),
                    Is.EqualTo(bareProduct * PerformancePoints.FlashlightMultiplier(300) + comboBonus).Within(1e-9));

                // And the mistake stated as its own assertion: distributing the multiplier over the
                // BONUS as well would land 10% of the bonus lower here, which is 2.08 pp and far
                // outside the tolerance above.
                Assert.That(PerformancePoints.Compute(3, 300, 5, 0.8, 250, mods(new TypeBeatModNoFail())),
                    Is.Not.EqualTo(bare * 0.90).Within(1e-9)); // pp:const no_fail_multiplier=0.90
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
        public void Version_TracksTheServersFormulaGeneration()
        {
            // Pinned so a client shipped against generation N cannot quietly price plays the server
            // stores at generation N+1. If this moves, the server's PerformancePoints.VERSION and
            // docs/pp.md move with it. v20 = the backlog-265 removal of the Half Time mirror
            // multiplier: no constant and no term moves, a whole FACTOR leaves the product, so every
            // stored base-rate Half Time row is repriced upwards and nothing else moves at all.
            Assert.That(PerformancePoints.VERSION, Is.EqualTo(21)); // pp:version
        }

        #endregion
    }
}
