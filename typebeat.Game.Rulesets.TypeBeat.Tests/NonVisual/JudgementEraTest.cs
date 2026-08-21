// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 151: the JUDGEMENT ERA a stored score was played under, as a switch the replay scorer
    /// takes, so a row can be re-derived on the ladder it was actually graded on.
    ///
    /// <para>Two judgement changes shipped with no such switch and would otherwise be applied
    /// unconditionally to every re-derivation, which would make the recalculation tool's reproduce
    /// pass fail on rows that are not corrupt at all:</para>
    /// <list type="bullet">
    /// <item><b>Backlog 148</b>, the untimed spacebar (<see cref="SpaceTimingRule"/>). Every map has
    /// spaces, so this reaches EVERY stored row.</item>
    /// <item><b>Backlog 150</b>, rate mods scaling the windows (<see cref="RateWindowRule"/>). This
    /// reaches every stored DT / NC / HT row.</item>
    /// </list>
    ///
    /// <para>Backlog 167 (<see cref="WordSkipRule"/>) and backlog 176
    /// (<see cref="ComboClaimRule"/>) added an axis each on the same pattern, and are pinned here
    /// too.</para>
    ///
    /// <para>The properties pinned here are the ones the tool rests on: the switches DEFAULT to
    /// today's rules (so nothing that does not ask for an era changes meaning), each one actually
    /// reaches the SUBMITTED account rather than only a display readout, and the rate axis is inert
    /// for a run that carries no rate mod. <see cref="UntimedSpaceTest"/> and
    /// <see cref="TypeBeatRateModTest"/> hold the live behaviour itself; this holds the ability to
    /// turn each of them back off.</para>
    /// </summary>
    [TestFixture]
    public class JudgementEraTest
    {
        #region Fixture

        /// <summary>
        /// "ab cd" as two words far apart: a = 0, b = 3000, ' ' = 6000 (the first unit's end),
        /// c = 20000, d = 23000. The gap is what lets the SPACE be pressed grossly late while every
        /// later press still lands dead on its own target, so the only thing an era switch can move
        /// is the space's own judgement. Line-granularity windows: Great [-250, 400], Ok [-600,
        /// 1000], Meh [-1200, 2000].
        /// </summary>
        private static TypeBeatBeatmap spacedMap()
        {
            var line = new LyricLine
            {
                RawText = "ab cd",
                StartTime = 0,
                EndTime = 40000,
                SingEndTime = 26000,
                Units = new[]
                {
                    new TimedUnit { Text = "ab", StartTime = 0, EndTime = 6000 },
                    new TimedUnit { Text = "cd", StartTime = 20000, EndTime = 26000 },
                },
            };

            return built(line);
        }

        /// <summary>
        /// "abc" over [0, 12000], so the cells target 0, 4000 and 8000. The same shape
        /// <see cref="TypeBeatRateModTest"/> uses for the live half of backlog 150.
        /// </summary>
        private static TypeBeatBeatmap plainMap()
        {
            var line = new LyricLine
            {
                RawText = "abc",
                StartTime = 0,
                EndTime = 20000,
                SingEndTime = 12000,
                Units = new[] { new TimedUnit { Text = "abc", StartTime = 0, EndTime = 12000 } },
            };

            return built(line);
        }

        private static TypeBeatBeatmap built(LyricLine line)
        {
            var map = new TypeBeatBeatmap();
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Line });

            // Nested per-cell objects come from ApplyDefaults, and they are what the score processor
            // counts maximum_statistics over.
            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        /// <summary>The same lines as a bare <see cref="LyricBeatmap"/>, for driving the engine directly.</summary>
        private static LyricBeatmap lyricBeatmap(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = string.Empty,
                AudioFileName = "a.mp3",
            },
            Lines = lines,
            Granularity = TimingGranularity.Line,
        };

        /// <summary>
        /// Two lines: "abc def" over [0, 20000] (cells a = 0, b = 2000, c = 4000, ' ' = 6000,
        /// d = 6000, e = 8000, f = 10000) and "ghi" over [20000, 40000] (g = 20000, h = 24000,
        /// i = 28000). The second line is what makes a combo break at the FIRST line's seal visible:
        /// it is a run the player is still building when that seal lands.
        /// </summary>
        private static TypeBeatBeatmap skipMap()
        {
            var first = new LyricLine
            {
                RawText = "abc def",
                StartTime = 0,
                EndTime = 20000,
                SingEndTime = 12000,
                Units = new[]
                {
                    new TimedUnit { Text = "abc", StartTime = 0, EndTime = 6000 },
                    new TimedUnit { Text = "def", StartTime = 6000, EndTime = 12000 },
                },
            };

            var second = new LyricLine
            {
                RawText = "ghi",
                StartTime = 20000,
                EndTime = 40000,
                SingEndTime = 32000,
                Units = new[] { new TimedUnit { Text = "ghi", StartTime = 20000, EndTime = 32000 } },
            };

            var map = new TypeBeatBeatmap();
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = first, Granularity = TimingGranularity.Line });
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 20000, LineIndex = 1, Line = second, Granularity = TimingGranularity.Line });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        /// <summary>
        /// "hear from me all" as four word units over [0, 40000], the fragment backlog 176 came out
        /// of ("if you never hear from me, all the satellites are down"). Sixteen cells, so a clean
        /// run of it is a max_combo of 16.
        /// </summary>
        private static LyricLine typoShapeLine() => new LyricLine
        {
            RawText = "hear from me all",
            StartTime = 0,
            EndTime = 40000,
            SingEndTime = 20000,
            Units = new[]
            {
                new TimedUnit { Text = "hear", StartTime = 0, EndTime = 5000 },
                new TimedUnit { Text = "from", StartTime = 5000, EndTime = 10000 },
                new TimedUnit { Text = "me", StartTime = 10000, EndTime = 15000 },
                new TimedUnit { Text = "all", StartTime = 15000, EndTime = 20000 },
            },
        };

        /// <summary>
        /// Each cell's own target time, read off the engine that builds them rather than recomputed
        /// here, so the presses below are struck on target whatever the subdivision rule is.
        /// </summary>
        private static double[] cellTargets(LyricLine line)
            => new TypingEngine(lyricBeatmap(line)).Lines[0].Cells.Select(c => c.TargetTime).ToArray();

        private static Replay replay(params (double time, char c)[] presses)
            => replay(false, presses);

        private static Replay replay(bool spaceSkipsWord, params (double time, char c)[] presses)
        {
            var r = new Replay();
            r.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true, spaceSkipsWord));

            foreach ((double time, char c) in presses)
                r.Frames.Add(new TypeBeatReplayFrame(time, c));

            return r;
        }

        /// <summary>
        /// Every cell struck on target except the SPACE, which lands 2500 ms late: outside even the
        /// Meh window (2000 late), so under the pre-148 rule it is a Lagging press that resolves its
        /// cell as a Miss and breaks the run.
        /// </summary>
        private static Replay lateSpaceReplay()
            => replay((0, 'a'), (3000, 'b'), (8500, ' '), (20000, 'c'), (23000, 'd'));

        private static int count(TypeBeatReplayAccount account, HitResult result)
            => account.Statistics.GetValueOrDefault(result);

        #endregion

        // -----------------------------------------------------------------------------------------
        // The default is TODAY. Nothing that does not ask for an era may get one.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The load-bearing default: a caller that names neither switch is judged under the LIVE
        /// rules, so every existing call site kept its meaning when the parameters were added. A
        /// default of the stored era would have silently re-graded live play's own replays.
        /// </summary>
        [Test]
        public void TheEraSwitchesDefaultToTodaysRules()
        {
            var map = spacedMap();
            var r = lateSpaceReplay();

            var implicitEra = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix);

            var explicitLive = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Untimed, RateWindowRule.ScaledByRate, WordSkipRule.Reclaimable, ComboClaimRule.StreakedBreakWins);

            Assert.Multiple(() =>
            {
                Assert.That(implicitEra.Statistics, Is.EquivalentTo(explicitLive.Statistics));
                Assert.That(implicitEra.MaxCombo, Is.EqualTo(explicitLive.MaxCombo));
                Assert.That(implicitEra.TotalScore, Is.EqualTo(explicitLive.TotalScore));

                // ...and the engine's own defaults agree, which is what live play takes.
                Assert.That(new TypingEngine(lyricBeatmap()).SpaceTiming, Is.EqualTo(SpaceTimingRule.Untimed));
                Assert.That(new TypingEngine(lyricBeatmap()).WordSkip, Is.EqualTo(WordSkipRule.Reclaimable));
                Assert.That(new TypingEngine(lyricBeatmap()).ComboClaim, Is.EqualTo(ComboClaimRule.StreakedBreakWins));
            });
        }

        // -----------------------------------------------------------------------------------------
        // Backlog 148: the spacebar, in and out of the timing challenge.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The whole reason this task exists. A space pressed 2500 ms late is a top-tier hit under
        /// today's rule and a MISS that breaks the run under the rule every stored row was played
        /// under, so the two arms disagree on <c>statistics</c> AND on <c>max_combo</c>: exactly the
        /// two quantities the recalculation tool's reproduce gate compares against the stored row.
        /// Without the switch that gate would report every row in the table as unreproducible.
        /// </summary>
        [Test]
        public void ThePreExemptionEraGradesASpaceOnTheClockAgain()
        {
            var map = spacedMap();
            var r = lateSpaceReplay();

            var live = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var stored = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Timed, RateWindowRule.ScaledByRate);

            Assert.Multiple(() =>
            {
                // Today: the space is judged on a zeroed delta, so all five cells are Greats and the
                // run is unbroken.
                Assert.That(count(live, HitResult.Great), Is.EqualTo(5));
                Assert.That(count(live, HitResult.Miss), Is.Zero);
                Assert.That(live.MaxCombo, Is.EqualTo(5));

                // Pre-148: 2500 late is outside the Meh window, so the press is Lagging, its cell
                // resolves as a Miss, and the streak ends at the two characters before it.
                Assert.That(count(stored, HitResult.Great), Is.EqualTo(4));
                Assert.That(count(stored, HitResult.Miss), Is.EqualTo(1));
                Assert.That(stored.MaxCombo, Is.EqualTo(2));

                Assert.That(stored.TotalScore, Is.LessThan(live.TotalScore));
                Assert.That(stored.Rank, Is.Not.EqualTo(live.Rank));
            });
        }

        /// <summary>
        /// The switch reaches the SYNC readouts too, not only the keypress. Under
        /// <see cref="SpaceTimingRule.Timed"/> a space is a timed cell, so it belongs in both halves
        /// of the sync mean exactly as it did before backlog 148; leaving the readout on the live
        /// filter would report a sync figure no client ever produced for that run.
        /// </summary>
        [Test]
        public void ThePreExemptionEraPutsSpacesBackIntoTheSyncMean()
        {
            var beatmap = lyricBeatmap(new LyricLine
            {
                RawText = "ab cd",
                StartTime = 0,
                EndTime = 40000,
                SingEndTime = 26000,
                Units = new[]
                {
                    new TimedUnit { Text = "ab", StartTime = 0, EndTime = 6000 },
                    new TimedUnit { Text = "cd", StartTime = 20000, EndTime = 26000 },
                },
            });

            var engine = new TypingEngine(beatmap) { SpaceTiming = SpaceTimingRule.Timed };
            engine.Update(0);

            Assert.That(engine.ActiveLineIndex, Is.EqualTo(0));

            engine.ProcessKey('a', 0);
            engine.ProcessKey('b', 3000);
            engine.ProcessKey(' ', 8500);
            engine.ProcessKey('c', 20000);
            engine.ProcessKey('d', 23000);

            var results = engine.BuildResults();

            Assert.Multiple(() =>
            {
                // Four cells dead on target plus one Lagging space, over a denominator of FIVE timed
                // cells: the space is in the divisor, so its zero quality costs a fifth of the mean.
                Assert.That(results.SyncPercent, Is.EqualTo(80).Within(1e-9));
                Assert.That(results.Counts[JudgementType.Lagging], Is.EqualTo(1));
            });

            // The same run under the live rule: the space is out of both halves, so four timed cells
            // struck on target are a clean 100.
            var todays = new TypingEngine(beatmap);
            todays.Update(0);
            todays.ProcessKey('a', 0);
            todays.ProcessKey('b', 3000);
            todays.ProcessKey(' ', 8500);
            todays.ProcessKey('c', 20000);
            todays.ProcessKey('d', 23000);

            Assert.That(todays.BuildResults().SyncPercent, Is.EqualTo(100).Within(1e-9));
        }

        // -----------------------------------------------------------------------------------------
        // Backlog 150: the rate mods and the window scale.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Three presses 500 ms late. Unmodded that is an Ok apiece (GreatLate 400); at 1.50x today's
        /// rule stretches the Great window to 600 and pays all three, and the pre-150 rule leaves the
        /// ladder in beatmap milliseconds, where the run is exactly the Ok run it always was.
        /// </summary>
        [Test]
        public void ThePreScalingEraLeavesARateRunOnTheUnscaledLadder()
        {
            var map = plainMap();
            var r = replay((500, 'a'), (4500, 'b'), (8500, 'c'));
            Mod[] doubleTime = { new TypeBeatModDoubleTime { SpeedChange = { Value = 1.5 } } };

            var live = TypeBeatReplayScorer.Score(map, doubleTime, r, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var stored = TypeBeatReplayScorer.Score(map, doubleTime, r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Untimed, RateWindowRule.Unscaled);

            var unmodded = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Untimed, RateWindowRule.Unscaled);

            Assert.Multiple(() =>
            {
                Assert.That(count(live, HitResult.Great), Is.EqualTo(3), "1.50x stretches GreatLate to 600");
                Assert.That(count(stored, HitResult.Ok), Is.EqualTo(3), "a pre-150 client graded on the base ladder");
                Assert.That(count(stored, HitResult.Great), Is.Zero);

                // The pre-150 arm is the no-rate-mod ladder, which is precisely what it means.
                Assert.That(stored.Statistics, Is.EquivalentTo(unmodded.Statistics));
            });
        }

        /// <summary>
        /// The rate axis is INERT for a run with no rate mod, which is most of the table: the loop it
        /// gates has nothing to iterate. Worth pinning because it is what lets the recalculation tool
        /// set the stored-era arm unconditionally instead of first working out whether the row has a
        /// rate mod on it.
        /// </summary>
        [Test]
        public void TheRateEraChangesNothingWithoutARateMod()
        {
            var map = plainMap();
            var r = replay((500, 'a'), (4500, 'b'), (8500, 'c'));

            var scaled = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Untimed, RateWindowRule.ScaledByRate);

            var unscaled = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Untimed, RateWindowRule.Unscaled);

            Assert.Multiple(() =>
            {
                Assert.That(scaled.Statistics, Is.EquivalentTo(unscaled.Statistics));
                Assert.That(scaled.MaxCombo, Is.EqualTo(unscaled.MaxCombo));
                Assert.That(scaled.TotalScore, Is.EqualTo(unscaled.TotalScore));
            });
        }

        // -----------------------------------------------------------------------------------------
        // Backlog 167: the abandoned word, reclaimable or missed on the spot.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The SUBMITTED account of a skip nobody goes back for is IDENTICAL under the two rules,
        /// which is the whole claim backlog 167 rests on: the reclaim moved when the abandoned cells
        /// are written off, never what they cost. Every quantity the recalculation tool compares
        /// against a stored row is checked, so a row played before the reclaim reproduces exactly.
        ///
        /// <para>Nothing about that is free, and this fixture is built to prove it. The run keeps
        /// typing into a SECOND line, so the two things the live rule has to do by hand are both
        /// visible in <c>max_combo</c>: the skip's own break is mirrored by hand (without it the run
        /// would never break at all and the maximum would read 8), and the deferred Misses are
        /// applied combo-neutral at the seal (without that they would break the run the player had
        /// rebuilt, and the maximum would read 4).</para>
        /// </summary>
        [Test]
        public void ASkipNobodyGoesBackForCostsTheSameUnderBothEras()
        {
            var map = skipMap();

            // Type 'a', abandon "bc", then carry on with "def" and the whole of the next line.
            var r = replay(true,
                (0, 'a'), (2500, ' '), (6100, 'd'), (8000, 'e'), (10000, 'f'),
                (20000, 'g'), (24000, 'h'), (28000, 'i'));

            var live = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var stored = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Untimed, RateWindowRule.ScaledByRate, WordSkipRule.ImmediateMiss);

            Assert.Multiple(() =>
            {
                Assert.That(count(live, HitResult.Miss), Is.EqualTo(2), "the two abandoned cells, written off at the seal");
                Assert.That(count(live, HitResult.Great), Is.EqualTo(8));
                Assert.That(live.MaxCombo, Is.EqualTo(7), "one break, at the skip, and none at the seal");

                Assert.That(live.Statistics, Is.EquivalentTo(stored.Statistics));
                Assert.That(live.MaxCombo, Is.EqualTo(stored.MaxCombo));
                Assert.That(live.TotalScore, Is.EqualTo(stored.TotalScore));
                Assert.That(live.Accuracy, Is.EqualTo(stored.Accuracy));
                Assert.That(live.Rank, Is.EqualTo(stored.Rank));
            });
        }

        /// <summary>
        /// And the switch is load-bearing the moment the player DOES go back. Under today's rule the
        /// abandoned cells are still unresolved, so the retype earns them and the map ends fully
        /// typed; under the rule a stored row was played on, they spent their one result at the skip,
        /// the backspaces land somewhere else entirely and the same keystrokes produce a mangled run.
        /// Re-deriving such a row under today's rule would invent a score its fingers never earned.
        /// </summary>
        [Test]
        public void ThePreReclaimEraCannotEarnASkippedWordBack()
        {
            var map = skipMap();

            // 'a', abandon "bc", two backspaces straight back into the word, then type it all out.
            var r = replay(true,
                (0, 'a'), (2500, ' '),
                (2600, TypeBeatReplayFrame.BACKSPACE), (2700, TypeBeatReplayFrame.BACKSPACE),
                (2800, 'a'), (3000, 'b'), (3200, 'c'),
                (6000, ' '), (6100, 'd'), (8000, 'e'), (10000, 'f'),
                (20000, 'g'), (24000, 'h'), (28000, 'i'));

            var live = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var stored = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Untimed, RateWindowRule.ScaledByRate, WordSkipRule.ImmediateMiss);

            Assert.Multiple(() =>
            {
                // Today: every cell of the map ends up typed, so nothing is missed and the play is
                // complete. The two reclaimed cells are graded on the clock like any other, which is
                // late here (the player went back for them), so they are not Greats.
                Assert.That(count(live, HitResult.Miss), Is.Zero);
                Assert.That(live.Completion, Is.EqualTo(1).Within(1e-9));
                Assert.That(live.Rank, Is.EqualTo(ScoreRank.X));

                // Pre-167: the cells were missed at the skip and can never be earned back.
                Assert.That(count(stored, HitResult.Miss), Is.EqualTo(2));
                Assert.That(stored.Completion, Is.LessThan(1));
                Assert.That(stored.Rank, Is.Not.EqualTo(ScoreRank.X));
                Assert.That(stored.TotalScore, Is.LessThan(live.TotalScore));
            });
        }

        // -----------------------------------------------------------------------------------------
        // Backlog 176: which break owns the claim when one costs nothing.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The whole submitted account of the shape backlog 176 came out of, under both arms. A real
        /// row is what forced it: score 6212 on "Joji - PIXELATED KISSES [Insane]", 447 combo deep
        /// into "if you never hear from me", where the player typed 'a' onto the 'm' cell and 'm'
        /// onto the 'e' cell, backspaced twice and typed "me" out correctly. That run re-derives to
        /// 796,939 and a max_combo of 447 under <see cref="ComboClaimRule.LatestBreakWins"/>, which
        /// is exactly what the server stored, and to 904,166 and a max_combo of 592 (every judged
        /// cell of the map: it is a full combo) under the live rule, with the statistics and the
        /// accuracy identical on both arms. This fixture is that shape in miniature, on sixteen
        /// cells instead of 592.
        ///
        /// <para>Which is why the change needs an era switch at all. It moves nothing about how a
        /// cell is JUDGED, so the statistics and the accuracy are bit-identical under both arms;
        /// what it moves is max_combo and therefore total_score, and those are two of the quantities
        /// the recalculation tool reproduces against the stored row.</para>
        /// </summary>
        [Test]
        public void ABreakThatCostNothingKeepsTheOlderClaimOnlyUnderTheLiveRule()
        {
            var map = built(typoShapeLine());
            double[] targets = cellTargets(typoShapeLine());

            const string text = "hear from me all";

            var presses = new List<(double, char)>();

            // "hear from " struck on target: a run of ten.
            for (int i = 0; i < 10; i++)
                presses.Add((targets[i], text[i]));

            // The reported fumble: 'a' onto the 'm' of "me", then 'm' onto the 'e', the second one
            // landing on a run the first has already zeroed.
            presses.Add((targets[10], 'a'));
            presses.Add((targets[11], 'm'));

            // Two backspaces and "me" typed out, then the rest of the line.
            presses.Add((targets[11] + 100, TypeBeatReplayFrame.BACKSPACE));
            presses.Add((targets[11] + 200, TypeBeatReplayFrame.BACKSPACE));
            presses.Add((targets[10] + 300, 'm'));
            presses.Add((targets[11] + 300, 'e'));

            for (int i = 12; i < text.Length; i++)
                presses.Add((targets[i], text[i]));

            var r = replay(presses.ToArray());

            var live = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var stored = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Untimed, RateWindowRule.ScaledByRate, WordSkipRule.Reclaimable, ComboClaimRule.LatestBreakWins);

            Assert.Multiple(() =>
            {
                // Today: the 'm' cell keeps its claim, so correcting it resumes the ten and the run
                // finishes as the full combo the player's fingers actually typed.
                Assert.That(live.MaxCombo, Is.EqualTo(16), "every cell of the line, exactly as the real row becomes a 592 full combo");
                Assert.That(count(live, HitResult.Miss), Is.Zero);

                // Pre-176: the second wrong key took the claim with the empty streak it broke, so
                // the run ends at the ten typed before the fumble.
                Assert.That(stored.MaxCombo, Is.EqualTo(10));
                Assert.That(stored.TotalScore, Is.LessThan(live.TotalScore));

                // Nothing about the JUDGEMENTS moves, which is the shape of the real row too.
                Assert.That(stored.Statistics, Is.EquivalentTo(live.Statistics));
                Assert.That(stored.Accuracy, Is.EqualTo(live.Accuracy));
                Assert.That(stored.Mistypes, Is.EqualTo(2));
                Assert.That(live.Mistypes, Is.EqualTo(2));
            });
        }

        /// <summary>
        /// The claim axis is INERT for a run that never fumbles twice in a row, which is nearly every
        /// row there is: with at most one redeemable break outstanding at a time there is never an
        /// older claim for an empty break to spare. Worth pinning for the same reason
        /// <see cref="TheRateEraChangesNothingWithoutARateMod"/> is: it is what lets the
        /// recalculation tool set the stored-era arm unconditionally.
        /// </summary>
        [Test]
        public void TheClaimEraChangesNothingWithoutASecondBreak()
        {
            var map = built(typoShapeLine());
            double[] targets = cellTargets(typoShapeLine());

            const string text = "hear from me all";

            var presses = new List<(double, char)>();

            for (int i = 0; i < 10; i++)
                presses.Add((targets[i], text[i]));

            // ONE wrong key, corrected: the plain backlog 140 shape, with no second break at all.
            presses.Add((targets[10], 'a'));
            presses.Add((targets[10] + 100, TypeBeatReplayFrame.BACKSPACE));
            presses.Add((targets[10] + 200, 'm'));

            for (int i = 11; i < text.Length; i++)
                presses.Add((targets[i], text[i]));

            var r = replay(presses.ToArray());

            var live = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var stored = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Untimed, RateWindowRule.ScaledByRate, WordSkipRule.Reclaimable, ComboClaimRule.LatestBreakWins);

            Assert.Multiple(() =>
            {
                Assert.That(live.MaxCombo, Is.EqualTo(16), "the correction resumes the run under backlog 140 alone");
                Assert.That(stored.MaxCombo, Is.EqualTo(live.MaxCombo));
                Assert.That(stored.Statistics, Is.EquivalentTo(live.Statistics));
                Assert.That(stored.TotalScore, Is.EqualTo(live.TotalScore));
            });
        }

        /// <summary>
        /// Easy and Hard Rock have no era switch, and this is the statement of why rather than an
        /// oversight: both ship for the first time in the release that also carries backlog 148 and
        /// 150, so no stored row can carry either acronym and there is no era in which their window
        /// scales should be off. Their arms therefore hold under BOTH era settings.
        /// </summary>
        [Test]
        public void TheNewDifficultyModsAreNotEraDependent()
        {
            var map = plainMap();
            var r = replay((500, 'a'), (4500, 'b'), (8500, 'c'));
            Mod[] easy = { new TypeBeatModEasy() };

            var live = TypeBeatReplayScorer.Score(map, easy, r, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var stored = TypeBeatReplayScorer.Score(map, easy, r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Timed, RateWindowRule.Unscaled);

            Assert.Multiple(() =>
            {
                // Easy doubles the windows, so GreatLate is 800 and 500 late is a Great, era or no era.
                Assert.That(count(live, HitResult.Great), Is.EqualTo(3));
                Assert.That(stored.Statistics, Is.EquivalentTo(live.Statistics));
            });
        }
    }
}
