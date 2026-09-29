// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 330, the IMPORTER half: non-Latin lyrics are romanised instead of deleted, the source
    /// is kept as the original, the language is detected and offered, a source's own transliteration
    /// is preferred, syllable cuts are carried through the romaniser's units, and words the romaniser
    /// cannot spell import flagged (saved, listed, never submitted).
    /// </summary>
    [TestFixture]
    public class OriginalTextImportTest
    {
        private string tempRoot = null!;

        [SetUp]
        public void SetUp()
        {
            LyricBeatmapDecoder.Register();
            tempRoot = Path.Combine(Path.GetTempPath(), "typebeat_original_text_test", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(tempRoot, true);
            }
            catch
            {
                // best effort
            }
        }

        private static IReadOnlyList<LyricLine> load(string? timingJson)
        {
            Assert.That(timingJson, Is.Not.Null);
            Assert.That(TimingJsonLoader.TryParse(timingJson!, out var lines), Is.True);
            return lines;
        }

        private (LyricImportResult Result, Beatmap Beatmap, string Osu) package(string timingJson, string? language)
        {
            string audioPath = Path.Combine(tempRoot, "A - B.mp3");
            File.WriteAllText(audioPath, "fake audio");

            string oszPath = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".osz");
            var result = LyricMapImporter.PackageOsz(oszPath, "A", "B", audioPath, timingJson, "unused", language: language);
            Assert.That(result.Success, Is.True, result.Error);

            using var archive = ZipFile.OpenRead(oszPath);
            using var reader = new StreamReader(archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)).Open());
            string osu = reader.ReadToEnd();

            return (result, OriginalTextFormatTest.Decode(osu), osu);
        }

        #region Russian LRC

        private const string russian_lrc = "[00:01.00]Расцветали яблони и груши\n[00:05.00]Поплыли туманы над рекой\n[00:09.00]\n";

        [Test]
        public void ARussianLrcImportsRomanisedWithItsOriginals()
        {
            string? language = LyricOriginals.DetectLanguage(new[] { russian_lrc });
            Assert.That(language, Is.EqualTo("russian"));

            var lines = load(LyricMapImporter.SynthesizeTimingJsonFromLrc(russian_lrc, language));

            Assert.That(lines.Select(l => l.RawText), Is.EqualTo(new[] { "Rastsvetali yabloni i grushi", "Poplyli tumany nad rekoy" }));
            Assert.That(lines[0].Original, Is.EqualTo("Расцветали яблони и груши"));
            Assert.That(lines[0].Units.Select(u => u.Original), Is.EqualTo(new[] { "Расцветали", "яблони", "и", "груши" }));
            Assert.That(lines[1].Units.Select(u => u.Original), Is.EqualTo(new[] { "Поплыли", "туманы", "над", "рекой" }));
            Assert.That(lines.SelectMany(l => l.UnromanisedWords), Is.Empty);

            // Timed exactly as the romanised text alone would be: an original never moves a time.
            var plain = load(LyricMapImporter.SynthesizeTimingJsonFromLrc("[00:01.00]Rastsvetali yabloni i grushi\n[00:05.00]Poplyli tumany nad rekoy\n[00:09.00]\n"));
            Assert.That(lines[0].Units.Select(u => u.StartTime), Is.EqualTo(plain[0].Units.Select(u => u.StartTime)));
            Assert.That(lines[0].SingEndTime, Is.EqualTo(plain[0].SingEndTime));
        }

        [Test]
        public void TheDetectedLanguageIsOfferedAsTheMapsLanguage()
        {
            var (result, beatmap, osu) = package(LyricMapImporter.SynthesizeTimingJsonFromLrc(russian_lrc, "russian")!, "russian");

            Assert.That(osu, Does.Contain("Language:russian"));
            Assert.That(beatmap.Metadata.Language, Is.EqualTo(BeatmapLanguage.Russian));
            Assert.That(result.Notice, Is.Null, "nothing was left unromanised");
            Assert.That(LyricOriginals.HasOriginals(beatmap), Is.True);

            // A language the list lacks is offered as Other, and a map with no originals gets none.
            Assert.That(LyricOriginals.OfferedLanguage("greek"), Is.EqualTo(BeatmapLanguage.Other));
            var (_, _, english) = package(LyricMapImporter.SynthesizeTimingJsonFromLrc("[00:01.00]hello world\n[00:03.00]\n")!, null);
            Assert.That(english, Does.Not.Contain("Language:"));
            Assert.That(english, Does.Not.Contain("original"));
        }

        [TestCase("Расцветали яблони", "russian")]
        [TestCase("Ще не вмерла України", "ukrainian")]
        [TestCase("Ђурђевдан је", "serbian")]
        [TestCase("さくら さくら 桜", "japanese")]
        [TestCase("我爱你", "chinese")]
        [TestCase("사랑해", "korean")]
        [TestCase("Καλημέρα κόσμε", "greek")]
        [TestCase("hello café", null)]
        public void TheLanguageIsDetectedFromTheScript(string text, string? expected)
            => Assert.That(LyricOriginals.DetectLanguage(new[] { text }), Is.EqualTo(expected));

        #endregion

        #region Japanese TTML

        private const string japanese_ttml =
            "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\" xml:lang=\"ja\">"
            + "<head><metadata><iTunesMetadata xmlns=\"http://music.apple.com/lyric-ttml-internal\"><transliterations><transliteration xml:lang=\"ja-Latn\">"
            + "<text for=\"L1\"><span xmlns=\"http://www.w3.org/ns/ttml\" begin=\"1.000\" end=\"1.600\">sakura</span> <span xmlns=\"http://www.w3.org/ns/ttml\" begin=\"1.600\" end=\"2.400\">sakura</span></text>"
            + "<text for=\"L2\"><span xmlns=\"http://www.w3.org/ns/ttml\" begin=\"3.000\" end=\"3.500\">kimi</span> <span xmlns=\"http://www.w3.org/ns/ttml\" begin=\"3.500\" end=\"4.000\">ga</span> <span xmlns=\"http://www.w3.org/ns/ttml\" begin=\"4.000\" end=\"5.000\">suki</span></text>"
            + "</transliteration></transliterations></iTunesMetadata></metadata></head>"
            + "<body dur=\"12.000\"><div>"
            // A kana line WITH a transliteration, word for word.
            + "<p begin=\"1.000\" end=\"2.400\" itunes:key=\"L1\"><span begin=\"1.000\" end=\"1.200\">さ</span><span begin=\"1.200\" end=\"1.400\">く</span><span begin=\"1.400\" end=\"1.600\">ら</span> <span begin=\"1.600\" end=\"2.400\">さくら</span></p>"
            // A kanji line written without spaces, whose transliteration is spaced: paired by time.
            + "<p begin=\"3.000\" end=\"5.000\" itunes:key=\"L2\"><span begin=\"3.000\" end=\"3.500\">君</span><span begin=\"3.500\" end=\"4.000\">が</span><span begin=\"4.000\" end=\"5.000\">好き</span></p>"
            // A katakana line with NO transliteration: the romaniser, with the source's syllables.
            + "<p begin=\"6.000\" end=\"7.000\"><span begin=\"6.000\" end=\"6.300\">コー</span><span begin=\"6.300\" end=\"6.600\">ヒ</span><span begin=\"6.600\" end=\"7.000\">ー</span></p>"
            // A kanji the romaniser cannot read, with no transliteration: flagged.
            + "<p begin=\"8.000\" end=\"9.000\"><span begin=\"8.000\" end=\"8.500\">君</span> <span begin=\"8.500\" end=\"9.000\">さくら</span></p>"
            + "</div></body></tt>";

        [Test]
        public void AJapaneseTtmlPrefersItsOwnTransliteration()
        {
            var lines = load(LyricMapImporter.SynthesizeTimingJsonFromTtml(japanese_ttml, language: "japanese"));
            Assert.That(lines.Count, Is.EqualTo(4));

            // Word for word: each transliterated word takes its source word as the original.
            Assert.That(lines[0].RawText, Is.EqualTo("sakura sakura"));
            Assert.That(lines[0].Units.Select(u => u.Original), Is.EqualTo(new[] { "さくら", "さくら" }));
            Assert.That(lines[0].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 1000.0, 1600.0 }));
            Assert.That(lines[0].Original, Is.EqualTo("さくら さくら"));

            // The kanji line reads through the transliteration (the romaniser could not), paired by time.
            Assert.That(lines[1].RawText, Is.EqualTo("kimi ga suki"));
            Assert.That(lines[1].Units.Select(u => u.Original), Is.EqualTo(new[] { "君", "が", "好き" }));
            Assert.That(lines[1].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 3000.0, 3500.0, 4000.0 }));
            Assert.That(lines[1].Original, Is.EqualTo("君が好き"));
            Assert.That(lines[1].UnromanisedWords, Is.Empty);
        }

        /// <summary>
        /// THE SYLLABLE MAPPING, the long-vowel rule: コー|ヒ|ー is "koohii" with ONE carried cut,
        /// "koo|hii": the cut before the lone ー is dropped and that syllable merges backward, so its
        /// time boundary (6600) goes with it.
        /// </summary>
        [Test]
        public void ARomanisedWordCarriesTheSourceSyllablesThroughTheUnits()
        {
            var line = load(LyricMapImporter.SynthesizeTimingJsonFromTtml(japanese_ttml, language: "japanese"))[2];

            Assert.That(line.RawText, Is.EqualTo("koohii"));
            Assert.That(line.Units.Single().Original, Is.EqualTo("コーヒー"));
            Assert.That(line.Units.Single().SyllableBoundaries, Is.EqualTo(new[] { 6300.0 }));
            Assert.That(line.Units.Single().SyllableSplits, Is.EqualTo(new[] { 3 }));
            Assert.That(SyllableSegments.SegmentTexts("koohii", line.Units.Single().SyllableSplits), Is.EqualTo(new[] { "koo", "hii" }));
        }

        [Test]
        public void AKanjiWithNoReadingImportsFlagged()
        {
            string timing = LyricMapImporter.SynthesizeTimingJsonFromTtml(japanese_ttml, language: "japanese")!;
            var line = load(timing)[3];

            Assert.That(line.RawText, Is.EqualTo("sakura"));
            Assert.That(line.Units.Single().Original, Is.EqualTo("さくら"));
            Assert.That(line.UnromanisedWords, Is.EqualTo(new[] { new UnromanisedWord(0, "君", 8000, 8500) }));

            var (result, beatmap, _) = package(timing, "japanese");

            Assert.That(result.Notice, Does.Contain("1 word could not be romanised"));
            Assert.That(result.Notice, Does.Contain("君"));
            Assert.That(beatmap.Metadata.Language, Is.EqualTo(BeatmapLanguage.Japanese));
            Assert.That(new TypeBeatRuleset().GetSubmissionRefusal(beatmap), Does.Contain("君"));
        }

        #endregion

        #region Korean timing.json

        private const string korean_timing =
            "{\"version\":2,\"lines\":[{\"text\":\"사랑해 아리랑\",\"start_ms\":1000,\"end_ms\":3000,\"words\":["
            + "{\"text\":\"사랑해\",\"start_ms\":1000,\"end_ms\":2000,\"syllables\":[{\"text\":\"사\",\"start_ms\":1000,\"end_ms\":1300},{\"text\":\"랑\",\"start_ms\":1300,\"end_ms\":1600},{\"text\":\"해\",\"start_ms\":1600,\"end_ms\":2000}]},"
            + "{\"text\":\"아리랑\",\"start_ms\":2000,\"end_ms\":3000}]}]}";

        /// <summary>
        /// An aligner document in hangul: one block is one syllable and maps to a known Latin run
        /// (사 sa, 랑 rang, 해 hae), so the aligner's own syllables land at "sa|rang|hae".
        /// </summary>
        [Test]
        public void AKoreanTimingJsonImportsWithItsSyllablesCarried()
        {
            string romanised = LyricMapImporter.RomaniseLines(korean_timing, LyricOriginals.DetectLanguage(new[] { korean_timing }));
            var line = load(romanised).Single();

            Assert.That(line.RawText, Is.EqualTo("saranghae arirang"));
            Assert.That(line.Original, Is.EqualTo("사랑해 아리랑"));
            Assert.That(line.Units.Select(u => u.Original), Is.EqualTo(new[] { "사랑해", "아리랑" }));
            Assert.That(line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1300.0, 1600.0 }));
            Assert.That(line.Units[0].SyllableSplits, Is.EqualTo(new[] { 2, 6 }));
            Assert.That(SyllableSegments.SegmentTexts("saranghae", line.Units[0].SyllableSplits), Is.EqualTo(new[] { "sa", "rang", "hae" }));
            Assert.That(line.Units[1].StartTime, Is.EqualTo(2000));

            // The line key sits straight after the text, as every writer puts it.
            Assert.That(romanised, Does.Contain("\"text\":\"saranghae arirang\",\"original\":"));
        }

        [Test]
        public void ADocumentWithNothingToRomaniseIsReturnedVerbatim()
        {
            const string english = "{\"version\":2,\"lines\":[{\"text\":\"hello world\",\"start_ms\":1000,\"end_ms\":2000,\"words\":[{\"text\":\"hello\",\"start_ms\":1000,\"end_ms\":1500}]}]}";
            Assert.That(LyricMapImporter.RomaniseLines(english, null), Is.SameAs(english));
        }

        /// <summary>
        /// THE SOKUON RULE on the aligner path: か|っ|た is "katta" cut "kat|ta", the lone っ merging
        /// BACKWARD (its cut dropped, the syllable before it taking its end).
        /// </summary>
        [Test]
        public void ALoneSokuonMergesIntoTheSyllableBeforeIt()
        {
            const string json = "{\"version\":2,\"lines\":[{\"text\":\"かった\",\"start_ms\":0,\"end_ms\":900,\"words\":["
                                + "{\"text\":\"かった\",\"start_ms\":0,\"end_ms\":900,\"syllables\":[{\"text\":\"か\",\"start_ms\":0,\"end_ms\":300},{\"text\":\"っ\",\"start_ms\":300,\"end_ms\":600},{\"text\":\"た\",\"start_ms\":600,\"end_ms\":900}]}]}]}";

            var unit = load(LyricMapImporter.RomaniseLines(json, "japanese")).Single().Units.Single();

            Assert.That(unit.Text, Is.EqualTo("katta"));
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 600.0 }));
            Assert.That(SyllableSegments.SegmentTexts("katta", unit.SyllableSplits), Is.EqualTo(new[] { "kat", "ta" }));
        }

        #endregion

        #region Flagged words

        [Test]
        public void AChineseLrcKeepsItsLinesForTheMapperToRomanise()
        {
            const string lrc = "[00:01.00]我爱你\n[00:04.00]hello 世界 world\n[00:08.00]\n";

            string timing = LyricMapImporter.SynthesizeTimingJsonFromLrc(lrc, LyricOriginals.DetectLanguage(new[] { lrc }))!;
            var lines = load(timing);

            Assert.That(lines.Count, Is.EqualTo(2), "a line of nothing but unromanised words is kept, not dropped");
            Assert.That(lines[0].RawText, Is.Empty);
            Assert.That(lines[0].UnromanisedWords.Single().Original, Is.EqualTo("我爱你"));
            Assert.That(lines[0].Original, Is.EqualTo("我爱你"));

            Assert.That(lines[1].RawText, Is.EqualTo("hello world"));
            Assert.That(lines[1].UnromanisedWords.Single(), Is.EqualTo(lines[1].UnromanisedWords.Single() with { Position = 1, Original = "世界" }));
            Assert.That(lines[1].UnromanisedWords.Single().StartTime, Is.GreaterThanOrEqualTo(lines[1].Units[0].EndTime));
            Assert.That(lines[1].UnromanisedWords.Single().EndTime, Is.LessThanOrEqualTo(lines[1].Units[1].StartTime));

            var (result, beatmap, _) = package(timing, "chinese");

            Assert.That(result.Notice, Does.Contain("2 words could not be romanised"));
            Assert.That(result.Notice, Does.Contain("我爱你, 世界"));
            Assert.That(beatmap.Metadata.Language, Is.EqualTo(BeatmapLanguage.Chinese));

            // SAVED: the map survives an editor save and reload with every flagged word in place.
            var reloaded = OriginalTextFormatTest.Decode(OriginalTextFormatTest.Encode(beatmap));
            Assert.That(LyricOriginals.UnromanisedWords(reloaded.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line)), Is.EqualTo(new[] { "我爱你", "世界" }));

            // NOT SUBMITTED: the ruleset's veto names them.
            string? refusal = new TypeBeatRuleset().GetSubmissionRefusal(reloaded);
            Assert.That(refusal, Does.Contain("2 words with no romanisation yet"));
            Assert.That(refusal, Does.Contain("我爱你"));
        }

        /// <summary>
        /// An aligner line with no words[] that the romaniser cannot spell at all keeps only its
        /// original: it still survives, is listed, and still blocks submission, exactly as the
        /// server's parse lists and refuses it.
        /// </summary>
        [Test]
        public void AWordlessLineTheRomaniserCannotSpellIsStillListed()
        {
            const string json = "{\"version\":2,\"lines\":[{\"text\":\"我爱你\",\"start_ms\":0,\"end_ms\":900},{\"text\":\"yeah\",\"start_ms\":1000,\"end_ms\":1900}]}";

            var lines = load(LyricMapImporter.RomaniseLines(json, "chinese"));

            Assert.That(lines.Select(l => l.RawText), Is.EqualTo(new[] { string.Empty, "yeah" }));
            Assert.That(lines[0].Original, Is.EqualTo("我爱你"));
            Assert.That(lines[0].UnromanisedWords, Is.Empty);
            Assert.That(LyricOriginals.UnromanisedWords(lines), Is.EqualTo(new[] { "我爱你" }));

            var (result, beatmap, _) = package(LyricMapImporter.RomaniseLines(json, "chinese"), "chinese");
            Assert.That(result.Notice, Does.Contain("我爱你"));
            Assert.That(new TypeBeatRuleset().GetSubmissionRefusal(beatmap), Does.Contain("我爱你"));
        }

        [Test]
        public void AMapWithoutFlaggedWordsMaySubmit()
            => Assert.That(new TypeBeatRuleset().GetSubmissionRefusal(package(LyricMapImporter.SynthesizeTimingJsonFromLrc(russian_lrc, "russian")!, "russian").Beatmap), Is.Null);

        #endregion
    }
}
