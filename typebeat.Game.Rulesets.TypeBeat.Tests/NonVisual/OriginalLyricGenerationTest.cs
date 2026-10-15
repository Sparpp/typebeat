// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class OriginalLyricGenerationTest
    {
        [TestCase(BeatmapLanguage.English, false)]
        [TestCase(BeatmapLanguage.Unspecified, false)]
        [TestCase(BeatmapLanguage.Instrumental, false)]
        [TestCase(BeatmapLanguage.Japanese, true)]
        [TestCase(BeatmapLanguage.Chinese, true)]
        [TestCase(BeatmapLanguage.Korean, true)]
        [TestCase(BeatmapLanguage.French, true)]
        [TestCase(BeatmapLanguage.Other, true)]
        public void AvailabilityFollowsTheSelectedLanguage(BeatmapLanguage language, bool enabled)
            => Assert.That(OriginalLyricGenerator.CanGenerate(language), Is.EqualTo(enabled));

        [TestCase(BeatmapLanguage.Japanese, "konnichiha sekai", "こんにちは世界")]
        [TestCase(BeatmapLanguage.Japanese, "konnichiwa sekai", "こんにちは世界")]
        [TestCase(BeatmapLanguage.Korean, "annyeong", "안녕")]
        [TestCase(BeatmapLanguage.Russian, "Privet mir", "Привет мир")]
        [TestCase(BeatmapLanguage.French, "bonjour cafe", "bonjour café")]
        [TestCase(BeatmapLanguage.Chinese, "ni hao", "你好")]
        public void RecoversTheOriginalScript(BeatmapLanguage language, string romanized, string original)
            => Assert.That(OriginalLyricGenerator.MatchLines(new[] { line(romanized) }, original, language), Is.EqualTo(new[] { original }));

        [Test]
        public void MatchesRepeatedVersesAndSkipsUnmappedLinesInOrder()
        {
            var lines = new[] { line("privet mir"), line("privet mir"), line("kafe") };
            var originals = OriginalLyricGenerator.MatchLines(lines, "Привет мир\nлишняя строка\nПривет мир\nкафе", BeatmapLanguage.Russian);
            Assert.That(originals, Is.EqualTo(new[] { "Привет мир", "Привет мир", "кафе" }));
        }

        [Test]
        public void DoesNotGuessAnUnrelatedTranslationOrShortWord()
        {
            Assert.That(OriginalLyricGenerator.MatchLines(new[] { line("hello world") }, "Привет мир", BeatmapLanguage.Russian).Single(), Is.Null);
            Assert.That(OriginalLyricGenerator.MatchLines(new[] { line("mir") }, "мор", BeatmapLanguage.Russian).Single(), Is.Null);
        }

        [Test]
        public void UsesSyncedTimesForScriptsWithoutLocalReadings()
        {
            var originals = OriginalLyricGenerator.MatchLines(new[] { line("ni hao", 10000) }, "[00:02.00]世界\n[00:10.20]你好", BeatmapLanguage.Chinese);
            Assert.That(originals.Single(), Is.EqualTo("你好"));
            Assert.That(OriginalLyricGenerator.MatchLines(new[] { line("ni hao", 20000) }, "[00:10.20]你好", BeatmapLanguage.Chinese).Single(), Is.Null);
        }

        [Test]
        public void RecoversWordBoundariesInsideUnspacedJapanese()
        {
            Assert.That(OriginalLyricGenerator.MatchWords(line("konnichiha sekai"), "こんにちは世界", BeatmapLanguage.Japanese),
                Is.EqualTo(new[] { "こんにちは", "世界" }));
        }

        [Test]
        public void AmbiguousWordBoundariesUseTheLineOriginal()
        {
            Assert.That(OriginalLyricGenerator.MatchWords(line("konnichiwa sekai"), "こんにちは世界", BeatmapLanguage.Japanese),
                Is.EqualTo(new string?[] { null, null }));
        }

        [Test]
        public void GenerationPreservesAuthoredDataAndSurvivesSaving()
        {
            LyricBeatmapDecoder.Register();
            var map = createMap();
            var targets = TypeBeatEditorOperations.OrderedLines(map);
            var snapshot = targets.Select(h => h.Line).ToArray();
            Assert.That(TypeBeatEditorOperations.AddGeneratedOriginals(map, targets, snapshot, new[] { "こんにちは世界", "Привет мир" }), Is.EqualTo(2));

            for (int i = 0; i < targets.Count; i++)
            {
                var before = snapshot[i];
                var after = targets[i].Line;
                Assert.That(after.RawText, Is.EqualTo(before.RawText));
                Assert.That(after.StartTime, Is.EqualTo(before.StartTime));
                Assert.That(after.EndTime, Is.EqualTo(before.EndTime));
                Assert.That(after.SingEndTime, Is.EqualTo(before.SingEndTime));
                Assert.That(after.SealGraceMs, Is.EqualTo(before.SealGraceMs));
                Assert.That(after.Estimated, Is.EqualTo(before.Estimated));

                for (int w = 0; w < before.Units.Count; w++)
                {
                    var oldWord = before.Units[w];
                    var newWord = after.Units[w];
                    Assert.That(newWord.Text, Is.EqualTo(oldWord.Text));
                    Assert.That(newWord.StartTime, Is.EqualTo(oldWord.StartTime));
                    Assert.That(newWord.EndTime, Is.EqualTo(oldWord.EndTime));
                    Assert.That(newWord.Source, Is.EqualTo(oldWord.Source));
                    Assert.That(newWord.Confidence, Is.EqualTo(oldWord.Confidence));
                    Assert.That(newWord.SyllableBoundaries, Is.EqualTo(oldWord.SyllableBoundaries));
                    Assert.That(newWord.SyllableSplits, Is.EqualTo(oldWord.SyllableSplits));
                    Assert.That(newWord.Pauses, Is.EqualTo(oldWord.Pauses));
                }
            }

            Assert.That(targets[0].Line.Units.Select(u => u.Original), Is.EqualTo(new[] { "こんにちは", "世界" }));
            Assert.That(LyricOriginals.HasOriginals(map), Is.True);
            Assert.That(new Mods.TypeBeatModPolyglot().IsAvailableFor(new Game.Tests.Beatmaps.TestWorkingBeatmap(map)), Is.True);
            var saved = OriginalTextFormatTest.Decode(OriginalTextFormatTest.Encode(map));
            Assert.That(saved.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line.Original), Is.EqualTo(new[] { "こんにちは世界", "Привет мир" }));
        }

        [Test]
        public void StaleLookupCannotOverwriteEditsOrRemovedLines()
        {
            var map = createMap();
            var targets = TypeBeatEditorOperations.OrderedLines(map);
            var snapshot = targets.Select(h => h.Line).ToArray();
            targets[0].Line = line("different lyric");
            Assert.That(TypeBeatEditorOperations.AddGeneratedOriginals(map, targets, snapshot, new[] { "こんにちは世界", "Привет мир" }), Is.Zero);
            Assert.That(LyricOriginals.HasOriginals(map), Is.False);
        }

        [Test]
        public void ExistingOriginalsAreKeptWhenGeneratingMissingLines()
        {
            var map = createMap();
            var targets = TypeBeatEditorOperations.OrderedLines(map);
            TypeBeatEditorOperations.SetLineOriginal(map, targets[0], "手書きの歌詞");
            Assert.That(TypeBeatEditorOperations.AddGeneratedOriginals(map, targets, targets.Select(h => h.Line).ToArray(), new[] { "こんにちは世界", "Привет мир" }), Is.EqualTo(1));
            Assert.That(targets[0].Line.Original, Is.EqualTo("手書きの歌詞"));
        }

        [TestCase("konnichiwa sekai", "こんにちは 世界")]
        [TestCase("konnichiha sekai!", "こんにちは 世界!")]
        [TestCase("watashi wa kimi o aishiteru", "私 は 君 を 愛してる")]
        [TestCase("tabeta", "食べた")]
        [TestCase("koohii", "コーヒー")]
        [TestCase("ko-hi-", "コーヒー")]
        [TestCase("\"sekai\"", "\"世界\"")]
        [TestCase("Sin'ya", "深夜")]
        [TestCase("xyaxyuxyo", "ゃゅょ")]
        [TestCase("qwerty sekai", "qwerty 世界")]
        [TestCase("yoru no taiyou ni", "夜 の 太陽 に")]
        [TestCase("fuan tei ni", "不安 定 に")]
        [TestCase("fuwa fuwa fuwa fuwa", "ふわ ふわ ふわ ふわ")]
        [TestCase("kura kura kura kura", "くら くら くら くら")]
        [TestCase("utsutta watashi to", "映った 私 と")]
        [TestCase("zaratto shita kankaku no saigo ni", "ざらっと した 感覚 の 最後 に")]
        [TestCase("kotoriya", "小鳥や")]
        public async Task JapaneseUsesTheBundledDictionaryWithoutMetadataOrInternet(string romaji, string expected)
        {
            using var handler = new FakeHandler(_ => throw new AssertionException("Japanese generation must not contact LRCLIB."));
            using var http = new HttpClient(handler);
            var originals = await OriginalLyricGenerator.GenerateAsync(http, "", "", BeatmapLanguage.Japanese,
                new[] { line(romaji) }, CancellationToken.None).ConfigureAwait(false);
            Assert.That(originals.Single(), Is.EqualTo(expected));
        }

        [TestCase("shi moji", "下地", 2)]
        [TestCase("ai ni kuwareta", "あいにく", 3)]
        public async Task JapanesePhraseContextCannotChangeAuthoredWordBoundaries(string text, string unrelated, int words)
        {
            var originals = await OriginalLyricGenerator.GenerateAsync("", "", BeatmapLanguage.Japanese,
                new[] { line(text) }, CancellationToken.None).ConfigureAwait(false);
            Assert.That(originals.Single(), Does.Not.Contain(unrelated));
            Assert.That(originals.Single()!.Split(' '), Has.Length.EqualTo(words));
        }

        [Test]
        public async Task JapaneseDoesNotReplaceLoanwordsWithCurrencySymbols()
        {
            var originals = await OriginalLyricGenerator.GenerateAsync("", "", BeatmapLanguage.Japanese,
                new[] { line("kudannai endorooru haigo ni") }, CancellationToken.None).ConfigureAwait(false);
            Assert.That(originals.Single(), Does.Contain("エンドロール"));
            Assert.That(originals.Single(), Does.Not.Contain("￥"));
        }

        [Test]
        public async Task JapaneseCompoundLookupPreservesAuthoredWordTiming()
        {
            var map = createMap();
            var target = TypeBeatEditorOperations.OrderedLines(map)[0];
            target.Line = line("fuan tei ni");
            var targets = TypeBeatEditorOperations.OrderedLines(map);
            var snapshot = targets.Select(h => h.Line).ToArray();
            var originals = await OriginalLyricGenerator.GenerateAsync("", "", BeatmapLanguage.Japanese, snapshot, CancellationToken.None).ConfigureAwait(false);
            Assert.That(TypeBeatEditorOperations.AddGeneratedOriginals(map, targets, snapshot, originals), Is.EqualTo(1));
            Assert.That(target.Line.Units.Select(u => u.Original), Is.EqualTo(new[] { "不安", "定", "に" }));
            Assert.That(JapaneseInputPlan.Create("不安定に", target.Line).Groups.Any(g => g.Reading == "てい"), Is.True);
            Assert.That(target.Line.Units.Select(u => (u.Text, u.StartTime, u.EndTime, u.SyllableSplits)),
                Is.EqualTo(snapshot[0].Units.Select(u => (u.Text, u.StartTime, u.EndTime, u.SyllableSplits))));
        }

        [Test]
        public async Task JapaneseLeavesUndecodableLinesAndExistingOriginalsAlone()
        {
            var map = createMap();
            var target = TypeBeatEditorOperations.OrderedLines(map)[0];
            TypeBeatEditorOperations.SetLineOriginal(map, target, "手書きの歌詞");
            var originals = await OriginalLyricGenerator.GenerateAsync("", "", BeatmapLanguage.Japanese,
                new[] { target.Line, line("qwerty"), line("世界") }, CancellationToken.None).ConfigureAwait(false);
            Assert.That(originals, Is.EqualTo(new string?[] { null, null, null }));
        }

        [Test]
        public void JapaneseGenerationHonoursCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.ThrowsAsync<OperationCanceledException>(async () => await OriginalLyricGenerator.GenerateAsync("", "", BeatmapLanguage.Japanese,
                new[] { line("sekai") }, cancellation.Token).ConfigureAwait(false));
        }

        [Test]
        public async Task DictionaryOriginalsPreserveWordsAndSurviveSaving()
        {
            LyricBeatmapDecoder.Register();
            var map = createMap();
            var target = TypeBeatEditorOperations.OrderedLines(map)[0];
            target.Line = line("konnichiwa sekai");
            var snapshot = TypeBeatEditorOperations.OrderedLines(map).Select(h => h.Line).ToArray();
            var originals = await OriginalLyricGenerator.GenerateAsync("", "", BeatmapLanguage.Japanese, snapshot, CancellationToken.None).ConfigureAwait(false);
            Assert.That(TypeBeatEditorOperations.AddGeneratedOriginals(map, TypeBeatEditorOperations.OrderedLines(map), snapshot, originals), Is.EqualTo(1));
            Assert.That(target.Line.RawText, Is.EqualTo("konnichiwa sekai"));
            Assert.That(target.Line.Units.Select(u => u.Original), Is.EqualTo(new[] { "こんにちは", "世界" }));
            Assert.That(target.Line.Units.Select(u => (u.StartTime, u.EndTime, u.SyllableSplits)),
                Is.EqualTo(snapshot[0].Units.Select(u => (u.StartTime, u.EndTime, u.SyllableSplits))));
            var saved = OriginalTextFormatTest.Decode(OriginalTextFormatTest.Encode(map));
            Assert.That(saved.HitObjects.OfType<TypeBeatHitObject>().First().Line.Original, Is.EqualTo("こんにちは 世界"));
            Assert.That(LyricOriginals.HasOriginals(saved), Is.True);
        }

        [Test]
        public async Task LookupUsesEscapedMetadataAndRejectsTheWrongArtist()
        {
            using var handler = new FakeHandler(request =>
            {
                Assert.That(request.RequestUri!.Query, Does.Contain("artist_name=A%26B"));
                Assert.That(request.RequestUri.Query, Does.Contain("track_name=Song%20%231"));
                Assert.That(request.Headers.UserAgent.ToString(), Does.Contain("typebeat"));
                return new[]
                {
                    new { artistName = "Wrong artist", trackName = "Song #1", plainLyrics = "ПРИВЕТ МИР" },
                    new { artistName = "A&B", trackName = "Song #1", plainLyrics = "Привет мир" },
                };
            });
            using var http = new HttpClient(handler);
            var originals = await OriginalLyricGenerator.GenerateAsync(http, "A&B", "Song #1", BeatmapLanguage.Russian, new[] { line("Privet mir") }, CancellationToken.None).ConfigureAwait(false);
            Assert.That(originals.Single(), Is.EqualTo("Привет мир"));
        }

        [Test]
        public async Task LookupRejectsLyricsInADifferentScript()
        {
            using var handler = new FakeHandler(_ => new[] { new { artistName = "Artist", trackName = "Song", plainLyrics = "こんにちは世界" } });
            using var http = new HttpClient(handler);
            var originals = await OriginalLyricGenerator.GenerateAsync(http, "Artist", "Song", BeatmapLanguage.Korean, new[] { line("konnichiha sekai") }, CancellationToken.None).ConfigureAwait(false);
            Assert.That(originals.Single(), Is.Null);
        }

        private static EditorBeatmap createMap()
        {
            var map = new Beatmap();
            map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            map.Metadata.Language = BeatmapLanguage.Japanese;
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 1000, LineIndex = 0, Line = line("konnichiha sekai"), Granularity = TimingGranularity.Syllable });
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 4000, LineIndex = 1, Line = line("Privet mir", 4000), Granularity = TimingGranularity.Word });
            return new EditorBeatmap(map);
        }

        private static LyricLine line(string text, double start = 1000) => new LyricLine
        {
            RawText = text,
            StartTime = start,
            EndTime = start + 3000,
            SingEndTime = start + 2500,
            SealGraceMs = 120,
            Estimated = true,
            Units = text.Split(' ').Select((word, i) => new TimedUnit
            {
                Text = word,
                StartTime = start + i * 1000,
                EndTime = start + (i + 1) * 1000,
                Source = TimingSource.Explicit,
                Confidence = 0.8,
                SyllableBoundaries = new[] { start + i * 1000 + 500 },
                SyllableSplits = new[] { 2 },
            }).ToArray(),
        };

        private sealed class FakeHandler(Func<HttpRequestMessage, object> response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response(request))) });
        }
    }
}
