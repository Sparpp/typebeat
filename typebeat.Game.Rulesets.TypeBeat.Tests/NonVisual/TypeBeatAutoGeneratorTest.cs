// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The autoplay generator must emit exactly one frame per typeable cell, in order, at (rounded)
    /// target times, and driving a <see cref="TypingEngine"/> with those frames (via the same
    /// Update-then-key call sequence playback uses) must produce a perfect play: every typeable
    /// cell Correct, no misses, 100% accuracy.
    /// </summary>
    [TestFixture]
    public class TypeBeatAutoGeneratorTest
    {
        /// <summary>
        /// Drives an engine with a generated replay exactly the way the playfield's engine ticker
        /// does (Update to the frame's timestamp, then the keystroke), asserting every single press
        /// is ACCEPTED. A press the engine answers with false is not a rejection, it is a press that
        /// never happened (no active line, or a line already complete), and it silently shifts every
        /// later press on that line onto the wrong cell.
        /// </summary>
        private static TypingEngine playPerfectly(TypeBeatBeatmap map, bool syllableTiming = false)
        {
            var replay = new TypeBeatAutoGenerator(map, syllableTiming: syllableTiming).Generate();
            var engine = new TypingEngine(lyricBeatmap(map)) { SyllableTiming = syllableTiming };

            foreach (var frame in replay.Frames.Cast<TypeBeatReplayFrame>())
            {
                engine.Update(frame.Time);
                Assert.IsTrue(engine.ProcessKey(frame.Character, frame.Time),
                    $"'{frame.Character}' @ {frame.Time} must reach a live cell (line {engine.ActiveLineIndex}, caret {engine.CaretIndex})");
                Assert.AreEqual(0, engine.ConsecutiveWrongKeys, $"'{frame.Character}' @ {frame.Time} must not be rejected");
            }

            engine.Update(1_000_000);

            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(1.0, engine.LiveAccuracy, "no wrong keys");

            foreach (var l in engine.Lines)
            {
                foreach (var cell in l.Cells)
                {
                    if (cell.IsTypeable)
                        Assert.AreEqual(CellState.Correct, cell.State, $"cell '{cell.Expected}' @ {cell.TargetTime} in \"{l.DisplayText}\"");
                }
            }

            return engine;
        }

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static TimedUnit unit(string text, double start, double end, params double[] syllables)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end, SyllableBoundaries = syllables };

        private static TypeBeatBeatmap beatmap(params LyricLine[] lines)
        {
            var map = new TypeBeatBeatmap();

            for (int i = 0; i < lines.Length; i++)
            {
                map.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
                    Granularity = TimingGranularity.Word,
                });
            }

            return map;
        }

        private static LyricBeatmap lyricBeatmap(TypeBeatBeatmap map) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "T", Title = "S", FolderPath = @"X:\n", AudioFileName = "a.mp3" },
            Lines = map.HitObjects.Select(h => h.Line).ToList(),
            Granularity = TimingGranularity.Word,
        };

        private static TypeBeatBeatmap createTwoLineMap() => beatmap(
            line("ab cd!", 0, 4000, 3500, unit("ab", 500, 1500), unit("cd!", 2000, 3500)),
            line("Ef 9", 4000, 8000, 7500, unit("Ef", 4500, 6000), unit("9", 6500, 7500)));

        [Test]
        public void GeneratesOneFramePerTypeableCellAtTargetTimes()
        {
            var map = createTwoLineMap();
            var replay = new TypeBeatAutoGenerator(map).Generate();

            var frames = replay.Frames.Cast<TypeBeatReplayFrame>().ToList();

            // Without Literate the perfect play types the DEFAULT stream: lower-cased, and the '!'
            // is not a cell at all, so there is no frame for it.
            Assert.AreEqual("ab cd" + "ef 9", string.Concat(frames.Select(f => f.Character)));

            var engineLines = map.HitObjects.Select(h => TypingLine.FromLyricLine(h.Line)).ToList();

            double[] expectedTargets = engineLines
                                       .SelectMany(l => l.Cells)
                                       .Where(c => c.IsTypeable)
                                       .Select(c => c.TargetTime)
                                       .ToArray();

            Assert.AreEqual(expectedTargets.Length, frames.Count);

            for (int i = 0; i < frames.Count; i++)
            {
                Assert.AreEqual(System.Math.Round(expectedTargets[i]), frames[i].Time, $"frame {i} time");
                Assert.AreEqual(frames[i].Time, System.Math.Round(frames[i].Time), $"frame {i} must have an integral time");
            }

            for (int i = 1; i < frames.Count; i++)
                Assert.LessOrEqual(frames[i - 1].Time, frames[i].Time, "frame times must be monotonic");
        }

        [Test]
        public void GeneratedReplayPlaysPerfectly()
        {
            var map = createTwoLineMap();
            var engine = playPerfectly(map);

            foreach (var l in engine.Lines)
            {
                foreach (var cell in l.Cells)
                {
                    if (cell.IsTypeable)
                        Assert.AreEqual(0, cell.JudgedDelta!.Value, 1.0, "pressed at target (within rounding)");
                }
            }
        }

        /// <summary>
        /// Backlog 51 regression, distilled from "Busta Rhymes Goes To The Wii Shop Channel".
        ///
        /// <para>Real maps carry FRACTIONAL times, and the decoder makes line windows contiguous
        /// (a line's EndTime IS the next line's StartTime), so the next line's first cell target is
        /// routinely that exact fractional boundary. Rounding the frame to integral milliseconds
        /// used to round it DOWN, landing the press a fraction of a millisecond BEFORE the previous
        /// line's seal deadline: the previous line was still the active one, it was already fully
        /// typed, and a complete line makes ProcessKey inert. The press vanished, and every later
        /// press on the new line landed one cell early.</para>
        ///
        /// <para>An all-freestyle line hides that drift completely (every key matches a freestyle
        /// cell) until the first SPACE cell, at which point autoplay's space lands on a freestyle
        /// cell (which rejects space, backlog 50) and its letters land on the space cell, rejected
        /// again and again: 13 consecutive rejections fail the play outright.</para>
        /// </summary>
        [Test]
        public void FractionalLineBoundaryDoesNotSwallowTheFirstPress()
        {
            // The boundary the previous line seals on, and the next line's first cell target.
            const double boundary = 1000.3856;

            var map = beatmap(
                line("ab", 0, boundary, 900, unit("ab", 100, 900)),
                line("&&& &&&", boundary, 4000, 3000,
                    unit("&&&", boundary, 2000),
                    unit("&&&", 2000, 3000)));

            var frames = new TypeBeatAutoGenerator(map).Generate().Frames.Cast<TypeBeatReplayFrame>().ToList();

            // The first press of the second line must be at or after the boundary, never rounded
            // back across it (1001, not 1000).
            var firstOnSecondLine = frames.First(f => f.Time >= 1000);
            Assert.GreaterOrEqual(firstOnSecondLine.Time, boundary, "a press must never precede its line's activation");

            playPerfectly(map);
        }

        /// <summary>
        /// The same swallow with the drift left visible: a fractional boundary in front of an
        /// ordinary lyric line. Pre-fix the first press was eaten and every later press landed on
        /// the previous cell, so the line's last cell was never typed and sealed as a miss.
        /// </summary>
        [Test]
        public void FractionalLineBoundaryKeepsOrdinaryLinesInStep()
        {
            const double boundary = 2000.25;

            var map = beatmap(
                line("hi", 0, boundary, 1800, unit("hi", 100, 1800)),
                line("cd ef", boundary, 6000, 5000,
                    unit("cd", boundary, 3500),
                    unit("ef", 3500, 5000)));

            playPerfectly(map);
        }

        #region Judgement era (backlog 181)

        /// <summary>
        /// Two mapper-subtimed words whose syllabifier split disagrees with the even-by-index
        /// target spread hard enough to throw a cell clean out of its own syllable's span in each
        /// direction (see <see cref="SyllableTimingTest"/> for the judgement arithmetic; this
        /// fixture is used here only for the FRAME times, which are granularity-independent).
        /// Cell 4 ('t' of "aviation") is timed 400 ms before its span opens and cell 16 (the second
        /// 'e' of "seventeen") 555.56 ms after its span closes.
        /// </summary>
        private static TypeBeatBeatmap createSubtimedMap() => beatmap(
            line("aviation seventeen", 1000, 20000, 11000,
                unit("aviation", 1000, 4000, 1800, 2600),
                unit("seventeen", 4000, 11000, 4500, 5000, 6000)));

        /// <summary>
        /// A Literate fixture whose marks sit outside their spans under aligned targets: the '(' after
        /// a gap before the word's start, and the apostrophe an authored split puts at the head of
        /// the second syllable before that syllable's start (see
        /// <see cref="AutoplayModMirrorsTheLiveEraCondition"/>).
        /// </summary>
        private static TypeBeatBeatmap createLiterateMarkedMap() => beatmap(
            line("go (o'clock", 0, 6000, 5000,
                unit("go", 500, 900),
                new TimedUnit { Text = "(o'clock", StartTime = 1000, EndTime = 5000, SyllableBoundaries = new[] { 2000.0 }, SyllableSplits = new[] { 2 } }));

        private static double[] frameTimes(Replay replay)
            => replay.Frames.Cast<TypeBeatReplayFrame>().Select(f => f.Time).ToArray();

        /// <summary>Rounded point targets: the frames every era before backlog 181 emitted.</summary>
        private static double[] targetPresses(TypeBeatBeatmap map)
            => map.HitObjects.OfType<TypeBeatHitObject>()
                  .OrderBy(h => h.LineIndex)
                  .SelectMany(h => TypingLine.FromLyricLine(h.Line).Cells)
                  .Where(c => c.IsTypeable)
                  .Select(c => Math.Round(c.TargetTime))
                  .ToArray();

        /// <summary>
        /// The era flag defaults to the CLASSIC rule, so a bare construction is byte-identical to
        /// the pre-backlog-181 generator: every press on its cell's own point target, subtimings or
        /// no subtimings. That is what a Hard Rock play needs (backlog 180 reverted HR to point
        /// targets), and what every caller that never heard of the flag keeps getting.
        /// </summary>
        [Test]
        public void ClassicEraPressesTheTargetsAndIsTheDefault()
        {
            var map = createSubtimedMap();

            double[] targets = targetPresses(map);

            Assert.That(frameTimes(new TypeBeatAutoGenerator(map).Generate()), Is.EqualTo(targets), "bare construction");
            Assert.That(frameTimes(new TypeBeatAutoGenerator(map, syllableTiming: false).Generate()), Is.EqualTo(targets), "era off");

            // Non-vacuity: the span era really does move presses on this fixture, so the equality
            // above is a claim about the flag and not about the map.
            Assert.That(frameTimes(new TypeBeatAutoGenerator(map, syllableTiming: true).Generate()), Is.Not.EqualTo(targets), "era on");

            // Classic generator judged by a classic engine: still a perfect play, every delta zero
            // to within the integral rounding of a fractional target.
            var engine = playPerfectly(map);

            foreach (var cell in engine.Lines.SelectMany(l => l.Cells).Where(c => c.IsTypeable))
                Assert.AreEqual(0, cell.JudgedDelta!.Value, 0.5, $"cell '{cell.Expected}' @ {cell.TargetTime}");
        }

        /// <summary>
        /// Under the span era every GROUPED cell is pressed at its target clamped into its
        /// syllable's span, and every UNGROUPED cell (the inter-word space here) keeps its point
        /// target, because that is what each of them is still judged against.
        /// </summary>
        [Test]
        public void SyllableEraClampsEachGroupedPressIntoItsSpan()
        {
            var map = createSubtimedMap();
            var typingLine = TypingLine.FromLyricLine(map.HitObjects.OfType<TypeBeatHitObject>().Single().Line);

            var expected = new List<double>();

            for (int i = 0; i < typingLine.Cells.Count; i++)
            {
                var cell = typingLine.Cells[i];

                if (!cell.IsTypeable)
                    continue;

                int syllable = typingLine.SyllableIndexOf(i);

                expected.Add(syllable < 0
                    ? Math.Round(cell.TargetTime)
                    : Math.Round(Math.Clamp(cell.TargetTime, typingLine.Syllables[syllable].StartTime, typingLine.Syllables[syllable].EndTime)));
            }

            Assert.That(frameTimes(new TypeBeatAutoGenerator(map, syllableTiming: true).Generate()), Is.EqualTo(expected.ToArray()));

            // Span generator judged by a span engine: perfect, and every delta EXACTLY zero rather
            // than merely inside a window.
            var engine = playPerfectly(map, syllableTiming: true);

            foreach (var cell in engine.Lines.SelectMany(l => l.Cells).Where(c => c.IsTypeable))
                Assert.AreEqual(0, cell.JudgedDelta!.Value, 1e-9, $"cell '{cell.Expected}' @ {cell.TargetTime}");
        }

        /// <summary>
        /// The mod is the only production caller, and the era it asks for must be the era
        /// <c>DrawableTypeBeatRuleset.createEngine</c> will judge in: span for every mod stack,
        /// point targets under Hard Rock alone. Literate stays carried through beside it.
        /// </summary>
        [Test]
        public void AutoplayModMirrorsTheLiveEraCondition()
        {
            var map = createSubtimedMap();

            // The live span stack carries both narrowings (backlog 209's stretch cells and backlog
            // 247's first chars), exactly as createEngine stamps them; the classic arm is inert to
            // both, so it needs neither.
            // Backlog 363's grouping rides along for every stack, exactly as createEngine stamps it.
            double[] span = frameTimes(new TypeBeatAutoGenerator(map, syllableTiming: true, charTimedStretch: true, firstCharTiming: true, authoredSyllablesOnly: true, alignSubdivisionTargets: true).Generate());
            double[] classic = frameTimes(new TypeBeatAutoGenerator(map, syllableTiming: false, authoredSyllablesOnly: true, alignSubdivisionTargets: true).Generate());
            double[] literateSpan = frameTimes(new TypeBeatAutoGenerator(map, literate: true, syllableTiming: true, charTimedStretch: true, firstCharTiming: true, authoredSyllablesOnly: true, alignSubdivisionTargets: true).Generate());
            double[] easySpan = frameTimes(new TypeBeatAutoGenerator(map, syllableTiming: true, charTimedStretch: true, firstCharTiming: true, wordShelter: true, authoredSyllablesOnly: true, alignSubdivisionTargets: true).Generate());

            Assert.That(span, Is.EqualTo(classic), "aligned targets already lie in their syllable spans");
            Assert.That(classic, Is.Not.EqualTo(frameTimes(new TypeBeatAutoGenerator(map).Generate())), "the fixture distinguishes the legacy targets");
            Assert.That(easySpan, Is.EqualTo(span), "aligned targets also lie in the word shelter");

            var mod = new TypeBeatModAutoplay();

            Assert.That(frameTimes(mod.CreateReplayData(map, Array.Empty<Mod>()).Replay), Is.EqualTo(span), "no mods");
            Assert.That(frameTimes(mod.CreateReplayData(map, new Mod[] { new TypeBeatModEasy() }).Replay), Is.EqualTo(easySpan), "Easy carries its word shelter through");
            Assert.That(frameTimes(mod.CreateReplayData(map, new Mod[] { new TypeBeatModHardRock() }).Replay), Is.EqualTo(classic), "Hard Rock reverts to point targets");
            Assert.That(frameTimes(mod.CreateReplayData(map, new Mod[] { new TypeBeatModLiterate() }).Replay), Is.EqualTo(literateSpan), "Literate is still carried through");

            // Under aligned targets every LETTER sits inside its own syllable and its own word, so
            // the plain map above cannot tell the three arms apart. A Literate punctuation cell can:
            // it is timed by the second pass, between its neighbours, so the '(' opening a word after
            // a gap sits before the word's start and the apostrophe an authored split opens a
            // syllable with sits before that syllable's start. The syllable arm presses both at
            // their span starts, the shelter arm only the '(' (the apostrophe is inside the word),
            // and the classic arm neither, so all three frame sets differ.
            var marked = createLiterateMarkedMap();
            double[] markedSpan = frameTimes(new TypeBeatAutoGenerator(marked, literate: true, syllableTiming: true, charTimedStretch: true, firstCharTiming: true, authoredSyllablesOnly: true, alignSubdivisionTargets: true).Generate());
            double[] markedClassic = frameTimes(new TypeBeatAutoGenerator(marked, literate: true, syllableTiming: false, authoredSyllablesOnly: true, alignSubdivisionTargets: true).Generate());
            double[] markedEasy = frameTimes(new TypeBeatAutoGenerator(marked, literate: true, syllableTiming: true, charTimedStretch: true, firstCharTiming: true, wordShelter: true, authoredSyllablesOnly: true, alignSubdivisionTargets: true).Generate());

            Assert.That(markedSpan, Is.Not.EqualTo(markedClassic), "the fixture must distinguish the syllable arm under aligned targets");
            Assert.That(markedEasy, Is.Not.EqualTo(markedSpan), "the fixture must distinguish the shelter from the syllable arm");
            Assert.That(markedEasy, Is.Not.EqualTo(markedClassic), "the fixture must distinguish the shelter from the classic arm");

            Assert.That(frameTimes(mod.CreateReplayData(marked, new Mod[] { new TypeBeatModLiterate() }).Replay), Is.EqualTo(markedSpan), "Literate keeps the syllable arm");
            Assert.That(frameTimes(mod.CreateReplayData(marked, new Mod[] { new TypeBeatModLiterate(), new TypeBeatModEasy() }).Replay), Is.EqualTo(markedEasy),
                "Literate with Easy keeps the word shelter");
            Assert.That(frameTimes(mod.CreateReplayData(marked, new Mod[] { new TypeBeatModLiterate(), new TypeBeatModHardRock() }).Replay), Is.EqualTo(markedClassic),
                "Literate with Hard Rock reverts to point targets");

            var frames = mod.CreateReplayData(map, Array.Empty<Mod>()).Replay.Frames.Cast<TypeBeatReplayFrame>().ToList();
            Assert.That(frames[0].IsConfigExtended && frames[0].AuthoredSyllablesOnly && frames[0].AlignSubdivisionTargets && frames[0].EarlyFinish, Is.True, "the era header opens the frames");
            Assert.That(frames[^2].IsConfigExtended && frames[^2].AuthoredSyllablesOnly && frames[^2].AlignSubdivisionTargets && frames[^2].EarlyFinish, Is.True,
                "and sits again ahead of the last press");
            Assert.That(frames.Count(f => f.IsConfigExtended), Is.EqualTo(2));
        }

        /// <summary>
        /// Attaching a replay clears every second-word era on the live engine, so autoplay must carry
        /// <see cref="TypingEngine.EarlyFinish"/> in its own header or the watched run waits for the
        /// final line's end while a live run ends on the last word.
        /// </summary>
        [Test]
        public void AutoplayFinishesOnItsLastPress()
        {
            var map = createTwoLineMap();
            var frames = new TypeBeatModAutoplay().CreateReplayData(map, Array.Empty<Mod>()).Replay.Frames.Cast<TypeBeatReplayFrame>().ToList();
            var engine = new TypingEngine(lyricBeatmap(map));

            ReplayEngineFeed.ClearExtendedEras(engine);

            foreach (var frame in frames)
            {
                engine.Update(frame.Time);
                ReplayEngineFeed.Apply(engine, frame);
            }

            double lastPress = frames[^1].Time;
            Assert.Less(lastPress + 1, 8000, "the fixture's final line ends well after its last press");

            engine.Update(lastPress + 1);
            Assert.IsTrue(engine.IsFinished, "the run ends on autoplay's last press, not at the final line's end");
        }

        #endregion
    }
}
