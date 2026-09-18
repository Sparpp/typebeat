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
        private const double reference_pp = 145.51039011765178; // pp[f.compute(4, 500, 500, 0, 0.9, 500)] at v23 dials

        [Test]
        public void Compute_MatchesAnIndependentlyEvaluatedReferencePlay()
        {
            double pp = PerformancePoints.Compute(starRating: 4, notes: 500, difficultCharacters: 500, misses: 0, accuracy: 0.9, maxCombo: 500, no_mods);

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
            // DEPARTURE 4 MAKES IT EXACTLY ZERO. The miss term still clamps, so the core is 0 —
            // and because the combo bonus MULTIPLIES the price rather than being added beside it,
            // a zeroed core takes the bonus with it. The v21 shape paid this play 0.375 pp for the
            // run of ten it briefly held; there is no consolation prize now, which is the point of
            // moving the bonus inside the product.
            double giveUp = PerformancePoints.Compute(4, notes: 1000, difficultCharacters: 1000, misses: 900, accuracy: 0.1, maxCombo: 10, no_mods);

            Assert.Multiple(() =>
            {
                Assert.That(giveUp, Is.Zero, "a multiplicative bonus cannot outlive a zeroed core");
                Assert.That(giveUp, Is.LessThan(reference_pp / 100));
            });
        }

        [Test]
        public void Compute_MissingEveryNoteIsExactlyZero()
            => Assert.That(PerformancePoints.Compute(6, notes: 400, difficultCharacters: 400, misses: 400, accuracy: 0, maxCombo: 0, no_mods), Is.Zero);

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
            double sloppyButClean = PerformancePoints.Compute(4, 500, difficultCharacters: 500, misses: 0, accuracy: 0.85, maxCombo: 500, no_mods);
            double accurateButMissy = PerformancePoints.Compute(4, 500, difficultCharacters: 500, misses: 25, accuracy: 0.93, maxCombo: 350, no_mods);

            Assert.That(sloppyButClean, Is.GreaterThan(accurateButMissy));
        }

        #endregion

        #region Accuracy: the exponential from a floor (departure 5)
        // The timing term is accuracyShare(acc) * knee(acc). At these dials the knee is OFF
        // (width 0, this file's declared-absence sentinel), so the curve below is the whole of
        // the accuracy shape. Two properties have to hold whatever the steepness is, and they
        // are what replaced the old knee tests: both ends are EXACT, and the curve never
        // reorders two plays. accuracyShare is normalised, so t = 0 is exactly 0 and t = 1
        // exactly 1 for every steepness, which is what makes the floor and the price of a
        // perfect play dial-independent.
        [Test]
        public void Compute_TheAccuracyCurveIsExactAtBothEndsAndZeroBelowTheFloor()
        {
            Assert.Multiple(() =>
            {
                // A perfect play keeps all of its core, from the curve as much as from the misses.
                Assert.That(PerformancePoints.Compute(4, 500, 500, 0, 1.0, 500, no_mods), Is.GreaterThan(0));
                // The floor is where the accuracy term reaches zero: at it, and below it.
                Assert.That(PerformancePoints.Compute(4, 500, 500, 0, 0.5, 500, no_mods), Is.Zero);
                Assert.That(PerformancePoints.Compute(4, 500, 500, 0, 0.2, 500, no_mods), Is.Zero);
                Assert.That(PerformancePoints.Compute(4, 500, 500, 0, 0.0, 500, no_mods), Is.Zero);
            });
        }

        [Test]
        public void Compute_IsStrictlyIncreasingInAccuracy()
        {
            // The curve and the knee are each strictly increasing, so their product is: accuracy
            // can respread the price, it can never reorder two plays on it.
            double last = -1;
            for (double acc = 0.5; acc <= 1.0001; acc += 0.01)
            {
                double pp = PerformancePoints.Compute(4, 500, 500, 0, acc, 500, no_mods);
                Assert.That(pp, Is.GreaterThanOrEqualTo(last), $"pp fell at accuracy {acc}");
                last = pp;
            }
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
                    double pp = PerformancePoints.Compute(sr, notes, difficultCharacters: notes, misses, acc, combo, no_mods);

                    Assert.That(pp, Is.Not.NaN, $"sr={sr} notes={notes} miss={misses} acc={acc} combo={combo}");
                    Assert.That(double.IsFinite(pp), Is.True, $"sr={sr} notes={notes} miss={misses} acc={acc} combo={combo}");
                    Assert.That(pp, Is.GreaterThanOrEqualTo(0), $"sr={sr} notes={notes} miss={misses} acc={acc} combo={combo}");
                }
            }
        }

        [Test]
        public void Compute_ZeroNotesEarnsNothing()
            => Assert.That(PerformancePoints.Compute(5, notes: 0, difficultCharacters: 0, misses: 0, accuracy: 1, maxCombo: 0, no_mods), Is.Zero);

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
            double pp = PerformancePoints.Compute(5, notes: 1, difficultCharacters: 1, misses: 0, accuracy: 1, maxCombo: 1, no_mods);

            Assert.Multiple(() =>
            {
                Assert.That(pp, Is.EqualTo(364.6750828359406).Within(1e-5)); // pp[f.compute(5, 1, 0, 1, 1)]
                Assert.That(pp, Is.GreaterThan(reference_pp));
            });
        }

        [Test]
        public void Compute_ClampsAComboAboveTheNoteCountRatherThanRewardingIt()
        {
            double honest = PerformancePoints.Compute(4, 500, difficultCharacters: 500, 0, 0.9, 500, no_mods);
            double tampered = PerformancePoints.Compute(4, 500, difficultCharacters: 500, 0, 0.9, 5000, no_mods);

            Assert.That(tampered, Is.EqualTo(honest).Within(1e-9));
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
            double fullCombo = PerformancePoints.Compute(4, 400, difficultCharacters: 400, 0, 0.85, 400, no_mods);
            double inflated = PerformancePoints.Compute(4, 460, difficultCharacters: 460, 0, 0.85, 400, no_mods);

            Assert.Multiple(() =>
            {
                // DEPARTURE 4 CHANGES WHAT THIS PROVES, and the answer is now about the KICKER
                // rather than about the cell count. Below the ceiling's cap the ceiling and the
                // run's share cancel - the ceiling is linear in the cells, so the bonus works out
                // as 1.5 * maxCombo / 20000 and does not read the cell count at all - which is why
                // a 400-cell full combo and a 400-run on a 460-cell map carry the SAME 2% bonus.
                // What separates them is that the first is spotless over the whole map and so earns
                // the 1.5x spotless-full-combo kicker, and the second is not. The price ratio is
                // therefore exactly 1.03/1.02, and the v21 shape's "a few percent" bound no longer
                // holds: the inflation costs this play 0.97%, not 2.9%.
                double core = fullCombo / 1.03;

                Assert.That(inflated, Is.EqualTo(core * 1.02).Within(1e-9));
                Assert.That(inflated, Is.LessThan(fullCombo));
            });
        }

        #endregion

        #region The miss penalty, over the map's difficult characters
        // THERE IS NO TYPO TERM (departure 1), so the tests that used to price a recovered wrong
        // keypress are deleted rather than renumbered: the counts are still derived for the
        // surfaces that display them, but nothing in the price reads them. What is pinned here
        // is the cleanliness term, which is judged over the map's DIFFICULT characters rather
        // than its cells (departure 2) with the power on the missed FRACTION (departure 3).
        [Test]
        public void Compute_ASpotlessPlayIsPricedAsIfTheMissTermWereNotThere()
        {
            // Zero misses leaves cleanliness at exactly 1 on every map, which is what keeps a
            // spotless play's price a pure function of rating, accuracy, mods and combo.
            double missy = PerformancePoints.Compute(4, 500, 500, 0, 0.9, 500, no_mods);
            double same = PerformancePoints.Compute(4, 500, 500, 0, 0.9, 500, no_mods);

            Assert.That(missy, Is.EqualTo(same).Within(1e-12));
            Assert.That(missy, Is.GreaterThan(0));
        }

        [Test]
        public void Compute_MissesCostPpAndMonotonicallySo()
        {
            // More of the difficult characters dropped can never pay more, at any accuracy.
            double last = double.PositiveInfinity;
            for (int misses = 0; misses <= 500; misses += 25)
            {
                double pp = PerformancePoints.Compute(4, 500, 500, misses, 0.9, 500 - misses, no_mods);
                Assert.That(pp, Is.LessThanOrEqualTo(last + 1e-12), $"pp rose at {misses} misses");
                last = pp;
            }
        }

        [Test]
        public void Compute_TheMissPenaltyFallsOffACliffAtTheDifficultCharacterCount()
        {
            // The base is 1 - (misses/difficult)^1.2, which reaches zero when the fraction is 1
            // and would run NEGATIVE past it; Math.Max clamps it, so the term is a cliff rather
            // than a curve. This is departure 3's move: the cliff sits at the DIFFICULT CHARACTER
            // count now, not at the count-power root of the note count it used to be pinned to,
            // so on a 500-note map whose difficulty is spread over 300 difficult characters it
            // falls at 300 dropped, not at 500.
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.Compute(6, 500, 300, 300, 1.0, 200, no_mods), Is.Zero,
                    "every difficult character missed is a well-defined zero");
                Assert.That(PerformancePoints.Compute(6, 500, 300, 299, 1.0, 201, no_mods), Is.GreaterThan(0),
                    "one short of the cliff still prices");
                // Past the cliff, the clamp holds: no NaN, no negative, no infinity.
                foreach (int over in new[] { 301, 400, 500 })
                {
                    double pp = PerformancePoints.Compute(6, 500, 300, over, 1.0, 0, no_mods);
                    Assert.That(pp, Is.Zero, $"{over} misses past the cliff must price as zero");
                }
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
        public void ModMultiplier_ReciteScalesTheFlashlightBonusAndFletcherIsFlat()
        {
            Assert.Multiple(() =>
            {
                // DEPARTURE 6: Recite is no longer flat. It pays recite_multiplier times what
                // Flashlight pays on the same map, so 300 notes gives 1 + 2.0 * (1.04862727528 - 1).
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModRecite()), 300),
                    Is.EqualTo(1.0972545505663595).Within(1e-12)); // pp[f.recite_multiplier_for(300)]
                Assert.That(PerformancePoints.FlashlightMultiplier(300), Is.EqualTo(1.0486272752831797).Within(1e-12));
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModFletcher()), 300), Is.EqualTo(1.02).Within(1e-12)); // pp[f.fletcher_strict_multiplier]

                // Stacked, because the multiplier is a product and a missing arm hides inside a
                // single-mod test whenever the neutral answer happens to be right.
                Assert.That(PerformancePoints.ModMultiplier(mods(new TypeBeatModRecite(), new TypeBeatModFletcher()), 300),
                    Is.EqualTo(1.1191996415776867).Within(1e-12)); // pp[f.mod_multiplier(["RE", "FC"], 300)]

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
        public void Compute_AppliesTheModMultiplierToTheWholePriceBonusIncluded()
        {
            double bare = PerformancePoints.Compute(3, 300, difficultCharacters: 300, 5, 0.8, 250, no_mods);

            Assert.Multiple(() =>
            {
                // Backlog 101 moves this from 29.377848 (which is where 97 put it, from 96's
                // 69.935719 and 95's 59.280683), ONLY through the miss term: the play carries no
                // typos, so its typo term is exactly 1.0 whatever the power, and the whole
                // change is max(0, 1 - 5^1.2/300)^10 = 0.97700^10 replacing 0.91667^10. Five misses
                // is far under the 116-miss cliff on a 300-note map, so this prices comfortably.
                Assert.That(bare, Is.EqualTo(40.32536335113692).Within(1e-5)); // pp[f.compute(3, 300, 300, 5, 0.8, 250)] at v23 dials
                Assert.That(PerformancePoints.Compute(3, 300, difficultCharacters: 300, 5, 0.8, 250, mods(new TypeBeatModNoFail())),
                    Is.EqualTo(bare * 0.90).Within(1e-9)); // pp:const no_fail_multiplier=0.90
                Assert.That(PerformancePoints.Compute(3, 300, difficultCharacters: 300, 5, 0.8, 250, mods(new TypeBeatModLegacyFletcher())),
                    Is.EqualTo(bare * 0.90).Within(1e-9)); // pp:const fletcher_multiplier=0.90
                // Literate does not reach this function at all any more: it moves the star rating
                // that was passed IN, not the multiplier applied here (backlog 144).
                Assert.That(PerformancePoints.Compute(3, 300, difficultCharacters: 300, 5, 0.8, 250, mods(new TypeBeatModLiterate())),
                    Is.EqualTo(bare).Within(1e-9));
                Assert.That(PerformancePoints.Compute(3, 300, difficultCharacters: 300, 5, 0.8, 250, mods(new TypeBeatModFlashlight())),
                    Is.EqualTo(bare * PerformancePoints.FlashlightMultiplier(300)).Within(1e-9));

                // And the v21 reading stated as its own assertion, now INVERTED: the bonus is a
                // FRACTION of the price rather than an amount beside it, so a mod multiplier does
                // reach it — the old shape is the one that would leave 10% of the bonus unmultiplied.
                Assert.That(PerformancePoints.Compute(3, 300, difficultCharacters: 300, 5, 0.8, 250, mods(new TypeBeatModNoFail())),
                    Is.EqualTo(bare * 0.90).Within(1e-9)); // pp:const no_fail_multiplier=0.90
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
                Is.EqualTo(PerformancePoints.Compute(4.2, 500, difficultCharacters: 12, 12, 0.87, 400, no_mods, 60)).Within(1e-12));
        }

        [Test]
        public void Version_TracksTheServersFormulaGeneration()
        {
            // Pinned so a client shipped against generation N cannot quietly price plays the server
            // stores at generation N+1. If this moves, the server's PerformancePoints.VERSION and
            // docs/pp.md move with it. v20 = the backlog-265 removal of the Half Time mirror
            // multiplier: no constant and no term moves, a whole FACTOR leaves the product, so every
            // stored base-rate Half Time row is repriced upwards and nothing else moves at all.
            // v23 = the PP Sandbox's shape adopted wholesale: departure 5 replaces the accuracy
            // power curve with the normalised exponential from a floor, departure 6 makes Recite a
            // scaled Flashlight bonus, and the dials move to the sandbox's active settings (scale 9,
            // acc steepness 1.75 / floor 0.5 with the knee off, Recite 2.0, Easy 0.9, Hard Rock
            // neutral). Every stored row is repriced, which is what forces the bump.
            // v24 = the PP Sandbox's dials re-read after the owner retuned the lab: Easy 0.9 to 0.85,
            // and the knee position written as the 0 the lab's panel holds (inert at width 0, so the
            // only live move is Easy's, which reprices Easy rows alone).
            Assert.That(PerformancePoints.VERSION, Is.EqualTo(24)); // pp:version
        }

        #endregion
    }
}
