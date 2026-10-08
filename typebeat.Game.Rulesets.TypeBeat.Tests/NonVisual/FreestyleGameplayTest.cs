// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class FreestyleGameplayTest
    {
        private static LyricLine line(string text, double start, double end)
            => new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = start + 3000,
                Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = start + 3000 } },
            };

        private static TypingEngine engine(params LyricLine[] lines)
        {
            var engine = new TypingEngine(new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Freestyle", FolderPath = "test", AudioFileName = "test.mp3" },
                Lines = lines,
                Granularity = TimingGranularity.Line,
            });
            engine.Update(1000);
            return engine;
        }

        [TestCase(-1201, JudgementType.Premature)]
        [TestCase(-1200, JudgementType.Meh)]
        [TestCase(-601, JudgementType.Meh)]
        [TestCase(-600, JudgementType.Ok)]
        [TestCase(-301, JudgementType.Ok)]
        [TestCase(-300, JudgementType.Great)]
        [TestCase(300, JudgementType.Great)]
        [TestCase(301, JudgementType.Ok)]
        [TestCase(600, JudgementType.Ok)]
        [TestCase(601, JudgementType.Meh)]
        [TestCase(1200, JudgementType.Meh)]
        [TestCase(1201, JudgementType.Lagging)]
        public void FreestyleLadderDoublesAllQualityWindows(double delta, JudgementType expected)
        {
            var run = engine(line("a" + Typeability.FREESTYLE_MARKER + "b", 1000, 10000));
            Assert.That(run.WindowsFor(run.Lines[0].Cells[1]).Classify(delta), Is.EqualTo(expected));
            Assert.That(run.WindowsFor(run.Lines[0].Cells[0]).GreatLate, Is.EqualTo(150));
        }

        [TestCase(300, JudgementType.Great)]
        [TestCase(301, JudgementType.Ok)]
        [TestCase(600, JudgementType.Ok)]
        [TestCase(601, JudgementType.Meh)]
        [TestCase(1200, JudgementType.Meh)]
        public void InputUsesFreestyleWindows(double delta, JudgementType expected)
        {
            var run = engine(line("a" + Typeability.FREESTYLE_MARKER + "b", 1000, 10000));
            var judged = new List<CharJudgement>();
            run.CharJudged += judged.Add;
            run.ProcessKey('a', 1000);
            run.ProcessKey(' ', 2000 + delta);
            Assert.That(judged.Last().Type, Is.EqualTo(expected));
            Assert.That(judged.Last().Delta, Is.EqualTo(delta));
            Assert.That(run.Lines[0].Cells[1].TypedChar, Is.EqualTo(' '));
            Assert.That(run.Mistypes, Is.Zero);
        }

        [Test]
        public void FreestyleWindowsComposeWithRateAndModScalesAndStayCached()
        {
            var run = engine(line("a" + Typeability.FREESTYLE_MARKER + "b", 1000, 10000));
            var cells = run.Lines[0].Cells;
            run.WindowScale = 1.5;
            run.HardRockFromMod = true;
            run.UnhalvedHardRockWindows = false;
            Assert.That(run.WindowsFor(cells[0]).GreatLate, Is.EqualTo(112.5));
            Assert.That(run.WindowsFor(cells[1]).GreatLate, Is.EqualTo(225));
            Assert.That(run.WindowsFor(cells[1]).MehLate, Is.EqualTo(900));
            Assert.That(run.WindowsFor(cells[1]), Is.SameAs(run.WindowsFor(cells[1])));
            run.UnhalvedHardRockWindows = true;
            Assert.That(run.WindowsFor(cells[1]).GreatLate, Is.EqualTo(450));
        }

        [Test]
        public void RetypeAndBothSyncReadoutsUseTheFreestyleLadder()
        {
            var run = engine(line("a" + Typeability.FREESTYLE_MARKER + "b", 1000, 10000));
            var judged = new List<CharJudgement>();
            run.CharJudged += judged.Add;
            run.ProcessKey('a', 1000);
            run.ProcessKey(' ', 2250);
            long firstScore = run.Score;
            Assert.That(run.LiveSyncPercent, Is.EqualTo(100 * (1 + 1 - 250 / 1200.0) / 2).Within(1e-9));
            Assert.That(run.ProcessBackspace(), Is.True);
            run.ProcessKey('x', 2600);
            Assert.That(judged.Last().Type, Is.EqualTo(JudgementType.Great), "first award is kept after retype");
            Assert.That(judged.Last().Delta, Is.EqualTo(250));
            Assert.That(run.Score, Is.EqualTo(firstScore), "retypes do not farm score");
            run.ProcessKey('b', 3000);
            Assert.That(run.BuildResults().SyncPercent, Is.EqualTo(100 * (3 - 250 / 1200.0) / 3).Within(1e-9));
        }

        [TestCase(HitResult.Great, 300)]
        [TestCase(HitResult.Ok, 600)]
        [TestCase(HitResult.Meh, 1200)]
        [TestCase(HitResult.Miss, 1200)]
        public void FrameworkExpiryAndJudgementWidthsAgree(HitResult result, double expected)
        {
            Assert.That(new TypeBeatHitWindows(isFreestyle: true).WindowFor(result), Is.EqualTo(expected));
            Assert.That(new TypeBeatHitWindows().WindowFor(result), Is.EqualTo(expected / 2));
        }

        [TestCase(-300, JudgementType.Great)]
        [TestCase(300, JudgementType.Great)]
        [TestCase(600, JudgementType.Ok)]
        [TestCase(1200, JudgementType.Meh)]
        public void BoundaryFreestyleKeepsItsFullWindowBeforeExpiry(double delta, JudgementType expected)
        {
            string text = Typeability.FREESTYLE_MARKER.ToString();
            var authored = new LyricLine
            {
                RawText = text,
                StartTime = 1000,
                EndTime = 2000,
                SingEndTime = 2000,
                Units = new[] { new TimedUnit { Text = text, StartTime = 2000, EndTime = 2000 } },
            };
            var run = engine(authored);
            var judged = new List<CharJudgement>();
            run.CharJudged += judged.Add;
            double target = run.Lines[0].Cells[0].TargetTime;
            Assert.That(run.EffectiveSealTime(run.Lines[0]), Is.EqualTo(target + 1201));
            run.Update(target + delta);
            Assert.That(run.LineIsActive, Is.True, "pinned caret keeps a freestyle endpoint open through Meh");
            Assert.That(run.ProcessKey(' ', target + delta), Is.True);
            Assert.That(judged.Last().Type, Is.EqualTo(expected));
        }

        [Test]
        public void WidenedFreestyleDeadlineComposesWithWindowScaleAndNormalDeadlineIsUnchanged()
        {
            string text = Typeability.FREESTYLE_MARKER.ToString();
            var authored = new LyricLine
            {
                RawText = text,
                StartTime = 1000,
                EndTime = 2000,
                SingEndTime = 2000,
                Units = new[] { new TimedUnit { Text = text, StartTime = 2000, EndTime = 2000 } },
            };
            var run = engine(authored);
            run.WindowScale = 1.5;
            Assert.That(run.EffectiveSealTime(run.Lines[0]), Is.EqualTo(3801));
            run.Update(3800);
            Assert.That(run.LineIsActive, Is.True);
            run.Update(3801);
            Assert.That(run.LineIsActive, Is.False);
            Assert.That(run.BuildResults().Counts[JudgementType.Miss], Is.EqualTo(1));

            var normal = engine(line("a", 1000, 2000));
            Assert.That(normal.EffectiveSealTime(normal.Lines[0]), Is.EqualTo(normal.Lines[0].EndTime + normal.Lines[0].SealGraceMs));
        }

        [TestCase(WordSkipRule.Reclaimable)]
        [TestCase(WordSkipRule.ImmediateMiss)]
        public void WordSkipStopsAtFreestyleAndTheSpaceFillsIt(WordSkipRule skipRule)
        {
            var run = engine(line("ab" + Typeability.FREESTYLE_MARKER + Typeability.FREESTYLE_MARKER + "c", 1000, 10000));
            run.SpaceSkipsWord = true;
            run.WordSkip = skipRule;
            var cells = run.Lines[0].Cells;
            run.ProcessKey('a', 1000);
            run.ProcessKey(' ', cells[2].TargetTime);
            Assert.That(cells[1].State, Is.EqualTo(skipRule == WordSkipRule.Reclaimable ? CellState.Abandoned : CellState.Missed));
            Assert.That(cells[2].State, Is.EqualTo(CellState.Correct));
            Assert.That(cells[2].TypedChar, Is.EqualTo(' '));
            Assert.That(cells[3].State, Is.EqualTo(CellState.Untyped));
            Assert.That(cells[4].State, Is.EqualTo(CellState.Untyped));
            Assert.That(run.CaretIndex, Is.EqualTo(3));
            run.ProcessKey(' ', cells[3].TargetTime);
            Assert.That(cells[3].State, Is.EqualTo(CellState.Correct));
            Assert.That(cells[3].TypedChar, Is.EqualTo(' '));
            Assert.That(run.Mistypes, Is.Zero);
        }

        [Test]
        public void RecordedSpacesReplayWithTheSameFreestyleJudgementsAndSync()
        {
            string text = "a" + Typeability.FREESTYLE_MARKER + Typeability.FREESTYLE_MARKER + "c";
            var live = engine(line(text, 1000, 10000));
            live.SpaceSkipsWord = true;
            live.InputEra2 = true;
            var playback = engine(line(text, 1000, 10000));
            ReplayEngineFeed.Apply(playback, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: true));
            ReplayEngineFeed.Apply(playback, TypeBeatReplayFrame.CreateExtendedConfigFrame(0, inputEra2: true));
            foreach (var frame in new[]
                     {
                         new TypeBeatReplayFrame(1000, 'a'),
                         new TypeBeatReplayFrame(2000, ' '),
                         new TypeBeatReplayFrame(2500, ' '),
                         new TypeBeatReplayFrame(3250, 'c'),
                     })
            {
                live.Update(frame.Time);
                live.ProcessKey(frame.Character, frame.Time);
                ReplayEngineFeed.Apply(playback, frame);
            }
            Assert.That(playback.BuildResults().Counts, Is.EquivalentTo(live.BuildResults().Counts));
            Assert.That(playback.Score, Is.EqualTo(live.Score));
            Assert.That(playback.BuildResults().SyncPercent, Is.EqualTo(live.BuildResults().SyncPercent));
            Assert.That(playback.Lines[0].Cells.Select(c => c.TypedChar), Is.EqualTo(new char?[] { 'a', ' ', ' ', 'c' }));
        }

        [Test]
        public void FreestyleInputAvailabilityUsesCurrentCaretAndDoubledWindow()
        {
            var run = engine(line("a" + Typeability.FREESTYLE_MARKER + "b", 1000, 10000));
            Assert.That(run.FreestyleInputWindowOpen(1000), Is.False, "ordinary caret is not a freestyle slot");
            run.ProcessKey('a', 1000);
            Assert.That(run.FreestyleInputWindowOpen(799), Is.False);
            Assert.That(run.FreestyleInputWindowOpen(800), Is.True);
            Assert.That(run.FreestyleInputWindowOpen(1800), Is.True, "early Great space is valid");
            Assert.That(run.FreestyleInputWindowOpen(3200), Is.True);
            Assert.That(run.FreestyleInputWindowOpen(3201), Is.False);
            Assert.That(run.CaretIndex, Is.EqualTo(1), "availability query does not mutate input state");
        }

        [Test]
        public void FreestyleInputAvailabilityPreservesHistoricalSpanTiming()
        {
            var run = engine(line(new string(Typeability.FREESTYLE_MARKER, 2), 1000, 10000));
            run.SyllableTiming = true;
            run.CharTimedStretch = false;
            run.ProcessKey('x', 1000);
            Assert.That(run.FreestyleInputWindowOpen(5000), Is.True, "historic span ends at 4000 with 1200 late tolerance");
            run.CharTimedStretch = true;
            Assert.That(run.FreestyleInputWindowOpen(5000), Is.False, "live timing uses the second cell target at 2500");
        }

        [Test]
        public void FreestyleInputAvailabilityRespectsManualEntryWindow()
        {
            var run = finishedFreestyle(Typeability.FREESTYLE_MARKER.ToString());
            run.WindowScale = 2;
            run.ProcessKey(' ', 2600);
            Assert.That(run.FreestyleInputWindowOpen(5400), Is.False, "entry is closed even when the widened ladder fits");
            Assert.That(run.FreestyleInputWindowOpen(5500), Is.True);
        }

        private static TypingEngine finishedFreestyle(string nextText = "cd", bool caseSensitive = false)
        {
            var run = engine(line("a" + Typeability.FREESTYLE_MARKER, 1000, 7000), line(nextText, 7000, 14000));
            run.ManualNewlines = true;
            run.NewlineOnTypedLetter = true;
            run.FletcherEnabled = true;
            run.FlexibleLineSnap = true;
            run.BoundedRush = true;
            run.CaseSensitive = caseSensitive;
            run.ProcessKey('a', 1000);
            run.ProcessKey('q', 2500);
            Assert.That(run.ActiveLineIndex, Is.Zero);
            Assert.That(run.CaretIndex, Is.EqualTo(2));
            return run;
        }

        [TestCase(false, 'C')]
        [TestCase(true, 'c')]
        public void WrongLettersStayParkedAfterFreestyleUntilNextCharacterMatches(bool caseSensitive, char correct)
        {
            var run = finishedFreestyle(caseSensitive: caseSensitive);
            run.Update(7000);
            Assert.That(run.ProcessKey('x', 7000), Is.False);
            if (caseSensitive)
                Assert.That(run.ProcessKey('C', 7000), Is.False);
            Assert.That(run.ActiveLineIndex, Is.Zero);
            Assert.That(run.Mistypes, Is.Zero);
            Assert.That(run.ProcessKey(correct, 7000), Is.True);
            Assert.That(run.ActiveLineIndex, Is.EqualTo(1));
            Assert.That(run.Lines[1].Cells[0].State, Is.EqualTo(CellState.Correct));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExplicitNewlinesStillCloseFreestyleEndedLines(bool enter)
        {
            var run = finishedFreestyle();
            Assert.That(enter ? run.ProcessEnter(2600) : run.ProcessKey(' ', 2600), Is.True);
            Assert.That(run.ActiveLineIndex, Is.EqualTo(1));
            Assert.That(run.AwaitingEntry, Is.True);
            Assert.That(run.Lines[1].Cells[0].State, Is.EqualTo(CellState.Untyped));
        }

        [Test]
        public void MatchingCharacterMayMoveEarlyButWaitsForEntryWindow()
        {
            var run = finishedFreestyle();
            Assert.That(run.ProcessKey('x', 2600), Is.False);
            Assert.That(run.ActiveLineIndex, Is.Zero);
            Assert.That(run.ProcessKey('c', 2600), Is.True);
            Assert.That(run.ActiveLineIndex, Is.EqualTo(1));
            Assert.That(run.Lines[1].Cells[0].State, Is.EqualTo(CellState.Untyped));
            run.Update(7000);
            run.ProcessKey('c', 7000);
            Assert.That(run.Lines[1].Cells[0].State, Is.EqualTo(CellState.Correct));
        }

        [Test]
        public void LeadingAutoSkipCellsDoNotChangeTheRequiredNewlineCharacter()
        {
            var run = finishedFreestyle("!cd");
            run.Update(7000);
            Assert.That(run.ProcessKey('x', 7000), Is.False);
            Assert.That(run.ProcessKey('c', 7000), Is.True);
            Assert.That(run.Lines[1].Cells.First(c => c.IsTypeable).State, Is.EqualTo(CellState.Correct));
        }

        [Test]
        public void FreestyleToFreestyleKeepsAcceptingAnyNewlineCharacter()
        {
            var run = finishedFreestyle(Typeability.FREESTYLE_MARKER.ToString());
            run.Update(7000);
            Assert.That(run.ProcessKey('x', 7000), Is.True);
            Assert.That(run.ActiveLineIndex, Is.EqualTo(1));
            Assert.That(run.Lines[1].Cells[0].TypedChar, Is.EqualTo('x'));
        }

        [Test]
        public void OrdinaryManualNewlineStillTypesThroughAWrongFirstCharacter()
        {
            var run = engine(line("ab", 1000, 7000), line("cd", 7000, 14000));
            run.ManualNewlines = true;
            run.NewlineOnTypedLetter = true;
            run.FletcherEnabled = true;
            run.FlexibleLineSnap = true;
            run.BoundedRush = true;
            run.ProcessKey('a', 1000);
            run.ProcessKey('b', 2500);
            run.Update(7000);
            Assert.That(run.ProcessKey('x', 7000), Is.True);
            Assert.That(run.ActiveLineIndex, Is.EqualTo(1));
            Assert.That(run.Lines[1].Cells[0].State, Is.EqualTo(CellState.Wrong));
        }
    }
}
