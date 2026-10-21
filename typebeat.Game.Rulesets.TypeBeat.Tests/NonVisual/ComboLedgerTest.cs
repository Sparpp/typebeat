// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Direct pins on the combo arithmetic TypingEngine delegates to ComboLedger. ComboRestoreTest,
// SealComboBreakTest and their WireCompat twins hold the same rules through whole played runs; these
// hold them one operation at a time, with the era rules handed in explicitly, so a failure here names
// the operation rather than a keystroke sequence.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class ComboLedgerTest
    {
        private static readonly ComboLedger.ClaimRules live = new ComboLedger.ClaimRules(
            ComboRestoreRule.OnFix, ComboClaimRule.StreakedBreakWins, SkipSpaceCreditRule.NotAStreakOfItsOwn,
            LosslessSkipReclaim: true, FoldsDisplacedClaim: true, StacksDisplacedClaims: true);

        private static ComboLedger runOf(int length, int line = 0)
        {
            var ledger = new ComboLedger();

            for (int i = 0; i < length; i++)
                ledger.Credit(line, i);

            return ledger;
        }

        [Test]
        public void CreditGrowsTheRunAndTheLedgerTogether()
        {
            var ledger = runOf(5);

            Assert.Multiple(() =>
            {
                Assert.That(ledger.Combo, Is.EqualTo(5));
                Assert.That(ledger.MaxCombo, Is.EqualTo(5));
                Assert.That(ledger.RunLength, Is.EqualTo(ledger.Combo), "one position per increment");
                Assert.That(ledger.HasClaim, Is.False);
            });
        }

        [Test]
        public void BreakZeroesTheRunHandsBackItsPositionsAndKeepsTheMax()
        {
            var ledger = runOf(4);
            var broken = ledger.Break();

            Assert.Multiple(() =>
            {
                Assert.That(broken.Select(p => p.CellIndex), Is.EqualTo(new[] { 0, 1, 2, 3 }));
                Assert.That(ledger.Combo, Is.Zero);
                Assert.That(ledger.RunLength, Is.Zero);
                Assert.That(ledger.MaxCombo, Is.EqualTo(4));
            });
        }

        [Test]
        public void BackDatingKeepsOnlyTheIncrementsEarnedPastThePivot()
        {
            var ledger = runOf(3, line: 0);
            ledger.Credit(1, 0);
            ledger.Credit(1, 1);

            int kept = ledger.BackDateTo(new ComboPosition(0, 2));

            Assert.Multiple(() =>
            {
                Assert.That(kept, Is.EqualTo(2), "the two increments on line 1 survive a break dated on line 0's last cell");
                Assert.That(ledger.Combo, Is.EqualTo(2));
                Assert.That(ledger.RunLength, Is.EqualTo(2));
                Assert.That(ledger.MaxCombo, Is.EqualTo(5), "the max records a run the player really held");
            });
        }

        [Test]
        public void ARedeemedClaimPutsTheBrokenStreakBackOnTopOfWhatWasEarnedSince()
        {
            var ledger = runOf(6);
            int streak = ledger.Combo;

            ledger.SnapshotRedeemableBreak(0, 6, streak, ledger.Break(), live);
            ledger.Credit(0, 7);
            ledger.Credit(0, 8);

            Assert.That(ledger.Redeem(0, 5), Is.Zero, "another cell redeems nothing");
            Assert.That(ledger.HasClaim, Is.True, "and leaves the claim standing");

            int restored = ledger.Redeem(0, 6);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.EqualTo(6));
                Assert.That(ledger.Combo, Is.EqualTo(8));
                Assert.That(ledger.RunLength, Is.EqualTo(8));
                Assert.That(ledger.MaxCombo, Is.EqualTo(8));
                Assert.That(ledger.HasClaim, Is.False, "the claim is spent");
                Assert.That(ledger.Redeem(0, 6), Is.Zero, "so a second retype restores nothing");
            });
        }

        [Test]
        public void NoClaimIsTakenWhenTheFixDoesNotRestore()
        {
            var ledger = runOf(3);
            var never = live with { Restore = ComboRestoreRule.Never };

            ledger.SnapshotRedeemableBreak(0, 3, 3, ledger.Break(), never);

            Assert.That(ledger.HasClaim, Is.False);
        }

        [Test]
        public void AZeroStreakBreakLeavesTheOutstandingClaimAloneOnlyUnderTheLiveClaimRule()
        {
            foreach (var claim in new[] { ComboClaimRule.StreakedBreakWins, ComboClaimRule.LatestBreakWins })
            {
                var ledger = runOf(4);
                var rules = live with { Claim = claim };

                ledger.SnapshotRedeemableBreak(0, 4, 4, ledger.Break(), rules);
                ledger.SnapshotRedeemableBreak(0, 5, 0, ledger.Break(), rules);

                int fromFirst = ledger.Redeem(0, 4);

                if (claim == ComboClaimRule.StreakedBreakWins)
                    Assert.That(fromFirst, Is.EqualTo(4), "the zero-streak break was passive");
                else
                    Assert.That(fromFirst, Is.Zero, "the latest break took the claim with nothing in it");
            }
        }

        [Test]
        public void ABreakTakingOnlyTheClaimsOwnPressCreditIsPassive()
        {
            var ledger = runOf(5);

            ledger.SnapshotRedeemableBreak(0, 5, 5, ledger.Break(), live);
            ledger.Credit(0, 9);
            ledger.CreditTheClaimsOwnPress();

            // A break of exactly that one credited increment does not take the claim, and its run
            // folds into the claim it leaves standing.
            ledger.SnapshotRedeemableBreak(0, 10, 1, ledger.Break(), live);

            Assert.That(ledger.Redeem(0, 5), Is.EqualTo(6));

            // Under the earlier arm the same break owns its streak of 1, takes the claim and folds
            // the displaced one into it.
            var old = runOf(5);
            var astreak = live with { SkipSpaceCredit = SkipSpaceCreditRule.AStreakLikeAnyOther };

            old.SnapshotRedeemableBreak(0, 5, 5, old.Break(), astreak);
            old.Credit(0, 9);
            old.CreditTheClaimsOwnPress();
            old.SnapshotRedeemableBreak(0, 10, 1, old.Break(), astreak);

            Assert.Multiple(() =>
            {
                Assert.That(old.Redeem(0, 5), Is.Zero, "the older cell no longer holds the claim");
                Assert.That(old.Redeem(0, 10), Is.EqualTo(6), "the newest broken cell redeems the folded chain");
            });
        }

        [Test]
        public void APassiveBreaksRunIsDroppedBeforeLosslessSkipReclaim()
        {
            var ledger = runOf(5);
            var pre260 = live with { LosslessSkipReclaim = false };

            ledger.SnapshotRedeemableBreak(0, 5, 5, ledger.Break(), pre260);
            ledger.Credit(0, 9);
            ledger.CreditTheClaimsOwnPress();
            ledger.SnapshotRedeemableBreak(0, 10, 1, ledger.Break(), pre260);

            Assert.That(ledger.Redeem(0, 5), Is.EqualTo(5), "the claim keeps its own streak and the spent increment is lost");
        }

        [Test]
        public void AnOwningBreakDropsTheDisplacedClaimBeforeFoldsDisplacedClaim()
        {
            var ledger = runOf(4);
            var pre262 = live with { FoldsDisplacedClaim = false };

            ledger.SnapshotRedeemableBreak(0, 4, 4, ledger.Break(), pre262);
            ledger.Credit(0, 5);
            ledger.Credit(0, 6);
            ledger.SnapshotRedeemableBreak(0, 7, 2, ledger.Break(), pre262);

            Assert.Multiple(() =>
            {
                Assert.That(ledger.Redeem(0, 4), Is.Zero);
                Assert.That(ledger.Redeem(0, 7), Is.EqualTo(2), "only the owning break's own streak comes back");
            });
        }

        [Test]
        public void ADisplacedClaimIsStackedUnderTheBitAndFoldedWithoutIt()
        {
            var folded = runOf(4);
            var pre422 = live with { StacksDisplacedClaims = false };

            folded.SnapshotRedeemableBreak(0, 4, 4, folded.Break(), pre422);
            folded.Credit(0, 5);
            folded.Credit(0, 6);
            folded.SnapshotRedeemableBreak(0, 7, 2, folded.Break(), pre422);

            var stacked = runOf(4);

            stacked.SnapshotRedeemableBreak(0, 4, 4, stacked.Break(), live);
            stacked.Credit(0, 5);
            stacked.Credit(0, 6);
            stacked.SnapshotRedeemableBreak(0, 7, 2, stacked.Break(), live);

            Assert.Multiple(() =>
            {
                Assert.That(folded.Redeem(0, 7), Is.EqualTo(6), "backlog 262: the newer cell redeems both, the older one uncorrected");
                Assert.That(stacked.Redeem(0, 7), Is.EqualTo(2), "backlog 422: only the run its own break broke");
                Assert.That(stacked.HasClaim, Is.True, "the older claim is back on top, waiting for its own cell");
                Assert.That(stacked.Redeem(0, 4), Is.EqualTo(4), "and it pays when that cell is corrected");
                Assert.That(stacked.Combo, Is.EqualTo(6));
                Assert.That(stacked.RunLength, Is.EqualTo(stacked.Combo), "one position per increment through both refunds");
            });
        }

        [Test]
        public void CorrectingADisplacedCellJoinsItsRunToTheNextNewerClaim()
        {
            var ledger = runOf(3);

            ledger.SnapshotRedeemableBreak(0, 3, 3, ledger.Break(), live); // A: 3
            ledger.Credit(0, 4);
            ledger.Credit(0, 5);
            ledger.SnapshotRedeemableBreak(0, 6, 2, ledger.Break(), live); // B: 2
            ledger.Credit(0, 7);
            ledger.SnapshotRedeemableBreak(0, 8, 1, ledger.Break(), live); // C: 1

            Assert.Multiple(() =>
            {
                Assert.That(ledger.Redeem(0, 3), Is.Zero, "B is still broken, so A's run joins B's rather than the current run");
                Assert.That(ledger.Redeem(0, 8), Is.EqualTo(1), "C pays only its own run");
                Assert.That(ledger.Redeem(0, 6), Is.EqualTo(5), "B pays its own 2 and the 3 A joined to it");
                Assert.That(ledger.Combo, Is.EqualTo(6));
                Assert.That(ledger.RunLength, Is.EqualTo(6));
                Assert.That(ledger.HasClaim, Is.False);
            });
        }

        [Test]
        public void DroppingADisplacedClaimDropsEveryOlderOneAndKeepsTheNewer()
        {
            var ledger = runOf(3, line: 0);

            ledger.SnapshotRedeemableBreak(0, 3, 3, ledger.Break(), live); // A on line 0
            ledger.Credit(1, 0);
            ledger.SnapshotRedeemableBreak(1, 1, 1, ledger.Break(), live); // B on line 1
            ledger.Credit(2, 0);
            ledger.Credit(2, 1);
            ledger.SnapshotRedeemableBreak(2, 2, 2, ledger.Break(), live); // C on line 2

            ledger.DropClaimOn(1);

            Assert.Multiple(() =>
            {
                Assert.That(ledger.HasClaim, Is.True, "C is newer than the sealed line's claim");
                Assert.That(ledger.Redeem(1, 1), Is.Zero, "B went with its line");
                Assert.That(ledger.Redeem(2, 2), Is.EqualTo(2), "C pays its own run");
                Assert.That(ledger.HasClaim, Is.False, "and A, older than B, went with it: its run could only ever have joined through B");
            });
        }

        [Test]
        public void RestoredIncrementsGoBackWhereTheyWereEarned()
        {
            var ledger = runOf(3, line: 0);

            ledger.SnapshotRedeemableBreak(1, 0, 3, ledger.Break(), live);
            ledger.Credit(1, 1);
            ledger.Redeem(1, 0);

            // Dated on line 0, the restored increments are the ones a break back-dated to line 0
            // destroys; the increment earned on line 1 survives it.
            Assert.That(ledger.BackDateTo(new ComboPosition(0, 2)), Is.EqualTo(1));
        }

        [Test]
        public void DropClaimOnForgetsOnlyThatLinesClaimAndResetClearsEverything()
        {
            var ledger = runOf(3);

            ledger.SnapshotRedeemableBreak(0, 3, 3, ledger.Break(), live);
            ledger.DropClaimOn(1);
            Assert.That(ledger.HasClaim, Is.True);

            ledger.DropClaimOn(0);
            Assert.That(ledger.HasClaim, Is.False);

            ledger.Credit(0, 4);
            ledger.Reset();

            Assert.Multiple(() =>
            {
                Assert.That(ledger.Combo, Is.Zero);
                Assert.That(ledger.MaxCombo, Is.Zero);
                Assert.That(ledger.RunLength, Is.Zero);
                Assert.That(ledger.HasClaim, Is.False);
            });
        }
    }
}
