// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class OriginalSubdivisionEditorTest
    {
        [SetUp]
        public void Setup() => LyricBeatmapDecoder.Register();

        [Test]
        public void RomajiCutsRemainAndOnlyAlignedCutsAppearInTheOriginal()
        {
            var (map, hit) = create("shika", "しか", new[] { 1, 3 }, new[] { 300d, 900d });
            TypeBeatEditorOperations.NormalizeOriginalSubdivisions(map);
            Assert.That(hit.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 1, 3 }));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 300d, 900d }));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line, "japanese"), Is.EqualTo("s|hi|ka"));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("し|か"));
            var before = hit.Line;
            TypeBeatEditorOperations.NormalizeOriginalSubdivisions(map);
            Assert.That(hit.Line, Is.SameAs(before), "reconciliation is idempotent");
        }

        [TestCase("shi", "し", 1)]
        [TestCase("watashi", "私", 3)]
        [TestCase("kya", "きゃ", 1)]
        public void AWordWithNoMappableCutShowsNoSubdivision(string romaji, string original, int split)
        {
            var (map, hit) = create(romaji, original, new[] { split }, new[] { 750d });
            TypeBeatEditorOperations.NormalizeOriginalSubdivisions(map);
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 750d }));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo(original));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line, "japanese"), Is.EqualTo(romaji.Insert(split, "|")));
        }

        [Test]
        public void MovingAPipeInEitherScriptMovesTheOtherWithoutRetiming()
        {
            var (map, hit) = create("aiu", "あいう", new[] { 1 }, new[] { 700d });
            Assert.That(TypeBeatEditorOperations.SetLineOriginal(map, hit, "あい|う"), Is.True);
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line, "japanese"), Is.EqualTo("ai|u"));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 700d }));
            TypeBeatEditorOperations.SetLineText(map, hit, "a|iu");
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("あ|いう"));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 700d }));
            Assert.That(hit.Line.Original, Is.EqualTo("あいう"));
        }

        [Test]
        public void OriginalPipesOverrideExistingCutsAndCanAddOrRemoveThem()
        {
            var (map, hit) = create("aiu", "あいう", new[] { 1 }, new[] { 700d });
            TypeBeatEditorOperations.SetLineOriginal(map, hit, "あ|い|う");
            Assert.That(hit.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 700d, 1350d }));
            TypeBeatEditorOperations.SetLineOriginal(map, hit, "あいう");
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.Empty);
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo("aiu"));
        }

        [Test]
        public void WordScriptRowsEditTheSameMarkersAndPreserveTheAuthoredSpelling()
        {
            var (map, hit) = create("sika", "しか", new[] { 2 }, new[] { 700d });
            Assert.That(TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "し|か"), Is.False, "an unchanged displayed word is a no-op");
            Assert.That(hit.Line.RawText, Is.EqualTo("sika"), "editing markers must not replace a valid alternate input spelling");
            TypeBeatEditorOperations.SetWordText(map, hit, 0, "sh|ika");
            Assert.That(hit.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 2 }), "the lower row may split inside し");
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("しか"));
            TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "し|か");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo("sh|i|ka"));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 700d, 1350d }));
        }

        [Test]
        public void OriginalCharactersAreSpreadAcrossTheSharedTimingSpans()
        {
            var (_, hit) = create("aiu", "あいう", new[] { 1 }, new[] { 700d });
            var projection = OriginalLyricSubdivisions.Project(hit.Line.Units[0], "japanese");
            var runs = PausedWord.DisplayRuns(projection.Text, projection, projection.StartTime, projection.EndTime);
            Assert.That(runs.Select(r => r.Text), Is.EqualTo(new[] { "あ", "いう" }));
            Assert.That(runs.Select(r => (r.StartTime, r.EndTime)), Is.EqualTo(new[] { (0d, 700d), (700d, 2000d) }));
        }

        [Test]
        public void PausedOriginalsKeepTheirGapAndMoveTheSharedCut()
        {
            var (map, hit) = create("aiu", "あいう");
            hit.Line = new LyricLine
            {
                RawText = "aiu", Original = "あいう", StartTime = 0, EndTime = 3000, SingEndTime = 2000,
                Units = new[] { new TimedUnit
                {
                    Text = "aiu", Original = "あいう", StartTime = 0, EndTime = 2000,
                    Pauses = new[] { new WordPause(600, 900, 1) }, Source = TimingSource.Explicit,
                } },
            };
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("あ|いう"));
            Assert.That(OriginalLyricSubdivisions.PipeText(hit.Line.Units[0], "japanese", true), Is.EqualTo("あ|いう"));
            var projection = OriginalLyricSubdivisions.Project(hit.Line.Units[0], "japanese");
            var runs = PausedWord.DisplayRuns(projection.Text, projection, 0, 2000, Typeability.IsPolyglotTypeCell);
            Assert.That(runs.Select(r => (r.Text, r.StartTime, r.EndTime)), Is.EqualTo(new[] { ("あ", 0d, 600d), ("いう", 900d, 2000d) }));
            TypeBeatEditorOperations.SetLineOriginal(map, hit, "あい|う");
            Assert.That(hit.Line.Units[0].Pauses.Single(), Is.EqualTo(new WordPause(600, 900, 2)));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line, "japanese"), Is.EqualTo("ai|u"));
        }

        [Test]
        public void LineOnlyOriginalsAcquireWordLinksWithoutRemovingRomajiCuts()
        {
            var (map, hit) = create("shika", null, new[] { 1, 3 }, new[] { 300d, 900d });
            hit.Line = new LyricLine
            {
                RawText = hit.Line.RawText, Original = "しか", StartTime = 0, EndTime = 3000,
                SingEndTime = 2000, Units = hit.Line.Units,
            };
            TypeBeatEditorOperations.NormalizeOriginalSubdivisions(map);
            Assert.That(hit.Line.Units.Single().Original, Is.EqualTo("しか"));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("し|か"));
            Assert.That(hit.Line.Units.Single().SyllableBoundaries, Is.EqualTo(new[] { 300d, 900d }));
        }

        [Test]
        public void AlphabeticOriginalsHideCutsInsideExpandedLetters()
        {
            var (map, hit) = create("Strasse", "Straße", new[] { 4, 5 }, new[] { 600d, 1200d });
            map.Metadata.Language = BeatmapLanguage.German;
            TypeBeatEditorOperations.NormalizeOriginalSubdivisions(map);
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line, "german"), Is.EqualTo("Stra|s|se"));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "german"), Is.EqualTo("Stra|ße"));
            TypeBeatEditorOperations.SetLineOriginal(map, hit, "Straß|e");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line, "german"), Is.EqualTo("Stras|s|e"));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1200d, 1600d }));
        }

        [TestCase("あいうしか")]
        [TestCase("あいう しか")]
        public void LinkedWordMarkersKeepTheOriginalLinesAuthoredSpacing(string original)
        {
            var (map, hit) = create("aiu", "あいう", new[] { 1 }, new[] { 700d });
            hit.Line = new LyricLine
            {
                RawText = "aiu shika", Original = original, StartTime = 0, EndTime = 6000, SingEndTime = 5000,
                Units = new[] { hit.Line.Units[0], new TimedUnit
                {
                    Text = "shika", Original = "しか", StartTime = 3000, EndTime = 5000,
                    SyllableSplits = new[] { 3 }, SyllableBoundaries = new[] { 4000d }, Source = TimingSource.Explicit,
                } },
            };
            string expected = original.Replace("あいう", "あ|いう").Replace("しか", "し|か");
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo(expected));
            TypeBeatEditorOperations.SetLineOriginal(map, hit, expected.Replace("あ|いう", "あい|う"));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line, "japanese"), Is.EqualTo("ai|u shi|ka"));
            Assert.That(hit.Line.Original, Is.EqualTo(original));
        }

        [Test]
        public void OriginalScriptSubdivideButtonCutsAtACharacterBoundary()
        {
            var (map, hit) = create("watashiha", "私は");
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(map, hit, 0, originalLyrics: true), Is.Not.Null);
            Assert.That(hit.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 7 }));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("私|は"));
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(map, hit, 0, caretTime: 500, originalLyrics: true), Is.Null,
                "a single kanji cannot be subdivided further");
        }

        [Test]
        public void ANewOriginalLineCarriesItsMarkersOntoRomanizedText()
        {
            var (map, hit) = create("hello", null);
            map.Metadata.Language = BeatmapLanguage.Russian;
            TypeBeatEditorOperations.SetLineText(map, hit, "При|вет");
            Assert.That(hit.Line.RawText, Is.EqualTo("Privet"));
            Assert.That(hit.Line.Original, Is.EqualTo("Привет"));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line, "russian"), Is.EqualTo("Pri|vet"));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "russian"), Is.EqualTo("При|вет"));
        }

        [Test]
        public void OriginalSpacingAndLineOnlyOriginalsSurviveAndMarkersAreNotStoredAsLyric()
        {
            var (map, hit) = create("shika", "しか", new[] { 3 }, new[] { 700d });
            TypeBeatEditorOperations.SetLineOriginal(map, hit, "し|か");
            string encoded = OriginalTextFormatTest.Encode(map);
            Assert.That(encoded, Does.Not.Contain("|"));
            var reloaded = OriginalTextFormatTest.Decode(encoded).HitObjects.OfType<TypeBeatHitObject>().Single().Line;
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(reloaded, "japanese"), Is.EqualTo("し|か"));
            Assert.That(reloaded.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 700d }));
        }

        [Test]
        public void AttachingOrCorrectingAnOriginalWithoutMovingItsPipesPreservesRomajiOnlyCuts()
        {
            var (map, hit) = create("watashi", null, new[] { 2, 4 }, new[] { 500d, 1000d });
            TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "私");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo("wa|ta|shi"));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("私"));
            TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "[私].,");
            Assert.That(hit.Line.Units[0].Original, Is.EqualTo("「私」。、"));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 500d, 1000d }));
        }

        [TestCase(BeatmapLanguage.Japanese, "[あ],.", "「あ」、。")]
        [TestCase(BeatmapLanguage.German, "[ä],.", "[ä],.")]
        public void OriginalLineInputUsesOnlyTheSelectedLanguagesPunctuation(BeatmapLanguage language, string input, string expected)
        {
            var (map, hit) = create("a", null);
            map.Metadata.Language = language;
            TypeBeatEditorOperations.SetLineOriginal(map, hit, input);
            Assert.That(hit.Line.Original, Is.EqualTo(expected));
        }

        [Test]
        public void EmptyOriginalFieldsAreAssignedToEveryWordAndSurviveSaving()
        {
            var (map, hit) = create("aiu", null, new[] { 1 }, new[] { 700d });
            TypeBeatEditorOperations.SetLineText(map, hit, "a|iu shika");
            var before = hit.Line;
            Assert.That(TypeBeatEditorOperations.AddOriginalScript(map), Is.True);
            Assert.That(TypeBeatEditorOperations.AddOriginalScript(map), Is.False);
            Assert.That(hit.Line.Units.Select(u => u.Original), Is.EqualTo(new[] { "", "" }));
            Assert.That(LyricOriginals.HasOriginals(map), Is.False, "blank fields alone must not enable Polyglot gameplay");
            Assert.That(hit.Line.Units.Select(u => (u.Text, u.StartTime, u.EndTime)),
                Is.EqualTo(before.Units.Select(u => (u.Text, u.StartTime, u.EndTime))));
            var saved = OriginalTextFormatTest.Decode(OriginalTextFormatTest.Encode(map)).HitObjects.OfType<TypeBeatHitObject>().Single().Line;
            Assert.That(saved.Units.Select(u => u.Original), Is.EqualTo(new[] { "", "" }));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(saved), Is.EqualTo("a|iu shika"));
            TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "あいう");
            Assert.That(hit.Line.Units[1].Original, Is.Empty);
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo("a|iu shika"));
            Assert.That(LyricOriginals.HasOriginals(map), Is.True);
        }

        [Test]
        public void EnglishMapsCannotAddOriginalScriptFields()
        {
            var (map, _) = create("word", null);
            map.Metadata.Language = BeatmapLanguage.English;
            Assert.That(TypeBeatEditorOperations.AddOriginalScript(map), Is.False);
        }

        [TestCase("watashi", "私")]
        [TestCase("shika", "しか")]
        public void AutomaticallyGeneratedImportSubdivisionsStayAtOriginalBoundaries(string romaji, string original)
        {
            var (_, hit) = create(romaji, original);
            var imported = ImportSyllables.Apply(hit.Line.Units.Single(), "japanese");
            Assert.That(imported.SyllableSplits.All(c => OriginalLyricSubdivisions.SourceCut(imported, c, "japanese").HasValue), Is.True);
            var (_, authored) = create(romaji, original, new[] { 1 }, new[] { 500d });
            Assert.That(ImportSyllables.Apply(authored.Line.Units.Single()), Is.SameAs(authored.Line.Units.Single()), "authored cuts are retained");
        }

        [Test]
        public void AutomaticImportCutsAlsoRespectLineOnlyOriginalsAndTimingJson()
        {
            var (_, hit) = create("watashi", null);
            var lineOnly = new LyricLine
            {
                RawText = "watashi", Original = "私", StartTime = 0, EndTime = 3000, SingEndTime = 2000, Units = hit.Line.Units,
            };
            var imported = ImportSyllables.Apply(new[] { lineOnly }, "japanese").Single();
            Assert.That(imported.Units.Single().SyllableBoundaries, Is.Empty);
            const string json = """
                {"version":2,"lines":[{"text":"watashi","original":"私","start_ms":0,"end_ms":2000,
                "words":[{"text":"watashi","start_ms":0,"end_ms":2000,"score":1}]}]}
                """;
            Assert.That(TimingJsonLoader.TryParse(ImportSyllables.ApplyToTimingJson(json, "japanese"), out var parsed), Is.True);
            Assert.That(parsed.Single().Units.Single().SyllableBoundaries, Is.Empty);
        }

        [TestCase("jigokugoku", "地獄国")]
        [TestCase("jigokugoku!", "地獄国！")]
        public void SongSpecificReadingProjectsEachValidCutIndependently(string romaji, string original)
        {
            var (_, hit) = create(romaji, original, new[] { 2, 4, 6, 8 }, new[] { 300d, 600d, 900d, 1200d });
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"),
                Is.EqualTo(original.Insert(2, "|").Insert(1, "|")));
            var projected = OriginalLyricSubdivisions.Project(hit.Line.Units[0], "japanese");
            Assert.That(projected.SyllableSplits, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(projected.SyllableBoundaries, Is.EqualTo(new[] { 300d, 900d }));
            Assert.That(hit.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 2, 4, 6, 8 }));
        }

        [Test]
        public void TententengokuLineKeepsItsSavedCutsAndPolyglotTargets()
        {
            var (map, hit) = create("jigokugoku!", "地獄国！");
            hit.Line = new LyricLine
            {
                RawText = "tententengoku jigokugoku!", Original = "天天天国地獄国！",
                StartTime = 12485.571428571428, EndTime = 14985.571428571428, SingEndTime = 13985.571428571428,
                Units = new[]
                {
                    new TimedUnit
                    {
                        Text = "tententengoku", Original = "天天天国", StartTime = 12485.571428571428, EndTime = 13342.714285714284,
                        SyllableSplits = new[] { 3, 6, 9, 11 },
                        SyllableBoundaries = new[] { 12699.857142857143, 12914.142857142857, 13128.42857142857, 13235.571428571428 },
                    },
                    new TimedUnit
                    {
                        Text = "jigokugoku!", Original = "地獄国！", StartTime = 13342.714285714284, EndTime = 13985.571428571428,
                        SyllableSplits = new[] { 2, 4, 6, 8 },
                        SyllableBoundaries = new[] { 13449.857142857143, 13557d, 13664.142857142857, 13771.285714285714 },
                    },
                },
            };
            TypeBeatEditorOperations.NormalizeOriginalSubdivisions(map);
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("天|天|天|国地|獄|国！"));
            var played = TypingLine.ForMods(hit.Line, false, true, "japanese", japaneseCharacterTiming: true);
            Assert.That(played.Cells.Skip(4).Select(c => c.TargetTime),
                Is.EqualTo(new[] { 13342.714285714284, 13449.857142857143, 13664.142857142857 }).Within(0.000001));
            var reloaded = OriginalTextFormatTest.Decode(OriginalTextFormatTest.Encode(map)).HitObjects.OfType<TypeBeatHitObject>().Single().Line;
            Assert.That(reloaded.Units[1].SyllableSplits, Is.EqualTo(new[] { 2, 4, 6, 8 }));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(reloaded, "japanese"), Is.EqualTo("天|天|天|国地|獄|国！"));
        }

        [TestCase("kya", "きゃ", "き|ゃ", "k|ya", "き|ゃ", new[] { 300d })]
        [TestCase("kyaku", "きゃく", "き|ゃ|く", "k|ya|ku", "き|ゃ|く", new[] { 300d, 1150d })]
        public void AnExplicitOriginalMarkerCanLinkAnOtherwiseUnrepresentableCut(string text, string original, string marked,
                                                                          string expectedRomaji, string expectedOriginal, double[] times)
        {
            var (map, hit) = create(text, original, new[] { 1 }, new[] { 300d });
            TypeBeatEditorOperations.SetLineOriginal(map, hit, marked);
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo(expectedRomaji));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(times));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo(expectedOriginal));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AddingAndRemovingOriginalCutsPreservesRomajiOnlyCuts(bool wordRow)
        {
            var (map, hit) = create("shika", "しか", new[] { 1 }, new[] { 300d });
            if (wordRow)
                TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "し|か");
            else
                TypeBeatEditorOperations.SetLineOriginal(map, hit, "し|か");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo("s|hi|ka"));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 300d, 1150d }));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("し|か"));
            if (wordRow)
                TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "しか");
            else
                TypeBeatEditorOperations.SetLineOriginal(map, hit, "しか");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo("s|hika"));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 300d }));
        }

        [Test]
        public void MovingAnOriginalCutKeepsHiddenRomajiSubdivisionsAndTheirTimes()
        {
            var (map, hit) = create("shikai", "しかい", new[] { 1, 3 }, new[] { 300d, 900d });
            TypeBeatEditorOperations.SetLineOriginal(map, hit, "しか|い");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo("s|hika|i"));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 300d, 900d }));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("しか|い"));
        }

        [Test]
        public void MovingAnOriginalCutKeepsHiddenRomajiPauses()
        {
            var (map, hit) = create("shikai", "しかい", new[] { 3 }, new[] { 900d });
            var u = hit.Line.Units[0];
            hit.Line = new LyricLine
            {
                RawText = "shikai", Original = "しかい", StartTime = 0, EndTime = 3000, SingEndTime = 2000,
                Units = new[] { new TimedUnit
                {
                    Text = u.Text, Original = u.Original, StartTime = u.StartTime, EndTime = u.EndTime,
                    SyllableSplits = u.SyllableSplits, SyllableBoundaries = u.SyllableBoundaries,
                    Pauses = new[] { new WordPause(300, 500, 1) },
                } },
            };
            TypeBeatEditorOperations.SetLineOriginal(map, hit, "しか|い");
            Assert.That(hit.Line.Units[0].Pauses.Single(), Is.EqualTo(new WordPause(300, 500, 1)));
            Assert.That(hit.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 5 }));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 900d }));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo("s|hika|i"));
        }

        [TestCase("オ|ープン", 1, false)]
        [TestCase("オー|プン", 2, false)]
        [TestCase("オ|ープン", 1, true)]
        [TestCase("オー|プン", 2, true)]
        public void CustomReadingsAcceptExplicitOriginalCutsAndPersistTheirTiming(string marked, int originalCut, bool wordRow)
        {
            var (map, hit) = create("open", "オープン", new[] { 1 }, new[] { 700d });
            Assert.That(OriginalLyricSubdivisions.SourceCut(hit.Line.Units[0], 1, "japanese"), Is.Null);
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("オープン"));
            if (wordRow)
                Assert.That(TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, marked), Is.True);
            else
                Assert.That(TypeBeatEditorOperations.SetLineOriginal(map, hit, marked), Is.True);
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo("o|pen"));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo(marked));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 700d }));
            var saved = OriginalTextFormatTest.Decode(OriginalTextFormatTest.Encode(map)).HitObjects.OfType<TypeBeatHitObject>().Single().Line;
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(saved, "japanese"), Is.EqualTo(marked));
            var played = PolyglotLine.Derive(saved, "japanese", japaneseCharacterTiming: true).Line.Units.Single();
            Assert.That(played.SyllableSplits, Is.EqualTo(new[] { originalCut }));
            Assert.That(played.SyllableBoundaries, Is.EqualTo(new[] { 700d }));
        }

        [Test]
        public void CustomReadingLinksFollowEditsAndRetimeWithoutChangingTheReading()
        {
            var (map, hit) = create("open", "オープン", new[] { 1 }, new[] { 700d });
            TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "オ|ープン");
            TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "オー|プン");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo("o|pen"));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("オー|プン"));
            TypeBeatEditorOperations.SetWordText(map, hit, 0, "op|en");
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("オー|プン"));
            Assert.That(hit.Line.Units[0].OriginalSplits.Single(), Is.EqualTo(new OriginalLyricSplit(2, 2)));
            TypeBeatEditorOperations.SetSyllableBoundary(map, hit, 0, 0, 900);
            Assert.That(OriginalLyricSubdivisions.Project(hit.Line.Units[0], "japanese").SyllableBoundaries, Is.EqualTo(new[] { 900d }));
            TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "オープン");
            Assert.That(hit.Line.Units[0].OriginalSplits, Is.Empty);
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo("op|en"));
            Assert.That(OriginalLyricSubdivisions.SourceCut(hit.Line.Units[0], 2, "japanese"), Is.Null);
        }

        [Test]
        public void OriginalSubdivideButtonAllowsCustomReadings()
        {
            var (map, hit) = create("open", "オープン");
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(map, hit, 0, originalLyrics: true), Is.Not.Null);
            Assert.That(hit.Line.RawText, Is.EqualTo("open"));
            Assert.That(hit.Line.Units[0].OriginalSplits, Has.Count.EqualTo(1));
            Assert.That(OriginalLyricSubdivisions.Project(hit.Line.Units[0], "japanese").SyllableBoundaries,
                Is.EqualTo(hit.Line.Units[0].SyllableBoundaries));
        }

        [Test]
        public void CustomReadingLinksSurviveSplittingAndMergingWords()
        {
            var (map, hit) = create("open", "オープン", new[] { 1, 2 }, new[] { 700d, 1300d });
            TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "オ|ー|プン");
            Assert.That(TypeBeatEditorOperations.SplitWord(map, hit, 0, 0), Is.True);
            Assert.That(hit.Line.Units.Select(u => u.Original), Is.EqualTo(new[] { "オ", "ープン" }));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("オー|プン"));
            Assert.That(TypeBeatEditorOperations.MergeWords(map, hit, 0), Is.True);
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(hit.Line), Is.EqualTo("o|p|en"));
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("オ|ー|プン"));
            Assert.That(hit.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 700d, 1300d }));
        }

        [Test]
        public void CopiedCustomReadingWordsRetainTheirOriginalLinks()
        {
            var (map, hit) = create("open", "オープン", new[] { 1 }, new[] { 700d });
            TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "オー|プン");
            var payload = TypeBeatEditorOperations.CopyUnitTimings(hit, new[] { 0 })!;
            var (_, parsed) = LyricTimingClipboard.TryParse(LyricTimingClipboard.Serialize(payload));
            Assert.That(parsed, Is.Not.Null);
            TypeBeatEditorOperations.PasteWordsAtTime(map, hit, 2000, parsed!);
            Assert.That(hit.Line.Units, Has.Count.EqualTo(2));
            Assert.That(OriginalLyricSubdivisions.PipeText(hit.Line.Units[1], "japanese", original: true), Is.EqualTo("オー|プン"));
            Assert.That(hit.Line.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 2700d }));
        }

        [Test]
        public void RemovingARomanizedSubdivisionClearsItsCustomOriginalLink()
        {
            var (map, hit) = create("open", "オープン", new[] { 1 }, new[] { 700d });
            TypeBeatEditorOperations.SetWordOriginal(map, hit, 0, "オー|プン");
            TypeBeatEditorOperations.RemoveSyllableBoundary(map, hit, 0, 0);
            Assert.That(hit.Line.Units[0].OriginalSplits, Is.Empty);
            Assert.That(TypeBeatEditorOperations.OriginalPipeDisplayText(hit.Line, "japanese"), Is.EqualTo("オープン"));
        }

        private static (EditorBeatmap Map, TypeBeatHitObject Hit) create(string text, string? original, int[]? splits = null, double[]? boundaries = null)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Language = BeatmapLanguage.Japanese;
            beatmap.Metadata.Artist = "Test";
            beatmap.Metadata.Title = "Subdivisions";
            beatmap.Metadata.AudioFile = "audio.mp3";
            var hit = new TypeBeatHitObject
            {
                StartTime = 0, Granularity = boundaries?.Length > 0 ? TimingGranularity.Syllable : TimingGranularity.Word,
                Line = new LyricLine
                {
                    RawText = text, Original = original, StartTime = 0, EndTime = 3000, SingEndTime = 2000,
                    Units = new[] { new TimedUnit
                    {
                        Text = text, Original = original, StartTime = 0, EndTime = 2000, Source = TimingSource.Explicit,
                        SyllableSplits = splits ?? Array.Empty<int>(), SyllableBoundaries = boundaries ?? Array.Empty<double>(),
                    } },
                },
            };
            beatmap.HitObjects.Add(hit);
            return (new EditorBeatmap(beatmap), hit);
        }
    }
}
