// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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
    }
}
