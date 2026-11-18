// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 494: a held letter is due at its hold's start (TypingEngine.HoldLeadIn, bit 15 of the second
// CONFIG word). The fixture is the real case, "Blockbuster," from NOMA - Brain Power (SamuelisHere)
// [Insane]: syllables Block|bus|ter, (split_chars [5, 8]), the "ter," syllable sung over
// [64351.98, 65234.34] and a hold on the 'r' (char 10) over [64403.10, 65057.87]. The even spread puts
// t, e, r at 64351.98, 64646.10 and 64940.22, so the hold opens 51 ms after the 't', before the 'e' is
// even due. Under the era the 'e' is re-spread to half way (64377.54) and the 'r' is due at 64403.10.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class HoldLeadInTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        #region Fixture

        private const double word_start = 63646.10083794706;
        private const double bus_start = 63999.042014417646;
        private const double ter_start = 64351.98319088823;
        private const double word_end = 65234.33613206471;
        private const double next_line = 66822.57142618235;
        private const double hold_start = 64403.10083794706;
        private const double hold_end = 65057.86554382941;

        /// <summary>The cells of "blockbuster" (the default stream drops the comma): t, e and r are 8, 9 and 10.</summary>
        private const int t = 8, e = 9, r = 10;

        private static TimedUnit blockbuster(params WordHold[] holds) => new TimedUnit
        {
            Text = "Blockbuster,",
            StartTime = word_start,
            EndTime = word_end,
            Source = TimingSource.Explicit,
            SyllableBoundaries = new[] { bus_start, ter_start },
            SyllableSplits = new[] { 5, 8 },
            Objects = holds.Length == 0 ? WordObjects.None : new WordObjects { Holds = holds },
        };

        private static readonly WordHold the_r = new WordHold(10, hold_start, hold_end);

        private static LyricLine line(TimedUnit unit, double end = next_line)
            => new LyricLine { RawText = unit.Text, StartTime = unit.StartTime, EndTime = end, SingEndTime = unit.EndTime, Units = new[] { unit } };

        private static LyricBeatmap map(LyricLine lyric) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "NOMA", Title = "Brain Power", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = new[] { lyric },
            Granularity = TimingGranularity.Word,
        };

        private static TypeBeatBeatmap beatmapOf(LyricLine lyric)
        {
            var beatmap = new TypeBeatBeatmap();
            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = lyric.StartTime, LineIndex = 0, Line = lyric, Granularity = TimingGranularity.Word });

            foreach (var hitObject in beatmap.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return beatmap;
        }

        private static double[] targetsOf(TypingLine typing) => typing.Cells.Select(c => c.TargetTime).ToArray();

        /// <summary>The even spread of "ter," over its syllable: what every stored replay's 't', 'e' and 'r' were due at.</summary>
        private static double evenSpread(int letter) => ter_start + (letter - t) * (word_end - ter_start) / 3;

        #endregion

        #region Targets

        [Test]
        public void WithoutTheEraTheHeldLetterKeepsItsEvenSpreadTarget()
        {
            var cells = TypingLine.FromLyricLine(line(blockbuster(the_r)), alignSubdivisionTargets: true).Cells;

            Assert.That(cells.Select(c => c.Expected), Is.EqualTo("blockbuster"));
            Assert.That(cells[r].HoldStart, Is.EqualTo(hold_start));

            Assert.Multiple(() =>
            {
                Assert.That(cells[t].TargetTime, Is.EqualTo(evenSpread(t)).Within(1e-6));
                Assert.That(cells[e].TargetTime, Is.EqualTo(evenSpread(e)).Within(1e-6));
                Assert.That(cells[r].TargetTime, Is.EqualTo(evenSpread(r)).Within(1e-6), "the reported 64940, 537 ms after the hold opens");
            });
        }

        [Test]
        public void UnderTheEraTheHeldLetterIsDueAtItsHoldsStartAndTheLetterBeforeItMeetsIt()
        {
            var off = TypingLine.FromLyricLine(line(blockbuster(the_r)), alignSubdivisionTargets: true);
            var on = TypingLine.FromLyricLine(line(blockbuster(the_r)), alignSubdivisionTargets: true, holdLeadIn: true);

            Assert.Multiple(() =>
            {
                Assert.That(on.Cells[t].TargetTime, Is.EqualTo(ter_start), "the lead-in opens on the segment's first target");
                Assert.That(on.Cells[e].TargetTime, Is.EqualTo(ter_start + (hold_start - ter_start) / 2), "re-spread half way to the hold");
                Assert.That(on.Cells[r].TargetTime, Is.EqualTo(hold_start), "the held letter, at the hold's start");

                Assert.That(targetsOf(on).Take(t), Is.EqualTo(targetsOf(off).Take(t)), "Block and bus do not move");
                Assert.That(on.SealGraceMs, Is.EqualTo(off.SealGraceMs), "nor does the seal grace");
                Assert.That(on.AuthoredGrouping.Groups.Select(g => (g.StartCell, g.EndCellExclusive, g.StartTime, g.EndTime)),
                    Is.EqualTo(off.AuthoredGrouping.Groups.Select(g => (g.StartCell, g.EndCellExclusive, g.StartTime, g.EndTime))), "nor any group");
                Assert.That(on.Cells.Select(c => (c.HoldStart, c.HoldEnd, c.HandsOffFrom)), Is.EqualTo(off.Cells.Select(c => (c.HoldStart, c.HoldEnd, c.HandsOffFrom))));
            });
        }

        [Test]
        public void TheEngineSwitchesTheEraBothWaysInEitherOrderWithTheSubdivisionEra()
        {
            var lyric = line(blockbuster(the_r));
            double[] legacy = targetsOf(TypingLine.FromLyricLine(lyric));
            double[] legacyLeadIn = targetsOf(TypingLine.FromLyricLine(lyric, holdLeadIn: true));
            double[] aligned = targetsOf(TypingLine.FromLyricLine(lyric, alignSubdivisionTargets: true));
            double[] alignedLeadIn = targetsOf(TypingLine.FromLyricLine(lyric, alignSubdivisionTargets: true, holdLeadIn: true));

            var leadInFirst = new TypingEngine(map(lyric));
            var alignedFirst = new TypingEngine(map(lyric));

            Assert.Multiple(() =>
            {
                Assert.That(targetsOf(leadInFirst.Lines[0]), Is.EqualTo(legacy), "a bare engine is on the stored era");

                leadInFirst.HoldLeadIn = true;
                Assert.That(targetsOf(leadInFirst.Lines[0]), Is.EqualTo(legacyLeadIn));
                leadInFirst.AlignSubdivisionTargets = true;
                Assert.That(targetsOf(leadInFirst.Lines[0]), Is.EqualTo(alignedLeadIn));

                alignedFirst.AlignSubdivisionTargets = true;
                Assert.That(targetsOf(alignedFirst.Lines[0]), Is.EqualTo(aligned));
                alignedFirst.HoldLeadIn = true;
                Assert.That(targetsOf(alignedFirst.Lines[0]), Is.EqualTo(alignedLeadIn));

                alignedFirst.HoldLeadIn = false;
                Assert.That(targetsOf(alignedFirst.Lines[0]), Is.EqualTo(aligned), "and back");
                alignedFirst.AlignSubdivisionTargets = false;
                Assert.That(targetsOf(alignedFirst.Lines[0]), Is.EqualTo(legacy));

                Assert.That(alignedLeadIn[r], Is.EqualTo(hold_start));
                Assert.That(legacyLeadIn[r], Is.EqualTo(hold_start), "the authored split is valid, so the stored subdivision era cuts the word the same way");
            });
        }

        [Test]
        public void ALineWithNoHoldIsByteIdenticalUnderTheEra()
        {
            var plain = line(new TimedUnit
            {
                Text = "brain", StartTime = 66822.57142618235, EndTime = 67175.51260265293, Source = TimingSource.Explicit,
            }, end: 70000);
            var held = line(blockbuster(the_r));
            var twoWords = new LyricLine
            {
                RawText = "Blockbuster, brain",
                StartTime = word_start,
                EndTime = 70000,
                SingEndTime = 66000,
                Units = new[] { blockbuster(the_r), new TimedUnit { Text = "brain", StartTime = 65300, EndTime = 66000, Source = TimingSource.Explicit } },
            };

            Assert.Multiple(() =>
            {
                foreach (bool aligned in new[] { false, true })
                {
                    Assert.That(targetsOf(TypingLine.FromLyricLine(plain, alignSubdivisionTargets: aligned, holdLeadIn: true)),
                        Is.EqualTo(targetsOf(TypingLine.FromLyricLine(plain, alignSubdivisionTargets: aligned))), "a line with no hold");
                    Assert.That(targetsOf(TypingLine.FromLyricLine(line(blockbuster()), alignSubdivisionTargets: aligned, holdLeadIn: true)),
                        Is.EqualTo(targetsOf(TypingLine.FromLyricLine(line(blockbuster()), alignSubdivisionTargets: aligned))), "the same word with its hold taken off");

                    // The word after the held one, and the space between them, keep their targets.
                    var on = targetsOf(TypingLine.FromLyricLine(twoWords, alignSubdivisionTargets: aligned, holdLeadIn: true));
                    var off = targetsOf(TypingLine.FromLyricLine(twoWords, alignSubdivisionTargets: aligned));
                    Assert.That(on.Skip(r + 1), Is.EqualTo(off.Skip(r + 1)), "the rest of a held line");
                }

                Assert.That(targetsOf(TypingLine.FromLyricLine(held, literate: true, alignSubdivisionTargets: true, holdLeadIn: true)).Take(r + 1),
                    Is.EqualTo(targetsOf(TypingLine.FromLyricLine(held, alignSubdivisionTargets: true, holdLeadIn: true))), "Literate moves no letter");
            });
        }

        #endregion

        #region Edges

        /// <summary>A hold on the first letter of its segment ('b' of "bus"): that letter is simply due at the hold's start.</summary>
        [Test]
        public void AHeldLetterThatOpensItsSegmentIsDueAtTheHoldsStart()
        {
            var hold = new WordHold(5, bus_start + 20, bus_start + 200);
            var off = TypingLine.FromLyricLine(line(blockbuster(hold)), alignSubdivisionTargets: true);
            var on = TypingLine.FromLyricLine(line(blockbuster(hold)), alignSubdivisionTargets: true, holdLeadIn: true);

            Assert.That(HeldWord.UsableHolds(blockbuster(hold)), Is.EqualTo(new[] { hold }));

            Assert.Multiple(() =>
            {
                Assert.That(off.Cells[5].TargetTime, Is.EqualTo(bus_start));
                Assert.That(on.Cells[5].TargetTime, Is.EqualTo(bus_start + 20));
                Assert.That(targetsOf(on).Where((_, i) => i != 5), Is.EqualTo(targetsOf(off).Where((_, i) => i != 5)), "nothing else moves");
                Assert.That(on.Cells[6].TargetTime, Is.EqualTo(bus_start + 200), "the 'u' is still handed off at the hold's end");
            });
        }

        /// <summary>
        /// A first-letter hold that starts before the letter ahead of it is due (here before Block's 'k'):
        /// the editor should not offer it, and the line's non-decreasing guard holds the 'b' level with the 'k'.
        /// </summary>
        [Test]
        public void AHoldStartingBeforeTheLetterAheadOfItIsClampedToThatLetter()
        {
            var hold = new WordHold(5, bus_start - 100, bus_start + 200);
            var on = TypingLine.FromLyricLine(line(blockbuster(hold)), alignSubdivisionTargets: true, holdLeadIn: true);

            Assert.That(HeldWord.UsableHolds(blockbuster(hold)), Is.EqualTo(new[] { hold }));
            Assert.That(on.Cells[4].TargetTime, Is.GreaterThan(bus_start - 100), "the 'k' is due after the hold starts");
            Assert.That(on.Cells[5].TargetTime, Is.EqualTo(on.Cells[4].TargetTime), "so the 'b' is due with it, never before it");
        }

        /// <summary>
        /// Two holds in one segment: the second hold's lead-in starts at the letter the first one hands off to,
        /// no earlier than the first hold's end, so the hand-off never undoes the spread.
        /// </summary>
        [Test]
        public void TwoHoldsInOneSegmentEachLeadInFromTheLastOnesHandOff()
        {
            // "hello" over [1000, 2000], one segment: h, e, l, l, o at 1000, 1200, 1400, 1600, 1800.
            var first = new WordHold(1, 1100, 1450);
            var second = new WordHold(3, 1500, 1700);
            var hello = new TimedUnit
            {
                Text = "hello", StartTime = 1000, EndTime = 2000, Source = TimingSource.Explicit,
                Objects = new WordObjects { Holds = new[] { first, second } },
            };
            var lyric = new LyricLine { RawText = "hello", StartTime = 1000, EndTime = 5000, SingEndTime = 2000, Units = new[] { hello } };

            Assert.That(HeldWord.UsableHolds(hello), Is.EqualTo(new[] { first, second }));

            Assert.Multiple(() =>
            {
                Assert.That(targetsOf(TypingLine.FromLyricLine(lyric, alignSubdivisionTargets: true)), Is.EqualTo(new double[] { 1000, 1200, 1450, 1600, 1800 }).Within(1e-9),
                    "without the era: the first 'l' is handed off at the first hold's end, nothing else moves");
                Assert.That(targetsOf(TypingLine.FromLyricLine(lyric, alignSubdivisionTargets: true, holdLeadIn: true)), Is.EqualTo(new double[] { 1000, 1100, 1450, 1500, 1800 }).Within(1e-9),
                    "with it: each held letter at its hold's start, the second's lead-in from the first's end");
            });
        }

        /// <summary>
        /// A paused word (backlog 448's "please", a rest over [2000, 2500] cut before the 'a'): the lead-in of a
        /// hold before the rest is its own stretch, and a hold opening the stretch after it is due at its start.
        /// </summary>
        [Test]
        public void APausedWordLeadsInWithinTheStretchItsHoldIsIn()
        {
            var please = new TimedUnit
            {
                Text = "please", StartTime = 1000, EndTime = 4000, Source = TimingSource.Explicit,
                Pauses = new[] { new WordPause(2000, 2500, 3) },
                Objects = new WordObjects { Holds = new[] { new WordHold(2, 1700, 2000), new WordHold(3, 2500, 2900) } },
            };
            var lyric = new LyricLine { RawText = "please", StartTime = 1000, EndTime = 60000, SingEndTime = 4000, Units = new[] { please } };

            Assert.That(targetsOf(TypingLine.FromLyricLine(lyric, alignSubdivisionTargets: true)), Is.EqualTo(new double[] { 1000, 1000 + 1000.0 / 3, 1000 + 2000.0 / 3, 2500, 3000, 3500 }).Within(1e-9));
            Assert.That(targetsOf(TypingLine.FromLyricLine(lyric, alignSubdivisionTargets: true, holdLeadIn: true)), Is.EqualTo(new double[] { 1000, 1350, 1700, 2500, 3000, 3500 }).Within(1e-9));
        }

        /// <summary>Polyglot plays no hold, so the era has nothing to move there.</summary>
        [Test]
        public void PolyglotIgnoresTheEra()
        {
            var lyric = line(blockbuster(the_r));
            var on = TypingLine.ForMods(lyric, false, true, "en", alignSubdivisionTargets: true, holdLeadIn: true);
            var off = TypingLine.ForMods(lyric, false, true, "en", alignSubdivisionTargets: true);

            Assert.That(on.Cells.All(c => c.HoldStart == null), Is.True);
            Assert.That(targetsOf(on), Is.EqualTo(targetsOf(off)));
        }

        #endregion

        #region The era

        /// <summary>
        /// The wire: bit 15 of the SECOND word, value 32768, mistaken for no other bit, and absent from every
        /// replay recorded before it (a CONFIG frame with no extended frame after it clears it even on an engine
        /// the live factory set it on, the watch path, and puts the even spread back on the cells).
        /// </summary>
        [Test]
        public void TheEraIsBitFifteenOfTheSecondWord()
        {
            var lyric = line(blockbuster(the_r));
            var beatmap = OriginalTextFormatTest.BuildBeatmap(new[] { lyric });
            var legacy = TypeBeatReplayFrame.CreateExtendedConfigFrame(0, holdLeadIn: true).ToLegacy(beatmap);

            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(legacy, beatmap);

            var engine = new TypingEngine(map(lyric)) { AlignSubdivisionTargets = true, HoldLeadIn = true };
            Assert.That(engine.Lines[0].Cells[r].TargetTime, Is.EqualTo(hold_start));

            ReplayEngineFeed.Apply(engine, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true));

            Assert.Multiple(() =>
            {
                Assert.That(legacy.MouseY, Is.EqualTo(32768f));
                Assert.That(decoded.IsConfigExtended && decoded.HoldLeadIn, Is.True);
                Assert.That(decoded.StacksDisplacedClaims || decoded.BestRetypeJudgement || decoded.AlignSubdivisionTargets, Is.False, "it is mistaken for no other bit");
                Assert.That(engine.HoldLeadIn, Is.False, "a CONFIG frame alone is a replay recorded before the era");
                Assert.That(engine.Lines[0].Cells[r].TargetTime, Is.EqualTo(evenSpread(r)).Within(1e-6), "which re-derives on the even spread");
            });

            ReplayEngineFeed.Apply(engine, decoded);
            Assert.That(engine.HoldLeadIn, Is.True);
            Assert.That(engine.Lines[0].Cells[r].TargetTime, Is.EqualTo(hold_start));
        }

        /// <summary>
        /// What the era decides on a judgement: under Hard Rock's point targets (SyllableTiming off) a press of
        /// the 'r' at the hold's start is on time under the era and 537 ms early without it.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void UnderPointTargetsTheEraDecidesTheHeldLettersJudgement(bool leadIn)
        {
            var lyric = line(blockbuster(the_r));
            var engine = new TypingEngine(map(lyric)) { AllowWrongInput = true };
            ReplayEngineFeed.Apply(engine, TypeBeatReplayFrame.CreateExtendedConfigFrame(0, authoredSyllablesOnly: true, alignSubdivisionTargets: true, holdLeadIn: leadIn));

            // Every letter pressed on the live era's cue (none before the line opens), the 'r' at the hold's start.
            double[] cues = targetsOf(TypingLine.FromLyricLine(lyric, alignSubdivisionTargets: true, holdLeadIn: true)).Select(v => System.Math.Max(System.Math.Round(v), System.Math.Ceiling(word_start))).ToArray();
            var cells = engine.Lines[0].Cells;
            const string word = "blockbuster";

            for (int i = 0; i < word.Length; i++)
            {
                engine.Update(cues[i]);
                Assert.That(engine.ProcessKey(word[i], cues[i]), Is.True, $"'{word[i]}' @ {cues[i]}");
            }

            Assert.That(cues[r], Is.EqualTo(64403));

            if (leadIn)
            {
                Assert.That(cells[r].JudgedDelta, Is.EqualTo(0).Within(1.0));
                Assert.That(cells[r].JudgedTier, Is.EqualTo(JudgementType.Great));
                Assert.That(cells[e].JudgedTier, Is.EqualTo(JudgementType.Great));
            }
            else
            {
                Assert.That(cells[r].TargetTime - cues[r], Is.GreaterThan(500), "537 ms early on the even spread");
                Assert.That(cells[r].JudgedTier, Is.Not.EqualTo(JudgementType.Great));
                Assert.That(cells[e].JudgedTier, Is.Not.EqualTo(JudgementType.Great), "and the 'e' 268 ms early");
            }
        }

        #endregion

        #region Autoplay

        /// <summary>
        /// THE REPORT: the live autoplay pressed the 'e' and the 'r' together at the 'e''s even-spread
        /// target, opening the hold 243 ms late. It presses the 'r' at the hold's start.
        /// </summary>
        [Test]
        public void LiveAutoplayPressesTheHeldLetterAtItsHoldsStart()
        {
            var frames = new TypeBeatModAutoplay().CreateReplayData(beatmapOf(line(blockbuster(the_r))), new Mod[0]).Replay.Frames
                                                  .Cast<TypeBeatReplayFrame>().Where(f => f.Character >= ' ').ToList();

            Assert.That(frames.Select(f => f.Character), Is.EqualTo("blockbuster"));

            Assert.Multiple(() =>
            {
                Assert.That(frames[t].Time, Is.EqualTo(64352), "t");
                Assert.That(frames[e].Time, Is.EqualTo(64378), "e, half way to the hold");
                Assert.That(frames[r].Time, Is.EqualTo(64403), "r, the hold's start");
            });
        }

        /// <summary>The stored era's autoplay is the reported one: the 'e' and the 'r' pressed together at 64646.</summary>
        [Test]
        public void WithoutTheEraAutoplayPressesTheEvenSpread()
        {
            var frames = new TypeBeatAutoGenerator(beatmapOf(line(blockbuster(the_r))), syllableTiming: true, charTimedStretch: true, firstCharTiming: true, authoredSyllablesOnly: true,
                             alignSubdivisionTargets: true, earlyFinish: true)
                         .Generate().Frames.Cast<TypeBeatReplayFrame>().ToList();
            var presses = frames.Where(f => f.Character >= ' ').ToList();

            Assert.That(presses[e].Time, Is.EqualTo(64646));
            Assert.That(presses[r].Time, Is.EqualTo(64646));
            Assert.That(frames.Where(f => f.IsConfigExtended).All(f => !f.HoldLeadIn), Is.True, "and carries no era it was not pressed for");
        }

        /// <summary>
        /// The live autoplay played back on the live engine, through its own headers: every press on time, the
        /// hold opened at its start (so its track starts empty), and its tail Great.
        /// </summary>
        [Test]
        public void LiveAutoplayOpensTheHoldOnTimeAndIsPerfect()
        {
            var lyric = line(blockbuster(the_r));
            var frames = new TypeBeatModAutoplay().CreateReplayData(beatmapOf(lyric), new Mod[0]).Replay.Frames.Cast<TypeBeatReplayFrame>().ToList();

            Assert.That(frames.Where(f => f.IsConfigExtended).Select(f => f.HoldLeadIn), Is.All.True, "the header carries the era");

            var engine = new TypingEngine(map(lyric)) { SyllableTiming = true, CharTimedStretch = true, FirstCharTiming = true };
            var tails = new List<HoldTailJudgement>();
            engine.HoldTailJudged += tails.Add;
            double? opened = null;

            foreach (var frame in frames)
            {
                ReplayEngineFeed.Apply(engine, frame);

                if (opened == null && engine.ActiveHold is { } hold)
                    opened = hold.PressTime;
            }

            engine.Update(next_line + 1000);
            var results = engine.BuildResults();

            Assert.Multiple(() =>
            {
                Assert.That(engine.HoldLeadIn, Is.True);
                Assert.That(opened, Is.EqualTo(64403), "the hold opens at its start, 51 ms after the 't'");
                Assert.That(tails.Select(h => h.Type), Is.EqualTo(new[] { JudgementType.Great }));
                Assert.That(results.Counts[JudgementType.Great], Is.EqualTo(12), "eleven letters and the tail");

                foreach (var cell in engine.Lines[0].Cells)
                    Assert.That(cell.JudgedDelta, Is.EqualTo(0).Within(1.0), $"'{cell.Expected}' @ {cell.TargetTime}");
            });
        }

        #endregion
    }
}
