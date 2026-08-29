// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osuTK.Input;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class KeyCharMapTest
    {
        [Test]
        public void QwertyMapsLettersByPosition()
        {
            Assert.IsTrue(KeyCharMap.TryMap(Key.Y, KeyboardLayout.Qwerty, out char y));
            Assert.AreEqual('y', y);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Z, KeyboardLayout.Qwerty, out char z));
            Assert.AreEqual('z', z);

            Assert.IsTrue(KeyCharMap.TryMap(Key.A, KeyboardLayout.Qwerty, out char a));
            Assert.AreEqual('a', a);
        }

        [Test]
        public void QwertzSwapsOnlyYAndZ()
        {
            // The physical QWERTY-Y key carries the Z keycap on QWERTZ, and vice versa.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Y, KeyboardLayout.Qwertz, out char fromY));
            Assert.AreEqual('z', fromY);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Z, KeyboardLayout.Qwertz, out char fromZ));
            Assert.AreEqual('y', fromZ);

            // Every other letter is unchanged from QWERTY.
            for (Key k = Key.A; k <= Key.Z; k++)
            {
                if (k == Key.Y || k == Key.Z)
                    continue;

                KeyCharMap.TryMap(k, KeyboardLayout.Qwerty, out char qwerty);
                KeyCharMap.TryMap(k, KeyboardLayout.Qwertz, out char qwertz);
                Assert.AreEqual(qwerty, qwertz, $"{k} should map identically on both layouts");
            }
        }

        [Test]
        public void AzertySwapsAQ_ZW_AndMovesM()
        {
            // Physical position → keycap the player reads on AZERTY.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Q, KeyboardLayout.Azerty, out char fromQ));
            Assert.AreEqual('a', fromQ);

            Assert.IsTrue(KeyCharMap.TryMap(Key.A, KeyboardLayout.Azerty, out char fromA));
            Assert.AreEqual('q', fromA);

            Assert.IsTrue(KeyCharMap.TryMap(Key.W, KeyboardLayout.Azerty, out char fromW));
            Assert.AreEqual('z', fromW);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Z, KeyboardLayout.Azerty, out char fromZ));
            Assert.AreEqual('w', fromZ);

            // M lives on the QWERTY semicolon position; the QWERTY M position carries ','
            // (outside the typeable surface) and must be inert.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Azerty, out char fromSemicolon));
            Assert.AreEqual('m', fromSemicolon);

            Assert.IsFalse(KeyCharMap.TryMap(Key.M, KeyboardLayout.Azerty, out _));

            // Every other letter is unchanged from QWERTY.
            for (Key k = Key.A; k <= Key.Z; k++)
            {
                if (k == Key.A || k == Key.Q || k == Key.W || k == Key.Z || k == Key.M)
                    continue;

                KeyCharMap.TryMap(k, KeyboardLayout.Qwerty, out char qwerty);
                KeyCharMap.TryMap(k, KeyboardLayout.Azerty, out char azerty);
                Assert.AreEqual(qwerty, azerty, $"{k} should map identically on both layouts");
            }

            // Semicolon stays inert on the other layouts.
            Assert.IsFalse(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Qwerty, out _));
            Assert.IsFalse(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Qwertz, out _));
        }

        /// <summary>
        /// Backlog 214. The punctuation surface is consulted BEFORE the letter map, so its US
        /// legend for the QWERTY semicolon position (';' / ':') used to shadow the 'm' keycap that
        /// AZERTY puts there: with Literate on, an AZERTY player pressing M got ';', a wrong key
        /// every time, and no map containing an 'm' could be completed.
        /// </summary>
        [TestCase(false, false, 'm')]
        [TestCase(true, false, 'M')]
        [TestCase(false, true, 'M')]
        [TestCase(true, true, 'm')]
        public void AzertyMKeycapSurvivesThePunctuationSurface(bool shift, bool capsLock, char expected)
        {
            Assert.IsTrue(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Azerty, shift, punctuation: true, capsLock, out char c),
                $"shift={shift} caps={capsLock}");
            Assert.AreEqual(expected, c, $"shift={shift} caps={capsLock}");

            // Identical with the surface closed: opening it must not move the letter at all.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Azerty, shift, punctuation: false, capsLock, out char plain));
            Assert.AreEqual(expected, plain, $"no-punctuation shift={shift} caps={capsLock}");
        }

        /// <summary>
        /// Backlog 215, backlog 214's failure one row up. On AZERTY the apostrophe is the
        /// UNSHIFTED legend of the 4 key and the digit is the shifted one, the reverse of the US
        /// row. The US table had it exactly backwards, so an AZERTY player pressing their own '
        /// keycap produced '4', a wrong key, and no map containing a "don't" or an "I'm", which is
        /// most of them, could be completed under Literate.
        /// </summary>
        [Test]
        public void AzertyApostropheLivesOnTheFourKey()
        {
            Assert.IsTrue(KeyCharMap.TryMap(Key.Number4, KeyboardLayout.Azerty, shift: false, punctuation: true, out char apostrophe));
            Assert.AreEqual('\'', apostrophe);

            // Shift is the DIGIT, exactly as on the real keyboard, not the US legend's '$'.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Number4, KeyboardLayout.Azerty, shift: true, punctuation: true, out char four));
            Assert.AreEqual('4', four);

            // Backlog 238: Verr Maj is a SHIFT LOCK on this layout, so caps selects the same legend
            // Shift does and Shift held over it selects the unshifted one. This pair used to assert
            // the opposite, on the assumption that no keyboard shift-locks a digit-row key.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Number4, KeyboardLayout.Azerty, shift: false, punctuation: true, capsLock: true, out char capsFour));
            Assert.AreEqual('4', capsFour);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Number4, KeyboardLayout.Azerty, shift: true, punctuation: true, capsLock: true, out char capsApostrophe));
            Assert.AreEqual('\'', capsApostrophe);

            // Without the mod the key is the digit under either modifier, exactly as before: the
            // punctuation surface is the only thing that moves, and it opens only for Literate.
            foreach (bool shift in new[] { false, true })
            {
                Assert.IsTrue(KeyCharMap.TryMap(Key.Number4, KeyboardLayout.Azerty, shift, punctuation: false, out char digit), $"shift={shift}");
                Assert.AreEqual('4', digit, $"shift={shift}");
            }

            // QWERTY and QWERTZ keep the US legend on that key, both ways round.
            foreach (var layout in new[] { KeyboardLayout.Qwerty, KeyboardLayout.Qwertz })
            {
                Assert.IsTrue(KeyCharMap.TryMap(Key.Number4, layout, shift: true, punctuation: true, out char dollar), $"{layout}");
                Assert.AreEqual('$', dollar, $"{layout}");

                Assert.IsTrue(KeyCharMap.TryMap(Key.Number4, layout, shift: false, punctuation: true, out char plain), $"{layout}");
                Assert.AreEqual('4', plain, $"{layout}");
            }
        }

        [Test]
        public void QwertySemicolonKeepsItsUsLegend()
        {
            // QWERTY is the only layout that genuinely has ';' there, and it is untouched by the
            // AZERTY and QWERTZ corrections.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Qwerty, shift: false, punctuation: true, out char semicolon));
            Assert.AreEqual(';', semicolon);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Qwerty, shift: true, punctuation: true, out char colon));
            Assert.AreEqual(':', colon);

            // And it is still inert without the mod.
            Assert.IsFalse(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Qwerty, shift: false, punctuation: false, out _));

            // Backlog 216: that position is the o-umlaut keycap on QWERTZ, so it hands a German
            // player nothing at all rather than the US ';' / ':' it used to.
            foreach (bool shift in new[] { false, true })
            {
                Assert.IsFalse(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Qwertz, shift, punctuation: true, out _), $"qwertz literate shift={shift}");
                Assert.IsFalse(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Qwertz, shift, punctuation: false, out _), $"qwertz plain shift={shift}");
            }

            // The QWERTY-M position is the letter on both of those layouts, mod or no mod.
            foreach (var layout in new[] { KeyboardLayout.Qwerty, KeyboardLayout.Qwertz })
            {
                Assert.IsTrue(KeyCharMap.TryMap(Key.M, layout, shift: false, punctuation: true, out char m), $"{layout}");
                Assert.AreEqual('m', m, $"{layout}");
            }
        }

        /// <summary>
        /// Backlog 216 gave QWERTZ a punctuation table of its own, and the surface is consulted
        /// BEFORE the letter map, so the Y/Z swap has to survive it: the German table claims no
        /// letter position, and case still comes from shift XOR caps applied after the remap.
        /// </summary>
        [TestCase(false, false, 'z')]
        [TestCase(true, false, 'Z')]
        [TestCase(false, true, 'Z')]
        [TestCase(true, true, 'z')]
        public void QwertzYZSwapSurvivesThePunctuationSurface(bool shift, bool capsLock, char expected)
        {
            Assert.IsTrue(KeyCharMap.TryMap(Key.Y, KeyboardLayout.Qwertz, shift, punctuation: true, capsLock, out char c),
                $"shift={shift} caps={capsLock}");
            Assert.AreEqual(expected, c, $"shift={shift} caps={capsLock}");

            // Identical with the surface closed: opening it must not move the letter at all.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Y, KeyboardLayout.Qwertz, shift, punctuation: false, capsLock, out char plain));
            Assert.AreEqual(expected, plain, $"no-punctuation shift={shift} caps={capsLock}");

            // The other half of the swap, in the same four states.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Z, KeyboardLayout.Qwertz, shift, punctuation: true, capsLock, out char other));
            Assert.AreEqual(expected == 'z' ? 'y' : 'Y', other, $"Z shift={shift} caps={capsLock}");
        }

        /// <summary>
        /// Caps Lock does not shift a punctuation key on a German keyboard either, so the QWERTZ
        /// table's two legends stay Shift-selected under it.
        /// </summary>
        [Test]
        public void CapsLockLeavesTheQwertzPunctuationSurfaceOnShiftOnlySemantics()
        {
            Assert.IsTrue(KeyCharMap.TryMap(Key.Comma, KeyboardLayout.Qwertz, shift: false, punctuation: true, capsLock: true, out char comma));
            Assert.AreEqual(',', comma);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Comma, KeyboardLayout.Qwertz, shift: true, punctuation: true, capsLock: true, out char semicolon));
            Assert.AreEqual(';', semicolon);

            // The mark above a digit is reachable by Shift and only by Shift, caps or no caps.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Number7, KeyboardLayout.Qwertz, shift: false, punctuation: true, capsLock: true, out char seven));
            Assert.AreEqual('7', seven);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Number7, KeyboardLayout.Qwertz, shift: true, punctuation: true, capsLock: true, out char slash));
            Assert.AreEqual('/', slash);

            // An inert position stays inert under caps.
            Assert.IsFalse(KeyCharMap.TryMap(Key.Quote, KeyboardLayout.Qwertz, shift: true, punctuation: true, capsLock: true, out _));
        }

        [Test]
        public void DigitsAndSpaceAreLayoutIndependent()
        {
            Assert.IsTrue(KeyCharMap.TryMap(Key.Number5, KeyboardLayout.Qwertz, out char five));
            Assert.AreEqual('5', five);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Keypad0, KeyboardLayout.Qwertz, out char zero));
            Assert.AreEqual('0', zero);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Space, KeyboardLayout.Qwertz, out char space));
            Assert.AreEqual(' ', space);
        }

        [Test]
        public void DefaultOverloadIsQwerty()
        {
            Assert.IsTrue(KeyCharMap.TryMap(Key.Y, out char y));
            Assert.AreEqual('y', y);
        }

        [Test]
        public void ShiftUpperCasesLettersAfterLayoutRemap()
        {
            // Shift on a letter produces the capital (needed for the Literate mod).
            Assert.IsTrue(KeyCharMap.TryMap(Key.A, KeyboardLayout.Qwerty, shift: true, out char a));
            Assert.AreEqual('A', a);

            // Case is applied AFTER the layout remap: AZERTY's physical Q keycap is 'a', so
            // Shift+Q yields 'A' (the capital the player reads), not 'Q'.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Q, KeyboardLayout.Azerty, shift: true, out char azertyQ));
            Assert.AreEqual('A', azertyQ);

            // QWERTZ's physical Y keycap is 'z', so Shift+Y yields 'Z'.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Y, KeyboardLayout.Qwertz, shift: true, out char qwertzY));
            Assert.AreEqual('Z', qwertzY);
        }

        [Test]
        public void ShiftLeavesDigitsAndSpaceUnchanged()
        {
            // Digits and space have no case; Shift must not alter them (a stray Shift never
            // turns a space/number into an un-typeable char).
            Assert.IsTrue(KeyCharMap.TryMap(Key.Number5, KeyboardLayout.Qwerty, shift: true, out char five));
            Assert.AreEqual('5', five);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Keypad0, KeyboardLayout.Qwerty, shift: true, out char zero));
            Assert.AreEqual('0', zero);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Space, KeyboardLayout.Qwerty, shift: true, out char space));
            Assert.AreEqual(' ', space);
        }

        /// <summary>
        /// Backlog 205. A letter's case is shift XOR capsLock, exactly as a real keyboard behaves,
        /// so a player who capitalises with Caps Lock produces the capitals the Literate mod
        /// demands. The fourth combination is the one that gets forgotten: Shift held while Caps
        /// Lock is ON types LOWER case.
        /// </summary>
        [TestCase(false, false, 'a')]
        [TestCase(true, false, 'A')]
        [TestCase(false, true, 'A')]
        [TestCase(true, true, 'a')]
        public void LetterCaseIsShiftXorCapsLock(bool shift, bool capsLock, char expected)
        {
            Assert.IsTrue(KeyCharMap.TryMap(Key.A, KeyboardLayout.Qwerty, shift, punctuation: false, capsLock, out char c));
            Assert.AreEqual(expected, c, $"shift={shift} caps={capsLock}");

            // Same rule with the punctuation surface open, which is the mode (Literate) that makes
            // case matter in the first place.
            Assert.IsTrue(KeyCharMap.TryMap(Key.A, KeyboardLayout.Qwerty, shift, punctuation: true, capsLock, out char literate));
            Assert.AreEqual(expected, literate, $"literate shift={shift} caps={capsLock}");
        }

        [Test]
        public void CapsLockCasesLettersAfterLayoutRemap()
        {
            // Caps applies to the REMAPPED letter, like Shift does: AZERTY's physical Q keycap is
            // 'a', so caps-lock Q is the 'A' the player reads, not 'Q'.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Q, KeyboardLayout.Azerty, shift: false, punctuation: false, capsLock: true, out char azertyQ));
            Assert.AreEqual('A', azertyQ);

            // QWERTZ's physical Y keycap is 'z'.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Y, KeyboardLayout.Qwertz, shift: false, punctuation: false, capsLock: true, out char qwertzY));
            Assert.AreEqual('Z', qwertzY);

            // And the XOR still holds after the remap.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Q, KeyboardLayout.Azerty, shift: true, punctuation: false, capsLock: true, out char azertyShiftQ));
            Assert.AreEqual('a', azertyShiftQ);
        }

        /// <summary>
        /// The US Caps Lock key locks letter case and nothing else, so on QWERTY it must leave the
        /// whole non-letter surface exactly where Shift alone left it. Backlog 238 scoped this to
        /// the layout: it is NOT true of French AZERTY, whose Verr Maj is a shift lock
        /// (see <see cref="AzertyCapsLockShiftLocksThePunctuationSurface"/>).
        /// </summary>
        [Test]
        public void CapsLockLeavesDigitsSpaceAndPunctuationOnShiftOnlySemantics()
        {
            // Digits and space: caps is inert with or without Shift.
            foreach (bool shift in new[] { false, true })
            {
                Assert.IsTrue(KeyCharMap.TryMap(Key.Number5, KeyboardLayout.Qwerty, shift, punctuation: false, capsLock: true, out char five));
                Assert.AreEqual('5', five, $"digit shift={shift}");

                Assert.IsTrue(KeyCharMap.TryMap(Key.Keypad0, KeyboardLayout.Qwerty, shift, punctuation: false, capsLock: true, out char zero));
                Assert.AreEqual('0', zero, $"keypad shift={shift}");

                Assert.IsTrue(KeyCharMap.TryMap(Key.Space, KeyboardLayout.Qwerty, shift, punctuation: false, capsLock: true, out char space));
                Assert.AreEqual(' ', space, $"space shift={shift}");
            }

            // The mark above a digit stays reachable by Shift and ONLY by Shift: with caps on,
            // plain 4 is still '4' and Shift+4 is still '$'.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Number4, KeyboardLayout.Qwerty, shift: false, punctuation: true, capsLock: true, out char plainFour));
            Assert.AreEqual('4', plainFour);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Number4, KeyboardLayout.Qwerty, shift: true, punctuation: true, capsLock: true, out char dollar));
            Assert.AreEqual('$', dollar);

            // Ordinary punctuation keys keep their two shift-selected legends under caps.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Comma, KeyboardLayout.Qwerty, shift: false, punctuation: true, capsLock: true, out char comma));
            Assert.AreEqual(',', comma);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Comma, KeyboardLayout.Qwerty, shift: true, punctuation: true, capsLock: true, out char less));
            Assert.AreEqual('<', less);

            Assert.IsTrue(KeyCharMap.TryMap(Key.Semicolon, KeyboardLayout.Qwerty, shift: true, punctuation: true, capsLock: true, out char colon));
            Assert.AreEqual(':', colon);

            // A key with no mark on either legend stays inert regardless of caps.
            Assert.IsFalse(KeyCharMap.TryMap(Key.BracketLeft, KeyboardLayout.Qwerty, shift: true, punctuation: true, capsLock: true, out _));
        }

        /// <summary>
        /// The pre-existing overloads keep meaning exactly what they always did (caps lock off), so
        /// every call site that cannot read the toggle degrades to shift-only rather than changing
        /// behaviour.
        /// </summary>
        [Test]
        public void OlderOverloadsDefaultToNoCapsLock()
        {
            foreach (var layout in new[] { KeyboardLayout.Qwerty, KeyboardLayout.Qwertz, KeyboardLayout.Azerty })
            {
                foreach (Key k in Enum.GetValues<Key>())
                {
                    foreach (bool shift in new[] { false, true })
                    {
                        foreach (bool punctuation in new[] { false, true })
                        {
                            bool old = KeyCharMap.TryMap(k, layout, shift, punctuation, out char oldChar);
                            bool @new = KeyCharMap.TryMap(k, layout, shift, punctuation, capsLock: false, out char newChar);

                            Assert.AreEqual(old, @new, $"{k} {layout} shift={shift} punct={punctuation}");
                            Assert.AreEqual(oldChar, newChar, $"{k} {layout} shift={shift} punct={punctuation}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Backlog 238, reported from the field and confirmed by the reporter. French Verr Maj is a
        /// SHIFT LOCK, not a caps lock: it produces the digit row (whose digits are the SHIFTED
        /// legends on AZERTY) and the upper punctuation legends, and typing digits that way is the
        /// normal French habit. The map used to withhold caps from the punctuation surface entirely,
        /// so under Literate the reporter's '.' key handed them ';' and their '?' key handed them
        /// ',', which are exactly the unshifted legends of those two keycaps, and plain digits read
        /// inert. All four (shift, caps) states are pinned because the fourth is the one that gets
        /// forgotten: Shift held over the lock is back on the UNSHIFTED legend.
        /// </summary>
        // Bottom row: the four keycaps the reporter named, one position left of the US ones.
        [TestCase(Key.Comma, false, false, ';')]
        [TestCase(Key.Comma, true, false, '.')]
        [TestCase(Key.Comma, false, true, '.')]
        [TestCase(Key.Comma, true, true, ';')]
        [TestCase(Key.M, false, false, ',')]
        [TestCase(Key.M, true, false, '?')]
        [TestCase(Key.M, false, true, '?')]
        [TestCase(Key.M, true, true, ',')]
        [TestCase(Key.Period, false, false, ':')]
        [TestCase(Key.Period, true, false, '/')]
        [TestCase(Key.Period, false, true, '/')]
        [TestCase(Key.Period, true, true, ':')]
        // The section sign is outside the supported set, so the shift LEVEL is inert by either route.
        [TestCase(Key.Slash, false, false, '!')]
        [TestCase(Key.Slash, true, false, '\0')]
        [TestCase(Key.Slash, false, true, '\0')]
        [TestCase(Key.Slash, true, true, '!')]
        // Digit row: '&' unshifted is unsupported, so the caps route is the one that gives the digit.
        [TestCase(Key.Number1, false, false, '\0')]
        [TestCase(Key.Number1, true, false, '1')]
        [TestCase(Key.Number1, false, true, '1')]
        [TestCase(Key.Number1, true, true, '\0')]
        // A parked bracket and a relocated mark ride the same level, arms and all.
        [TestCase(Key.BracketLeft, false, false, '^')]
        [TestCase(Key.BracketLeft, true, false, '[')]
        [TestCase(Key.BracketLeft, false, true, '[')]
        [TestCase(Key.BracketLeft, true, true, '^')]
        public void AzertyCapsLockShiftLocksThePunctuationSurface(Key key, bool shift, bool capsLock, char expected)
        {
            string what = $"{key} shift={shift} caps={capsLock}";

            bool produced = KeyCharMap.TryMap(key, KeyboardLayout.Azerty, shift, punctuation: true, capsLock, out char c);

            if (expected == '\0')
            {
                Assert.IsFalse(produced, what);
                return;
            }

            Assert.IsTrue(produced, what);
            Assert.AreEqual(expected, c, what);
        }

        /// <summary>
        /// Backlog 238, the half of the report that is easy to under-weight: the reporter reached
        /// for Caps Lock unprompted, so it is the habitual French route to the digit row rather than
        /// an edge case, and all ten digits must arrive by it. Shift held over the lock is back on
        /// the French unshifted legends, of which only four are supported marks.
        /// </summary>
        [Test]
        public void EveryDigitIsReachableOnAzertyByCapsLockAlone()
        {
            for (int d = 0; d <= 9; d++)
            {
                var key = Key.Number0 + d;

                Assert.IsTrue(KeyCharMap.TryMap(key, KeyboardLayout.Azerty, shift: false, punctuation: true, capsLock: true, out char digit), $"caps digit {d}");
                Assert.AreEqual((char)('0' + d), digit, $"caps digit {d}");

                // Shift over the lock is the unshifted French legend: 3 4 5 6 carry marks, the rest
                // are the ampersand, the accented letters and the underscore, none of them typeable.
                char legend = d switch
                {
                    3 => '"',
                    4 => '\'',
                    5 => '(',
                    6 => '-',
                    _ => '\0',
                };

                bool produced = KeyCharMap.TryMap(key, KeyboardLayout.Azerty, shift: true, punctuation: true, capsLock: true, out char c);

                Assert.AreEqual(legend != '\0', produced, $"shift+caps digit {d}");

                if (legend != '\0')
                    Assert.AreEqual(legend, c, $"shift+caps digit {d}");
            }
        }

        /// <summary>
        /// Backlog 238 is LAYOUT-SCOPED, and this is the pin that keeps it so: on QWERTY and QWERTZ
        /// Caps Lock still changes letter CASE and absolutely nothing else, over every key, both
        /// shift states and both surfaces. Applying the AZERTY shift-lock XOR to either of these
        /// reds this exhaustively (with caps on, plain 4 would stop being '4').
        /// </summary>
        [Test]
        public void CapsLockChangesOnlyLetterCaseOnQwertyAndQwertz()
        {
            foreach (var layout in new[] { KeyboardLayout.Qwerty, KeyboardLayout.Qwertz })
            {
                foreach (Key k in Enum.GetValues<Key>())
                {
                    foreach (bool shift in new[] { false, true })
                    {
                        foreach (bool punctuation in new[] { false, true })
                        {
                            string what = $"{k} {layout} shift={shift} punct={punctuation}";

                            bool shiftOnly = KeyCharMap.TryMap(k, layout, shift, punctuation, capsLock: false, out char plain);
                            bool underCaps = KeyCharMap.TryMap(k, layout, shift, punctuation, capsLock: true, out char caps);

                            // Caps never claims or releases a position on these two layouts.
                            Assert.AreEqual(shiftOnly, underCaps, what);

                            // A letter flips case; everything else, marks and digits and space and
                            // the inert positions alike, is the same character it was.
                            char expected = char.IsAsciiLetter(plain)
                                ? char.IsAsciiLetterLower(plain) ? char.ToUpperInvariant(plain) : char.ToLowerInvariant(plain)
                                : plain;

                            Assert.AreEqual(expected, caps, what);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The AZERTY shift lock reaches the punctuation surface, and letters must be untouched by
        /// that: their case was already shift XOR caps, and it still comes from the keycap the
        /// player reads rather than the physical position (AZERTY Q is the 'a' keycap).
        /// </summary>
        [TestCase(false, false, 'a')]
        [TestCase(true, false, 'A')]
        [TestCase(false, true, 'A')]
        [TestCase(true, true, 'a')]
        public void AzertyLettersKeepTheirXorUnderThePunctuationSurface(bool shift, bool capsLock, char expected)
        {
            Assert.IsTrue(KeyCharMap.TryMap(Key.Q, KeyboardLayout.Azerty, shift, punctuation: true, capsLock, out char c),
                $"shift={shift} caps={capsLock}");
            Assert.AreEqual(expected, c, $"shift={shift} caps={capsLock}");

            // Identical with the surface closed: the shift lock moves marks, never letters.
            Assert.IsTrue(KeyCharMap.TryMap(Key.Q, KeyboardLayout.Azerty, shift, punctuation: false, capsLock, out char plain));
            Assert.AreEqual(expected, plain, $"no-punctuation shift={shift} caps={capsLock}");
        }
    }
}
