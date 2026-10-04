// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JapaneseWapuroTest
    {
        private static IEnumerable<TestCaseData> kanaSpellings()
        {
            // Independent, pinned upstream data: do not derive expected readings from the game.
            string path = Path.Combine(AppContext.BaseDirectory, "NonVisual", "fixtures", "japanese-input", "mozc-romaji.tsv");
            var rows = File.ReadAllLines(path).Where(l => l.Length > 0).Distinct().Select(l => l.Split('\t')).ToArray();
            var completed = rows.Where(r => r.Length == 2).ToDictionary(r => r[0], r => JapaneseRomaji.Hiragana(r[1]));
            foreach (var row in rows)
            {
                string kana = JapaneseRomaji.Hiragana(row[1]);
                if (!kana.All(c => c is >= 'ぁ' and <= 'ゖ' or 'ー'))
                    continue;
                string input = row[0];
                if (row.Length > 2 && row[2].Length > 0)
                {
                    // Mozc emits small tsu and keeps a consonant pending (qq -> っ + q,
                    // tch -> っ + ch). Complete that pending syllable with a vowel.
                    input += "a";
                    kana += completed[row[2] + "a"];
                }
                yield return new TestCaseData(kana, input);
            }
        }

        [TestCaseSource(nameof(kanaSpellings))]
        public void EveryMozcKanaSpellingMatchesIncrementally(string kana, string input)
        {
            for (int i = 1; i <= input.Length; i++)
                Assert.That(JapaneseRomaji.Matches(kana, input[..i]), Is.Not.EqualTo(JapaneseRomaji.Match.Invalid), input[..i]);
            Assert.That(JapaneseRomaji.Matches(kana, input) & JapaneseRomaji.Match.Complete, Is.EqualTo(JapaneseRomaji.Match.Complete));
        }

        [TestCaseSource(nameof(kanaSpellings))]
        public void EveryMozcKanaSpellingWorksInGameplayForBothScripts(string kana, string input)
        {
            string katakana = new string(kana.Select(c => c is >= 'ぁ' and <= 'ゖ' ? (char)(c + 0x60) : c).ToArray());
            foreach (string original in new[] { kana, katakana })
            {
                var beatmap = new LyricBeatmap
                {
                    Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", AudioFileName = "a.mp3", FolderPath = string.Empty },
                    Granularity = TimingGranularity.Word,
                    Lines = new[]
                    {
                        new LyricLine
                        {
                            RawText = input, Original = original, StartTime = 0, EndTime = 5000, SingEndTime = 3000,
                            Units = new[] { new TimedUnit { Text = input, Original = original, StartTime = 1000, EndTime = 3000 } },
                        },
                    },
                };
                var engine = new TypingEngine(beatmap, polyglot: true, polyglotLanguage: "japanese");
                engine.Update(1000);
                foreach (char c in input)
                    Assert.That(engine.ProcessKey(c, 1200), Is.True, original + ": " + c);
                if (engine.JapanesePending.Length > 0)
                    Assert.That(engine.ProcessEnter(1250), Is.True, "confirm a terminal nasal");
                Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True, original);
                Assert.That(engine.Mistypes, Is.Zero);
            }
        }

        [TestCaseSource(nameof(kanaSpellings))]
        public void EveryMozcKanaSpellingDecodesForAuthoredReadings(string kana, string input)
            => Assert.That(JapaneseRomaji.ReadAuthored(input), Is.EqualTo(kana));
    }
}
