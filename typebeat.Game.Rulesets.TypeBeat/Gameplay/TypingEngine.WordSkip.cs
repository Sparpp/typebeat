// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// TypingEngine, WORD SKIP AND ITS UNDO: the space that abandons the rest of a word, the backspace
// that takes a skip back, and the word-gap test both are written against.

using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        /// <summary>
        /// "Space to skip current word" (see <see cref="SpaceSkipsWord"/>): abandon the word the caret
        /// is inside and leave the caret on the word gap that follows it (or at the end of the line,
        /// for a word with no gap after it). Every typeable cell of that word nobody has typed
        /// ANYTHING into enters <see cref="CellState.Abandoned"/>, and the whole abandonment costs AT
        /// MOST ONE combo break no matter how many characters were given up, which is the same rule a
        /// sealed line's misses follow. There is always at least one such cell, the one the caret is
        /// sitting on, so the break always has a cell behind it.
        ///
        /// <para>Backlog 167 moved everything except that break out of this method and on to the two
        /// places a phantom cell can end up: the backspace that reclaims it, and the seal that
        /// resolves it as a miss. What is left here is the entry into the phantom state, the break,
        /// and the SNAPSHOT of the streak that break cost, taken against the first abandoned cell so
        /// that typing it later resumes the run through the backlog 140 machinery (see
        /// <see cref="ComboRestored"/>). That replaces the outright discard the skip used to do, and
        /// it is why the skip is one of the breaks that can be redeemed rather than one that takes
        /// ownership of the streak.</para>
        ///
        /// <para>Non-typeable cells inside the run are marked <see cref="CellState.AutoSkipped"/>,
        /// which is exactly what <see cref="autoSkipForward"/> would have done to them had the caret
        /// walked over them one press at a time; they are not typed, so they cannot be missed.</para>
        ///
        /// <para>Returns whether a redeemable claim is outstanding when it hands the press back, which
        /// is what tells <see cref="ProcessKey"/> that the combo the SAME press is about to earn on the
        /// word gap belongs to the break rather than to the player (backlog 243, see
        /// <see cref="creditTheClaimsOwnPress"/>). True for the claim this skip took AND for an older
        /// one it passively left alone, because the argument is about the press and not about which
        /// break wrote the claim.</para>
        /// </summary>
        // Japanese input shares abandonment/reclaim accounting while retaining untyped word seams.
        private bool skipCurrentWord(double time, bool singleJapaneseCharacter = false, bool japaneseWord = false)
        {
            var cells = lines[activeLineIndex].Cells;

            // The era switch (backlog 167), read once: the whole of the difference between today's
            // reclaimable skip and the immediate-miss one every score stored before it was played
            // under. Nothing else in the engine needs a switch, because the phantom state is what
            // every other part of the behaviour hangs off and the other arm never creates one.
            bool reclaimable = EraRules.SkippedWordIsReclaimable(WordSkip);

            // The WHOLE word the caret is inside: the run of cells between the word gaps either side
            // of it (a word gap being a typeable SPACE cell), or the ends of the line. Deliberately
            // the whole word rather than just the tail from the caret onwards, and that choice is
            // what keeps this correct now that a cell can be resolved out of turn: without
            // AnyOrderWithinWord the two scans give up exactly the same cells (every typeable cell
            // behind the caret is already Correct or Wrong, resolving it being the only way the caret
            // got past it), and WITH it (backlog 231) the tail scan would abandon cells the player
            // has already typed while the caret still sits back at the first one they have not.
            // Scanning the word is what the feature promises either way, and it puts the weight on
            // the "already resolved" test below instead of on an argument about where the caret can be.
            int start = caretIndex;
            int end = singleJapaneseCharacter ? caretIndex + 1 : caretIndex;
            var japanesePlan = JapaneseRomajiInput && UsesJapaneseRomaji ? lines[activeLineIndex].JapaneseInput : null;
            bool canSkip(int index) => japanesePlan == null || !cells[index].IsTypeable || japanesePlan.IsRomanCell(index);

            if (japaneseWord)
            {
                int wordEnd = japanesePlan?.WordAt(caretIndex)?.EndCellExclusive ?? caretIndex + 1;
                while (end < wordEnd && !isWordGap(cells[end]) && !cells[end].IsFreestyle)
                    end++;
            }
            else
            {
                while (!singleJapaneseCharacter && start > 0 && !isWordGap(cells[start - 1]) && canSkip(start - 1))
                    start--;

                while (!singleJapaneseCharacter && end < cells.Count && !isWordGap(cells[end]) && canSkip(end))
                    end++;
            }

            var abandoned = new List<int>();

            for (int i = start; i < end; i++)
            {
                var cell = cells[i];

                if (!cell.IsTypeable)
                {
                    cell.State = CellState.AutoSkipped;
                    continue;
                }

                // Only a cell nobody has put anything into is given up. A CORRECT one has already
                // handed its drawable the one osu result it will ever have and a Great cannot be
                // revoked (ApplyEngineResult drops every later result on an already-judged cell, and
                // there is no un-apply). A WRONG one is not given up either, and since backlog 124
                // that is for its own reason rather than that one: a typed-through wrong character
                // is a cell the player FINISHED, so abandoning the word cannot turn it into a miss.
                // Its deferred result is decided at the seal like every other unfixed typo, which
                // also leaves the promise intact that backspacing back into the word can still fix
                // it. Backlog 109 had it given up here, because at the time the only fate available
                // to an unfixed typo was a Miss.
                if (cell.State != CellState.Untyped)
                    continue;

                // The phantom state (backlog 167) or, under the pre-167 era rule, the Miss the cell
                // used to take here. Nothing else in this method differs between the two arms: the
                // count and the announced judgement type follow from this, and every downstream
                // consequence of the phantom state is unreachable when no cell is ever in it.
                cell.State = reclaimable ? CellState.Abandoned : CellState.Missed;

                // The miss COUNT is the cell's resolution, so under the live rule it waits for the
                // seal exactly as the osu result does. Counting it now would say the character is
                // lost while the player can still walk back into it and type it.
                if (!reclaimable)
                    counts[JudgementType.Miss]++;

                abandoned.Add(i);
            }

            caretIndex = end;

            if (abandoned.Count == 0)
                return false;

            int brokenStreak = combo;

            var brokenPositions = breakRun();

            // The break is IMMEDIATE under both rules, and under the live one it is also the only
            // thing the skip spends. Snapshotted against the FIRST abandoned cell, so re-typing that
            // cell resumes the run: the skip is a break the player can come back for, which is
            // exactly what a typo's break is (see ComboRestored). A skip discards an older cell's
            // claim the way any other intervening break would, but only if it broke a streak of its
            // own (backlog 176): a skip taken over a typo that has already zeroed the run leaves
            // that typo's claim redeemable, because the skip itself cost nothing.
            //
            // Under the pre-167 rule nothing is left to come back to, so the skip is a plain break
            // and ends any outstanding claim outright, exactly as it did then.
            if (reclaimable)
                snapshotRedeemableBreak(abandoned[0], brokenStreak, brokenPositions);
            else
                discardRestorableStreak();

            raise(ComboBroken);

            // The break rides here rather than on a result, and this is the seam it rides on. The
            // ORIGINAL argument for announcing the abandoned cells immediately was that leaving them
            // to the seal would let osu's combo count on past a break the engine had already taken.
            // That argument survives backlog 167 intact, because the BREAK is still taken here; only
            // the RESULTS moved. What moved with them is the obligation: with no Miss result left to
            // carry the break, WordAbandoned carries it by hand (TypeBeatComboFeed.OnWordAbandoned),
            // and the seal marks the deferred misses combo-neutral so they cannot take it a second
            // time (see AbandonSealed).
            if (reclaimable)
                raise(WordAbandoned, new AbandonedCells(activeLineIndex, abandoned));

            // Announce the cells AFTER the break so every judgement carries the post-break combo, and
            // one per cell so the stage repaints it. Under the live rule the judgement resolves
            // nothing (JudgementType.Abandoned maps to no osu result, exactly as a typed-through
            // wrong char does); under the pre-167 rule it IS the cell's Miss, taken now.
            foreach (int i in abandoned)
            {
                var type = reclaimable ? JudgementType.Abandoned : JudgementType.Miss;
                raise(CharJudged, new CharJudgement(activeLineIndex, i, type, time - cells[i].TargetTime, combo));
            }

            return ledger.HasClaim;
        }

        /// <summary>
        /// Whether one backspace can undo the word skip adjacent to the caret. Always false outside
        /// <see cref="InputEra2"/>, whose backspace is the only one that undoes a skip.
        /// </summary>
        public bool CanUndoWordSkip => InputEra2 && adjacentSkippedWord() != null;

        private (int firstAbandoned, int wordEnd, int gapIndex)? adjacentSkippedWord()
        {
            if (isFinished || activeLineIndex == -1)
                return null;

            var line = lines[activeLineIndex];
            var cells = line.Cells;
            if (JapaneseInputEra3 && JapaneseRomajiInput && UsesJapaneseRomaji && caretIndex > 0
                && line.JapaneseInput?.WordAt(caretIndex - 1) is { } japaneseWord
                && japaneseWord.EndCellExclusive == caretIndex)
            {
                for (int i = japaneseWord.StartCell; i < caretIndex; i++)
                {
                    if (cells[i].State == CellState.Abandoned)
                        return (i, caretIndex, -1);
                }
                return null;
            }

            int wordEnd;
            int gapIndex = -1;

            if (caretIndex < cells.Count && isWordGap(cells[caretIndex]))
                wordEnd = caretIndex;
            else if (caretIndex > 0 && isWordGap(cells[caretIndex - 1]))
                wordEnd = gapIndex = caretIndex - 1;
            else if (caretIndex == cells.Count)
                wordEnd = caretIndex;
            else
                return null;

            int wordStart = wordEnd;

            while (wordStart > 0 && !isWordGap(cells[wordStart - 1]))
                wordStart--;

            for (int i = wordStart; i < wordEnd; i++)
            {
                if (cells[i].State == CellState.Abandoned)
                    return (i, wordEnd, gapIndex);
            }

            return null;
        }

        private bool tryUndoWordSkip(IReadOnlyList<TypingCell> cells)
        {
            var skip = adjacentSkippedWord();

            if (skip == null)
                return false;

            var (firstAbandoned, wordEnd, gapIndex) = skip.Value;
            var reclaimed = new List<int>();

            for (int i = firstAbandoned; i < wordEnd; i++)
            {
                if (cells[i].State == CellState.Abandoned)
                {
                    cells[i].State = CellState.Untyped;
                    reclaimed.Add(i);
                }
                else if (cells[i].State == CellState.AutoSkipped)
                    cells[i].State = CellState.Untyped;
            }

            if (gapIndex >= 0)
            {
                var gap = cells[gapIndex];

                // A skip can also step over an already-spoiled gap. In that case this space did
                // not type the gap, so keep the earlier typo for its own backspace to erase.
                if (gap.State == CellState.Correct)
                {
                    gap.State = CellState.Untyped;
                    gap.TypedChar = null;
                    gap.JudgedDelta = null;
                }
            }

            caretIndex = firstAbandoned;
            raise(AbandonReclaimed, new AbandonedCells(activeLineIndex, reclaimed));
            return true;
        }

        /// <summary>
        /// A WORD GAP: the typeable SPACE cell that separates two words. The one boundary both
        /// word-level queries below are written against, and the same test
        /// <see cref="skipCurrentWord"/> scans a word with, so "word" means one thing in this file.
        /// A non-typeable cell (punctuation the default stream kept, and every mark under the
        /// Literate mod) is NOT a boundary: it rides inside the word it is attached to.
        /// </summary>
        private static bool isWordGap(TypingCell cell) => cell.IsTypeable && cell.Expected == ' ';
    }
}
