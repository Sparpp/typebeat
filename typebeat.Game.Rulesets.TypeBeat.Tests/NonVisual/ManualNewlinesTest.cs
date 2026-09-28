// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// MANUAL NEWLINES (the setting): with it on, a FINISHED line is the player's to close - space or
    /// Enter hands the caret to the next line - and neither of the two time-driven arms does it for
    /// them. The newline ALWAYS LANDS, however early it is pressed: landing on the next line before
    /// its entry window opens leaves the caret WAITING there (the characters greyed, every key
    /// swallowed) until the window opens, and a backspace from the head of that line steps back up.
    ///
    /// <para>THE ONE THING THAT MOVES THE CARET WITHOUT A PRESS is the song's, and it is pinned here.
    /// A finished caret is handed on at the line's DRAG CUTOFF - the instant the push warning's red
    /// bar completes, one <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> past the line's own
    /// deadline, and the same moment a caret still dragging behind on that line is force-sealed at
    /// with the setting off. Not the last sung word's end, which can be seconds earlier, and not the
    /// line's own deadline either, which in any ordinary map is the next line's first word: those are
    /// the two instants the setting has to hold the caret through. That same cutoff closes the step
    /// back up, so a caret the song moved is never one the player can walk back from.</para>
    ///
    /// <para>Every fixture here is the same three-line map, so the numbers are worth writing down
    /// once. L0 "ab" [1000, 7000] (a = 1000, b = 1500) with its sung word ending at 2000, L1 "cd"
    /// [7000, 12000] with its words at [8000, 9000] (c = 8000, d = 8500), L2 "ef" [12000, 17000] with
    /// its words at [13000, 14000]. A line's activation is never earlier than its own start, so L1
    /// activates at 7000 and the rush bound opens entry into it at 5500 (ActivationTime -
    /// FLETCHER_DRAG_GRACE_MS). Three instants a finished caret could be moved at, a
    /// second and then a second and a half apart: the last sung word ending (2000), L0's own deadline
    /// of 7000 - which is also L1's activation and therefore its first word - and the drag cutoff at
    /// 8500 (7000 + the line's zero seal grace + 1500). Only the last of the three hands a manual
    /// caret on, and the two gaps are the point of the fixture: waiting out both is what the setting
    /// asks of a player who does not press.</para>
    /// </summary>
    [TestFixture]
    public class ManualNewlinesTest
    {
        /// <summary>The instant the rush bound opens entry into L1 (ActivationTime 7000 - 1500).</summary>
        private const double l1_entry_opens = 5500;

        /// <summary>The instant L0's last sung word ends: the caret must NOT be handed on here.</summary>
        private const double l0_word_ends = 2000;

        /// <summary>The instant L0's own deadline passes: the next line's first word. Not the hand-over.</summary>
        private const double l0_seals = 7000;

        /// <summary>
        /// The instant L0's drag cutoff lands on: the push warning's red bar reaching the end, the
        /// same moment a caret dragging on the line is force-sealed at with the setting off. This IS
        /// the hand-over.
        /// </summary>
        private const double l0_drag_cutoff = 8500;

        #region Fixture builders

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static LyricBeatmap threeLines() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = new[]
            {
                line("ab", 1000, l0_seals, 2000, unit("ab", 1000, 2000)),
                line("cd", l0_seals, 12000, 9000, unit("cd", 8000, 9000)),
                line("ef", 12000, 17000, 14000, unit("ef", 13000, 14000)),
            },
            Granularity = TimingGranularity.Line,
        };

        /// <summary>
        /// A live stack's era flags: the caret unpinned with the line-start snap and the rush bound,
        /// or the pinned game the Fletcher mod asks for. Everything the fixture does is inert under
        /// the pinned one, which is the point of its own test.
        /// </summary>
        private static TypingEngine started(bool manualNewlines, bool pinned = false)
        {
            var engine = new TypingEngine(threeLines())
            {
                ManualNewlines = manualNewlines,
                FletcherEnabled = !pinned,
                FlexibleLineSnap = !pinned,
                BoundedRush = true,
            };

            engine.Update(1000);
            Assert.AreEqual(0, engine.ActiveLineIndex);
            return engine;
        }

        /// <summary>The first line typed out dead on target, which finishes it at 1500.</summary>
        private static void typeLine0(TypingEngine engine)
        {
            engine.ProcessKey('a', 1000);
            engine.ProcessKey('b', 1500);
        }

        /// <summary>
        /// The same three lines' worth of setup with a WORD GAP in the first one: "ab cd" [1000, 4000]
        /// (a = 1000, b = 1500, ' ' = 2000, c = 2000, d = 2500) and one line after it to be handed on
        /// to. Only the space-skip fixture needs a gap, so it lives here rather than in the map above.
        /// </summary>
        private static LyricBeatmap gappedLines() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = new[]
            {
                line("ab cd", 1000, 4000, 3000, unit("ab", 1000, 2000), unit("cd", 2000, 3000)),
                line("ef", 4000, 8000, 6000, unit("ef", 5000, 6000)),
            },
            Granularity = TimingGranularity.Line,
        };

        #endregion

        [Test]
        public void TheSettingIsOffOnAFreshEngine() => Assert.IsFalse(new TypingEngine(threeLines()).ManualNewlines);

        #region the hand-over the setting takes away

        /// <summary>
        /// The default, so the setting can be told apart from it: a caret that finished its line early
        /// is carried onto the next line by the snap the instant that line's entry window opens.
        /// </summary>
        [Test]
        public void OffKeepsTheAutomaticHandOver()
        {
            var engine = started(manualNewlines: false);
            typeLine0(engine);

            Assert.AreEqual(0, engine.ActiveLineIndex, "parked: 1500 is well outside the entry window");
            Assert.IsTrue(engine.IsLineComplete);

            engine.Update(l1_entry_opens - 1);
            Assert.AreEqual(0, engine.ActiveLineIndex, "still parked one millisecond before the window opens");

            engine.Update(l1_entry_opens);
            Assert.AreEqual(1, engine.ActiveLineIndex, "the snap carried the finished caret the instant it could");
            Assert.AreEqual(0, engine.CaretIndex);
        }

        /// <summary>
        /// The same run with the setting on STOPS at the end of the line and WAITS: the finishing
        /// press does not roll, and the caret sits on the finished line - a letter there is inert -
        /// through every instant that could have moved it. The last sung word ending, the next line's
        /// entry window opening and the line's own deadline, which is the next line's first word, each
        /// leave it exactly where it is; only the drag cutoff takes it (see the next region).
        /// </summary>
        [Test]
        public void OnParksAFinishedLineUntilTheCutoff()
        {
            var engine = started(manualNewlines: true);
            typeLine0(engine);

            Assert.AreEqual(0, engine.ActiveLineIndex, "the press that finished the line did not roll it");
            Assert.IsTrue(engine.IsLineComplete);
            Assert.IsFalse(engine.AwaitingEntry, "a finished line the player is still standing on is not a wait");

            // A letter is inert on a finished line, under either arm.
            Assert.IsFalse(engine.ProcessKey('c', 1600));
            Assert.AreEqual(0, engine.ActiveLineIndex);

            // Each of the three instants in turn, and none of them moves the caret.
            engine.Update(l0_word_ends);
            Assert.AreEqual(0, engine.ActiveLineIndex, "the last sung word ending is not the hand-over");

            engine.Update(l1_entry_opens);
            Assert.AreEqual(0, engine.ActiveLineIndex, "the entry window opening does not snap a manual caret");

            engine.Update(l0_seals);
            Assert.AreEqual(0, engine.ActiveLineIndex, "and neither does the line's own deadline, the next line's first word");
            Assert.AreEqual(0, engine.NextUnsealedLineIndex, "the line is still the player's at that deadline");

            engine.Update(l0_drag_cutoff - 1);
            Assert.AreEqual(0, engine.ActiveLineIndex, "one millisecond before the cutoff it is still theirs");
        }

        #endregion

        #region the timing constraints that still apply

        /// <summary>
        /// THE CORE OF THE CHANGE: a newline pressed long before the next line's entry window opens
        /// LANDS, and the line it lands on waits - greyed, every key swallowed - until the window
        /// opens. The window used to refuse the press itself, which made a player press again at the
        /// right moment and read as the newline being broken.
        /// </summary>
        [Test]
        public void AnEarlyNewlineLandsAndTheNextLineWaits()
        {
            var engine = started(manualNewlines: true);
            typeLine0(engine);

            Assert.IsTrue(engine.ProcessKey(' ', 1600), "the press lands 3900 ms before the window opens");
            Assert.AreEqual(1, engine.ActiveLineIndex);
            Assert.AreEqual(0, engine.CaretIndex);

            engine.Update(1600);
            Assert.IsTrue(engine.AwaitingEntry, "the caret is on the line but may not type on it yet");

            // Nothing is judged on the waiting line: no correct char, no typo, no advance.
            Assert.IsFalse(engine.ProcessKey('c', 1600));
            Assert.IsFalse(engine.ProcessKey('x', 3000));
            Assert.AreEqual(0, engine.CaretIndex, "a swallowed key does not move the caret");
            engine.Update(3000);
            Assert.IsTrue(engine.AwaitingEntry);

            // The window opening is what releases it, to the millisecond.
            engine.Update(l1_entry_opens - 1);
            Assert.IsTrue(engine.AwaitingEntry);
            engine.Update(l1_entry_opens);
            Assert.IsFalse(engine.AwaitingEntry, "5500 is the instant entry opens");
            Assert.IsTrue(engine.ProcessKey('c', l1_entry_opens), "and the line types from there");
            Assert.AreEqual(1, engine.CaretIndex);
        }

        /// <summary>
        /// THE TYPED-THROUGH NEWLINE (see <c>TypingEngine.NewlineOnTypedLetter</c>): a letter at a
        /// finished caret hands the line on as well, and then lands on its first slot, right or wrong.
        /// The MOVE is ungated, exactly like the space's, because the window's business is what may be
        /// TYPED and not where the caret may stand: gating the press meant finishing a line early -
        /// the whole point - got nothing. So the caret moves either way, and the window then refuses
        /// the CHARACTER on a line that has not opened yet.
        /// </summary>
        [Test]
        public void ATypedLetterHandsTheLineOnOnceTheWindowOpens()
        {
            var engine = started(manualNewlines: true);
            engine.NewlineOnTypedLetter = true;
            typeLine0(engine);

            engine.Update(1600);
            Assert.IsTrue(engine.ProcessKey('c', 1600), "the letter hands the line on even 3900 ms before the window opens");
            Assert.AreEqual(1, engine.ActiveLineIndex, "the caret is on the next line");
            Assert.AreEqual(0, engine.CaretIndex, "but the CHARACTER was refused: nothing was typed");

            engine.Update(l1_entry_opens);
            Assert.IsTrue(engine.ProcessKey('c', l1_entry_opens), "and once the window opens the letter lands");
            Assert.AreEqual(1, engine.ActiveLineIndex);
            Assert.AreEqual(1, engine.CaretIndex, "having typed that line's first slot");
        }

        /// <summary>
        /// The era bit's own test: with it clear the same press is inert, which is every replay stored
        /// before the setting existed. It is a bit rather than an amendment to
        /// <c>ManualNewlines</c> precisely because it decides whether a keystroke is ACCEPTED.
        /// </summary>
        [Test]
        public void ATypedLetterIsInertWithoutItsEraBit()
        {
            var engine = started(manualNewlines: true);
            typeLine0(engine);

            engine.Update(l1_entry_opens);
            Assert.IsFalse(engine.ProcessKey('c', l1_entry_opens), "the old era keeps every letter inert at a finished caret");
            Assert.AreEqual(0, engine.ActiveLineIndex);
            Assert.AreEqual(2, engine.CaretIndex);
        }

        /// <summary>
        /// The wait is undone from the head of the waiting line: a backspace steps back up to the
        /// line it came from, WHILE that line can still be typed - its sweep has not reached its own
        /// end - and the player is free to retype it with a second backspace.
        /// </summary>
        [Test]
        public void BackspaceStepsBackUpWhileTheOldLineIsStillOpen()
        {
            var engine = started(manualNewlines: true);
            typeLine0(engine);

            Assert.IsTrue(engine.ProcessKey(' ', 1600));
            Assert.AreEqual(1, engine.ActiveLineIndex);

            engine.Update(1600);
            Assert.IsTrue(engine.ProcessBackspace(), "1600 is long before the cutoff, so the step back is allowed");
            Assert.AreEqual(0, engine.ActiveLineIndex);
            Assert.AreEqual(2, engine.CaretIndex, "the caret returns to the frontier of what was typed: the end, for a line typed out in full");

            // And the line it returns to is ordinary again: a further backspace takes the last char.
            Assert.IsTrue(engine.ProcessBackspace());
            Assert.AreEqual(1, engine.CaretIndex);
            Assert.IsFalse(engine.IsLineComplete);
        }

        /// <summary>Enter is the other newline key, and behaves identically - early included.</summary>
        [Test]
        public void EnterClosesAFinishedLine()
        {
            var early = started(manualNewlines: true);
            typeLine0(early);

            Assert.IsTrue(early.ProcessEnter(1600), "Enter lands early, exactly as space does");
            Assert.AreEqual(1, early.ActiveLineIndex);
            Assert.IsTrue(early.AwaitingEntry);

            var inWindow = started(manualNewlines: true);
            typeLine0(inWindow);

            Assert.IsTrue(inWindow.ProcessEnter(l1_entry_opens));
            Assert.AreEqual(1, inWindow.ActiveLineIndex);

            inWindow.Update(l1_entry_opens);
            Assert.IsFalse(inWindow.AwaitingEntry, "inside the window there is nothing to wait for");
        }

        /// <summary>
        /// Enter pressed MID-LINE is still the line skip it has always been: the rest of the line is
        /// given up, and the same press is ALSO the newline under the setting. It lands whenever it
        /// is made - the line it lands on waits for its window if it has to - so one gesture never
        /// needs a second press.
        /// </summary>
        [Test]
        public void EnterMidLineSkipsAndStillMovesOnInOnePress()
        {
            var early = started(manualNewlines: true);
            Assert.IsTrue(early.ProcessEnter(100), "the rest of the line is given up, and the press is the newline");
            Assert.AreEqual(1, early.ActiveLineIndex, "and the same press hands the caret on");
            Assert.IsTrue(early.AwaitingEntry, "where it waits, because 100 is long before the window");

            var inWindow = started(manualNewlines: true);
            inWindow.ProcessKey('a', 1000);
            Assert.IsTrue(inWindow.ProcessEnter(l1_entry_opens), "a skip inside the window lands on the next line at once");
            Assert.AreEqual(1, inWindow.ActiveLineIndex);
            Assert.AreEqual(0, inWindow.CaretIndex);
        }

        #endregion

        #region the song hands the caret on at the push cutoff, and not before it

        /// <summary>
        /// The player who never presses is not stranded, and is not left standing on a line they may
        /// not type on either: the song hands a finished caret on at the DRAG CUTOFF - the instant the
        /// push warning's red bar completes, and the one a caret still dragging on the line is
        /// force-sealed at with the setting off. Not when the last word finishes, and not when the
        /// line's own deadline passes, which is the next line's first word.
        /// </summary>
        [Test]
        public void TheSongHandsTheCaretOnAtThePushCutoff()
        {
            var engine = started(manualNewlines: true);
            typeLine0(engine);

            engine.Update(l0_seals + 1);
            Assert.AreEqual(0, engine.ActiveLineIndex, "the line's deadline, and the next line's first word, are not it");

            engine.Update(l0_drag_cutoff - 1);
            Assert.AreEqual(0, engine.ActiveLineIndex, "the song has not taken the line yet");

            engine.Update(l0_drag_cutoff);
            Assert.AreEqual(1, engine.ActiveLineIndex, "the cutoff is what hands it on");
            Assert.AreEqual(0, engine.CaretIndex);
            Assert.IsFalse(engine.AwaitingEntry, "the window opened long ago, so the line types at once");
            Assert.IsTrue(engine.ProcessKey('c', l0_drag_cutoff), "and it is an ordinary line from there");
            Assert.AreEqual(1, engine.CaretIndex);

            // The hand-over and the seal are the SAME instant for a finished line: the line the caret
            // left is taken as the caret is moved off it, not seconds earlier at its deadline.
            Assert.AreEqual(1, engine.NextUnsealedLineIndex, "L0 sealed at the cutoff, not at its deadline");
        }

        /// <summary>
        /// The player watching for the instant has something to watch: the push warning's readout
        /// names the cutoff while a manual caret is parked on a line it has finished, which is what
        /// draws the red bar, and it keeps naming it as the playhead passes the line's own deadline.
        /// </summary>
        [Test]
        public void ThePushWarningCountsDownToTheManualCutoff()
        {
            var engine = started(manualNewlines: true);
            typeLine0(engine);

            Assert.AreEqual(l0_drag_cutoff, engine.DragCutoffAt, "a parked manual caret is warned about the instant it is taken");

            engine.Update(l0_seals);
            Assert.AreEqual(l0_drag_cutoff, engine.DragCutoffAt, "unchanged as the playhead passes the line's own deadline");

            engine.Update(l0_drag_cutoff);
            Assert.AreEqual(1, engine.ActiveLineIndex);
            Assert.AreEqual(13500, engine.DragCutoffAt, "and the line it is handed on to has a warning of its own");
        }

        /// <summary>
        /// ONCE THE SONG HAS HANDED THE CARET ON, THE STEP BACK IS CLOSED: the line it came from can
        /// no longer be typed, so a backspace from the head of the new line is inert. That is the same
        /// cutoff the hand-over happened at, which is what keeps the two from disagreeing - and it is
        /// LATER than the line's deadline, so the undo survives the line running out of song.
        /// </summary>
        [Test]
        public void TheStepBackClosesWithTheLine()
        {
            var engine = started(manualNewlines: true);
            typeLine0(engine);

            Assert.IsTrue(engine.ProcessKey(' ', 1600), "the player presses their own newline");
            Assert.AreEqual(1, engine.ActiveLineIndex);

            engine.Update(l0_seals + 1);
            Assert.IsTrue(engine.ProcessBackspace(), "the line has passed its deadline but it is still the player's");
            Assert.AreEqual(0, engine.ActiveLineIndex, "the step back still works from the head of the new line");

            // And it is the same instant for the caret the player never moved: the cutoff takes the
            // line and hands the caret on, and the step back goes with it.
            engine.Update(l0_drag_cutoff);
            Assert.AreEqual(1, engine.ActiveLineIndex);
            Assert.IsFalse(engine.ProcessBackspace(), "L0 is sealed, so there is nothing to go back to");
            Assert.AreEqual(1, engine.ActiveLineIndex);
            Assert.AreEqual(0, engine.CaretIndex);
        }

        #endregion

        #region stepping back into a line the player gave up

        /// <summary>
        /// THE UNDO FOR A MID-LINE ENTER. The step back lands on the LAST CHARACTER THE PLAYER
        /// ACTUALLY TYPED rather than at the end of the line, so the characters that Enter gave up are
        /// in front of the caret again - untyped, typable, and nothing the skip took still owed.
        /// Landing at the end instead reads as a COMPLETE line, where keypresses are inert: the
        /// characters the player came back for would be unreachable and would be missed at the seal,
        /// which is the opposite of what coming back is for.
        /// </summary>
        [Test]
        public void BackspaceIntoASkippedLineLandsOnTheLastTypedCharacter()
        {
            var engine = started(manualNewlines: true);

            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.IsTrue(engine.ProcessEnter(1100), "the rest of the line is given up, and the press is the newline");
            Assert.AreEqual(1, engine.ActiveLineIndex);

            Assert.IsTrue(engine.ProcessBackspace(), "and the step back undoes it");
            Assert.AreEqual(0, engine.ActiveLineIndex);
            Assert.AreEqual(1, engine.CaretIndex, "the last character actually typed, not the end of the line");
            Assert.IsFalse(engine.IsLineComplete, "so the character it gave up is in front of the caret again");

            Assert.IsTrue(engine.ProcessKey('b', 1500), "and is typable rather than stranded");
            Assert.IsTrue(engine.IsLineComplete);

            engine.Update(l0_drag_cutoff);
            Assert.AreEqual(1, engine.ActiveLineIndex);
            Assert.AreEqual(0, engine.BuildResults().Counts[JudgementType.Miss], "and nothing the skip gave up ended up missed");
        }

        /// <summary>
        /// A WORD GIVEN UP BEFORE THE ENTER IS IN THAT TAIL TOO. A space skip leaves its cells PHANTOM
        /// rather than untyped (backlog 167), and it has already drained health for them by the time
        /// the player steps back, so the step back re-opens them exactly as the backspace's own walk
        /// re-opens one: live again, drain refunded, with the combo still owed at the retype.
        /// </summary>
        [Test]
        public void TheStepBackReopensAWordTheSkipLeftPhantom()
        {
            var engine = new TypingEngine(gappedLines())
            {
                ManualNewlines = true,
                FletcherEnabled = true,
                FlexibleLineSnap = true,
                BoundedRush = true,
                SpaceSkipsWord = true,
            };

            int refunded = 0;
            engine.AbandonReclaimed += _ => refunded++;

            engine.Update(1000);
            engine.ProcessKey('a', 1000);
            engine.ProcessKey('b', 1500);
            engine.ProcessKey(' ', 2000);

            Assert.IsTrue(engine.ProcessKey(' ', 2200), "the space inside the last word gives the rest of it up");
            Assert.AreEqual(CellState.Abandoned, engine.Lines[0].Cells[3].State, "phantom, not missed: nothing is judged at the skip");

            Assert.IsTrue(engine.ProcessEnter(2300), "and Enter is still the newline");
            Assert.AreEqual(1, engine.ActiveLineIndex);

            Assert.IsTrue(engine.ProcessBackspace(), "the step back");
            Assert.AreEqual(0, engine.ActiveLineIndex);
            Assert.AreEqual(3, engine.CaretIndex, "at the word it gave up, not at the end of the line");
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[3].State, "the phantom cells are live again");
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[4].State);
            Assert.AreEqual(1, refunded, "and the drain the skip took is given back");
            Assert.IsTrue(engine.ProcessKey('c', 2400), "so the word can be typed after all");
        }

        #endregion

        #region a pinned caret ignores it

        /// <summary>
        /// Under the Fletcher mod the caret is pinned to the song, so there is no roll for the setting
        /// to take away and no newline press to honour: the pinned game's own hand-over, the seal and
        /// the next line's activation, still moves the player on.
        /// </summary>
        [Test]
        public void APinnedCaretIgnoresTheSetting()
        {
            var engine = started(manualNewlines: true, pinned: true);
            typeLine0(engine);

            Assert.AreEqual(0, engine.ActiveLineIndex);
            Assert.IsFalse(engine.ProcessKey(' ', l1_entry_opens), "a pinned caret is not the player's to move");
            Assert.IsFalse(engine.AwaitingEntry, "and it never waits: the song is always on its line");

            engine.Update(6000);
            Assert.AreEqual(0, engine.ActiveLineIndex, "and it does not rush onto the next line either");

            engine.Update(l0_seals + 1);
            Assert.AreEqual(1, engine.ActiveLineIndex, "the song moved the pinned caret on at L0's seal");
        }

        #endregion

        #region a newline onto a line with nothing to type

        /// <summary>
        /// FletcherEngineTest's parked-line shape: L0 "ab" [1000, 3000) (a = 1000, b = 1500), L1 "..."
        /// [3000, 20000), pure punctuation the default stream strips, so it has NO typeable cell, and
        /// L2 "cd" [10000, 30000) (c = 12000, d = 12500). L1 activates at its StartTime of 3000, so
        /// entry into it opens at 1500; L2 activates at 10500 (12000 - CUE_LEAD_MS), so entry into it
        /// opens at 9000. A newline pressed at 2000 therefore lands on L1 with nothing to wait for,
        /// and the next one lands on L2 long before its window.
        /// </summary>
        private static LyricBeatmap cellLessMiddleLine() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = new[]
            {
                line("ab", 1000, 3000, 2000, unit("ab", 1000, 2000)),
                line("...", 3000, 20000, 19000, unit("...", 3000, 19000)),
                line("cd", 10000, 30000, 13000, unit("cd", 12000, 13000)),
            },
            Granularity = TimingGranularity.Line,
        };

        /// <summary>What the engine reported after L0 was typed out: every judgement and every typo.</summary>
        private sealed class Reports
        {
            public readonly List<CharJudgement> Judged = new List<CharJudgement>();
            public int Mistyped;
        }

        /// <summary>The live manual arm on <see cref="cellLessMiddleLine"/>, with L0 typed out and parked.</summary>
        private static TypingEngine parkedBeforeTheCellLessLine(out Reports reports)
        {
            var engine = new TypingEngine(cellLessMiddleLine())
            {
                ManualNewlines = true,
                NewlineOnTypedLetter = true,
                FletcherEnabled = true,
                FlexibleLineSnap = true,
                BoundedRush = true,
            };

            Assert.IsFalse(engine.Lines[1].Cells.Any(c => c.IsTypeable), "the fixture's middle line must have nothing to type");

            engine.Update(1000);
            Assert.IsTrue(engine.ProcessKey('a', 1000));
            engine.Update(1500);
            Assert.IsTrue(engine.ProcessKey('b', 1500));
            engine.Update(2000);
            Assert.AreEqual(0, engine.ActiveLineIndex, "parked on the finished line, as the manual arm leaves it");

            var r = new Reports();
            engine.CharJudged += r.Judged.Add;
            engine.Mistyped += () => r.Mistyped++;
            reports = r;
            return engine;
        }

        /// <summary>
        /// THE TYPED-LETTER NEWLINE ONTO A CELL-LESS LINE (backlog 326). The letter hands the caret
        /// on, and the line it lands on has no slot for it: the move is kept and the letter goes
        /// nowhere, exactly what the awaiting branch does with a letter it cannot type yet, and what
        /// typebeat-core.js does. It used to read <c>Cells[caretIndex]</c> past the end and throw.
        /// Nothing is judged, the run is untouched, and the engine carries on: the next letter is a
        /// newline again, onto L2, where it waits for the window and then types.
        /// </summary>
        [Test]
        public void ATypedLetterOntoACellLessLineMovesAndDropsTheLetter()
        {
            var engine = parkedBeforeTheCellLessLine(out var reports);
            int comboBefore = engine.Combo;

            bool moved = false;
            Assert.DoesNotThrow(() => moved = engine.ProcessKey('c', 2000));
            Assert.IsTrue(moved, "the move is reported, so the caller records the frame");
            Assert.AreEqual(1, engine.ActiveLineIndex, "the caret is on the cell-less line");
            Assert.AreEqual(0, engine.CaretIndex);
            Assert.IsFalse(engine.AwaitingEntry, "2000 is inside L1's entry window, so this is not a wait");
            Assert.IsTrue(engine.IsLineComplete, "and a line with nothing to type is complete the moment it is landed on");
            Assert.IsEmpty(reports.Judged, "the letter judged nothing");
            Assert.AreEqual(0, reports.Mistyped, "and was not a typo either");
            Assert.AreEqual(0, engine.Mistypes);
            Assert.AreEqual(comboBefore, engine.Combo, "the run is untouched");

            // The engine keeps running: the next letter is the newline off the cell-less line, onto L2
            // 7000 ms before its window, where the character is refused as on any waiting line.
            Assert.IsTrue(engine.ProcessKey('c', 2100));
            Assert.AreEqual(2, engine.ActiveLineIndex);
            Assert.AreEqual(0, engine.CaretIndex);
            engine.Update(2100);
            Assert.IsTrue(engine.AwaitingEntry);
            Assert.IsEmpty(reports.Judged);

            engine.Update(9000);
            Assert.IsFalse(engine.AwaitingEntry, "9000 is the instant entry into L2 opens");
            Assert.IsTrue(engine.ProcessKey('c', 9000), "and the line types from there");
            Assert.AreEqual(1, engine.CaretIndex);
            Assert.AreEqual(1, reports.Judged.Count, "the first judgement since the cell-less landing is L2's own first cell");
            Assert.AreEqual(0, reports.Mistyped);
        }

        /// <summary>
        /// The same line through the other two newline keys, pinned so all three agree. Neither needed
        /// a guard: a space at a finished caret returns straight out of <c>rollForwardManually</c>, and
        /// Enter measures the frontier after the auto-skip and closes a complete line the same way, so
        /// each lands on the cell-less line, judges nothing, and hands it on to L2 when pressed again.
        /// </summary>
        [TestCase(' ')]
        [TestCase('\n')]
        public void SpaceAndEnterHandACellLessLineOnWithoutJudging(char key)
        {
            var engine = parkedBeforeTheCellLessLine(out var reports);

            bool press(double time) => key == '\n' ? engine.ProcessEnter(time) : engine.ProcessKey(key, time);

            Assert.IsTrue(press(2000));
            Assert.AreEqual(1, engine.ActiveLineIndex, "onto the cell-less line");
            Assert.AreEqual(0, engine.CaretIndex);

            Assert.IsTrue(press(2100));
            Assert.AreEqual(2, engine.ActiveLineIndex, "and off it again");
            Assert.AreEqual(0, engine.CaretIndex);

            Assert.IsEmpty(reports.Judged);
            Assert.AreEqual(0, reports.Mistyped);
        }

        #endregion

        #region the flag reaches a re-derived run

        /// <summary>
        /// The CONFIG frame's bit 14 puts the setting on the engine before any keystroke is fed, which
        /// is what makes a stored run re-derive the caret its player actually had.
        /// </summary>
        [Test]
        public void TheFrameBitSetsTheEngineFlag()
        {
            var engine = new TypingEngine(threeLines());
            Assert.IsFalse(engine.ManualNewlines);

            ReplayEngineFeed.Apply(engine, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, manualNewlines: true));
            Assert.IsTrue(engine.ManualNewlines);

            ReplayEngineFeed.Apply(engine, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true));
            Assert.IsFalse(engine.ManualNewlines, "and a run without the bit puts it back off");
        }

        #endregion
    }
}
