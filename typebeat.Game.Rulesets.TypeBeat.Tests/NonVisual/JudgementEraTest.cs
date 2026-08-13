// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
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

        private static Replay replay(params (double time, char c)[] presses)
        {
            var r = new Replay();
            r.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true));

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
                SpaceTimingRule.Untimed, RateWindowRule.ScaledByRate);

            Assert.Multiple(() =>
            {
                Assert.That(implicitEra.Statistics, Is.EquivalentTo(explicitLive.Statistics));
                Assert.That(implicitEra.MaxCombo, Is.EqualTo(explicitLive.MaxCombo));
                Assert.That(implicitEra.TotalScore, Is.EqualTo(explicitLive.TotalScore));

                // ...and the engine's own default agrees, which is what live play takes.
                Assert.That(new TypingEngine(lyricBeatmap()).SpaceTiming, Is.EqualTo(SpaceTimingRule.Untimed));
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
