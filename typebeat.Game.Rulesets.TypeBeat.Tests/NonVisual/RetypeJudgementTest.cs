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
    public class RetypeJudgementTest
    {
        [TestCase(-400, 0, JudgementType.Great, 0)]
        [TestCase(-400, -200, JudgementType.Ok, -200)]
        [TestCase(-225, 0, JudgementType.Great, 0)]
        [TestCase(0, 400, JudgementType.Great, 0)]
        [TestCase(-225, 400, JudgementType.Ok, -225)]
        [TestCase(-400, 400, JudgementType.Meh, -400)]
        public void RetypeKeepsHigherTierWithoutAnotherScoringCell(double first, double second, JudgementType expected, double retainedDelta)
        {
            var engine = create();
            var events = new List<CharJudgement>();
            engine.CharJudged += events.Add;
            engine.ProcessKey('a', 1000 + first);
            engine.ProcessBackspace();
            engine.ProcessKey('a', 1000 + second);
            var cell = engine.Lines[0].Cells[0];
            Assert.That(cell.JudgedTier, Is.EqualTo(expected));
            Assert.That(cell.JudgedDelta, Is.EqualTo(retainedDelta));
            Assert.That(engine.Combo, Is.EqualTo(1));
            Assert.That(engine.MaxCombo, Is.EqualTo(1));
            Assert.That(engine.Score, Is.EqualTo(SyncWindows.BasePoints(expected)));
            var summary = engine.BuildResults();
            Assert.That(summary.Counts[expected], Is.EqualTo(1));
            Assert.That(summary.Counts.Values.Sum(), Is.EqualTo(1));
            Assert.That(summary.SyncTimeline, Has.Count.EqualTo(1));
            Assert.That(summary.SyncTimeline.Single().Delta, Is.EqualTo(retainedDelta));
            Assert.That(events.Last().Upgraded, Is.EqualTo(SyncWindows.BasePoints(expected) > SyncWindows.BasePoints(SyncWindows.Default.Classify(first))));
        }

        [Test]
        public void RepeatedImprovementsDoNotFarmComboOrUseTheLaterComboMultiplier()
        {
            var engine = create();
            engine.ProcessKey('a', 600);
            engine.ProcessKey('b', engine.Lines[0].Cells[1].TargetTime);
            long before = engine.Score;
            engine.ProcessBackspace();
            engine.ProcessBackspace();
            engine.ProcessKey('a', 800);
            engine.ProcessBackspace();
            engine.ProcessKey('a', 1000);
            for (int i = 0; i < 20; i++)
            {
                engine.ProcessBackspace();
                engine.ProcessKey('a', 1400);
            }
            Assert.That(engine.Score, Is.EqualTo(before + 250));
            Assert.That(engine.Combo, Is.EqualTo(2));
            Assert.That(engine.MaxCombo, Is.EqualTo(2));
            Assert.That(engine.BuildResults().Counts[JudgementType.Great], Is.EqualTo(2));
            Assert.That(engine.BuildResults().SyncTimeline, Has.Count.EqualTo(2));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReplayUsesRecordedRetypeRule(bool bestRetype)
        {
            var map = playable(line());
            var replay = new Replay();
            replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(-1000, true));
            var header = TypeBeatReplayFrame.CreateExtendedConfigFrame(-1000, bestRetypeJudgement: bestRetype);
            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(header.ToLegacy(map), map);
            replay.Frames.Add(decoded);
            replay.Frames.Add(new TypeBeatReplayFrame(600, 'a'));
            replay.Frames.Add(new TypeBeatReplayFrame(700, TypeBeatReplayFrame.BACKSPACE));
            replay.Frames.Add(new TypeBeatReplayFrame(1000, 'a'));
            replay.Frames.Add(new TypeBeatReplayFrame(2000, 'b'));
            var account = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            Assert.That(account.Statistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(bestRetype ? 2 : 1));
            Assert.That(account.Statistics.GetValueOrDefault(HitResult.Meh), Is.EqualTo(bestRetype ? 0 : 1));
            Assert.That(account.Accuracy, Is.EqualTo(bestRetype ? 1 : 350.0 / 600));
            Assert.That(account.MaxCombo, Is.EqualTo(2));
        }

        [Test]
        public void JapaneseInputsImproveIndependentlyWhenCharacterCompletes()
        {
            var engine = create(line("tsuki", "月"), japanese: true);
            engine.ProcessKey('t', 600); // Meh.
            engine.ProcessKey('u', 1000);
            engine.ProcessKey('k', 1100);
            engine.ProcessKey('i', 1200);
            engine.ProcessBackspace();
            engine.ProcessKey('t', 1000); // Great first input; later inputs will be late.
            Assert.That(engine.BuildResults().Counts[JudgementType.Meh], Is.EqualTo(1), "no amendment before completion");
            engine.ProcessKey('u', 3400);
            engine.ProcessKey('k', 3500);
            engine.ProcessKey('i', 3600);
            Assert.That(engine.Lines[0].Cells.Single().InputJudgements!.Select(i => i.Type), Is.All.EqualTo(JudgementType.Great));
            Assert.That(engine.BuildResults().Counts[JudgementType.Great], Is.EqualTo(4));
            Assert.That(engine.BuildResults().Counts.Values.Sum(), Is.EqualTo(4));
            Assert.That(engine.Combo, Is.EqualTo(4));
            Assert.That(engine.BuildResults().SyncTimeline, Has.Count.EqualTo(4));
        }

        [Test]
        public void DifferentJapaneseSpellingKeepsTheMoreAccurateAttemptAndFixedCombo()
        {
            var engine = create(line("tsuki", "月"), japanese: true);
            engine.ProcessKey('つ', 600);
            engine.ProcessKey('き', 700);
            engine.ProcessBackspace();
            foreach (char key in "tuki")
                engine.ProcessKey(key, 1000);
            Assert.That(engine.BuildResults().Counts[JudgementType.Great], Is.EqualTo(4));
            Assert.That(engine.BuildResults().Counts.Values.Sum(), Is.EqualTo(4));
            Assert.That(engine.Combo, Is.EqualTo(4));
            Assert.That(engine.BuildResults().SyncTimeline, Has.Count.EqualTo(4));
        }

        [Test]
        public void DifferentJapaneseSpellingComparesActualAccuracyWeights()
        {
            var engine = create(line("chou", "蝶"), japanese: true);
            engine.ProcessKey('t', 1000);
            engine.ProcessKey('y', 1000);
            engine.ProcessKey('o', 3400);
            engine.ProcessKey('u', 3400);
            engine.ProcessBackspace();
            engine.ProcessKey('ち', 800);
            engine.ProcessKey('ょ', 800);
            engine.ProcessKey('う', 1000);
            // Two Greats and two Mehs have better accuracy than one Great and two Okays.
            Assert.That(engine.BuildResults().Counts[JudgementType.Great], Is.EqualTo(2));
            Assert.That(engine.BuildResults().Counts[JudgementType.Meh], Is.EqualTo(2));
            Assert.That(engine.BuildResults().Counts.Values.Sum(), Is.EqualTo(4));
            Assert.That(engine.Combo, Is.EqualTo(4));
        }

        [Test]
        public void ScoreAmendmentAndRewindKeepCompletionComboAndStatisticsConsistent()
        {
            var map = playable(line());
            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            processor.ApplyBeatmap(map);
            var character = map.HitObjects.OfType<TypeBeatHitObject>().Single().NestedHitObjects.OfType<TypeBeatCharObject>().First();
            var result = new TypeBeatJapaneseJudgementResult(character, character.Judgement) { Type = HitResult.Meh, JudgedTimeOffset = -400 };
            processor.ApplyResult(result);
            double originalScore = processor.TotalScore.Value;
            int newJudgements = 0;
            processor.NewJudgement += _ => newJudgements++;
            processor.UpdateCellResult(result, HitResult.Great, 0);
            Assert.That(processor.Accuracy.Value, Is.EqualTo(1));
            Assert.That(processor.TotalScore.Value, Is.GreaterThan(originalScore));
            Assert.That(processor.JudgedHits, Is.EqualTo(1));
            Assert.That(processor.Combo.Value, Is.EqualTo(1));
            Assert.That(processor.HighestCombo.Value, Is.EqualTo(1));
            Assert.That(processor.ComboNotesJudged, Is.EqualTo(1));
            Assert.That(processor.HitEvents, Has.Count.EqualTo(1));
            Assert.That(processor.HitEvents.Single().Result, Is.EqualTo(HitResult.Great));
            Assert.That(newJudgements, Is.Zero, "no duplicate health or judgement event");

            var engine = create();
            engine.ProcessKey('a', 600);
            processor.ResyncAfterRewind(0, engine);
            Assert.That(processor.Accuracy.Value, Is.EqualTo(1.0 / 6));
            Assert.That(processor.Statistics.GetValueOrDefault(HitResult.Meh), Is.EqualTo(1));
            engine.ProcessBackspace();
            engine.ProcessKey('a', 1000);
            processor.ResyncAfterRewind(0, engine);
            Assert.That(processor.Accuracy.Value, Is.EqualTo(1));
            processor.RevertResult(result);
            Assert.That(processor.JudgedHits, Is.Zero);
            Assert.That(processor.Statistics.Values.Sum(), Is.Zero);
        }

        [TestCase(1, 4)]
        [TestCase(6, 1)]
        public void JapaneseScoreAmendmentChangesGradeCountWithoutChangingCombo(int initialInputs, int newInputs)
        {
            var map = OriginalTextFormatTest.BuildBeatmap(new[] { line("tsuki", "月") });
            map.BeatmapInfo.Metadata.Language = BeatmapLanguage.Japanese;
            new TypeBeatModPolyglot().ApplyToBeatmap(map);
            foreach (var hit in map.HitObjects)
                hit.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());
            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            processor.ApplyBeatmap(map);
            var character = map.HitObjects.Single().NestedHitObjects.OfType<TypeBeatCharObject>().Single(c => c.Phase == 0);
            var result = new TypeBeatJapaneseJudgementResult(character, character.Judgement)
            {
                Type = HitResult.Meh, InputResults = Enumerable.Repeat(HitResult.Meh, initialInputs).ToArray(),
            };
            processor.ApplyResult(result);
            processor.UpdateCellResult(result, HitResult.Great, 0, Enumerable.Repeat(HitResult.Great, newInputs).ToArray());
            Assert.That(processor.Statistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(newInputs));
            Assert.That(processor.MaximumStatistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(newInputs));
            Assert.That(processor.Combo.Value, Is.EqualTo(4));
            Assert.That(processor.ComboNotesJudged, Is.EqualTo(4));
            Assert.That(processor.MaximumCombo, Is.EqualTo(4));
            Assert.That(processor.Accuracy.Value, Is.EqualTo(1));
            Assert.That(processor.TotalScore.Value, Is.EqualTo(1_000_000));
            processor.RevertResult(result);
            Assert.That(processor.Statistics.Values.Sum(), Is.Zero);
            Assert.That(processor.MaximumStatistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(4));
            Assert.That(processor.ComboNotesJudged, Is.Zero);
        }

        private static LyricLine line(string text = "ab", string? original = null) => new LyricLine
        {
            RawText = text, Original = original, StartTime = 0, EndTime = 6000, SingEndTime = 3000,
            Units = new[] { new TimedUnit { Text = text, Original = original, StartTime = 1000, EndTime = 3000 } },
        };

        private static TypingEngine create(LyricLine? source = null, bool japanese = false)
        {
            var engine = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", FolderPath = "", AudioFileName = "a.mp3" },
                Granularity = TimingGranularity.Word, Lines = new[] { source ?? line() },
            }, polyglot: japanese, polyglotLanguage: japanese ? "japanese" : null) { EarlyFinish = false };
            engine.Update(0);
            return engine;
        }

        private static Beatmap playable(LyricLine source)
        {
            var map = OriginalTextFormatTest.BuildBeatmap(new[] { source });
            foreach (var hit in map.HitObjects)
                hit.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());
            return map;
        }
    }
}
