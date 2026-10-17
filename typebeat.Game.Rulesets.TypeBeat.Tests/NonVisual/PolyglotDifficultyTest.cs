// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Tests.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class PolyglotDifficultyTest
    {
        private static readonly Mod[] polyglot = { new TypeBeatModPolyglot() };

        private static LyricLine line(string romanised, string original, double[]? boundaries = null, int[]? splits = null)
            => new LyricLine
            {
                RawText = romanised, StartTime = 1000, EndTime = 7000, SingEndTime = 6000,
                Units = new[]
                {
                    new TimedUnit
                    {
                        Text = romanised, Original = original, StartTime = 1000, EndTime = 6000,
                        SyllableBoundaries = boundaries ?? Array.Empty<double>(), SyllableSplits = splits ?? Array.Empty<int>(),
                    },
                },
            };

        [TestCase("spanish", "corazon", "corazón", 7, 1)]
        [TestCase("italian", "citta", "città", 5, 0)]
        [TestCase("polish", "zazolc", "zażółć", 6, 0)]
        [TestCase("swedish", "smorgas", "smörgås", 7, 0)]
        [TestCase("russian", "shchastye", "счастье", 7, 0)]
        [TestCase("russian", "yoy", "ёй", 2, 0)]
        [TestCase("french", "coeur", "cœur", 4, 1)]
        [TestCase("german", "strasse", "straße", 6, 0)]
        [TestCase("french", "Noel", "Noël", 4, 0)]
        public void OriginalLettersAndCompositionCostsAreCountedSeparately(string language, string romanised, string original, int cells, int extra)
        {
            var source = line(romanised, original);
            var projected = PolyglotDifficulty.LinesFor(new[] { source }, polyglot, language);
            var words = LyricDifficulty.BuildWords(projected, 1, false);
            Assert.Multiple(() =>
            {
                Assert.That(words.Sum(w => w.Cells), Is.EqualTo(cells));
                Assert.That(words.Sum(w => w.ExtraInputs), Is.EqualTo(extra));
                Assert.That(source.RawText, Is.EqualTo(romanised));
                Assert.That(source.Units[0].Original, Is.EqualTo(original));
            });
            var prefix = ChunkedEndurance.BuildShiftPrefix(words, 0, 150, false, ChunkedEndurance.Live);
            Assert.That(prefix?.Last() ?? 0, Is.EqualTo(extra * ChunkedEndurance.Live.shift_cost).Within(1e-9));
        }

        [Test]
        public void CompositionRaisesStarsWithoutIncreasingScoringCellCount()
        {
            var accented = PolyglotDifficulty.LinesFor(new[] { line("eeeeeeeeeeeeeeee", "êêêêêêêêêêêêêêêê") }, polyglot, "french");
            var plain = new[] { line("eeeeeeeeeeeeeeee", "eeeeeeeeeeeeeeee") };
            var extra = LyricDifficulty.ComputeDetail(accented);
            var regular = LyricDifficulty.ComputeDetail(plain);
            Assert.That(extra.Stars, Is.GreaterThan(regular.Stars));
            Assert.That(LyricDifficulty.BuildWords(accented, 1, false).Sum(w => w.Cells), Is.EqualTo(16));
        }

        [TestCase("shi", "し", 2)]
        [TestCase("chi", "ち", 2)]
        [TestCase("tsu", "つ", 2)]
        [TestCase("wo", "を", 1)]
        [TestCase("shashin", "しゃしん", 6)]
        [TestCase("gakkou", "がっこう", 6)]
        [TestCase("koohii", "コーヒー", 6)]
        [TestCase("konnichiha", "こんにちは", 9)]
        [TestCase("toukyou", "東京", 7)]
        public void JapaneseUsesTheShortestAcceptedInput(string romanised, string original, int inputs)
        {
            var projected = PolyglotDifficulty.LinesFor(new[] { line(romanised, original) }, polyglot, "japanese");
            Assert.That(projected[0].RawText.Length, Is.EqualTo(inputs), projected[0].RawText);
            Assert.That(LyricDifficulty.BuildWords(projected, 1, false).Sum(w => w.Cells), Is.EqualTo(inputs));
            Assert.That(projected[0].Units[0].SyllableBoundaries.Count, Is.EqualTo(inputs - 1));
            Assert.That(LyricDifficulty.Compute(projected), Is.GreaterThan(0));
        }

        [Test]
        public void JapaneseKeepsAlignedSubdivisionTargets()
        {
            var source = line("kaki", "かき", new[] { 4000.0 }, new[] { 2 });
            var projected = PolyglotDifficulty.LinesFor(new[] { source }, polyglot, "japanese").Single();
            Assert.That(projected.RawText, Is.EqualTo("kaki"));
            Assert.That(projected.Units[0].SyllableBoundaries, Does.Contain(4000.0));
        }

        [Test]
        public void JapaneseDoesNotFillAuthoredRestsWithInputs()
        {
            var source = new LyricLine
            {
                RawText = "ka ki", StartTime = 1000, EndTime = 7000, SingEndTime = 6000,
                Units = new[]
                {
                    new TimedUnit { Text = "ka", Original = "か", StartTime = 1000, EndTime = 2000 },
                    new TimedUnit { Text = "ki", Original = "き", StartTime = 4000, EndTime = 6000 },
                },
            };
            var projected = PolyglotDifficulty.LinesFor(new[] { source }, polyglot, "japanese").Single();
            Assert.That(projected.RawText, Is.EqualTo("kaki"));
            Assert.That(projected.Units.Single().Pauses, Does.Contain(new WordPause(2000, 4000, 2)));
            var words = LyricDifficulty.BuildWords(new[] { projected }, 1, false);
            Assert.That(words.Count, Is.EqualTo(2));
            Assert.That(words[0].Start, Is.EqualTo(1000));
            Assert.That(words[0].End, Is.EqualTo(2000));
            Assert.That(words[1].Start, Is.EqualTo(4000));
            Assert.That(words.Sum(w => w.Cells), Is.EqualTo(4));
            Assert.That(LyricDifficulty.BuildWords(new[] { source }, 1, false).Sum(w => w.Cells), Is.EqualTo(5),
                "nomod includes the space between authored words");
            var paceLines = PolyglotDifficulty.LinesFor(new[] { source }, polyglot, "japanese", forPace: true);
            Assert.That(LyricPaceStatistics.Compute(paceLines).TypeableCellCount, Is.EqualTo(4),
                "Polyglot pace excludes the same removed space as SR");
        }

        [TestCase(false, 7)]
        [TestCase(true, 9)]
        public void OriginalPunctuationUsesTheSameCellRulesAsGameplay(bool literate, int count)
        {
            var mods = literate ? new Mod[] { new TypeBeatModPolyglot(), new TypeBeatModLiterate() } : polyglot;
            var source = line("corazon!", "¿corazón!");
            var projected = PolyglotDifficulty.LinesFor(new[] { source }, mods, "spanish");
            Assert.That(LyricDifficulty.BuildWords(projected, 1, literate).Sum(w => w.Cells), Is.EqualTo(count));
            Assert.That(TypingLine.ForMods(source, literate, true, "spanish").Cells.Count(c => c.IsTypeable), Is.EqualTo(count));
            Assert.That(LyricDifficulty.BuildWords(projected, 1, literate).Sum(w => w.ExtraInputs), Is.EqualTo(literate ? 2 : 1));
        }

        [Test]
        public void LineOnlyOriginalsAndPartialOriginalsArePriced()
        {
            var source = line("strasse", "straße");
            var lineOnly = new LyricLine
            {
                RawText = source.RawText, Original = "straße", StartTime = source.StartTime,
                EndTime = source.EndTime, SingEndTime = source.SingEndTime,
                Units = new[] { new TimedUnit { Text = "strasse", StartTime = 1000, EndTime = 6000 } },
            };
            var projected = PolyglotDifficulty.LinesFor(new[] { lineOnly }, polyglot, "german");
            Assert.That(LyricDifficulty.BuildWords(projected, 1, false).Sum(w => w.Cells), Is.EqualTo(6));
        }

        [Test]
        public void UnrankedRateModsUseTheirActualConfiguredRate()
        {
            var wind = new ModWindUp();
            wind.InitialRate.Value = 0.8;
            Assert.That(PerformancePointsDisplay.RateFor(new Mod[] { wind }), Is.EqualTo(0.8));
        }

        [TestCase(BeatmapLanguage.German, "Strasse!", "Straße!")]
        [TestCase(BeatmapLanguage.Japanese, "shashin", "しゃしん")]
        [TestCase(BeatmapLanguage.Russian, "shchastye", "счастье")]
        public void RatingAndPpEntryPointsAgreeWithPolyglotAndLiterateAndRate(BeatmapLanguage language, string romanised, string original)
        {
            var map = new TypeBeatBeatmap { BeatmapInfo = new BeatmapInfo { Status = BeatmapOnlineStatus.Ranked } };
            map.BeatmapInfo.Metadata.Language = language;
            map.HitObjects.Add(new TypeBeatHitObject { Line = line(romanised, original), StartTime = 1000 });
            var mods = new Mod[] { new TypeBeatModPolyglot(), new TypeBeatModLiterate(), new TypeBeatModDoubleTime() };
            var projected = PolyglotDifficulty.LinesFor(map.HitObjects.Select(h => h.Line), mods, language.ToCanonicalName());
            var expected = LyricDifficulty.ComputeDetail(projected, 1.5, true);
            var actual = PerformancePointsDisplay.DifficultyFor(map, mods)!.Value;
            var ruleset = new TypeBeatRuleset();
            var attributes = new TypeBeatDifficultyCalculator(ruleset.RulesetInfo, new TestWorkingBeatmap(map)).Calculate(mods);
            Assert.Multiple(() =>
            {
                Assert.That(actual.Stars, Is.EqualTo(expected.Stars).Within(1e-9));
                Assert.That(actual.DifficultCharacters, Is.EqualTo(expected.DifficultCharacters).Within(1e-9));
                Assert.That(attributes.StarRating, Is.EqualTo(expected.Stars).Within(1e-9));
                Assert.That(PerformancePoints.StarsFor(map.HitObjects.Select(h => h.Line), mods, language.ToCanonicalName()), Is.EqualTo(expected.Stars).Within(1e-9));
            });
        }

        [TestCase("italian", 'à', 0)]
        [TestCase("italian", 'â', 1)]
        [TestCase("spanish", 'ñ', 0)]
        [TestCase("spanish", 'á', 1)]
        [TestCase("german", 'ä', 0)]
        [TestCase("french", 'é', 0)]
        [TestCase("french", 'ê', 1)]
        [TestCase("polish", 'ą', 0)]
        [TestCase("polish", 'ó', 0)]
        [TestCase("hungarian", 'ő', 0)]
        [TestCase("romanian", 'ș', 0)]
        [TestCase("portuguese", 'ç', 0)]
        [TestCase("portuguese", 'ã', 1)]
        [TestCase("czech", 'ě', 0)]
        [TestCase("czech", 'ó', 1)]
        [TestCase("slovak", 'ľ', 0)]
        [TestCase("croatian", 'ć', 0)]
        [TestCase("serbian", 'ђ', 0)]
        [TestCase("russian", 'ё', 0)]
        [TestCase("russian", 'й', 0)]
        [TestCase("bulgarian", 'ѝ', 1)]
        [TestCase("slovak", 'ó', 1)]
        [TestCase("swedish", 'ö', 0)]
        [TestCase("finnish", 'ä', 0)]
        [TestCase("danish", 'ø', 0)]
        [TestCase("norwegian", 'å', 0)]
        [TestCase("dutch", 'é', 1)]
        [TestCase("afrikaans", 'ê', 1)]
        public void NativeLayoutLettersAreExemptButComposedAccentsPayForTheirExtraInput(string language, char letter, int extra)
            => Assert.That(PolyglotDifficulty.CompositionInputs(letter, language), Is.EqualTo(extra));

        [TestCase(BeatmapLanguage.Afrikaans, "wereld", "wêreld")]
        [TestCase(BeatmapLanguage.Bavarian, "Madl", "Mädl")]
        [TestCase(BeatmapLanguage.Dutch, "ideeen", "ideeën")]
        [TestCase(BeatmapLanguage.Danish, "kaere", "kære")]
        [TestCase(BeatmapLanguage.Norwegian, "sjo", "sjø")]
        [TestCase(BeatmapLanguage.LowGerman, "moin", "möin")]
        [TestCase(BeatmapLanguage.SwissGerman, "gruezi", "Grüezi")]
        [TestCase(BeatmapLanguage.Belarusian, "belarus", "Беларусь")]
        [TestCase(BeatmapLanguage.Bulgarian, "lyubov", "любов")]
        [TestCase(BeatmapLanguage.Croatian, "covjek", "čovjek")]
        [TestCase(BeatmapLanguage.Czech, "laska", "láska")]
        [TestCase(BeatmapLanguage.Serbian, "ljubav", "љубав")]
        [TestCase(BeatmapLanguage.Slovak, "laska", "láska")]
        [TestCase(BeatmapLanguage.Ukrainian, "shchastya", "щастя")]
        [TestCase(BeatmapLanguage.Finnish, "sydan", "sydän")]
        [TestCase(BeatmapLanguage.Hungarian, "sziv", "szív")]
        [TestCase(BeatmapLanguage.Catalan, "canco", "cançó")]
        [TestCase(BeatmapLanguage.Neapolitan, "citta", "città")]
        [TestCase(BeatmapLanguage.Portuguese, "coracao", "coração")]
        [TestCase(BeatmapLanguage.Romanian, "tara", "țară")]
        public void EveryNewLanguageRatesItsOriginalCharacterStream(BeatmapLanguage language, string romanised, string original)
        {
            var source = line(romanised, original);
            var projected = PolyglotDifficulty.LinesFor(new[] { source }, polyglot, language.ToCanonicalName());
            Assert.That(projected.Single(), Is.Not.SameAs(source));
            Assert.That(LyricDifficulty.BuildWords(projected, 1, false).Sum(w => w.Cells), Is.EqualTo(original.Length));
            Assert.That(PerformancePoints.StarsFor(new[] { source }, polyglot, language.ToCanonicalName()), Is.GreaterThan(0));
        }

        [TestCase("russian", "ПРИВЕТ", "Privet")]
        [TestCase("ukrainian", "ЇЇ", "Yiyi")]
        [TestCase("german", "ÄÖÜ", "AOU")]
        public void NativeCapitalsPayTheSameShiftSurchargeAsRomanCapitals(string language, string original, string romanised)
        {
            var mods = new Mod[] { new TypeBeatModPolyglot(), new TypeBeatModLiterate() };
            var words = LyricDifficulty.BuildWords(PolyglotDifficulty.LinesFor(new[] { line(romanised, original) }, mods, language), 1, true);
            double expected = ChunkedEndurance.ShiftSurcharge(new string('A', original.Length), true, ChunkedEndurance.Live.shift_cost);
            Assert.That(ChunkedEndurance.BuildShiftPrefix(words, 0, 150, true, ChunkedEndurance.Live)!.Last(), Is.EqualTo(expected).Within(1e-9));
        }

        [TestCase(BeatmapLanguage.Chinese, "nihao", "你好")]
        [TestCase(BeatmapLanguage.Korean, "annyeong", "안녕")]
        [TestCase(BeatmapLanguage.Other, "sawatdi", "สวัสดี")]
        [TestCase(BeatmapLanguage.Other, "han", "𠀀")]
        public void UnsupportedScriptsKeepBaseStarsAndHaveNoPpAcrossEntryPoints(BeatmapLanguage language, string romanised, string original)
        {
            var source = line(romanised, original);
            var map = new TypeBeatBeatmap { BeatmapInfo = new BeatmapInfo { Status = BeatmapOnlineStatus.Ranked } };
            map.Metadata.Language = language;
            map.HitObjects.Add(new TypeBeatHitObject { Line = source, StartTime = 1000 });
            var ruleset = new TypeBeatRuleset();
            var attributes = new TypeBeatDifficultyCalculator(ruleset.RulesetInfo, new TestWorkingBeatmap(map)).Calculate(polyglot);
            var score = new typebeat.Game.Scoring.ScoreInfo
            {
                BeatmapInfo = map.BeatmapInfo, Ruleset = ruleset.RulesetInfo, Mods = polyglot, Passed = true,
                Rank = typebeat.Game.Scoring.ScoreRank.X, Accuracy = 1, MaxCombo = 5, PP = 99,
                Statistics = new System.Collections.Generic.Dictionary<typebeat.Game.Rulesets.Scoring.HitResult, int>
                {
                    [typebeat.Game.Rulesets.Scoring.HitResult.Great] = 5,
                },
            };
            if (language is BeatmapLanguage.Chinese or BeatmapLanguage.Korean)
                Assert.That(typebeat.Game.Screens.Ranking.Expanded.Statistics.PerformanceStatistic.ScoreEarnsPerformancePoints(score), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(attributes.StarRating, Is.EqualTo(LyricDifficulty.Compute(new[] { source })).Within(1e-9));
                Assert.That(attributes.CanCalculatePerformance, Is.False);
                Assert.That(PerformancePointsDisplay.DifficultyFor(map, polyglot, requireRankedMap: false), Is.Null);
                Assert.That(PerformancePointsDisplay.ForScore(score, map), Is.Null, "an archived pp value cannot bypass the script gate");
                Assert.That(new TypeBeatPerformanceCalculator(ruleset).Calculate(score, attributes).Total, Is.Zero);
                Assert.That(PerformancePoints.StarsFor(new[] { source }, polyglot, language.ToCanonicalName()), Is.Null);
                Assert.That(PerformancePointsDisplay.DifficultyFor(map, Array.Empty<Mod>(), requireRankedMap: false), Is.Not.Null);
            });
        }

        [Test]
        public void MissingOriginalsAndNoModPreserveTheAuthoredRating()
        {
            var source = line("hello", "héllo");
            Assert.That(PolyglotDifficulty.LinesFor(new[] { source }, Array.Empty<Mod>()).Single(), Is.SameAs(source));
            var noOriginal = new LyricLine { RawText = "hello", StartTime = 0, EndTime = 1000, SingEndTime = 1000,
                Units = new[] { new TimedUnit { Text = "hello", StartTime = 0, EndTime = 1000 } } };
            Assert.That(PolyglotDifficulty.LinesFor(new[] { noOriginal }, polyglot, "japanese").Single(), Is.SameAs(noOriginal));
        }

        [TestCase("greek", "agapi", "αγάπη")]
        public void OtherScriptsRetainTheirExistingRating(string language, string romanised, string original)
        {
            var source = line(romanised, original);
            Assert.That(PolyglotDifficulty.LinesFor(new[] { source }, polyglot, language).Single(), Is.SameAs(source));
        }

        [Test]
        public void JapaneseWordsWithoutRomanisationStillUseTheirReading()
        {
            var source = new LyricLine
            {
                RawText = "", StartTime = 1000, EndTime = 7000, SingEndTime = 6000,
                Units = Array.Empty<TimedUnit>(),
                UnromanisedWords = new[] { new UnromanisedWord(0, "こんにちは", 1000, 6000) },
            };
            var projected = PolyglotDifficulty.LinesFor(new[] { source }, polyglot).Single();
            Assert.That(projected.RawText.Length, Is.EqualTo(9));
        }

        [Test]
        public void EveryUnrankedModHasALocalPpEstimate()
        {
            var ruleset = new TypeBeatRuleset();
            var map = new TypeBeatBeatmap { BeatmapInfo = new BeatmapInfo { Status = BeatmapOnlineStatus.Ranked } };
            map.HitObjects.Add(new TypeBeatHitObject { Line = line("hellohellohello", "héllohéllohéllo"), StartTime = 1000 });
            foreach (var mod in Enum.GetValues<ModType>().SelectMany(ruleset.GetModsFor).Where(m => !m.Ranked))
            {
                var mods = new[] { mod };
                Assert.That(PerformancePointsDisplay.DifficultyFor(map, mods)?.Stars, Is.GreaterThan(0), mod.Acronym);
                var score = new typebeat.Game.Scoring.ScoreInfo
                {
                    BeatmapInfo = map.BeatmapInfo, Mods = mods, Passed = true,
                    Rank = typebeat.Game.Scoring.ScoreRank.X, Accuracy = 1, MaxCombo = 15,
                    Statistics = new System.Collections.Generic.Dictionary<typebeat.Game.Rulesets.Scoring.HitResult, int>
                    {
                        [typebeat.Game.Rulesets.Scoring.HitResult.Great] = 15,
                    },
                };
                Assert.That(PerformancePointsDisplay.ForScore(score, map), Is.GreaterThan(0), mod.Acronym);
                Assert.That(ruleset.ScoreEarnsPerformancePoints(score), Is.False, mod.Acronym);
                Assert.That(ruleset.ScoreCanDisplayPerformancePoints(score), Is.True, mod.Acronym);
            }
        }
    }
}
