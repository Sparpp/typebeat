// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Formerly KeyCharMapTest. Backlog 383 deleted KeyCharMap, the three hand-written tables that turned
// a physical key (plus Shift, Caps Lock and the chosen layout) into the character it typed: the OS
// layout now commits the character and TextInputFold folds it to the surface. Each pin below is the
// old one reworked to what is still the game's: the same character in gives the same cell out (case
// kept, marks only under Literate), and the few facts about KEYS the playfield still uses
// (TypingKeys: which keys commit text, which are typing keys, the positional digit row).

using System.Linq;
using NUnit.Framework;
using osuTK.Input;
using typebeat.Game.Input;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypingSurfaceTest
    {
        private static string fold(string committed, bool punctuation = false) => new string(TextInputFold.Fold(committed, punctuation).ToArray());

        /// <summary>Every letter the old tables could produce, in either case, types itself.</summary>
        [Test]
        public void QwertyMapsLettersByPosition()
        {
            for (char c = 'a'; c <= 'z'; c++)
            {
                Assert.AreEqual(c.ToString(), fold(c.ToString()));
                Assert.AreEqual(char.ToUpperInvariant(c).ToString(), fold(char.ToUpperInvariant(c).ToString()));
            }

            // And every letter KEY is a typing key that commits text.
            for (Key key = Key.A; key <= Key.Z; key++)
            {
                Assert.IsTrue(TypingKeys.CommitsText(key), key.ToString());
                Assert.IsTrue(TypingKeys.AlwaysSwallowed(key), key.ToString());
            }
        }

        /// <summary>
        /// The layouts that move letters (QWERTZ's Y and Z, AZERTY's A Q Z W and M) still deliver a
        /// typing key at every letter keycap after the shortcut rewrite (backlog 371), so a letter
        /// press is swallowed mid-line on every layout exactly as before; what it types is the OS's.
        /// </summary>
        [TestCase(KeyboardLayout.Qwertz)]
        [TestCase(KeyboardLayout.Azerty)]
        public void QwertzSwapsOnlyYAndZ(KeyboardLayout layout)
        {
            for (Key physical = Key.A; physical <= Key.Z; physical++)
            {
                Key delivered = KeycapLayout.ToKeycap(physical, layout);

                // AZERTY's M position carries ',' and is delivered as the semicolon key: a
                // punctuation position, which commits text and is swallowed only when that types.
                if (layout == KeyboardLayout.Azerty && physical == Key.M)
                {
                    Assert.AreEqual(Key.Semicolon, delivered);
                    Assert.IsTrue(TypingKeys.CommitsText(delivered));
                    Assert.IsFalse(TypingKeys.AlwaysSwallowed(delivered));
                    continue;
                }

                Assert.IsTrue(TypingKeys.AlwaysSwallowed(delivered), $"{layout} {physical}");
            }

            // And the semicolon position that carries AZERTY's 'm' keycap is delivered as the M key.
            Assert.AreEqual(Key.M, KeycapLayout.ToKeycap(Key.Semicolon, KeyboardLayout.Azerty));
        }

        [Test]
        public void AzertySwapsAQ_ZW_AndMovesM()
        {
            // The French letters the OS commits at those positions type themselves.
            foreach (string letter in new[] { "a", "q", "z", "w", "m" })
                Assert.AreEqual(letter, fold(letter));
        }

        [TestCase("m", "m")]
        [TestCase("M", "M")]
        public void AzertyMKeycapSurvivesThePunctuationSurface(string committed, string expected)
        {
            Assert.AreEqual(expected, fold(committed, true));
            Assert.AreEqual(expected, fold(committed, false));
        }

        /// <summary>Backlog 215: a French player's own apostrophe types the apostrophe under the mod.</summary>
        [Test]
        public void AzertyApostropheLivesOnTheFourKey()
        {
            Assert.AreEqual("'", fold("'", true));
            Assert.AreEqual(string.Empty, fold("'", false));

            // Including the typographic apostrophe some layouts commit.
            Assert.AreEqual("'", fold("’", true));
        }

        [Test]
        public void QwertySemicolonKeepsItsUsLegend()
        {
            Assert.AreEqual(";", fold(";", true));
            Assert.AreEqual(":", fold(":", true));
            Assert.AreEqual(string.Empty, fold(";", false));
        }

        [TestCase("z", "z")]
        [TestCase("Z", "Z")]
        [TestCase("y", "y")]
        [TestCase("Y", "Y")]
        public void QwertzYZSwapSurvivesThePunctuationSurface(string committed, string expected)
        {
            Assert.AreEqual(expected, fold(committed, true));
            Assert.AreEqual(expected, fold(committed, false));
        }

        /// <summary>A mark has no case: whatever Shift or Caps Lock made the OS commit, the fold types that mark.</summary>
        [Test]
        public void CapsLockLeavesTheQwertzPunctuationSurfaceOnShiftOnlySemantics()
        {
            foreach (char mark in Typeability.PUNCTUATION)
                Assert.AreEqual(mark.ToString(), fold(mark.ToString(), true));
        }

        /// <summary>
        /// The digit row is the one place the default surface stays POSITIONAL: a digit key types its
        /// digit whatever the layout commits for it (Shift+1's '!', an AZERTY 'é'), top row and keypad.
        /// </summary>
        [Test]
        public void DigitsAndSpaceAreLayoutIndependent()
        {
            for (int d = 0; d <= 9; d++)
            {
                Assert.IsTrue(TypingKeys.TryPositionalDigit(Key.Number0 + d, out char top));
                Assert.AreEqual((char)('0' + d), top);

                Assert.IsTrue(TypingKeys.TryPositionalDigit(Key.Keypad0 + d, out char pad));
                Assert.AreEqual((char)('0' + d), pad);

                Assert.AreEqual(((char)('0' + d)).ToString(), fold(((char)('0' + d)).ToString()));
            }

            Assert.IsFalse(TypingKeys.TryPositionalDigit(Key.A, out _));
            Assert.IsFalse(TypingKeys.TryPositionalDigit(Key.Minus, out _));

            Assert.IsTrue(TypingKeys.CommitsText(Key.Space));
            Assert.IsTrue(TypingKeys.AlwaysSwallowed(Key.Space));
            Assert.AreEqual(" ", fold(" "));
        }

        /// <summary>The fold's default is the WIDE surface (marks on), the one the website's parity harness calls with no second argument.</summary>
        [Test]
        public void DefaultOverloadIsQwerty()
        {
            CollectionAssert.AreEqual(new[] { ',' }, TextInputFold.Fold(",").ToArray());
            CollectionAssert.AreEqual(TextInputFold.Fold("a,b", true).ToArray(), TextInputFold.Fold("a,b").ToArray());
        }

        [Test]
        public void ShiftUpperCasesLettersAfterLayoutRemap()
        {
            Assert.AreEqual("A", fold("A"));
            Assert.AreEqual("E", fold("É"));
        }

        [Test]
        public void ShiftLeavesDigitsAndSpaceUnchanged()
        {
            // Without Literate a shifted digit key still types its digit (positional), and what the
            // OS commits for it is inert there.
            foreach (string shifted in new[] { "!", "@", "#", "$", "%", "^", "&", "*", "(", ")" })
                Assert.AreEqual(string.Empty, fold(shifted, false), shifted);
        }

        [TestCase("a", "a")]
        [TestCase("A", "A")]
        public void LetterCaseIsShiftXorCapsLock(string committed, string expected) => Assert.AreEqual(expected, fold(committed));

        [Test]
        public void CapsLockCasesLettersAfterLayoutRemap()
        {
            // The OS applies Caps Lock (and Shift, and the layout); the fold keeps the case it gets.
            Assert.AreEqual("QWERTZAZERTY", fold("QWERTZAZERTY"));
            Assert.AreEqual("qwertzazerty", fold("qwertzazerty"));
        }

        [Test]
        public void CapsLockLeavesDigitsSpaceAndPunctuationOnShiftOnlySemantics()
        {
            Assert.AreEqual("0123456789 ", fold("0123456789 ", true));
            Assert.AreEqual(",.'-?!", fold(",.'-?!", true));
        }

        /// <summary>The fold's whole signature is (text, punctuation): there is no hidden layout or lock state left to default.</summary>
        [Test]
        public void OlderOverloadsDefaultToNoCapsLock()
        {
            Assert.AreEqual("aB", fold("aB", false));
            Assert.AreEqual("aB", fold("aB", true));
        }

        /// <summary>
        /// AZERTY's Verr Maj is a shift LOCK (backlog 238), which the OS applies before it commits:
        /// whichever legend it selected, the fold types that legend. The French legends of the
        /// bottom row and the circumflex key, and the digit a locked digit key commits.
        /// </summary>
        [TestCase(";", ";")]
        [TestCase(".", ".")]
        [TestCase(",", ",")]
        [TestCase("?", "?")]
        [TestCase(":", ":")]
        [TestCase("/", "/")]
        [TestCase("!", "!")]
        [TestCase("§", "")]
        [TestCase("1", "1")]
        [TestCase("&", "")]
        [TestCase("^", "^")]
        [TestCase("[", "[")]
        public void AzertyCapsLockShiftLocksThePunctuationSurface(string committed, string expected) => Assert.AreEqual(expected, fold(committed, true));

        [Test]
        public void EveryDigitIsReachableOnAzertyByCapsLockAlone()
        {
            for (char d = '0'; d <= '9'; d++)
                Assert.AreEqual(d.ToString(), fold(d.ToString(), true));
        }

        [Test]
        public void CapsLockChangesOnlyLetterCaseOnQwertyAndQwertz()
        {
            for (char c = 'a'; c <= 'z'; c++)
                Assert.AreEqual(char.ToUpperInvariant(c).ToString(), fold(char.ToUpperInvariant(c).ToString(), true));

            Assert.AreEqual("4$", fold("4$", true));
        }

        [TestCase("a", "a")]
        [TestCase("A", "A")]
        public void AzertyLettersKeepTheirXorUnderThePunctuationSurface(string committed, string expected) => Assert.AreEqual(expected, fold(committed, true));
    }
}
