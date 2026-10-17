// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// The ENGINE events that move osu's <see cref="ScoreProcessor.Combo"/> (and the mistype count
    /// beside it) without travelling on a judgement result, plus the combo-neutral marks that stop a
    /// later result taking a break a second time, in one place. osu's combo is maintained
    /// incrementally off results, so every break or restore the engine takes where no result can
    /// carry it has to be written by hand; these are those writes. Until they lived here they were
    /// written twice, as private handlers in <c>TypeBeatPlayfield</c> and as local functions in
    /// <see cref="TypeBeatReplayScorer"/>, and live play and recalculation could only agree on
    /// <c>max_combo</c> as long as the two copies did.
    ///
    /// <list type="bullet">
    /// <item><see cref="TypingEngine.CharJudged"/>: the pre-347 rush cap's break on a press that is
    /// still a hit (<see cref="OnCharJudged"/>), AFTER the cell's own result.</item>
    /// <item><see cref="TypingEngine.Mistyped"/>: the wrong keypress's break under
    /// <see cref="TypoRule.Deferred"/>, and the mistype count (<see cref="OnMistyped"/>).</item>
    /// <item><see cref="TypingEngine.WrongKeyRejected"/>: the rejected key's break under
    /// <see cref="TypoRule.ImmediateMiss"/>, where it lived before backlog 109
    /// (<see cref="OnWrongKeyRejected"/>).</item>
    /// <item><see cref="TypingEngine.ComboRestored"/>: the streak a corrected typo or a reclaimed
    /// skip resumes (backlog 140, widened by 167).</item>
    /// <item><see cref="TypingEngine.WordAbandoned"/> and <see cref="TypingEngine.AbandonSealed"/>:
    /// the skip's one break, and the marks that stop its cells' seal Misses taking it again
    /// (backlog 167).</item>
    /// <item><see cref="TypingEngine.LineSealed"/>: the back-dated seal break
    /// (<see cref="OnLineSealed"/>, backlog 259), BEFORE the seal's results, each of which
    /// <see cref="SealedCellResult"/> decides and marks.</item>
    /// </list>
    ///
    /// <para>The RESULTS themselves are the caller's: the playfield hands them to its drawables, the
    /// scorer to its own cell registry. <see cref="Attach"/> takes both as callbacks so the order of
    /// each shared event (the cell's result, then the rush-cap break; the seal break, then the seal's
    /// results) is fixed here rather than by the order two subscriptions happen to be made in.</para>
    ///
    /// <para>The typo rule is a parameter because the scorer re-derives stored scores from before
    /// backlog 109. Live play always passes <see cref="TypoRule.Deferred"/>, under which the
    /// rejected-key arm writes nothing and the mistype arm always breaks.</para>
    /// </summary>
    public static class TypeBeatComboFeed
    {
        /// <summary>
        /// Fletcher's rush cap breaks combo on a press that is still judged Great/Ok/Meh, so the hit
        /// result alone (which INCREMENTS osu's combo) cannot carry the break: mirror the engine's own
        /// combo by hand, after the result has been applied. Gated on the unpinned caret so the
        /// default path is untouched: there every <c>ComboAfter == 0</c> judgement either maps to a
        /// Miss (which breaks osu's combo itself) or is a WrongChar or an abandoned cell, whose break
        /// <see cref="OnMistyped"/> or <see cref="OnWordAbandoned"/> has already carried.
        ///
        /// <para>Since backlog 347 this is the PRE-347 ERA's seam only (a replay without the extended
        /// CONFIG word): under <see cref="TypingEngine.RushCapCostsAccuracy"/> an over-cap press is an
        /// ordinary Meh that credits the engine's combo, so it never arrives here with
        /// <c>ComboAfter == 0</c>, and under <see cref="TypingEngine.InputEra2"/> (every new live run)
        /// there is no cap at all.</para>
        /// </summary>
        public static void OnCharJudged(ScoreProcessor? processor, TypingEngine engine, CharJudgement judgement)
        {
            if (engine.FletcherEnabled && judgement.ComboAfter == 0 && processor != null)
                processor.Combo.Value = 0;
        }

        /// <summary>
        /// Every wrong KEYPRESS, in both input models (see <see cref="TypingEngine.Mistyped"/>), and
        /// since backlog 109 the seam carrying both of its consequences on the submitted account: the
        /// mistype count and the combo break.
        ///
        /// <para>Neither model raises a judgement RESULT for a wrong keypress any more. A rejected key
        /// never did, and a typed-through wrong char defers its cell's result until the cell is
        /// corrected or sealed on, so the break is mirrored by hand here, or the submitted
        /// <c>max_combo</c> would count straight on through the rest of the line. Setting the bindable
        /// directly, rather than through a result, leaves <c>HighestCombo</c>, the judged count and
        /// accuracy alone; <c>HighestCombo</c> needs no update because this only shrinks
        /// <c>Combo</c>.</para>
        ///
        /// <para>This is the ONLY break a wrong keypress costs (backlog 122), and since backlog 124 it
        /// is the cell's whole combo consequence in both directions: the result the cell resolves
        /// with at the seal is a hit, which <see cref="SealedCellResult"/> marks combo-neutral.</para>
        /// </summary>
        public static void OnMistyped(ScoreProcessor? processor, TypoRule rule)
        {
            if (TypeBeatResultMapping.MistypeCarriesTheComboBreak(rule) && processor != null)
                processor.Combo.Value = 0;

            (processor as TypeBeatScoreProcessor)?.RecordMistype();
        }

        /// <summary>
        /// A wrong key was REJECTED. Under <see cref="TypoRule.Deferred"/> its break has already
        /// ridden on <see cref="OnMistyped"/>, one event earlier, so this writes nothing; before
        /// backlog 109 the break for a rejected key lived here, and a typed-through wrong char broke
        /// combo through the Miss its cell took. The mash guard on the same event is health only
        /// (<see cref="TypeBeatHealthFeed"/>).
        /// </summary>
        public static void OnWrongKeyRejected(ScoreProcessor? processor, TypoRule rule)
        {
            if (!TypeBeatResultMapping.MistypeCarriesTheComboBreak(rule) && processor != null)
                processor.Combo.Value = 0;
        }

        /// <summary>
        /// A corrected typo, or a reclaimed word skip, resumes the streak its break cost (backlog 140,
        /// widened by 167, see <see cref="TypingEngine.ComboRestored"/>). The mirror image of
        /// <see cref="OnMistyped"/>: no result carries a restore. The engine raises this BEFORE the
        /// corrected retype's own judgement, so that result is weighted by the resumed streak, which
        /// is what makes fixing a typo worth SCORE and not only accuracy.
        /// </summary>
        public static void OnComboRestored(ScoreProcessor? processor, int streak) => (processor as TypeBeatScoreProcessor)?.RestoreCombo(streak);

        /// <summary>
        /// A word skip abandoned a run of cells (backlog 167). The engine has taken its one break at
        /// the skip, and the Miss results that used to carry it into osu's combo now arrive at the
        /// seal, a whole line later, so the break is written by hand here. The skip's health drain
        /// is <see cref="TypeBeatHealthFeed"/>'s.
        /// </summary>
        public static void OnWordAbandoned(ScoreProcessor? processor)
        {
            if (processor != null)
                processor.Combo.Value = 0;
        }

        /// <summary>
        /// The line sealed on cells the player never came back for (backlog 167): each one's break
        /// was taken at the skip, so the Miss it is about to take must leave combo where it found it,
        /// or it would wipe a run the player rebuilt through the rest of the line. The engine raises
        /// this BEFORE <see cref="TypingEngine.LineSealed"/>, so the marks land before the results.
        /// </summary>
        public static void OnAbandonSealed(ScoreProcessor? processor, AbandonedCells abandoned)
        {
            if (processor is not TypeBeatScoreProcessor typeBeatProcessor)
                return;

            foreach (int cellIndex in abandoned.CellIndices)
                typeBeatProcessor.MarkComboNeutral(abandoned.LineIndex, cellIndex);
        }

        /// <summary>
        /// THE SEAL'S BREAK, under <see cref="TypingEngine.BackDatedSealBreak"/> (backlog 259). A Miss
        /// carries a break to exactly one place, the combo it finds when it lands, and back-dating
        /// means the break belongs to an earlier one. So every seal miss is made combo-neutral
        /// (<see cref="SealedCellResult"/>) and the one break is written here, BEFORE the results, as
        /// an absolute write of the run the engine is left holding
        /// (<see cref="LineSealResult.SurvivingCombo"/>): the two accounts hold increments for the
        /// very same cells, so the engine's surviving count IS this one's. Written first so every
        /// result the seal applies, the unfixed typos included, is weighted by the combo the break
        /// left.
        ///
        /// <para>MIN, not an assignment: this is a BREAK, so it may only ever take combo away. A break
        /// that credited combo would be a defect in whichever account happened to be behind, not a
        /// rule anyone wants.</para>
        /// </summary>
        public static void OnLineSealed(ScoreProcessor? processor, TypingEngine engine, LineSealResult sealResult)
        {
            if (engine.BackDatedSealBreak && sealResult.ComboBroken && processor != null)
                processor.Combo.Value = Math.Min(processor.Combo.Value, sealResult.SurvivingCombo);
        }

        /// <summary>
        /// The result a cell the play never resolved takes at its line's seal, with its combo-neutral
        /// mark written immediately before the caller applies it. Two results, not one (backlog 124):
        /// a cell nobody typed is a MISS, a cell left holding a typed-through wrong character is an
        /// unfixed TYPO, which is a hit, and only the engine knows which
        /// (<see cref="TypeBeatResultMapping.UnresolvedCellResult"/>).
        ///
        /// <para>The typo's hit is combo-neutral because its break was taken at the keypress
        /// (<see cref="OnMistyped"/>); under the back-dated seal break every seal miss joins it,
        /// because their one break was written by <see cref="OnLineSealed"/>. Call it only for a cell
        /// that is actually about to be resolved.</para>
        /// </summary>
        public static HitResult SealedCellResult(ScoreProcessor? processor, TypingEngine engine, int lineIndex, int cellIndex, TypoRule rule)
        {
            var result = engine.IsJapaneseCharacter(lineIndex, cellIndex) ? HitResult.Miss
                : TypeBeatResultMapping.UnresolvedCellResult(engine.CellLeftWrong(lineIndex, cellIndex), rule);

            if (result == TypeBeatResultMapping.UNFIXED_TYPO || (engine.BackDatedSealBreak && result == TypeBeatResultMapping.SEAL_MISS))
                (processor as TypeBeatScoreProcessor)?.MarkComboNeutral(lineIndex, cellIndex);

            return result;
        }

        /// <summary>
        /// Subscribe the hooks above to <paramref name="engine"/>, each resolving its processor
        /// through <paramref name="processor"/> at the moment the event fires (so a caller whose
        /// processor is absent, a bare playfield in a test scene, writes nothing). Dispose the result
        /// to unsubscribe.
        /// </summary>
        /// <param name="engine">The engine whose events are mirrored.</param>
        /// <param name="processor">The score processor the writes go to.</param>
        /// <param name="rule">The typo rule the run is judged under. Live play is always
        /// <see cref="TypoRule.Deferred"/>.</param>
        /// <param name="applyCellResult">Applies a cell judgement's osu result, if it has one. Called
        /// BEFORE <see cref="OnCharJudged"/> on the same event.</param>
        /// <param name="applySealResults">Resolves a sealed line's unresolved cells (through
        /// <see cref="SealedCellResult"/>) and the line itself. Called AFTER
        /// <see cref="OnLineSealed"/> on the same event.</param>
        public static IDisposable Attach(TypingEngine engine, Func<ScoreProcessor?> processor, TypoRule rule,
                                         Action<CharJudgement> applyCellResult, Action<LineSealResult> applySealResults)
        {
            ArgumentNullException.ThrowIfNull(engine);
            ArgumentNullException.ThrowIfNull(processor);
            ArgumentNullException.ThrowIfNull(applyCellResult);
            ArgumentNullException.ThrowIfNull(applySealResults);

            return new Subscription(engine, processor, rule, applyCellResult, applySealResults);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly TypingEngine engine;
            private readonly Func<ScoreProcessor?> processor;
            private readonly TypoRule rule;
            private readonly Action<CharJudgement> applyCellResult;
            private readonly Action<LineSealResult> applySealResults;
            private bool disposed;

            public Subscription(TypingEngine engine, Func<ScoreProcessor?> processor, TypoRule rule,
                                Action<CharJudgement> applyCellResult, Action<LineSealResult> applySealResults)
            {
                this.engine = engine;
                this.processor = processor;
                this.rule = rule;
                this.applyCellResult = applyCellResult;
                this.applySealResults = applySealResults;

                engine.CharJudged += onCharJudged;
                engine.LineSealed += onLineSealed;
                engine.Mistyped += onMistyped;
                engine.WrongKeyRejected += onWrongKeyRejected;
                engine.ComboRestored += onComboRestored;
                engine.WordAbandoned += onWordAbandoned;
                engine.AbandonSealed += onAbandonSealed;
            }

            private void onCharJudged(CharJudgement judgement)
            {
                applyCellResult(judgement);
                OnCharJudged(processor(), engine, judgement);
            }

            private void onLineSealed(LineSealResult sealResult)
            {
                OnLineSealed(processor(), engine, sealResult);
                applySealResults(sealResult);
            }

            private void onMistyped() => OnMistyped(processor(), rule);

            private void onWrongKeyRejected(char _) => OnWrongKeyRejected(processor(), rule);

            private void onComboRestored(int streak) => OnComboRestored(processor(), streak);

            private void onWordAbandoned(AbandonedCells _) => OnWordAbandoned(processor());

            private void onAbandonSealed(AbandonedCells abandoned) => OnAbandonSealed(processor(), abandoned);

            public void Dispose()
            {
                if (disposed)
                    return;

                disposed = true;

                engine.CharJudged -= onCharJudged;
                engine.LineSealed -= onLineSealed;
                engine.Mistyped -= onMistyped;
                engine.WrongKeyRejected -= onWrongKeyRejected;
                engine.ComboRestored -= onComboRestored;
                engine.WordAbandoned -= onWordAbandoned;
                engine.AbandonSealed -= onAbandonSealed;
            }
        }
    }
}
