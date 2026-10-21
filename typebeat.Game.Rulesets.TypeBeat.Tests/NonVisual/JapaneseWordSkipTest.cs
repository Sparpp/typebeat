// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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
    public class JapaneseWordSkipTest
    {
        [TestCase(" ", false)]
        [TestCase("　", false)]
        [TestCase(" \t ", false)]
        [TestCase(" ", true)]
        [TestCase("　", true)]
        public void AuthoredSpacesDisappearAndWordTimingSurvives(string space, bool literate)
        {
            var engine = create("つきみ", "tsukimi", literate: literate, captionSpace: space);
            Assert.That(engine.Lines[0].DisplayText, Is.EqualTo("つきみと"));
            Assert.That(engine.Lines[0].Cells.Any(c => c.Expected == ' '), Is.False);
            Assert.That(engine.Lines[0].Cells.Select(c => c.TargetTime), Is.EqualTo(new[] { 1000.0, 1000 + 1000.0 / 3, 1000 + 2000.0 / 3, 2500.0 }).Within(1e-8));
        }

        [TestCase("天使", "tenshi", "")]
        [TestCase("天使", "tenshi", "t")]
        [TestCase("つきみ", "tsukimi", "t")]
        [TestCase("つきみ", "tsukimi", "tsuk")]
        [TestCase("つ き み", "tsukimi", "tsuk")]
        public void SpaceSkipsTheRemainderOfTheAuthoredWord(string original, string romaji, string input)
        {
            var engine = create(original, romaji);
            foreach (char c in input)
                engine.ProcessKey(c, 1100);
            int start = engine.CaretIndex;
            int end = engine.Lines[0].Cells.Count - 1;
            Assert.That(engine.ProcessKey(' ', 1200), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(engine.CaretIndex, Is.EqualTo(end));
                Assert.That(engine.Lines[0].Cells.Take(start).Select(c => c.State), Is.All.EqualTo(CellState.Correct));
                Assert.That(engine.Lines[0].Cells.Skip(start).Take(end - start).Select(c => c.State), Is.All.EqualTo(CellState.Abandoned));
                Assert.That(engine.Lines[0].Cells.Last().State, Is.EqualTo(CellState.Untyped));
                Assert.That(engine.JapanesePending, Is.Empty);
                Assert.That(engine.Mistypes, Is.Zero);
            });
            foreach (char c in "to")
                engine.ProcessKey(c, 2600);
            Assert.That(engine.Lines[0].Cells.Last().State, Is.EqualTo(CellState.Correct));
        }

        [TestCase("天使", "tenshi")]
        [TestCase("つきみ", "tsukimi")]
        public void LineOnlyOriginalsStillSkipTheTimedWord(string original, string romaji)
        {
            var engine = create(original, romaji, wordOriginals: false);
            engine.ProcessKey('t', 1100);
            Assert.That(engine.ProcessKey(' ', 1200), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(original.Length));
            Assert.That(engine.Lines[0].Cells.Take(original.Length).Select(c => c.State), Is.All.EqualTo(CellState.Abandoned));
            Assert.That(engine.Lines[0].Cells.Last().State, Is.EqualTo(CellState.Untyped));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EnterSkipsTheLineDuringAnIncompleteJapaneseReading(bool manualNewlines)
        {
            var engine = create("天使", "tenshi");
            engine.FletcherEnabled = true;
            engine.ManualNewlines = manualNewlines;
            engine.ProcessKey('t', 1100);
            Assert.That(engine.JapanesePending, Is.EqualTo("t"));
            Assert.That(engine.ProcessEnter(1200), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(engine.IsLineComplete, Is.True);
                Assert.That(engine.CaretIndex, Is.EqualTo(engine.Lines[0].Cells.Count));
                Assert.That(engine.JapanesePending, Is.Empty);
                Assert.That(engine.Mistypes, Is.Zero);
                Assert.That(engine.Lines[0].Cells.Select(c => c.State), Is.All.EqualTo(CellState.Untyped),
                    "line skipping leaves untyped characters for the normal deadline misses");
            });
        }

        [Test]
        public void CompletedCharactersProtectTheNextCharacterUntilInputBegins()
        {
            var engine = create("つきみ", "tsukimi");
            foreach (char c in "tsu")
                engine.ProcessKey(c, 1100);
            Assert.That(engine.ProcessKey(' ', 1200), Is.False);
            Assert.That(engine.ProcessKey(' ', 1250), Is.False);
            Assert.That(engine.CaretIndex, Is.EqualTo(1));
            Assert.That(engine.Lines[0].Cells.Skip(1).Select(c => c.State), Is.All.EqualTo(CellState.Untyped));
            engine.ProcessKey('k', 1300);
            Assert.That(engine.ProcessKey(' ', 1400), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(3));
        }

        [Test]
        public void WrongCharactersStayWrongWhenTheRemainderIsSkipped()
        {
            var engine = create("つきみ", "tsukimi");
            engine.ProcessKey('z', 1100);
            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Wrong));
            Assert.That(engine.ProcessKey(' ', 1200), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(3));
            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Wrong));
            Assert.That(engine.Lines[0].Cells.Skip(1).Take(2).Select(c => c.State), Is.All.EqualTo(CellState.Abandoned));
            Assert.That(engine.Lines[0].Cells.Last().State, Is.EqualTo(CellState.Untyped));
            Assert.That(engine.Mistypes, Is.EqualTo(1));
        }

        [Test]
        public void BackspaceReclaimsTheWordWithoutErasingItsCorrectPrefix()
        {
            var engine = create("つきみ", "tsukimi");
            foreach (char c in "tsuk")
                engine.ProcessKey(c, 1100);
            engine.ProcessKey(' ', 1200);
            Assert.That(engine.CanUndoWordSkip, Is.True);
            Assert.That(engine.ProcessBackspace(), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(1));
            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Correct));
            Assert.That(engine.Lines[0].Cells.Skip(1).Select(c => c.State), Is.All.EqualTo(CellState.Untyped));
            foreach (char c in "kimito")
                engine.ProcessKey(c, 2600);
            Assert.That(engine.Lines[0].Cells.Select(c => c.State), Is.All.EqualTo(CellState.Correct));
            Assert.That(engine.Mistypes, Is.Zero);
        }

        [Test]
        public void LiteratePunctuationBelongsToTheSkippedWord()
        {
            var engine = create("つき！", "tsuki!", literate: true);
            foreach (char c in "tsuk")
                engine.ProcessKey(c, 1100);
            Assert.That(engine.ProcessKey(' ', 1200), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(3));
            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Correct));
            Assert.That(engine.Lines[0].Cells.Skip(1).Take(2).Select(c => c.State), Is.All.EqualTo(CellState.Abandoned));
            Assert.That(engine.Lines[0].Cells.Last().State, Is.EqualTo(CellState.Untyped));
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void DisabledSkippingOrGatekeeperKeepsThePartialReading(bool skipping, bool wrongInput)
        {
            var engine = create("天使", "tenshi");
            engine.SpaceSkipsWord = skipping;
            engine.AllowWrongInput = wrongInput;
            engine.ProcessKey('t', 1100);
            Assert.That(engine.ProcessKey(' ', 1200), Is.False);
            Assert.That(engine.CaretIndex, Is.Zero);
            Assert.That(engine.JapanesePending, Is.EqualTo("t"));
            Assert.That(engine.Lines[0].Cells.Select(c => c.State), Is.All.EqualTo(CellState.Untyped));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReplayRoundTripAndRewindRetainTheSkipRule(bool wordSkip)
        {
            var extended = TypeBeatReplayFrame.CreateExtendedConfigFrame(0, inputEra2: true, authoredSyllablesOnly: true,
                japaneseRomajiInput: true, japaneseWordTiming: true, japaneseInputEra2: true, japaneseInputEra3: wordSkip);
            var decoded = new TypeBeatReplayFrame { Time = 0 };
            decoded.FromLegacy(extended.ToLegacy(new Beatmap()), new Beatmap());
            Assert.That(decoded.JapaneseInputEra3, Is.EqualTo(wordSkip));
            var frames = new ReplayFrame[]
            {
                TypeBeatReplayFrame.CreateConfigFrame(0, true, spaceSkipsWord: true), decoded,
                new TypeBeatReplayFrame(1100, 't'), new TypeBeatReplayFrame(1200, ' '),
                new TypeBeatReplayFrame(2600, 't'), new TypeBeatReplayFrame(2700, 'o'),
            };
            var engine = create("天使", "tenshi");
            ReplayEngineFeed.RebuildTo(engine, frames, 1300);
            Assert.That(engine.CaretIndex, Is.EqualTo(wordSkip ? 2 : 1));
            Assert.That(engine.Lines[0].Cells[1].State, Is.EqualTo(wordSkip ? CellState.Abandoned : CellState.Untyped));
            ReplayEngineFeed.RebuildTo(engine, frames, 2800);
            var cells = engine.Lines[0].Cells.Select(c => (c.State, c.TypedChar, c.JudgedDelta)).ToArray();
            double wpm = engine.LiveWpm;
            ReplayEngineFeed.RebuildTo(engine, frames, 1150);
            Assert.That(engine.JapanesePending, Is.EqualTo("t"));
            ReplayEngineFeed.RebuildTo(engine, frames, 2800);
            Assert.That(engine.Lines[0].Cells.Select(c => (c.State, c.TypedChar, c.JudgedDelta)), Is.EqualTo(cells));
            Assert.That(engine.LiveWpm, Is.EqualTo(wpm));
        }

        private static TypingEngine create(string original, string romaji, bool literate = false, string captionSpace = " ", bool wordOriginals = true)
        {
            var engine = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", AudioFileName = "a.mp3", FolderPath = string.Empty },
                Granularity = TimingGranularity.Word,
                Lines = new[]
                {
                    new LyricLine
                    {
                        RawText = romaji + " to", Original = original + captionSpace + "と", StartTime = 0, EndTime = 6000, SingEndTime = 5000,
                        Units = new[]
                        {
                            new TimedUnit { Text = romaji, Original = wordOriginals ? original : null, StartTime = 1000, EndTime = 2000 },
                            new TimedUnit { Text = "to", Original = wordOriginals ? "と" : null, StartTime = 2500, EndTime = 5000 },
                        },
                    },
                },
            }, literate: literate, polyglot: true, polyglotLanguage: "japanese")
            {
                SpaceSkipsWord = true, InputEra2 = true, AuthoredSyllablesOnly = true,
            };
            engine.Update(1000);
            return engine;
        }
    }
}
