// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Spaceless (backlog 470): every inter-word space completes itself, and the spacebar is not input.
// This fixture pins:
//
//   1. the SHIPPING SURFACE (acronym, type, unranked, 0.6x, incompatibilities, the stamp it applies);
//   2. the CELL LIST (spaces completed, letters untouched, counts coherent with the nested objects);
//   3. the KEY (refused before every path that could read it: typing, Gatekeeper's rejection, the
//      word skip, mashing, the first line's opening press, the manual newline);
//   4. the WORD (the gestures and the Dyslexia scan still see two words, not one);
//   5. autoplay and REPLAY re-derivation, through the same scorer the server runs.

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
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;
using typebeat.Game.Utils;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypeBeatModSpacelessTest
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
        /// "cat dog": 0 c = 1000, 1 a = 1100, 2 t = 1200, 3 ' ' = 1300, 4 d = 2000, 5 o = 2100,
        /// 6 g = 2200 (the same line <c>TypeBeatModDyslexiaTest</c> uses). Under the mod cell 3 is
        /// completed, so six cells are typed.
        /// </summary>
        private static LyricLine catDogLine() => line("cat dog", 1000, 6000, 2300, 1000, unit("cat", 1000, 1300), unit("dog", 2000, 2300));

        private static LyricBeatmap catDog() => map(catDogLine());

        /// <summary>The live input model (backlog 181 / 184) with the unpinned caret, as the Dyslexia fixture runs it.</summary>
        private static TypingEngine engine(LyricBeatmap beatmap, bool spaceless) => new TypingEngine(beatmap, spaceless: spaceless)
        {
            WrongInputOnWordGaps = true,
            StrictSpaces = true,
            FletcherEnabled = true,
            FlexibleLineSnap = true,
            BoundedRush = true,
        };

        /// <summary>The same engine with its first line already active.</summary>
        private static TypingEngine active(LyricBeatmap beatmap, bool spaceless = true)
        {
            var typing = engine(beatmap, spaceless);
            typing.Update(1000);
            return typing;
        }

        private static IReadOnlyList<TypingCell> cells(TypingEngine typing) => typing.Lines[0].Cells;

        private static void typeCatDog(TypingEngine typing)
        {
            Assert.IsTrue(typing.ProcessKey('c', 1000));
            Assert.IsTrue(typing.ProcessKey('a', 1100));
            Assert.IsTrue(typing.ProcessKey('t', 1200));
            Assert.IsTrue(typing.ProcessKey('d', 2000));
            Assert.IsTrue(typing.ProcessKey('o', 2100));
            Assert.IsTrue(typing.ProcessKey('g', 2200));
        }

        #endregion

        #region The mod's shipping surface

        [Test]
        public void ReportsAnUnrankedDifficultyReductionWithTheSlAcronym()
        {
            var mod = new TypeBeatModSpaceless();

            Assert.AreEqual("Spaceless", mod.Name);
            Assert.AreEqual("SL", mod.Acronym);
            Assert.AreEqual(ModType.DifficultyReduction, mod.Type);
            Assert.IsFalse(mod.Ranked, "a run that never presses space is not the same play, so it stays off the shared boards");
            Assert.IsTrue(mod.HasImplementation);
            Assert.IsNull(mod.Icon, "type!beat's own mods show their acronym, like Shorthand and Literate");
        }

        [Test]
        public void RulesetOffersItUnderDifficultyReductionWithAFreeAcronym()
        {
            var ruleset = new TypeBeatRuleset();

            Assert.IsTrue(ruleset.GetModsFor(ModType.DifficultyReduction).Any(m => m is TypeBeatModSpaceless));

            var acronyms = ruleset.AllMods.Select(m => m.Acronym).ToList();
            Assert.AreEqual(acronyms.Count, acronyms.Distinct().Count(), "two mods share an acronym");
            Assert.AreEqual(1, acronyms.Count(a => a == "SL"));
            Assert.IsInstanceOf<TypeBeatModSpaceless>(ruleset.CreateModFromAcronym("SL"), "a stored score's acronym must resolve to the mod");
        }

        [Test]
        public void ScoresAtPointSixAndEarnsNoPpTerm()
        {
            var calculator = new TypeBeatScoreMultiplierCalculator(new ScoreMultiplierContext(new BeatmapDifficulty()));

            Assert.AreEqual(0.6, calculator.CalculateFor(new Mod[] { new TypeBeatModSpaceless() }), 1e-9);
            Assert.AreEqual(0.6 * 1.05, calculator.CalculateFor(new Mod[] { new TypeBeatModSpaceless(), new TypeBeatModLiterate() }), 1e-9);

#pragma warning disable CS0618 // Member is obsolete
            Assert.AreEqual(0.6, new TypeBeatModSpaceless().ScoreMultiplier, 1e-9, "the legacy self-report moves with the calculator");
#pragma warning restore CS0618

            // Unranked, so it earns no pp at all; the pp table is neutral for it like for Shorthand.
            Assert.AreEqual(1.0, PerformancePoints.ModMultiplier(new Mod[] { new TypeBeatModSpaceless() }, 500), 1e-12);
        }

        [Test]
        public void IsIncompatibleWithShorthandAndPolyglotOnBothSides()
        {
            var mod = new TypeBeatModSpaceless();

            CollectionAssert.AreEquivalent(new[] { typeof(TypeBeatModShorthand), typeof(TypeBeatModPolyglot) }, mod.IncompatibleMods);
            CollectionAssert.Contains(new TypeBeatModShorthand().IncompatibleMods, typeof(TypeBeatModSpaceless));
            CollectionAssert.Contains(new TypeBeatModPolyglot().IncompatibleMods, typeof(TypeBeatModSpaceless));

            Assert.IsFalse(ModUtils.CheckCompatibleSet(new Mod[] { mod, new TypeBeatModShorthand() }));
            Assert.IsFalse(ModUtils.CheckCompatibleSet(new Mod[] { mod, new TypeBeatModPolyglot() }));

            foreach (var other in new Mod[]
                     {
                         new TypeBeatModLiterate(), new TypeBeatModGatekeeper(), new TypeBeatModDyslexia(), new TypeBeatModFletcher(),
                         new TypeBeatModEasy(), new TypeBeatModHardRock(), new TypeBeatModFlashlight(), new TypeBeatModRecite(), new TypeBeatModMashing(),
                     })
            {
                Assert.IsTrue(ModUtils.CheckCompatibleSet(new Mod[] { mod, other }), $"{other.Acronym} should be wearable with Spaceless");
            }
        }

        /// <summary>It shapes the beatmap (a cell list change), and does so only through the stamp.</summary>
        [Test]
        public void ItsOnlyEffectIsTheLineStamp()
        {
            var mod = new TypeBeatModSpaceless();

            Assert.IsTrue(mod is IApplicableAfterBeatmapConversion);
            Assert.IsFalse(mod is IApplicableToTypingEngine, "the engine reads it off the mod list at construction, like Shorthand");
            CollectionAssert.AreEqual(new[] { mod }, ModUtils.BeatmapShapingMods(new Mod[] { mod }));

            var beatmap = unstampedBeatmap();
            Assert.IsFalse(beatmap.HitObjects.Single().Spaceless);

            mod.ApplyToBeatmap(beatmap);
            Assert.IsTrue(beatmap.HitObjects.Single().Spaceless);

            Assert.IsFalse(new TypingEngine(catDog()).Spaceless, "off on a bare engine, which is what every stored run re-derives under");
        }

        #endregion

        #region The cell list

        [Test]
        public void EverySpaceIsCompletedAndNothingElseMoves()
        {
            var plain = TypingLine.ForMods(catDogLine(), literate: false, polyglot: false, language: null);
            var spaceless = TypingLine.ForMods(catDogLine(), literate: false, polyglot: false, language: null, spaceless: true);

            Assert.IsTrue(spaceless.Spaceless);
            Assert.AreEqual(plain.Cells.Count, spaceless.Cells.Count, "the space is still a cell; it is just not typed");
            Assert.AreEqual(7, plain.TypeableCount);
            Assert.AreEqual(6, spaceless.TypeableCount);

            for (int i = 0; i < plain.Cells.Count; i++)
            {
                Assert.AreEqual(plain.Cells[i].Expected, spaceless.Cells[i].Expected);
                Assert.AreEqual(plain.Cells[i].TargetTime, spaceless.Cells[i].TargetTime, 1e-9, $"cell {i}'s target moved");
                bool space = plain.Cells[i].Expected == ' ';
                Assert.AreEqual(!space, spaceless.Cells[i].IsTypeable, $"cell {i}");
                Assert.AreEqual(space, spaceless.Cells[i].IsAutoCompleted, $"cell {i}");
            }

            // The syllables and words were cut from the full list, so neither moved.
            Assert.AreEqual(plain.Syllables.Count, spaceless.Syllables.Count);
            Assert.AreEqual(plain.Words.Count, spaceless.Words.Count);
        }

        /// <summary>
        /// The default stream turns a hyphen into a space, and that space is completed too; under
        /// Literate the hyphen is a mark, typed, and only the authored spaces are completed.
        /// </summary>
        [Test]
        public void TheSpacesOfEitherStreamAreCompleted()
        {
            var authored = line("The bad-cat sat.", 1000, 6000, 2300, 1000, unit("The", 1000, 1300), unit("bad-cat", 1300, 1900), unit("sat.", 2000, 2300));

            var stream = TypingLine.ForMods(authored, literate: false, polyglot: false, language: null, spaceless: true);
            Assert.AreEqual("the bad cat sat", stream.DisplayText);
            Assert.IsFalse(stream.Cells.Any(c => c.Expected == ' ' && c.IsTypeable));
            Assert.AreEqual(3, stream.Cells.Count(c => c.Expected == ' ' && c.IsAutoCompleted));
            Assert.AreEqual(12, stream.TypeableCount);

            var literate = TypingLine.ForMods(authored, literate: true, polyglot: false, language: null, spaceless: true);
            Assert.AreEqual(2, literate.Cells.Count(c => c.Expected == ' ' && c.IsAutoCompleted));
            Assert.IsTrue(literate.Cells.Single(c => c.Expected == '-').IsTypeable, "a Literate hyphen is a mark, not a space");
        }

        /// <summary>Polyglot ignores the mod, as it ignores Shorthand, so a tampered stack still cuts one coherent list.</summary>
        [Test]
        public void PolyglotIgnoresTheMod()
        {
            var typing = new TypingEngine(catDog(), polyglot: true, spaceless: true);

            Assert.IsFalse(typing.Spaceless, "the key and the cells must agree: Polyglot keeps both its spaces and its spacebar");
            Assert.IsTrue(typing.Lines[0].Cells[3].IsTypeable);
        }

        /// <summary>
        /// The nested scoring objects follow the stamp, which is what keeps maximum_statistics, the
        /// engine's typeable count and the cells the score processor resolves index for index.
        /// </summary>
        [Test]
        public void TheNestedScoringObjectsLeaveTheSpaceOut()
        {
            var plain = replayBeatmap(spaceless: false).HitObjects.Single();
            var stamped = replayBeatmap(spaceless: true).HitObjects.Single();

            Assert.AreEqual(7, plain.NestedHitObjects.Count);
            Assert.AreEqual(6, stamped.NestedHitObjects.Count);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 4, 5, 6 }, stamped.NestedHitObjects.OfType<TypeBeatCharObject>().Select(o => o.CellIndex));
        }

        #endregion

        #region The key

        [Test]
        public void TheLineIsTypedWithoutASpace()
        {
            var typing = active(catDog());

            typeCatDog(typing);

            Assert.AreEqual(CellState.AutoSkipped, cells(typing)[3].State, "the caret hopped the completed space");
            Assert.IsTrue(cells(typing).Where(c => c.IsTypeable).All(c => c.State == CellState.Correct));
            Assert.IsTrue(typing.IsLineComplete);
            Assert.AreEqual(6, typing.Combo);
            Assert.AreEqual(0, typing.Mistypes);

            typing.Update(6000);
            var results = typing.BuildResults();
            Assert.AreEqual(6, results.MaxCombo);
            Assert.AreEqual(1.0, results.Accuracy, 1e-12);
        }

        /// <summary>
        /// The spacebar anywhere in the line, in a word or at its end, does nothing at all: not
        /// consumed, not a typo, the caret, combo and accuracy untouched.
        /// </summary>
        [Test]
        public void ASpaceIsNeverInputAndNeverWrong()
        {
            var typing = active(catDog());

            typing.ProcessKey('c', 1000);

            Assert.IsFalse(typing.ProcessKey(' ', 1050), "mid-word");
            Assert.AreEqual(1, typing.CaretIndex);
            Assert.AreEqual(CellState.Untyped, cells(typing)[1].State);

            typing.ProcessKey('a', 1100);
            typing.ProcessKey('t', 1200);
            Assert.AreEqual(4, typing.CaretIndex);

            Assert.IsFalse(typing.ProcessKey(' ', 1300), "where the space used to be owed");
            Assert.AreEqual(4, typing.CaretIndex);

            typing.ProcessKey('d', 2000);
            typing.ProcessKey('o', 2100);
            typing.ProcessKey('g', 2200);

            Assert.AreEqual(0, typing.Mistypes);
            Assert.AreEqual(6, typing.Combo);
            Assert.AreEqual(1.0, typing.LiveAccuracy, 1e-12, "a refused press is not a keypress");
        }

        [Test]
        public void WithoutTheModTheSameSpaceIsATypo()
        {
            // The control: on the bare engine a mid-word space is a typo typed through (backlog 184).
            var typing = active(catDog(), spaceless: false);

            typing.ProcessKey('c', 1000);
            Assert.IsTrue(typing.ProcessKey(' ', 1050));
            Assert.AreEqual(1, typing.Mistypes);
        }

        [Test]
        public void GatekeeperNeverRejectsIt()
        {
            var typing = active(catDog());
            typing.AllowWrongInput = false;

            int rejected = 0, broken = 0;
            typing.WrongKeyRejected += _ => rejected++;
            typing.ComboBroken += () => broken++;

            typing.ProcessKey('c', 1000);

            for (int i = 0; i < 20; i++)
                Assert.IsFalse(typing.ProcessKey(' ', 1010 + i));

            Assert.AreEqual(0, rejected);
            Assert.AreEqual(0, broken);
            Assert.AreEqual(0, typing.ConsecutiveWrongKeys, "twenty spaces must not build the mash-fail streak");
            Assert.AreEqual(1, typing.Combo);
        }

        [Test]
        public void ItSkipsNoWord()
        {
            var typing = active(catDog());
            typing.SpaceSkipsWord = true;

            typing.ProcessKey('c', 1000);
            Assert.IsFalse(typing.ProcessKey(' ', 1050));

            Assert.AreEqual(CellState.Untyped, cells(typing)[1].State, "no cell was abandoned");
            Assert.AreEqual(1, typing.CaretIndex);
        }

        [Test]
        public void MashingDoesNotTurnItIntoALetter()
        {
            var typing = active(catDog());
            typing.MashingEnabled = true;

            Assert.IsFalse(typing.ProcessKey(' ', 1000));
            Assert.AreEqual(0, typing.CaretIndex);

            Assert.IsTrue(typing.ProcessKey('z', 1000), "every other key is still the right key under Mashing");
            Assert.AreEqual(1, typing.CaretIndex);
        }

        /// <summary>The live first-line lead-in (CONFIG bit 16) lets a press open the map's first line; a space is not one.</summary>
        [Test]
        public void ItDoesNotOpenTheFirstLine()
        {
            var typing = engine(catDog(), spaceless: true);
            typing.FirstLineLeadIn = true;

            Assert.IsTrue(typing.FirstLineTypingOpensAt(1000));
            Assert.IsFalse(typing.ProcessKey(' ', 1000));
            Assert.IsFalse(typing.LineIsActive);

            Assert.IsTrue(typing.ProcessKey('c', 1000));
            Assert.IsTrue(typing.LineIsActive);
        }

        /// <summary>
        /// On a FINISHED line the spacebar is the manual newline; under the mod it is not, and Enter
        /// still is, so the player is never stranded.
        /// </summary>
        [Test]
        public void TheManualNewlineIsEnterNotSpace()
        {
            LyricBeatmap twoLines() => map(catDogLine(), line("cow", 7000, 14000, 7300, 1000, unit("cow", 7000, 7300)));

            var control = engine(twoLines(), spaceless: false);
            control.ManualNewlines = true;
            control.Update(1000);
            double[] times = { 1000, 1100, 1200, 1300, 2000, 2100, 2200 };
            for (int i = 0; i < times.Length; i++)
                control.ProcessKey("cat dog"[i], times[i]);
            Assert.IsTrue(control.IsLineComplete);
            Assert.IsTrue(control.ProcessKey(' ', 2400), "the control: a space is the newline without the mod");
            Assert.AreEqual(1, control.ActiveLineIndex);

            var typing = engine(twoLines(), spaceless: true);
            typing.ManualNewlines = true;
            typing.Update(1000);
            typeCatDog(typing);
            Assert.IsTrue(typing.IsLineComplete);

            Assert.IsFalse(typing.ProcessKey(' ', 2400));
            Assert.AreEqual(0, typing.ActiveLineIndex);

            Assert.IsTrue(typing.ProcessEnter(2400));
            Assert.AreEqual(1, typing.ActiveLineIndex);
        }

        #endregion

        #region The word

        /// <summary>
        /// The completed space still separates two words, so CTRL+BACKSPACE erases the word the caret
        /// is in, not the whole line back to its head.
        /// </summary>
        [Test]
        public void EraseWordStopsAtTheCompletedSpace()
        {
            var typing = active(catDog());

            typing.ProcessKey('c', 1000);
            typing.ProcessKey('a', 1100);
            typing.ProcessKey('t', 1200);
            typing.ProcessKey('d', 2000);
            Assert.AreEqual(5, typing.CaretIndex);
            Assert.AreEqual(4, typing.WordBackspaceTarget, "mid-word: back to the head of 'dog'");

            // Composed exactly as the key handler composes it.
            int target = typing.WordBackspaceTarget;

            while (typing.CaretIndex > target && typing.ProcessBackspace())
            {
            }

            Assert.AreEqual(4, typing.CaretIndex);
            Assert.IsTrue(cells(typing).Take(3).All(c => c.State == CellState.Correct), "'cat' was not touched");

            // At a word's head the gesture takes the space and the word before it, as it does when
            // the space is typed.
            Assert.AreEqual(0, typing.WordBackspaceTarget);
        }

        [Test]
        public void SelectBackToTypoAnchorsOnTheTyposOwnWord()
        {
            var typing = active(catDog());

            typing.ProcessKey('c', 1000);
            typing.ProcessKey('a', 1100);
            typing.ProcessKey('t', 1200);
            typing.ProcessKey('x', 2000); // a typo on 'd', typed through
            typing.ProcessKey('o', 2100);

            Assert.AreEqual(CellState.Wrong, cells(typing)[4].State);
            Assert.AreEqual(4, typing.RetypeSelectionAnchor, "the selection starts at 'dog', not at the head of the line");
        }

        /// <summary>Dyslexia stays word-scoped: a letter of the next word is not reached across the completed space.</summary>
        [Test]
        public void DyslexiaStillScansOneWord()
        {
            var typing = active(catDog());
            typing.AnyOrderWithinWord = true;

            Assert.IsTrue(typing.ProcessKey('d', 1000));
            Assert.AreEqual(CellState.Wrong, cells(typing)[0].State, "a typo on the caret's cell");
            Assert.AreEqual(CellState.Untyped, cells(typing)[4].State, "the next word's 'd' was not reached");

            // ...while inside a word the letters still arrive in any order.
            var inWord = active(catDog());
            inWord.AnyOrderWithinWord = true;
            Assert.IsTrue(inWord.ProcessKey('t', 1100));
            Assert.AreEqual(CellState.Correct, cells(inWord)[2].State);
        }

        /// <summary>
        /// The pace underline (display only) still prices each WORD on its own: the completed space
        /// closes "cat" exactly as a typed one does, through the UI twin of the engine's word-gap
        /// rule. Without the mod the same line cuts at the same cell, so the bands are unchanged.
        /// </summary>
        [Test]
        public void ThePaceUnderlineStillCutsAtTheCompletedSpace()
        {
            var spaceless = active(catDog());
            var typed = active(catDog(), spaceless: false);

            Assert.IsTrue(spaceless.Lines[0].Spaceless);
            Assert.IsFalse(cells(spaceless)[3].IsTypeable, "the space is completed, not typed");

            var expected = new[] { (0, 4), (4, 7) };

            CollectionAssert.AreEqual(expected, UnderlinePace.SegmentLine(spaceless.Lines[0]).Select(s => (s.StartCell, s.EndCellExclusive)).ToArray(), "one band per word under the mod");
            CollectionAssert.AreEqual(expected, UnderlinePace.SegmentLine(typed.Lines[0]).Select(s => (s.StartCell, s.EndCellExclusive)).ToArray(), "and the same bands without it");

            // The cell-only overload has no line to ask, so it keeps the typeable-only rule.
            Assert.AreEqual(1, UnderlinePace.SegmentLine(cells(spaceless), 2300).Length);
        }

        #endregion

        #region Autoplay and stored runs

        private static TypeBeatBeatmap unstampedBeatmap()
        {
            var beatmap = new TypeBeatBeatmap();

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Line = catDogLine(),
                Granularity = TimingGranularity.Line,
            });

            return beatmap;
        }

        private static TypeBeatBeatmap replayBeatmap(bool spaceless)
        {
            var beatmap = unstampedBeatmap();

            // The mod's own stamp, in the window the game applies it in (after conversion, before
            // ApplyDefaults builds the nested objects).
            if (spaceless)
                new TypeBeatModSpaceless().ApplyToBeatmap(beatmap);

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

        private static int count(IReadOnlyDictionary<HitResult, int> statistics, HitResult result) => statistics.GetValueOrDefault(result);

        [Test]
        public void AutoplayNeverPressesSpaceAndPlaysPerfectly()
        {
            var beatmap = replayBeatmap(spaceless: true);
            var replay = new TypeBeatAutoGenerator(beatmap).Generate();
            var frames = replay.Frames.Cast<TypeBeatReplayFrame>().ToList();

            Assert.AreEqual("catdog", string.Concat(frames.Select(f => f.Character)));

            var account = score(beatmap, replay, new TypeBeatModSpaceless());

            Assert.AreEqual(0, account.UnconsumedFrames);
            Assert.AreEqual(6, count(account.Statistics, HitResult.Great));
            Assert.AreEqual(6, count(account.MaximumStatistics, HitResult.Great));
            Assert.AreEqual(0, count(account.Statistics, HitResult.Miss));
            Assert.AreEqual(6, account.MaxCombo);
            Assert.AreEqual(1.0, account.Accuracy, 1e-12);
            Assert.AreEqual(ScoreRank.X, account.Rank);

            // And the control: without the stamp autoplay still types the space.
            var plain = new TypeBeatAutoGenerator(replayBeatmap(spaceless: false)).Generate();
            Assert.AreEqual("cat dog", string.Concat(plain.Frames.Cast<TypeBeatReplayFrame>().Select(f => f.Character)));
        }

        /// <summary>
        /// THE MOD LIST IS THE MECHANISM, as for Dyslexia: no CONFIG bit, so the score's mod list is
        /// what tells the rescorer to complete the spaces and refuse the key. With it, a letters-only
        /// run re-derives clean, its frames survive the .osr encoding, and a space frame (which a
        /// live run never records, the engine having refused it) is refused again and changes
        /// nothing. Without it, the same letters are a different run.
        /// </summary>
        [Test]
        public void AStoredRunReDerivesExactlyWithTheModOnTheList()
        {
            var beatmap = replayBeatmap(spaceless: true);
            var letters = run((1000, 'c'), (1100, 'a'), (1200, 't'), (2000, 'd'), (2100, 'o'), (2200, 'g'));

            var clean = score(beatmap, letters, new TypeBeatModSpaceless());

            Assert.AreEqual(6, count(clean.Statistics, HitResult.Great));
            Assert.AreEqual(6, count(clean.MaximumStatistics, HitResult.Great));
            Assert.AreEqual(0, clean.Mistypes);
            Assert.AreEqual(6, clean.MaxCombo);
            Assert.AreEqual(1.0, clean.Accuracy, 1e-12);
            Assert.AreEqual(0, clean.UnconsumedFrames);

            // Re-derived twice, the account is identical (nothing in the path is stateful).
            var again = score(beatmap, letters, new TypeBeatModSpaceless());
            Assert.AreEqual(clean.TotalScore, again.TotalScore);
            Assert.AreEqual(clean.Accuracy, again.Accuracy, 1e-12);

            // The frames round-trip through the legacy (.osr) encoding unchanged.
            foreach (var frame in letters.Frames.Cast<TypeBeatReplayFrame>().Skip(1))
            {
                var restored = new TypeBeatReplayFrame();
                restored.FromLegacy(frame.ToLegacy(beatmap), beatmap);
                Assert.AreEqual(frame.Character, restored.Character);
            }

            // Space frames, mid-word and between the words: refused, so not one number moves.
            var withSpaces = run((1000, 'c'), (1050, ' '), (1100, 'a'), (1200, 't'), (1300, ' '), (2000, 'd'), (2100, 'o'), (2200, 'g'));
            var spaced = score(beatmap, withSpaces, new TypeBeatModSpaceless());

            CollectionAssert.AreEquivalent(clean.Statistics, spaced.Statistics);
            Assert.AreEqual(clean.MaxCombo, spaced.MaxCombo);
            Assert.AreEqual(clean.TotalScore, spaced.TotalScore);
            Assert.AreEqual(clean.Accuracy, spaced.Accuracy, 1e-12);
            // Fed like any other frame (the scorer counts a frame it handed the engine, refused or
            // not), so a stray space never marks the account untrustworthy either.
            Assert.AreEqual(0, spaced.UnconsumedFrames);

            // Without the mod (and its stamp) the same letters leave the space owed and missed.
            var withoutMod = score(replayBeatmap(spaceless: false), letters);
            Assert.AreEqual(7, count(withoutMod.MaximumStatistics, HitResult.Great));
            Assert.Less(withoutMod.Accuracy, clean.Accuracy);
        }

        /// <summary>The score's own multiplier is the 0.6x one, through the scorer the server runs.</summary>
        [Test]
        public void TheRescoredTotalCarriesTheMultiplier()
        {
            var beatmap = replayBeatmap(spaceless: true);
            var letters = run((1000, 'c'), (1100, 'a'), (1200, 't'), (2000, 'd'), (2100, 'o'), (2200, 'g'));

            var account = score(beatmap, letters, new TypeBeatModSpaceless());

            Assert.AreEqual(Math.Round(account.TotalScoreWithoutMods * 0.6), account.TotalScore, 1);
        }

        #endregion
    }
}
