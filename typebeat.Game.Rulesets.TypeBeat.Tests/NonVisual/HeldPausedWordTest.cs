// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 448: one word may carry rests AND holds. The rule is that a hold never spans nor overlaps a
// rest: it sits wholly inside one sung stretch of its word, on the same side of each rest's cut as its
// letter (HeldWord.ClearOfRest).
//
// The fixture is "please" sung over [1000, 4000] with a rest over [2000, 2500] cut before the 'a'
// ("ple" + rest + "ase"). The stretches spread their own cells, so the targets are 1000, 1333.33,
// 1666.67 and then 2500, 3000, 3500 (cross-checks, worked from PausedWord's even spread).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class HeldPausedWordTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        private static readonly WordPause rest = new WordPause(2000, 2500, 3);

        /// <summary>A hold on the 'e', let go exactly as the rest starts.</summary>
        private static readonly WordHold before_rest = new WordHold(2, 1700, 2000);

        /// <summary>A hold on the 'a', the first letter after the rest, from the rest's end.</summary>
        private static readonly WordHold after_rest = new WordHold(3, 2500, 2900);

        private static TimedUnit please(params WordHold[] holds) => new TimedUnit
        {
            Text = "please",
            StartTime = 1000,
            EndTime = 4000,
            Source = TimingSource.Explicit,
            Pauses = new[] { rest },
            Objects = holds.Length == 0 ? WordObjects.None : new WordObjects { Holds = holds },
        };

        private static LyricLine line(TimedUnit unit)
            => new LyricLine { RawText = unit.Text, StartTime = 1000, EndTime = 60000, SingEndTime = unit.EndTime, Units = new[] { unit } };

        #region Which holds a play honours

        [Test]
        public void AHoldBeforeAndAHoldAfterTheRestAreBothUsable()
        {
            var unit = please(before_rest, after_rest);

            Assert.That(HeldWord.UsableHolds(unit), Is.EqualTo(new[] { before_rest, after_rest }));
            Assert.That(LyricDifficulty.RatedHolds(unit, unit.Text), Is.EqualTo(new[] { before_rest, after_rest }), "the rating credits both");
        }

        [TestCase(2, 1700, 2200, TestName = "A hold running into the rest")]
        [TestCase(2, 1700, 2700, TestName = "A hold spanning the rest")]
        [TestCase(3, 2300, 2800, TestName = "A hold starting inside the rest")]
        [TestCase(3, 1700, 1950, TestName = "A letter after the cut held before the rest")]
        [TestCase(2, 2600, 2900, TestName = "A letter before the cut held after the rest")]
        public void AHoldThatTouchesTheRestOrCrossesItsCutIsUnusable(int c, double start, double end)
        {
            var hold = new WordHold(c, start, end);
            var unit = please(hold);

            Assert.That(HeldWord.UsableHolds(unit), Is.Empty);
            Assert.That(LyricDifficulty.RatedHolds(unit, unit.Text), Is.Empty, "the rating drops it too");

            // Non-vacuity: the same hold on the word without its rest is usable whenever it is otherwise legal.
            if (end <= 2200 && c == 2)
                Assert.That(HeldWord.UsableHolds(please(hold).WithPauses(Array.Empty<WordPause>())), Is.EqualTo(new[] { hold }));
        }

        [Test]
        public void TheHeldCellsTimeTheLetterAfterTheRestFromTheRestAndHandOffOnlyInsideAStretch()
        {
            var cells = TypingLine.FromLyricLine(line(please(before_rest, after_rest)), alignSubdivisionTargets: true).Cells;

            Assert.That(cells.Select(c => c.Expected), Is.EqualTo("please"));
            Assert.That((cells[2].HoldStart, cells[2].HoldEnd), Is.EqualTo(((double?)1700, (double?)2000)));
            Assert.That((cells[3].HoldStart, cells[3].HoldEnd), Is.EqualTo(((double?)2500, (double?)2900)));

            // The 'a' opens the stretch after the rest: due at the rest's end, and no hand-off from the 'e'.
            Assert.That(cells[3].TargetTime, Is.EqualTo(2500).Within(1e-6));
            Assert.That(cells[3].HandsOffFrom, Is.Null);

            // The 's' follows the 'a' inside one stretch: handed off, and due on its own target (after the hold's end).
            Assert.That(cells[4].HandsOffFrom, Is.EqualTo(3));
            Assert.That(cells[4].TargetTime, Is.EqualTo(3000).Within(1e-6));
            Assert.That(cells[1].TargetTime, Is.EqualTo(1333.333).Within(1e-3));
        }

        #endregion

        #region Play

        private static LyricBeatmap map(LyricLine lyric) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = new[] { lyric },
            Granularity = TimingGranularity.Word,
        };

        private static TypingEngine engineFor(LyricLine lyric)
        {
            var engine = new TypingEngine(map(lyric)) { SyllableTiming = true, CharTimedStretch = true, AuthoredSyllablesOnly = true, AlignSubdivisionTargets = true };
            engine.Update(1000);
            return engine;
        }

        [Test]
        public void AutoplayIsPerfectOnAPausedHeldWord()
        {
            var lyric = line(please(before_rest, after_rest));
            var beatmap = new TypeBeatBeatmap();
            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = lyric.StartTime, LineIndex = 0, Line = lyric, Granularity = TimingGranularity.Word });

            foreach (var hitObject in beatmap.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            var frames = new TypeBeatAutoGenerator(beatmap, syllableTiming: true, charTimedStretch: true, authoredSyllablesOnly: true, alignSubdivisionTargets: true)
                         .Generate().Frames.Cast<TypeBeatReplayFrame>().Where(f => f.Character >= ' ').ToList();

            Assert.That(frames.Select(f => f.Character), Is.EqualTo("please"));

            // The held 'e' goes down at its hold's start, the 'a' at the rest's end.
            Assert.That(frames[2].Time, Is.EqualTo(1700));
            Assert.That(frames[3].Time, Is.EqualTo(2500));

            var engine = engineFor(lyric);
            var tails = new List<HoldTailJudgement>();
            engine.HoldTailJudged += t => tails.Add(t);

            foreach (var frame in frames)
            {
                engine.Update(frame.Time);
                Assert.That(engine.ProcessKey(frame.Character, frame.Time), Is.True, $"'{frame.Character}' @ {frame.Time}");
            }

            engine.Update(70000);

            var results = engine.BuildResults();

            Assert.Multiple(() =>
            {
                Assert.That(tails.Select(t => (t.CellIndex, t.Type)), Is.EqualTo(new[] { (2, JudgementType.Great), (3, JudgementType.Great) }));
                Assert.That(results.Counts[JudgementType.Great], Is.EqualTo(frames.Count + 2), "every press and both tails");
                Assert.That(results.Counts[JudgementType.Ok] + results.Counts[JudgementType.Meh] + results.Counts[JudgementType.Miss], Is.Zero);
                Assert.That(results.Counts[JudgementType.Premature] + results.Counts[JudgementType.Lagging], Is.Zero);

                foreach (var cell in engine.Lines[0].Cells)
                    Assert.That(cell.JudgedDelta, Is.EqualTo(0).Within(1.0), $"cell '{cell.Expected}' @ {cell.TargetTime}");
            });
        }

        [Test]
        public void TheLetterAfterTheRestIsNotHandedOffFromTheHoldBeforeIt()
        {
            var engine = engineFor(line(please(before_rest, after_rest)));

            Assert.That(engine.ProcessKey('p', 1000), Is.True);
            Assert.That(engine.ProcessKey('l', 1333), Is.True);
            engine.Update(1700);
            Assert.That(engine.ProcessKey('e', 1700), Is.True);

            // Inside the 'e' hold's release slack ([1920, 2000)), which would be "on time" for a hand-off;
            // the 'a' is due at 2500, after the rest, so it is 550 early.
            engine.Update(1950);
            Assert.That(engine.ProcessKey('a', 1950), Is.True);

            var a = engine.Lines[0].Cells[3];
            Assert.That(a.JudgedDelta, Is.EqualTo(-550).Within(1e-6));
            Assert.That(a.JudgedTier, Is.Not.EqualTo(JudgementType.Great));
        }

        [Test]
        public void TheLetterAfterAHoldInsideAStretchIsHandedOffOnTime()
        {
            // A hold on the 'l' over [1400, 1600], before the rest: the 'e' (target 1666.67) is handed off.
            var hold = new WordHold(1, 1400, 1600);
            var engine = engineFor(line(please(hold)));

            Assert.That(engine.Lines[0].Cells[2].HandsOffFrom, Is.EqualTo(1));

            var tails = new List<HoldTailJudgement>();
            engine.HoldTailJudged += t => tails.Add(t);

            Assert.That(engine.ProcessKey('p', 1000), Is.True);
            engine.Update(1400);
            Assert.That(engine.ProcessKey('l', 1400), Is.True);

            // In the release slack ([1520, 1600)): the hand-off lets the hold go and is judged on time.
            engine.Update(1550);
            Assert.That(engine.ProcessKey('e', 1550), Is.True);

            Assert.That(engine.Lines[0].Cells[2].JudgedDelta, Is.EqualTo(0));
            Assert.That(tails.Select(t => t.Type), Is.EqualTo(new[] { JudgementType.Great }));
        }

        #endregion

        #region The editor

        private static EditorBeatmap editorWith(TimedUnit unit)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Hold";
            beatmap.Metadata.AudioFile = "audio.mp3";

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = unit.StartTime,
                LineIndex = 0,
                Line = new LyricLine
                {
                    RawText = unit.Text,
                    StartTime = unit.StartTime,
                    EndTime = unit.EndTime,
                    SingEndTime = unit.EndTime,
                    Units = new[] { unit },
                },
                Granularity = TimingGranularity.Word,
            });

            return new EditorBeatmap(beatmap);
        }

        private static TypeBeatHitObject lineAt(EditorBeatmap editorBeatmap) => TypeBeatEditorOperations.OrderedLines(editorBeatmap)[0];

        private static TimedUnit wordAt(EditorBeatmap editorBeatmap) => lineAt(editorBeatmap).Line.Units[0];

        /// <summary>"please" over [1000, 4000] with no rest: cell targets 1000, 1500, ..., 3500.</summary>
        private static TimedUnit unpaused(params WordHold[] holds) => please(holds).WithPauses(Array.Empty<WordPause>());

        [Test]
        public void InsertWordPauseIsRefusedOnlyWhenItWouldHitAHold()
        {
            // A hold on the 'l' over [1500, 1900].
            var held = new WordHold(1, 1500, 1900);

            // At 1700 the rest is [1700, 2000] cut before the 'e': it would split the hold.
            var refused = editorWith(unpaused(held));
            Assert.That(TypeBeatEditorOperations.InsertWordPause(refused, lineAt(refused), 0, 1700), Is.Null);
            Assert.That(wordAt(refused).Pauses, Is.Empty);

            // At 2600 the rest is [2600, 3000] cut before the 's', clear of the hold.
            var accepted = editorWith(unpaused(held));
            Assert.That(TypeBeatEditorOperations.InsertWordPause(accepted, lineAt(accepted), 0, 2600), Is.EqualTo(new WordPause(2600, 3000, 4)));
            Assert.That(HeldWord.UsableHolds(wordAt(accepted)), Is.EqualTo(new[] { held }), "the hold still plays");

            // A rest BEFORE a later hold is fine too: [1700, 2000] cut before the 'e', with the 's' held over [3000, 3400].
            var later = new WordHold(4, 3000, 3400);
            var before = editorWith(unpaused(later));
            Assert.That(TypeBeatEditorOperations.InsertWordPause(before, lineAt(before), 0, 1700), Is.EqualTo(new WordPause(1700, 2000, 2)));
            Assert.That(HeldWord.UsableHolds(wordAt(before)), Is.EqualTo(new[] { later }));
        }

        [Test]
        public void ExtendingASubdivisionIntoARestIsRefusedOnlyWhenItWouldHitAHold()
        {
            // "please" subdivided at 2500 between "ple" and "ase", with the 'e' held over [2100, 2400].
            var held = new WordHold(2, 2100, 2400);
            var unit = new TimedUnit
            {
                Text = "please",
                StartTime = 1000,
                EndTime = 4000,
                Source = TimingSource.Explicit,
                SyllableBoundaries = new[] { 2500.0 },
                SyllableSplits = new[] { 3 },
                Objects = new WordObjects { Holds = new[] { held } },
            };

            Assert.That(HeldWord.UsableHolds(unit), Is.EqualTo(new[] { held }), "the premise");

            // Dragged from 2500 back to 2200: the rest [2200, 2500] would overlap the hold.
            var refused = editorWith(unit);
            Assert.That(TypeBeatEditorOperations.ExtendSubdivisionIntoPause(refused, lineAt(refused), 0, 0, 2500, 2200), Is.False);

            // Dragged from 2500 on to 2800: the rest [2500, 2800] starts as the hold ends.
            var accepted = editorWith(unit);
            Assert.That(TypeBeatEditorOperations.ExtendSubdivisionIntoPause(accepted, lineAt(accepted), 0, 0, 2500, 2800), Is.True);
            Assert.That(wordAt(accepted).Pauses, Is.EqualTo(new[] { new WordPause(2500, 2800, 3) }));
            Assert.That(HeldWord.UsableHolds(wordAt(accepted)), Is.EqualTo(new[] { held }));
        }

        [Test]
        public void TheDefaultHoldStaysInsideItsStretch()
        {
            // The rest [2600, 3000] cut before the 's': the stretches are "plea" over [1000, 2600] (targets
            // 1000, 1400, 1800, 2200) and "se" over [3000, 4000] (targets 3000, 3500).
            var unit = unpaused().WithPauses(new[] { new WordPause(2600, 3000, 4) });

            var a = TypeBeatEditorOperations.DefaultHold(unit, 3, double.NaN);
            Assert.That(a, Is.Not.Null);
            Assert.That((a!.Value.StartTime, a.Value.EndTime), Is.EqualTo((2200.0, 2600.0)).Using(new TolerantPair()), "let go as the rest starts");

            var s = TypeBeatEditorOperations.DefaultHold(unit, 4, double.NaN);
            Assert.That(s, Is.Not.Null);
            Assert.That(s!.Value.StartTime, Is.EqualTo(3000).Within(1e-6), "held from the rest's end");

            foreach (var hold in new[] { a.Value, s.Value })
                Assert.That(HeldWord.ClearOfRest(hold, unit.Pauses[0]), Is.True);
        }

        [Test]
        public void HoldAndRestEdgeDragsStopAtEachOther()
        {
            var restAt = new WordPause(2600, 3000, 4);
            var e = new WordHold(2, 1800, 2200);
            var s = new WordHold(4, 3100, 3400);
            var unit = unpaused(e, s).WithPauses(new[] { restAt });

            Assert.That(HeldWord.UsableHolds(unit), Is.EqualTo(new[] { e, s }), "the premise");

            // A hold's end is walled by the rest after it, its start by the rest before it.
            var holds = editorWith(unit);
            TypeBeatEditorOperations.SetHoldEnd(holds, lineAt(holds), 0, 0, 3500);
            TypeBeatEditorOperations.SetHoldStart(holds, lineAt(holds), 0, 1, 2000);

            var moved = wordAt(holds).Objects.Holds;
            Assert.That(moved[0].EndTime, Is.EqualTo(2600));
            Assert.That(moved[1].StartTime, Is.EqualTo(3000));
            Assert.That(HeldWord.UsableHolds(wordAt(holds)), Has.Count.EqualTo(2));

            // A rest's start is walled by the hold before its cut, its end by the hold after it.
            var rests = editorWith(unit);
            TypeBeatEditorOperations.SetWordPauseStart(rests, lineAt(rests), 0, 0, 1500);
            TypeBeatEditorOperations.SetWordPauseEnd(rests, lineAt(rests), 0, 0, 3800);

            Assert.That(wordAt(rests).Pauses, Is.EqualTo(new[] { new WordPause(2200, 3100, 4) }));
            Assert.That(HeldWord.UsableHolds(wordAt(rests)), Is.EqualTo(new[] { e, s }));
        }

        [Test]
        public void AHoldMarkerOnAPausedWordAddsTheDefaultHold()
        {
            var restAt = new WordPause(2600, 3000, 4);
            var beatmap = editorWith(unpaused().WithPauses(new[] { restAt }));
            var expected = TypeBeatEditorOperations.DefaultHold(wordAt(beatmap), 1, double.NaN);
            bool refused = false;

            string shown = TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap).Line);
            Assert.That(shown, Is.EqualTo("plea|se"));
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap), "pl=ea|se", () => refused = true), Is.True);

            Assert.That(refused, Is.False);
            Assert.That(wordAt(beatmap).Pauses, Is.EqualTo(new[] { restAt }), "the rest is kept");
            Assert.That(wordAt(beatmap).Objects.Holds, Is.EqualTo(new[] { expected!.Value }));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap).Line), Is.EqualTo("pl=ea|se"));
        }

        private sealed class TolerantPair : IEqualityComparer<(double, double)>
        {
            public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) < 1e-6 && Math.Abs(x.Item2 - y.Item2) < 1e-6;

            public int GetHashCode((double, double) obj) => 0;
        }

        #endregion
    }

    internal static class HeldPausedWordTestExtensions
    {
        /// <summary><paramref name="unit"/> with other rests.</summary>
        public static TimedUnit WithPauses(this TimedUnit unit, IReadOnlyList<WordPause> pauses) => new TimedUnit
        {
            Text = unit.Text,
            StartTime = unit.StartTime,
            EndTime = unit.EndTime,
            Source = unit.Source,
            SyllableBoundaries = unit.SyllableBoundaries,
            SyllableSplits = unit.SyllableSplits,
            Objects = unit.Objects,
            Pauses = pauses.ToArray(),
        };
    }
}
