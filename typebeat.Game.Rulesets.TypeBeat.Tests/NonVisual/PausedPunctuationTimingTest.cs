// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class PausedPunctuationTimingTest
    {
        private static IEnumerable<TestCaseData> punctuatedPauses()
        {
            foreach (string marks in Typeability.PUNCTUATION.Select(c => c.ToString()).Concat(new[] { ".,", Typeability.PUNCTUATION }))
            {
                for (int split = 2; split <= marks.Length + 2; split++)
                {
                    foreach (bool aligned in new[] { false, true })
                        yield return new TestCaseData("ab" + marks + "cd", split, aligned);
                }
            }
        }

        [TestCaseSource(nameof(punctuatedPauses))]
        public void EveryCharacterStaysOnItsAuthoredSideOfThePause(string text, int split, bool aligned)
        {
            var line = TypingLine.FromLyricLine(lineOf(text, new WordPause(1200, 1800, split)), literate: true, alignSubdivisionTargets: aligned);
            Assert.That(line.DisplayText, Is.EqualTo(text));
            for (int i = 0; i < line.Cells.Count; i++)
            {
                var cell = line.Cells[i];
                Assert.That(cell.TargetTime, Is.InRange(i < split ? 1000d : 1800d, i < split ? 1200d : 2000d), $"cell {i}: {cell.Expected}");
                if (i > 0)
                    Assert.That(cell.TargetTime, Is.GreaterThanOrEqualTo(line.Cells[i - 1].TargetTime));
            }
        }

        [Test]
        public void PunctuationOnBothSidesUsesOnlyItsOwnSungSpanWithoutMovingLetters()
        {
            var source = lineOf("ab.,cd", new WordPause(1200, 1800, 3));
            var line = TypingLine.FromLyricLine(source, literate: true, alignSubdivisionTargets: true);
            Assert.That(line.Cells.Select(c => c.TargetTime), Is.EqualTo(new[] { 1000d, 1100, 1150, 1800, 1800, 1900 }));
            var plain = TypingLine.FromLyricLine(source, alignSubdivisionTargets: true);
            Assert.That(line.Cells.Where(c => char.IsLetter(c.Expected)).Select(c => c.TargetTime),
                Is.EqualTo(plain.Cells.Select(c => c.TargetTime)));
        }

        [Test]
        public void MultiplePausesKeepPunctuationWithinEachSungSection()
        {
            var line = TypingLine.FromLyricLine(lineOf("ab.,cd!?ef", new WordPause(1200, 1500, 4), new WordPause(1700, 1800, 8)), literate: true);
            Assert.That(line.Cells[2].TargetTime, Is.InRange(1100d, 1200d));
            Assert.That(line.Cells[3].TargetTime, Is.InRange(1100d, 1200d));
            Assert.That(line.Cells[6].TargetTime, Is.InRange(1600d, 1700d));
            Assert.That(line.Cells[7].TargetTime, Is.InRange(1600d, 1700d));
        }

        [TestCase(1, 1800d)]
        [TestCase(2, 1100d)]
        public void AHyphenCreatedSpaceAlsoStaysOutsideThePause(int split, double target)
        {
            var source = lineOf("a-b", new WordPause(1200, 1800, split));
            var plain = TypingLine.FromLyricLine(source, alignSubdivisionTargets: true);
            var literate = TypingLine.FromLyricLine(source, literate: true, alignSubdivisionTargets: true);
            Assert.That(plain.DisplayText, Is.EqualTo("a b"));
            Assert.That(plain.Cells[1].TargetTime, Is.EqualTo(target));
            Assert.That(literate.Cells[1].TargetTime, Is.EqualTo(target));
        }

        [Test]
        public void SubdivisionTargetsRemainWithinTheirPausedStretch()
        {
            var source = new LyricLine
            {
                RawText = "ab.,cd", StartTime = 0, SingEndTime = 2000, EndTime = 5000,
                Units = new[] { new TimedUnit
                {
                    Text = "ab.,cd", StartTime = 1000, EndTime = 2000,
                    SyllableBoundaries = new[] { 1150d }, SyllableSplits = new[] { 1 },
                    Pauses = new[] { new WordPause(1200, 1800, 3) },
                } },
            };
            var line = TypingLine.FromLyricLine(source, literate: true, alignSubdivisionTargets: true);
            Assert.That(line.Cells.Select(c => c.TargetTime), Is.EqualTo(new[] { 1000d, 1150, 1175, 1800, 1800, 1900 }));
            Assert.That(line.SyllableMarkerCells, Is.EqualTo(new[] { 1, 3 }));
        }

        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void HardRockAndLiterateJudgeTheCharactersInTheirSungSections(int split)
        {
            var map = new TypeBeatBeatmap();
            var hit = new TypeBeatHitObject { StartTime = 0, Line = lineOf("ab.,cd", new WordPause(1200, 1800, split)), Granularity = TimingGranularity.Word, Literate = true };
            map.HitObjects.Add(hit);
            hit.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());
            var drawable = new DrawableTypeBeatRuleset(new TypeBeatRuleset(), map, new Mod[] { new TypeBeatModHardRock(), new TypeBeatModLiterate() });
            var engine = drawable.Engine;
            Assert.That(engine.SyllableTiming, Is.False);
            Assert.That(engine.Literate, Is.True);
            var judgements = new List<CharJudgement>();
            engine.CharJudged += judgements.Add;
            engine.Update(0);
            foreach (var cell in engine.Lines[0].Cells)
            {
                Assert.That(cell.TargetTime, Is.InRange(judgements.Count < split ? 1000d : 1800d, judgements.Count < split ? 1200d : 2000d));
                engine.Update(cell.TargetTime);
                Assert.That(engine.ProcessKey(cell.Expected, cell.TargetTime), Is.True);
            }
            Assert.That(judgements, Has.Count.EqualTo(6));
            Assert.That(judgements.All(j => j.Type == JudgementType.Great), Is.True);
        }

        private static LyricLine lineOf(string text, params WordPause[] pauses) => new LyricLine
        {
            RawText = text, StartTime = 0, SingEndTime = 2000, EndTime = 5000,
            Units = new[] { new TimedUnit { Text = text, StartTime = 1000, EndTime = 2000, Pauses = pauses } },
        };
    }
}
