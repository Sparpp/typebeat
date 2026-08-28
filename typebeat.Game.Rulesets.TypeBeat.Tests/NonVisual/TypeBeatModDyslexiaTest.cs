// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Dyslexia (backlog 231): the letters of a word may be typed in ANY ORDER. The whole mod is one
// fork at the expected-character lookup in TypingEngine.ProcessKey plus an arm in each of the two
// engine factories, so this fixture pins three things and nothing else:
//
//   1. the SHIPPING SURFACE (acronym, type, unranked, incompatibility, the one flag it flips);
//   2. the FORK, from both sides: the flag off must be byte-identical to today, the flag on must be
//      identical again for in-order play, and an out-of-order press must be graded, announced,
//      restored and rendered against the cell it actually landed on;
//   3. the CARET INVARIANT the rest of the engine reads, which is what stops the mod leaking into
//      the rush cap, the Flashlight window and line completion: the caret is the leftmost cell
//      nobody has typed, whatever order the ones after it arrived in.
//
// Every expected delta below is hand-computed beside its assert, in the style of TypingEngineTest.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Utils;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypeBeatModDyslexiaTest
    {
        #region Fixture

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, double singEnd, double sealGrace, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, SealGraceMs = sealGrace, Units = units };

        private static LyricBeatmap map(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = string.Empty,
                AudioFileName = "a.mp3",
            },
            Lines = lines,
            Granularity = TimingGranularity.Line,
        };

        /// <summary>
        /// "cat dog" on one line, each word sung over a 300 ms window so a whole word can be typed
        /// inside one Great tolerance (250 early / 400 late at Line granularity) and every press
        /// below still classifies the same way whichever cell it lands on.
        ///
        /// <para>Cells: 0 c = 1000, 1 a = 1100, 2 t = 1200, 3 ' ' = 1300 (the first unit's EndTime),
        /// 4 d = 2000, 5 o = 2100, 6 g = 2200. The line runs [1000, 6000] with a 1000 ms seal grace,
        /// so "sealed at EndTime" and "sealed at EndTime + grace" are two distinguishable
        /// instants.</para>
        /// </summary>
        private static LyricBeatmap catDog() => map(
            line("cat dog", 1000, 6000, 2300, 1000, unit("cat", 1000, 1300), unit("dog", 2000, 2300)));

        /// <summary>
        /// One word with a MARK inside it, for the Literate arms. Under that mod every authored
        /// character is a cell, so "can't" flattens to c = 1000, a = 1100, n = 1200, ' = 1250 (the
        /// mark is untimed and interpolated between the letters either side of it) and t = 1300. The
        /// apostrophe is NOT a word boundary: only a typeable SPACE is (see
        /// <c>TypingEngine.isWordGap</c>), so all five cells are one word.
        /// </summary>
        private static LyricBeatmap apostrophe() => map(
            line("can't", 1000, 6000, 1400, 1000, unit("can't", 1000, 1400)));

        /// <summary>
        /// The live input model (backlog 181 and 184) with the JUDGEMENT rule left at the engine
        /// default, so every delta below is the plain (press time - cell target) that can be checked
        /// by hand. Deliberate: Dyslexia is orthogonal to which rule grades a press, because
        /// <c>judgedDeltaFor</c> already takes the cell index and both of its arms are pure functions
        /// of it, so proving the point-target arm proves the span one.
        /// </summary>
        private static TypingEngine engine(LyricBeatmap beatmap, bool dyslexia) => new TypingEngine(beatmap)
        {
            AnyOrderWithinWord = dyslexia,
            WrongInputOnWordGaps = true,
            StrictSpaces = true,
            FletcherEnabled = true,
            FlexibleLineSnap = true,
            BoundedRush = true,
        };

        /// <summary>The same engine with its first line already active.</summary>
        private static TypingEngine active(LyricBeatmap beatmap, bool dyslexia = true)
        {
            var typing = engine(beatmap, dyslexia);
            typing.Update(1000);
            return typing;
        }

        private static IReadOnlyList<TypingCell> cells(TypingEngine typing) => typing.Lines[0].Cells;

        /// <summary>The leftmost cell of line 0 nobody has typed anything into, or its cell count.</summary>
        private static int leftmostUntyped(TypingEngine typing)
        {
            var line = typing.Lines[0].Cells;

            for (int i = 0; i < line.Count; i++)
            {
                if (line[i].IsTypeable && line[i].State == CellState.Untyped)
                    return i;
            }

            return line.Count;
        }

        #endregion

        #region The mod's shipping surface

        [Test]
        public void ReportsAnUnrankedConversionWithTheDxAcronym()
        {
            var mod = new TypeBeatModDyslexia();

            Assert.AreEqual("Dyslexia", mod.Name);
            Assert.AreEqual("DX", mod.Acronym);
            Assert.AreEqual(ModType.Conversion, mod.Type);
            Assert.IsFalse(mod.Ranked,
                "word ORDER is most of what typing to a lyric asks; a run that need not keep it has no business on the shared leaderboards");
            Assert.IsTrue(mod.HasImplementation);
            Assert.IsTrue(mod.Description.ToString().Contains("any order"),
                "the description must say plainly what it does to a word");
        }

        /// <summary>
        /// The acronym is checked against the ruleset's own mod list rather than a list written out
        /// here, so a future mod that claims DX fails this instead of silently colliding.
        /// </summary>
        [Test]
        public void RulesetSurfacesDyslexiaUnderConversionWithAFreeAcronym()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.IsTrue(ruleset.GetModsFor(ModType.Conversion).Any(m => m is TypeBeatModDyslexia),
                "Dyslexia must be offered in the mod-select overlay under Conversion.");

            // An INPUT MODEL swap, like Gatekeeper and Fletcher, and not a difficulty knob: nothing
            // is tightened or loosened, the question the game asks changes.
            Assert.IsFalse(ruleset.GetModsFor(ModType.DifficultyReduction).Any(m => m is TypeBeatModDyslexia));
            Assert.IsFalse(ruleset.GetModsFor(ModType.Fun).Any(m => m is TypeBeatModDyslexia));

            var acronyms = ruleset.AllMods.Select(m => m.Acronym).ToList();
            Assert.AreEqual(acronyms.Count, acronyms.Distinct().Count(), "two mods share an acronym");
            Assert.AreEqual(1, acronyms.Count(a => a == "DX"));
        }

        /// <summary>
        /// No multiplier of any kind, in either pricing path. An unlisted acronym is 1.0x in both,
        /// and that is where an UNRANKED mod belongs: nothing it produces is submitted as a ranked
        /// score, so there is nothing to price.
        /// </summary>
        [Test]
        public void CostsAndPaysExactlyNothing()
        {
            var calculator = new TypeBeatScoreMultiplierCalculator(
                new ScoreMultiplierContext(new BeatmapDifficulty()));

            Assert.AreEqual(1.0, calculator.CalculateFor(new Mod[] { new TypeBeatModDyslexia() }), 1e-9);

            // Being unlisted must be neutral, not absorbing.
            double stacked = calculator.CalculateFor(new Mod[] { new TypeBeatModDyslexia(), new TypeBeatModLiterate() });
            Assert.AreEqual(1.05, stacked, 1e-9);

#pragma warning disable CS0618 // Member is obsolete
            Assert.AreEqual(1.0, new TypeBeatModDyslexia().ScoreMultiplier, 1e-9);
#pragma warning restore CS0618

            Assert.AreEqual(1.0, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModDyslexia() }, 500), 1e-12);
        }

        /// <summary>
        /// Mashing is the one mod it cannot be worn with, and the declaration is not merely tidiness:
        /// mashing rewrites the press into the CARET cell's expected character before the word is
        /// ever searched, so on an ordinary word the scan can only ever return the caret and the mod
        /// does nothing at all. (A freestyle slot is exempt from that rewrite, which is the one place
        /// the pair would not even be inert, and the same declaration covers it.)
        /// </summary>
        [Test]
        public void IsIncompatibleWithMashingAndNothingElse()
        {
            var mod = new TypeBeatModDyslexia();

            Assert.AreEqual(new[] { typeof(TypeBeatModMashing) }, mod.IncompatibleMods);
            Assert.IsFalse(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModDyslexia(), new TypeBeatModMashing() }));

            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModDyslexia(), new TypeBeatModGatekeeper() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModDyslexia(), new TypeBeatModLiterate() }));
            Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { new TypeBeatModDyslexia(), new TypeBeatModFletcher() }));

            var ruleset = new TypeBeatRuleset();

            foreach (var other in ruleset.AllMods.OfType<Mod>())
            {
                if (other is TypeBeatModMashing)
                    continue;

                Assert.IsFalse(other.IncompatibleMods.Any(t => t.IsAssignableFrom(typeof(TypeBeatModDyslexia))),
                    $"{other.Acronym} declares Dyslexia incompatible");
            }
        }

        /// <summary>
        /// One engine flag, applied through the same seam Mashing and Gatekeeper use, and OFF on a
        /// bare engine: no stored replay can carry this mod, so the default is the only thing a run
        /// recorded before it existed can re-derive under.
        /// </summary>
        [Test]
        public void ItsOnlyEffectIsSettingTheEngineFlag()
        {
            var mod = new TypeBeatModDyslexia();

            Assert.IsTrue(mod is IApplicableToDrawableRuleset<TypeBeatHitObject>);
            Assert.IsFalse(mod is IApplicableToScoreProcessor);
            Assert.IsFalse(mod is IApplicableToHealthProcessor);
            Assert.IsFalse(mod is IApplicableAfterBeatmapConversion);
            Assert.IsFalse(mod is IApplicableToDifficulty);
            Assert.IsFalse(mod is IApplicableToRate);
            Assert.IsFalse(mod is IApplicableFailOverride);

            Assert.IsFalse(new TypingEngine(catDog()).AnyOrderWithinWord,
                "the engine default is one character at a time, which is what every stored run was played on");
        }

        #endregion

        #region The fork: off, on-in-order, on-out-of-order

        /// <summary>
        /// THE PINNED PATH. With the flag clear, t-a-c on "cat" is what it has always been: the 't'
        /// and the 'c' are typos typed through on the cells they were pressed on, and only the 'a',
        /// which happens to fall where the caret already was, is correct.
        /// </summary>
        [Test]
        public void WithoutTheFlagOutOfOrderPressesAreTyposExactlyAsBefore()
        {
            var typing = active(catDog(), dyslexia: false);
            var line = cells(typing);

            Assert.IsTrue(typing.ProcessKey('t', 1200)); // caret cell 0 wants 'c'
            Assert.IsTrue(typing.ProcessKey('a', 1210)); // caret cell 1 wants 'a': the one that lands
            Assert.IsTrue(typing.ProcessKey('c', 1220)); // caret cell 2 wants 't'

            Assert.AreEqual(CellState.Wrong, line[0].State);
            Assert.AreEqual('t', line[0].TypedChar);
            Assert.AreEqual(CellState.Correct, line[1].State);
            Assert.AreEqual(CellState.Wrong, line[2].State);
            Assert.AreEqual('c', line[2].TypedChar);

            Assert.AreEqual(2, typing.Mistypes);
            Assert.AreEqual(3, typing.CaretIndex, "one step per press, wrong or right");
            Assert.AreEqual(0, typing.Combo, "the last press broke it");
            Assert.AreEqual(1, typing.MaxCombo);
        }

        /// <summary>
        /// The mod costs NOTHING when it is not used: a run typed in order is identical in every
        /// number the engine reports, which is the only way an input-model fork is safe to ship.
        /// </summary>
        [Test]
        public void WithTheFlagInOrderPlayIsIdentical()
        {
            var without = active(catDog(), dyslexia: false);
            var with = active(catDog(), dyslexia: true);

            // Every cell struck on its own target: c a t ' ' d o g.
            double[] targets = { 1000, 1100, 1200, 1300, 2000, 2100, 2200 };
            const string text = "cat dog";

            for (int i = 0; i < text.Length; i++)
            {
                Assert.IsTrue(without.ProcessKey(text[i], targets[i]));
                Assert.IsTrue(with.ProcessKey(text[i], targets[i]));
            }

            without.Update(6000);
            with.Update(6000);

            var a = without.BuildResults();
            var b = with.BuildResults();

            Assert.AreEqual(a.Score, b.Score);
            Assert.AreEqual(a.MaxCombo, b.MaxCombo);
            Assert.AreEqual(a.Accuracy, b.Accuracy, 1e-12);
            Assert.AreEqual(a.SyncPercent, b.SyncPercent, 1e-12);
            Assert.AreEqual(a.Grade, b.Grade);

            foreach (JudgementType type in Enum.GetValues<JudgementType>())
                Assert.AreEqual(a.Counts[type], b.Counts[type], $"{type} count moved");
        }

        /// <summary>
        /// THE MOD. The same three presses that were two typos above are three clean characters, and
        /// each is graded against ITS OWN cell rather than the caret's: the press times are staggered
        /// so the three deltas are all different and only one reading of them is possible.
        ///
        /// <para>The announced cell index is checked too, because that is what the display repaints
        /// (<c>LyricLineDisplay.refreshCell</c> is driven by <c>CharJudged</c>'s cell index) and it is
        /// the only way a caret that has not moved can still paint the character that was typed.</para>
        /// </summary>
        [Test]
        public void OutOfOrderPressesLandOnTheirOwnCellsAndAreGradedThere()
        {
            var typing = active(catDog());
            var line = cells(typing);

            var judged = new List<CharJudgement>();
            typing.CharJudged += judged.Add;

            Assert.IsTrue(typing.ProcessKey('t', 1200)); // cell 2, target 1200 => delta 0
            Assert.IsTrue(typing.ProcessKey('a', 1210)); // cell 1, target 1100 => delta 110
            Assert.IsTrue(typing.ProcessKey('c', 1220)); // cell 0, target 1000 => delta 220

            Assert.AreEqual(0, typing.Mistypes, "every press matched a character the word still owed");
            Assert.AreEqual(3, typing.Combo);

            Assert.AreEqual(new[] { 2, 1, 0 }, judged.Select(j => j.CellIndex).ToArray(),
                "the judgement must name the cell the press landed on, not the caret");
            Assert.AreEqual(new[] { 0.0, 110.0, 220.0 }, judged.Select(j => j.Delta).ToArray(),
                "each press is graded against its own cell's target, all three inside the 400 ms late Great window");

            foreach (var j in judged)
                Assert.AreEqual(JudgementType.Great, j.Type);

            Assert.AreEqual(220.0, line[0].JudgedDelta);
            Assert.AreEqual(110.0, line[1].JudgedDelta);
            Assert.AreEqual(0.0, line[2].JudgedDelta);

            Assert.AreEqual('c', line[0].TypedChar);
            Assert.AreEqual('a', line[1].TypedChar);
            Assert.AreEqual('t', line[2].TypedChar);

            // The frontier only rolled when the LEFTMOST cell was finally struck, and then it rolled
            // over the whole run at once, stopping on the word gap nobody has typed.
            Assert.AreEqual(3, typing.CaretIndex);
        }

        #endregion

        #region Where the scan may look

        /// <summary>
        /// The scan is bounded by the WORD, in both directions, and the boundary is
        /// <c>isWordGap</c>'s: a typeable space and nothing else.
        /// </summary>
        [Test]
        public void TheScanStopsAtTheWordBoundary()
        {
            // FORWARD. 'd' spells the first character of the NEXT word, and it is not consumed: the
            // press is priced as the typo it is, on the cell the caret is on.
            var forward = active(catDog());

            Assert.IsTrue(forward.ProcessKey('d', 1000));
            Assert.AreEqual(CellState.Wrong, cells(forward)[0].State);
            Assert.AreEqual('d', cells(forward)[0].TypedChar);
            Assert.AreEqual(CellState.Untyped, cells(forward)[4].State, "the next word's 'd' was not reached across the gap");
            Assert.AreEqual(1, forward.Mistypes);

            // FROM THE GAP. A caret sitting ON a word gap is not inside a word at all, so no scan
            // runs: the press keeps its ordinary meaning (a typo taking the gap, backlog 181) and
            // still does not reach into the word in front of it.
            var atGap = active(catDog());

            atGap.ProcessKey('c', 1000);
            atGap.ProcessKey('a', 1100);
            atGap.ProcessKey('t', 1200);
            Assert.AreEqual(3, atGap.CaretIndex);

            Assert.IsTrue(atGap.ProcessKey('d', 1300));
            Assert.AreEqual(CellState.Wrong, cells(atGap)[3].State);
            Assert.AreEqual('d', cells(atGap)[3].TypedChar);
            Assert.AreEqual(CellState.Untyped, cells(atGap)[4].State);

            // BACKWARD. A cell of an earlier word can never be UNTYPED while the caret is in a later
            // one, because the caret IS the leftmost untyped cell (pinned below), so the strongest
            // observable form of the same containment is a word the player GAVE UP: its cells sit
            // behind the caret holding nothing the player put there, and a key that spells one of
            // them still lands where the caret is.
            var back = active(catDog());
            back.SpaceSkipsWord = true;

            back.ProcessKey('c', 1000);      // cell 0
            back.ProcessKey(' ', 1100);      // abandons 'a' and 't', then types the gap it lands on
            Assert.AreEqual(CellState.Abandoned, cells(back)[1].State);
            Assert.AreEqual(4, back.CaretIndex);

            Assert.IsTrue(back.ProcessKey('a', 2000));
            Assert.AreEqual(CellState.Abandoned, cells(back)[1].State, "the abandoned 'a' was not reached backwards over the gap");
            Assert.AreEqual(CellState.Wrong, cells(back)[4].State, "the press was priced at the caret instead");
        }

        /// <summary>
        /// A MARK is not a boundary: it rides inside the word it is attached to (which is
        /// <c>isWordGap</c>'s whole point), so under the Literate mod, where marks are cells of their
        /// own, an out-of-order press reaches straight across one.
        /// </summary>
        [Test]
        public void PunctuationRidesInsideTheWord()
        {
            var typing = new TypingEngine(apostrophe(), literate: true) { AnyOrderWithinWord = true };
            typing.Update(1000);

            var line = typing.Lines[0].Cells;
            Assert.AreEqual(5, line.Count);
            Assert.AreEqual('\'', line[3].Expected);
            Assert.IsTrue(line[3].IsTypeable, "under Literate a supported mark is a cell the player types");

            // The last character first, straight over the mark, then the mark itself.
            Assert.IsTrue(typing.ProcessKey('t', 1300)); // cell 4, target 1300 => delta 0
            Assert.AreEqual(CellState.Correct, line[4].State);
            Assert.AreEqual(0, typing.CaretIndex, "nothing before it has been typed, so the frontier has not moved");

            Assert.IsTrue(typing.ProcessKey('\'', 1300)); // cell 3, target 1250 => delta 50
            Assert.AreEqual(CellState.Correct, line[3].State);
            Assert.AreEqual(50.0, line[3].JudgedDelta);

            Assert.AreEqual(0, typing.Mistypes);
        }

        #endregion

        #region What the other input-model mods do to it

        /// <summary>
        /// Literate + Dyslexia: the mod widens WHICH cell a press may land on, never what "matches"
        /// means. A wrong-case letter satisfies nothing in the word, in any order, and is priced as
        /// the typo the Literate mod says it is.
        /// </summary>
        [Test]
        public void LiterateStillDemandsTheExactCase()
        {
            var typing = new TypingEngine(apostrophe(), literate: true) { AnyOrderWithinWord = true };
            typing.Update(1000);

            var line = typing.Lines[0].Cells;
            Assert.IsTrue(typing.CaseSensitive);

            Assert.IsTrue(typing.ProcessKey('T', 1300));

            Assert.AreEqual(CellState.Untyped, line[4].State, "'T' does not satisfy the 't' the word is still owed");
            Assert.AreEqual(CellState.Wrong, line[0].State, "so the press falls through to the caret and is a typo there");
            Assert.AreEqual('T', line[0].TypedChar);
            Assert.AreEqual(1, typing.Mistypes);

            // ...and case-insensitive play, which is every stack but Literate, takes the same press
            // out of order.
            var folding = active(catDog());

            Assert.IsTrue(folding.ProcessKey('T', 1200));
            Assert.AreEqual(CellState.Correct, cells(folding)[2].State);
            Assert.AreEqual(0, folding.Mistypes);
        }

        /// <summary>
        /// Gatekeeper + Dyslexia is a meaningful stack and stays one: a key that matches nothing the
        /// word still owes is REJECTED outright, caret unmoved, and still grows the mash-fail streak.
        /// A key the word does owe is accepted wherever in the word it sits.
        /// </summary>
        [Test]
        public void GatekeeperStillRejectsAKeyTheWordDoesNotWant()
        {
            var typing = active(catDog());
            typing.AllowWrongInput = false;

            var rejected = new List<char>();
            typing.WrongKeyRejected += rejected.Add;

            Assert.IsTrue(typing.ProcessKey('z', 1000));

            Assert.AreEqual(new[] { 'z' }, rejected);
            Assert.AreEqual(1, typing.ConsecutiveWrongKeys);
            Assert.AreEqual(1, typing.Mistypes);
            Assert.AreEqual(0, typing.CaretIndex, "rejection holds the caret on the cell");
            Assert.AreEqual(CellState.Untyped, cells(typing)[0].State, "and writes nothing into it");

            // The 't' the word still owes is taken, out of order, and clears the streak.
            Assert.IsTrue(typing.ProcessKey('t', 1200));
            Assert.AreEqual(CellState.Correct, cells(typing)[2].State);
            Assert.AreEqual(0, typing.ConsecutiveWrongKeys);
            Assert.AreEqual(0, typing.CaretIndex);
        }

        /// <summary>
        /// Mashing rewrites every press into the caret cell's own expected character before the word
        /// is searched, so the leftmost untyped cell always matches and the scan can only return the
        /// caret: the flag changes nothing. Declared incompatible anyway (see above), this pins that
        /// the engine does not misbehave if the pair is ever built by hand.
        /// </summary>
        [Test]
        public void MashingLeavesNothingForTheScanToFind()
        {
            var mashing = active(catDog(), dyslexia: false);
            mashing.MashingEnabled = true;

            var both = active(catDog());
            both.MashingEnabled = true;

            double[] times = { 1000, 1100, 1200, 1300, 2000, 2100, 2200 };

            foreach (double t in times)
            {
                Assert.IsTrue(mashing.ProcessKey('z', t));
                Assert.IsTrue(both.ProcessKey('z', t));
            }

            Assert.AreEqual(mashing.CaretIndex, both.CaretIndex);
            Assert.AreEqual(mashing.Combo, both.Combo);
            Assert.AreEqual(mashing.Score, both.Score);
            Assert.AreEqual(mashing.Mistypes, both.Mistypes);

            for (int i = 0; i < cells(mashing).Count; i++)
            {
                Assert.AreEqual(cells(mashing)[i].State, cells(both)[i].State, $"cell {i} state");
                Assert.AreEqual(cells(mashing)[i].TypedChar, cells(both)[i].TypedChar, $"cell {i} char");
            }

            Assert.AreEqual(7, both.CaretIndex, "and the mashed line is finished either way");
        }

        #endregion

        #region The caret, and everything that reads it

        /// <summary>
        /// COMBO RESTORE across an out-of-order fix. The claim a break leaves is a (line, cell,
        /// streak) triple redeemed by typing THAT cell, so it has to be keyed on the cell the press
        /// landed on and not on the caret.
        ///
        /// <para>The state that separates the two is a word SKIP reclaimed by backspace: the skip
        /// snapshots its break against the first abandoned cell, and the backspace that reclaims the
        /// word puts the caret on the last character actually typed, which is BEHIND that cell. So
        /// the cell holding the claim is ahead of the caret, and only a press that reports where it
        /// landed can redeem it.</para>
        /// </summary>
        [Test]
        public void AnOutOfOrderFixRestoresTheStreakTheBreakCost()
        {
            var typing = active(catDog());
            typing.SpaceSkipsWord = true;

            int restored = 0;
            typing.ComboRestored += streak => restored += streak;

            Assert.IsTrue(typing.ProcessKey('c', 1000)); // cell 0, combo 1
            Assert.AreEqual(1, typing.Combo);

            // The skip abandons cells 1 and 2, takes the one break the streak of 1 costs, and
            // snapshots it against cell 1; the space itself then lands on the gap.
            Assert.IsTrue(typing.ProcessKey(' ', 1300));
            Assert.AreEqual(CellState.Abandoned, cells(typing)[1].State);
            Assert.AreEqual(CellState.Abandoned, cells(typing)[2].State);
            Assert.AreEqual(1, typing.Combo, "the gap press rebuilt one");

            // The first backspace takes back the space itself (the nearest thing the player typed),
            // and the second steps transparently over both abandoned cells, re-opening them, and
            // erases cell 0. The claim's cell (1) is now UNTYPED and AHEAD of the caret.
            Assert.IsTrue(typing.ProcessBackspace());
            Assert.AreEqual(3, typing.CaretIndex);

            Assert.IsTrue(typing.ProcessBackspace());
            Assert.AreEqual(0, typing.CaretIndex);
            Assert.AreEqual(CellState.Untyped, cells(typing)[1].State);
            Assert.AreEqual(CellState.Untyped, cells(typing)[2].State);
            Assert.AreEqual(0, restored);

            // Typing it out of order redeems the break: combo 1 (standing) + 1 (restored) + 1 (this
            // press) = 3.
            Assert.IsTrue(typing.ProcessKey('a', 1600));
            Assert.AreEqual(1, restored, "the streak the skip broke was put back by the cell that redeems it");
            Assert.AreEqual(3, typing.Combo);
        }

        /// <summary>
        /// THE CARET INVARIANT, which is what keeps the mod out of everything that reads the caret:
        /// <c>CaretCountablePosition</c>, the Fletcher rush cap and the Flashlight window all treat
        /// it as a monotone frontier, so a run typed out of order must measure exactly like an
        /// in-order run that reached the same character.
        /// </summary>
        [Test]
        public void TheCaretIsAlwaysTheLeftmostUntypedCell()
        {
            var typing = active(catDog());

            Assert.AreEqual(leftmostUntyped(typing), typing.CaretIndex);

            typing.ProcessKey('t', 1200);
            Assert.AreEqual(0, typing.CaretIndex);
            Assert.AreEqual(leftmostUntyped(typing), typing.CaretIndex);
            Assert.AreEqual(0, typing.CaretCountablePosition, "a press ahead of the caret spends no distance budget");

            typing.ProcessKey('a', 1210);
            Assert.AreEqual(leftmostUntyped(typing), typing.CaretIndex);
            Assert.AreEqual(0, typing.CaretCountablePosition);

            typing.ProcessKey('c', 1220);
            Assert.AreEqual(3, typing.CaretIndex);
            Assert.AreEqual(leftmostUntyped(typing), typing.CaretIndex);

            // ...and it measures the same as the in-order run that reached the same place.
            var inOrder = active(catDog(), dyslexia: false);
            inOrder.ProcessKey('c', 1000);
            inOrder.ProcessKey('a', 1100);
            inOrder.ProcessKey('t', 1200);

            Assert.AreEqual(inOrder.CaretIndex, typing.CaretIndex);
            Assert.AreEqual(inOrder.CaretCountablePosition, typing.CaretCountablePosition);
            Assert.AreEqual(inOrder.CharsAheadOfPlayhead(1300), typing.CharsAheadOfPlayhead(1300));

            // A typo typed through has to roll the frontier the same way, or the caret would come to
            // rest on a cell that was already typed out of order.
            var afterTypo = active(catDog());
            afterTypo.ProcessKey('a', 1100); // cell 1, out of order
            afterTypo.ProcessKey('t', 1200); // cell 2, out of order
            Assert.AreEqual(0, afterTypo.CaretIndex);

            Assert.IsTrue(afterTypo.ProcessKey('z', 1220)); // matches nothing left: a typo on cell 0
            Assert.AreEqual(CellState.Wrong, cells(afterTypo)[0].State);
            Assert.AreEqual(3, afterTypo.CaretIndex, "the caret stepped over the two cells already typed");
            Assert.AreEqual(leftmostUntyped(afterTypo), afterTypo.CaretIndex);
        }

        /// <summary>
        /// The SEAL reads cell state and the caret reads its own frontier, and a word typed out of
        /// order has to leave the two agreeing: the line is complete, nothing is missed, and the
        /// early seal fires at the line's own EndTime instead of holding the song up for the grace.
        /// A line only PART typed still seals its untyped cells as misses, exactly as today.
        /// </summary>
        [Test]
        public void AnOutOfOrderLineStillSealsEarlyAndCleanly()
        {
            var typing = active(catDog());

            var sealed_ = new List<LineSealResult>();
            typing.LineSealed += sealed_.Add;

            // Both words backwards, and the gap in the middle where it always was.
            typing.ProcessKey('t', 1200);
            typing.ProcessKey('a', 1210);
            typing.ProcessKey('c', 1220);
            typing.ProcessKey(' ', 1300);
            typing.ProcessKey('g', 2200);
            typing.ProcessKey('o', 2210);
            typing.ProcessKey('d', 2220);

            Assert.IsTrue(typing.IsLineComplete);
            Assert.AreEqual(7, typing.CaretIndex, "== Cells.Count when complete");

            typing.Update(6000); // the line's own EndTime: the early seal, 1000 ms before the grace runs out
            Assert.AreEqual(1, sealed_.Count);
            Assert.AreEqual(0, sealed_[0].MissedCells);
            Assert.AreEqual(0, typing.BuildResults().Counts[JudgementType.Miss]);
            Assert.AreEqual(7, typing.BuildResults().Counts[JudgementType.Great]);

            // The other half: a word left half typed holds the line open to its full grace and then
            // seals the rest as misses, which is what the early seal must not short-circuit.
            var partial = active(catDog());
            var partialSealed = new List<LineSealResult>();
            partial.LineSealed += partialSealed.Add;

            partial.ProcessKey('t', 1200);
            partial.ProcessKey('c', 1220);

            partial.Update(6000);
            Assert.IsEmpty(partialSealed, "five cells are still owed, so the line keeps its reclaim window");

            partial.Update(7000); // EndTime + SealGraceMs, and still open: the player is mid-line, so
            Assert.IsEmpty(partialSealed, "the unpinned caret's drag grace holds it open a further 1500");

            partial.Update(8500); // EndTime + SealGraceMs + FLETCHER_DRAG_GRACE_MS
            Assert.AreEqual(1, partialSealed.Count);
            Assert.AreEqual(5, partialSealed[0].MissedCells, "'a', the gap, and all three of dog");
        }

        #endregion

        #region Re-deriving a stored run

        private static TypeBeatBeatmap replayBeatmap()
        {
            var beatmap = new TypeBeatBeatmap();

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Line = catDog().Lines[0],
                Granularity = TimingGranularity.Line,
            });

            // Nested per-cell objects are built by ApplyDefaults, which is what gives the score
            // processor its maximum_statistics.
            foreach (var hitObject in beatmap.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return beatmap;
        }

        /// <summary>The live CONFIG header every recorded run carries, plus the given keystrokes.</summary>
        private static Replay run(params (double time, char c)[] presses)
        {
            var replay = new Replay();

            replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(1000, true,
                syllableTiming: false, wrongInputOnWordGaps: true, strictSpaces: true,
                charTimedStretch: true, flexibleLines: true, boundedRush: true));

            foreach (var (time, c) in presses)
                replay.Frames.Add(new TypeBeatReplayFrame(time, c));

            return replay;
        }

        private static TypeBeatReplayAccount score(IBeatmap beatmap, Replay replay, params Mod[] mods)
            => TypeBeatReplayScorer.Score(beatmap, mods, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);

        private static int count(TypeBeatReplayAccount account, HitResult result)
            => account.Statistics.GetValueOrDefault(result);

        /// <summary>
        /// THE MOD LIST IS THE MECHANISM. Dyslexia carries no replay CONFIG bit, because no stored
        /// run can predate a mod, so the only thing that can tell the rescorer a run was played with
        /// the letters arriving in any order is the score's own mod list. This pins both halves: with
        /// the mod, an out-of-order run re-derives the SAME account as the in-order run of the same
        /// cells; without it, the identical frames re-derive a different one, which is what makes the
        /// arm in <c>TypeBeatReplayScorer.createEngine</c> load-bearing rather than decorative.
        /// </summary>
        [Test]
        public void AStoredDyslexiaRunReDerivesOnlyWithTheModOnTheList()
        {
            var beatmap = replayBeatmap();

            // Each cell struck dead on its own target, in the order c a t ' ' d o g.
            var ordered = run((1000, 'c'), (1100, 'a'), (1200, 't'), (1300, ' '), (2000, 'd'), (2100, 'o'), (2200, 'g'));

            // The same seven cells, each struck within its own Great window, both words backwards.
            var scrambled = run((1200, 't'), (1210, 'a'), (1220, 'c'), (1300, ' '), (2200, 'g'), (2210, 'o'), (2220, 'd'));

            var clean = score(beatmap, ordered);
            var withMod = score(beatmap, scrambled, new TypeBeatModDyslexia());
            var withoutMod = score(beatmap, scrambled);

            Assert.AreEqual(7, count(clean, HitResult.Great));

            Assert.AreEqual(count(clean, HitResult.Great), count(withMod, HitResult.Great),
                "every out-of-order press still landed inside its own cell's Great window");
            Assert.AreEqual(0, count(withMod, HitResult.Miss));
            Assert.AreEqual(clean.MaxCombo, withMod.MaxCombo);
            Assert.AreEqual(clean.Accuracy, withMod.Accuracy, 1e-12);
            Assert.AreEqual(clean.TotalScore, withMod.TotalScore);
            Assert.AreEqual(clean.Completion, withMod.Completion, 1e-12);
            Assert.AreEqual(clean.Rank, withMod.Rank);

            // ...and the same frames judged without the mod are a different run entirely: most of
            // those presses were the wrong character for the cell the caret was on.
            Assert.Less(count(withoutMod, HitResult.Great), 7);
            Assert.Less(withoutMod.MaxCombo, withMod.MaxCombo);
            Assert.Less(withoutMod.Accuracy, withMod.Accuracy);
        }

        #endregion
    }
}
