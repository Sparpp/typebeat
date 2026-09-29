// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 329: <see cref="Romaniser"/>. The per-script fixtures under
    /// <c>NonVisual/fixtures/romaniser</c> are SHARED with the server's
    /// <c>tests/Typebeat.WireCompat/RomaniserParityTest.cs</c>, which runs them through both copies.
    /// </summary>
    [TestFixture]
    public class RomaniserTest
    {
        public sealed class Fixture
        {
            public string File = string.Empty;
            public int Line;
            public string? Language;
            public string Source = string.Empty;
            public string Expected = string.Empty;
            public string[] Unromanised = Array.Empty<string>();

            public override string ToString() => $"{File}:{Line}";
        }

        public static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "NonVisual", "fixtures", "romaniser");

        public static IEnumerable<Fixture> LoadFixtures()
        {
            foreach (string path in Directory.GetFiles(FixtureDirectory, "*.tsv").OrderBy(p => p, StringComparer.Ordinal))
            {
                string[] lines = System.IO.File.ReadAllLines(path);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0 || lines[i].StartsWith('#'))
                        continue;

                    string[] cols = lines[i].Split('\t');

                    yield return new Fixture
                    {
                        File = Path.GetFileName(path),
                        Line = i + 1,
                        Language = cols[0].Length == 0 ? null : cols[0],
                        Source = cols[1],
                        Expected = cols[2],
                        Unromanised = cols.Length > 3 ? cols[3].Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>(),
                    };
                }
            }
        }

        private static IEnumerable<TestCaseData> fixtureCases()
            => LoadFixtures().Select(f => new TestCaseData(f).SetName($"Fixture({f})"));

        [TestCaseSource(nameof(fixtureCases))]
        public void FixtureRomanisesAsExpected(Fixture f)
        {
            var result = Romaniser.Romanise(f.Source, f.Language);

            Assert.That(result.Text, Is.EqualTo(f.Expected));
            Assert.That(result.Unromanised, Is.EqualTo(f.Unromanised));
            Assert.That(result.IsComplete, Is.EqualTo(f.Unromanised.Length == 0));
        }

        /// <summary>
        /// THE CONTRACT: a complete result is a fixed point of <see cref="Typeability.Normalize"/>, and
        /// every romanised (unflagged) unit of any result is typeable text or a supported mark.
        /// </summary>
        [TestCaseSource(nameof(fixtureCases))]
        public void OutputRoundTripsThroughNormalize(Fixture f)
        {
            var result = Romaniser.Romanise(f.Source, f.Language);

            if (result.IsComplete)
                Assert.That(Typeability.Normalize(result.Text), Is.EqualTo(result.Text));

            foreach (var unit in result.Units.Where(u => !u.Flagged))
            {
                string piece = result.Text.Substring(unit.TextStart, unit.TextLength);
                Assert.That(piece.All(c => Typeability.IsTypeable(c) && c != ' ' || Typeability.IsPunctuation(c)), Is.True, $"unit text \"{piece}\"");
            }
        }

        /// <summary>
        /// The units tile the source (whitespace aside) in order, and each one's text run is where it
        /// says in <see cref="Romaniser.Result.Text"/>: what the importer's per-syllable mapping reads.
        /// </summary>
        [TestCaseSource(nameof(fixtureCases))]
        public void UnitsTileTheSource(Fixture f)
        {
            var result = Romaniser.Romanise(f.Source, f.Language);
            int source = 0;
            int text = 0;

            foreach (var unit in result.Units)
            {
                while (source < unit.SourceStart)
                {
                    Assert.That(char.IsWhiteSpace(f.Source[source]) || f.Source[source] == '・', Is.True, $"gap at {source}");
                    source++;
                }

                Assert.That(unit.SourceLength, Is.GreaterThan(0));
                Assert.That(unit.TextStart, Is.GreaterThanOrEqualTo(text));
                source = unit.SourceStart + unit.SourceLength;
                text = unit.TextStart + unit.TextLength;

                if (unit.Flagged)
                    Assert.That(result.Text.Substring(unit.TextStart, unit.TextLength), Is.EqualTo(f.Source.Substring(unit.SourceStart, unit.SourceLength)));
            }

            Assert.That(f.Source.Substring(source).All(c => char.IsWhiteSpace(c)), Is.True);
            Assert.That(text, Is.EqualTo(result.Text.Length));
        }

        [Test]
        public void EveryScriptHasFixtures()
        {
            var byFile = LoadFixtures().GroupBy(f => f.File).ToDictionary(g => g.Key, g => g.Count());

            Assert.That(byFile.Keys, Is.EquivalentTo(new[]
            {
                "armenian.tsv", "cyrillic.tsv", "georgian.tsv", "greek.tsv", "japanese.tsv", "korean.tsv", "latin.tsv", "unromanised.tsv",
            }));
            Assert.That(byFile.Values.All(n => n >= 4), Is.True);
        }

        [TestCase("닭", "dak")]
        [TestCase("없", "eop")]
        [TestCase("읽", "ik")]
        [TestCase("꽃", "kkot")]
        [TestCase("좋", "jot")]
        [TestCase("앉", "an")]
        [TestCase("삶", "sam")]
        [TestCase("밟", "bal")]
        [TestCase("닭", "dak")] // the same block, decomposed into conjoining jamo
        public void HangulBatchimReadsAsTheIsolatedSyllable(string block, string expected)
            => Assert.That(Romaniser.Romanise(block).Text, Is.EqualTo(expected));

        [TestCase("きゃ", "kya")]
        [TestCase("しゅ", "shu")]
        [TestCase("ちょ", "cho")]
        [TestCase("じゃ", "ja")]
        [TestCase("ぢゅ", "ju")]
        [TestCase("りょ", "ryo")]
        [TestCase("ぴゃ", "pya")]
        [TestCase("デュ", "dyu")]
        [TestCase("フォ", "fo")]
        [TestCase("ゃ", "ya")]
        [TestCase("っきゃ", "kkya")]
        [TestCase("っしゃ", "ssha")]
        [TestCase("っちゃ", "tcha")]
        [TestCase("っじゃ", "jja")]
        [TestCase("あっ", "a")]
        [TestCase("が", "ga")] // decomposed dakuten recomposes
        [TestCase("いすゞ", "isuzu")]
        public void KanaDigraphsAndSokuon(string kana, string expected)
            => Assert.That(Romaniser.Romanise(kana).Text, Is.EqualTo(expected));

        [TestCase("λόγος", "logos")]
        [TestCase("ΛΟΓΟΣ", "LOGOS")]
        [TestCase("Σς", "Ss")]
        [TestCase("σοφός", "sofos")]
        public void GreekFinalSigma(string word, string expected)
            => Assert.That(Romaniser.Romanise(word, "greek").Text, Is.EqualTo(expected));

        [TestCase("serbian", "Џеп", "Dzep")]
        [TestCase("serbian", "џем", "dzem")]
        [TestCase("sr", "ЏЕП", "DzEP")]
        [TestCase("macedonian", "џеб", "djeb")]
        [TestCase(null, "џ", "dz")]
        public void SerbianDzhe(string? language, string word, string expected)
            => Assert.That(Romaniser.Romanise(word, language).Text, Is.EqualTo(expected));

        [TestCase("ſ", "s")]
        [TestCase("Maſs", "Mass")]
        [TestCase("ƒ", "f")]
        [TestCase("Ƒ", "F")]
        [TestCase("ħ", "h")]
        [TestCase("Ħ", "H")]
        [TestCase("ŧ", "t")]
        [TestCase("Ŧ", "T")]
        public void StepALeftovers(string word, string expected)
        {
            var result = Romaniser.Romanise(word);
            Assert.That(result.Text, Is.EqualTo(expected));
            Assert.That(result.IsComplete, Is.True);
            Assert.That(Typeability.Normalize(word), Is.Not.EqualTo(expected), "Normalize alone must still delete it (its table is frozen)");
        }

        [Test]
        public void LatinExtrasStayOutOfTheFrozenNormalizeTable()
        {
            foreach (var (letter, spelled) in Romaniser.LATIN_EXTRAS)
            {
                Assert.That(Typeability.SPECIAL_LETTERS.ContainsKey(letter), Is.False, letter.ToString());
                Assert.That(Typeability.Normalize(spelled), Is.EqualTo(spelled), letter.ToString());
            }

            foreach (var (mark, ascii) in Romaniser.PUNCTUATION_MARKS)
                Assert.That(ascii.All(Typeability.IsPunctuation), Is.True, mark.ToString());
        }

        [TestCase("สวัสดี")]
        [TestCase("ไทย")]
        [TestCase("漢字")]
        public void UnknownScriptIsFlaggedNeverEmptied(string word)
        {
            var result = Romaniser.Romanise(word, "other");

            Assert.That(result.Text, Is.Not.Empty);
            Assert.That(result.IsComplete, Is.False);
            Assert.That(result.Unromanised, Is.Not.Empty);
            Assert.That(result.Units.All(u => u.Flagged), Is.True);
        }

        [Test]
        public void SymbolsAreDroppedWithoutAFlag()
        {
            var result = Romaniser.Romanise("♪ さくら ♪");
            Assert.That(result.Text, Is.EqualTo("sakura"));
            Assert.That(result.IsComplete, Is.True);
        }

        [TestCase("", false, false)]
        [TestCase("hello", false, false)]
        [TestCase("123", false, false)]
        [TestCase("café", false, false)]
        [TestCase("Straße", false, false)]
        [TestCase("♪", false, false)]
        [TestCase("ħ", false, true)]
        [TestCase("ə", false, true)]
        [TestCase("Ｈｅｌｌｏ", false, true)]
        [TestCase("１２", false, true)]
        [TestCase("hi、", false, true)]
        [TestCase("«hi»", false, true)]
        [TestCase("привет", true, true)]
        [TestCase("Ωμέγα", true, true)]
        [TestCase("かな", true, true)]
        [TestCase("カタカナ", true, true)]
        [TestCase("한국", true, true)]
        [TestCase("ქართული", true, true)]
        [TestCase("Հայ", true, true)]
        [TestCase("漢字", true, true)]
        [TestCase("สวัสดี", true, true)]
        [TestCase("love привет", true, true)]
        public void HasNonLatinAndNeedsRomanisingTruthTable(string text, bool hasNonLatin, bool needsRomanising)
        {
            Assert.That(Romaniser.HasNonLatin(text), Is.EqualTo(hasNonLatin), "HasNonLatin");
            Assert.That(Romaniser.NeedsRomanising(text), Is.EqualTo(needsRomanising), "NeedsRomanising");
        }

        /// <summary>
        /// The per-unit mapping the lyric importer needs: one kana mora (a digraph included) or one
        /// hangul block is one unit with a known run of Latin letters.
        /// </summary>
        [Test]
        public void UnitsMapEachMoraAndBlockToItsLetters()
        {
            Assert.That(spans(Romaniser.Romanise("きゃりー")), Is.EqualTo(new[] { "0+2=kya", "2+1=ri", "3+1=i" }));
            Assert.That(spans(Romaniser.Romanise("まっちゃ")), Is.EqualTo(new[] { "0+1=ma", "1+1=t", "2+2=cha" }));
            Assert.That(spans(Romaniser.Romanise("사랑해")), Is.EqualTo(new[] { "0+1=sa", "1+1=rang", "2+1=hae" }));
            Assert.That(spans(Romaniser.Romanise("του", "greek")), Is.EqualTo(new[] { "0+1=t", "1+2=ou" }));
            Assert.That(spans(Romaniser.Romanise("ｶﾞｯ")), Is.EqualTo(new[] { "0+2=ga", "2+1=" }));
            Assert.That(spans(Romaniser.Romanise("été")), Is.EqualTo(new[] { "0+2=e", "2+1=t", "3+1=e" }));

            static string[] spans(Romaniser.Result r)
                => r.Units.Select(u => $"{u.SourceStart}+{u.SourceLength}={r.Text.Substring(u.TextStart, u.TextLength)}").ToArray();
        }

        [TestCase("ukrainian")]
        [TestCase("UK")]
        [TestCase(" Ukrainian ")]
        public void LanguageIsReadByNameOrCode(string language)
            => Assert.That(Romaniser.Romanise("Київ", language).Text, Is.EqualTo("Kyiv"));

        [TestCase(null)]
        [TestCase("")]
        [TestCase("russian")]
        [TestCase("klingon")]
        [TestCase("japanese")]
        public void UnknownOrNonCyrillicLanguageReadsCyrillicAsRussian(string? language)
            => Assert.That(Romaniser.Romanise("Жизнь", language).Text, Is.EqualTo("Zhizn"));

        [Test]
        public void TheMetadataNameSelectsTheTable()
            => Assert.That(Romaniser.Romanise("жизнь", BeatmapLanguage.Russian.ToCanonicalName()).Text, Is.EqualTo("zhizn"));
    }
}
