// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 181: a wrong letter pressed on the WORD GAP is typed through, exactly as one pressed on a
// lyric character is. Before this the gap was the one typeable cell in the map that could only ever
// reject a wrong key, which made the caret stop dead between two words for no reason a typist can
// see; now the gap takes the typo character, shows it in the error red (a red SPACE being nothing at
// all, the glyph is the one the player pressed), the caret advances, backspace erases it and the
// corrected press earns the cell's real judgement plus any streak the typo broke.
//
// Two things are deliberately NOT widened. The space KEY stays strict on every cell under every arm:
// it is the word-advance key, not a glyph anyone means to leave in a lyric. And the whole rule is an
// ERA (TypingEngine.WrongInputOnWordGaps, CONFIG frame flags bit 3, default FALSE), because every
// replay on disk holds wrong-key-on-gap frames that were REJECTED when the run was played: type one
// of those through on re-derivation and the caret lands a cell further on than it did live, which
// desynchronises every keystroke after it. The era region at the bottom is that guarantee.

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
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class SpaceTypoTest
    {
        #region Fixture

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine abCdLine(double end) => new LyricLine
        {
            RawText = "ab cd",
            StartTime = 1000,
            EndTime = end,
            SingEndTime = 3000,
            Units = new[] { unit("ab", 1000, 2000), unit("cd", 2000, 3000) },
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

        /// <summary>
        /// "ab cd": cells a = 1000, b = 1500, ' ' = 2000 (unit 0's end), c = 2000, d = 2500, the same
        /// shape <c>UntimedSpaceTest</c> works on. The line runs to 60000 so nothing seals while a
        /// press is being made absurdly late on purpose. Line-granularity windows: Great [-250, 400],
        /// Ok [-600, 1000], Meh [-1200, 2000]. Cell 2 is THE word gap every test here aims at.
        /// </summary>
        private static LyricBeatmap abCd() => map(abCdLine(60000));

        /// <summary>The same cells with a real deadline (4000), so the line seals.</summary>
        private static LyricBeatmap sealingAbCd() => map(abCdLine(4000));

        /// <summary>The same line as a playable beatmap, with the nested per-cell objects the score
        /// processor's maximum statistics come from.</summary>
        private static TypeBeatBeatmap playableAbCd()
        {
            var beatmap = new TypeBeatBeatmap();
            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = 1000, LineIndex = 0, Line = abCdLine(4000), Granularity = TimingGranularity.Line });

            foreach (var hitObject in beatmap.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return beatmap;
        }

        /// <summary>An engine on the given map with the line already active, and the backlog 181
        /// input model selected explicitly (never defaulted: the default IS the other era).</summary>
        private static TypingEngine started(LyricBeatmap beatmap, bool gapTypos)
        {
            var engine = new TypingEngine(beatmap) { WrongInputOnWordGaps = gapTypos };
            engine.Update(1000);
            Assert.That(engine.ActiveLineIndex, Is.Zero);
            return engine;
        }

        /// <summary>Every judgement the engine raises, in order.</summary>
        private static List<CharJudgement> record(TypingEngine engine)
        {
            var judged = new List<CharJudgement>();
            engine.CharJudged += j => judged.Add(j);
            return judged;
        }

        /// <summary>Type "ab" cleanly, which puts the caret on the word gap with a streak of 2 behind it.</summary>
        private static void typeAb(TypingEngine engine)
        {
            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('b', 1500), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(2));
            Assert.That(engine.Combo, Is.EqualTo(2));
        }

        private static IReadOnlyList<TypingCell> cells(TypingEngine engine) => engine.Lines[0].Cells;

        #endregion

        /// <summary>
        /// The fixture's own shape, asserted rather than trusted: everything below indexes cell 2 as
        /// the word gap and prices presses against these targets.
        /// </summary>
        [Test]
        public void TheFixtureIsTwoWordsAroundOneGap()
        {
            var c = cells(started(abCd(), gapTypos: true));

            Assert.Multiple(() =>
            {
                Assert.That(c.Select(x => x.Expected), Is.EqualTo(new[] { 'a', 'b', ' ', 'c', 'd' }));
                Assert.That(c.Select(x => x.TargetTime), Is.EqualTo(new[] { 1000d, 1500, 2000, 2000, 2500 }));
                Assert.That(c[2].IsTypeable, Is.True, "the gap is a typeable cell");
                Assert.That(c[2].IsCountable, Is.False, "and an uncountable one");
            });
        }

        // -----------------------------------------------------------------------------------------
        // The rule itself
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The headline. A wrong letter on the gap lands IN the gap, in every particular a wrong
        /// letter on a lyric cell lands: the cell holds it, the caret moves on, the streak is gone,
        /// the mistype is counted, and the mash-fail streak (a Gatekeeper-only guard) is untouched.
        /// </summary>
        [Test]
        public void AWrongLetterOnTheWordGapIsTypedThrough()
        {
            var engine = started(abCd(), gapTypos: true);
            var judged = record(engine);
            typeAb(engine);

            Assert.That(engine.ProcessKey('x', 2000), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Wrong));
                Assert.That(cells(engine)[2].TypedChar, Is.EqualTo('x'));
                Assert.That(engine.CaretIndex, Is.EqualTo(3), "the caret advanced past the gap it spoiled");
                Assert.That(engine.Combo, Is.Zero);
                Assert.That(engine.Mistypes, Is.EqualTo(1));
                Assert.That(engine.ConsecutiveWrongKeys, Is.Zero, "type-through never feeds the mash-fail streak");
                Assert.That(judged[^1].Type, Is.EqualTo(JudgementType.WrongChar));
                Assert.That(judged[^1].CellIndex, Is.EqualTo(2));
                Assert.That(judged[^1].PointsAwarded, Is.Zero);
            });
        }

        /// <summary>
        /// The other era, which is what the same keystroke did before backlog 181 and what every
        /// stored replay still has to do: rejected outright. Nothing is written, the caret does not
        /// move, and the mash-fail streak accrues, because that guard exists to police exactly this
        /// branch.
        /// </summary>
        [Test]
        public void TheClassicEraStillRejectsIt()
        {
            var engine = started(abCd(), gapTypos: false);
            var judged = record(engine);

            char? rejected = null;
            engine.WrongKeyRejected += c => rejected = c;

            typeAb(engine);

            Assert.That(engine.ProcessKey('x', 2000), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Untyped));
                Assert.That(cells(engine)[2].TypedChar, Is.Null);
                Assert.That(engine.CaretIndex, Is.EqualTo(2), "the caret is still stuck on the gap");
                Assert.That(engine.ConsecutiveWrongKeys, Is.EqualTo(1));
                Assert.That(rejected, Is.EqualTo('x'));
                Assert.That(judged.Count(j => j.CellIndex == 2), Is.Zero, "a rejection judges no cell");
            });
        }

        /// <summary>
        /// The gap is the only thing that moved. A wrong letter on a LYRIC cell was already typed
        /// through and still is, identically, under both arms of the new flag: this pins that the
        /// widening did not accidentally become a narrowing anywhere.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void AWrongLetterOnALyricCellIsUnaffected(bool gapTypos)
        {
            var engine = started(abCd(), gapTypos);

            Assert.That(engine.ProcessKey('z', 1000), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[0].State, Is.EqualTo(CellState.Wrong));
                Assert.That(cells(engine)[0].TypedChar, Is.EqualTo('z'));
                Assert.That(engine.CaretIndex, Is.EqualTo(1));
                Assert.That(engine.ConsecutiveWrongKeys, Is.Zero);
            });
        }

        /// <summary>
        /// The space KEY stays strict everywhere, which is the half of the old rule backlog 181
        /// keeps: there is no cell a wrong space is typed into. A space on a LYRIC character is
        /// rejected exactly as it always was (mistype, combo break, mash streak), and a space on the
        /// GAP is not a wrong key at all, it is the correct one, so the branch simply has no third
        /// case to widen.
        /// </summary>
        [Test]
        public void TheSpaceKeyIsStillRejectedOnALyricCell()
        {
            var engine = started(abCd(), gapTypos: true);

            Assert.That(engine.ProcessKey(' ', 1000), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[0].State, Is.EqualTo(CellState.Untyped));
                Assert.That(engine.CaretIndex, Is.Zero);
                Assert.That(engine.ConsecutiveWrongKeys, Is.EqualTo(1), "a wrong SPACE is still a gatekeeper rejection");
                Assert.That(engine.Mistypes, Is.EqualTo(1));
            });
        }

        // -----------------------------------------------------------------------------------------
        // Accounting: identical to a lyric typo, cell for cell
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The keypress costs the accuracy denominator and an error in BOTH eras, which is why
        /// backlog 181 moves no accuracy at all at the moment of the mistake: the strict rejection
        /// already charged for it. The two arms are asserted equal rather than each asserted against
        /// a number, because "this changes nothing about accuracy" IS that equality.
        /// </summary>
        [Test]
        public void TheKeypressCostsTheSameAccuracyInBothEras()
        {
            var through = started(abCd(), gapTypos: true);
            var strict = started(abCd(), gapTypos: false);

            foreach (var engine in new[] { through, strict })
            {
                typeAb(engine);
                Assert.That(engine.ProcessKey('x', 2000), Is.True);
            }

            Assert.Multiple(() =>
            {
                Assert.That(through.LiveAccuracy, Is.EqualTo(strict.LiveAccuracy).Within(1e-12));
                Assert.That(through.LiveAccuracy, Is.EqualTo(2 / 3.0).Within(1e-12));
                Assert.That(through.Mistypes, Is.EqualTo(strict.Mistypes));
                Assert.That(through.Combo, Is.EqualTo(strict.Combo));
            });
        }

        /// <summary>
        /// The CharJudgement a gap typo carries is the delta a CORRECT press on that cell would have
        /// carried, which is <c>SyllableTimingTest.WrongCharCarriesTheSpanDelta</c>'s rule applied to
        /// the one cell type with an accounting of its own. For an UNTIMED space (backlog 148, the
        /// live rule) that is 0 however late the press was; under the stored
        /// <see cref="SpaceTimingRule.Timed"/> era it is the real point delta, because that is what a
        /// correct press there would have been priced on.
        /// </summary>
        [Test]
        public void TheGapTypoCarriesTheDeltaACorrectPressWouldHave()
        {
            var untimed = started(abCd(), gapTypos: true);
            var judgedUntimed = record(untimed);
            typeAb(untimed);
            Assert.That(untimed.ProcessKey('x', 3500), Is.True); // 1500 past the gap's 2000 target

            var timed = started(abCd(), gapTypos: true);
            timed.SpaceTiming = SpaceTimingRule.Timed;
            var judgedTimed = record(timed);
            typeAb(timed);
            Assert.That(timed.ProcessKey('x', 3500), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(judgedUntimed[^1].Delta, Is.EqualTo(0).Within(1e-9), "the spacebar is outside the timing challenge");
                Assert.That(judgedTimed[^1].Delta, Is.EqualTo(1500).Within(1e-9), "and inside it under the pre-148 era");
            });
        }

        /// <summary>
        /// The sync readouts do not see a gap typo at all, because they do not see the gap: an
        /// untimed space is out of both halves of the mean (<c>TypingEngine.isTimed</c>). Four
        /// dead-on lyric characters with an unfixed typo sitting between them is still a perfect
        /// sync percent, which is the correct reading: the player's timing was perfect, their
        /// spelling was not, and those are different numbers.
        /// </summary>
        [Test]
        public void AGapTypoLeavesTheSyncReadoutsAlone()
        {
            var engine = started(sealingAbCd(), gapTypos: true);

            typeAb(engine);
            Assert.That(engine.ProcessKey('x', 2000), Is.True);
            Assert.That(engine.ProcessKey('c', 2000), Is.True);
            Assert.That(engine.ProcessKey('d', 2500), Is.True);

            Assert.That(engine.LiveSyncPercent, Is.EqualTo(100).Within(1e-9));

            engine.Update(10000);

            Assert.That(engine.BuildResults().SyncPercent, Is.EqualTo(100).Within(1e-9));
        }

        /// <summary>
        /// Fletcher's currency is COUNTABLE characters and a space is not one, so a typo on the gap
        /// spends nothing from the rush budget: the caret's countable position is the same on both
        /// sides of the press. (It is the same property that keeps a correctly typed space free, and
        /// the typo must not be the one way a gap starts costing budget.)
        /// </summary>
        [Test]
        public void AGapTypoSpendsNoneOfFletchersBudget()
        {
            var engine = started(abCd(), gapTypos: true);
            engine.FletcherEnabled = true;

            typeAb(engine);
            int before = engine.CaretCountablePosition;

            Assert.That(engine.ProcessKey('x', 2000), Is.True);

            Assert.That(engine.CaretCountablePosition, Is.EqualTo(before), "the gap is uncountable however it is resolved");
        }

        // -----------------------------------------------------------------------------------------
        // Backspace, correction, and the seal
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The full fix cycle, which is the whole point of typing a typo through rather than
        /// refusing it: backspace takes the character back (and announces the erase, which is what
        /// refunds the health the keypress drained), and the corrected space earns the cell's real
        /// judgement PLUS the streak the typo broke.
        /// </summary>
        [Test]
        public void BackspaceErasesAGapTypoAndTheCorrectionRestoresTheStreak()
        {
            var engine = started(abCd(), gapTypos: true);

            int erased = 0;
            int? restored = null;
            engine.TypoErased += () => erased++;
            engine.ComboRestored += amount => restored = amount;

            typeAb(engine);
            Assert.That(engine.ProcessKey('x', 2000), Is.True);

            Assert.That(engine.ProcessBackspace(), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Untyped));
                Assert.That(cells(engine)[2].TypedChar, Is.Null);
                Assert.That(engine.CaretIndex, Is.EqualTo(2));
                Assert.That(erased, Is.EqualTo(1), "erasing a WRONG space is a typo erase like any other");
            });

            Assert.That(engine.ProcessKey(' ', 2000), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.EqualTo(2), "the streak the typo broke comes back at the fix");
                Assert.That(engine.Combo, Is.EqualTo(3));
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Correct));
                Assert.That(cells(engine)[2].JudgedDelta, Is.EqualTo(0));
            });
        }

        /// <summary>
        /// An unfixed gap typo is a HIT and not a miss (backlog 124's rule, reaching a cell state
        /// that could not exist before backlog 181): the seal leaves the cell WRONG rather than
        /// turning it into a Missed one, takes no engine miss for it, and does not break the combo a
        /// second time. <see cref="TypingEngine.CellLeftWrong"/> is what the drawable and headless
        /// seal paths both ask, so this is the pin that they will resolve it as an unfixed typo.
        /// </summary>
        [Test]
        public void AnUnfixedGapTypoSealsAsAHitNotAMiss()
        {
            var engine = started(sealingAbCd(), gapTypos: true);

            typeAb(engine);
            Assert.That(engine.ProcessKey('x', 2000), Is.True);
            Assert.That(engine.ProcessKey('c', 2000), Is.True);
            Assert.That(engine.ProcessKey('d', 2500), Is.True);

            int combo = engine.Combo;

            engine.Update(10000);

            var results = engine.BuildResults();

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Wrong), "the seal must leave a wrong SPACE wrong");
                Assert.That(cells(engine)[2].TypedChar, Is.EqualTo('x'), "and still show which character went in");
                Assert.That(engine.CellLeftWrong(0, 2), Is.True);
                Assert.That(results.Counts[JudgementType.Miss], Is.Zero);
                Assert.That(engine.Combo, Is.EqualTo(combo), "the break was taken at the keypress, not here");
            });
        }

        /// <summary>
        /// The same run through the real score processor: the gap resolves as
        /// <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>, not a Miss, and it costs COMPLETION
        /// exactly as a lyric typo does (four of five cells typed). This is the seam a Wrong SPACE
        /// cell had never reached before, so it is asserted end to end rather than at the engine.
        /// </summary>
        [Test]
        public void TheHeadlessSealResolvesAGapTypoAsAnUnfixedTypo()
        {
            var account = scoreGapRun(bit_wrong_input | bit_wrong_input_on_word_gaps);

            Assert.Multiple(() =>
            {
                Assert.That(account.Statistics.GetValueOrDefault(TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(1));
                Assert.That(account.Statistics.GetValueOrDefault(HitResult.Miss), Is.Zero);
                Assert.That(account.Completion, Is.EqualTo(4 / 5.0).Within(1e-9), "an unfixed typo is not a typed cell");
            });
        }

        // -----------------------------------------------------------------------------------------
        // Interplays that must not break
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Under Mashing no wrong key ever REACHES the gap: the press is rewritten to the caret
        /// cell's expected character before the match, which on a gap is the space it stood for. The
        /// new branch is therefore unreachable under the mod, and the gap is typed correctly.
        /// </summary>
        [Test]
        public void MashingNeverSendsAWrongKeyToTheGap()
        {
            var engine = started(abCd(), gapTypos: true);
            engine.MashingEnabled = true;

            typeAb(engine);
            Assert.That(engine.ProcessKey('x', 2000), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Correct));
                Assert.That(cells(engine)[2].TypedChar, Is.EqualTo(' '));
                Assert.That(engine.Mistypes, Is.Zero);
            });
        }

        /// <summary>
        /// SpaceSkipsWord is orthogonal to backlog 181, and both halves are pinned here because the
        /// two rules meet on the same cell. A SPACE inside a word still abandons it (the skip is keyed
        /// on the KEY being a space, which a typo never is), and a wrong LETTER on the gap still types
        /// through with the setting on.
        ///
        /// <para>On the CLASSIC space era (<see cref="TypingEngine.StrictSpaces"/> false, the default
        /// this fixture builds), where the gap typo also ADVANCES the caret. Backlog 184 parks it
        /// instead when both flags are on, which is what stops the next space feeding a spoiled gap to
        /// the skip gate; see <c>SpaceDisciplineTest</c> for that arm.</para>
        /// </summary>
        [Test]
        public void SpaceSkipsWordIsUnaffected()
        {
            var skipping = started(abCd(), gapTypos: true);
            skipping.SpaceSkipsWord = true;

            Assert.That(skipping.ProcessKey('a', 1000), Is.True);
            Assert.That(skipping.ProcessKey(' ', 1200), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(skipping)[1].State, Is.EqualTo(CellState.Abandoned), "the space still skips the word");
                Assert.That(cells(skipping)[2].State, Is.EqualTo(CellState.Correct), "and lands on the gap as a typed space");
            });

            var typo = started(abCd(), gapTypos: true);
            typo.SpaceSkipsWord = true;
            typeAb(typo);

            Assert.That(typo.ProcessKey('x', 2000), Is.True);

            Assert.That(cells(typo)[2].State, Is.EqualTo(CellState.Wrong), "a wrong LETTER on the gap is not a skip");
            Assert.That(typo.CaretIndex, Is.EqualTo(3), "and on the classic space era it carries the caret past the gap");
        }

        /// <summary>
        /// Gatekeeper still refuses everything. The new flag is an extension of
        /// <see cref="TypingEngine.AllowWrongInput"/> and not a competitor to it, so with wrong input
        /// off the gap rejects a typo whichever way the era flag points.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void GatekeeperRejectsAGapTypoUnderBothEras(bool gapTypos)
        {
            var engine = started(abCd(), gapTypos);
            engine.AllowWrongInput = false;

            typeAb(engine);
            Assert.That(engine.ProcessKey('x', 2000), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Untyped));
                Assert.That(engine.CaretIndex, Is.EqualTo(2));
                Assert.That(engine.ConsecutiveWrongKeys, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// The syllable-span judgement rule (backlog 179) and this one do not interact: a space cell
        /// is in no syllable group, so it keeps the point delta, which the untimed-space rule then
        /// zeroes. Pinned because both are era flags read within a few lines of each other.
        /// </summary>
        [Test]
        public void SyllableTimingDoesNotChangeAGapTypo()
        {
            var engine = started(abCd(), gapTypos: true);
            engine.SyllableTiming = true;
            var judged = record(engine);

            typeAb(engine);
            Assert.That(engine.ProcessKey('x', 3500), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Wrong));
                Assert.That(judged[^1].Delta, Is.EqualTo(0).Within(1e-9));
            });
        }

        // -----------------------------------------------------------------------------------------
        // Rendering: the gap shows the typo character, because a red space is invisible
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// <see cref="LyricLineDisplay.CellGlyph"/> is the pure seam every non-freestyle cell's text
        /// routes through, so pinning it pins what the player sees. A wrong LYRIC cell is unchanged
        /// (the expected character, reddened, so a mistyped line still reads as the line it was meant
        /// to be); a wrong WORD GAP shows the typed character, because the alternative is a red space
        /// and a red space is nothing at all.
        /// </summary>
        [Test]
        public void AWrongWordGapRendersTheTypedCharacter()
        {
            Assert.Multiple(() =>
            {
                Assert.That(LyricLineDisplay.CellGlyph(' ', CellState.Wrong, 'x'), Is.EqualTo('x'));
                Assert.That(LyricLineDisplay.CellFillColour(CellState.Wrong, isFreestyle: false, inSungSyllable: false, syncQuality: null),
                    Is.EqualTo(TypeBeatStyle.ErrorChar), "and it wears the same error red a lyric typo does");

                Assert.That(LyricLineDisplay.CellGlyph('a', CellState.Wrong, 'x'), Is.EqualTo('a'), "a lyric cell keeps showing its lyric character");
            });
        }

        /// <summary>
        /// Every other state of a word gap is a space, the typo's own erase included: the
        /// substitution is scoped to the one state that has something to show and cannot leak a
        /// stale glyph into a cell the player took back.
        /// </summary>
        [Test]
        public void AWordGapInEveryOtherStateIsASpace()
        {
            foreach (var state in Enum.GetValues<CellState>())
            {
                if (state == CellState.Wrong)
                    continue;

                Assert.That(LyricLineDisplay.CellGlyph(' ', state, 'x'), Is.EqualTo(' '), $"a {state} gap renders as a space");
            }

            Assert.That(LyricLineDisplay.CellGlyph(' ', CellState.Wrong, null), Is.EqualTo(' '), "and so does a wrong one with nothing typed in it");
        }

        // -----------------------------------------------------------------------------------------
        // The ERA: every replay on disk keeps its rejections
        // -----------------------------------------------------------------------------------------

        private const int bit_wrong_input = 1;
        private const int bit_space_skips_word = 2;
        private const int bit_syllable_timing = 4;
        private const int bit_wrong_input_on_word_gaps = 8;

        /// <summary>
        /// The load-bearing default: a bare engine, and therefore a replay with no CONFIG frame and
        /// every replay written before backlog 181, judges the word gap STRICTLY. A default of the
        /// live rule would silently re-derive every stored run's rejected keystroke as a typo.
        /// </summary>
        [Test]
        public void TheEngineDefaultsToTheClassicStrictWordGap()
        {
            Assert.That(new TypingEngine(abCd()).WrongInputOnWordGaps, Is.False);
        }

        /// <summary>
        /// Live play turns it on UNCONDITIONALLY, Hard Rock included, which is the one place this
        /// era flag differs from <see cref="TypingEngine.SyllableTiming"/> (backlog 180 reverts that
        /// one under HR). HR halves the judgement WINDOWS; this is not a window, it is which cells
        /// the wrong-input model reaches, and an HR run already types wrong letters through
        /// everywhere else.
        /// </summary>
        [Test]
        public void LivePlayTurnsItOnForEveryModStack()
        {
            Assert.Multiple(() =>
            {
                Assert.That(liveEngine().WrongInputOnWordGaps, Is.True, "a no-mod play types gap typos through");
                Assert.That(liveEngine(new TypeBeatModHardRock()).WrongInputOnWordGaps, Is.True, "and so does Hard Rock: this is an input model, not a window");
                Assert.That(liveEngine(new TypeBeatModEasy()).WrongInputOnWordGaps, Is.True);
                Assert.That(liveEngine(new TypeBeatModDoubleTime(), new TypeBeatModHardRock()).WrongInputOnWordGaps, Is.True);

                // The contrast, restated here so the asymmetry is deliberate rather than accidental.
                Assert.That(liveEngine(new TypeBeatModHardRock()).SyllableTiming, Is.False);
            });
        }

        /// <summary>
        /// The bits are at the positions the format names, so the encoded word stays readable as a
        /// number: a replay of live play (wrong input allowed, no word skipping, syllable judgement,
        /// gap typos) is exactly 1 | 4 | 8 = 13.
        /// </summary>
        [Test]
        public void TheFlagsWordCarriesBitThree()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: true)
                                               .ToLegacy(new Beatmap()).MouseY, Is.EqualTo(13f));

                Assert.That(TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: true, syllableTiming: true, wrongInputOnWordGaps: true)
                                               .ToLegacy(new Beatmap()).MouseY, Is.EqualTo(15f));

                // The older call sites keep meaning what they always did.
                Assert.That(TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true)
                                               .ToLegacy(new Beatmap()).MouseY, Is.EqualTo(5f));
            });
        }

        /// <summary>
        /// 0..7 are the only flags words that existed before backlog 181, and every one of them must
        /// decode with bit 3 CLEAR, i.e. to the strict word gap those runs were played on. The three
        /// older bits keep their meaning and their positions exactly.
        /// </summary>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        public void ReplaysRecordedBeforeGapTyposDecodeAsStrict(int storedFlags)
        {
            var decoded = decode(storedFlags);

            Assert.Multiple(() =>
            {
                Assert.That(decoded.IsConfig, Is.True);
                Assert.That(decoded.WrongInputOnWordGaps, Is.False, "a replay from before the rule existed rejected wrong keys on the gap");
                Assert.That(decoded.AllowWrongInput, Is.EqualTo((storedFlags & bit_wrong_input) != 0));
                Assert.That(decoded.SpaceSkipsWord, Is.EqualTo((storedFlags & bit_space_skips_word) != 0));
                Assert.That(decoded.SyllableTiming, Is.EqualTo((storedFlags & bit_syllable_timing) != 0));

                // ...and the same word with bit 3 added decodes to the live model, changing nothing else.
                var live = decode(storedFlags | bit_wrong_input_on_word_gaps);
                Assert.That(live.WrongInputOnWordGaps, Is.True);
                Assert.That(live.AllowWrongInput, Is.EqualTo(decoded.AllowWrongInput));
                Assert.That(live.SpaceSkipsWord, Is.EqualTo(decoded.SpaceSkipsWord));
                Assert.That(live.SyllableTiming, Is.EqualTo(decoded.SyllableTiming));
            });
        }

        /// <summary>
        /// The behavioural half of the same guarantee, run through the real headless scorer on a run
        /// whose third keystroke is a wrong key on the word gap: under every flags word a stored
        /// replay can carry, that keystroke is REJECTED, the caret stays where it was, and the run
        /// finishes with all five cells typed. Nothing about the account records that a typo ever
        /// happened, which is exactly what those rows hold today.
        /// </summary>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        public void AStoredFlagsWordStillRejectsTheWrongKeyOnTheGap(int storedFlags)
        {
            var stored = scoreGapRun(storedFlags);

            Assert.Multiple(() =>
            {
                Assert.That(stored.Statistics.GetValueOrDefault(TypeBeatResultMapping.UNFIXED_TYPO), Is.Zero, "no cell was left wrong");
                Assert.That(stored.Statistics.GetValueOrDefault(HitResult.Miss), Is.Zero);
                Assert.That(stored.Completion, Is.EqualTo(1), "all five cells were typed");
                Assert.That(stored.UnconsumedFrames, Is.Zero);

                // And the same word with bit 3 set types it through instead, which is the whole of
                // the difference the era carries. Only where bit 0 lets a wrong key land at all: the
                // new flag extends allow-wrong-input rather than competing with it, so a GATEKEEPER
                // replay (bit 0 clear) re-derives identically under both, which is the second thing
                // this sweep pins.
                var live = scoreGapRun(storedFlags | bit_wrong_input_on_word_gaps);

                if ((storedFlags & bit_wrong_input) != 0)
                {
                    Assert.That(live.Statistics.GetValueOrDefault(TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(1));
                    Assert.That(live.Statistics, Is.Not.EquivalentTo(stored.Statistics));
                }
                else
                {
                    Assert.That(live.Statistics, Is.EquivalentTo(stored.Statistics), "Gatekeeper refuses the gap typo whichever way bit 3 points");
                    Assert.That(live.TotalScore, Is.EqualTo(stored.TotalScore));
                }
            });
        }

        /// <summary>
        /// The identity pin, on the flags word a live replay carried the day before backlog 181
        /// (wrong input allowed plus syllable-span timing, 1 | 4 = 5): the whole submitted account of
        /// the wrong-key-on-gap run is BIT-FOR-BIT what it was, hardcoded, because the point is that
        /// no arm of the new era may move it. This is the guarantee the recalculation tool rests on.
        /// </summary>
        [Test]
        public void AStoredRunReDerivesToItsStoredTotals()
        {
            var stored = scoreGapRun(bit_wrong_input | bit_syllable_timing);

            Assert.Multiple(() =>
            {
                Assert.That(stored.Statistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(5));
                Assert.That(stored.Statistics.GetValueOrDefault(HitResult.Miss), Is.Zero);
                Assert.That(stored.MaxCombo, Is.EqualTo(3));
                Assert.That(stored.TotalScore, Is.EqualTo(stored_run_total_score));
                Assert.That(stored.Accuracy, Is.EqualTo(1).Within(1e-9));
                Assert.That(stored.Completion, Is.EqualTo(1));
                Assert.That(stored.Rank, Is.EqualTo(ScoreRank.X));
            });
        }

        /// <summary>The pre-181 account of <see cref="gapRun"/> at flags 5, hardcoded so that no
        /// arm of the new era can move it unnoticed.</summary>
        private const long stored_run_total_score = 891328;

        #region Era harness

        private static TypeBeatReplayFrame decode(int storedFlags)
        {
            var frame = new TypeBeatReplayFrame();
            frame.FromLegacy(new LegacyReplayFrame(500, (float)TypeBeatReplayFrame.CONFIG, storedFlags, ReplayButtonState.None), new Beatmap());
            return frame;
        }

        /// <summary>
        /// "ab", a wrong key ON THE GAP, then the space and "cd". <paramref name="flags"/> is taken
        /// through the LEGACY DECODE, so the era arm is the one a stored .osr really produces rather
        /// than one the test constructs.
        /// </summary>
        private static Replay gapRun(int flags)
        {
            var config = decode(flags);
            config.Time = 1000;

            var replay = new Replay();

            replay.Frames.AddRange(new[]
            {
                config,
                new TypeBeatReplayFrame(1000, 'a'),
                new TypeBeatReplayFrame(1500, 'b'),
                new TypeBeatReplayFrame(2000, 'x'), // the word gap
                new TypeBeatReplayFrame(2000, ' '),
                new TypeBeatReplayFrame(2000, 'c'),
                new TypeBeatReplayFrame(2500, 'd'),
            });

            return replay;
        }

        private static TypeBeatReplayAccount scoreGapRun(int flags)
            => TypeBeatReplayScorer.Score(playableAbCd(), Array.Empty<Mod>(), gapRun(flags), TypoRule.Deferred, ComboRestoreRule.OnFix);

        /// <summary>
        /// A drawable ruleset built over the fixture exactly as gameplay builds it, mods and all: the
        /// engine is a lazy property off the constructor's beatmap and mod list, which is why the era
        /// is decided there rather than in a mod's <c>ApplyToDrawableRuleset</c>.
        /// </summary>
        private static TypingEngine liveEngine(params Mod[] mods)
            => new DrawableTypeBeatRuleset(new TypeBeatRuleset(), playableAbCd(), mods).Engine;

        #endregion
    }
}
