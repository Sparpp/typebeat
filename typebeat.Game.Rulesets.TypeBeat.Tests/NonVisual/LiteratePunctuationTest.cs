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

            // Nothing about the correction reaches QWERTY or QWERTZ.
            foreach (var layout in new[] { KeyboardLayout.Qwerty, KeyboardLayout.Qwertz })
            {
                Assert.IsTrue(KeyCharMap.TryMap(Key.Comma, layout, false, true, out char comma));
                Assert.AreEqual(',', comma, $"{layout}");

                Assert.IsTrue(KeyCharMap.TryMap(Key.Period, layout, true, true, out char greater));
                Assert.AreEqual('>', greater, $"{layout}");

                Assert.IsTrue(KeyCharMap.TryMap(Key.Slash, layout, true, true, out char question));
                Assert.AreEqual('?', question, $"{layout}");

                Assert.IsFalse(KeyCharMap.TryMap(Key.NonUSBackSlash, layout, false, true, out _), $"{layout}");
                Assert.IsFalse(KeyCharMap.TryMap(Key.NonUSBackSlash, layout, true, true, out _), $"{layout}");
            }
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

            // None of it reaches QWERTY or QWERTZ, which keep every US legend involved.
            foreach (var layout in new[] { KeyboardLayout.Qwerty, KeyboardLayout.Qwertz })
            {
                Assert.IsTrue(KeyCharMap.TryMap(Key.Quote, layout, false, true, out char quote), $"{layout}");
                Assert.AreEqual('\'', quote, $"{layout}");

                Assert.IsTrue(KeyCharMap.TryMap(Key.Quote, layout, true, true, out char doubleQuote), $"{layout}");
                Assert.AreEqual('"', doubleQuote, $"{layout}");

                Assert.IsTrue(KeyCharMap.TryMap(Key.BracketLeft, layout, false, true, out char open), $"{layout}");
                Assert.AreEqual('[', open, $"{layout}");

                Assert.IsTrue(KeyCharMap.TryMap(Key.BracketRight, layout, false, true, out char close), $"{layout}");
                Assert.AreEqual(']', close, $"{layout}");

                Assert.IsTrue(KeyCharMap.TryMap(Key.Minus, layout, false, true, out char hyphen), $"{layout}");
                Assert.AreEqual('-', hyphen, $"{layout}");

                Assert.IsFalse(KeyCharMap.TryMap(Key.BracketLeft, layout, true, true, out _), $"{layout}");
                Assert.IsFalse(KeyCharMap.TryMap(Key.BracketRight, layout, true, true, out _), $"{layout}");
                Assert.IsFalse(KeyCharMap.TryMap(Key.BackSlash, layout, false, true, out _), $"{layout}");
            }
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
        /// The correction is per LAYOUT, so the layouts that genuinely carry the US legends must
        /// come out of it byte for byte unchanged. QWERTZ differs from QWERTY in exactly two
        /// letters and in nothing else, punctuation surface open or closed.
        /// </summary>
        [Test]
        public void TheUsTableIsUntouchedByTheAzertyCorrection()
        {
            foreach (Key key in Enum.GetValues<Key>())
            {
                if (key == Key.Y || key == Key.Z)
                    continue;

                foreach (bool shift in new[] { false, true })
                {
                    foreach (bool punctuation in new[] { false, true })
                    {
                        bool qwerty = KeyCharMap.TryMap(key, KeyboardLayout.Qwerty, shift, punctuation, out char qwertyChar);
                        bool qwertz = KeyCharMap.TryMap(key, KeyboardLayout.Qwertz, shift, punctuation, out char qwertzChar);

                        Assert.AreEqual(qwerty, qwertz, $"{key} shift={shift} punct={punctuation}");
                        Assert.AreEqual(qwertyChar, qwertzChar, $"{key} shift={shift} punct={punctuation}");
                    }
                }
            }
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
