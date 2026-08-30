// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 209: the STRETCH exploit in the syllable-span rule, and the era that keeps every stored
// replay judging the way it was played.
//
// Backlog 179 grades a grouped cell on distance from its syllable's sung SPAN, 0 anywhere inside
// it. Two shapes turn that into free points, because inside one span they make every cell
// interchangeable:
//
//   FREESTYLE. A token of markers ("&&&&&&") passes Syllabifier.IsSyllabifiable (only 3+ identical
//   LETTERS fail it, and '&' is not a letter), so it gets ONE group over the whole word, and a
//   freestyle cell accepts any key. A player who mashes the section the instant it opens fills
//   every slot on a judged delta of zero, seconds ahead of the vocal.
//
//   A STRETCHED RUN. Three or more identical characters inside a single syllable ("yyyy" of a
//   subtimed "hey|yyyy", the "000" of "1000") are indistinguishable to the matcher, so the same
//   mash types the whole run out at delta zero.
//
// The fix reverts exactly those cells to CHARACTER timing (the cell's own point target) while the
// rest of the line keeps the span rule, carried as an ERA on the CONFIG frame's flags bit 6
// (TypingEngine.CharTimedStretch).
//
// The deltas here are CROSS-CHECKS, worked out from the fixtures' own targets and the Line-tier
// window ladder (Great [-250, 400], Ok [-600, 1000], Meh [-1200, 2000]).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class CharTimedStretchTest
    {
        private const char marker = Typeability.FREESTYLE_MARKER;

        #region Fixture builders

        private static TimedUnit unit(string text, double start, double end, params double[] boundaries)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end, SyllableBoundaries = boundaries };

        private static TimedUnit splitUnit(string text, double start, double end, double boundary, int splitChar)
            => new TimedUnit
            {
                Text = text,
                StartTime = start,
                EndTime = end,
                SyllableBoundaries = new[] { boundary },
                SyllableSplits = new[] { splitChar },
            };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static LyricBeatmap map(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = lines,
            Granularity = TimingGranularity.Line,
        };

        /// <summary>
        /// The field report's shape: a freestyle SECTION, six markers sung as one word over
        /// [1000, 13000]. Cells target 1000, 3000, 5000, 7000, 9000, 11000 and the whole token is
        /// ONE syllable group spanning [1000, 13000], because the syllabifier only refuses a run of
        /// three identical LETTERS and '&amp;' is not a letter.
        /// </summary>
        private static LyricBeatmap freestyleSpam()
            => map(line(new string(marker, 6), 1000, 60000, 13000, unit(new string(marker, 6), 1000, 13000)));

        /// <summary>
        /// "1000" over [1000, 13000]: cells target 1000, 4000, 7000, 10000, and a digit run is one
        /// syllable, so all four share the group [1000, 13000]. The '1' is a lone character in that
        /// group and the three '0's are a stretched run, which is what makes this fixture able to
        /// tell the two rules apart INSIDE a single syllable.
        /// </summary>
        private static LyricBeatmap digitRun() => map(line("1000", 1000, 60000, 13000, unit("1000", 1000, 13000)));

        /// <summary>
        /// "goo" over [1000, 13000] (cells 1000, 5000, 9000), one syllable: a run of exactly TWO
        /// identical characters, which keeps span timing. The threshold is 3 for the same reason
        /// <see cref="Syllabifier.IsSyllabifiable"/>'s is.
        /// </summary>
        private static LyricBeatmap doubledChar() => map(line("goo", 1000, 60000, 13000, unit("goo", 1000, 13000)));

        /// <summary>
        /// The subtimed stretch: "heyyyyy" mapper-subtimed at 5000 with the authored split "hey|yyyy",
        /// so the gate that would have left a stylised spelling ungrouped does not apply. Group 0
        /// owns cells 0..2 over [1000, 5000] (targets 1000, 2333.33, 3666.67) and group 1 owns
        /// cells 3..6 over [5000, 13000] (targets 5000, 7000, 9000, 11000). The 'y' in group 0 is a
        /// run of one and keeps the span; the four in group 1 are the stretch.
        /// </summary>
        private static LyricBeatmap subtimedStretch()
            => map(line("heyyyyy", 1000, 60000, 13000, splitUnit("heyyyyy", 1000, 13000, 5000, 3)));

        /// <summary>
        /// The autoplay fixture: a freestyle section and a subtimed stretch on one line, cells
        /// &amp;0 &amp;1 &amp;2 &amp;3 _4 a5 a6 a7 a8 a9.
        ///
        /// <para>"aaaaa" is mapper-subtimed at 9000 with a DERIVED split, which the syllabifier cuts
        /// "aa|aaa" while the target spread walks the five characters evenly BY INDEX across the two
        /// segments (the cut at 2.5 characters). The two therefore disagree, and cell 7 (the first
        /// character of the three-long run) is timed at 8200 while its own syllable is not sung
        /// until 9000: 800 ms early, which is exactly the shape the generator's span clamp used to
        /// move and must stop moving now the engine judges that cell on its target.</para>
        /// </summary>
        private static LyricLine autoplayLine()
            => line(new string(marker, 4) + " aaaaa", 1000, 60000, 17000,
                unit(new string(marker, 4), 1000, 5000),
                unit("aaaaa", 5000, 17000, 9000));

        private static TypingEngine started(LyricBeatmap beatmap, bool charTimedStretch = false)
        {
            var engine = new TypingEngine(beatmap) { SyllableTiming = true, CharTimedStretch = charTimedStretch };
            engine.Update(1000);
            Assert.AreEqual(0, engine.ActiveLineIndex);
            return engine;
        }

        private static TypeBeatBeatmap scored(LyricLine lyric)
        {
            var beatmap = new TypeBeatBeatmap();
            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = lyric.StartTime, LineIndex = 0, Line = lyric, Granularity = TimingGranularity.Line });

            foreach (var hitObject in beatmap.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return beatmap;
        }

        private static int count(TypeBeatReplayAccount account, HitResult result)
            => account.Statistics.GetValueOrDefault(result);

        private static List<CharJudgement> record(TypingEngine engine)
        {
            var judged = new List<CharJudgement>();
            engine.CharJudged += j => judged.Add(j);
            return judged;
        }

        #endregion

        #region Structure: the groups the two rules disagree inside

        [Test]
        public void AFreestyleTokenIsOneGroupOverTheWholeWord()
        {
            var tl = TypingLine.FromLyricLine(freestyleSpam().Lines[0]);

            Assert.AreEqual(6, tl.Cells.Count);
            Assert.IsTrue(tl.Cells.All(c => c.IsFreestyle));

            for (int i = 0; i < 6; i++)
                Assert.AreEqual(1000 + i * 2000, tl.Cells[i].TargetTime, 1e-9, $"cell {i} target");

            // THE MEMBERSHIP PIN: a freestyle token is grouped like any other syllabifiable word,
            // which is exactly why the span rule reached it. Nothing about the fix ungroups it (the
            // sung-syllable highlight renders off these groups unconditionally), so this must hold
            // before and after, and a future grouping change has to come past it.
            Assert.AreEqual(1, tl.Syllables.Count);
            Assert.AreEqual(new SyllableGroup(0, 6, 1000, 13000), tl.Syllables[0]);

            for (int i = 0; i < 6; i++)
                Assert.AreEqual(0, tl.SyllableIndexOf(i), $"freestyle cell {i} is in the group");

            Assert.IsTrue(Syllabifier.IsSyllabifiable(new string(marker, 6)),
                "the syllabifier only refuses a run of three identical LETTERS");
        }

        [Test]
        public void ADigitRunSharesOneGroupWithItsNeighbour()
        {
            var tl = TypingLine.FromLyricLine(digitRun().Lines[0]);

            Assert.AreEqual("1000", tl.DisplayText);
            Assert.AreEqual(new double[] { 1000, 4000, 7000, 10000 }, tl.Cells.Select(c => c.TargetTime).ToArray());

            Assert.AreEqual(1, tl.Syllables.Count);
            Assert.AreEqual(new SyllableGroup(0, 4, 1000, 13000), tl.Syllables[0]);

            for (int i = 0; i < 4; i++)
                Assert.AreEqual(0, tl.SyllableIndexOf(i), $"cell {i}");
        }

        [Test]
        public void TheSubtimedStretchKeepsItsRunInsideOneGroup()
        {
            var tl = TypingLine.FromLyricLine(subtimedStretch().Lines[0]);

            Assert.AreEqual("heyyyyy", tl.DisplayText);
            Assert.AreEqual(2, tl.Syllables.Count);
            Assert.AreEqual(new SyllableGroup(0, 3, 1000, 5000), tl.Syllables[0]);
            Assert.AreEqual(new SyllableGroup(3, 7, 5000, 13000), tl.Syllables[1]);

            double[] expectedTargets = { 1000, 1000 + 4000 / 3.0, 1000 + 8000 / 3.0, 5000, 7000, 9000, 11000 };

            for (int i = 0; i < expectedTargets.Length; i++)
                Assert.AreEqual(expectedTargets[i], tl.Cells[i].TargetTime, 1e-9, $"cell {i} target");
        }

        [Test]
        public void ADoubledCharIsOneOrdinarySyllable()
        {
            var tl = TypingLine.FromLyricLine(doubledChar().Lines[0]);

            Assert.AreEqual(new double[] { 1000, 5000, 9000 }, tl.Cells.Select(c => c.TargetTime).ToArray());
            Assert.AreEqual(1, tl.Syllables.Count);
            Assert.AreEqual(new SyllableGroup(0, 3, 1000, 13000), tl.Syllables[0]);
        }

        #endregion

        #region The exploit, as the pure span rule grades it

        /// <summary>
        /// TODAY'S ACCOUNT, which is also the era every stored replay must keep re-deriving under:
        /// six keys mashed the instant the section opens fill all six freestyle slots on a delta of
        /// zero, ten seconds ahead of the last one's target. This is the report the field bug came
        /// in as, and it stays green after the fix because the fix is an ERA (bit 6 clear = this).
        /// </summary>
        [Test]
        public void ThePureSpanRuleGradesFreestyleSpamPerfect()
        {
            var engine = started(freestyleSpam(), charTimedStretch: false);
            var judged = record(engine);

            for (int i = 0; i < 6; i++)
                Assert.IsTrue(engine.ProcessKey('q', 1000), $"press {i}");

            Assert.AreEqual(6, judged.Count);
            Assert.IsTrue(judged.All(j => j.Type == JudgementType.Great), "every mashed slot is a top-tier hit");
            Assert.IsTrue(judged.All(j => j.Delta == 0), "and every one of them on a delta of zero");
            Assert.AreEqual(6, engine.MaxCombo);
        }

        /// <summary>The same free ride on a stretched run: the three '0's of "1000" typed at once.</summary>
        [Test]
        public void ThePureSpanRuleGradesAStretchedRunPerfect()
        {
            var engine = started(digitRun(), charTimedStretch: false);
            var judged = record(engine);

            foreach (char c in "1000")
                Assert.IsTrue(engine.ProcessKey(c, 1000));

            Assert.IsTrue(judged.All(j => j.Type == JudgementType.Great));
            Assert.IsTrue(judged.All(j => j.Delta == 0));
        }

        #endregion

        #region The predicate: which cells lose the span

        [Test]
        public void EveryFreestyleCellIsAStretchCell()
        {
            var tl = TypingLine.FromLyricLine(freestyleSpam().Lines[0]);

            for (int i = 0; i < tl.Cells.Count; i++)
                Assert.IsTrue(tl.IsCharTimedStretch(i), $"cell {i}");

            // A LONE freestyle slot between two letters qualifies too: it is the "any key" rule that
            // makes it unjudgeable on a span, not the length of the run it sits in.
            var lone = TypingLine.FromLyricLine(map(line("a" + marker + "b", 1000, 60000, 13000, unit("a" + marker + "b", 1000, 13000))).Lines[0]);

            Assert.IsFalse(lone.IsCharTimedStretch(0));
            Assert.IsTrue(lone.IsCharTimedStretch(1));
            Assert.IsFalse(lone.IsCharTimedStretch(2));
        }

        [Test]
        public void AThreeCharRunIsAStretchAndItsNeighbourIsNot()
        {
            var tl = TypingLine.FromLyricLine(digitRun().Lines[0]);

            Assert.IsFalse(tl.IsCharTimedStretch(0), "the '1' is a run of one");

            for (int i = 1; i < 4; i++)
                Assert.IsTrue(tl.IsCharTimedStretch(i), $"the '0' at cell {i}");
        }

        [Test]
        public void ARunOfTwoIsNotAStretch()
        {
            var tl = TypingLine.FromLyricLine(doubledChar().Lines[0]);

            for (int i = 0; i < tl.Cells.Count; i++)
                Assert.IsFalse(tl.IsCharTimedStretch(i), $"cell {i} of \"goo\"");
        }

        [Test]
        public void ARunIsCutAtTheSyllableBoundary()
        {
            var tl = TypingLine.FromLyricLine(subtimedStretch().Lines[0]);

            Assert.IsFalse(tl.IsCharTimedStretch(0), "'h'");
            Assert.IsFalse(tl.IsCharTimedStretch(1), "'e'");
            Assert.IsFalse(tl.IsCharTimedStretch(2), "the 'y' the split leaves alone in the first syllable");

            for (int i = 3; i < 7; i++)
                Assert.IsTrue(tl.IsCharTimedStretch(i), $"the 'y' at cell {i}, inside the four-long run");
        }

        /// <summary>
        /// Case folds, matching the matcher: default gameplay is case-insensitive, so the "YyY" of a
        /// subtimed "heY|YyY" is one run of three however the mapper capitalised it. A SPACE breaks
        /// a run outright (it is in no group at all), which is what keeps "a a a" three separate
        /// characters.
        /// </summary>
        [Test]
        public void RunsFoldCaseAndBreakAtSpaces()
        {
            var folded = TypingLine.FromLyricLine(map(line("heYYyY", 1000, 60000, 13000, splitUnit("heYYyY", 1000, 13000, 5000, 3))).Lines[0]);

            for (int i = 0; i < 3; i++)
                Assert.IsFalse(folded.IsCharTimedStretch(i), $"cell {i} of the first syllable");

            for (int i = 3; i < 6; i++)
                Assert.IsTrue(folded.IsCharTimedStretch(i), $"cell {i} of \"YyY\"");

            var spaced = TypingLine.FromLyricLine(map(line("a a a", 1000, 60000, 13000,
                unit("a", 1000, 5000), unit("a", 5000, 9000), unit("a", 9000, 13000))).Lines[0]);

            for (int i = 0; i < spaced.Cells.Count; i++)
                Assert.IsFalse(spaced.IsCharTimedStretch(i), $"cell {i} of \"a a a\"");
        }

        /// <summary>
        /// The flags are ADDITIVE: not one group, membership or target moves, which is what lets the
        /// construction parity fixtures (here and in the server's mirror) stay untouched.
        /// </summary>
        [Test]
        public void TheStretchFlagsMoveNoGroupOrTarget()
        {
            foreach (var beatmap in new[] { freestyleSpam(), digitRun(), doubledChar(), subtimedStretch() })
            {
                var tl = TypingLine.FromLyricLine(beatmap.Lines[0]);

                for (int i = 0; i < tl.Cells.Count; i++)
                {
                    int syllable = tl.SyllableIndexOf(i);

                    if (!tl.IsCharTimedStretch(i))
                        continue;

                    // A stretch cell is still a member of its group, and still sits inside its cell
                    // range: the fix lives in the judgement predicate, never in the grouping, so the
                    // sung-syllable highlight keeps lighting it.
                    Assert.GreaterOrEqual(syllable, 0, $"cell {i} of \"{tl.DisplayText}\" must keep its group");
                    Assert.GreaterOrEqual(i, tl.Syllables[syllable].StartCell);
                    Assert.Less(i, tl.Syllables[syllable].EndCellExclusive);
                }
            }
        }

        #endregion

        #region The live rule: a stretch is back on the clock

        /// <summary>
        /// THE FIX, on the reported shape: the same six keys mashed at the section's opening are
        /// judged on the characters' own targets, so only the first is on time and the other five
        /// are 2 to 10 seconds early, straight off the ladder.
        /// </summary>
        [Test]
        public void FreestyleSpamIsJudgedPerCharacterUnderTheLiveRule()
        {
            var engine = started(freestyleSpam(), charTimedStretch: true);
            var judged = record(engine);

            for (int i = 0; i < 6; i++)
                Assert.IsTrue(engine.ProcessKey('q', 1000), $"press {i}");

            // Targets are 1000 + 2000 i, so press i is 2000 i early. MehEarly is 1200.
            Assert.AreEqual(new double[] { 0, -2000, -4000, -6000, -8000, -10000 }, judged.Select(j => j.Delta).ToArray());
            Assert.AreEqual(JudgementType.Great, judged[0].Type);

            for (int i = 1; i < 6; i++)
                Assert.AreEqual(JudgementType.Premature, judged[i].Type, $"press {i}");
        }

        /// <summary>And a freestyle slot played ON its own target is still a Great: the fix prices
        /// the mash, it does not make the section unplayable.</summary>
        [Test]
        public void AFreestyleCellPlayedOnItsTargetIsStillGreat()
        {
            var engine = started(freestyleSpam(), charTimedStretch: true);
            var judged = record(engine);

            foreach (var cell in engine.Lines[0].Cells)
                Assert.IsTrue(engine.ProcessKey('q', cell.TargetTime));

            Assert.IsTrue(judged.All(j => j.Type == JudgementType.Great), "every on-target press judges Great");
            Assert.IsTrue(judged.All(j => j.Delta == 0));
        }

        /// <summary>
        /// The run arm, inside ONE syllable: the three '0's of "1000" lose the span while the '1'
        /// beside them keeps it, so the same press time is worth two different things four cells
        /// apart. That is the narrowing stated as sharply as the fixture can state it.
        /// </summary>
        [Test]
        public void AStretchedRunLosesTheSpanWhileItsNeighbourKeepsIt()
        {
            var engine = started(digitRun(), charTimedStretch: true);
            var judged = record(engine);

            foreach (char c in "1000")
                Assert.IsTrue(engine.ProcessKey(c, 1000));

            // '1' is inside [1000, 13000], so the span pays it 0. The '0's target 4000, 7000, 10000.
            Assert.AreEqual(new double[] { 0, -3000, -6000, -9000 }, judged.Select(j => j.Delta).ToArray());
            Assert.AreEqual(JudgementType.Great, judged[0].Type);
            Assert.IsTrue(judged.Skip(1).All(j => j.Type == JudgementType.Premature));
        }

        [Test]
        public void ALoneCharacterKeepsTheWholeSpanUnderTheLiveRule()
        {
            var engine = started(digitRun(), charTimedStretch: true);
            var judged = record(engine);

            // 11 seconds past its 1000 target, and still inside its syllable's span: delta 0.
            Assert.IsTrue(engine.ProcessKey('1', 12000));

            Assert.AreEqual(0, judged[0].Delta, 1e-9);
            Assert.AreEqual(JudgementType.Great, judged[0].Type);
        }

        [Test]
        public void ADoubledCharacterKeepsTheSpanUnderTheLiveRule()
        {
            var engine = started(doubledChar(), charTimedStretch: true);
            var judged = record(engine);

            // Targets 1000, 5000, 9000; every press 12000, deep inside the span [1000, 13000].
            foreach (char c in "goo")
                Assert.IsTrue(engine.ProcessKey(c, 12000));

            Assert.IsTrue(judged.All(j => j.Delta == 0), "a doubled letter is an ordinary spelling");
            Assert.IsTrue(judged.All(j => j.Type == JudgementType.Great));
        }

        /// <summary>The subtimed "hey|yyyy": the first syllable is span-judged whole, the four-long
        /// run in the second is not.</summary>
        [Test]
        public void ASubtimedStretchIsCharTimedAndItsOwnSyllableIsNot()
        {
            var engine = started(subtimedStretch(), charTimedStretch: true);
            var judged = record(engine);

            // "hey" pressed at 4900, inside [1000, 5000]: three different characters, all delta 0,
            // the third of them a 'y' that the split left out of the run.
            foreach (char c in "hey")
                Assert.IsTrue(engine.ProcessKey(c, 4900));

            // The run's cells target 5000, 7000, 9000 and 11000; all four mashed at 5100.
            for (int i = 0; i < 4; i++)
                Assert.IsTrue(engine.ProcessKey('y', 5100));

            Assert.AreEqual(new double[] { 0, 0, 0, 100, -1900, -3900, -5900 }, judged.Select(j => j.Delta).ToArray());

            Assert.IsTrue(judged.Take(4).All(j => j.Type == JudgementType.Great));
            Assert.IsTrue(judged.Skip(4).All(j => j.Type == JudgementType.Premature));
        }

        /// <summary>
        /// PRECEDENCE against backlog 247's first-char hybrid: a stretch cell that OPENS a group
        /// stays on its own point target, which is already stricter than any span rule. Cell 7 of
        /// the autoplay fixture is exactly that shape (first cell of the [9000, 17000] group, timed
        /// at 8200): pressed on its target it judges 0, and pressed on the span's start it judges
        /// the 800 its target is away, where the first-char arm would have graded those two presses
        /// the other way around (-800 and 0).
        /// </summary>
        [Test]
        public void AStretchCellOpeningAGroupKeepsItsPointTargetUnderTheHybrid()
        {
            var beatmap = map(autoplayLine());

            var onTarget = new TypingEngine(beatmap) { SyllableTiming = true, CharTimedStretch = true, FirstCharTiming = true };
            var onSpanStart = new TypingEngine(beatmap) { SyllableTiming = true, CharTimedStretch = true, FirstCharTiming = true };
            onTarget.Update(1000);
            onSpanStart.Update(1000);

            var judgedTarget = record(onTarget);
            var judgedStart = record(onSpanStart);

            foreach (var engine in new[] { onTarget, onSpanStart })
            {
                Assert.IsTrue(engine.ProcessKey('q', 1000)); // four freestyle slots on their targets
                Assert.IsTrue(engine.ProcessKey('q', 2000));
                Assert.IsTrue(engine.ProcessKey('q', 3000));
                Assert.IsTrue(engine.ProcessKey('q', 4000));
                Assert.IsTrue(engine.ProcessKey(' ', 4000));
                Assert.IsTrue(engine.ProcessKey('a', 5000)); // cell 5 opens [5000, 9000] on its start
                Assert.IsTrue(engine.ProcessKey('a', 6000)); // cell 6, non-first, in span
            }

            Assert.IsTrue(onTarget.ProcessKey('a', 8200));   // cell 7 on its own target
            Assert.IsTrue(onSpanStart.ProcessKey('a', 9000)); // cell 7 on its group's start

            Assert.AreEqual(0, judgedTarget[7].Delta, 1e-9, "the stretch cell's own target is still the perfect instant");
            Assert.AreEqual(800, judgedStart[7].Delta, 1e-9, "and the span start its group opens at is not");
            Assert.AreEqual(JudgementType.Great, judgedTarget[7].Type);
            Assert.AreEqual(JudgementType.Ok, judgedStart[7].Type);
        }

        #endregion

        #region The era: bit 6 clear re-derives what the run was played on

        /// <summary>
        /// The whole account of a mashed freestyle section, re-derived under both arms of the CONFIG
        /// frame's bit 6. Clear (every replay stored before backlog 209) is the perfect run its
        /// player was shown; set (the live client) is the same keystream priced on the clock. The
        /// two disagree on statistics, accuracy and total_score, which is why the bit has to exist:
        /// without it the recalculation tool would report every such row as unreproducible.
        /// </summary>
        [Test]
        public void TheStoredEraKeepsAMashedSectionPerfect()
        {
            var beatmap = scored(freestyleSpam().Lines[0]);

            var stored = TypeBeatReplayScorer.Score(beatmap, Array.Empty<Mod>(), mashReplay(charTimedStretch: false), TypoRule.Deferred, ComboRestoreRule.OnFix);
            var live = TypeBeatReplayScorer.Score(beatmap, Array.Empty<Mod>(), mashReplay(charTimedStretch: true), TypoRule.Deferred, ComboRestoreRule.OnFix);

            Assert.AreEqual(6, count(stored, HitResult.Great), "the pure span rule paid every mashed slot");
            Assert.AreEqual(0, count(stored, HitResult.Meh));
            Assert.AreEqual(1, stored.Accuracy, 1e-9);

            Assert.AreEqual(1, count(live, HitResult.Great), "only the slot the mash actually landed on");
            Assert.AreEqual(5, count(live, HitResult.Meh));
            Assert.AreEqual(0, count(live, HitResult.Miss), "an off-time press is still a hit (backlog 199)");

            Assert.Less(live.Accuracy, stored.Accuracy);
            Assert.Less(live.TotalScore, stored.TotalScore);

            // The mash still fills every cell, so the play is complete under both arms: what moved
            // is what the presses were WORTH, not whether they landed.
            Assert.AreEqual(6, stored.MaxCombo);
            Assert.AreEqual(6, live.MaxCombo);
        }

        /// <summary>Six keys a millisecond apart at the section's opening, as a live client would
        /// have recorded them (bit 0, 2, 3 and 4 set), with bit 6 the parameter.</summary>
        private static Replay mashReplay(bool charTimedStretch)
        {
            var replay = new Replay();

            replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: false,
                syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: charTimedStretch));

            for (int i = 0; i < 6; i++)
                replay.Frames.Add(new TypeBeatReplayFrame(1000 + i, 'q'));

            return replay;
        }

        /// <summary>
        /// The engine's own default is the OLD era, so nothing that does not ask for the narrowing
        /// gets it: a replay with no CONFIG frame at all, and every call site that predates the
        /// flag, keeps judging on the pure span.
        /// </summary>
        [Test]
        public void TheEngineDefaultIsTheStoredEra()
        {
            Assert.IsFalse(new TypingEngine(freestyleSpam()).CharTimedStretch);
            Assert.IsFalse(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true).CharTimedStretch);
        }

        #endregion

        #region Autoplay

        /// <summary>
        /// The fixture's disagreement, asserted before anything is played: the derived split cuts
        /// "aaaa" where the even-by-index target spread does not, so a STRETCH cell is timed
        /// outside its own syllable's span. That is what the generator's span clamp used to move,
        /// and what it must stop moving now the engine judges the cell on its target.
        /// </summary>
        [Test]
        public void TheAutoplayFixtureTimesAStretchCellOutsideItsSpan()
        {
            var tl = TypingLine.FromLyricLine(autoplayLine());

            Assert.AreEqual(10, tl.Cells.Count);
            Assert.AreEqual(-1, tl.SyllableIndexOf(4), "the word gap is in no group");

            // The freestyle section is one group; "aaaaa" is cut "aa|aaa".
            Assert.AreEqual(3, tl.Syllables.Count);
            Assert.AreEqual(new SyllableGroup(0, 4, 1000, 5000), tl.Syllables[0]);
            Assert.AreEqual(new SyllableGroup(5, 7, 5000, 9000), tl.Syllables[1]);
            Assert.AreEqual(new SyllableGroup(7, 10, 9000, 17000), tl.Syllables[2]);

            bool[] expected = { true, true, true, true, false, false, false, true, true, true };

            for (int i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], tl.IsCharTimedStretch(i), $"cell {i}");

            // THE DISAGREEMENT: the first cell of the three-long run is timed 800 ms before its own
            // syllable opens, so the span clamp would have pushed its press to 9000 and the engine,
            // judging that cell on its target now, would have graded the press 800 late (an Ok).
            Assert.AreEqual(8200, tl.Cells[7].TargetTime, 1e-9);
            Assert.Less(tl.Cells[7].TargetTime, tl.Syllables[2].StartTime);
        }

        /// <summary>
        /// Autoplay is still a perfect play with the narrowing live: the generator presses a stretch
        /// cell on its own target (never the span edge it would be clamped to) and every other cell
        /// on the span it is still judged against, so the whole line judges Great on a delta of
        /// zero. This covers both arms at once, freestyle slots and a subtimed run.
        ///
        /// <para>Run under BOTH values of backlog 247's first-char hybrid, which pins the
        /// PRECEDENCE in the generator: cell 7 opens its group AND is a stretch cell, and the
        /// stretch arm wins, so its press stays its own 8200 target rather than moving to the 9000
        /// span start the hybrid would otherwise press. The two other group-first cells (the
        /// opening freestyle marker and the 'a' at cell 5) are already timed exactly on their
        /// spans' starts, so this fixture's frames are byte-identical across the eras.</para>
        /// </summary>
        [Test]
        public void AutoplayIsAllGreatOverAFreestyleAndAStretch([Values] bool firstChar)
        {
            var lyric = autoplayLine();
            var beatmap = scored(lyric);

            var frames = new TypeBeatAutoGenerator(beatmap, syllableTiming: true, charTimedStretch: true, firstCharTiming: firstChar)
                .Generate().Frames.Cast<TypeBeatReplayFrame>().ToList();

            Assert.AreEqual(10, frames.Count);

            // The clamp is skipped for the stretch cell, so its press is its own target and not the
            // 9000 span edge (nor, under the hybrid, the 9000 span START: the stretch arm keeps
            // precedence over the first-char arm). That is the whole generator change, stated as
            // one number.
            Assert.AreEqual(8200, frames[7].Time, 1e-9);

            var engine = new TypingEngine(map(lyric)) { SyllableTiming = true, CharTimedStretch = true, FirstCharTiming = firstChar };
            engine.Update(1000);

            foreach (var frame in frames)
            {
                engine.Update(frame.Time);
                Assert.IsTrue(engine.ProcessKey(frame.Character, frame.Time), $"'{frame.Character}' @ {frame.Time}");
            }

            engine.Update(70000);

            var results = engine.BuildResults();
            Assert.AreEqual(frames.Count, results.Counts[JudgementType.Great], "every autoplay press judges Great");
            Assert.AreEqual(0, results.Counts[JudgementType.Ok]);
            Assert.AreEqual(0, results.Counts[JudgementType.Meh]);
            Assert.AreEqual(0, results.Counts[JudgementType.Premature]);
            Assert.AreEqual(0, results.Counts[JudgementType.Lagging]);

            foreach (var cell in engine.Lines[0].Cells)
                Assert.AreEqual(0, cell.JudgedDelta!.Value, 1.0, $"cell '{cell.Expected}' @ {cell.TargetTime}");
        }

        #endregion
    }
}
