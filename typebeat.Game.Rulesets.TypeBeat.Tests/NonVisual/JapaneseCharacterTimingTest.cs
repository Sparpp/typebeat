// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JapaneseCharacterTimingTest
    {
        [Test]
        public void SplittingAndMergingLinkedKanjiRoundTrips()
        {
            var line = source("nihon", "日本", new[] { 1300d }, new[] { 2 });
            var map = editor(line);
            var hit = first(map);
            Assert.That(TypeBeatEditorOperations.SplitWord(map, hit, 0, 0), Is.True);
            Assert.That(hit.Line.Units.Select(u => u.Original), Is.EqualTo(new[] { "日", "本" }));
            Assert.That(TypeBeatEditorOperations.MergeWords(map, hit, 0), Is.True);
            Assert.That(hit.Line.Units.Single().Original, Is.EqualTo("日本"));
            Assert.That(OriginalTextFormatTest.Decode(OriginalTextFormatTest.Encode(map)).HitObjects.OfType<TypeBeatHitObject>().Single().Line.Units.Single().Original, Is.EqualTo("日本"));
        }

        [Test]
        public void SplitInsideAReadingKeepsTheUnsplitCaption()
        {
            var map = editor(source("tsuki", "月", new[] { 1300d }, new[] { 2 }));
            Assert.That(TypeBeatEditorOperations.SplitWord(map, first(map), 0, 0), Is.True);
            Assert.That(first(map).Line.Units.Select(u => u.Original), Is.All.Null);
            Assert.That(first(map).Line.Original, Is.EqualTo("月"));
        }

        [Test]
        public void CompoundHasCharacterInputsAndAlignedSubdivision()
        {
            var engine = create(source("nihon", "日本", new[] { 1300d }, new[] { 2 }));
            Assert.That(engine.Lines[0].Cells.Select(c => c.TargetTime), Is.EqualTo(new[] { 1000d, 1300d }));
            engine.ProcessKey('n', 1000); engine.ProcessKey('i', 1100);
            Assert.That(engine.CaretIndex, Is.EqualTo(1), "日 completes before 本 is typed");
            Assert.That(engine.Lines[0].JapaneseInput!.AtForInput(1)!.Reading, Is.EqualTo("ほん"));
        }

        [Test]
        public void StartAndFinishReceiveIndependentGrades()
        {
            var engine = create(source("tsuki", "月"));
            var judgements = new List<CharJudgement>();
            engine.CharJudged += judgements.Add;
            engine.ProcessKey('t', 1000); engine.ProcessKey('s', 1200); engine.ProcessKey('u', 1400); engine.ProcessKey('k', 1800); engine.ProcessKey('i', 2200);
            Assert.That(judgements.Select(j => (j.Phase, j.Type)), Is.EqualTo(new[] { (0, JudgementType.Great), (1, JudgementType.Ok) }));
            Assert.That(engine.Lines[0].Cells.Single().EndTargetTime, Is.EqualTo(2000));
            Assert.That(engine.BuildResults().Counts[JudgementType.Great], Is.EqualTo(1));
            Assert.That(engine.BuildResults().Counts[JudgementType.Ok], Is.EqualTo(1));
        }

        [TestCase(-151, JudgementType.Ok)]
        [TestCase(-150, JudgementType.Great)]
        [TestCase(0, JudgementType.Great)]
        [TestCase(150, JudgementType.Great)]
        [TestCase(151, JudgementType.Ok)]
        public void StartKeepsItsSymmetricTimingWindow(double startDelta, JudgementType expected)
        {
            var engine = create(source("tsuki", "月"), initialTime: 0);
            var judgements = new List<CharJudgement>();
            engine.CharJudged += judgements.Add;
            engine.ProcessKey('t', 1000 + startDelta);
            engine.ProcessKey('u', 1400);
            engine.ProcessKey('k', 1700);
            engine.ProcessKey('i', 2000);

            Assert.That(judgements.Select(j => (j.Phase, j.Type)), Is.EqualTo(new[] { (0, expected), (1, JudgementType.Great) }));
            Assert.That(judgements[0].Delta, Is.EqualTo(startDelta));
        }

        [TestCase(-900, 1, JudgementType.Great)]
        [TestCase(-151, 1, JudgementType.Great)]
        [TestCase(-150, 1, JudgementType.Great)]
        [TestCase(0, 1, JudgementType.Great)]
        [TestCase(150, 1, JudgementType.Great)]
        [TestCase(151, 1, JudgementType.Ok)]
        [TestCase(300, 1, JudgementType.Ok)]
        [TestCase(301, 1, JudgementType.Meh)]
        [TestCase(600, 1, JudgementType.Meh)]
        [TestCase(601, 1, JudgementType.Lagging)]
        [TestCase(-900, 0.5, JudgementType.Great)]
        [TestCase(75, 0.5, JudgementType.Great)]
        [TestCase(76, 0.5, JudgementType.Ok)]
        [TestCase(300, 2, JudgementType.Great)]
        [TestCase(301, 2, JudgementType.Ok)]
        public void FinishOnlyPenalisesLateness(double endDelta, double windowScale, JudgementType expected)
        {
            var engine = create(source("tsuki", "月"));
            engine.WindowScale = windowScale;
            var judgements = new List<CharJudgement>();
            engine.CharJudged += judgements.Add;
            double finishTime = 2000 + endDelta;
            engine.ProcessKey('t', 1000);
            engine.ProcessKey('u', 1000 + (finishTime - 1000) / 3);
            engine.ProcessKey('k', 1000 + 2 * (finishTime - 1000) / 3);
            engine.ProcessKey('i', finishTime);

            Assert.That(judgements.Select(j => (j.Phase, j.Type)), Is.EqualTo(new[] { (0, JudgementType.Great), (1, expected) }));
            Assert.That(judgements[1].Delta, Is.EqualTo(Math.Max(0, endDelta)));
            Assert.That(engine.Lines[0].Cells.Single().EndJudgedTier, Is.EqualTo(expected));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnfinishedOrUnfixedWrongCharacterHasTwoMisses(bool wrong)
        {
            var engine = create(source("tsuki", "月"));
            engine.ProcessKey('t', 1000);
            if (wrong) engine.ProcessKey('z', 1200);
            engine.Update(6000);
            Assert.That(engine.BuildResults().Counts[JudgementType.Miss], Is.EqualTo(2));
        }

        [Test]
        public void LegacyShortKanaHasOneGradeAndNoFinishWindow()
        {
            var engine = create(source("ka", "か"));
            engine.ProcessKey('k', 1000); engine.ProcessKey('a', 1100);
            Assert.That(engine.BuildResults().Counts.Values.Sum(), Is.EqualTo(1));
            Assert.That(engine.Lines[0].Cells.Single().EndTargetTime, Is.Null);
        }

        [Test]
        public void SelectedLyricsReplaceExistingOriginalsWithoutChangingTiming()
        {
            var line = source("nihon", "二本");
            var map = editor(line);
            var targets = new[] { first(map) };
            var originals = OriginalLyricGenerator.MatchLines(new[] { line }, "日本", BeatmapLanguage.Japanese);
            Assert.That(TypeBeatEditorOperations.AddGeneratedOriginals(map, targets, new[] { line }, originals, replaceExisting: true), Is.EqualTo(1));
            Assert.That(first(map).Line.Original, Is.EqualTo("日本"));
            Assert.That(first(map).Line.Units.Single().Original, Is.EqualTo("日本"));
            Assert.That(first(map).Line.Units.Single().StartTime, Is.EqualTo(1000));
            Assert.That(first(map).Line.RawText, Is.EqualTo("nihon"));
        }

        [Test]
        public void QuantizeSnapsBothEdgesAndPreservesOriginals()
        {
            var map = editor(source("tsuki", "月"));
            Assert.That(TypeBeatEditorOperations.QuantizeWords(map, first(map), new[] { 0 }, t => t == 1000 ? 1125 : 1875), Is.True);
            Assert.That(first(map).Line.Units.Single().StartTime, Is.EqualTo(1125));
            Assert.That(first(map).Line.Units.Single().EndTime, Is.EqualTo(1875));
            Assert.That(first(map).Line.Units.Single().Original, Is.EqualTo("月"));
        }

        [TestCase(false, 1100)]
        [TestCase(false, 2150)]
        [TestCase(false, 2200)]
        [TestCase(true, 2200)]
        public void ReplayScoringCountsBothEndpointsAndBothMisses(bool wrong, double finishTime)
        {
            var line = source("tsuki", "月");
            var map = OriginalTextFormatTest.BuildBeatmap(new[] { line });
            map.BeatmapInfo.Metadata.Language = BeatmapLanguage.Japanese;
            var mod = new TypeBeatModPolyglot();
            mod.ApplyToBeatmap(map);
            foreach (var hit in map.HitObjects)
                hit.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());
            Assert.That(map.HitObjects.Single().NestedHitObjects.OfType<TypeBeatCharObject>().Count(), Is.EqualTo(2));
            var engine = create(line);
            var score = new Score { Replay = new Replay() };
            score.Replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(1000, true, syllableTiming: true));
            score.Replay.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(1000, inputEra2: true, authoredSyllablesOnly: true,
                japaneseRomajiInput: true, japaneseInputEra2: true, japaneseCharacterTiming: true));
            var keys = wrong ? new[] { ('t', 1000d), ('z', 1200d) }
                : new[] { ('t', 1000d), ('u', 1000 + (finishTime - 1000) / 3), ('k', 1000 + 2 * (finishTime - 1000) / 3), ('i', finishTime) };
            foreach (var (key, time) in keys)
            {
                engine.Update(time);
                engine.ProcessKey(key, time);
                score.Replay.Frames.Add(new TypeBeatReplayFrame(time, key));
            }
            var roundTrip = new Replay();
            foreach (var frame in score.Replay.Frames.Cast<TypeBeatReplayFrame>())
            {
                var decoded = new TypeBeatReplayFrame { Time = frame.Time };
                decoded.FromLegacy(frame.ToLegacy(map), null!);
                roundTrip.Frames.Add(decoded);
            }
            var account = TypeBeatReplayScorer.Score(map, new Mod[] { mod }, roundTrip, TypoRule.Deferred, ComboRestoreRule.OnFix);
            Assert.That(account.MaximumStatistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(2));
            if (wrong)
            {
                Assert.That(account.Statistics.GetValueOrDefault(HitResult.Miss), Is.EqualTo(2));
                Assert.That(account.Accuracy, Is.Zero);
            }
            else
            {
                bool greatFinish = finishTime <= 2150;
                Assert.That(account.Statistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(greatFinish ? 2 : 1), string.Join(", ", account.Statistics.Select(p => $"{p.Key}:{p.Value}")));
                Assert.That(account.Statistics.GetValueOrDefault(HitResult.Ok), Is.EqualTo(greatFinish ? 0 : 1));
                Assert.That(account.Accuracy, Is.EqualTo(greatFinish ? 1 : 2d / 3).Within(0.0001));
            }
        }

        [Test]
        public void BackspacingACompletedLongReadingDoesNotAwardTwoMoreGrades()
        {
            var engine = create(source("tsuki", "月"));
            foreach (char c in "tuki") engine.ProcessKey(c, 1100);
            var counts = engine.BuildResults().Counts.ToDictionary(p => p.Key, p => p.Value);
            engine.ProcessBackspace();
            foreach (char c in "tuki") engine.ProcessKey(c, 1500);
            Assert.That(engine.BuildResults().Counts, Is.EqualTo(counts));
        }

        [TestCase("日本")]
        [TestCase("[00:01.000]日本")]
        [TestCase("[00:01.000]<00:01.000>日<00:01.300>本")]
        [TestCase("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"1.000\" end=\"2.000\">日本</p></div></body></tt>")]
        public void ProvidedLyricsFormatsCanMatchACompoundAlternateReading(string content)
            => Assert.That(OriginalLyricGenerator.MatchLines(new[] { source("nihon", "二本") }, content, BeatmapLanguage.Japanese).Single(), Is.EqualTo("日本"));

        [Test]
        public void LegacyWordTimingReplayKeepsOneScoringObject()
        {
            var map = OriginalTextFormatTest.BuildBeatmap(new[] { source("tsuki", "月") });
            map.BeatmapInfo.Metadata.Language = BeatmapLanguage.Japanese;
            var mod = new TypeBeatModPolyglot();
            mod.ApplyToBeatmap(map);
            foreach (var hit in map.HitObjects)
                hit.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());
            var legacy = new Replay();
            legacy.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(1000, true, syllableTiming: true));
            legacy.Frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(1000, japaneseWordTiming: true));
            legacy.Frames.Add(new TypeBeatReplayFrame(1500, '月'));
            var account = TypeBeatReplayScorer.Score(map, new Mod[] { mod }, legacy, TypoRule.Deferred, ComboRestoreRule.OnFix);
            Assert.That(account.MaximumStatistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(1));
            Assert.That(account.Statistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(1));
            Assert.That(account.Accuracy, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PunctuationDoesNotShortenTheCharacterFinishWindow(bool literate)
        {
            var line = TypingLine.ForMods(source("(tsuki)", "（月）"), literate, true, "japanese", japaneseCharacterTiming: true);
            Assert.That(line.Cells.Single(c => c.Expected == '月').EndTargetTime, Is.EqualTo(2000));
        }

        [Test]
        public void ACharacterBeforeAPauseFinishesAtTheStartOfThePause()
        {
            var line = new LyricLine
            {
                RawText = "tsukihoshi", Original = "月星", StartTime = 0, EndTime = 4000, SingEndTime = 2000,
                Units = new[] { new TimedUnit
                {
                    Text = "tsukihoshi", Original = "月星", StartTime = 1000, EndTime = 2000,
                    Pauses = new[] { new WordPause(1500, 1700, 5) },
                } },
            };
            var typing = TypingLine.ForMods(line, false, true, "japanese", japaneseCharacterTiming: true);
            Assert.That(typing.Cells[0].EndTargetTime, Is.EqualTo(1500));
            Assert.That(typing.Cells[1].TargetTime, Is.EqualTo(1700));
        }

        [Test]
        public void ReplayRewindRestoresBothCharacterJudgements()
        {
            var engine = create(source("tsuki", "月"));
            var frames = new typebeat.Game.Rulesets.Replays.ReplayFrame[]
            {
                TypeBeatReplayFrame.CreateConfigFrame(1000, true, syllableTiming: true),
                TypeBeatReplayFrame.CreateExtendedConfigFrame(1000, japaneseRomajiInput: true, japaneseInputEra2: true, japaneseCharacterTiming: true),
                new TypeBeatReplayFrame(1000, 't'), new TypeBeatReplayFrame(1300, 'u'),
                new TypeBeatReplayFrame(1700, 'k'), new TypeBeatReplayFrame(2200, 'i'),
            };
            ReplayEngineFeed.RebuildTo(engine, frames, 3000);
            var counts = engine.BuildResults().Counts.ToDictionary(p => p.Key, p => p.Value);
            ReplayEngineFeed.RebuildTo(engine, frames, 1300);
            Assert.That(engine.JapanesePending, Is.EqualTo("tu"));
            ReplayEngineFeed.RebuildTo(engine, frames, 3000);
            Assert.That(engine.BuildResults().Counts, Is.EqualTo(counts));
            Assert.That(engine.Lines[0].Cells.Single().EndJudgedTier, Is.EqualTo(JudgementType.Ok));
        }

        private static LyricLine source(string text, string original, double[]? boundaries = null, int[]? cuts = null) => new LyricLine
        {
            RawText = text, Original = original, StartTime = 0, EndTime = 4000, SingEndTime = 2000,
            Units = new[] { new TimedUnit { Text = text, Original = original, StartTime = 1000, EndTime = 2000, SyllableBoundaries = boundaries ?? Array.Empty<double>(), SyllableSplits = cuts ?? Array.Empty<int>() } },
        };
        private static EditorBeatmap editor(LyricLine line)
        {
            var map = new EditorBeatmap(OriginalTextFormatTest.BuildBeatmap(new[] { line }));
            map.Metadata.Language = BeatmapLanguage.Japanese;
            return map;
        }
        private static TypeBeatHitObject first(EditorBeatmap map) => map.HitObjects.OfType<TypeBeatHitObject>().Single();
        private static TypingEngine create(LyricLine line, double initialTime = 1000)
        {
            var engine = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", AudioFileName = "a.mp3", FolderPath = "" },
                Granularity = TimingGranularity.Syllable, Lines = new[] { line },
            }, polyglot: true, polyglotLanguage: "japanese") { SyllableTiming = true, AuthoredSyllablesOnly = true, FirstCharTiming = true, JapaneseInputScoring = false };
            engine.Update(initialTime);
            return engine;
        }
    }
}
