// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 383: Latin gameplay typing reads the OS's committed characters, folded to the typing
// surface by TextInputFold. These pins are the fold itself: the accented letters, the special
// letters spelled out, the typographic variants, case, the punctuation gate, the inert rest, and
// the property everything hangs on, that a character the keyboard commits lands on exactly the cell
// the lyric normalizer made of the same character.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TextInputFoldTest
    {
        private static string fold(string committed, bool punctuation = true) => new string(TextInputFold.Fold(committed, punctuation).ToArray());

        [TestCase("a", "a")]
        [TestCase("Z", "Z")]
        [TestCase("7", "7")]
        [TestCase(" ", " ")]
        public void TheAsciiSurfaceTypesItself(string committed, string expected) => Assert.That(fold(committed), Is.EqualTo(expected));

        [TestCase("é", "e")]
        [TestCase("è", "e")]
        [TestCase("ê", "e")]
        [TestCase("ë", "e")]
        [TestCase("ç", "c")]
        [TestCase("ñ", "n")]
        [TestCase("å", "a")]
        [TestCase("ö", "o")]
        [TestCase("ü", "u")]
        [TestCase("É", "E")]
        [TestCase("Ç", "C")]
        [TestCase("ǘ", "u")]
        public void AnAccentedLetterTypesItsBaseLetterKeepingCase(string committed, string expected) => Assert.That(fold(committed), Is.EqualTo(expected));

        [Test]
        public void ADecomposedCommitTypesTheSameSinglePressAsAPrecomposedOne()
        {
            // A dead key followed by its vowel can arrive either way, depending on the platform.
            Assert.That(fold("é"), Is.EqualTo("e"));
            Assert.That(fold("é"), Is.EqualTo("e"));
        }

        [TestCase("ß", "ss")]
        [TestCase("ẞ", "SS")]
        [TestCase("æ", "ae")]
        [TestCase("Æ", "AE")]
        [TestCase("œ", "oe")]
        [TestCase("ø", "o")]
        [TestCase("Ø", "O")]
        [TestCase("ł", "l")]
        [TestCase("đ", "d")]
        [TestCase("þ", "th")]
        [TestCase("ð", "d")]
        [TestCase("ı", "i")]
        [TestCase("ŋ", "ng")]
        [TestCase("ǽ", "ae")]
        public void TheSpecialLettersSpellOutInOrder(string committed, string expected) => Assert.That(fold(committed), Is.EqualTo(expected));

        [Test]
        public void TheRomanisersLatinExtrasSpellOutToo()
        {
            foreach (var (letter, spelled) in Romaniser.LATIN_EXTRAS)
                Assert.That(fold(letter.ToString()), Is.EqualTo(spelled), $"'{letter}'");
        }

        [TestCase("’", "'")]
        [TestCase("‘", "'")]
        [TestCase("“", "\"")]
        [TestCase("”", "\"")]
        [TestCase("–", "-")]
        [TestCase("—", "-")]
        [TestCase(" ", " ")]
        public void TypographicVariantsFoldAsTheNormalizerFoldsThem(string committed, string expected) => Assert.That(fold(committed), Is.EqualTo(expected));

        [Test]
        public void EverySupportedMarkTypesUnderLiterateAndIsInertWithout()
        {
            foreach (char mark in Typeability.PUNCTUATION)
            {
                Assert.That(fold(mark.ToString()), Is.EqualTo(mark.ToString()), $"'{mark}' under Literate");
                Assert.That(fold(mark.ToString(), false), Is.Empty, $"'{mark}' without the mod");
            }
        }

        [TestCase("€")]
        [TestCase("&")]
        [TestCase("@")]
        [TestCase("#")]
        [TestCase("=")]
        [TestCase("`")]
        [TestCase("§")]
        [TestCase("²")]
        [TestCase("µ")]
        [TestCase("¨")]
        [TestCase("´")]
        [TestCase("ж")]
        [TestCase("α")]
        [TestCase("\t")]
        [TestCase("\r")]
        [TestCase("😀")]
        [TestCase("\uD83D")]
        public void ACharacterTheSurfaceCannotHoldIsInert(string committed)
        {
            Assert.That(fold(committed), Is.Empty);
            Assert.That(fold(committed, false), Is.Empty);
        }

        [Test]
        public void AMultiCharacterCommitTypesEveryPressInOrder()
        {
            // US-International commits the apostrophe and the letter together when a dead quote
            // meets a letter it cannot compose with.
            Assert.That(fold("'t"), Is.EqualTo("'t"));
            Assert.That(fold("'t", false), Is.EqualTo("t"));
            Assert.That(fold("Straße"), Is.EqualTo("Strasse"));
        }

        [Test]
        public void NothingCommittedTypesNothing()
        {
            Assert.That(fold(string.Empty), Is.Empty);
            Assert.That(TextInputFold.Fold(null), Is.Empty);
        }

        /// <summary>
        /// THE PROPERTY. For every character a Latin keyboard can commit (the whole of Latin-1 and
        /// Latin Extended-A and -B, plus the typographic variants), the fold types exactly the cells
        /// the lyric normalizer stores for the same character, so typing what is printed always
        /// lands on the cell. The two documented differences: the fold types a SPACE (the
        /// normalizer trims a lone one away), and it spells out the Romaniser's Latin extras, which
        /// only a romanised lyric carries.
        /// </summary>
        [Test]
        public void TheSameCharacterInGivesTheSameCellOut()
        {
            var chars = Enumerable.Range(0x20, 0x250 - 0x20).Select(i => (char)i)
                                  .Concat("‘’‚′“”„″–—―−");

            foreach (char c in chars)
            {
                if (char.IsWhiteSpace(c) || Romaniser.LATIN_EXTRAS.ContainsKey(c))
                    continue;

                Assert.That(fold(c.ToString()), Is.EqualTo(Typeability.Normalize(c.ToString())), $"U+{(int)c:X4} '{c}'");
            }
        }

        /// <summary>
        /// The surface itself: every char the fold can produce is one the engine can hold as a cell
        /// (with the mod) or a plain typeable char (without it). Pinned over the same range, so a
        /// future table entry that spelled out to something off the surface fails here.
        /// </summary>
        [Test]
        public void TheFoldNeverLeavesTheSurface()
        {
            for (int i = 0x20; i < 0x2000; i++)
            {
                string committed = ((char)i).ToString();

                Assert.That(TextInputFold.Fold(committed, false).All(Typeability.IsTypeable), $"U+{i:X4} without the mod");
                Assert.That(TextInputFold.Fold(committed).All(c => Typeability.IsTypeable(c) || Typeability.IsPunctuation(c)), $"U+{i:X4} under Literate");
            }
        }
    }
}
