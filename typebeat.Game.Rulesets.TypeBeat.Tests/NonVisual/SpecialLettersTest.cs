// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 329, step a: the Latin special letters FormD cannot decompose are SPELLED OUT by
    /// <see cref="Typeability.Normalize"/> instead of being deleted as untypeable, so "straße" is
    /// stored and typed as "strasse" rather than "strae". Case is preserved, because the Literate
    /// mod types the stored line case-sensitively.
    /// </summary>
    [TestFixture]
    public class SpecialLettersTest
    {
        /// <summary>The whole table, letter by letter, including the casing decisions.</summary>
        private static readonly (char Letter, string Spelled)[] table =
        [
            ('ß', "ss"), ('ẞ', "SS"),
            ('æ', "ae"), ('Æ', "AE"),
            ('œ', "oe"), ('Œ', "OE"),
            ('ø', "o"), ('Ø', "O"),
            ('ł', "l"), ('Ł', "L"),
            ('đ', "d"), ('Đ', "D"),
            ('þ', "th"), ('Þ', "Th"),
            ('ð', "d"), ('Ð', "D"),
            ('ı', "i"),
            ('ŋ', "ng"), ('Ŋ', "Ng"),
            ('ĸ', "k"),
        ];

        [Test]
        public void TheTableIsExactlyTheDocumentedLetters()
        {
            Assert.That(Typeability.SPECIAL_LETTERS, Is.EquivalentTo(table.Select(t => new KeyValuePair<char, string>(t.Letter, t.Spelled))));
            Assert.That(Typeability.SPECIAL_LETTERS.Count, Is.EqualTo(20));
        }

        [Test]
        public void EveryLetterSpellsOutOnItsOwnAndInsideAWord()
        {
            Assert.Multiple(() =>
            {
                foreach ((char letter, string spelled) in table)
                {
                    Assert.That(Typeability.Normalize(letter.ToString()), Is.EqualTo(spelled), $"'{letter}' alone");
                    Assert.That(Typeability.Normalize($"a{letter}b"), Is.EqualTo($"a{spelled}b"), $"'{letter}' inside a word");
                }
            });
        }

        [Test]
        public void EveryLetterIsOneFormDCannotReach()
        {
            Assert.Multiple(() =>
            {
                foreach ((char letter, string spelled) in table)
                {
                    // In scope only BECAUSE canonical decomposition leaves it alone: a letter FormD
                    // does fold (å, ä, ö, é) must never be added here.
                    Assert.That(letter.ToString().Normalize(NormalizationForm.FormD), Is.EqualTo(letter.ToString()), $"'{letter}' has no decomposition");
                    Assert.That(Typeability.IsTypeable(letter), Is.False, $"'{letter}' was not typeable before");
                    Assert.That(spelled.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'), Is.True, $"'{letter}' spells out in ASCII letters only");

                    // Case is preserved: a lower-case letter spells lower case, a capital leads with a capital.
                    if (char.IsUpper(letter))
                        Assert.That(char.IsUpper(spelled[0]), Is.True, $"'{letter}' is a capital");
                    else
                        Assert.That(spelled, Is.EqualTo(spelled.ToLowerInvariant()), $"'{letter}' is lower case");
                }
            });
        }

        [TestCase("straße", "strasse")]
        [TestCase("Øresund", "Oresund")]
        // ó and ź DECOMPOSE (FormD drops their marks), ł does not and is spelled by the table.
        [TestCase("łódź", "lodz")]
        [TestCase("Łódź", "Lodz")]
        [TestCase("þú", "thu")]
        [TestCase("Þór", "Thor")]
        [TestCase("ÞÚ", "ThU")] // the one fixed capital form, even on an all-caps line
        [TestCase("STRAẞE", "STRASSE")]
        [TestCase("Ærø", "AEro")]
        [TestCase("Œuvre cœur", "OEuvre coeur")]
        [TestCase("Đorđe", "Dorde")]
        [TestCase("Ðað", "Dad")]
        [TestCase("kalı", "kali")]
        [TestCase("Ŋaŋ", "Ngang")]
        [TestCase("ĸ", "k")]
        // Precomposed letters that DECOMPOSE to a table letter plus a mark are reached as well,
        // because the table runs after the FormD step.
        [TestCase("Ǿ ǿ ǽ Ǣ", "O o ae AE")]
        // Untouched: the letters FormD already folds, and everything outside the Latin set.
        [TestCase("å ä ö é ñ", "a a o e n")]
        [TestCase("Grüße, Straße!", "Grusse, Strasse!")]
        public void Normalizes(string raw, string expected)
        {
            Assert.That(Typeability.Normalize(raw), Is.EqualTo(expected));

            // Every output is a FIXED POINT: the stored line re-normalizes to itself on decode.
            Assert.That(Typeability.Normalize(expected), Is.EqualTo(expected), "idempotent");
        }

        [Test]
        public void TheDefaultStreamFoldsTheSpelledCapitals()
        {
            Assert.That(Typeability.ToDefaultStream(Typeability.Normalize("Þór STRAẞE Ærø")), Is.EqualTo("thor strasse aero"));
        }

        [Test]
        public void TheMarkersStillSurviveBesideASpelledLetter()
        {
            Assert.That(Typeability.Normalize("stra|ße &", keepFreestyleMarkers: true, keepSplitMarkers: true), Is.EqualTo("stra|sse &"));
        }

        [Test]
        public void AnAlignerLineDecodesWithItsLettersAndKeepsItsWordTimings()
        {
            // A lyriclab timing.json carries the raw lyric, and the .osu an import writes re-emits
            // it verbatim, so this is the exact text a German map's decode sees.
            const string json =
                """
                {"version":2,"song_end_ms":9000,"lines":[
                  {"text":"Straße nach Łódź","start_ms":1000,"end_ms":4000,"words":[
                    {"text":"Straße","start_ms":1000,"end_ms":2000},
                    {"text":"nach","start_ms":2000,"end_ms":3000},
                    {"text":"Łódź","start_ms":3000,"end_ms":4000}]}]}
                """;

            Assert.That(TimingJsonLoader.TryParse(json, out var lines), Is.True);
            Assert.That(lines, Has.Count.EqualTo(1));
            Assert.That(lines[0].RawText, Is.EqualTo("Strasse nach Lodz"));
            Assert.That(lines[0].Units.Select(u => u.Text), Is.EqualTo(new[] { "Strasse", "nach", "Lodz" }));

            // The word pairing survives the longer spelling: each word keeps its aligned start.
            Assert.That(lines[0].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 1000.0, 2000.0, 3000.0 }));
        }
    }
}
