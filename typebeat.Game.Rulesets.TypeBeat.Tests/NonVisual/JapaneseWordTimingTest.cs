// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JapaneseWordTimingTest
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void MapperWordsDefineTheFourJapaneseTimingWindows(bool literate, bool aligned)
        {
            var source = example();
            var japanese = TypingLine.ForMods(source, literate, true, "japanese", aligned);
            Assert.Multiple(() =>
            {
                Assert.That(japanese.DisplayText, Is.EqualTo(source.Original));
                Assert.That(japanese.Syllables.Select(s => (s.StartCell, s.EndCellExclusive, s.StartTime, s.EndTime)),
                    Is.EqualTo(new[] { (0, 2, 1000.0, 2000.0), (2, 4, 2000.0, 3000.0), (5, 9, 3500.0, 4500.0), (10, 15, 5000.0, 6500.0) }));
                Assert.That(japanese.Source.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 2000.0 }), "only the mapper word seam remains");
                Assert.That(japanese.Source.Units.Skip(1).All(u => u.SyllableBoundaries.Count == 0), Is.True);
                Assert.That(source.Units.Sum(u => u.SyllableBoundaries.Count), Is.EqualTo(12), "the authored romanised map is preserved");
                Assert.That(TypingLine.ForMods(source, literate, false, "japanese", aligned).Syllables.Count, Is.EqualTo(16));
            });
        }

        [TestCase(2000, false)]
        [TestCase(2500, true)]
        public void JoinedJapaneseWordsKeepTheirIndividualWindowsAndPauses(double secondStart, bool pause)
        {
            var source = line(
                word("konnichiha", "こんにちは", 1000, 2000, new[] { 1200.0, 1400, 1600 }, new[] { 3, 5, 8 }),
                word("sekai", "せかい", secondStart, 3500, new[] { 3000.0 }, new[] { 2 }));
            var japanese = TypingLine.ForMods(source, false, true, "ja");
            Assert.Multiple(() =>
            {
                Assert.That(japanese.DisplayText, Is.EqualTo("こんにちはせかい"));
                Assert.That(japanese.Syllables.Select(s => (s.StartTime, s.EndTime)), Is.EqualTo(new[] { (1000.0, 2000.0), (secondStart, 3500.0) }));
                Assert.That(japanese.Source.Units.Single().Pauses.Count, Is.EqualTo(pause ? 1 : 0));
                Assert.That(japanese.Source.Units.Single().SyllableBoundaries, Is.EqualTo(pause ? Array.Empty<double>() : new[] { 2000.0 }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void KanaIsJudgedWithinTheWholeMapperWord(bool directKana)
        {
            var source = line(word("konnichiha", "こんにちは", 1000, 3000, new[] { 1400.0, 1800, 2400 }, new[] { 3, 5, 8 }));
            var engine = create(source);
            string[] inputs = directKana ? new[] { "こ", "んに", "ち", "は" } : new[] { "ko", "nni", "chi", "ha" };
            double[] times = { 1000, 2000, 2600, 2950 };
            for (int i = 0; i < inputs.Length; i++)
                foreach (char c in inputs[i])
                    Assert.That(engine.ProcessKey(c, times[i]), Is.True, c.ToString());
            Assert.Multiple(() =>
            {
                Assert.That(engine.Lines[0].Cells.All(c => c.State == CellState.Correct), Is.True);
                Assert.That(engine.Lines[0].Cells.Select(c => c.JudgedDelta), Is.All.Zero);
                Assert.That(engine.Mistypes, Is.Zero);
            });
        }

        [Test]
        public void DictionaryConversionsDoNotCrossMapperWordWindows()
        {
            // The dictionary reads the combined 天国 as tengoku. The mapper's two words
            // explicitly read ten and koku, and must convert and time independently.
            var source = line(word("ten", "天", 1000, 2000), word("koku", "国", 2000, 3000));
            var engine = create(source);
            var plan = engine.Lines[0].JapaneseInput!;
            Assert.Multiple(() =>
            {
                Assert.That(plan.At(0)!.EndCellExclusive, Is.EqualTo(1));
                Assert.That(plan.At(0)!.Reading, Is.EqualTo("てん"));
                Assert.That(plan.At(1)!.StartCell, Is.EqualTo(1));
                Assert.That(plan.At(1)!.Reading, Is.EqualTo("こく"));
            });
            foreach (char c in "ten")
                Assert.That(engine.ProcessKey(c, 1000), Is.True);
            foreach (char c in "koku")
                Assert.That(engine.ProcessKey(c, 2000), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(engine.Lines[0].Cells.Select(c => c.State), Is.All.EqualTo(CellState.Correct));
                Assert.That(engine.Lines[0].Cells.Select(c => c.JudgedDelta), Is.All.Zero);
                Assert.That(engine.Mistypes, Is.Zero);
            });
        }

        [Test]
        public void ExampleCanBeTypedAtTheFourWordOnsets()
        {
            var engine = create(example());
            string[] inputs = { "yumemi", "kokochi", " ", "fureai", " ", "madowasetai" };
            double[] times = { 1000, 2000, 3000, 3500, 4500, 5000 };
            for (int i = 0; i < inputs.Length; i++)
                foreach (char c in inputs[i])
                    Assert.That(engine.ProcessKey(c, times[i]), Is.True, $"{inputs[i]}: {c}");
            Assert.Multiple(() =>
            {
                Assert.That(engine.Lines[0].Cells.Select(c => c.State), Is.All.EqualTo(CellState.Correct));
                Assert.That(engine.Lines[0].Cells.Select(c => c.JudgedDelta), Is.All.Zero);
                Assert.That(engine.Mistypes, Is.Zero);
            });
        }

        [Test]
        public void InputsAfterTheWordEndRemainLate()
        {
            var engine = create(line(word("konnichiha", "こんにちは", 1000, 3000, new[] { 1400.0, 1800, 2400 }, new[] { 3, 5, 8 })));
            foreach (char c in "konnichi")
                engine.ProcessKey(c, 1000);
            foreach (char c in "ha")
                engine.ProcessKey(c, 3151);
            Assert.That(engine.Lines[0].Cells[^1].JudgedDelta, Is.EqualTo(151));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ReplayRoundTripAndRewindKeepTheRecordedTimingWindows(bool wordTiming, bool aligned)
        {
            var source = line(word("konnichiha", "こんにちは", 1000, 3000, new[] { 1400.0, 1800, 2400 }, new[] { 3, 5, 8 }));
            var live = create(source);
            live.JapaneseWordTiming = wordTiming;
            live.AlignSubdivisionTargets = aligned;
            var replay = create(source);
            var cells = replay.Lines[0].Cells.ToArray();
            var frames = new List<ReplayFrame>
            {
                roundTrip(TypeBeatReplayFrame.CreateConfigFrame(0, live.AllowWrongInput, syllableTiming: true, firstCharTiming: true, charTimedStretch: true)),
                roundTrip(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, authoredSyllablesOnly: true, alignSubdivisionTargets: aligned,
                    japaneseRomajiInput: true, japaneseWordTiming: wordTiming)),
            };
            string[] inputs = { "ko", "nni", "chi", "ha" };
            double[] times = { 1000, 2000, 2600, 2950 };
            for (int i = 0; i < inputs.Length; i++)
                foreach (char c in inputs[i])
                {
                    live.ProcessKey(c, times[i]);
                    frames.Add(roundTrip(new TypeBeatReplayFrame(times[i], c)));
                }
            Assert.That(((TypeBeatReplayFrame)frames[1]).JapaneseWordTiming, Is.EqualTo(wordTiming));
            ReplayEngineFeed.RebuildTo(replay, frames, 3000);
            assertSamePlay();
            ReplayEngineFeed.RebuildTo(replay, frames, 2100);
            ReplayEngineFeed.RebuildTo(replay, frames, 3000);
            assertSamePlay();
            Assert.That(replay.Lines[0].Cells.Zip(cells).All(pair => ReferenceEquals(pair.First, pair.Second)), Is.True, "rendered cells survive timing changes");
            Assert.That(replay.Lines[0].Syllables.Count, Is.EqualTo(wordTiming ? 1 : 4));
            Assert.That(live.Lines[0].Cells.Any(c => c.JudgedDelta > 0), Is.EqualTo(!wordTiming));

            void assertSamePlay()
            {
                Assert.Multiple(() =>
                {
                    Assert.That(replay.JapaneseWordTiming, Is.EqualTo(wordTiming));
                    Assert.That(replay.Lines[0].Cells.Select(c => (c.State, c.TypedChar, c.JudgedDelta)),
                        Is.EqualTo(live.Lines[0].Cells.Select(c => (c.State, c.TypedChar, c.JudgedDelta))));
                    Assert.That(replay.Score, Is.EqualTo(live.Score));
                    Assert.That(replay.Mistypes, Is.EqualTo(live.Mistypes));
                    Assert.That(replay.Combo, Is.EqualTo(live.Combo));
                });
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TimingCanSwitchBackToLiveWithoutLosingCellState(bool aligned)
        {
            var engine = create(line(word("tsukitenshi", "つき テンシ", 1000, 3000, new[] { 1400.0, 2000, 2600 }, new[] { 3, 5, 8 })));
            engine.AlignSubdivisionTargets = aligned;
            var typedLine = engine.Lines[0];
            foreach (char c in "tsu")
                engine.ProcessKey(c, 1000);
            var targets = typedLine.Cells.Select(c => c.TargetTime).ToArray();
            var spans = typedLine.Syllables.Select(s => (s.StartTime, s.EndTime)).ToArray();
            var words = typedLine.Words.Select(w => (w.StartTime, w.EndTime)).ToArray();
            engine.JapaneseWordTiming = false;
            Assert.That(typedLine.Syllables.Count, Is.EqualTo(4));
            engine.AlignSubdivisionTargets = !aligned;
            engine.JapaneseWordTiming = true;
            engine.AlignSubdivisionTargets = aligned;
            Assert.Multiple(() =>
            {
                Assert.That(typedLine.Cells.Select(c => c.TargetTime), Is.EqualTo(targets));
                Assert.That(typedLine.Syllables.Select(s => (s.StartTime, s.EndTime)), Is.EqualTo(spans));
                Assert.That(typedLine.Words.Select(w => (w.StartTime, w.EndTime)), Is.EqualTo(words));
                Assert.That(typedLine.Cells[0].State, Is.EqualTo(CellState.Correct));
            });
        }

        private static TypeBeatReplayFrame roundTrip(TypeBeatReplayFrame frame)
        {
            var beatmap = new Beatmap();
            var legacy = frame.ToLegacy(beatmap);
            var decoded = new TypeBeatReplayFrame { Time = frame.Time };
            decoded.FromLegacy(legacy, beatmap);
            return decoded;
        }

        private static LyricLine example()
        {
            return new LyricLine
            {
                RawText = "yumemi kokochi fureai madowasetai", Original = "夢見心地 触れ合い 惑わせたい",
                StartTime = 0, EndTime = 8000, SingEndTime = 6500,
                Units = new[]
                {
                    word("yumemi", "夢見", 1000, 2000, new[] { 1300.0, 1700 }, new[] { 2, 4 }),
                    word("kokochi", "心地", 2000, 3000, new[] { 2300.0, 2700 }, new[] { 2, 4 }),
                    word("fureai", "触れ合い", 3500, 4500, new[] { 3800.0, 4100, 4300 }, new[] { 2, 4, 5 }),
                    word("madowasetai", "惑わせたい", 5000, 6500, new[] { 5100.0, 5300, 5600, 5900, 6300 }, new[] { 2, 4, 6, 8, 10 }),
                },
            };
        }

        private static TypingEngine create(LyricLine source)
        {
            var engine = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", AudioFileName = "a.mp3", FolderPath = string.Empty },
                Granularity = TimingGranularity.Syllable, Lines = new[] { source },
            }, polyglot: true, polyglotLanguage: "japanese")
            {
                SyllableTiming = true, AuthoredSyllablesOnly = true, FirstCharTiming = true, CharTimedStretch = true, AlignSubdivisionTargets = true,
            };
            engine.Update(1000);
            return engine;
        }

        private static LyricLine line(params TimedUnit[] words) => new LyricLine
        {
            RawText = string.Join(' ', words.Select(w => w.Text)), StartTime = 0, EndTime = 8000, SingEndTime = words[^1].EndTime, Units = words,
        };

        private static TimedUnit word(string text, string original, double start, double end, double[]? boundaries = null, int[]? splits = null) => new TimedUnit
        {
            Text = text, Original = original, StartTime = start, EndTime = end,
            SyllableBoundaries = boundaries ?? Array.Empty<double>(), SyllableSplits = splits ?? Array.Empty<int>(),
        };
    }
}
