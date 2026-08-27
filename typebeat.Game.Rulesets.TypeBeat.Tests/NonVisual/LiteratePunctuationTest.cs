// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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
        public void TheSupportedSetIsExactlyTheDocumentedTwentyMarks()
        {
            Assert.AreEqual(",.'-?!;:()[]\"$%^*<>/", Typeability.PUNCTUATION);
            Assert.AreEqual(20, Typeability.PUNCTUATION.Length);
            Assert.AreEqual(20, Typeability.PUNCTUATION.Distinct().Count(), "no mark may be listed twice");

            foreach (char c in Typeability.PUNCTUATION)
            {
                Assert.IsTrue(Typeability.IsPunctuation(c), $"'{c}' must be supported");
                // A mark is deliberately NOT "typeable": that predicate feeds the difficulty model,
                // the interpolation weights and the pace counts, none of which may see punctuation.
                Assert.IsFalse(Typeability.IsTypeable(c), $"'{c}' must not count as a plain typeable char");
            }

            // Neighbours that are deliberately OUT: the freestyle marker, the split marker, the
            // underscore, the backtick, the hash, the at sign, plus/equals, tilde, backslash.
            // Backlog 202 moved '$', '%', '^', '*', '<', '>' and '/' out of this list and into the
            // supported set above.
            foreach (char c in "&_`#@+=|~\\")
                Assert.IsFalse(Typeability.IsPunctuation(c), $"'{c}' must not be a supported mark");
        }

        [Test]
        public void NormalizeKeepsEveryMarkAndFoldsTheTypographicVariants()
        {
            // Every supported mark survives, in one line, exactly once.
            Assert.AreEqual("a,b.c'd-e?f!g;h:i(j)k[l]m\"n$o%p^q*r<s>t/u",
                Typeability.Normalize("a,b.c'd-e?f!g;h:i(j)k[l]m\"n$o%p^q*r<s>t/u"));

            // Curly quotes/apostrophes and en/em/horizontal dashes and the minus sign all fold into
            // the ASCII forms, so only the supported set can ever reach a map.
            Assert.AreEqual("'''' \"\"\"\" ----", Typeability.Normalize("‘’‚′ “”„″ –—―−"));

            // The asterisk and the slash are supported since backlog 202, so they now survive.
            Assert.AreEqual("a*b", Typeability.Normalize("a*b"));
            Assert.AreEqual("a/b", Typeability.Normalize("a/b"));

            // Unsupported punctuation is still dropped outright, before anything else sees it.
            Assert.AreEqual("ab", Typeability.Normalize("a&b"));
            Assert.AreEqual("ab", Typeability.Normalize("a_b"));
            Assert.AreEqual("ab", Typeability.Normalize("a#b"));

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

            // The seven marks backlog 202 added disappear exactly like the rest of them: only the
            // hyphen is special, and nothing about the default stream moved for them.
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a$b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a%b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a^b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a*b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a<b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a>b"));
            Assert.AreEqual("ab", Typeability.ToDefaultStream("a/b"));

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
            // Gatekeeper on: the point here is that a mark/capital is a REAL target, which reads
            // most sharply on the model that refuses anything else (backlog 107 made typing-through
            // the default, and typing through would consume the cell instead of holding it).
            var engine = new TypingEngine(normativeMap(), literate: true) { AllowWrongInput = false };
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

            Assert.IsFalse(KeyCharMap.TryMap(Key.Slash, KeyboardLayout.Qwerty, false, out _));
            Assert.IsFalse(KeyCharMap.TryMap(Key.Comma, KeyboardLayout.Qwerty, true, out _));
            Assert.IsFalse(KeyCharMap.TryMap(Key.Period, KeyboardLayout.Qwerty, true, out _));

            // ...and Shift+digit still produces the DIGIT, not the mark above it.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Number1, KeyboardLayout.Qwerty, true, out char one));
            Assert.AreEqual('1', one);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Number4, KeyboardLayout.Qwerty, true, out char four));
            Assert.AreEqual('4', four);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Number8, KeyboardLayout.Qwerty, true, out char eight));
            Assert.AreEqual('8', eight);

            // On: every supported mark is reachable.
            var expected = new Dictionary<(Key, bool), char>
            {
                [(Key.Comma, false)] = ',',
                [(Key.Period, false)] = '.',
                [(Key.Quote, false)] = '\'',
                [(Key.Quote, true)] = '"',
                [(Key.Minus, false)] = '-',
                [(Key.Slash, true)] = '?',
                [(Key.Slash, false)] = '/',
                [(Key.Number1, true)] = '!',
                [(Key.Semicolon, false)] = ';',
                [(Key.Semicolon, true)] = ':',
                [(Key.Number9, true)] = '(',
                [(Key.Number0, true)] = ')',
                [(Key.BracketLeft, false)] = '[',
                [(Key.BracketRight, false)] = ']',
                [(Key.Number4, true)] = '$',
                [(Key.Number5, true)] = '%',
                [(Key.Number6, true)] = '^',
                [(Key.Number8, true)] = '*',
                [(Key.Comma, true)] = '<',
                [(Key.Period, true)] = '>',
            };

            foreach (var ((key, shift), mark) in expected)
            {
                Assert.IsTrue(KeyCharMap.TryMap(key, KeyboardLayout.Qwerty, shift, true, out char produced), $"{key} (shift={shift})");
                Assert.AreEqual(mark, produced, $"{key} (shift={shift})");
            }

            // The whole supported set is covered by the map, with nothing left untypeable.
            Assert.AreEqual(Typeability.PUNCTUATION.OrderBy(c => c).ToArray(), expected.Values.OrderBy(c => c).ToArray());

            // The unshifted slash is '/', a supported mark since backlog 202, so the key is live
            // under the mod (and, per the assert at the top of this test, still inert without it).
            Assert.IsTrue(KeyCharMap.TryMap(Key.Slash, KeyboardLayout.Qwerty, false, true, out char slash));
            Assert.AreEqual('/', slash);
            Assert.IsFalse(KeyCharMap.TryMap(Key.Slash, KeyboardLayout.Qwerty, false, out _));

            // Keys with no mark on either legend stay inert even under the mod.
            Assert.IsFalse(KeyCharMap.TryMap(Key.Minus, KeyboardLayout.Qwerty, true, true, out _));
            Assert.IsFalse(KeyCharMap.TryMap(Key.BracketLeft, KeyboardLayout.Qwerty, true, true, out _));

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

            // ...including with the surface open, which is the whole of backlog 214: the US ';'
            // legend for that position must not shadow the keycap the AZERTY player reads.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Azerty, false, true, out char literateM));
            Assert.AreEqual('m', literateM);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Azerty, true, true, out char literateCapitalM));
            Assert.AreEqual('M', literateCapitalM);
        }

        /// <summary>
        /// Backlog 214. AZERTY's bottom row sits one position left of the US one, so five physical
        /// positions carry a different legend. Under Literate each must produce what the player
        /// reads on the keycap, or produce nothing: a mark the keycap does not show is a wrong key,
        /// which is a combo break, a typo and HP drain rather than a miss.
        /// </summary>
        [Test]
        public void AzertyBottomRowCarriesItsOwnLegendsUnderTheMod()
        {
            var expected = new Dictionary<(Key, bool), char>
            {
                // The four corrected punctuation positions, unshifted then shifted.
                [(Key.M, false)] = ',',
                [(Key.M, true)] = '?',
                [(Key.Comma, false)] = ';',
                [(Key.Comma, true)] = '.',
                [(Key.Period, false)] = ':',
                [(Key.Period, true)] = '/',
                [(Key.Slash, false)] = '!',
                // The ISO key AZERTY has and US QWERTY does not, which is where the angle
                // brackets live once Comma and Period carry their French legends.
                [(Key.NonUSBackSlash, false)] = '<',
                [(Key.NonUSBackSlash, true)] = '>',
            };

            foreach (var ((key, shift), mark) in expected)
            {
                Assert.IsTrue(KeyCharMap.TryMap(key, KeyboardLayout.Azerty, shift, true, out char produced), $"{key} (shift={shift})");
                Assert.AreEqual(mark, produced, $"{key} (shift={shift})");

                // Every one of them is still inert without the mod, exactly like the US table's
                // punctuation keys: a habitual comma never costs an AZERTY player anything.
                Assert.IsFalse(KeyCharMap.TryMap(key, KeyboardLayout.Azerty, shift, false, out _), $"{key} (shift={shift}) without the mod");
            }

            // Shift on the '!' key is the section sign, outside the supported set, so the key
            // stays inert for that modifier state rather than producing the US legend's '?'.
            Assert.IsFalse(KeyCharMap.TryMap(Key.Slash, KeyboardLayout.Azerty, true, true, out _));

            // Nothing about the correction reaches QWERTY, which keeps every US legend involved.
            // (QWERTZ has a table of its own since backlog 216, pinned below.)
            Assert.IsTrue(KeyCharMap.TryMap(Key.Comma, KeyboardLayout.Qwerty, false, true, out char usComma));
            Assert.AreEqual(',', usComma);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Period, KeyboardLayout.Qwerty, true, true, out char greater));
            Assert.AreEqual('>', greater);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Slash, KeyboardLayout.Qwerty, true, true, out char question));
            Assert.AreEqual('?', question);

            Assert.IsFalse(KeyCharMap.TryMap(Key.NonUSBackSlash, KeyboardLayout.Qwerty, false, true, out _));
            Assert.IsFalse(KeyCharMap.TryMap(Key.NonUSBackSlash, KeyboardLayout.Qwerty, true, true, out _));
        }

        /// <summary>
        /// Backlog 215. AZERTY's digit row is the REVERSE of the US one: the marks are unshifted
        /// and the digits are shifted. The apostrophe on the 4 key is the one that mattered, since
        /// a lyric without a "don't" or an "I'm" in it is the exception.
        /// </summary>
        [Test]
        public void AzertyDigitRowIsMarksUnshiftedAndDigitsShifted()
        {
            var marks = new Dictionary<Key, char>
            {
                [Key.Number3] = '"',
                [Key.Number4] = '\'',
                [Key.Number5] = '(',
                [Key.Number6] = '-',
                [Key.Minus] = ')', // the key immediately right of 0
            };

            foreach (var (key, mark) in marks)
            {
                Assert.IsTrue(KeyCharMap.TryMap(key, KeyboardLayout.Azerty, false, true, out char produced), $"{key}");
                Assert.AreEqual(mark, produced, $"{key}");
            }

            // The six whose unshifted legend is outside the supported set ('&', the accented
            // letters, '_') produce NOTHING rather than falling through to the digit their keycap
            // shows only on Shift: a digit the player never asked for is a wrong key like any other.
            foreach (var key in new[] { Key.Number1, Key.Number2, Key.Number7, Key.Number8, Key.Number9, Key.Number0 })
                Assert.IsFalse(KeyCharMap.TryMap(key, KeyboardLayout.Azerty, false, true, out _), $"{key}");

            // Shift is the digit, on all ten.
            for (int d = 0; d <= 9; d++)
            {
                Assert.IsTrue(KeyCharMap.TryMap(Key.Number0 + d, KeyboardLayout.Azerty, true, true, out char digit), $"digit {d}");
                Assert.AreEqual((char)('0' + d), digit, $"digit {d}");
            }

            // Shift on the ')' key is the degree sign, outside the supported set, so it stays inert
            // rather than producing the US legend's '-' (which lives on the 6 key here).
            Assert.IsFalse(KeyCharMap.TryMap(Key.Minus, KeyboardLayout.Azerty, true, true, out _));

            // WITHOUT the mod nothing about the row moves: every digit key is its digit under both
            // modifiers exactly as before, and the ')' key is inert. This is load-bearing, the
            // punctuation surface only ever opens under Literate.
            foreach (bool shift in new[] { false, true })
            {
                for (int d = 0; d <= 9; d++)
                {
                    Assert.IsTrue(KeyCharMap.TryMap(Key.Number0 + d, KeyboardLayout.Azerty, shift, false, out char digit), $"digit {d} shift={shift}");
                    Assert.AreEqual((char)('0' + d), digit, $"digit {d} shift={shift}");
                }

                Assert.IsFalse(KeyCharMap.TryMap(Key.Minus, KeyboardLayout.Azerty, shift, false, out _), $"minus shift={shift}");
            }
        }

        /// <summary>
        /// Backlog 215. The marks the reversed digit row displaced ('$', '%', '^', '*') all have
        /// real French homes of their own, so they move there rather than being parked: the rule is
        /// still that a position only ever yields what the keycap in front of the player shows.
        /// </summary>
        [Test]
        public void AzertyRelocatedMarksSitOnTheirFrenchKeycaps()
        {
            var expected = new Dictionary<(Key, bool), char>
            {
                // Top row: '$' on the US-BracketRight position, the circumflex legend on the
                // US-BracketLeft one.
                [(Key.BracketRight, false)] = '$',
                [(Key.BracketLeft, false)] = '^',
                // Home row: the u-grave keycap is '%' shifted, and the US-BackSlash position is '*'.
                [(Key.Quote, true)] = '%',
                [(Key.BackSlash, false)] = '*',
            };

            foreach (var ((key, shift), mark) in expected)
            {
                Assert.IsTrue(KeyCharMap.TryMap(key, KeyboardLayout.Azerty, shift, true, out char produced), $"{key} (shift={shift})");
                Assert.AreEqual(mark, produced, $"{key} (shift={shift})");

                Assert.IsFalse(KeyCharMap.TryMap(key, KeyboardLayout.Azerty, shift, false, out _), $"{key} (shift={shift}) without the mod");
            }

            // The US Quote position is the u-grave keycap here, outside the supported set, so it no
            // longer hands an AZERTY player the apostrophe (and the '"' above it): both moved to the
            // digit row, where the French keycaps actually show them.
            Assert.IsFalse(KeyCharMap.TryMap(Key.Quote, KeyboardLayout.Azerty, false, true, out _));

            // The micro sign above '*' is outside the set too.
            Assert.IsFalse(KeyCharMap.TryMap(Key.BackSlash, KeyboardLayout.Azerty, true, true, out _));

            // None of it reaches QWERTY, which keeps every US legend involved.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Quote, KeyboardLayout.Qwerty, false, true, out char quote));
            Assert.AreEqual('\'', quote);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Quote, KeyboardLayout.Qwerty, true, true, out char doubleQuote));
            Assert.AreEqual('"', doubleQuote);

            Assert.IsTrue(KeyCharMap.TryMap(Key.BracketLeft, KeyboardLayout.Qwerty, false, true, out char open));
            Assert.AreEqual('[', open);

            Assert.IsTrue(KeyCharMap.TryMap(Key.BracketRight, KeyboardLayout.Qwerty, false, true, out char close));
            Assert.AreEqual(']', close);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Minus, KeyboardLayout.Qwerty, false, true, out char hyphen));
            Assert.AreEqual('-', hyphen);

            Assert.IsFalse(KeyCharMap.TryMap(Key.BracketLeft, KeyboardLayout.Qwerty, true, true, out _));
            Assert.IsFalse(KeyCharMap.TryMap(Key.BracketRight, KeyboardLayout.Qwerty, true, true, out _));
            Assert.IsFalse(KeyCharMap.TryMap(Key.BackSlash, KeyboardLayout.Qwerty, false, true, out _));
        }

        /// <summary>
        /// The two deliberate exceptions to the keycap rule. '[' and ']' are AltGr-only on AZERTY
        /// and AltGr is not modelled at all, so they are parked on the SHIFTED US bracket positions,
        /// whose real legends (the diaeresis dead key and the pound sign) are outside the supported
        /// set and so displace nothing faithful. Parking beats stranding: a mark with no key makes
        /// every map containing it uncompletable, which is the whole bug class, while a mark on a
        /// spare shifted legend is merely undiscoverable.
        /// </summary>
        [Test]
        public void AzertyParksTheBracketsOnTheSpareShiftedLegends()
        {
            Assert.IsTrue(KeyCharMap.TryMap(Key.BracketLeft, KeyboardLayout.Azerty, true, true, out char open));
            Assert.AreEqual('[', open);

            Assert.IsTrue(KeyCharMap.TryMap(Key.BracketRight, KeyboardLayout.Azerty, true, true, out char close));
            Assert.AreEqual(']', close);

            // Inert without the mod, like every other punctuation position.
            Assert.IsFalse(KeyCharMap.TryMap(Key.BracketLeft, KeyboardLayout.Azerty, true, false, out _));
            Assert.IsFalse(KeyCharMap.TryMap(Key.BracketRight, KeyboardLayout.Azerty, true, false, out _));
        }

        /// <summary>
        /// Every position the US punctuation surface claims, pinned key by key, with the assertion
        /// that EVERY other position falls straight through to the plain letter/digit map. The
        /// corrections are per LAYOUT, so QWERTY is the one layout that must come out of all three
        /// of them byte for byte unchanged, and this is the pin that says so. (It used to be stated
        /// as "QWERTY and QWERTZ are equal", which backlog 216 retired: QWERTZ now has German
        /// keycaps and a table of its own.)
        /// </summary>
        [Test]
        public void TheUsTableIsPinnedKeyByKey()
        {
            var usSurface = new Dictionary<(Key, bool), char>
            {
                [(Key.Comma, false)] = ',',
                [(Key.Comma, true)] = '<',
                [(Key.Period, false)] = '.',
                [(Key.Period, true)] = '>',
                [(Key.Quote, false)] = '\'',
                [(Key.Quote, true)] = '"',
                [(Key.Minus, false)] = '-',
                [(Key.Slash, false)] = '/',
                [(Key.Slash, true)] = '?',
                [(Key.Semicolon, false)] = ';',
                [(Key.Semicolon, true)] = ':',
                [(Key.BracketLeft, false)] = '[',
                [(Key.BracketRight, false)] = ']',
                [(Key.Number1, true)] = '!',
                [(Key.Number4, true)] = '$',
                [(Key.Number5, true)] = '%',
                [(Key.Number6, true)] = '^',
                [(Key.Number8, true)] = '*',
                [(Key.Number9, true)] = '(',
                [(Key.Number0, true)] = ')',
            };

            // The 20 claimed states are exactly the 20 supported marks, one each.
            Assert.AreEqual(Typeability.PUNCTUATION.OrderBy(c => c).ToArray(), usSurface.Values.OrderBy(c => c).ToArray());

            foreach (Key key in Enum.GetValues<Key>())
            {
                foreach (bool shift in new[] { false, true })
                {
                    bool got = KeyCharMap.TryMap(key, KeyboardLayout.Qwerty, shift, true, out char c);
                    bool plain = KeyCharMap.TryMap(key, KeyboardLayout.Qwerty, shift, false, out char plainChar);

                    if (usSurface.TryGetValue((key, shift), out char mark))
                    {
                        Assert.IsTrue(got, $"{key} shift={shift}");
                        Assert.AreEqual(mark, c, $"{key} shift={shift}");
                    }
                    else
                    {
                        // Not claimed: the surface is transparent, so opening it changes nothing.
                        Assert.AreEqual(plain, got, $"{key} shift={shift} claimed nothing but moved");
                        Assert.AreEqual(plainChar, c, $"{key} shift={shift} claimed nothing but moved");
                    }

                    // ...and the plain map underneath is letters (cased), digits, keypad and space,
                    // and nothing else, on every key of the enum.
                    bool wantPlain;
                    char wantPlainChar = default;

                    if (key >= Key.A && key <= Key.Z)
                    {
                        wantPlain = true;
                        char letter = (char)('a' + (key - Key.A));
                        wantPlainChar = shift ? char.ToUpperInvariant(letter) : letter;
                    }
                    else if (key >= Key.Number0 && key <= Key.Number9)
                    {
                        wantPlain = true;
                        wantPlainChar = (char)('0' + (key - Key.Number0));
                    }
                    else if (key >= Key.Keypad0 && key <= Key.Keypad9)
                    {
                        wantPlain = true;
                        wantPlainChar = (char)('0' + (key - Key.Keypad0));
                    }
                    else if (key == Key.Space)
                    {
                        wantPlain = true;
                        wantPlainChar = ' ';
                    }
                    else
                        wantPlain = false;

                    Assert.AreEqual(wantPlain, plain, $"plain {key} shift={shift}");
                    Assert.AreEqual(wantPlainChar, plainChar, $"plain {key} shift={shift}");
                }
            }
        }

        /// <summary>
        /// Backlog 216. QWERTZ's digit row carries the US DIGITS unshifted, so the row falls through
        /// to the letter/digit map for that state and 0-9 stay reachable; its SHIFTED legends are
        /// German and differ from the US ones on seven of the ten keys.
        /// </summary>
        [Test]
        public void QwertzDigitRowIsUsDigitsWithGermanMarksAbove()
        {
            var marks = new Dictionary<Key, char>
            {
                [Key.Number1] = '!',
                [Key.Number2] = '"',
                [Key.Number4] = '$',
                [Key.Number5] = '%',
                [Key.Number7] = '/',
                [Key.Number8] = '(',
                [Key.Number9] = ')',
            };

            foreach (var (key, mark) in marks)
            {
                Assert.IsTrue(KeyCharMap.TryMap(key, KeyboardLayout.Qwertz, true, true, out char produced), $"{key}");
                Assert.AreEqual(mark, produced, $"{key}");
            }

            // Shift on 3 is the section sign and on 0 it is '=', both outside the supported set, so
            // they produce NOTHING rather than falling through to a digit the keycap only shows
            // unshifted (which is what the US table's fall-through would have done).
            Assert.IsFalse(KeyCharMap.TryMap(Key.Number3, KeyboardLayout.Qwertz, true, true, out _));
            Assert.IsFalse(KeyCharMap.TryMap(Key.Number0, KeyboardLayout.Qwertz, true, true, out _));

            // Shift on 6 is the AMPERSAND, which is the FREESTYLE MARKER: deliberately outside
            // Typeability.PUNCTUATION, so this position must stay inert and never produce it. It is
            // also where the US table puts '^', which on a German keyboard lives left of the 1 key.
            Assert.IsFalse(KeyCharMap.TryMap(Key.Number6, KeyboardLayout.Qwertz, true, true, out _));
            Assert.IsFalse(Typeability.IsPunctuation(Typeability.FREESTYLE_MARKER), "the marker is out of the set on purpose");

            // Unshifted the row is the digits, mod or no mod, exactly as on US.
            foreach (bool punctuation in new[] { false, true })
            {
                for (int d = 0; d <= 9; d++)
                {
                    Assert.IsTrue(KeyCharMap.TryMap(Key.Number0 + d, KeyboardLayout.Qwertz, false, punctuation, out char digit), $"digit {d} punct={punctuation}");
                    Assert.AreEqual((char)('0' + d), digit, $"digit {d} punct={punctuation}");
                }
            }

            // WITHOUT the mod nothing about the row moves: shifted is the digit on all ten, exactly
            // as before. The punctuation surface only ever opens under Literate.
            for (int d = 0; d <= 9; d++)
            {
                Assert.IsTrue(KeyCharMap.TryMap(Key.Number0 + d, KeyboardLayout.Qwertz, true, false, out char digit), $"digit {d}");
                Assert.AreEqual((char)('0' + d), digit, $"digit {d}");
            }
        }

        /// <summary>
        /// Backlog 216, the rest of the German table: every non-digit position it claims, both
        /// modifier states, produced marks and inert states alike. Before it, QWERTZ read the US
        /// table with only the Y/Z letter swap applied, so a German player under Literate got ';'
        /// from their o-umlaut key, the apostrophe from their a-umlaut key, '/' from their '-' key
        /// and no way at all to produce the semicolon, the colon, the double quote, the slash, the
        /// asterisk, the two angle brackets or the apostrophe.
        /// </summary>
        [Test]
        public void QwertzCarriesItsOwnGermanLegendsUnderTheMod()
        {
            // null = the position is claimed but its legend for that state is outside the set, so
            // it produces nothing AND does not fall through.
            var expected = new Dictionary<(Key, bool), char?>
            {
                // Left of the 1 key: the circumflex dead key, degree sign above it.
                [(Key.Tilde, false)] = '^',
                [(Key.Tilde, true)] = null,
                // Right of 0: the eszett keycap, '?' above it.
                [(Key.Minus, false)] = null,
                [(Key.Minus, true)] = '?',
                // Right of that: the dead acute, dead grave above it.
                [(Key.Plus, false)] = null,
                [(Key.Plus, true)] = null,
                // Top row, right of P: the u-umlaut keycap ('[' PARKED on it), then '+' / '*'
                // (']' PARKED on the '+').
                [(Key.BracketLeft, false)] = '[',
                [(Key.BracketLeft, true)] = null,
                [(Key.BracketRight, false)] = ']',
                [(Key.BracketRight, true)] = '*',
                // Home row, right of L: the o-umlaut and a-umlaut keycaps, then '#' / apostrophe.
                [(Key.Semicolon, false)] = null,
                [(Key.Semicolon, true)] = null,
                [(Key.Quote, false)] = null,
                [(Key.Quote, true)] = null,
                [(Key.BackSlash, false)] = null,
                [(Key.BackSlash, true)] = '\'',
                // Bottom row: the ISO key US keyboards do not have, then ',' '.' '-' with ';' ':'
                // and the underscore above them.
                [(Key.NonUSBackSlash, false)] = '<',
                [(Key.NonUSBackSlash, true)] = '>',
                [(Key.Comma, false)] = ',',
                [(Key.Comma, true)] = ';',
                [(Key.Period, false)] = '.',
                [(Key.Period, true)] = ':',
                [(Key.Slash, false)] = '-',
                [(Key.Slash, true)] = null,
            };

            foreach (var ((key, shift), mark) in expected)
            {
                if (mark is null)
                    Assert.IsFalse(KeyCharMap.TryMap(key, KeyboardLayout.Qwertz, shift, true, out _), $"{key} (shift={shift}) must be inert");
                else
                {
                    Assert.IsTrue(KeyCharMap.TryMap(key, KeyboardLayout.Qwertz, shift, true, out char produced), $"{key} (shift={shift})");
                    Assert.AreEqual(mark.Value, produced, $"{key} (shift={shift})");
                }

                // Every one of them is still inert WITHOUT the mod, exactly as it was before: none
                // of these positions is on the plain letter/digit map, so a habitual comma (or a
                // stray umlaut key) never costs a German player anything.
                Assert.IsFalse(KeyCharMap.TryMap(key, KeyboardLayout.Qwertz, shift, false, out _), $"{key} (shift={shift}) without the mod");
            }
        }

        /// <summary>
        /// Backlog 216's version of backlog 214's failure: four US punctuation positions are
        /// LETTER keycaps on a German keyboard (o-umlaut, a-umlaut, u-umlaut and the eszett). None
        /// of those letters is on the typeable surface (the normalizer strips diacritics, so no
        /// lyric ever asks for one), so the positions produce nothing rather than the US marks they
        /// used to hand over.
        /// </summary>
        [Test]
        public void QwertzUmlautAndEszettPositionsDoNotHandOverUsMarks()
        {
            // The o-umlaut and a-umlaut keys, where the US table had ';' ':' and '\'' '"'.
            foreach (var key in new[] { Key.Semicolon, Key.Quote })
            {
                foreach (bool shift in new[] { false, true })
                    Assert.IsFalse(KeyCharMap.TryMap(key, KeyboardLayout.Qwertz, shift, true, out _), $"{key} shift={shift}");
            }

            // The u-umlaut key, where the US table had '['. It stays '[' unshifted, but as a
            // documented PARK rather than a legend (see the bracket test); shifted it is inert.
            Assert.IsFalse(KeyCharMap.TryMap(Key.BracketLeft, KeyboardLayout.Qwertz, true, true, out _));

            // The eszett key, where the US table had '-' unshifted. Its shifted legend '?' is the
            // one supported mark on it.
            Assert.IsFalse(KeyCharMap.TryMap(Key.Minus, KeyboardLayout.Qwertz, false, true, out _));

            Assert.IsTrue(KeyCharMap.TryMap(Key.Minus, KeyboardLayout.Qwertz, true, true, out char question));
            Assert.AreEqual('?', question);

            // The letter map does not claim any of the four either, mod or no mod, so nothing about
            // them moved for a player without Literate.
            foreach (var key in new[] { Key.Semicolon, Key.Quote, Key.BracketLeft, Key.Minus })
            {
                foreach (bool shift in new[] { false, true })
                    Assert.IsFalse(KeyCharMap.TryMap(key, KeyboardLayout.Qwertz, shift, false, out _), $"{key} shift={shift} without the mod");
            }

            // ...and every letter of the alphabet is still reachable without them, 'y' and 'z'
            // included, so nothing was stranded by refusing to claim them.
            for (char letter = 'a'; letter <= 'z'; letter++)
            {
                bool found = false;

                foreach (Key key in Enum.GetValues<Key>())
                {
                    if (KeyCharMap.TryMap(key, KeyboardLayout.Qwertz, false, true, out char c) && c == letter)
                        found = true;
                }

                Assert.IsTrue(found, $"'{letter}' has no key on QWERTZ");
            }
        }

        /// <summary>
        /// The two deliberate exceptions to the keycap rule on QWERTZ. '[' and ']' are AltGr+8 and
        /// AltGr+9 on a real German keyboard and AltGr is not modelled at all, so they are parked
        /// on the UNSHIFTED US bracket positions, which here show the u-umlaut and '+': neither is
        /// in the supported set, so nothing faithful is displaced, and the US positional memory
        /// survives exactly. Parking beats stranding: a mark with no key makes every lyric
        /// containing it uncompletable, while a mark on a spare legend is merely undiscoverable.
        /// </summary>
        [Test]
        public void QwertzParksTheBracketsOnTheUsBracketPositions()
        {
            Assert.IsTrue(KeyCharMap.TryMap(Key.BracketLeft, KeyboardLayout.Qwertz, false, true, out char open));
            Assert.AreEqual('[', open);

            Assert.IsTrue(KeyCharMap.TryMap(Key.BracketRight, KeyboardLayout.Qwertz, false, true, out char close));
            Assert.AreEqual(']', close);

            // The parks sit on the two positions QWERTY uses, so a US-trained German player finds
            // them where they expect.
            Assert.IsTrue(KeyCharMap.TryMap(Key.BracketLeft, KeyboardLayout.Qwerty, false, true, out char usOpen));
            Assert.AreEqual(open, usOpen);

            Assert.IsTrue(KeyCharMap.TryMap(Key.BracketRight, KeyboardLayout.Qwerty, false, true, out char usClose));
            Assert.AreEqual(close, usClose);

            // Nothing faithful was displaced: '*', the one supported mark either keycap shows, keeps
            // its own state on the '+' key.
            Assert.IsTrue(KeyCharMap.TryMap(Key.BracketRight, KeyboardLayout.Qwertz, true, true, out char star));
            Assert.AreEqual('*', star);

            // Inert without the mod, like every other punctuation position.
            Assert.IsFalse(KeyCharMap.TryMap(Key.BracketLeft, KeyboardLayout.Qwertz, false, false, out _));
            Assert.IsFalse(KeyCharMap.TryMap(Key.BracketRight, KeyboardLayout.Qwertz, false, false, out _));
        }

        /// <summary>
        /// The property the QWERTZ table has to preserve, and the one the pre-216 US-table-plus-swap
        /// modelling broke outright: no supported mark, digit or letter may be left with no key at
        /// all, because that makes every lyric containing it uncompletable under Literate.
        /// </summary>
        [Test]
        public void EverySupportedMarkStaysReachableOnQwertz()
        {
            var reachable = new HashSet<char>();

            foreach (Key key in Enum.GetValues<Key>())
            {
                foreach (bool shift in new[] { false, true })
                {
                    if (KeyCharMap.TryMap(key, KeyboardLayout.Qwertz, shift, true, out char c))
                        reachable.Add(c);
                }
            }

            foreach (char mark in Typeability.PUNCTUATION)
                Assert.IsTrue(reachable.Contains(mark), $"'{mark}' has no key on QWERTZ");

            for (char letter = 'a'; letter <= 'z'; letter++)
            {
                Assert.IsTrue(reachable.Contains(letter), $"'{letter}' has no key on QWERTZ");
                Assert.IsTrue(reachable.Contains(char.ToUpperInvariant(letter)), $"'{char.ToUpperInvariant(letter)}' has no key on QWERTZ");
            }

            for (char digit = '0'; digit <= '9'; digit++)
                Assert.IsTrue(reachable.Contains(digit), $"'{digit}' has no key on QWERTZ");

            Assert.IsTrue(reachable.Contains(' '), "the space has no key on QWERTZ");

            // The freestyle marker is not a mark the surface may ever produce, on any layout.
            Assert.IsFalse(reachable.Contains(Typeability.FREESTYLE_MARKER));
        }

        /// <summary>
        /// The exhaustive diff pin, the replacement for "QWERTY and QWERTZ are equal": the two
        /// layouts differ on the Y/Z letter swap (under both surfaces) and, with the punctuation
        /// surface OPEN, on exactly the positions where the German keycaps differ from the US ones.
        /// Everywhere else, including every state with the surface CLOSED, they are byte identical.
        /// </summary>
        [Test]
        public void QwertzDiffersFromQwertyExactlyWhereTheGermanKeycapsDo()
        {
            var expected = new HashSet<(Key, bool, bool)>();

            // The letter swap: both keys, both shift states, surface open or closed.
            foreach (var key in new[] { Key.Y, Key.Z })
            {
                foreach (bool shift in new[] { false, true })
                {
                    foreach (bool punctuation in new[] { false, true })
                        expected.Add((key, shift, punctuation));
                }
            }

            // The punctuation surface, and ONLY with it open. Read as "US legend -> German legend".
            (Key key, bool shift)[] surfaceDiffs =
            {
                (Key.Comma, true), // '<' -> ';'
                (Key.Period, true), // '>' -> ':'
                (Key.Quote, false), // '\'' -> a-umlaut
                (Key.Quote, true), // '"' -> capital a-umlaut
                (Key.Minus, false), // '-' -> eszett
                (Key.Minus, true), // nothing -> '?'
                (Key.Slash, false), // '/' -> '-'
                (Key.Slash, true), // '?' -> the underscore
                (Key.Semicolon, false), // ';' -> o-umlaut
                (Key.Semicolon, true), // ':' -> capital o-umlaut
                (Key.BracketRight, true), // nothing -> '*'
                (Key.Number0, true), // ')' -> '='
                (Key.Number2, true), // the digit -> '"'
                (Key.Number3, true), // the digit -> the section sign
                (Key.Number6, true), // '^' -> the ampersand
                (Key.Number7, true), // the digit -> '/'
                (Key.Number8, true), // '*' -> '('
                (Key.Number9, true), // '(' -> ')'
                (Key.Tilde, false), // nothing -> '^'
                (Key.BackSlash, true), // nothing -> the apostrophe
                (Key.NonUSBackSlash, false), // nothing -> '<'
                (Key.NonUSBackSlash, true), // nothing -> '>'
            };

            foreach (var (key, shift) in surfaceDiffs)
                expected.Add((key, shift, true));

            var actual = new HashSet<(Key, bool, bool)>();

            foreach (Key key in Enum.GetValues<Key>())
            {
                foreach (bool shift in new[] { false, true })
                {
                    foreach (bool punctuation in new[] { false, true })
                    {
                        bool qwerty = KeyCharMap.TryMap(key, KeyboardLayout.Qwerty, shift, punctuation, out char qwertyChar);
                        bool qwertz = KeyCharMap.TryMap(key, KeyboardLayout.Qwertz, shift, punctuation, out char qwertzChar);

                        if (qwerty != qwertz || qwertyChar != qwertzChar)
                            actual.Add((key, shift, punctuation));
                    }
                }
            }

            string describe(IEnumerable<(Key, bool, bool)> set)
                => string.Join(", ", set.OrderBy(x => x.Item1).ThenBy(x => x.Item2).Select(x => $"{x.Item1} shift={x.Item2} punct={x.Item3}"));

            Assert.AreEqual(string.Empty, describe(expected.Except(actual)), "expected to differ but did not");
            Assert.AreEqual(string.Empty, describe(actual.Except(expected)), "differ but were not expected to");
        }

        /// <summary>
        /// The digit row's own version of the reachability property: moving the marks onto the
        /// unshifted legends must not cost the player the DIGITS, which are real lyric content.
        /// They end up on Shift, exactly where the French keyboard puts them.
        /// </summary>
        [Test]
        public void EveryDigitStaysReachableOnAzertyUnderTheMod()
        {
            for (int d = 0; d <= 9; d++)
            {
                Assert.IsTrue(KeyCharMap.TryMap(Key.Number0 + d, KeyboardLayout.Azerty, true, true, out char c), $"digit {d}");
                Assert.AreEqual((char)('0' + d), c, $"digit {d}");
            }

            // And the space, the other non-letter cell every line is full of.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Space, KeyboardLayout.Azerty, false, true, out char space));
            Assert.AreEqual(' ', space);
        }

        /// <summary>
        /// The property the AZERTY table has to preserve: moving a position onto its French legend
        /// must not strand a supported mark with no key at all, which is the same failure as the
        /// untypeable 'm' in a slower disguise.
        /// </summary>
        [Test]
        public void EverySupportedMarkStaysReachableOnAzerty()
        {
            var reachable = new HashSet<char>();

            foreach (Key key in Enum.GetValues<Key>())
            {
                foreach (bool shift in new[] { false, true })
                {
                    if (KeyCharMap.TryMap(key, KeyboardLayout.Azerty, shift, true, out char c))
                        reachable.Add(c);
                }
            }

            foreach (char mark in Typeability.PUNCTUATION)
                Assert.IsTrue(reachable.Contains(mark), $"'{mark}' has no key on AZERTY");

            // And the letters are all still there too, 'm' included.
            for (char letter = 'a'; letter <= 'z'; letter++)
                Assert.IsTrue(reachable.Contains(letter), $"'{letter}' has no key on AZERTY");
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
