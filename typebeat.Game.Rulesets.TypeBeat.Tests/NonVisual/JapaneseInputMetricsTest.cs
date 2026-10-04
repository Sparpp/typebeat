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
    public class JapaneseInputMetricsTest
    {
        [TestCase("")]
        [TestCase("t")]
        public void SpaceSkipsOneUnfinishedKanjiAndLeavesTheRestTypeable(string prefix)
        {
            var engine = create("天使と");
            foreach (char c in prefix)
                engine.ProcessKey(c, 1100);
            Assert.That(engine.ProcessKey(' ', 1200), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(engine.CaretIndex, Is.EqualTo(1));
                Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Abandoned));
                Assert.That(engine.Lines[0].Cells.Skip(1).Select(c => c.State), Is.All.EqualTo(CellState.Untyped));
                Assert.That(engine.JapanesePending, Is.Empty);
                Assert.That(engine.Mistypes, Is.Zero);
            });
            foreach (char c in "shito")
                engine.ProcessKey(c, 1300);
            Assert.That(engine.Lines[0].Cells.Skip(1).Select(c => c.State), Is.All.EqualTo(CellState.Correct));
            Assert.That(engine.Mistypes, Is.Zero);
        }

        [Test]
        public void SkippingKanaAndBackspacingCanReclaimJustTheSkippedCharacter()
        {
            var engine = create("つきと");
            engine.ProcessKey('t', 1100);
            engine.ProcessKey(' ', 1200);
            Assert.That(engine.CaretIndex, Is.EqualTo(1));
            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(CellState.Abandoned));
            Assert.That(engine.ProcessBackspace(), Is.True);
            Assert.That(engine.CaretIndex, Is.Zero);
            foreach (char c in "tsukito")
                engine.ProcessKey(c, 1300);
            Assert.That(engine.Lines[0].Cells.Select(c => c.State), Is.All.EqualTo(CellState.Correct));
        }

        [TestCase("tsuki")]
        [TestCase("月")]
        public void ConfirmationSpaceAfterConversionDoesNotSkipTheNextCharacter(string input)
        {
            var engine = create("月と");
            foreach (char c in input)
                engine.ProcessKey(c, 1100);
            Assert.That(engine.ProcessKey(' ', 1200), Is.False);
            Assert.That(engine.CaretIndex, Is.EqualTo(1));
            Assert.That(engine.Lines[0].Cells[1].State, Is.EqualTo(CellState.Untyped));
            engine.ProcessKey('t', 1300);
            Assert.That(engine.ProcessKey(' ', 1400), Is.True, "starting the next reading makes it eligible to skip");
            Assert.That(engine.Lines[0].Cells[1].State, Is.EqualTo(CellState.Abandoned));
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void SkippingOffOrGatekeeperRetainsAnIncompleteReading(bool skipping, bool wrongInput)
        {
            var engine = create("月と");
            engine.SpaceSkipsWord = skipping;
            engine.AllowWrongInput = wrongInput;
            engine.ProcessKey('t', 1100);
            Assert.That(engine.ProcessKey(' ', 1200), Is.False);
            Assert.That(engine.CaretIndex, Is.Zero);
            Assert.That(engine.JapanesePending, Is.EqualTo("t"));
        }

        [TestCase("月", "tsuki")]
        [TestCase("月", "tuki")]
        [TestCase("月", "月")]
        [TestCase("天使", "tennshi")]
        [TestCase("月abc", "tsukiabc")]
        [TestCase("こんにちは", "konnichiha")]
        public void WpmCountsActualAcceptedInputsRatherThanConvertedCharacters(string original, string input)
        {
            var engine = create(original);
            for (int i = 0; i < input.Length; i++)
            {
                double time = 1000 + 500.0 * (i + 1) / input.Length;
                engine.Update(time);
                Assert.That(engine.ProcessKey(input[i], time), Is.True, input[i].ToString());
            }
            Assert.Multiple(() =>
            {
                Assert.That(engine.LiveWpm, Is.EqualTo(input.Length * 24).Within(1e-6));
                Assert.That(engine.LiveRollingWpm, Is.EqualTo(input.Length * 24).Within(1e-6));
                Assert.That(engine.BuildResults().Wpm, Is.EqualTo(engine.LiveWpm));
                Assert.That(engine.Mistypes, Is.Zero);
            });
        }

        [Test]
        public void PendingInputsCountButConfirmationAndSkipSpacesDoNot()
        {
            var engine = create("天と");
            engine.Update(1500);
            foreach (char c in "ten")
                engine.ProcessKey(c, 1500);
            Assert.That(engine.LiveWpm, Is.EqualTo(72));
            engine.ProcessKey(' ', 1500);
            Assert.That(engine.LiveWpm, Is.EqualTo(72), "confirmation is not an extra text-producing input");
            engine.ProcessKey('t', 1500);
            Assert.That(engine.LiveWpm, Is.EqualTo(96));
            engine.ProcessKey(' ', 1500);
            Assert.That(engine.LiveWpm, Is.EqualTo(96), "skipping does not manufacture WPM");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReplayPreservesSkippingAndWpmAcrossRewind(bool inputEra)
        {
            var extended = TypeBeatReplayFrame.CreateExtendedConfigFrame(0, inputEra2: true, authoredSyllablesOnly: true,
                japaneseRomajiInput: true, japaneseWordTiming: true, japaneseInputEra2: inputEra);
            var decoded = new TypeBeatReplayFrame { Time = 0 };
            decoded.FromLegacy(extended.ToLegacy(new Beatmap()), new Beatmap());
            Assert.That(decoded.JapaneseInputEra2, Is.EqualTo(inputEra));
            var frames = new ReplayFrame[]
            {
                TypeBeatReplayFrame.CreateConfigFrame(0, true, spaceSkipsWord: true, syllableTiming: true), decoded,
                new TypeBeatReplayFrame(1100, 't'), new TypeBeatReplayFrame(1200, ' '),
                new TypeBeatReplayFrame(1300, 't'), new TypeBeatReplayFrame(1400, 'o'),
            };
            var engine = create("月と");
            ReplayEngineFeed.RebuildTo(engine, frames, 1500);
            var cells = engine.Lines[0].Cells.Select(c => (c.State, c.TypedChar, c.JudgedDelta)).ToArray();
            double wpm = engine.LiveWpm;
            double rolling = engine.LiveRollingWpm;
            Assert.That(engine.Lines[0].Cells[0].State, Is.EqualTo(inputEra ? CellState.Abandoned : CellState.Wrong));
            Assert.That(wpm > 0, Is.EqualTo(inputEra));
            ReplayEngineFeed.RebuildTo(engine, frames, 1150);
            Assert.That(engine.JapanesePending, Is.EqualTo("t"));
            ReplayEngineFeed.RebuildTo(engine, frames, 1500);
            Assert.Multiple(() =>
            {
                Assert.That(engine.JapaneseInputEra2, Is.EqualTo(inputEra));
                Assert.That(engine.Lines[0].Cells.Select(c => (c.State, c.TypedChar, c.JudgedDelta)), Is.EqualTo(cells));
                Assert.That(engine.LiveWpm, Is.EqualTo(wpm).Within(1e-6));
                Assert.That(engine.LiveRollingWpm, Is.EqualTo(rolling).Within(1e-6));
            });
        }

        private static TypingEngine create(string original)
        {
            var engine = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "A", Title = "T", AudioFileName = "a.mp3", FolderPath = string.Empty },
                Granularity = TimingGranularity.Word,
                Lines = new[]
                {
                    new LyricLine
                    {
                        RawText = "reading", StartTime = 0, EndTime = 6000, SingEndTime = 5000,
                        Units = new[] { new TimedUnit { Text = LyricOriginals.RomaniseWord(original, "japanese").Text, Original = original, StartTime = 1000, EndTime = 5000 } },
                    },
                },
            }, polyglot: true, polyglotLanguage: "japanese") { SpaceSkipsWord = true, InputEra2 = true, SyllableTiming = true, AuthoredSyllablesOnly = true };
            engine.Update(1000);
            return engine;
        }
    }
}
