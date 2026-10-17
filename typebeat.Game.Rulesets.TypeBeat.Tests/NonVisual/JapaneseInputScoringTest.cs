// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JapaneseInputScoringTest
    {
        [Test]
        public void TenAwardsOneGradePerInputOnlyAfterCompletion()
        {
            var engine = create(source("ten", "天"));
            var judgements = new List<CharJudgement>();
            engine.CharJudged += judgements.Add;
            engine.ProcessKey('t', 820);
            engine.ProcessKey('e', 1100);
            engine.ProcessKey('n', 1500);
            Assert.That(engine.Combo, Is.Zero);
            Assert.That(judgements, Is.Empty);
            Assert.That(engine.BuildResults().Counts.Values.Sum(), Is.Zero);

            engine.ProcessKey(' ', 1600); // Confirms the ambiguous trailing n, without another romaji input.
            Assert.That(engine.Combo, Is.EqualTo(3));
            Assert.That(judgements, Has.Count.EqualTo(1));
            Assert.That(judgements.Single().Inputs!.Select(i => i.Type), Is.EqualTo(new[] { JudgementType.Ok, JudgementType.Great, JudgementType.Great }));
        }

        [TestCase("tyou", 4)]
        [TestCase("tixyou", 6)]
        public void AlternativeReadingsChangeGradesButNotCombo(string input, int count)
        {
            var engine = create(source("chou", "蝶"));
            type(engine, input, 820, 1800);
            Assert.That(engine.Combo, Is.EqualTo(4));
            Assert.That(engine.BuildResults().Counts[JudgementType.Ok], Is.EqualTo(1));
            Assert.That(engine.BuildResults().Counts[JudgementType.Great], Is.EqualTo(count - 1));
        }

        [TestCase("shi", "し", 2)]
        [TestCase("tsuki", "月", 4)]
        public void DirectKanaOrCommittedCharacterHasOneGradeAndRomajiCombo(string romanized, string original, int combo)
        {
            var engine = create(source(romanized, original));
            engine.ProcessKey(original[0], 1000);
            Assert.That(engine.Combo, Is.EqualTo(combo));
            Assert.That(engine.BuildResults().Counts[JudgementType.Great], Is.EqualTo(1));
            Assert.That(engine.BuildResults().Counts.Values.Sum(), Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void KanaDigraphUsesTheSameMinimumComboAsItsRomajiProjection(bool directKana)
        {
            var engine = create(source("sho", "しょ"));
            type(engine, directKana ? "しょ" : "syo", 1000, 1800);
            Assert.That(engine.Combo, Is.EqualTo(3));
            Assert.That(engine.BuildResults().Counts.Values.Sum(), Is.EqualTo(directKana ? 2 : 3));
        }

        [TestCase("つき")]
        [TestCase("ツキ")]
        [TestCase("つki")]
        [TestCase("tuき")]
        public void FuriganaInputsKeepPerInputGradesAndMinimumRomajiCombo(string input)
        {
            var engine = create(source("tsuki", "月"));
            engine.ProcessKey(input[0], 820);
            Assert.That(engine.Combo, Is.Zero);
            Assert.That(engine.BuildResults().Counts.Values.Sum(), Is.Zero);
            for (int i = 1; i < input.Length; i++)
                engine.ProcessKey(input[i], 1000 + 500 * i / input.Length);
            Assert.That(engine.Lines[0].Cells.Single().State, Is.EqualTo(CellState.Correct));
            Assert.That(engine.Combo, Is.EqualTo(4));
            Assert.That(engine.BuildResults().Counts[JudgementType.Ok], Is.EqualTo(1));
            Assert.That(engine.BuildResults().Counts[JudgementType.Great], Is.EqualTo(input.Length - 1));
            Assert.That(engine.Mistypes, Is.Zero);
        }

        [Test]
        public void FuriganaProgressAndBackspaceDoNotGradeBeforeCompletion()
        {
            var engine = create(source("tsuki", "月"));
            engine.ProcessKey('つ', 1000);
            Assert.That(engine.JapanesePendingKanaCount, Is.EqualTo(1));
            engine.ProcessBackspace();
            Assert.That(engine.JapanesePendingKanaCount, Is.Zero);
            engine.ProcessKey('つ', 1100);
            engine.ProcessKey('き', 1500);
            Assert.That(engine.Combo, Is.EqualTo(4));
            Assert.That(engine.BuildResults().Counts[JudgementType.Great], Is.EqualTo(2));
        }

        [TestCase("にほん", "nihon", "日本")]
        [TestCase("てんし", "tenshi", "天使")]
        [TestCase("ひとり", "hitori", "一人")]
        [TestCase("がっこう", "gakkou", "学校")]
        public void KanaReadingsAcrossKanjiReplayWithTheirActualInputCount(string input, string romaji, string original)
        {
            var (map, mod) = playable(source(romaji, original));
            var replay = frames(input, 1000, 1800);
            var account = TypeBeatReplayScorer.Score(map, new Mod[] { mod }, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            Assert.That(account.Statistics.GetValueOrDefault(HitResult.Miss), Is.Zero);
            Assert.That(account.Statistics.Where(p => p.Key.AffectsAccuracy()).Sum(p => p.Value), Is.EqualTo(input.Length));
            Assert.That(account.MaxCombo, Is.GreaterThan(input.Length));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EarlierJapaneseReplayGrammarDoesNotAcceptKanaForKanji(bool perInputScoring)
        {
            var engine = create(source("tsuki", "月"));
            engine.JapaneseInputScoring = perInputScoring;
            engine.JapaneseKanaInput = false;
            engine.ProcessKey('つ', 1000);
            Assert.That(engine.Lines[0].Cells.Single().State, Is.Not.EqualTo(CellState.Correct));
            Assert.That(engine.JapanesePending, Is.Empty);
        }

        [Test]
        public void CorrectingTheNextCharacterRestoresTheWholeRomajiCombo()
        {
            var engine = create(source("tsukito", "月と"));
            type(engine, "tuki", 1000, 1200);
            Assert.That(engine.Combo, Is.EqualTo(4));
            engine.ProcessKey('z', 1500);
            Assert.That(engine.Combo, Is.Zero);
            engine.ProcessBackspace();
            engine.ProcessKey('t', 1500);
            engine.ProcessKey('o', 1600);
            Assert.That(engine.Combo, Is.EqualTo(6));
            Assert.That(engine.MaxCombo, Is.EqualTo(6));
        }

        [TestCase(849, JudgementType.Ok)]
        [TestCase(850, JudgementType.Great)]
        [TestCase(2150, JudgementType.Great)]
        [TestCase(2151, JudgementType.Ok)]
        public void LaterInputsUseTheCharactersSpan(double time, JudgementType expected)
        {
            var engine = create(source("tsuki", "月"));
            engine.ProcessKey('t', Math.Min(1000, time));
            engine.ProcessKey('u', time);
            engine.ProcessKey('k', Math.Max(time, 1700));
            engine.ProcessKey('i', Math.Max(time, 1800));
            Assert.That(engine.Lines[0].Cells.Single().InputJudgements![1].Type, Is.EqualTo(expected));
        }

        [Test]
        public void BackspaceRemovesThePendingKeysTimingBeforeCompletion()
        {
            var engine = create(source("tsuki", "月"));
            engine.ProcessKey('t', 1000);
            engine.ProcessKey('s', 1100);
            engine.ProcessBackspace();
            engine.ProcessKey('u', 1200);
            engine.ProcessKey('k', 1800);
            engine.ProcessKey('i', 2200);
            Assert.That(engine.Lines[0].Cells.Single().InputJudgements, Has.Count.EqualTo(4));
            Assert.That(engine.BuildResults().Counts[JudgementType.Great], Is.EqualTo(3));
            Assert.That(engine.BuildResults().Counts[JudgementType.Ok], Is.EqualTo(1));
            Assert.That(engine.Combo, Is.EqualTo(4));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnfinishedAndWrongCharactersMissTheirMinimumInputs(bool wrong)
        {
            var engine = create(source("tsuki", "月"));
            engine.ProcessKey('t', 1000);
            if (wrong)
                engine.ProcessKey('z', 1200);
            engine.Update(6000);
            Assert.That(engine.BuildResults().Counts[JudgementType.Miss], Is.EqualTo(4));
            Assert.That(engine.Combo, Is.Zero);
        }

        [TestCase("tyou", 4, false)]
        [TestCase("tixyou", 6, false)]
        [TestCase("蝶", 1, false)]
        [TestCase("tixyou", 6, true)]
        public void ReplayScoreUsesActualGradesAndFixedMaximumCombo(string input, int count, bool early)
        {
            var (map, mod) = playable(source("chou", "蝶"));
            var replay = frames(input, early ? 820 : 1000, 1800);
            var account = TypeBeatReplayScorer.Score(map, new Mod[] { mod }, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            int oks = early ? 1 : 0;
            Assert.That(account.MaxCombo, Is.EqualTo(4));
            Assert.That(account.MaximumStatistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(count));
            Assert.That(account.Statistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(count - oks));
            Assert.That(account.Statistics.GetValueOrDefault(HitResult.Ok), Is.EqualTo(oks));
            Assert.That(account.Accuracy, Is.EqualTo((300d * (count - oks) + 100 * oks) / (300 * count)).Within(0.000001));
            if (!early)
                Assert.That(account.TotalScore, Is.EqualTo(1_000_000));

            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            processor.ApplyBeatmap(map);
            Assert.That(processor.MaximumCombo, Is.EqualTo(4), "map maximum remains the minimum romaji count");
        }

        [Test]
        public void TenReplayHasTwoGreatsAndOneOkay()
        {
            var (map, mod) = playable(source("ten", "天"));
            var account = TypeBeatReplayScorer.Score(map, new Mod[] { mod }, frames("ten ", 820, 1800), TypoRule.Deferred, ComboRestoreRule.OnFix);
            Assert.That(account.MaxCombo, Is.EqualTo(3));
            Assert.That(account.Statistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(2));
            Assert.That(account.Statistics.GetValueOrDefault(HitResult.Ok), Is.EqualTo(1));
            Assert.That(account.Accuracy, Is.EqualTo(7d / 9).Within(0.000001));
        }

        [Test]
        public void RevertingACompletedCharacterRestoresItsGradesComboAndScoreMaximum()
        {
            var (map, _) = playable(source("chou", "蝶"));
            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            processor.ApplyBeatmap(map);
            var cell = map.HitObjects.Single().NestedHitObjects.OfType<TypeBeatCharObject>().Single(c => c.Phase == 0);
            var result = new TypeBeatJapaneseJudgementResult(cell, cell.Judgement)
            {
                Type = HitResult.Ok,
                InputResults = new[] { HitResult.Ok, HitResult.Great, HitResult.Great, HitResult.Great, HitResult.Great, HitResult.Great },
            };
            processor.ApplyResult(result);
            Assert.That(processor.Combo.Value, Is.EqualTo(4));
            Assert.That(processor.ComboNotesJudged, Is.EqualTo(4));
            Assert.That(processor.MaximumStatistics[HitResult.Great], Is.EqualTo(6));
            Assert.That(processor.Accuracy.Value, Is.EqualTo(8d / 9).Within(0.000001));

            processor.RevertResult(result);
            Assert.That(processor.Combo.Value, Is.Zero);
            Assert.That(processor.HighestCombo.Value, Is.Zero);
            Assert.That(processor.ComboNotesJudged, Is.Zero);
            Assert.That(processor.Statistics.Values.Sum(), Is.Zero);
            Assert.That(processor.MaximumStatistics[HitResult.Great], Is.EqualTo(4));
            Assert.That(processor.TotalScore.Value, Is.Zero);

            processor.ApplyResult(new TypeBeatJapaneseJudgementResult(cell, cell.Judgement) { Type = HitResult.Great, InputResults = new[] { HitResult.Great } });
            Assert.That(processor.Combo.Value, Is.EqualTo(4));
            Assert.That(processor.MaximumStatistics[HitResult.Great], Is.EqualTo(1));
            Assert.That(processor.Accuracy.Value, Is.EqualTo(1));
            Assert.That(processor.TotalScore.Value, Is.EqualTo(1_000_000));
        }

        [Test]
        public void PerfectSpellingsAndDirectKanaKeepTheSamePpComboBonus()
        {
            double expected = PerformancePoints.Compute(7, 4, 4, 0, 1, 4, Array.Empty<Mod>(), comboNotes: 4);
            foreach (int inputs in new[] { 1, 4, 6 })
                Assert.That(PerformancePoints.Compute(7, inputs, 4, 0, 1, 4, Array.Empty<Mod>(), comboNotes: 4), Is.EqualTo(expected));
        }

        [Test]
        public void RewindAndRetypeCannotDuplicateInputGradesOrCombo()
        {
            var engine = create(source("chou", "蝶"));
            var replay = frames("tixyou", 1000, 1800);
            ReplayEngineFeed.RebuildTo(engine, replay.Frames, 1900);
            var counts = engine.BuildResults().Counts.ToDictionary(p => p.Key, p => p.Value);
            engine.ProcessBackspace();
            type(engine, "tyou", 1900, 1950);
            Assert.That(engine.BuildResults().Counts, Is.EqualTo(counts));
            Assert.That(engine.Combo, Is.EqualTo(4));

            ReplayEngineFeed.RebuildTo(engine, replay.Frames, 1200);
            Assert.That(engine.Combo, Is.Zero);
            Assert.That(engine.BuildResults().Counts.Values.Sum(), Is.Zero);
            ReplayEngineFeed.RebuildTo(engine, replay.Frames, 1900);
            Assert.That(engine.BuildResults().Counts, Is.EqualTo(counts));
            Assert.That(engine.Combo, Is.EqualTo(4));
            Assert.That(engine.BuildResults().SyncPercent, Is.EqualTo(100));
        }

        private static void type(TypingEngine engine, string input, double start, double end)
        {
            for (int i = 0; i < input.Length; i++)
                engine.ProcessKey(input[i], start + (end - start) * i / Math.Max(1, input.Length - 1));
        }

        private static Replay frames(string input, double start, double end)
        {
            var replay = new Replay();
            replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true, syllableTiming: true, firstCharTiming: true));
            replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, inputEra2: true, authoredSyllablesOnly: true,
                japaneseRomajiInput: true, japaneseInputEra2: true, japaneseInputEra3: true, japaneseCharacterTiming: true, japaneseInputScoring: true, japaneseKanaInput: true));
            for (int i = 0; i < input.Length; i++)
                replay.Frames.Add(new TypeBeatReplayFrame(start + (end - start) * i / Math.Max(1, input.Length - 1), input[i]));
            return replay;
        }

        private static (Beatmap, TypeBeatModPolyglot) playable(LyricLine line)
        {
            var map = OriginalTextFormatTest.BuildBeatmap(new[] { line });
            map.BeatmapInfo.Metadata.Language = BeatmapLanguage.Japanese;
            var mod = new TypeBeatModPolyglot();
            mod.ApplyToBeatmap(map);
            foreach (var hit in map.HitObjects)
                hit.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());
            return (map, mod);
        }

        private static TypingEngine create(LyricLine line)
        {
            var engine = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", AudioFileName = "a.mp3", FolderPath = "" },
                Granularity = TimingGranularity.Syllable, Lines = new[] { line },
            }, polyglot: true, polyglotLanguage: "japanese") { SyllableTiming = true, AuthoredSyllablesOnly = true, FirstCharTiming = true };
            engine.Update(0);
            return engine;
        }

        private static LyricLine source(string text, string original) => new LyricLine
        {
            RawText = text, Original = original, StartTime = 0, EndTime = 4000, SingEndTime = 2000,
            Units = new[] { new TimedUnit { Text = text, Original = original, StartTime = 1000, EndTime = 2000 } },
        };
    }
}
