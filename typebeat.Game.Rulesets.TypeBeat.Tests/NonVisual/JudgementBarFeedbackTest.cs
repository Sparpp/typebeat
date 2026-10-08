// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JudgementBarFeedbackTest
    {
        [TestCase(0, JudgementType.Great)]
        [TestCase(225, JudgementType.Ok)]
        [TestCase(375, JudgementType.Meh)]
        [TestCase(550, JudgementType.Meh)]
        [TestCase(650, JudgementType.Lagging)]
        [TestCase(-650, JudgementType.Premature)]
        public void AwardedTierIsAvailableBeforeJudgementEvent(double delta, JudgementType expected)
        {
            var engine = create();
            bool observed = false;
            engine.CharJudged += judgement =>
            {
                observed = true;
                Assert.That(engine.Lines[0].Cells[judgement.CellIndex].JudgedTier, Is.EqualTo(judgement.Type));
            };
            engine.Update(1000);
            Assert.That(engine.ProcessKey('a', 1000 + delta), Is.True);
            Assert.That(observed, Is.True);
            Assert.That(engine.Lines[0].Cells[0].JudgedTier, Is.EqualTo(expected));
        }

        [Test]
        public void CorrectedLetterRetainsItsCappedAwardThroughRetypeAndSilentReplay()
        {
            var engine = create();
            engine.Update(1000);
            Assert.That(engine.ProcessKey('x', 1000), Is.True);
            Assert.That(engine.ProcessBackspace(), Is.True);
            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.Lines[0].Cells[0].JudgedTier, Is.EqualTo(JudgementType.Ok));
            Assert.That(engine.ProcessBackspace(), Is.True);
            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.Lines[0].Cells[0].JudgedTier, Is.EqualTo(JudgementType.Ok));

            int events = 0;
            engine.CharJudged += _ => events++;
            engine.Rebuild(replay =>
            {
                replay.Update(1000);
                replay.ProcessKey('a', 1225);
            });
            Assert.That(events, Is.Zero, "replay reconstruction is silent");
            Assert.That(engine.Lines[0].Cells[0].JudgedTier, Is.EqualTo(JudgementType.Ok));
            engine.Rebuild(replay => replay.Update(0));
            Assert.That(engine.Lines[0].Cells[0].JudgedTier, Is.Null, "seeking before the press removes its feedback");
        }

        [TestCase(JudgementType.Premature)]
        [TestCase(JudgementType.Lagging)]
        public void CorrectOffTimeInputsUseTheirActualMehResult(JudgementType tier)
            => Assert.That(LyricLineDisplay.JudgementBarColourFor(tier), Is.EqualTo(Color4.Orange));

        [Test]
        public void RetypingCannotReplaceTheOriginalAwardAfterWindowsChange()
        {
            var engine = create();
            engine.Update(1000);
            engine.ProcessKey('a', 1225);
            Assert.That(engine.Lines[0].Cells[0].JudgedTier, Is.EqualTo(JudgementType.Ok));
            engine.WindowScale = 10;
            engine.ProcessBackspace();
            engine.ProcessKey('a', 1000);
            Assert.That(engine.Lines[0].Cells[0].JudgedTier, Is.EqualTo(JudgementType.Ok));
        }

        [Test]
        public void RushCapMehRetainsTheAwardInsteadOfDisplayingItsGreatDelta()
        {
            const string text = "abcdefghijklmnopqrst";
            var engine = create(text, 1200);
            engine.FletcherEnabled = true;
            engine.FlexibleLineSnap = true;
            engine.BoundedRush = true;
            engine.RushCapCostsAccuracy = true;
            engine.SyllableTiming = true;
            engine.Update(1000);
            foreach (char c in text)
                engine.ProcessKey(c, 1000);
            Assert.That(engine.Lines[0].Cells.Any(cell => cell.JudgedPastRushCap && cell.JudgedTier == JudgementType.Meh), Is.True);
        }

        private static TypingEngine create(string text = "abcde", double end = 5000) => new TypingEngine(new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Feedback", FolderPath = "", AudioFileName = "a.mp3" },
            Granularity = TimingGranularity.Word,
            Lines = new[]
            {
                new LyricLine
                {
                    RawText = text, StartTime = 0, EndTime = 60000, SingEndTime = end,
                    Units = new[] { new TimedUnit { Text = text, StartTime = 1000, EndTime = end } },
                },
            },
        }) { SyllableTiming = false };
    }
}
