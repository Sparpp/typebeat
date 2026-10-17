// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JapaneseLyricCorrectionsTest
    {
        [TestCase("kitto anata ha kizukanai", "あなた")]
        [TestCase("mata anata ni", "また")]
        [TestCase("kakeru yume", "かける")]
        public void KanaPreferencesAlsoApplyInPhrases(string romaji, string expected)
            => Assert.That(JapaneseLyricGenerator.Generate(romaji), Does.Contain(expected));

        [TestCase("一人で2")]
        [TestCase("一人 で2")]
        public void OriginalAlignmentDoesNotDiscardUnmatchedNumbers(string original)
        {
            var (_, hit) = create("hitori de", "ひとり で");
            Assert.That(OriginalLyricGenerator.MatchWords(hit.Line, original, BeatmapLanguage.Japanese), Is.All.Null);
        }

        [TestCase("hitori", "ひとり", "一人")]
        [TestCase("hitoride", "ひとりで", "一人で")]
        public void CorrectingHitoriToKanjiPreservesItsReadingAndTiming(string romaji, string kana, string kanji)
        {
            var (map, hit) = create(romaji, kana);
            Assert.That(TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, kanji), Is.True);
            Assert.That(hit.Line.RawText, Is.EqualTo(romaji));
            Assert.That(hit.Line.Units.Single().Original, Is.EqualTo(kanji));
            Assert.That(hit.Line.Units.Single().StartTime, Is.EqualTo(1000));
            var plan = JapaneseInputPlan.Create(kanji, hit.Line);
            Assert.That(string.Concat(plan.Groups.Select(g => g.Reading)), Is.EqualTo(kana));
        }

        [Test]
        public void KanjiCorrectionsInTheMainLineBoxAlsoKeepTheAuthoredReading()
        {
            var (map, hit) = create("hitori", "ひとり");
            TypeBeatEditorOperations.SetLineText(map, hit, "一人");
            Assert.That(hit.Line.RawText, Is.EqualTo("hitori"));
            Assert.That(hit.Line.Units.Single().Original, Is.EqualTo("一人"));
            Assert.That(hit.Line.Units.Single().StartTime, Is.EqualTo(1000));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void WordCorrectionsUpdateTheUnspacedLineCaption(bool lineGranularity)
        {
            var (map, hit) = create("hitori de", "ひとり で");
            if (lineGranularity)
            {
                var source = hit.Line;
                hit.Granularity = TimingGranularity.Line;
                hit.Line = new LyricLine
                {
                    RawText = source.RawText, Original = source.Original, StartTime = source.StartTime,
                    EndTime = source.EndTime, SingEndTime = source.SingEndTime,
                    Units = LrcParser.InterpolateUnits(source.RawText, source.StartTime, source.SingEndTime)
                        .Select((u, i) => LrcParser.WithOriginal(u, source.Units[i].Original)).ToArray(),
                };
            }
            TypeBeatEditorOperations.SetLineOriginal(map, hit, "ひとりで");
            TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "一人");
            Assert.That(hit.Line.Original, Is.EqualTo("一人で"));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("一人で"));
            var saved = OriginalTextFormatTest.Decode(OriginalTextFormatTest.Encode(map)).HitObjects.OfType<TypeBeatHitObject>().Single().Line;
            Assert.That(saved.Original, Is.EqualTo("一人で"));
            Assert.That(saved.RawText, Is.EqualTo("hitori de"));
            Assert.That(TypingLine.ForMods(saved, false, true, "japanese", japaneseCharacterTiming: true).DisplayText, Is.EqualTo("一人で"));
        }

        [TestCase("watashi ga mino hodo shirazu ni koi wo shita kara")]
        [TestCase("watashi ga mi no hodo shirazu ni koi wo shita kara")]
        [TestCase("watashi ga minohodoshirazu ni koi wo shita kara")]
        public void CorrectedUnspacedLineReachesGameplayWithoutChangingRomaji(string romaji)
        {
            var (map, hit) = create(romaji, "わたし が みの ほど しらず に こい を した から");
            const string corrected = "私が身の程知らずに恋をしたから";
            Assert.That(TypeBeatEditorOperations.SetLineOriginal(map, hit, corrected), Is.True);
            Assert.That(hit.Line.RawText, Is.EqualTo(romaji));
            Assert.That(string.Concat(hit.Line.Units.Select(u => u.Original)), Is.EqualTo(corrected));
            Assert.That(TypingLine.ForMods(hit.Line, false, true, "japanese", japaneseCharacterTiming: true).DisplayText, Is.EqualTo(corrected));
            var saved = OriginalTextFormatTest.Decode(OriginalTextFormatTest.Encode(map)).HitObjects.OfType<TypeBeatHitObject>().Single().Line;
            Assert.That(saved.RawText, Is.EqualTo(romaji));
            Assert.That(string.Concat(saved.Units.Select(u => u.Original)), Is.EqualTo(corrected));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void AnAlreadyCorrectedCaptionRepairsStaleGameplayOriginals(bool reopenEditor)
        {
            var (map, hit) = create("hitori de", "ひとり で");
            hit.Line = new LyricLine
            {
                RawText = hit.Line.RawText, Original = "「一人」で。", Units = hit.Line.Units,
                StartTime = hit.Line.StartTime, EndTime = hit.Line.EndTime, SingEndTime = hit.Line.SingEndTime,
            };
            if (reopenEditor)
                TypeBeatEditorOperations.NormalizeOriginalSubdivisions(map);
            else
                Assert.That(TypeBeatEditorOperations.SetLineOriginal(map, hit, "「一人」で。"), Is.True);
            Assert.That(string.Concat(hit.Line.Units.Select(u => u.Original)), Is.EqualTo("「一人」で。"));
            Assert.That(hit.Line.RawText, Is.EqualTo("hitori de"));
        }

        [Test]
        public void LineCorrectionsPreserveAlternateReadingsAndJapanesePunctuation()
        {
            var (map, hit) = create("hitori de", "ひとり で");
            const string corrected = "「一人」で。、";
            TypeBeatEditorOperations.SetLineOriginal(map, hit, corrected);
            Assert.That(hit.Line.RawText, Is.EqualTo("hitori de"));
            Assert.That(string.Concat(hit.Line.Units.Select(u => u.Original)), Is.EqualTo(corrected));
            Assert.That(TypingLine.ForMods(hit.Line, true, true, "japanese").DisplayText, Is.EqualTo(corrected));
            Assert.That(TypingLine.ForMods(hit.Line, false, true, "japanese").DisplayText, Is.EqualTo("一人で"));
            var engine = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Punctuation", AudioFileName = "audio.mp3", FolderPath = "" },
                Granularity = TimingGranularity.Word, Lines = new[] { hit.Line },
            }, literate: true, polyglot: true, polyglotLanguage: "japanese");
            engine.Update(1000);
            foreach (char c in "[hitori]de.,")
                Assert.That(engine.ProcessKey(c, 1200), Is.True, c.ToString());
            Assert.That(engine.Lines.Single().Cells.All(c => c.State == CellState.Correct), Is.True);
            Assert.That(engine.Mistypes, Is.Zero);
        }

        private static (EditorBeatmap Map, TypeBeatHitObject Hit) create(string romaji, string original)
        {
            LyricBeatmapDecoder.Register();
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Language = BeatmapLanguage.Japanese;
            beatmap.Metadata.Artist = "Test";
            beatmap.Metadata.Title = "Japanese corrections";
            beatmap.Metadata.AudioFile = "audio.mp3";
            string[] words = romaji.Split(' '), originals = original.Split(' ');
            var hit = new TypeBeatHitObject
            {
                StartTime = 1000, Granularity = TimingGranularity.Word,
                Line = new LyricLine
                {
                    RawText = romaji, Original = original, StartTime = 1000, EndTime = 1000 + words.Length * 1000,
                    SingEndTime = 500 + words.Length * 1000,
                    Units = words.Select((w, i) => new TimedUnit
                    {
                        Text = w, Original = originals.Length == words.Length ? originals[i] : null,
                        StartTime = 1000 + i * 1000, EndTime = 1500 + i * 1000, Source = TimingSource.Explicit,
                    }).ToArray(),
                },
            };
            beatmap.HitObjects.Add(hit);
            return (new EditorBeatmap(beatmap), hit);
        }
    }
}
