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
        /// Caps Lock does not shift a digit-row or punctuation key on any real keyboard, so it must
        /// leave the whole non-letter surface exactly where Shift alone left it.
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
    }
}
