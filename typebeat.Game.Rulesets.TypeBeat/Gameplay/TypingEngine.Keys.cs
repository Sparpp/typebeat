// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// TypingEngine, THE KEYPRESS: ProcessKey and the ordered steps a press goes through, plus the
// character match every step shares.

using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

using System;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        /// <summary>
        /// Whether a press of <paramref name="c"/> satisfies a cell expecting
        /// <paramref name="expected"/>: the Literate exact-case rule or the default fold, and under
        /// <see cref="Polyglot"/> the same comparison in NFC. Every play without the mod runs the
        /// original expression, character for character.
        /// </summary>
        private bool charMatches(char c, char expected)
        {
            if (Polyglot)
                return PolyglotText.Matches(c, expected, CaseSensitive);

            return CaseSensitive ? c == expected : Typeability.Fold(c) == Typeability.Fold(expected);
        }

        /// <summary>
        /// Process a char the input path produced (TextInputFold since backlog 383) at gameplay time <paramref name="time"/>.
        /// Returns false when inert (no active line / line complete / finished).
        /// A space pressed inside a word abandons it under <see cref="SpaceSkipsWord"/> (off by default).
        /// A space typed ON a space cell is UNTIMED (backlog 148): it is judged as though it landed on
        /// target, so it always takes the top tier and never breaks combo, however loosely it was hit.
        /// Under <see cref="AnyOrderWithinWord"/> the press is offered to the whole of the caret's
        /// word rather than to the caret cell alone, and only a key that matches nothing still
        /// untyped there is wrong at all.
        /// A wrong char is TYPED THROUGH by default (<see cref="AllowWrongInput"/>), or REJECTED
        /// under Gatekeeper. Either way it breaks combo, counts as a mistype, stays in the accuracy
        /// denominator forever, and resolves NO cell against the score processor; only the rejection
        /// path grows <see cref="ConsecutiveWrongKeys"/>. On a SPACE cell the type-through needs
        /// <see cref="WrongInputOnWordGaps"/> as well (live play sets it; a stored replay says so in
        /// its own header), and a wrong SPACE key is rejected on every cell except under
        /// <see cref="StrictSpaces"/> with <see cref="SpaceSkipsWord"/> off, where it is a typo like
        /// any other. <see cref="StrictSpaces"/> also PARKS the caret on a gap a typo spoiled, so the
        /// space that gap was owed is still owed: pressing it steps over the typo, and backspace
        /// clears the typo where it sits.
        /// </summary>
        public bool ProcessKey(char c, double time)
        {
            string pending = JapanesePending;
            bool consumed = processKey(c, time);
            // Cancelling a partial reading is itself an input mutation, even when the underlying
            // key (such as a refused space) judges nothing. Record it so replay state stays exact.
            return consumed || pending != JapanesePending;
        }

        private bool processKey(char c, double time)
        {
            var press = new KeyPress(c, time);

            openTheFirstLineOnAPress(time);

            if (refusesThePress(time))
                return false;

            press.Line = lines[activeLineIndex];

            // Hop auto-skip cells before matching (normally already done on advance/activation).
            autoSkipForward();

            if (tryPressOnAFinishedLine(ref press, out bool consumed))
                return consumed;

            // A space where spaces are completed (Shorthand, or just past an auto-fill word) is habit, so inert.
            // After the finished-line branch, so the spacebar still works as the manual newline there.
            if (Shorthand && press.Char == ' ')
                return false;

            if (press.Char == ' ' && caretIndex > 0 && press.Line.Cells[caretIndex - 1] is { IsAutoCompleted: true, Expected: ' ' })
                return false;

            // The press is going to do something, so the player is typing on this line; if the song
            // has not reached it yet, that is the instant the WPM clock starts counting (backlog 222,
            // see clockRunsFrom). Placed here, above every branch below, so it is one site and no
            // path can quietly skip it, and attributed to the line the press LANDS on: a press that
            // finishes this line and rolls the caret onward has typed here, not there.
            armWpmClockAheadOfTheCue(press.Time);

            press.Cell = press.Line.Cells[caretIndex];

            if (processJapaneseKey(press.Char, press.Time, press.Line) is bool japaneseConsumed)
                return japaneseConsumed;

            applyMashing(ref press);

            if (trySkipTheWord(ref press, out consumed))
                return consumed;

            if (tryStepOverASpoiledGap(ref press, out consumed))
                return consumed;

            aimWithinTheWord(ref press);

            timeThePress(ref press);

            return pressMatches(press) ? judgeACorrectKey(ref press) : judgeAWrongKey(ref press);
        }

        /// <summary>
        /// THE MAP'S FIRST LINE OPENS ON A PRESS (see <see cref="FIRST_LINE_LEAD_MS"/>), the first step of
        /// <see cref="processKey"/>.
        /// </summary>
        private void openTheFirstLineOnAPress(double time)
        {
            // THE MAP'S FIRST LINE OPENS A SECOND EARLY (see FIRST_LINE_LEAD_MS). There is no previous
            // line to rush from, so the PRESS is what opens it: the same hand-over the time-driven
            // activation arm performs, made on demand rather than idling a line the song has not reached.
            // The clock alone still leaves the line alone, so nothing that only WATCHES the engine sees a
            // line become active a second before its word.
            if (activeLineIndex == -1 && FirstLineTypingOpensAt(time))
            {
                activeLineIndex = 0;
                caretIndex = 0;
                autoSkipForward();
                raise(LineActivated, 0);
            }
        }

        /// <summary>
        /// Whether the press is refused before anything is read: nothing is active, or the caret is on
        /// a line it may not type on yet. A refused press is inert.
        /// </summary>
        private bool refusesThePress(double time)
        {
            if (isFinished || activeLineIndex == -1)
                return true;

            // WAITING FOR THE WINDOW (see AwaitingEntry): the player was handed this line early, so
            // nothing here is judged - not the character, not a typo - until the window opens.
            return awaitingEntry(time);
        }

        /// <summary>
        /// A press on a FINISHED line (the caret past its last cell): the manual newline by space or by a
        /// typed letter, or nothing. Handled unless a typed-letter newline landed the caret on a slot the
        /// letter can still be judged on, which the steps after this one then judge.
        /// </summary>
        private bool tryPressOnAFinishedLine(ref KeyPress press, out bool consumed)
        {
            consumed = false;

            if (caretIndex >= press.Line.Cells.Count)
            {
                // MANUAL NEWLINES: on a finished line the SPACEBAR is the newline (see
                // ManualNewlines), so it hands the caret on instead of being inert, and a space
                // outside the next line's entry window is refused exactly as one pressed mid-word
                // would be.
                if (press.Char == ' ')
                {
                    consumed = rollForwardManually(press.Time);
                    return true;
                }

                // OR BY TYPING, under its own era bit: ANY letter hands the caret on and then lands
                // on the next line's first slot, right or wrong. It moves on the SAME terms the space
                // does - NO WINDOW GATE on the press - because the window's business is what may be
                // TYPED, not where the caret may stand. Gating the press here meant a player who
                // finished a line more than FLETCHER_DRAG_GRACE_MS early (the case this exists for)
                // got nothing at all, while the space still worked. So the move happens, and the
                // window then refuses the CHARACTER exactly as it refuses every other press on a line
                // that has not opened yet - the player types the letter again when the line does.
                if (NewlineOnTypedLetter && ManualNewlines && FletcherEnabled
                    && activeLineIndex + 1 < lines.Count
                    && typedNewlineMatchesAfterFreestyle(press.Line, press.Char)
                    && rollForwardManually(press.Time))
                {
                    press.Line = lines[activeLineIndex];

                    if (awaitingEntry(press.Time))
                    {
                        consumed = true;
                        return true;
                    }

                    // A landed line with nothing typeable on it (every cell auto-skipped) leaves the
                    // caret past its end with no slot for the letter, so the move is kept and the
                    // letter goes nowhere: the answer the awaiting branch above already gives, and the
                    // one typebeat-core.js gives. Nothing is judged and the WPM clock is not armed,
                    // because nothing was typed.
                    if (caretIndex >= press.Line.Cells.Count)
                    {
                        consumed = true;
                        return true;
                    }
                }
                else
                {
                    // Every other key stays inert here, which is the dead zone the automatic roll
                    // parks in today.
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// MASHING (Relax): any key is the right key.
        /// </summary>
        private void applyMashing(ref KeyPress press)
        {
            // Mashing mod: any key is the right key; judge it as the caret cell's expected char.
            // A FREESTYLE cell is exempt: it already accepts any key, and rewriting c here would
            // stamp the authoring marker over the char the player actually pressed (the one thing
            // a freestyle cell must remember). No double effect, mashing simply has nothing to add.
            // Space is the single exception to that exemption: a freestyle cell REJECTS space (see
            // the match below), so mashing's "any key is the right key" promise needs a substitute
            // to hand it, and the char an automated player presses into a freestyle slot is the
            // canonical one. Nothing else about the exemption changes, the pressed char still
            // survives on every other key.
            if (MashingEnabled)
            {
                if (!press.Cell.IsFreestyle)
                    press.Char = press.Cell.Expected;
                else if (press.Char == ' ')
                    press.Char = Typeability.FREESTYLE_AUTO_CHAR;
            }
        }

        /// <summary>
        /// SPACE-SKIP (see <see cref="SpaceSkipsWord"/>). Handled when the skip ended the press, either at
        /// the end of the line or on a Japanese cell; otherwise the press goes on to be judged on the gap.
        /// </summary>
        private bool trySkipTheWord(ref KeyPress press, out bool consumed)
        {
            consumed = false;

            // SPACE-SKIP (see SpaceSkipsWord), evaluated BEFORE the match: a space pressed while the
            // caret sits on a lyric character abandons the rest of that word. The caret cell is
            // typeable here (autoSkipForward ran above), so "Expected is not a space" is exactly "the
            // caret is inside a word"; a space pressed ON the word gap keeps its ordinary meaning and
            // never reaches this branch. Placed after the Mashing rewrite on purpose: mashing has
            // already turned the press into the expected char, so this is unreachable under it.
            //
            // Under InputEra2 the skip also needs wrong input allowed: Gatekeeper refuses a
            // mid-word space like any other wrong key. Every run stored before that era skipped
            // under Gatekeeper too, so the AllowWrongInput term is ignored when the era is clear.
            if (SpaceSkipsWord && (AllowWrongInput || !InputEra2) && press.Char == ' ' && press.Cell.Expected != ' ')
            {
                press.CaretBeforeSkip = caretIndex;
                press.SkipLeftAClaimOutstanding = skipCurrentWord(press.Time);

                if (caretIndex >= press.Line.Cells.Count)
                {
                    // The abandoned word ran to the end of the line, so there is no word gap for the
                    // space to land on. The line is complete, exactly as it would be had the player
                    // typed that last word out, and the same end-of-line handling applies.
                    rollForwardIfFinishedEarly(press.Time);
                    consumed = true;
                    return true;
                }

                // The caret now sits on the word gap, and the press is judged there: a space typed
                // from further back in the word is still a typed space, so it takes the ordinary
                // path below (same windows, points, combo, accuracy) and leaves the cell exactly as
                // a normally typed space would.
                press.Cell = press.Line.Cells[caretIndex];
                // A Roman run in a mixed Japanese word stops at the next script. The skip's space
                // is not a typo on that Japanese character.
                if (JapaneseRomajiInput && UsesJapaneseRomaji && press.Cell.Expected != ' ' && !press.Cell.IsFreestyle
                    && press.Line.JapaneseInput?.IsRomanCell(caretIndex) == false)
                {
                    consumed = true;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// STEP OVER A SPOILED GAP (backlog 184, see <see cref="StrictSpaces"/>).
        /// </summary>
        private bool tryStepOverASpoiledGap(ref KeyPress press, out bool consumed)
        {
            consumed = false;

            // STEP OVER A SPOILED GAP (backlog 184, see StrictSpaces): the caret is PARKED on a word
            // gap that a wrong letter took, and the space that gap was owed has arrived. It walks the
            // caret past the gap and leaves the typo exactly as it is. The cell is NOT rewritten to
            // Correct, because the character sitting in it is not the one that was owed: it stays an
            // unfixed, backspace-redeemable claim, and the seal resolves it as UNFIXED_TYPO like every
            // other one. It judges nothing: no tier, no points, no combo gained and none broken (the
            // typo already took the break, and this press cannot be asked to pay for it twice).
            //
            // Counted as a CORRECT keypress, which is the same argument once more: the space IS the
            // right key for the cell it lands on, the gap it was owed, and the typo has already paid
            // an error and a break of its own. Charging accuracy for it as well would punish the same
            // word twice, and the press a player must make to recover from a mistake is not itself a
            // mistake. correctKeypresses feeds LiveAccuracy alone, so this credits accuracy and moves
            // nothing else: the cell still resolves as an unfixed typo and still costs COMPLETION,
            // which is where an unfixed typo is supposed to be paid for.
            //
            // Gated on the CELL STATE rather than on the era flags, which is exact rather than
            // approximate: the caret can only come to rest on a Wrong cell through the park above, so
            // a replay re-deriving with flags bit 4 CLEAR never reaches this branch at all. Everywhere
            // else, resolving a cell is the only way the caret got past it.
            if (press.Char == ' ' && press.Cell.Expected == ' ' && press.Cell.State == CellState.Wrong)
            {
                totalKeypresses++;
                correctKeypresses++;

                caretIndex++;
                advanceCaretToFrontier();

                rollForwardIfFinishedEarly(press.Time);
                consumed = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// ANY ORDER WITHIN A WORD (backlog 231): which cell the press lands on.
        /// </summary>
        private void aimWithinTheWord(ref KeyPress press)
        {
            // ANY ORDER WITHIN A WORD (backlog 231, see AnyOrderWithinWord): the press is offered to
            // every cell of the caret's word nobody has typed anything into yet, ascending, and the
            // first one it matches is the cell it lands on. This is the WHOLE of the mod, because
            // everything downstream of here is already index-parameterised: judgedDeltaFor grades a
            // cell against ITS OWN point target or syllable span, the combo-restore claim is keyed by
            // cell, the mutation writes through the cell object, and CharJudged carries the index the
            // display repaints.
            //
            // A miss leaves targetIndex at the caret, so a key matching nothing still untyped in the
            // word falls through UNCHANGED and is priced exactly as it is without the mod: typed
            // through, parked, or rejected by the unchanged block below.
            //
            // Skipped outright when the caret is ON a word gap, which is not "inside a word" at all.
            // Two ways the caret gets there and neither wants this: the gap a finished word owes its
            // space (every cell behind it is resolved, so the scan could only look backwards into a
            // word with nothing left to find), and the gap a typo has PARKED the caret on under
            // StrictSpaces, where the press must keep its ordinary meaning or the park is not a park.
            press.TargetIndex = caretIndex;

            if (AnyOrderWithinWord && !isWordGap(press.Cell) && matchWithinWord(press.Line.Cells, press.Char) is int outOfOrder)
            {
                press.TargetIndex = outOfOrder;
                press.Cell = press.Line.Cells[press.TargetIndex];
            }
        }

        /// <summary>
        /// The delta the press is judged on, untimed spaces zeroed (backlog 148).
        /// </summary>
        private void timeThePress(ref KeyPress press)
        {
            press.Delta = IsJapaneseCharacter(activeLineIndex, press.TargetIndex)
                ? press.Time - press.Cell.TargetTime : judgedDeltaFor(press.Line, press.TargetIndex, press.Time);

            // SPACES ARE UNTIMED (backlog 148), decided here rather than after the match so that
            // EVERY reading of this press agrees on what its cell was worth: the correct press
            // below, and the wrong one typed through it since backlog 181, whose CharJudgement
            // carries exactly the delta a correct press on the same cell would have carried. The
            // rule and its argument are on the untimed-space block further down; only the position
            // moved, and the move reaches nothing new: the predicate is false for every LYRIC cell,
            // so the only press it can newly touch is the gap typo it was moved for.
            press.UntimedSpace = press.Cell.Expected == ' ' && EraRules.SpacesAreUntimed(spaceTiming);

            if (press.UntimedSpace)
                press.Delta = 0;
        }

        /// <summary>
        /// Whether the pressed key fits the cell it landed on.
        /// </summary>
        private bool pressMatches(in KeyPress press)
        {
            // FREESTYLE cell: every char EXCEPT SPACE matches, in any case, under every mod (so the
            // Literate mod's exact-case rule and the allow-wrong-input path are both bypassed for
            // it). The press is then judged exactly like a correct char: same windows, points,
            // combo, accuracy and completion, with the pressed char kept in TypedChar.
            // SPACE is carved out (backlog 50): it is the word-advance key, not a glyph a player
            // means to leave sitting in a lyric, so it falls through to the ordinary non-match path
            // below and is judged exactly as a wrong key on any other cell would be. The strict
            // rejection is the only outcome available to it, because neither allow-wrong-input path
            // will type a space through (c != ' ' guards both arms). With SpaceSkipsWord on the space
            // was consumed by the word skip above (freestyle slot included), except under Gatekeeper
            // in InputEra2, where it reaches the strict rejection below.
            // Literate mod folds nothing: the typed char must match the target's exact case.
            // Default gameplay is case-insensitive (both sides lower-cased through Fold).
            return (press.Cell.IsFreestyle && press.Char != ' ')
                           || charMatches(press.Char, press.Cell.Expected);
        }

        /// <summary>
        /// A press that fits no cell: typed through, or rejected under Gatekeeper. Always consumed.
        /// </summary>
        private bool judgeAWrongKey(ref KeyPress press)
        {
            // Reached only with targetIndex == caretIndex, under every mod: a scan hit above
            // satisfies exactly the test just made, so a press the word took out of order can
            // never arrive here and this whole block still speaks about the caret's own cell.
            //
            // DEFAULT: a wrong LETTER is typed through, marked red, backspaceable, instead of
            // rejected. What the WORD GAP does with a wrong letter is the first era switch (see
            // WrongInputOnWordGaps): it takes the typo exactly as a lyric cell does, which is the
            // live rule, or it drops to the strict branch below, which is what every stored replay
            // was played under. This path never feeds the mash-fail streak (consecutiveWrongKeys
            // is left at 0, which is why that guard is Gatekeeper-only).
            //
            // The space KEY is the second era switch (see StrictSpaces). Classically it is strict
            // on every cell: there is no cell a wrong space is typed into, because it is the
            // word-advance key and not a glyph a player means to leave sitting in a lyric. Under
            // StrictSpaces with SpaceSkipsWord OFF it is admitted on a lyric character, and the
            // reason is that with no word to skip the press means nothing else: it is a wrong
            // character, so it is treated as one, no differently from a wrong letter (the cell
            // still renders its own expected character in the error red, since CellGlyph
            // substitutes the typed char for GAPS only, which is what makes an invisible red space
            // a non-problem). With SpaceSkipsWord ON the skip gate above consumed it, except
            // under Gatekeeper in InputEra2, where the press falls through to rejection.
            //
            // A FREESTYLE slot keeps refusing the space key under every arm. Its promise is "any
            // character except the word-advance key" (backlog 50) and it has no expected glyph to
            // redden, so a space typed into one would blank the cell rather than mark it.
            bool spaceMayLand = StrictSpaces && !SpaceSkipsWord && !press.Cell.IsFreestyle;

            if (AllowWrongInput && (press.Char != ' ' || spaceMayLand) && (WrongInputOnWordGaps || press.Cell.Expected != ' '))
                return typeAWrongKeyThrough(ref press);

            return rejectAWrongKey(in press);
        }

        /// <summary>
        /// A wrong key TYPED THROUGH into the caret cell (the default model, backlog 107).
        /// </summary>
        private bool typeAWrongKeyThrough(ref KeyPress press)
        {
            totalKeypresses++;
            errorCount++;

            // The streak this keypress is about to break, snapshotted against the cell it
            // spoils: correcting that cell resumes it (backlog 140, see ComboRestored). A
            // wrong key on a SECOND cell discards the first cell's claim the way any other
            // intervening break would, but only if it broke a streak of its own (backlog
            // 176, see snapshotRedeemableBreak).
            int brokenStreak = combo;

            var brokenPositions = breakRun();

            counts[JudgementType.WrongChar]++;

            press.Cell.State = CellState.Wrong;
            press.Cell.TypedChar = press.Char;

            // The cell is now one whose eventual judgement, if the player goes back for it,
            // will be a CORRECTION and not a clean first attempt, and backlog 210 prices
            // those differently (see EraRules.AwardedTier). Recorded on the
            // cell rather than counted, so a wrong-fix-wrong-fix cycle caps exactly once.
            //
            // Gated on the cell being UNJUDGED, which is what makes the flag mean what it
            // says. A cell that was already judged CLEAN and then spoiled by a wrong key on
            // the way back through keeps that clean judgement (a cell takes only its first
            // result, and the retype that follows is inert), so flagging it would demote a
            // judgement the player earned honestly before they ever fumbled it.
            //
            // Era-independent on purpose: this records what HAPPENED, and CorrectionCredit
            // decides what it is worth, so a stored replay re-derives the same flag under
            // either arm.
            if (press.Cell.FirstCorrectDelta is null)
                press.Cell.HeldWrongBeforeJudged = true;

            int wrongCellIndex = caretIndex;

            snapshotRedeemableBreak(wrongCellIndex, brokenStreak, brokenPositions);

            // PARK on a spoiled word gap under StrictSpaces (backlog 184), instead of moving
            // on: the space is still owed, so the player pays it (which steps over the typo,
            // see the branch above) or backspaces it away, rather than being carried into the
            // next word behind a gap the skip gate can no longer read as one. A further wrong
            // letter lands on this same cell and overwrites this same character, so one park
            // is one unfixed typo however many letters arrive; the snapshot above is idempotent
            // for the same reason (a break with no streak behind it leaves the standing claim
            // alone, see snapshotRedeemableBreak).
            //
            // Scoped to SpaceSkipsWord because that is where the damage was: with the setting
            // off, an advancing gap typo costs the player one cell, and with it on the next
            // space fed the skip gate a spoiled gap and gave up a whole word. Every typo on a
            // LYRIC cell advances exactly as it always has, under both arms.
            //
            // The advance rolls the frontier the same way a correct press does (see
            // advanceCaretToFrontier), which matters under AnyOrderWithinWord and nowhere
            // else: the cells this typo steps over can already have been typed out of order,
            // and leaving the caret parked on one of them would break the very invariant the
            // mod is built to keep.
            if (!(StrictSpaces && SpaceSkipsWord && press.Cell.Expected == ' '))
            {
                caretIndex++;
                advanceCaretToFrontier();
            }

            // The keypress was wrong, so it is a mistype exactly as it would be in strict
            // mode, and since backlog 109 it ACCOUNTS exactly as strict mode does too: the
            // mistype carries the combo break by hand (TypeBeatComboFeed.OnMistyped) and the
            // cell hands the score processor nothing at all.
            raise(Mistyped);
            raise(ComboBroken);
            // The CELL's judgement still travels here, for the stage's red/shake feedback,
            // but DrawableTypeBeatHitObject.ApplyCharJudgement deliberately applies no osu
            // result for a WrongChar: the cell's result is DEFERRED. Backspace and retype it
            // correctly and it earns its real Great/Ok/Meh, plus the streak this press
            // just broke (backlog 140, see ComboRestored); leave it and the seal
            // resolves it as an unfixed typo, which is a hit and not a miss (backlog 124).
            raise(CharJudged, new CharJudgement(activeLineIndex, wrongCellIndex, JudgementType.WrongChar, press.Delta, combo));
            rollForwardIfFinishedEarly(press.Time);
            return true;
        }

        /// <summary>
        /// A wrong key REJECTED (Gatekeeper).
        /// </summary>
        private bool rejectAWrongKey(in KeyPress press)
        {
            // Gatekeeper (strict): wrong key REJECTED, no cell mutation, no caret advance, no
            // CharJudged. It still costs the accuracy denominator, an error, a combo break, and
            // the consecutive-wrong-key streak (the game fails the play when it hits 13), and
            // it is counted as a MISTYPE, which is the only route by which a rejected key
            // reaches the score processor and the persisted statistics (see Mistyped).
            totalKeypresses++;
            errorCount++;
            consecutiveWrongKeys++;
            breakRun();

            // Nothing was written into a cell, so there is nothing to go back and correct: this
            // break is final, and it ends any older cell's claim on the streak.
            discardRestorableStreak();
            counts[JudgementType.WrongChar]++;
            raise(Mistyped);
            raise(ComboBroken);
            raise(WrongKeyRejected, press.Char);
            return true;
        }
        private bool judgeACorrectKey(ref KeyPress press)
        {
            consecutiveWrongKeys = 0;
            closeHoldHandedOff(activeLineIndex, press.TargetIndex, press.Time);

            resumeStreakIfThisRedeemsTheBreak(press.TargetIndex);

            // A retype keeps the existing award unless its timing earns a higher tier.
            // It never earns another combo increment or an extra scoring cell.
            bool inertRetype = press.Cell.FirstCorrectDelta is not null;

            if (usesJapaneseInputScoring(activeLineIndex, press.TargetIndex))
                return judgeJapaneseInputs(press.Line, press.Cell, press.TargetIndex, press.Char, press.Time, inertRetype);

            JudgementType type;
            bool upgraded = false;

            if (inertRetype)
            {
                type = press.Cell.JudgedTier ?? awardedRetypeTier(press.Cell, press.Cell.FirstCorrectDelta!.Value, press.Cell.JudgedPastRushCap);
                bool pastCap = FletcherEnabled && !RushCapExempt && !InputEra2 && rushesPastCap(press.Cell, press.Time, caretIndex);
                var candidate = awardedRetypeTier(press.Cell, press.Delta, pastCap && RushCapCostsAccuracy);
                if (BestRetypeJudgement && betterTier(candidate, type))
                {
                    improveRetypedAward(press.Cell, candidate, press.Delta, press.Time);
                    type = candidate;
                    press.Cell.JudgedPastRushCap = pastCap && RushCapCostsAccuracy;
                    upgraded = true;
                }
                press.Delta = press.Cell.FirstCorrectDelta!.Value;

                press.Cell.State = CellState.Correct;
                press.Cell.TypedChar = press.Char;
                press.Cell.JudgedDelta = press.Delta;
            }
            else
            {
                // ALL scoring keypresses (correct + wrong) count in the accuracy denominator, forever.
                totalKeypresses++;

                // Correct char: always accepted; the clock decides the judgement.
                // Premature/Lagging still count as CORRECT keypresses (right char, wrong time).
                correctKeypresses++;

                // The clock classifies the press, then backlog 210's CORRECTION CAP decides what it
                // is awarded: a cell that held a wrong character before it was ever judged resolves
                // at min(that tier, Ok), so a corrected cell can never be worth what a clean one is
                // (see EraRules.AwardedTier for why the cap sits on the TIER rather
                // than on the osu result). Applied here, above everything the tier decides, so the
                // engine's point ladder, the tier counts, the announced CharJudged and the cell's
                // osu result all follow the one decision and cannot say different things. The delta
                // itself is untouched, so the sync timeline and the sync readouts see the press the
                // player actually made.
                type = EraRules.AwardedTier(WindowsFor(press.Cell).Classify(press.Delta), press.Cell.HeldWrongBeforeJudged, CorrectionCredit);

                // Fletcher RUSH CAP, evaluated before the caret moves: does this press put the caret
                // more than RushCap countable chars past the playhead?
                //
                // "Before the caret moves" is true of every press but one, and that one is the whole
                // of backlog 260 (see LosslessSkipReclaim): a space that skipped a word is judged on
                // the gap AFTER the skip has already walked the caret over the abandoned tail, so the
                // measurement below has to be taken at the caret the press started from or the player
                // is charged for characters they gave up rather than typed.
                int caretForCap = LosslessSkipReclaim && press.CaretBeforeSkip >= 0 ? press.CaretBeforeSkip : caretIndex;

                // ...and RushCapExempt (backlog 261) takes the cap out of the question entirely, for
                // the one mod whose playhead IS the tape the player is dragging: see the flag.
                // InputEra2 takes it out for every run played in that era, which is every new live
                // run; a stored run without the era keeps the cap it was played with.
                bool rushedPastCap = FletcherEnabled && !RushCapExempt && !InputEra2 && rushesPastCap(press.Cell, press.Time, caretForCap);

                // WHAT THE CAP COSTS is the era axis (backlog 347, RushCapCostsAccuracy). Under the
                // live rule it costs the JUDGEMENT and nothing else: the press is awarded the lowest
                // hit tier (RushCapTier, a min over the ladder, so an off-time press is never lifted
                // by it) and then flows through every arm below exactly as a Meh struck by the clock
                // would, combo credit included. Applied here, above the point ladder, for the reason
                // backlog 210's cap is: the points, the tier counts, the announced CharJudged and the
                // cell's osu result all follow one decision. The delta is untouched. Under the
                // pre-347 rule the tier stands and the combo arm below takes the break instead.
                bool rushCostsTheJudgement = rushedPastCap && RushCapCostsAccuracy;

                if (rushCostsTheJudgement)
                {
                    type = EraRules.RushCapTier(type);
                    press.Cell.JudgedPastRushCap = true;
                }

                // Premature / Lagging (an OFF-TIME press: the right character, outside the outermost
                // Meh window) earns nothing above, and since backlog 199 that is the whole of what it
                // costs the score ladder. Whether it also costs the RUN is the OffTime era axis:
                //
                //   MehHit (live)      the press is a hit. It extends the combo like any other
                //                      accepted character, raises no ComboBroken, and leaves an
                //                      outstanding restorable claim alone, because only a BREAK
                //                      discards one. Its cell resolves as an osu Meh
                //                      (TypeBeatResultMapping.CellResult), so ACCURACY is the
                //                      punishment and osu's combo follows the engine's without any
                //                      hand-mirroring at the playfield seam.
                //   BreaksCombo        the pre-199 rule every stored row was played under: the run is
                //                      zeroed, the claim discarded, ComboBroken raised, and the cell
                //                      takes an osu Miss that carries the break.
                //
                // A space can never reach either arm under the live space rule: an untimed space is
                // judged on a zeroed delta and always takes the top tier (see SpaceTiming).
                bool offTimeBreak = (type is JudgementType.Premature or JudgementType.Lagging) && !EraRules.OffTimePressIsAHit(OffTime);

                if (offTimeBreak)
                {
                    breakRun();
                    discardRestorableStreak();
                    raise(ComboBroken);
                }
                else if (rushedPastCap && !rushCostsTheJudgement)
                {
                    // THE PRE-347 ERA (RushCapCostsAccuracy clear), kept so every stored run
                    // re-derives as it was played. The live rule never reaches this arm: its penalty
                    // was taken above as the Meh, and the press credits combo in the arm below.
                    //
                    // A combo penalty, not a block: the char lands and scores exactly as it would
                    // without the mod, but no combo may accumulate while the caret is out past the
                    // cap. ComboBroken therefore fires once, on the press that crosses the line,
                    // and re-arms the moment a press lands back inside it (combo starts building
                    // again, so the next excursion breaks it again).
                    //
                    // It reaches an off-time press too, under MehHit only, and that is the coherent
                    // reading of both rules rather than an accident: the cap measures where the CARET
                    // is, not how well the press was timed, so a press it would refuse combo for
                    // cannot earn combo merely by also being mistimed. Under BreaksCombo the arm
                    // above has already taken the break, exactly as it did pre-199.
                    bool hadCombo = combo > 0;

                    breakRun();

                    if (hadCombo)
                    {
                        discardRestorableStreak();
                        raise(ComboBroken);
                    }
                }
                else
                {
                    creditCombo(press.TargetIndex);

                    // The one press that can credit combo it also broke (backlog 243): the space that
                    // skipped the word, now being judged on the gap the skip parked the caret on. The
                    // combo is real and stands, but it belongs to the break, so the claim remembers it
                    // and the next break has to beat it to take the claim away.
                    if (press.SkipLeftAClaimOutstanding)
                        creditTheClaimsOwnPress();
                }

                press.Cell.State = CellState.Correct;
                press.Cell.TypedChar = press.Char;
                press.Cell.JudgedDelta = press.Delta;
                press.Cell.FirstCorrectDelta = press.Delta; // retained through erasure and improved only by a better retype.

                // SyncTimeline records every AWARDED correct-char judgement, incl. Premature/
                // Lagging, and since backlog 148 EXCEPT an untimed space. This series is offset and
                // sync ANALYSIS: a record of where the player's hands sit against the map. A space
                // no longer measures that. Its delta is 0 by RULE rather than by observation, so
                // keeping it adds a sample that saw nothing and pulls the mean toward zero, and
                // keeping the true delta instead would be worse still, because a player told the
                // spacebar does not matter will type it loosely on purpose and every word gap in
                // the map would then widen the spread. Fewer honest samples beat more polluted
                // ones, and the lyric characters (which the player is still timing) are the whole
                // of what the analysis is about.
                if (!press.UntimedSpace)
                {
                    press.Cell.SyncSampleStart = syncTimeline.Count;
                    press.Cell.SyncSampleCount = 1;
                    syncTimeline.Add(new SyncSample(press.Time, press.Delta));
                }

                counts[type]++;
            }

            // Retain the best awarded result for visual feedback and silent replay reconstruction.
            press.Cell.JudgedTier ??= type;

            // Log the press for the HUD's rolling WPM. Both branches above land the cell Correct, so a
            // scoring-inert retype still counts here: this is a record of keystrokes, not of cell states.
            if (!UsesJapaneseKeystrokeWpm)
                pushRollingSample();
            else if (!committingJapanese)
                recordJapaneseInput();

            bool completedJapanese = UsesJapaneseKeystrokeWpm && !committingJapanese && !press.Cell.IsFreestyle
                                     && press.Cell.Expected != ' ' && press.Line.JapaneseInput?.IsJapaneseCell(press.TargetIndex) == true;

            // The cell the press landed on, which is what the display repaints and what a consumer
            // reading the judgement back has to be told: under AnyOrderWithinWord that is not always
            // where the caret is.
            int judgedCellIndex = press.TargetIndex;

            // The caret is the LEFTMOST UNTYPED TYPEABLE CELL of the line, always. Without the mod
            // that is one step forward, because the cell just resolved is the one the caret was on.
            // With it, a press that landed AHEAD of the caret moves nothing (the leftmost untyped
            // cell is still untyped) and a press that landed ON the caret rolls the frontier over the
            // whole run of cells already typed out of order behind it, both of which the walk below
            // says in one line.
            if (!AnyOrderWithinWord)
                caretIndex++;

            advanceCaretToFrontier();

            raise(CharJudged, new CharJudgement(activeLineIndex, judgedCellIndex, type, press.Delta, combo, Upgraded: upgraded));
            openHoldIfHeld(activeLineIndex, judgedCellIndex, press.Time);
            if (JapaneseCharacterTiming && press.Cell.EndTargetTime is double endTarget && press.Cell.FirstEndDelta == null)
            {
                double endTime = japaneseCommitEndTime ?? press.Time;
                // Finishing early is allowed, just like completing a syllable before its end.
                // Only lateness contributes to the finish grade and its timing feedback;
                // the independently judged start still uses its signed timing delta.
                double endDelta = Math.Max(0, endTime - endTarget);
                var endType = EraRules.AwardedTier(WindowsFor(press.Cell).Classify(endDelta), press.Cell.HeldWrongBeforeJudged, CorrectionCredit);
                press.Cell.FirstEndDelta = endDelta;
                press.Cell.EndJudgedTier = endType;
                counts[endType]++;
                creditCombo(judgedCellIndex);
                press.Cell.EndSyncSampleIndex = syncTimeline.Count;
                syncTimeline.Add(new SyncSample(endTime, endDelta));
                raise(CharJudged, new CharJudgement(activeLineIndex, judgedCellIndex, endType, endDelta, combo, Phase: 1));
            }
            else if (BestRetypeJudgement && JapaneseCharacterTiming && press.Cell.EndTargetTime is double retypeEnd && press.Cell.FirstEndDelta.HasValue)
            {
                double endTime = japaneseCommitEndTime ?? press.Time;
                double endDelta = Math.Max(0, endTime - retypeEnd);
                var endType = awardedRetypeTier(press.Cell, endDelta, false);
                if (betterTier(endType, press.Cell.EndJudgedTier!.Value))
                {
                    improveRetypedAward(press.Cell, endType, endDelta, endTime, finish: true);
                    raise(CharJudged, new CharJudgement(activeLineIndex, judgedCellIndex, endType, endDelta, combo, Phase: 1, Upgraded: true));
                }
            }
            rollForwardIfFinishedEarly(japaneseCommitEndTime ?? press.Time);
            if (completedJapanese)
                protectJapaneseCompletion();
            return true;
        }


        /// <summary>
        /// One press as it travels through the steps of <see cref="processKey"/>: the key (rewritten by
        /// mashing), its time, the line and cell it is judged on, and what the steps before it found.
        /// </summary>
        private struct KeyPress
        {
            public char Char;
            public readonly double Time;
            public TypingLine Line;
            public TypingCell Cell;
            public int TargetIndex;
            public double Delta;
            public bool UntimedSpace;

            /// <summary>
            /// Backlog 243: set when this press is a skip that left a claim outstanding, so the combo
            /// the SAME press goes on to earn on the word gap is recorded as the claim's OWN credit
            /// rather than as a run the player built after the break. Read once, at the one arm that
            /// increments the combo.
            /// </summary>
            public bool SkipLeftAClaimOutstanding;

            /// <summary>
            ///
            /// Backlog 260: the caret as it stood BEFORE a word skip moved it, which is what the rush
            /// cap measures this press against under LosslessSkipReclaim. Negative when this press
            /// skipped nothing, which is every press but one and leaves the cap exactly where it was.
            /// </summary>
            public int CaretBeforeSkip;

            public KeyPress(char key, double time)
            {
                Char = key;
                Time = time;
                Line = null!;
                Cell = null!;
                TargetIndex = 0;
                Delta = 0;
                UntimedSpace = false;
                SkipLeftAClaimOutstanding = false;
                CaretBeforeSkip = -1;
            }
        }

        /// <summary>
        /// After freestyle, random continued input must not spill onto an ordinary next line.
        /// The first typeable cell is the same one autoSkipForward will land on after the handoff.
        /// Explicit space/Enter and ordinary line endings keep their existing newline behavior.
        /// </summary>
        private bool typedNewlineMatchesAfterFreestyle(TypingLine finishedLine, char c)
        {
            TypingCell? lastRequired = null;
            for (int i = finishedLine.Cells.Count - 1; i >= 0; i--)
            {
                if (!finishedLine.Cells[i].IsTypeable)
                    continue;

                lastRequired = finishedLine.Cells[i];
                break;
            }

            if (lastRequired?.IsFreestyle != true)
                return true;

            var nextLine = lines[activeLineIndex + 1];
            for (int i = 0; i < nextLine.Cells.Count; i++)
            {
                var next = nextLine.Cells[i];
                if (!next.IsTypeable)
                    continue;
                if (next.IsFreestyle || charMatches(c, next.Expected))
                    return true;
                var group = JapaneseRomajiInput ? nextLine.JapaneseInput?.At(i) : null;
                return group != null && JapaneseRomaji.Matches(group.Reading, char.ToLowerInvariant(c).ToString(), nextLine.JapaneseInput!.Following(group)) != JapaneseRomaji.Match.Invalid;
            }

            return true;
        }

        /// <summary>
        /// ANY ORDER WITHIN A WORD (see <see cref="AnyOrderWithinWord"/>): the index of the first
        /// cell of the word the caret is inside that <paramref name="c"/> can be typed into, or null
        /// when there is none. The word is scanned with the two-sided <see cref="isWordGap"/> walk
        /// <see cref="skipCurrentWord"/> uses, so "word" still means exactly one thing in this file:
        /// punctuation the stream kept rides INSIDE the word, and only a typeable space ends it.
        ///
        /// <para>ASCENDING, and that is a contract rather than a detail: the answer is then a pure
        /// function of the cells' states and the pressed char, so a stored Dyslexia run re-derives
        /// keystroke for keystroke from the (char, time) pairs a replay holds and nothing else.</para>
        ///
        /// <para>Only an UNTYPED typeable cell is a candidate. A Correct or Wrong one is a cell the
        /// player is finished with (a Wrong one is corrected by backspacing back to it, which stays
        /// the one route into a spoiled cell), an Abandoned one is reclaimed by the backspace its own
        /// feature promises, and a non-typeable one is the auto-skip's business. A FREESTYLE slot is
        /// excluded for the reason given on <see cref="AnyOrderWithinWord"/>: it matches every key
        /// except space, so offering it here would swallow the first press of the word and starve every
        /// exact match in it.</para>
        ///
        /// <para>The match is the SAME test the caret cell is matched with in
        /// <see cref="ProcessKey"/>, <see cref="CaseSensitive"/> included, so a wrong-case letter
        /// satisfies nothing under the Literate mod whether it is typed in order or out of it.</para>
        /// </summary>
        private int? matchWithinWord(IReadOnlyList<TypingCell> cells, char c)
        {
            int start = caretIndex;
            int end = caretIndex;

            while (start > 0 && !isWordGap(cells[start - 1]))
                start--;

            while (end < cells.Count && !isWordGap(cells[end]))
                end++;

            for (int i = start; i < end; i++)
            {
                var cell = cells[i];

                if (!cell.IsTypeable || cell.IsFreestyle || cell.State != CellState.Untyped)
                    continue;

                if (charMatches(c, cell.Expected))
                    return i;
            }

            return null;
        }
    }
}
