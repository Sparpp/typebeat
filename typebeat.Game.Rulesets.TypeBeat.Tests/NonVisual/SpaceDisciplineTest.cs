// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 184: MONKEYTYPE SPACE DISCIPLINE. The spacebar is the word BOUNDARY, a key the player owes
// at every gap, and TypingEngine.StrictSpaces is the two rules that make it one. Which rule a run
// gets is decided by SpaceSkipsWord, because each fixes what that setting's own arm did with a
// misplaced space:
//
//   * with word skipping ON, a wrong letter on the WORD GAP parks the caret on the gap instead of
//     carrying it into the next word. Before this, the follow-up space met a caret sitting on a
//     lyric character and the skip gate gave up the WHOLE next word for one mistimed keystroke.
//   * with word skipping OFF, a SPACE typed inside a word is a typo like any other (typed through,
//     backspaceable) rather than a gatekeeper rejection. With no word to skip, that press means
//     nothing else.
//
// It is an ERA (CONFIG frame flags bit 4, default FALSE) for the reason every input-model change
// here is one: both halves decide WHERE THE CARET ENDS UP after an already-recorded keystroke, so a
// replay stored before backlog 184 re-derived under the live rule would desynchronise from the first
// misplaced space onwards. The era region at the bottom is that guarantee.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class SpaceDisciplineTest
    {
        #region Fixture

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        /// <summary>
        /// "this moment", the line the whole feature reads off: a four-letter word, THE gap at cell 4,
        /// and a six-letter word after it that a mistimed space used to throw away whole.
        /// </summary>
        private static LyricLine thisMomentLine(double end) => new LyricLine
        {
            RawText = "this moment",
            StartTime = 1000,
            EndTime = end,
            SingEndTime = 4000,
            Units = new[] { unit("this", 1000, 2000), unit("moment", 2000, 4000) },
        };

        private static LyricBeatmap map(LyricLine line) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = new List<LyricLine> { line },
            Granularity = TimingGranularity.Line,
        };

        /// <summary>The line with no reachable deadline, so nothing seals mid-test.</summary>
        private static LyricBeatmap thisMoment() => map(thisMomentLine(60000));

        /// <summary>The same line as a playable beatmap, with the nested per-cell objects the score
        /// processor's maximum statistics come from.</summary>
        private static TypeBeatBeatmap playableThisMoment()
        {
            var beatmap = new TypeBeatBeatmap();
            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = 1000, LineIndex = 0, Line = thisMomentLine(5000), Granularity = TimingGranularity.Line });

            foreach (var hitObject in beatmap.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return beatmap;
        }

        /// <summary>
        /// An engine with the line active and BOTH input-model eras selected explicitly, never
        /// defaulted: the default is the other era in each case, which is the whole point of them.
        /// </summary>
        private static TypingEngine started(bool strictSpaces, bool spaceSkipsWord)
        {
            var engine = new TypingEngine(thisMoment())
            {
                WrongInputOnWordGaps = true,
                StrictSpaces = strictSpaces,
                SpaceSkipsWord = spaceSkipsWord,
            };

            engine.Update(1000);
            Assert.That(engine.ActiveLineIndex, Is.Zero);
            return engine;
        }

        private static IReadOnlyList<TypingCell> cells(TypingEngine engine) => engine.Lines[0].Cells;

        /// <summary>Type <paramref name="text"/> into consecutive cells from <paramref name="from"/>,
        /// each press dead on its own cell's target.</summary>
        private static void typeFrom(TypingEngine engine, int from, string text)
        {
            for (int i = 0; i < text.Length; i++)
                Assert.That(engine.ProcessKey(text[i], cells(engine)[from + i].TargetTime), Is.True, $"typing '{text[i]}' at cell {from + i}");
        }

        /// <summary>"this", cleanly: the caret lands on the gap with a streak of 4 behind it.</summary>
        private static void typeThis(TypingEngine engine)
        {
            typeFrom(engine, 0, "this");
            Assert.That(engine.CaretIndex, Is.EqualTo(gap));
            Assert.That(engine.Combo, Is.EqualTo(4));
        }

        private const int gap = 4;

        private static double gapTime(TypingEngine engine) => cells(engine)[gap].TargetTime;

        #endregion

        /// <summary>The fixture's own shape, asserted rather than trusted: every index below is read
        /// off this layout, and the gap sits at cell 4.</summary>
        [Test]
        public void TheFixtureIsTwoWordsAroundTheGapAtFour()
        {
            var c = cells(started(strictSpaces: true, spaceSkipsWord: true));

            Assert.Multiple(() =>
            {
                Assert.That(new string(c.Select(x => x.Expected).ToArray()), Is.EqualTo("this moment"));
                Assert.That(c[gap].Expected, Is.EqualTo(' '));
                Assert.That(c[gap].IsTypeable, Is.True, "the gap is a typeable cell");
                Assert.That(c[gap].IsCountable, Is.False, "and an uncountable one");
                Assert.That(c, Has.Count.EqualTo(11));
            });
        }

        // -----------------------------------------------------------------------------------------
        // Rule one: with word skipping ON, a gap typo holds the caret
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The headline. A wrong letter on the gap is accounted EXACTLY as it was before (the cell
        /// takes it, the streak goes, the mistype counts, the mash-fail streak is untouched) and the
        /// caret does not move: the space that gap was owed is still owed.
        /// </summary>
        [Test]
        public void AGapTypoHoldsTheCaretOnTheGap()
        {
            var engine = started(strictSpaces: true, spaceSkipsWord: true);
            var judged = new List<CharJudgement>();
            engine.CharJudged += j => judged.Add(j);

            typeThis(engine);

            Assert.That(engine.ProcessKey('m', gapTime(engine)), Is.True, "the 'm' of the next word, one keystroke early");

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[gap].State, Is.EqualTo(CellState.Wrong));
                Assert.That(cells(engine)[gap].TypedChar, Is.EqualTo('m'));
                Assert.That(engine.CaretIndex, Is.EqualTo(gap), "the caret PARKED on the gap it spoiled");
                Assert.That(engine.Combo, Is.Zero);
                Assert.That(engine.Mistypes, Is.EqualTo(1));
                Assert.That(engine.ConsecutiveWrongKeys, Is.Zero, "type-through never feeds the mash-fail streak");
                Assert.That(judged[^1].Type, Is.EqualTo(JudgementType.WrongChar));
                Assert.That(judged[^1].CellIndex, Is.EqualTo(gap));
            });

            // And the player SEES "thism moment": the gap has no glyph of its own, so a wrong one
            // shows the character that went into it (backlog 181's rule, unchanged).
            Assert.That(LyricLineDisplay.CellGlyph(' ', CellState.Wrong, 'm'), Is.EqualTo('m'));
        }

        /// <summary>
        /// The space then STEPS OVER the spoiled gap. It does not earn the CELL, whose character is
        /// not the one that went into it: the typo stands as an unfixed one and the caret moves to the
        /// first letter of the next word. Nothing about the press is judged, because the typo already
        /// took the break and this press cannot be asked to pay for it twice.
        ///
        /// <para>It IS a correct keypress, for that same reason: the space is the right key for the
        /// cell it lands on, so it credits accuracy rather than charging for the recovery. The unfixed
        /// typo is paid for in COMPLETION, which is where an unfixed typo belongs.</para>
        /// </summary>
        [Test]
        public void ASpaceStepsOverTheSpoiledGapAndLeavesTheTypoStanding()
        {
            var engine = started(strictSpaces: true, spaceSkipsWord: true);
            typeThis(engine);
            Assert.That(engine.ProcessKey('m', gapTime(engine)), Is.True);

            var judged = new List<CharJudgement>();
            engine.CharJudged += j => judged.Add(j);
            long score = engine.Score;

            Assert.That(engine.ProcessKey(' ', gapTime(engine)), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(engine.CaretIndex, Is.EqualTo(gap + 1), "the caret stepped over the gap, onto the 'm' of \"moment\"");
                Assert.That(cells(engine)[gap].State, Is.EqualTo(CellState.Wrong), "the typo is NOT rewritten to Correct");
                Assert.That(cells(engine)[gap].TypedChar, Is.EqualTo('m'));
                Assert.That(cells(engine)[gap].JudgedDelta, Is.Null, "and the cell was never judged");
                Assert.That(judged, Is.Empty, "a step-over judges nothing");
                Assert.That(engine.Score, Is.EqualTo(score));
                Assert.That(engine.Combo, Is.Zero, "no combo gained, and none broken a second time");
                Assert.That(engine.Mistypes, Is.EqualTo(1), "the step-over is not itself a mistype");
                Assert.That(engine.LiveAccuracy, Is.EqualTo(5 / 6.0).Within(1e-12), "and it counts CORRECT: 4 letters + this space, over 6 presses");
            });

            // The rest of the word types normally from there, which is the whole point: one mistimed
            // keystroke costs one cell, not a word.
            typeFrom(engine, gap + 1, "moment");

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine).Count(c => c.State == CellState.Correct), Is.EqualTo(10));
                Assert.That(cells(engine).Count(c => c.State == CellState.Wrong), Is.EqualTo(1));
                Assert.That(engine.Combo, Is.EqualTo(6));
                Assert.That(engine.LiveAccuracy, Is.EqualTo(11 / 12.0).Within(1e-12), "the typo is the one press of the twelve that was wrong");
            });
        }

        /// <summary>
        /// Further wrong letters on a parked gap overwrite the same cell: they cost a keypress and an
        /// error each (the combo is already gone), and the caret still does not move. One park is ONE
        /// unfixed typo however many letters land on it, which is what stops a fumbled gap from
        /// eating the next word one cell at a time.
        /// </summary>
        [Test]
        public void RepeatedLettersOnAParkedGapMakeOneWrongCellAndManyErrors()
        {
            var engine = started(strictSpaces: true, spaceSkipsWord: true);
            typeThis(engine);

            foreach (char c in "mox")
                Assert.That(engine.ProcessKey(c, gapTime(engine)), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine).Count(c => c.State == CellState.Wrong), Is.EqualTo(1), "one spoiled cell, not three");
                Assert.That(cells(engine)[gap].TypedChar, Is.EqualTo('x'), "showing the most recent attempt");
                Assert.That(engine.CaretIndex, Is.EqualTo(gap));
                Assert.That(engine.Mistypes, Is.EqualTo(3), "each attempt is still a wrong keypress");
                Assert.That(engine.LiveAccuracy, Is.EqualTo(4 / 7.0).Within(1e-12),
                    "and each still costs the accuracy denominator: 4 correct letters over 7 presses, no step-over space among them");
            });
        }

        /// <summary>
        /// Backspace on a parked gap clears it WHERE IT SITS: the caret does not retreat into the
        /// perfectly good word in front of it, because that word is not what the player is looking at.
        /// The erase announces itself like any other typo erase (which is what refunds the health the
        /// keypress drained), and the corrected space then earns the cell plus the streak the typo
        /// broke, through the existing backlog 140 machinery.
        /// </summary>
        [Test]
        public void BackspaceClearsAParkedGapInPlace()
        {
            var engine = started(strictSpaces: true, spaceSkipsWord: true);

            int erased = 0;
            int? restored = null;
            engine.TypoErased += () => erased++;
            engine.ComboRestored += amount => restored = amount;

            typeThis(engine);
            Assert.That(engine.ProcessKey('m', gapTime(engine)), Is.True);

            Assert.That(engine.ProcessBackspace(), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[gap].State, Is.EqualTo(CellState.Untyped));
                Assert.That(cells(engine)[gap].TypedChar, Is.Null);
                Assert.That(cells(engine)[gap].JudgedDelta, Is.Null);
                Assert.That(engine.CaretIndex, Is.EqualTo(gap), "the caret did not move: the gap is still owed its space");
                Assert.That(cells(engine)[gap - 1].State, Is.EqualTo(CellState.Correct), "and the 's' of \"this\" was not touched");
                Assert.That(erased, Is.EqualTo(1));
            });

            Assert.That(engine.ProcessKey(' ', gapTime(engine)), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[gap].State, Is.EqualTo(CellState.Correct));
                Assert.That(restored, Is.EqualTo(4), "the streak the typo broke comes back at the fix");
                Assert.That(engine.Combo, Is.EqualTo(5));
            });
        }

        /// <summary>
        /// THE BUG, pinned on the arm that still has it. Under the classic era the gap typo carries the
        /// caret onto the 'm' of "moment", so the follow-up space is a mid-word space and the skip gate
        /// abandons the entire word: one mistimed keystroke, six characters gone.
        /// </summary>
        [Test]
        public void TheClassicEraGivesUpTheWholeNextWordInstead()
        {
            var engine = started(strictSpaces: false, spaceSkipsWord: true);
            typeThis(engine);

            Assert.That(engine.ProcessKey('m', gapTime(engine)), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(gap + 1), "the classic gap typo advances");

            Assert.That(engine.ProcessKey(' ', gapTime(engine)), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine).Count(c => c.State == CellState.Abandoned), Is.EqualTo(6), "the whole of \"moment\"");
                Assert.That(engine.CaretIndex, Is.EqualTo(11), "and the caret is at the end of the line");
            });
        }

        /// <summary>
        /// The park is scoped to <see cref="TypingEngine.SpaceSkipsWord"/>, because that is where the
        /// damage was. With word skipping off, a gap typo advances exactly as backlog 181 left it: the
        /// player's next space lands on the first letter of the next word and is a typo of its own,
        /// which costs one cell rather than a word.
        /// </summary>
        [Test]
        public void AGapTypoStillAdvancesWhenWordSkippingIsOff()
        {
            var engine = started(strictSpaces: true, spaceSkipsWord: false);
            typeThis(engine);

            Assert.That(engine.ProcessKey('m', gapTime(engine)), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[gap].State, Is.EqualTo(CellState.Wrong));
                Assert.That(engine.CaretIndex, Is.EqualTo(gap + 1));
            });
        }

        // -----------------------------------------------------------------------------------------
        // Rule two: with word skipping OFF, a mid-word space is a typo
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// A SPACE typed inside a word takes the ordinary wrong-input path: the cell holds it, the
        /// caret advances, backspace takes it back. No special case, because there is nothing special
        /// about it: with no word to skip, the press is simply a wrong character.
        /// </summary>
        [Test]
        public void AMidWordSpaceIsTypedThroughAsAnOrdinaryTypo()
        {
            var engine = started(strictSpaces: true, spaceSkipsWord: false);
            char? rejected = null;
            engine.WrongKeyRejected += c => rejected = c;

            typeFrom(engine, 0, "th");

            Assert.That(engine.ProcessKey(' ', cells(engine)[2].TargetTime), Is.True, "on the 'i' of \"this\"");

            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.Null, "not a rejection any more");
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Wrong));
                Assert.That(cells(engine)[2].TypedChar, Is.EqualTo(' '));
                Assert.That(engine.CaretIndex, Is.EqualTo(3));
                Assert.That(engine.Combo, Is.Zero);
                Assert.That(engine.Mistypes, Is.EqualTo(1));
                Assert.That(engine.ConsecutiveWrongKeys, Is.Zero, "a typed-through key never feeds the mash-fail streak");
            });

            // Backspace-correctable like any other typo.
            Assert.That(engine.ProcessBackspace(), Is.True);
            Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Untyped));
            Assert.That(engine.CaretIndex, Is.EqualTo(2));
        }

        /// <summary>
        /// Rendering needs no arm for it, which is why the delta is not special-cased: a wrong LYRIC
        /// cell keeps showing its EXPECTED character in the error red (the substitution is scoped to
        /// GAPS), so the line still reads as the line it was meant to be and an invisible red space is
        /// never drawn.
        /// </summary>
        [Test]
        public void AMidWordSpaceRendersAsTheExpectedCharacter()
        {
            Assert.Multiple(() =>
            {
                Assert.That(LyricLineDisplay.CellGlyph('i', CellState.Wrong, ' '), Is.EqualTo('i'));
                Assert.That(LyricLineDisplay.CellFillColour(CellState.Wrong, isFreestyle: false, inSungSyllable: false, syncQuality: null),
                    Is.EqualTo(TypeBeatStyle.ErrorChar), "wearing the same error red every typo wears");
            });
        }

        /// <summary>
        /// The classic era, which is what that same keystroke did before backlog 184 and what every
        /// stored replay still has to do: rejected outright, caret frozen, and the mash-fail streak
        /// growing, because that guard exists to police exactly this branch.
        /// </summary>
        [Test]
        public void TheClassicEraStillRejectsAMidWordSpace()
        {
            var engine = started(strictSpaces: false, spaceSkipsWord: false);
            char? rejected = null;
            engine.WrongKeyRejected += c => rejected = c;

            typeFrom(engine, 0, "th");

            Assert.That(engine.ProcessKey(' ', cells(engine)[2].TargetTime), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(rejected, Is.EqualTo(' '));
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Untyped));
                Assert.That(engine.CaretIndex, Is.EqualTo(2), "caret frozen: nothing entered the cell");
                Assert.That(engine.ConsecutiveWrongKeys, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// The knock-on, stated on purpose: mid-word spaces stop feeding the 13-key mash-fail streak
        /// in live play, because they no longer reach the rejection branch that grows it. The streak
        /// still does its job on the arm that still rejects them (see
        /// <c>UntimedSpaceTest.TheMashFailStreakStillAccruesOnRejectedSpaces</c>), and under
        /// Gatekeeper, which is the model the guard was written for.
        /// </summary>
        [Test]
        public void MidWordSpacesNoLongerFeedTheMashFailStreak()
        {
            var engine = started(strictSpaces: true, spaceSkipsWord: false);

            for (int i = 0; i < 4; i++)
                Assert.That(engine.ProcessKey(' ', 1000 + i), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(engine.ConsecutiveWrongKeys, Is.Zero);
                Assert.That(engine.CaretIndex, Is.EqualTo(gap), "four spaces spelled \"this\" wrong and arrived at the gap");
                Assert.That(cells(engine).Count(c => c.State == CellState.Wrong), Is.EqualTo(4));
            });
        }

        /// <summary>
        /// Word skipping still wins where it applies: with the setting ON the skip gate intercepts a
        /// mid-word space before the match, so nothing about rule two is reachable there. The two
        /// halves of StrictSpaces are exclusive by construction, not by coincidence.
        /// </summary>
        [Test]
        public void WordSkippingStillInterceptsAMidWordSpace()
        {
            var engine = started(strictSpaces: true, spaceSkipsWord: true);

            typeFrom(engine, 0, "th");
            Assert.That(engine.ProcessKey(' ', cells(engine)[2].TargetTime), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Abandoned), "the space skipped the word");
                Assert.That(cells(engine)[3].State, Is.EqualTo(CellState.Abandoned));
                Assert.That(cells(engine)[gap].State, Is.EqualTo(CellState.Correct), "and landed on the gap as a typed space");
                Assert.That(engine.Mistypes, Is.Zero);
            });
        }

        /// <summary>
        /// Gatekeeper refuses everything, whichever way the new flag points: StrictSpaces extends
        /// <see cref="TypingEngine.AllowWrongInput"/> rather than competing with it, exactly as
        /// <see cref="TypingEngine.WrongInputOnWordGaps"/> does.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void GatekeeperRejectsAMidWordSpaceUnderBothEras(bool strictSpaces)
        {
            var engine = started(strictSpaces, spaceSkipsWord: false);
            engine.AllowWrongInput = false;

            typeFrom(engine, 0, "th");
            Assert.That(engine.ProcessKey(' ', cells(engine)[2].TargetTime), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Untyped));
                Assert.That(engine.CaretIndex, Is.EqualTo(2));
                Assert.That(engine.ConsecutiveWrongKeys, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// A FREESTYLE slot keeps refusing the space key under every arm, which is a deliberate carve
        /// out of rule two rather than an oversight. The slot's promise is "any character except the
        /// word-advance key" (backlog 50), and it has no expected glyph to redden: a space typed into
        /// one would BLANK the cell instead of marking it, since a filled freestyle cell renders the
        /// character the player pressed.
        /// </summary>
        [Test]
        public void AFreestyleSlotStillRefusesTheSpaceKey()
        {
            var freestyle = new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata
                {
                    Artist = "Test",
                    Title = "Song",
                    FolderPath = @"X:\nowhere",
                    AudioFileName = "a.mp3",
                },
                Lines = new List<LyricLine>
                {
                    new LyricLine
                    {
                        RawText = "a" + Typeability.FREESTYLE_MARKER + "b",
                        StartTime = 1000,
                        EndTime = 60000,
                        SingEndTime = 4000,
                        Units = new[] { unit("a" + Typeability.FREESTYLE_MARKER + "b", 1000, 4000) },
                    },
                },
                Granularity = TimingGranularity.Word,
            };

            var engine = new TypingEngine(freestyle) { WrongInputOnWordGaps = true, StrictSpaces = true };
            engine.Update(1000);

            Assert.That(engine.Lines[0].Cells[1].IsFreestyle, Is.True, "the fixture's middle cell is the free slot");
            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey(' ', 2000), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(engine.Lines[0].Cells[1].State, Is.EqualTo(CellState.Untyped), "NOT Wrong: the slot would render blank");
                Assert.That(engine.Lines[0].Cells[1].TypedChar, Is.Null);
                Assert.That(engine.CaretIndex, Is.EqualTo(1), "caret unmoved: the slot is still open");
                Assert.That(engine.ConsecutiveWrongKeys, Is.EqualTo(1), "the strict path, exactly as before");
            });
        }

        // -----------------------------------------------------------------------------------------
        // Ctrl+A anchors on the EARLIEST typo (backlog 184's third half, a pure query)
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// With typos in two different words the EARLIEST wins, so one gesture offers back everything
        /// that has to be retyped. The old rule (nearest) made the gesture a loop the player could not
        /// see the end of: press, retype, press again, with nothing in the caret to say how many
        /// rounds were left.
        /// </summary>
        [Test]
        public void TheAnchorTakesTheEarliestTypoOnTheLine()
        {
            var engine = started(strictSpaces: true, spaceSkipsWord: false);

            Assert.That(engine.ProcessKey('x', cells(engine)[0].TargetTime), Is.True, "wrong for 't'");
            typeFrom(engine, 1, "his");
            Assert.That(engine.ProcessKey(' ', gapTime(engine)), Is.True);
            Assert.That(engine.ProcessKey('z', cells(engine)[5].TargetTime), Is.True, "wrong for the 'm' of \"moment\"");

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[0].State, Is.EqualTo(CellState.Wrong));
                Assert.That(cells(engine)[5].State, Is.EqualTo(CellState.Wrong));
                Assert.That(engine.RetypeSelectionAnchor, Is.Zero, "the head of \"this\", not of \"moment\"");
            });
        }

        /// <summary>
        /// A GAP typo still anchors on the gap itself when it is the earliest one: the gap is the cell
        /// to retype and it belongs to no word, so walking back from it would swallow the perfectly
        /// good word in front of it. The two rules compose rather than fighting.
        /// </summary>
        [Test]
        public void TheEarliestTypoBeingAGapStillAnchorsOnTheGap()
        {
            var engine = started(strictSpaces: false, spaceSkipsWord: false);

            typeThis(engine);
            Assert.That(engine.ProcessKey('m', gapTime(engine)), Is.True, "a typo on the gap, which advances on this arm");
            Assert.That(engine.ProcessKey('z', cells(engine)[5].TargetTime), Is.True, "and another in \"moment\"");

            Assert.That(engine.RetypeSelectionAnchor, Is.EqualTo(gap), "the gap, so \"this\" is left alone");
        }

        // -----------------------------------------------------------------------------------------
        // The ERA: every replay on disk keeps the caret it was played with
        // -----------------------------------------------------------------------------------------

        private const int bit_wrong_input = 1;
        private const int bit_space_skips_word = 2;
        private const int bit_syllable_timing = 4;
        private const int bit_wrong_input_on_word_gaps = 8;
        private const int bit_strict_spaces = 16;

        /// <summary>
        /// The load-bearing default: a bare engine, and therefore a replay with no CONFIG frame and
        /// every replay written before backlog 184, gets the classic space rules. A default of the
        /// live rule would silently re-derive every stored run's misplaced space onto the wrong cell.
        /// </summary>
        [Test]
        public void TheEngineDefaultsToTheClassicSpaceRules()
        {
            Assert.That(new TypingEngine(thisMoment()).StrictSpaces, Is.False);
        }

        /// <summary>
        /// Live play turns it on UNCONDITIONALLY, Hard Rock included, for the same reason
        /// <see cref="TypingEngine.WrongInputOnWordGaps"/> is: HR halves the judgement WINDOWS, and
        /// this is not a window, it is what the spacebar means. It is also not a user setting, which
        /// is why there is nothing here reading config.
        /// </summary>
        [Test]
        public void LivePlayTurnsStrictSpacesOnForEveryModStack()
        {
            Assert.Multiple(() =>
            {
                Assert.That(liveEngine().StrictSpaces, Is.True);
                Assert.That(liveEngine(new TypeBeatModHardRock()).StrictSpaces, Is.True, "an input model, not a window");
                Assert.That(liveEngine(new TypeBeatModEasy()).StrictSpaces, Is.True);
                Assert.That(liveEngine(new TypeBeatModDoubleTime(), new TypeBeatModHardRock()).StrictSpaces, Is.True);

                // The contrast, restated so the asymmetry stays deliberate.
                Assert.That(liveEngine(new TypeBeatModHardRock()).SyllableTiming, Is.False);
            });
        }

        /// <summary>
        /// The bit is at the position the format names, so the encoded word stays readable as a
        /// number: a replay of live play (wrong input allowed, syllable judgement, gap typos, strict
        /// spaces) is exactly 1 | 4 | 8 | 16 = 29, and 31 with word skipping on top.
        /// </summary>
        [Test]
        public void TheFlagsWordCarriesBitFour()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true)
                                               .ToLegacy(new Beatmap()).MouseY, Is.EqualTo(29f));

                Assert.That(TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: true, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true)
                                               .ToLegacy(new Beatmap()).MouseY, Is.EqualTo(31f));

                // The older call sites keep meaning what they always did.
                Assert.That(TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: true)
                                               .ToLegacy(new Beatmap()).MouseY, Is.EqualTo(13f));
            });
        }

        /// <summary>
        /// 0..15 are the only flags words that existed before backlog 184, and every one of them must
        /// decode with bit 4 CLEAR, i.e. to the space rules those runs were played on. The four older
        /// bits keep their meaning and their positions exactly.
        /// </summary>
        [Test]
        public void ReplaysRecordedBeforeStrictSpacesDecodeAsClassic([Range(0, 15)] int storedFlags)
        {
            var decoded = decode(storedFlags);

            Assert.Multiple(() =>
            {
                Assert.That(decoded.IsConfig, Is.True);
                Assert.That(decoded.StrictSpaces, Is.False, "a replay from before the rules existed played the other ones");
                Assert.That(decoded.AllowWrongInput, Is.EqualTo((storedFlags & bit_wrong_input) != 0));
                Assert.That(decoded.SpaceSkipsWord, Is.EqualTo((storedFlags & bit_space_skips_word) != 0));
                Assert.That(decoded.SyllableTiming, Is.EqualTo((storedFlags & bit_syllable_timing) != 0));
                Assert.That(decoded.WrongInputOnWordGaps, Is.EqualTo((storedFlags & bit_wrong_input_on_word_gaps) != 0));

                // ...and the same word with bit 4 added decodes to the live rules, changing nothing else.
                var live = decode(storedFlags | bit_strict_spaces);
                Assert.That(live.StrictSpaces, Is.True);
                Assert.That(live.AllowWrongInput, Is.EqualTo(decoded.AllowWrongInput));
                Assert.That(live.SpaceSkipsWord, Is.EqualTo(decoded.SpaceSkipsWord));
                Assert.That(live.SyllableTiming, Is.EqualTo(decoded.SyllableTiming));
                Assert.That(live.WrongInputOnWordGaps, Is.EqualTo(decoded.WrongInputOnWordGaps));
            });
        }

        /// <summary>
        /// The behavioural half of the same guarantee, through the real headless scorer on the run
        /// this whole file is about (a gap typo, then the space). A STORED replay, bit 4 clear, still
        /// gives up the whole of "moment": six misses, four cells typed out of eleven. That is what
        /// those rows hold today and nothing here may move it.
        /// </summary>
        [Test]
        public void AStoredReplayStillGivesUpTheWholeWord()
        {
            var stored = scoreSpaceRun(bit_wrong_input | bit_space_skips_word | bit_wrong_input_on_word_gaps);

            Assert.Multiple(() =>
            {
                Assert.That(stored.Statistics.GetValueOrDefault(HitResult.Miss), Is.EqualTo(6), "the whole of \"moment\" was abandoned");
                Assert.That(stored.Statistics.GetValueOrDefault(TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(1), "plus the gap the typo took");
                Assert.That(stored.Completion, Is.EqualTo(4 / 11.0).Within(1e-9));
                Assert.That(stored.UnconsumedFrames, Is.Zero);
            });
        }

        /// <summary>
        /// The same keystrokes recorded LIVE (bit 4 set) instead: the caret parks, the space steps
        /// over, and the run finishes with ten of eleven cells typed and one unfixed typo. Nothing is
        /// missed, which is the whole of what the feature buys.
        /// </summary>
        [Test]
        public void ALiveReplayKeepsTheWordAndOneUnfixedTypo()
        {
            var live = scoreSpaceRun(bit_wrong_input | bit_space_skips_word | bit_wrong_input_on_word_gaps | bit_strict_spaces);

            Assert.Multiple(() =>
            {
                Assert.That(live.Statistics.GetValueOrDefault(HitResult.Miss), Is.Zero);
                Assert.That(live.Statistics.GetValueOrDefault(TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(1));
                Assert.That(live.Statistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(10));
                Assert.That(live.Completion, Is.EqualTo(10 / 11.0).Within(1e-9));
                Assert.That(live.UnconsumedFrames, Is.Zero);
            });
        }

        /// <summary>
        /// A live replay survives the LEGACY (.osr) encoding: the flags word is written to MouseY,
        /// read back by <see cref="TypeBeatReplayFrame.FromLegacy"/>, and the account re-derived from
        /// the decoded frames is bit for bit the one the in-memory frames produce. That is what makes
        /// bit 4 an era carrier rather than a field that happens to exist.
        /// </summary>
        [Test]
        public void ALiveReplayRoundTripsThroughTheLegacyEncodingAndReDerivesIdentically()
        {
            int flags = bit_wrong_input | bit_space_skips_word | bit_wrong_input_on_word_gaps | bit_strict_spaces;

            var direct = TypeBeatReplayScorer.Score(playableThisMoment(), Array.Empty<Mod>(), spaceRun(flags), TypoRule.Deferred, ComboRestoreRule.OnFix);
            var roundTripped = TypeBeatReplayScorer.Score(playableThisMoment(), Array.Empty<Mod>(), throughLegacy(spaceRun(flags)), TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.Multiple(() =>
            {
                Assert.That(roundTripped.Statistics, Is.EquivalentTo(direct.Statistics));
                Assert.That(roundTripped.MaxCombo, Is.EqualTo(direct.MaxCombo));
                Assert.That(roundTripped.TotalScore, Is.EqualTo(direct.TotalScore));
                Assert.That(roundTripped.Accuracy, Is.EqualTo(direct.Accuracy));
                Assert.That(roundTripped.Completion, Is.EqualTo(direct.Completion));
                Assert.That(roundTripped.Rank, Is.EqualTo(direct.Rank));

                // ...and it really is the live arm that was re-derived, not the default one.
                Assert.That(roundTripped.Statistics.GetValueOrDefault(HitResult.Miss), Is.Zero);
                Assert.That(roundTripped.Statistics.GetValueOrDefault(TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(1));
            });
        }

        #region Era harness

        private static TypeBeatReplayFrame decode(int storedFlags)
        {
            var frame = new TypeBeatReplayFrame();
            frame.FromLegacy(new LegacyReplayFrame(500, (float)TypeBeatReplayFrame.CONFIG, storedFlags, ReplayButtonState.None), new Beatmap());
            return frame;
        }

        /// <summary>
        /// "this", a wrong key ON THE GAP, the space it was owed, then "moment". Integral times only,
        /// because that is what the legacy encoding stores. <paramref name="flags"/> goes through the
        /// LEGACY DECODE, so the era arm is the one a stored .osr really produces rather than one the
        /// test constructs.
        /// </summary>
        private static Replay spaceRun(int flags)
        {
            var config = decode(flags);
            config.Time = 1000;

            var replay = new Replay();

            replay.Frames.Add(config);

            foreach ((char c, double time) in new[]
                     {
                         ('t', 1000d), ('h', 1250), ('i', 1500), ('s', 1750),
                         ('m', 2000), // the word gap, one keystroke early
                         (' ', 2000),
                         ('m', 2000), ('o', 2333), ('m', 2667), ('e', 3000), ('n', 3333), ('t', 3667),
                     })
            {
                replay.Frames.Add(new TypeBeatReplayFrame(time, c));
            }

            return replay;
        }

        /// <summary>Every frame encoded to legacy and decoded back the way the score decoder does.</summary>
        private static Replay throughLegacy(Replay replay)
        {
            var carried = new Replay();

            foreach (var frame in replay.Frames.OfType<TypeBeatReplayFrame>())
            {
                var legacy = frame.ToLegacy(new Beatmap());
                double storedTime = Math.Round(legacy.Time);

                var decoded = new TypeBeatReplayFrame();
                decoded.FromLegacy(new LegacyReplayFrame(storedTime, legacy.MouseX, legacy.MouseY, legacy.ButtonState), new Beatmap());
                decoded.Time = storedTime; // LegacyScoreDecoder.convertFrame overwrites Time after FromLegacy.

                carried.Frames.Add(decoded);
            }

            return carried;
        }

        private static TypeBeatReplayAccount scoreSpaceRun(int flags)
            => TypeBeatReplayScorer.Score(playableThisMoment(), Array.Empty<Mod>(), spaceRun(flags), TypoRule.Deferred, ComboRestoreRule.OnFix);

        /// <summary>A drawable ruleset built over the fixture exactly as gameplay builds it, mods and
        /// all: the engine is a lazy property off the constructor's beatmap and mod list.</summary>
        private static TypingEngine liveEngine(params Mod[] mods)
            => new DrawableTypeBeatRuleset(new TypeBeatRuleset(), playableThisMoment(), mods).Engine;

        #endregion
    }
}
