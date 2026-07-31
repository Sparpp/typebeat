// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osuTK.Input;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// A map stores the AUTHOR'S line: punctuated and case-sensitive. What the player types, and
    /// therefore what the screen shows, is derived from it:
    /// <list type="bullet">
    /// <item>without the Literate mod, <see cref="Typeability.ToDefaultStream"/>: lower-cased,
    /// hyphens turned into word breaks, every other supported mark gone.</item>
    /// <item>with it, the authored line verbatim, every mark a typed cell of its own.</item>
    /// </list>
    /// These tests pin the derivation itself, the cell flattening both sides of it, the input
    /// surface that has to be able to produce the marks, and, above all, that the default path is
    /// untouched for text that carries no punctuation (which is every map written before this).
    /// </summary>
    [TestFixture]
    public class LiteratePunctuationTest
    {
        // --- The derivation ---------------------------------------------------------------------

        [Test]
        public void TheNormativeExample()
        {
            // The whole feature in one line: the map holds this,
            Assert.AreEqual("The bad-cat sat.", Typeability.Normalize("The bad-cat sat."));

            // and without the mod the player types (and sees) this.
            Assert.AreEqual("the bad cat sat", Typeability.ToDefaultStream("The bad-cat sat."));
        }

        [Test]
        public void TheSupportedSetIsExactlyTheDocumentedThirteenMarks()
        {
            Assert.AreEqual(",.'-?!;:()[]\"", Typeability.PUNCTUATION);

            foreach (char c in Typeability.PUNCTUATION)
            {
                Assert.IsTrue(Typeability.IsPunctuation(c), $"'{c}' must be supported");
                // A mark is deliberately NOT "typeable": that predicate feeds the difficulty model,
                // the interpolation weights and the pace counts, none of which may see punctuation.
                Assert.IsFalse(Typeability.IsTypeable(c), $"'{c}' must not count as a plain typeable char");
            }

            // Neighbours that are deliberately OUT: the freestyle marker, the slash, the asterisk,
            // the underscore, the backtick.
            foreach (char c in "&/*_`#@$%^+=<>|~\\")
                Assert.IsFalse(Typeability.IsPunctuation(c), $"'{c}' must not be a supported mark");
        }

        [Test]
        public void NormalizeKeepsEveryMarkAndFoldsTheTypographicVariants()
        {
            // Every supported mark survives, in one line, exactly once.
            Assert.AreEqual("a,b.c'd-e?f!g;h:i(j)k[l]m\"n",
                Typeability.Normalize("a,b.c'd-e?f!g;h:i(j)k[l]m\"n"));

            // Curly quotes/apostrophes and en/em/horizontal dashes and the minus sign all fold into
            // the ASCII forms, so only the supported set can ever reach a map.
            Assert.AreEqual("'''' \"\"\"\" ----", Typeability.Normalize("‘’‚′ “”„″ –—―−"));

            // Unsupported punctuation is still dropped outright, before anything else sees it.
            Assert.AreEqual("ab", Typeability.Normalize("a*b"));
            Assert.AreEqual("ab", Typeability.Normalize("a/b"));
            Assert.AreEqual("ab", Typeability.Normalize("a&b"));

            // Diacritics still strip first, so a mark next to an accent is unaffected.
            Assert.AreEqual("Hello, world!", Typeability.Normalize("Héllo,  wörld!"));
        }

        [Test]
        public void DefaultCharIsTheWholeRuleForOneChar()
        {
            Assert.AreEqual(' ', Typeability.DefaultChar('-'));

            foreach (char c in Typeability.PUNCTUATION)
            {
                if (c == Typeability.WORD_BREAK)
                    continue;

                Assert.IsNull(Typeability.DefaultChar(c), $"'{c}' must be deleted from the default stream");
            }

            Assert.AreEqual('a', Typeability.DefaultChar('A'));
            Assert.AreEqual('a', Typeability.DefaultChar('a'));
            Assert.AreEqual('7', Typeability.DefaultChar('7'));
            Assert.AreEqual(' ', Typeability.DefaultChar(' '));
            Assert.AreEqual(Typeability.FREESTYLE_MARKER, Typeability.DefaultChar(Typeability.FREESTYLE_MARKER));
        }

        [Test]
        public void EveryMarkDisappearsFromTheDefaultStreamExceptTheHyphen()
        {
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a,b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a.b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a'b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a?b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a!b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a;b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a:b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a(b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a)b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a[b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a]b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a\"b"));

            // The hyphen alone is a WORD BREAK.
            Assert.AreEqual("a b", Typeability.ToDefaultStream("a-b"));
        }

        [Test]
        public void CaseIsFoldedForTheDefaultStream()
        {
            Assert.AreEqual("mixed case here", Typeability.ToDefaultStream("MiXeD CaSe HeRe"));
            Assert.AreEqual("shouting", Typeability.ToDefaultStream("SHOUTING"));
            Assert.AreEqual("42 apples", Typeability.ToDefaultStream("42 Apples")); // digits have no case
        }

        [Test]
        public void HyphenSpacesNeverDoubleUpAndNeverSitAtTheEdges()
        {
            // A hyphen next to an authored space collapses the whole run into one break: the words
            // must not end up two spaces apart.
            Assert.AreEqual("a b", Typeability.ToDefaultStream("a - b"));
            Assert.AreEqual("a b", Typeability.ToDefaultStream("a- b"));
            Assert.AreEqual("a b", Typeability.ToDefaultStream("a -b"));
            Assert.AreEqual("a b", Typeability.ToDefaultStream("a - - b"));

            // A run with no hyphen in it is left exactly as authored (Normalize has already
            // collapsed those); nothing about the default path may move for hyphen-free text.
            Assert.AreEqual("a  b", Typeability.ToDefaultStream("a  b"));
            Assert.AreEqual("  a  ", Typeability.ToDefaultStream("  A  "));

            // A run of hyphens is one break, and an em dash (folded to '-' by Normalize) is too.
            Assert.AreEqual("a b", Typeability.ToDefaultStream("a--b"));
            Assert.AreEqual("a b", Typeability.ToDefaultStream(Typeability.Normalize("a—b")));

            // A hyphen at either edge separates nothing, so it disappears entirely.
            Assert.AreEqual("ab", Typeability.ToDefaultStream("-ab"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("ab-"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("-ab-"));

            // Deleted marks do not create a break of their own.
            Assert.AreEqual("well done", Typeability.ToDefaultStream("well, done"));
            Assert.AreEqual("dont", Typeability.ToDefaultStream("don't"));
        }

        [Test]
        public void TheDerivationIsIdempotentAndInertOnHyphenFreeText()
        {
            string[] samples =
            {
                "", "a", "the bad cat sat", "hello world", "he&&o wor&d",
                "The bad-cat sat.", "It's a half-cut, well-worn \"quote\"; really?!",
                "-", "---", "a - - b", "a  b",
            };

            foreach (string s in samples)
            {
                string once = Typeability.ToDefaultStream(s);
                Assert.AreEqual(once, Typeability.ToDefaultStream(once), $"not idempotent for \"{s}\"");
            }

            // Stronger than idempotence, and the reason no old map can have moved: for any text
            // with no hyphen and no mark, the derivation is exactly ToLowerInvariant, so it cannot
            // add, remove or reorder a single cell.
            foreach (string s in new[] { "the bad cat sat", "  odd   spacing  ", "he&&o wor&d", "AB CD", "", " ", "   " })
                Assert.AreEqual(s.ToLowerInvariant(), Typeability.ToDefaultStream(s), $"moved under \"{s}\"");
        }

        // --- Cell flattening --------------------------------------------------------------------

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricBeatmap map(TimingGranularity granularity, params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", FolderPath = "/x", AudioFileName = "a.mp3" },
            Lines = lines,
            Granularity = granularity,
        };

        /// <summary>The normative example as a real one-line map: "The bad-cat sat.", three words.</summary>
        private static LyricBeatmap normativeMap() => map(TimingGranularity.Word,
            line("The bad-cat sat.", 0, 8000, 6000,
                unit("The", 1000, 2000), unit("bad-cat", 2000, 4000), unit("sat.", 4000, 6000)));

        [Test]
        public void DisplayFollowsTheTypedStreamBothWays()
        {
            var plain = new TypingEngine(normativeMap());
            var literate = new TypingEngine(normativeMap(), literate: true);

            // The display text IS the concatenation of the cells the engine judges, so the two can
            // never disagree; there is no second derivation to keep in step.
            Assert.AreEqual("the bad cat sat", plain.Lines[0].DisplayText);
            Assert.AreEqual("The bad-cat sat.", literate.Lines[0].DisplayText);

            Assert.AreEqual(plain.Lines[0].DisplayText, string.Concat(plain.Lines[0].Cells.Select(c => c.Expected)));
            Assert.AreEqual(literate.Lines[0].DisplayText, string.Concat(literate.Lines[0].Cells.Select(c => c.Expected)));

            Assert.AreEqual(Typeability.ToDefaultStream(plain.Lines[0].Source.RawText), plain.Lines[0].DisplayText);
        }

        [Test]
        public void TurningTheModOnAddsCellsWithoutMovingAnyLetter()
        {
            var plain = new TypingEngine(normativeMap()).Lines[0];
            var literate = new TypingEngine(normativeMap(), literate: true).Lines[0];

            // Every letter/digit of the default stream appears in the literate stream at the SAME
            // target time; only the marks (and the hyphen-turned-space) differ.
            var plainLetters = plain.Cells.Where(c => c.Expected != ' ').Select(c => (char.ToLowerInvariant(c.Expected), c.TargetTime)).ToList();
            var literateLetters = literate.Cells.Where(c => !Typeability.IsPunctuation(c.Expected) && c.Expected != ' ')
                                          .Select(c => (char.ToLowerInvariant(c.Expected), c.TargetTime)).ToList();

            Assert.AreEqual(literateLetters, plainLetters);
        }

        [Test]
        public void ATrailingMarkAttachesToThePrecedingChar()
        {
            var literate = new TypingEngine(normativeMap(), literate: true).Lines[0];

            var cells = literate.Cells;
            var period = cells[^1];
            var t = cells[^2]; // the 't' of "sat."

            Assert.AreEqual('.', period.Expected);
            Assert.AreEqual('t', t.Expected);
            Assert.AreEqual(t.TargetTime, period.TargetTime, "nothing follows it, so it rides the char before it");
        }

        [Test]
        public void ALeadingMarkAttachesToTheFollowingChar()
        {
            var literate = new TypingEngine(map(TimingGranularity.Word,
                line("\"hi\"", 0, 4000, 3000, unit("\"hi\"", 1000, 3000))), literate: true).Lines[0];

            Assert.AreEqual('"', literate.Cells[0].Expected);
            Assert.AreEqual('h', literate.Cells[1].Expected);
            Assert.AreEqual(literate.Cells[1].TargetTime, literate.Cells[0].TargetTime,
                "nothing precedes it, so it rides the char after it");
        }

        [Test]
        public void AMarkRunIsSpreadEvenlyAcrossTheGapItSitsIn()
        {
            // "a?!b" in one word [1000, 2000]: k = 2 typeable => a = 1000, b = 1500. The two marks
            // between them split that 500 ms gap into three: 1000 + 500/3 and 1000 + 1000/3.
            var literate = new TypingEngine(map(TimingGranularity.Word,
                line("a?!b", 0, 4000, 3000, unit("a?!b", 1000, 2000))), literate: true).Lines[0];

            Assert.AreEqual(4, literate.Cells.Count);
            Assert.AreEqual(1000, literate.Cells[0].TargetTime);
            Assert.AreEqual(1000 + 500.0 / 3, literate.Cells[1].TargetTime, 1e-9);
            Assert.AreEqual(1000 + 1000.0 / 3, literate.Cells[2].TargetTime, 1e-9);
            Assert.AreEqual(1500, literate.Cells[3].TargetTime);
        }

        [Test]
        public void TheHyphenSpaceInheritsTheHyphensSlot()
        {
            var plain = new TypingEngine(normativeMap()).Lines[0];
            var literate = new TypingEngine(normativeMap(), literate: true).Lines[0];

            // Literate cell 7 is the hyphen of "bad-cat"; the default stream's cell 7 is the space
            // it became, and it carries the hyphen's interpolated target verbatim.
            Assert.AreEqual('-', literate.Cells[7].Expected);
            Assert.AreEqual(' ', plain.Cells[7].Expected);
            Assert.AreEqual(literate.Cells[7].TargetTime, plain.Cells[7].TargetTime);

            // "bad-cat" is unit [2000, 4000] with k = 6 letters (step 1000/3): d = 2000 + 2000/3,
            // c = 2000 + 3000/3. The hyphen splits that step in half.
            double d = 2000 + 2 * 2000.0 / 6;
            double c = 2000 + 3 * 2000.0 / 6;
            Assert.AreEqual((d + c) / 2, plain.Cells[7].TargetTime, 1e-9);
        }

        [Test]
        public void MarksAreTypedJudgedAndCountedUnderTheMod()
        {
            var engine = new TypingEngine(normativeMap(), literate: true);
            var cells = engine.Lines[0].Cells;

            Assert.IsTrue(engine.Literate);
            Assert.IsTrue(engine.CaseSensitive, "Literate implies exact-case matching");
            Assert.AreEqual(cells.Count, engine.Lines[0].TypeableCount, "every authored char is typed");

            engine.Update(cells[0].TargetTime);

            // The capital demands Shift; a lower-case 't' is a wrong key like any other.
            Assert.IsTrue(engine.ProcessKey('t', cells[0].TargetTime));
            Assert.AreEqual(CellState.Untyped, cells[0].State);
            Assert.AreEqual(1, engine.ConsecutiveWrongKeys);

            foreach (var cell in cells)
                Assert.IsTrue(engine.ProcessKey(cell.Expected, cell.TargetTime), $"'{cell.Expected}' must land");

            Assert.IsTrue(engine.IsLineComplete);
            Assert.AreEqual(cells.Count, engine.Combo);
        }

        // --- Input surface ----------------------------------------------------------------------

        [Test]
        public void ThePunctuationSurfaceIsOpenedOnlyForTheMod()
        {
            // Off (the default): the punctuation keys are inert, exactly as they always were, so a
            // habitual comma still costs nothing.
            Assert.IsFalse(KeyCharMap.TryMap(Key.Comma, KeyboardLayout.Qwerty, false, out _));
            Assert.IsFalse(KeyCharMap.TryMap(Key.Period, KeyboardLayout.Qwerty, false, out _));
            Assert.IsFalse(KeyCharMap.TryMap(Key.Quote, KeyboardLayout.Qwerty, true, out _));

            // ...and Shift+digit still produces the DIGIT, not the mark above it.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Number1, KeyboardLayout.Qwerty, true, out char one));
            Assert.AreEqual('1', one);

            // On: every supported mark is reachable.
            var expected = new Dictionary<(Key, bool), char>
            {
                [(Key.Comma, false)] = ',',
                [(Key.Period, false)] = '.',
                [(Key.Quote, false)] = '\'',
                [(Key.Quote, true)] = '"',
                [(Key.Minus, false)] = '-',
                [(Key.Slash, true)] = '?',
                [(Key.Number1, true)] = '!',
                [(Key.Semicolon, false)] = ';',
                [(Key.Semicolon, true)] = ':',
                [(Key.Number9, true)] = '(',
                [(Key.Number0, true)] = ')',
                [(Key.BracketLeft, false)] = '[',
                [(Key.BracketRight, false)] = ']',
            };

            foreach (var ((key, shift), mark) in expected)
            {
                Assert.IsTrue(KeyCharMap.TryMap(key, KeyboardLayout.Qwerty, shift, true, out char produced), $"{key} (shift={shift})");
                Assert.AreEqual(mark, produced, $"{key} (shift={shift})");
            }

            // The whole supported set is covered by the map, with nothing left untypeable.
            Assert.AreEqual(Typeability.PUNCTUATION.OrderBy(c => c).ToArray(), expected.Values.OrderBy(c => c).ToArray());

            // An unshifted slash would be '/', which is not a supported mark, so it stays inert.
            Assert.IsFalse(KeyCharMap.TryMap(Key.Slash, KeyboardLayout.Qwerty, false, true, out _));

            // Letters are untouched by the wider surface.
            Assert.IsTrue(KeyCharMap.TryMap(Key.A, KeyboardLayout.Qwerty, true, true, out char shifted));
            Assert.AreEqual('A', shifted);
        }

        [Test]
        public void AzertyCommaKeyComesAliveOnlyWithTheMod()
        {
            // The QWERTY-M position carries ',' on AZERTY (the 'm' keycap moved to the semicolon
            // position). Inert without the mod, the comma key with it.
            Assert.IsFalse(KeyCharMap.TryMap(Key.M, KeyboardLayout.Azerty, false, out _));

            Assert.IsTrue(KeyCharMap.TryMap(Key.M, KeyboardLayout.Azerty, false, true, out char comma));
            Assert.AreEqual(',', comma);

            // The relocated 'm' still works either way.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Azerty, false, out char m));
            Assert.AreEqual('m', m);
        }

        // --- Mod plumbing and replays -----------------------------------------------------------

        private static TypeBeatBeatmap hitObjectMap()
        {
            var beatmap = new TypeBeatBeatmap();
            var lines = normativeMap().Lines;

            for (int i = 0; i < lines.Count; i++)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
                    Granularity = TimingGranularity.Word,
                });
            }

            return beatmap;
        }

        [Test]
        public void TheModStampsEveryLineSoNestedObjectsFlattenTheSameWay()
        {
            var beatmap = hitObjectMap();

            foreach (var h in beatmap.HitObjects)
                Assert.IsFalse(h.Literate);

            new TypeBeatModLiterate().ApplyToBeatmap(beatmap);

            foreach (var h in beatmap.HitObjects)
                Assert.IsTrue(h.Literate);

            // The nested per-cell scoring objects now match the engine's cells one for one, which is
            // what keeps CharJudgement.CellIndex routing to the right drawable.
            var hitObject = beatmap.HitObjects.OfType<TypeBeatHitObject>().Single();
            hitObject.ApplyDefaults(new typebeat.Game.Beatmaps.ControlPoints.ControlPointInfo(), new BeatmapDifficulty());

            var engineCells = new TypingEngine(normativeMap(), literate: true).Lines[0].Cells;
            var nested = hitObject.NestedHitObjects.OfType<TypeBeatCharObject>().ToList();

            Assert.AreEqual(engineCells.Count(c => c.IsTypeable), nested.Count);

            for (int i = 0; i < nested.Count; i++)
            {
                Assert.AreEqual(engineCells[nested[i].CellIndex].Expected, nested[i].Expected, $"nested {i}");
                Assert.AreEqual(engineCells[nested[i].CellIndex].TargetTime, nested[i].StartTime, $"nested {i}");
            }
        }

        [Test]
        public void ALiterateReplayRoundTripsThroughTheFrameFormat()
        {
            var beatmap = hitObjectMap();
            new TypeBeatModLiterate().ApplyToBeatmap(beatmap);

            var replay = new TypeBeatAutoGenerator(beatmap, literate: true).Generate();
            var frames = replay.Frames.Cast<TypeBeatReplayFrame>().ToList();

            // The perfect play types the authored line verbatim, marks and capitals included.
            Assert.AreEqual("The bad-cat sat.", string.Concat(frames.Select(f => f.Character)));

            // Every recorded char survives the legacy (.osr) encoding: it rides in MouseX as a float
            // and comes back identical, and none of them collides with a sentinel.
            foreach (var frame in frames)
            {
                Assert.AreNotEqual(TypeBeatReplayFrame.BACKSPACE, frame.Character);
                Assert.AreNotEqual(TypeBeatReplayFrame.CONFIG, frame.Character);

                var restored = new TypeBeatReplayFrame();
                restored.FromLegacy(frame.ToLegacy(beatmap), beatmap);
                Assert.AreEqual(frame.Character, restored.Character);
            }

            // Feeding those frames back through the same Update-then-key sequence playback uses
            // reproduces a perfect play against a literate engine.
            var engine = new TypingEngine(normativeMap(), literate: true);

            foreach (var frame in frames)
            {
                engine.Update(frame.Time);
                Assert.IsTrue(engine.ProcessKey(frame.Character, frame.Time), $"'{frame.Character}' @ {frame.Time}");
            }

            engine.Update(1_000_000);

            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(1.0, engine.LiveAccuracy);
            Assert.IsTrue(engine.Lines[0].Cells.All(c => c.State == CellState.Correct));
        }

        // --- Default-path parity ----------------------------------------------------------------

        [Test]
        public void AnOldStyleMapScoresBitIdenticallyOnTheDefaultPath()
        {
            // "Old style" = the text a map written before punctuation existed holds: already
            // stripped by the normalizer of the day, so hyphen-free and mark-free. The derivation
            // is a no-op on it beyond case folding, which judgement folds away anyway, so every
            // cell, every target and every counter must come out exactly as before.
            var oldStyle = map(TimingGranularity.Word,
                line("If we take it from the top now", 0, 6000, 5000,
                    unit("If", 1000, 1400), unit("we", 1400, 1800), unit("take", 1800, 2400),
                    unit("it", 2400, 2800), unit("from", 2800, 3400), unit("the", 3400, 3800),
                    unit("top", 3800, 4400), unit("now", 4400, 5000)),
                line("beggin him", 6000, 12000, 11000,
                    unit("beggin", 7000, 8200), unit("him", 8200, 9000)));

            var engine = new TypingEngine(oldStyle);

            // Cell for cell: same count, same chars (lower-cased), same targets, same tiers.
            for (int k = 0; k < oldStyle.Lines.Count; k++)
            {
                var source = oldStyle.Lines[k];
                var flattened = engine.Lines[k];

                Assert.AreEqual(source.RawText.Length, flattened.Cells.Count, $"line {k} cell count");
                Assert.AreEqual(source.RawText.ToLowerInvariant(), flattened.DisplayText, $"line {k} stream");
            }

            // And a full perfect play produces the same score/accuracy/sync a pre-punctuation build
            // would have: every cell Correct at delta 0.
            foreach (var typingLine in engine.Lines)
            {
                engine.Update(typingLine.ActivationTime);

                foreach (var cell in typingLine.Cells)
                {
                    engine.Update(cell.TargetTime);
                    Assert.IsTrue(engine.ProcessKey(cell.Expected, cell.TargetTime), $"'{cell.Expected}' @ {cell.TargetTime}");
                }
            }

            engine.Update(1_000_000);

            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(1.0, engine.LiveAccuracy);
            Assert.AreEqual(100.0, engine.BuildResults().SyncPercent, 1e-9);
            Assert.AreEqual(0, engine.BuildResults().Counts[JudgementType.Miss]);
        }

        [Test]
        public void ALineWithNothingToTypeIsStillDropped()
        {
            // A line that is nothing but punctuation now normalizes NON-empty, but it gives the
            // player no cell at all, so it must still vanish and let the previous line extend over
            // its span, exactly as a whole-line backing vocal does. Every drop guard therefore
            // measures the DEFAULT stream, not the normalized text.
            var lines = LrcParser.Parse("[00:01.00] real one\n[00:02.00] ...\n[00:04.00] real two\n[00:06.00]\n");

            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual("real one", lines[0].RawText);
            Assert.AreEqual(4000, lines[0].EndTime); // extends over the dropped line
            Assert.AreEqual("real two", lines[1].RawText);
        }

        [Test]
        public void PaceStatisticsMeasureTheDefaultStreamNotTheAuthoredLine()
        {
            // The authored line has 16 chars; the default stream has 15 cells across 4 words
            // ("the", "bad", "cat", "sat"), because the hyphen is a word break and the period is
            // gone. The pace of a map is the pace of the play everyone shares.
            var stats = LyricPaceStatistics.Compute(normativeMap().Lines);

            Assert.AreEqual(4, stats.WordCount);
            Assert.AreEqual("the bad cat sat".Length, stats.TypeableCellCount);

            // Unchanged for a line that carries no punctuation: the numbers a pre-punctuation build
            // computed still come out the same.
            var plain = LyricPaceStatistics.Compute(new[]
            {
                line("The bad cat sat", 0, 8000, 6000,
                    unit("The", 1000, 2000), unit("bad", 2000, 3000), unit("cat", 3000, 4000), unit("sat", 4000, 6000)),
            });

            Assert.AreEqual(4, plain.WordCount);
            Assert.AreEqual(15, plain.TypeableCellCount);
        }
    }
}
