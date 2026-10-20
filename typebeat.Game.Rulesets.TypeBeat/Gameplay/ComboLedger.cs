// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// Where one combo increment was earned: the (line, cell) it was credited on, ordered
    /// lexicographically, which is the order the map is typed in and the order a seal's
    /// back-dating asks about (see <see cref="TypingEngine.BackDatedSealBreak"/>).
    /// </summary>
internal readonly record struct ComboPosition(int LineIndex, int CellIndex)
    {
        /// <summary>
        /// Whether this increment was earned AT OR BEFORE <paramref name="pivot"/>, i.e. is one
        /// of the ones a break back-dated to that cell destroys. The complement, strictly past
        /// the pivot, is what survives.
        /// </summary>
        public bool IsAtOrBefore(ComboPosition pivot)
            => LineIndex < pivot.LineIndex || (LineIndex == pivot.LineIndex && CellIndex <= pivot.CellIndex);
    }

    /// <summary>
    /// THE COMBO LEDGER: the current run, the longest run, WHERE each increment of the run was
    /// earned, and the redeemable claim a fixable break (a typo, or a word skip) leaves behind (with,
    /// since backlog 422, the older claims a newer break displaced, see <see cref="displaced"/>).
    /// <see cref="TypingEngine"/> owns one and decides WHEN each operation runs; this class decides
    /// what each does to the run and the claim, which is the whole of the combo arithmetic and none
    /// of the judging.
    ///
    /// <para>It reads no engine state. The line a claim or an increment belongs to is passed in, and
    /// so are the era rules a snapshot is taken under (<see cref="ClaimRules"/>), which is what lets
    /// the arithmetic be pinned directly (<c>ComboLedgerTest</c>) rather than only through whole
    /// played runs.</para>
    /// </summary>
    internal sealed class ComboLedger
    {
        /// <summary>
        /// The era rules a redeemable break is snapshotted under: the engine's
        /// <see cref="TypingEngine.ComboRestore"/>, <see cref="TypingEngine.ComboClaim"/>,
        /// <see cref="TypingEngine.SkipSpaceCredit"/>, <see cref="TypingEngine.LosslessSkipReclaim"/>,
        /// <see cref="TypingEngine.FoldsDisplacedClaim"/> and
        /// <see cref="TypingEngine.StacksDisplacedClaims"/>, read at the moment of the break.
        /// </summary>
        public readonly record struct ClaimRules(
            ComboRestoreRule Restore,
            ComboClaimRule Claim,
            SkipSpaceCreditRule SkipSpaceCredit,
            bool LosslessSkipReclaim,
            bool FoldsDisplacedClaim,
            bool StacksDisplacedClaims = false);

        private int combo;

        private int maxCombo;

        /// <summary>The current run.</summary>
        public int Combo => combo;

        /// <summary>The longest run the play has held.</summary>
        public int MaxCombo => maxCombo;

        /// <summary>Whether a redeemable claim is outstanding.</summary>
        public bool HasClaim => restorable is not null;

        /// <summary>
        /// The ledger's own length, for the tests that hold <c>runPositions.Count == combo</c>.
        /// </summary>
        internal int RunLength => runPositions.Count;

        /// <summary>
        /// WHERE each increment of the current run was earned, one entry per unit of
        /// <see cref="combo"/>, in the order they were credited. The ledger
        /// <see cref="TypingEngine.BackDatedSealBreak"/> needs and the only thing that can answer "how much of
        /// this run was earned past the cells this line is about to miss": combo is a single
        /// integer, and a break that keeps part of a run has to know which part.
        ///
        /// <para><c>runPositions.Count == combo</c> is the invariant, and it holds by construction
        /// because the four ways combo moves all move this with it: an increment appends
        /// (<see cref="Credit"/>), a break clears (<see cref="Break"/>, which hands the list
        /// to a redeemable break's snapshot), a restore puts the snapshot's own entries back, and the
        /// back-dated seal drops exactly the entries it destroys.</para>
        ///
        /// <para>Bounded by the map: a run is at most one increment per cell, since a cell that has
        /// been judged correct once is inert on every retype, so nothing here grows with the length
        /// of the play. Not readonly, because <see cref="Break"/> hands the whole list over to the
        /// snapshot rather than copying it.</para>
        /// </summary>
        private List<ComboPosition> runPositions = new List<ComboPosition>();

        /// <summary>
        /// The one outstanding combo snapshot (see <see cref="TypingEngine.ComboRestored"/>): the cell a wrong
        /// keypress spoiled or a word skip abandoned, and the streak that break cost, or null when
        /// there is nothing to go back for. Set by that keypress or skip (through
        /// <see cref="SnapshotRedeemableBreak"/>, the one write site the two share), redeemed by
        /// typing that same cell correctly, and discarded by any other combo break that had a streak
        /// to take (<see cref="Discard"/>).
        ///
        /// <para><c>ownPressCredit</c> is backlog 243: how much of the CURRENT run was credited by
        /// the claim's own press rather than typed after it. It is 1 for a claim a word skip took,
        /// because the skipping space is judged on the word gap it lands on and rebuilds the run to
        /// exactly 1 (<see cref="CreditTheClaimsOwnPress"/>), and 0 for every other claim and for a
        /// skip whose space credited nothing. A break standing on no more than that is passive, the
        /// same way a break landing at zero is, and SPENDS the credit when it does. See
        /// <see cref="TypingEngine.SkipSpaceCredit"/>.</para>
        ///
        /// <para><c>positions</c> is the run the break took, cell for cell (see
        /// <see cref="runPositions"/>), so a redemption puts back not just HOW MUCH combo the break
        /// cost but WHERE it was earned. Without it a restored streak would have to be dated at the
        /// cell that redeemed it, and a later seal on an earlier line would then keep increments its
        /// misses are entitled to destroy. Its length is always the <c>streak</c> beside it.</para>
        /// </summary>
        private (int lineIndex, int cellIndex, int streak, int ownPressCredit, List<ComboPosition> positions)? restorable;

        /// <summary>
        /// The claims a newer break DISPLACED from <see cref="restorable"/>, oldest first, each still
        /// waiting for its own cell (backlog 422, see <see cref="TypingEngine.StacksDisplacedClaims"/>).
        /// Empty under every other era, which is what keeps every operation below exactly what it was
        /// for a stored replay: the stack is only ever pushed by
        /// <see cref="SnapshotRedeemableBreak"/> under that rule.
        ///
        /// <para>Read as a chain of runs separated by breaks: entry <c>i</c> holds the run its break
        /// cut off, and the run between it and the next newer break is held by entry <c>i + 1</c> (or
        /// by <see cref="restorable"/> for the newest). Correcting a cell therefore JOINS its run to the
        /// next newer one, which is a refund to the current run only when no newer break is still
        /// outstanding. <c>positions.Count == streak</c> holds for every entry, as it does for the
        /// claim on top.</para>
        ///
        /// <para>Never non-empty while <see cref="restorable"/> is null: a redemption of the top claim
        /// brings the newest entry back up, and every discard clears both.</para>
        /// </summary>
        private readonly List<(int lineIndex, int cellIndex, int streak, List<ComboPosition> positions)> displaced =
            new List<(int lineIndex, int cellIndex, int streak, List<ComboPosition> positions)>();

        /// <summary>
        /// A combo break that is nobody's fixable typo happened, so the outstanding snapshot (if
        /// any) is discarded: the streak it was holding has been lost to THIS break, and correcting
        /// the older cell later cannot bring back a run that ended after it. Called at every
        /// <see cref="TypingEngine.ComboBroken"/> seam except the two that can be walked back into
        /// and are therefore snapshotted instead: a wrong keypress, and (since backlog 167) a word
        /// skip. Every displaced claim below it goes too, for the same reason: their runs could
        /// only ever reach the current one through this break.
        /// </summary>
        public void Discard()
        {
            restorable = null;
            displaced.Clear();
        }

        /// <summary>
        /// A sealed line's cells can never be typed again, so a claim taken on
        /// <paramref name="lineIndex"/> is unredeemable and is dropped, whether or not the seal broke
        /// anything.
        ///
        /// <para>Under <see cref="TypingEngine.StacksDisplacedClaims"/> the claim can be one of the
        /// displaced ones as well as the one on top. Its break is now permanent, so the NEWEST claim on
        /// the line goes and so does every claim OLDER than it, whatever line those are on: the runs
        /// they hold could only ever have joined the current one through the break that just became
        /// final. Claims newer than it are untouched, which is backlog 422's point: the earlier line's
        /// typo stops being anything a later correction can refund.</para>
        /// </summary>
        public void DropClaimOn(int lineIndex)
        {
            if (restorable?.lineIndex == lineIndex)
            {
                restorable = null;
                displaced.Clear();
                return;
            }

            for (int i = displaced.Count - 1; i >= 0; i--)
            {
                if (displaced[i].lineIndex == lineIndex)
                {
                    displaced.RemoveRange(0, i + 1);
                    return;
                }
            }
        }

        /// <summary>Every piece of run state back to the moment before the first frame.</summary>
        public void Reset()
        {
            combo = 0;
            maxCombo = 0;
            runPositions.Clear();
            restorable = null;
            displaced.Clear();
        }

        /// <summary>
        /// Credit one combo increment, earned on the cell the press landed on (not on the caret,
        /// which is not the same thing under <see cref="TypingEngine.AnyOrderWithinWord"/>). The one
        /// place <see cref="combo"/> grows by a keypress, so <see cref="runPositions"/> cannot fall
        /// behind it.
        /// </summary>
        public void Credit(int lineIndex, int cellIndex)
        {
            combo++;
            maxCombo = Math.Max(maxCombo, combo);
            runPositions.Add(new ComboPosition(lineIndex, cellIndex));
        }

        /// <summary>
        /// Zero the run and hand back the positions that composed it: a REDEEMABLE break puts them on
        /// its snapshot (<see cref="SnapshotRedeemableBreak"/>) so a redemption can restore them, and
        /// every other break simply drops them. The one place a break empties the ledger, which is
        /// what keeps <c>runPositions.Count == combo</c> true through all of them.
        /// </summary>
        public List<ComboPosition> Break()
        {
            var broken = runPositions;

            runPositions = new List<ComboPosition>();
            combo = 0;

            return broken;
        }

        /// <summary>
        /// A break dated at <paramref name="pivot"/> rather than at now (see
        /// <see cref="TypingEngine.BackDatedSealBreak"/>): every increment earned at or before that cell is
        /// destroyed and every increment earned strictly past it survives, in place. Returns the
        /// surviving run, which is also <see cref="combo"/>'s new value.
        ///
        /// <para>Compacts the ledger in place rather than filtering into a new list, because the
        /// survivors are the tail of a run in the common case and this is on the seal path. Order is
        /// preserved, which matters only for readability: nothing reads a position's index.</para>
        /// </summary>
        public int BackDateTo(ComboPosition pivot)
        {
            int kept = 0;

            for (int i = 0; i < runPositions.Count; i++)
            {
                if (!runPositions[i].IsAtOrBefore(pivot))
                    runPositions[kept++] = runPositions[i];
            }

            runPositions.RemoveRange(kept, runPositions.Count - kept);

            // MaxCombo is deliberately not touched: it records a run the player really did hold, and
            // this break is dated in the past, not a claim that the run never happened.
            combo = kept;

            return kept;
        }

        /// <summary>
        /// Take the snapshot for a REDEEMABLE break (a wrong keypress, or a word skip): the streak
        /// it cost, against the cell the player has to come back to. The one write site for
        /// <see cref="restorable"/> other than the discards, so the two breaks that can be walked
        /// back into cannot drift apart.
        ///
        /// <para>A break takes ownership of the streak only if it HAS a streak to own (backlog 176).
        /// One landing at a combo of zero costs the player nothing, so it does not get to end an
        /// older cell's claim on a run that is still redeemable: the claim it would write is empty,
        /// and swapping a live claim for an empty one is a pure loss to a player who then goes back
        /// and fixes both cells. With NOTHING outstanding it still writes its own empty claim, so
        /// that redeeming it restores nothing, which is what
        /// <see cref="Redeem"/> has always done with a zero.</para>
        ///
        /// <para>"A streak to own" excludes the streak the OUTSTANDING claim's own press credited
        /// (backlog 243). One press can both take a claim and rebuild the run: a space struck inside
        /// a word abandons the rest of it and is then judged on the word gap it lands on, which puts
        /// the combo back to 1. A break on that very gap therefore broke a run of 1 rather than of 0,
        /// and under 176 alone that was enough to overwrite a claim hundreds deep with a worthless
        /// one. The 1 was not progress, it was the break's own press, so a break taking no more than
        /// the claim's own credit is passive exactly as a zero-streak break is, and SPENDS that credit
        /// on its way past: whatever the player rebuilds after this break is measured from zero, so
        /// the next break arms normally. See <see cref="TypingEngine.SkipSpaceCredit"/> for the era
        /// arm.</para>
        ///
        /// <para>A passive break SPENDS a run without owning it, and since backlog 260 that run is
        /// FOLDED INTO the claim it leaves standing rather than dropped (see
        /// <see cref="TypingEngine.LosslessSkipReclaim"/>): the call site has already run <see cref="Break"/>,
        /// so anything the claim does not take is gone for good, and the cells that earned it are
        /// resolved, so no retype can earn it back. Folding is what makes "an accidental skip, fully
        /// corrected, costs nothing" true of a double space as well as of a single one.</para>
        ///
        /// <para>A break that DOES own its streak takes the claim, and since backlog 262 it takes the
        /// displaced claim's streak WITH it (see <see cref="TypingEngine.FoldsDisplacedClaim"/>) rather than
        /// dropping it: the older break's increments are just as unreachable as a passive break's, and
        /// a player who corrects both accidents in full is entitled to both. The chain is then
        /// redeemed at the NEWEST of the broken cells, and the seal's back-dated break (backlog 259)
        /// is what still takes back anything the line never really earned.</para>
        ///
        /// <para>Under <see cref="ComboRestoreRule.Never"/> no snapshot exists at all, so the break
        /// is as final here as it is everywhere else.</para>
        ///
        /// <para>The claim is taken against cell <paramref name="cellIndex"/> of line
        /// <paramref name="lineIndex"/>, under the engine's era <paramref name="rules"/>.</para>
        /// </summary>
        public void SnapshotRedeemableBreak(int lineIndex, int cellIndex, int brokenStreak, List<ComboPosition> brokenPositions, ClaimRules rules)
        {
            if (!EraRules.FixRestoresTheComboBreak(rules.Restore))
            {
                Discard();
                return;
            }

            if (restorable is (int heldLine, int heldCell, int heldStreak, int ownPressCredit, var heldPositions)
                && EraRules.OnlyABreakWithAStreakTakesTheClaim(rules.Claim))
            {
                // The most this break can have taken and still be passive. Zero is backlog 176's
                // rule, and it is what the pre-243 arm keeps: the credit is READ through the era
                // switch rather than written through it, so the claim itself says the same thing
                // under both arms and only its consequence moves.
                int passive = EraRules.TheClaimsOwnCreditIsNotAStreak(rules.SkipSpaceCredit) ? ownPressCredit : 0;

                if (brokenStreak <= passive)
                {
                    if (!rules.LosslessSkipReclaim)
                    {
                        // The pre-260 arm: the held claim keeps its OWN streak and positions, and the
                        // run this break spent is dropped on the floor. The call site has already run
                        // breakRun, so those increments are gone with nothing left to redeem them,
                        // and the cells that earned them are Correct with a FirstCorrectDelta, so no
                        // retype can ever earn them again. That is the second half of backlog 260's
                        // missing increment.
                        restorable = (heldLine, heldCell, heldStreak, 0, heldPositions);
                        return;
                    }

                    // BACKLOG 260: a passive break took nothing the player earned, but it still SPENT
                    // a run, so the run folds INTO the claim it left standing rather than being
                    // dropped. Streak and positions move together (positions.Count == streak is the
                    // ledger's invariant, see runPositions), and the broken ones append in run order
                    // because the claim's own increments were earned before them. Redeeming the claim
                    // then puts back the whole of what the two breaks between them cost.
                    if (brokenStreak > 0)
                    {
                        var folded = new List<ComboPosition>(heldPositions.Count + brokenPositions.Count);

                        folded.AddRange(heldPositions);
                        folded.AddRange(brokenPositions);
                        heldPositions = folded;
                    }

                    // The credit is still zeroed: the exemption is worth exactly one break (backlog
                    // 243), and folding the run in does not re-arm it.
                    restorable = (heldLine, heldCell, heldStreak + brokenStreak, 0, heldPositions);
                    return;
                }
            }

            // BACKLOG 262: this break OWNS the streak it broke, so it takes the claim, but the claim
            // it displaces is not therefore worthless: the increments behind it were earned and the
            // cells that earned them are resolved, so discarding it loses them for good even though
            // the player can still go back and correct both accidents. The displaced claim FOLDS into
            // this one instead (see FoldsDisplacedClaim), which makes the newest of the broken cells
            // the one that redeems the whole chain, and chains transitively through a third break and
            // a fourth. Positions go in front of this break's own, oldest first, because a redemption
            // puts them back at the head of the ledger; the count still equals the streak; and the
            // credit still starts at zero, so backlog 243's exemption is not re-armed by folding.
            //
            // BACKLOG 422 refines that: the fold above credits the displaced streak whether or not its
            // own cell is ever corrected, so a typo left wrong on a line that has since sealed was
            // refunded by correcting the NEWER one. Under StacksDisplacedClaims the displaced claim is
            // kept, separately, below this one: each break's run comes back only when its own cell
            // is corrected (see Redeem), and a claim whose line seals is dropped (see DropClaimOn).
            // Two accidents both corrected still cost nothing, in either order. A displaced claim
            // holding nothing is not kept, exactly as the fold below ignores one.
            if (rules.FoldsDisplacedClaim && rules.StacksDisplacedClaims)
            {
                if (restorable is (int keptLine, int keptCell, int keptStreak, _, var keptPositions) && keptStreak > 0)
                    displaced.Add((keptLine, keptCell, keptStreak, keptPositions));

                restorable = (lineIndex, cellIndex, brokenStreak, 0, brokenPositions);
                return;
            }

            if (rules.FoldsDisplacedClaim && restorable is (_, _, int displacedStreak, _, var displacedPositions) && displacedStreak > 0)
            {
                var folded = new List<ComboPosition>(displacedPositions.Count + brokenPositions.Count);

                folded.AddRange(displacedPositions);
                folded.AddRange(brokenPositions);

                restorable = (lineIndex, cellIndex, displacedStreak + brokenStreak, 0, folded);
                return;
            }

            restorable = (lineIndex, cellIndex, brokenStreak, 0, brokenPositions);
        }

        /// <summary>
        /// The press that just took (or passively kept) the outstanding claim has itself credited one
        /// combo: record that on the claim, so a break landing before the player has typed anything
        /// else takes nothing and leaves the claim alone (backlog 243, see
        /// <see cref="SnapshotRedeemableBreak"/>).
        ///
        /// <para>Called from the one arm that can be reached by such a press: a word skip's space,
        /// falling through to be judged on the word gap the skip parked the caret on. It is deliberately
        /// keyed on the press having ACTUALLY credited combo rather than on the skip having happened,
        /// so a skip whose space earned nothing (an inert retype of an already judged gap, the rush cap
        /// refusing a caret that was ALREADY out past its bound before the skip, or a word abandoned
        /// all the way to the end of a line, where there is no gap for the space to land on at all)
        /// records no credit and behaves exactly as backlog 176 left it. The word this press gave up
        /// no longer counts against that bound (backlog 260, see <see cref="TypingEngine.LosslessSkipReclaim"/>),
        /// which is why the cap refusing here is now a statement about the player's own lead.</para>
        /// </summary>
        public void CreditTheClaimsOwnPress()
        {
            if (restorable is (int lineIndex, int cellIndex, int streak, _, var positions))
                restorable = (lineIndex, cellIndex, streak, 1, positions);
        }

        /// <summary>
        /// Redeem the outstanding snapshot if the cell about to be typed correctly is the cell it
        /// was taken against: the run resumes at that streak plus everything earned since, which is
        /// exactly <c>combo + streak</c> because no break has landed in between (any that had would
        /// have discarded the snapshot). The claim is spent either way, so a second correct retype
        /// of the same cell restores nothing.
        ///
        /// <para>Two breaks can be waiting here, and the redemption is identical for both: the wrong
        /// keypress that spoiled the cell (backlog 140), and the word skip that abandoned it
        /// (backlog 167). In both cases the cell is the one the player has to come back to, so
        /// typing it is what says they came back.</para>
        ///
        /// <para>Under <see cref="TypingEngine.StacksDisplacedClaims"/> the cell can also be one a
        /// DISPLACED claim is waiting for (backlog 422, see <see cref="displaced"/>). Correcting it
        /// joins that claim's run to the run after it, which is still broken by a newer outstanding
        /// claim, so nothing is refunded NOW: the streak and positions fold into the next newer claim
        /// (oldest positions in front), and come back when that one is redeemed in turn. Redeeming the
        /// claim on top brings the newest displaced one back up to wait for its own cell, so two
        /// accidents corrected in either order both come back, and one left wrong stays lost.</para>
        ///
        /// <para>Returns the streak put back, which the engine announces on
        /// <see cref="TypingEngine.ComboRestored"/>, or 0 when nothing was restored.</para>
        /// </summary>
        public int Redeem(int activeLineIndex, int cellIndex)
        {
            joinDisplacedClaimsOn(activeLineIndex, cellIndex);

            if (restorable is not (int lineIndex, int typoCellIndex, int streak, _, var positions))
                return 0;

            if (lineIndex != activeLineIndex || typoCellIndex != cellIndex)
                return 0;

            restorable = null;

            // The newest displaced claim is the next break back, and with this one corrected the
            // current run reaches all the way to it: it is the claim on top now. Its press credit is
            // zero, since the run it would describe is no longer the one its own press rebuilt.
            if (displaced.Count > 0)
            {
                var (nextLine, nextCell, nextStreak, nextPositions) = displaced[^1];

                displaced.RemoveAt(displaced.Count - 1);
                restorable = (nextLine, nextCell, nextStreak, 0, nextPositions);
            }

            // A break that cost nothing restores nothing, and announcing it would have every
            // consumer write back a combo it already holds.
            if (streak <= 0)
                return 0;

            combo += streak;
            maxCombo = Math.Max(maxCombo, combo);

            // The restored increments go back WHERE THEY WERE EARNED, at the head of the run, not at
            // the cell that redeemed them: a later seal on an earlier line back-dates against those
            // positions, and dating them here would let a break's misses keep combo they are
            // entitled to destroy (see runPositions).
            runPositions.InsertRange(0, positions);

            return streak;
        }

        /// <summary>
        /// Fold every displaced claim waiting for cell <paramref name="cellIndex"/> of line
        /// <paramref name="lineIndex"/> into the next newer claim (backlog 422, see
        /// <see cref="Redeem"/>). Walked oldest first, so a claim folded into a newer one that is
        /// waiting for the same cell is carried on with it. A no-op whenever the stack is empty, which
        /// is every era but <see cref="TypingEngine.StacksDisplacedClaims"/>.
        /// </summary>
        private void joinDisplacedClaimsOn(int lineIndex, int cellIndex)
        {
            int i = 0;

            while (i < displaced.Count)
            {
                var (claimLine, claimCell, claimStreak, claimPositions) = displaced[i];

                if (claimLine != lineIndex || claimCell != cellIndex)
                {
                    i++;
                    continue;
                }

                displaced.RemoveAt(i);

                // Index i now names the next newer displaced claim, or, past the end, the claim on
                // top (never null while the stack is non-empty). The joined run was earned first, so
                // its positions go in front; the counts still equal the streaks.
                if (i < displaced.Count)
                {
                    var (newerLine, newerCell, newerStreak, newerPositions) = displaced[i];
                    displaced[i] = (newerLine, newerCell, claimStreak + newerStreak, joined(claimPositions, newerPositions));
                }
                else if (restorable is (int topLine, int topCell, int topStreak, int topCredit, var topPositions))
                {
                    restorable = (topLine, topCell, claimStreak + topStreak, topCredit, joined(claimPositions, topPositions));
                }
            }
        }

        private static List<ComboPosition> joined(List<ComboPosition> older, List<ComboPosition> newer)
        {
            var result = new List<ComboPosition>(older.Count + newer.Count);

            result.AddRange(older);
            result.AddRange(newer);
            return result;
        }
    }
}
