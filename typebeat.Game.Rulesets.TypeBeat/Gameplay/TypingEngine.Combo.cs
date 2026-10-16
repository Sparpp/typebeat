// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// TypingEngine, THE COMBO: the engine's side of the combo ledger. The run, its ledger of where each
// increment was earned and the one redeemable claim live in ComboLedger; these members bind each of
// its operations to the line the caret is on and to the engine's era rules, under the names the
// rest of the engine (and the JS port's comments) call them by.

using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        public int Combo => combo;

        public int MaxCombo => maxCombo;

        /// <summary>The run, the longest run, the run's positions and the redeemable claim (see <see cref="ComboLedger"/>).</summary>
        private readonly ComboLedger ledger = new ComboLedger();

        private int combo => ledger.Combo;

        private int maxCombo => ledger.MaxCombo;

        /// <summary>The era rules a redeemable break is snapshotted under, as this engine is set now.</summary>
        private ComboLedger.ClaimRules claimRules => new ComboLedger.ClaimRules(ComboRestore, ComboClaim, SkipSpaceCredit, LosslessSkipReclaim, FoldsDisplacedClaim);

        /// <summary>Discard the outstanding claim (see <see cref="ComboLedger.Discard"/>).</summary>
        private void discardRestorableStreak() => ledger.Discard();

        /// <summary>Credit one increment, earned on <paramref name="cellIndex"/> of the active line (see <see cref="ComboLedger.Credit"/>).</summary>
        private void creditCombo(int cellIndex) => ledger.Credit(activeLineIndex, cellIndex);

        /// <summary>Zero the run and hand back its positions (see <see cref="ComboLedger.Break"/>).</summary>
        private List<ComboPosition> breakRun() => ledger.Break();

        /// <summary>A break dated at <paramref name="pivot"/> (see <see cref="ComboLedger.BackDateTo"/>).</summary>
        private int backDateBreakTo(ComboPosition pivot) => ledger.BackDateTo(pivot);

        /// <summary>
        /// Take the snapshot for a REDEEMABLE break against <paramref name="cellIndex"/> of the active
        /// line, under this engine's era rules (see <see cref="ComboLedger.SnapshotRedeemableBreak"/>).
        /// </summary>
        private void snapshotRedeemableBreak(int cellIndex, int brokenStreak, List<ComboPosition> brokenPositions)
            => ledger.SnapshotRedeemableBreak(activeLineIndex, cellIndex, brokenStreak, brokenPositions, claimRules);

        /// <summary>Record the claim's own press credit (see <see cref="ComboLedger.CreditTheClaimsOwnPress"/>).</summary>
        private void creditTheClaimsOwnPress() => ledger.CreditTheClaimsOwnPress();

        /// <summary>
        /// Redeem the outstanding claim if <paramref name="cellIndex"/> of the active line is the cell
        /// it was taken against (see <see cref="ComboLedger.Redeem"/>), and announce the streak put
        /// back on <see cref="ComboRestored"/>.
        /// </summary>
        private void resumeStreakIfThisRedeemsTheBreak(int cellIndex)
        {
            int restored = ledger.Redeem(activeLineIndex, cellIndex);

            if (restored > 0)
                raise(ComboRestored, restored);
        }
    }
}
