// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Headless coverage of the health / fail model (backlog item 9): sustained not-typing must
// drain HP and fail the play, while imperfect-but-complete play survives. The engine is the
// judgement authority; these tests bridge its judgements into TypeBeatHealthProcessor exactly
// as TypeBeatPlayfield does (CharJudged -> Great/Ok/Meh, seal misses -> Miss, wrong keys ->
// ApplyWrongKeyStreak), so they exercise the real magnitudes end-to-end without a game host.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Judgements;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypeBeatHealthTest
    {
        private const double frame = 1000.0 / 60;

        #region Direct-processor unit pins

        [Test]
        public void MissDrainsAndCorrectCharRecovers()
        {
            var health = new TypeBeatHealthProcessor();

            apply(health, HitResult.Miss);
            Assert.AreEqual(1 - TypeBeatHealthProcessor.MISS_HEALTH_DRAIN, health.Health.Value, 1e-9);

            // One Great (GREAT_HEALTH_INCREASE) more than repays one miss (MISS_HEALTH_DRAIN), so the
            // first Great already overshoots the full bar and the second overshoots further, which is
            // what exercises the clamp this test pins.
            Assert.Greater(TypeBeatHealthProcessor.GREAT_HEALTH_INCREASE, TypeBeatHealthProcessor.MISS_HEALTH_DRAIN,
                "a perfect char must more than repay a miss");
            apply(health, HitResult.Great);
            apply(health, HitResult.Great);
            // Recovery caps at the full bar.
            Assert.AreEqual(1.0, health.Health.Value, 1e-9);

            // From a drained bar the recovery is a real increment, not a snap to full.
            for (int i = 0; i < 20; i++)
                apply(health, HitResult.Miss);

            double drained = health.Health.Value;
            apply(health, HitResult.Meh);
            Assert.AreEqual(drained + TypeBeatHealthProcessor.MEH_HEALTH_INCREASE, health.Health.Value, 1e-9);
        }

        /// <summary>
        /// Backlog 125, closed by backlog 126. An uncorrected typo resolves as an osu HIT
        /// (<c>TypeBeatResultMapping.UNFIXED_TYPO</c>) so that pp can stop pricing it as a miss, and
        /// on the stock health table every hit RECOVERS. That made a masher immortal: type nothing
        /// but wrong characters and the bar climbed. The cell was not typed, so it costs exactly
        /// what an untyped cell costs, and that constant is reused rather than reinvented.
        ///
        /// <para>Backlog 166 moved WHEN, not how much: the drain is taken at the keypress, so the
        /// seal's result is HP-inert and the two cannot bill the same typo twice.</para>
        /// </summary>
        [Test]
        public void AnUncorrectedTypoDrainsInsteadOfRecovering()
        {
            var health = new TypeBeatHealthProcessor();

            health.ApplyTypoDrain();
            Assert.AreEqual(1 - TypeBeatHealthProcessor.MISS_HEALTH_DRAIN, health.Health.Value, 1e-9);

            // The seal reaching the same cell adds NOTHING: the typo has already been paid for.
            apply(health, TypeBeatResultMapping.UNFIXED_TYPO);
            Assert.AreEqual(1 - TypeBeatHealthProcessor.MISS_HEALTH_DRAIN, health.Health.Value, 1e-9,
                "an unfixed typo costs one drain in total, not one per account that hears about it");

            // And it certainly never RECOVERS, which is the failure backlog 125 found.
            Assert.LessOrEqual(health.Health.Value, 1 - TypeBeatHealthProcessor.MISS_HEALTH_DRAIN);

            // Held against the Meh it used to be keyed as, which is what a correct-but-late char
            // takes: that one still recovers, and the two must not be the same number.
            var recovering = new TypeBeatHealthProcessor();
            apply(recovering, HitResult.Meh);
            Assert.AreEqual(1.0, recovering.Health.Value, 1e-9, "a correct char cannot lose health");
        }

        /// <summary>
        /// Erasing a typo gives back exactly what typing it drained, and no more (backlog 166): the
        /// bar returns to where it was, and the recovery the corrected retype earns is left to the
        /// retype's own result. A refund that paid a bonus would make a deliberate typo-and-fix
        /// cycle a way to heal.
        /// </summary>
        [Test]
        public void ErasingATypoRefundsExactlyItsDrain()
        {
            var health = new TypeBeatHealthProcessor();

            // From a bar that is not at the cap, so neither the drain nor the refund is hidden by
            // the clamp.
            health.Health.Value = 0.5;

            health.ApplyTypoDrain();
            Assert.AreEqual(0.5 - TypeBeatHealthProcessor.MISS_HEALTH_DRAIN, health.Health.Value, 1e-9);

            health.RefundTypoDrain();
            Assert.AreEqual(0.5, health.Health.Value, 1e-9, "the refund is the drain, not the drain plus a bonus");

            // The clamp still holds from a full bar: a typo, an erase and a hundred more erases
            // cannot bank credit above the cap.
            var full = new TypeBeatHealthProcessor();
            full.ApplyTypoDrain();
            full.RefundTypoDrain();
            full.RefundTypoDrain();
            Assert.AreEqual(1.0, full.Health.Value, 1e-9);
        }

        /// <summary>
        /// The same run backlog 125 flagged, end to end: a play typed ENTIRELY wrong must die. Under
        /// the stock table it never could, because every one of its cells was a health-recovering
        /// hit. It now dies on exactly the same schedule as a play that typed nothing at all, and
        /// since backlog 166 it dies on the KEYPRESS that empties the bar rather than on the seal
        /// that follows it.
        /// </summary>
        [Test]
        public void SustainedTyposEmptyBarAndFail()
        {
            int cellsToDeath = (int)Math.Ceiling(1.0 / TypeBeatHealthProcessor.MISS_HEALTH_DRAIN);

            var health = new TypeBeatHealthProcessor();

            for (int i = 0; i < cellsToDeath - 1; i++)
                health.ApplyTypoDrain();

            Assert.IsFalse(health.HasFailed, "must not fail before the bar empties");

            health.ApplyTypoDrain();
            Assert.IsTrue(health.HasFailed, "a run typed entirely wrong must not survive on health");
        }

        [Test]
        public void SustainedMissesEmptyBarAndFail()
        {
            // The whole point of item 9: nothing but misses (i.e. never typing) empties the bar
            // and fails. Death lands after ceil(1 / MISS_HEALTH_DRAIN) misses.
            int missesToDeath = (int)Math.Ceiling(1.0 / TypeBeatHealthProcessor.MISS_HEALTH_DRAIN);

            var health = new TypeBeatHealthProcessor();

            for (int i = 0; i < missesToDeath - 1; i++)
                apply(health, HitResult.Miss);

            Assert.IsFalse(health.HasFailed, "must not fail before the bar empties");

            apply(health, HitResult.Miss);
            Assert.IsTrue(health.HasFailed, "sustained misses must empty the bar and fail");
        }

        [Test]
        public void WrongKeyStreakEmptiesBarAndFailsAtThreshold()
        {
            var health = new TypeBeatHealthProcessor();

            // An uninterrupted mash from full depletes the bar linearly (the "stop mashing" warning)
            // and fails exactly at the streak threshold.
            for (int streak = 1; streak < TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK; streak++)
            {
                health.ApplyWrongKeyStreak(streak);
                Assert.IsFalse(health.HasFailed, $"must not fail at streak {streak}");
            }

            Assert.Less(health.Health.Value, 0.1, "bar is nearly empty just before the fail threshold");

            health.ApplyWrongKeyStreak(TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK);
            Assert.IsTrue(health.HasFailed, "mashing must fail at the streak threshold");
        }

        [Test]
        public void OccasionalSpreadMissesNeverFail()
        {
            // A few misses scattered through otherwise-correct play (here a punishing ~12.5%, one
            // miss every 8 cells, over a long map) must comfortably survive: the bar refills between
            // misses and never approaches empty. Only SUSTAINED not-typing kills.
            var health = new TypeBeatHealthProcessor();

            double min = 1;

            for (int i = 0; i < 1600; i++)
            {
                apply(health, i % 8 == 7 ? HitResult.Miss : HitResult.Great);
                min = Math.Min(min, health.Health.Value);
            }

            Assert.IsFalse(health.HasFailed, "scattered misses must not fail");
            Assert.Greater(min, 0.9, "health stays comfortably full through spread-out misses");
            TestContext.WriteLine($"12.5%-scattered-miss floor: {min:0.####} (one miss deep below the cap).");
        }

        #endregion

        #region Synthetic multi-line map (runs without the standalone maps checkout)

        [Test]
        public void SyntheticAfkFailsPartwayThroughMap()
        {
            var beatmap = syntheticMap(lineCount: 20, cellsPerLine: 10);
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            double failTime = playThrough(engine, bridge, beatmap, typeEverything: false);

            Assert.IsTrue(bridge.Health.HasFailed, "sustained AFK must fail");
            Assert.Greater(failTime, 0, "must not fail on the very first frame");
            Assert.Less(failTime, beatmap.LastLineEnd, "AFK must fail before the map ends");
        }

        [Test]
        public void SyntheticPerfectPlayKeepsHealthFull()
        {
            var beatmap = syntheticMap(lineCount: 20, cellsPerLine: 10);
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            playThrough(engine, bridge, beatmap, typeEverything: true);

            Assert.IsFalse(bridge.Health.HasFailed, "perfect play must never fail");
            Assert.AreEqual(1.0, bridge.Health.Health.Value, 1e-9, "perfect play keeps the bar full");
        }

        #endregion

        #region Typo HP: charged at the keypress, refunded by the erase (backlog 166)

        /// <summary>
        /// The point of backlog 166: the bar moves on the keypress that typed the wrong character,
        /// not a line later when it seals. Every other judgement already settled HP at its keypress
        /// (a correct char through its own result, a rejected key through the mash drain); the typo
        /// was the one that waited, because its cell's osu result is deferred.
        /// </summary>
        [Test]
        public void ATypoDrainsAtTheKeypressNotAtTheSeal()
        {
            var beatmap = syntheticMap(lineCount: 2, cellsPerLine: 3);
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            engine.Update(0);
            Assert.AreEqual(0, engine.ActiveLineIndex, "line 0 must be active before anything is typed");

            double t = engine.Lines[0].Cells[0].TargetTime;
            engine.Update(t);
            engine.ProcessKey('z', t); // every synthetic cell expects 'a'

            Assert.AreEqual(1 - TypeBeatHealthProcessor.MISS_HEALTH_DRAIN, bridge.Health.Health.Value, 1e-9,
                "the drain lands on the keypress");
            Assert.AreEqual(0, engine.ActiveLineIndex, "and lands while the line is still being typed");
            Assert.Less(t, beatmap.Lines[0].EndTime, "well before the line seals");
        }

        /// <summary>
        /// The no-double-drain pin, end to end through the real engine: a typo nobody corrects costs
        /// ONE <c>MISS_HEALTH_DRAIN</c> in total, even though two accounts hear about it (the
        /// keypress and, a line later, the <c>UNFIXED_TYPO</c> its cell seals with). The other two
        /// cells of the line are never typed, so they seal as ordinary misses and the arithmetic is
        /// exact rather than clamped.
        /// </summary>
        [Test]
        public void AnUnfixedTypoIsChargedOnceAcrossKeypressAndSeal()
        {
            var beatmap = syntheticMap(lineCount: 2, cellsPerLine: 3);
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            engine.Update(0);

            double t = engine.Lines[0].Cells[0].TargetTime;
            engine.Update(t);
            engine.ProcessKey('z', t);

            sealFirstLine(engine, beatmap);

            Assert.IsTrue(engine.CellLeftWrong(0, 0), "the typo was never corrected");

            // 1 typo + 2 cells nobody typed, at MISS_HEALTH_DRAIN each. A seal that drained the
            // typo again would read 1 - 4 * MISS_HEALTH_DRAIN.
            Assert.AreEqual(1 - 3 * TypeBeatHealthProcessor.MISS_HEALTH_DRAIN, bridge.Health.Health.Value, 1e-9);
        }

        /// <summary>
        /// The refund must not overpay: a typo, a backspace and a correct retype must leave the bar
        /// exactly where typing the character right first time would have. So the two runs below are
        /// compared rather than pinned to a hand-computed number, and both start from a HALF-FULL
        /// bar, because at the cap a bonus refund would be invisible.
        /// </summary>
        [Test]
        public void FixingATypoLeavesHealthWhereTypingItRightWouldHave()
        {
            double corrected = playFirstLine(typoOnFirstCell: true);
            double clean = playFirstLine(typoOnFirstCell: false);

            Assert.AreEqual(clean, corrected, 1e-9, "the detour is refunded, and pays no bonus");

            // Non-vacuous both ways: the run really did climb from the starting bar (so the retype's
            // own recovery is in there) and really did type a typo.
            Assert.Greater(clean, 0.5);
            TestContext.WriteLine($"typo-then-fixed: {corrected:0.######}, typed right first time: {clean:0.######}.");
        }

        /// <summary>
        /// The refund rides on the ERASE, not on the fix, so a typo the player backspaces away and
        /// then leaves empty is priced as the miss it has become: one drain at the seal, exactly
        /// what the cell would have cost had it never been touched. Refunding only at the correction
        /// would charge that cell twice.
        /// </summary>
        [Test]
        public void ATypoErasedAndLeftEmptyCostsOneMissAndNoMore()
        {
            var beatmap = syntheticMap(lineCount: 2, cellsPerLine: 3);
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            engine.Update(0);

            double t = engine.Lines[0].Cells[0].TargetTime;
            engine.Update(t);
            engine.ProcessKey('z', t);
            Assert.IsTrue(engine.ProcessBackspace());

            Assert.AreEqual(1.0, bridge.Health.Health.Value, 1e-9, "the erase gave the drain back");

            sealFirstLine(engine, beatmap);

            Assert.IsFalse(engine.CellLeftWrong(0, 0), "an erased typo leaves an EMPTY cell, which is a miss");

            // All three cells of the line seal untyped.
            Assert.AreEqual(1 - 3 * TypeBeatHealthProcessor.MISS_HEALTH_DRAIN, bridge.Health.Health.Value, 1e-9);
        }

        /// <summary>
        /// The player-visible consequence of charging at the keypress: death can now land MID-LINE,
        /// on the character that empties the bar, where before it could only land on a line seal.
        /// </summary>
        [Test]
        public void ATypoCanFailThePlayMidLine()
        {
            var beatmap = syntheticMap(lineCount: 2, cellsPerLine: 10);
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            engine.Update(0);

            // Less than one typo left in the bar. How it got that low is not what this pins; when
            // the next typo is charged is.
            bridge.Health.Health.Value = TypeBeatHealthProcessor.MISS_HEALTH_DRAIN / 2;

            double t = engine.Lines[0].Cells[0].TargetTime;
            engine.Update(t);
            engine.ProcessKey('z', t);

            Assert.IsTrue(bridge.Health.HasFailed, "the typo that empties the bar kills on the keypress");
            Assert.AreEqual(0, engine.ActiveLineIndex, "and it dies mid-line");
            Assert.Less(t, beatmap.Lines[0].EndTime, "with the line's own seal still ahead of it");
        }

        /// <summary>
        /// Plays the first line of a 3-cell synthetic map from a half-full bar, either straight
        /// through or with the first cell typed wrong, backspaced and retyped correctly at the same
        /// time the clean run typed it. Returns the health left once the line has sealed.
        /// </summary>
        private static double playFirstLine(bool typoOnFirstCell)
        {
            var beatmap = syntheticMap(lineCount: 2, cellsPerLine: 3);
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            engine.Update(0);
            bridge.Health.Health.Value = 0.5;

            var cells = engine.Lines[0].Cells;

            for (int i = 0; i < cells.Count; i++)
            {
                double t = cells[i].TargetTime;
                engine.Update(t);

                if (i == 0 && typoOnFirstCell)
                {
                    engine.ProcessKey('z', t);
                    Assert.IsTrue(engine.ProcessBackspace());
                }

                engine.ProcessKey(cells[i].Expected, t);
            }

            sealFirstLine(engine, beatmap);
            Assert.IsFalse(bridge.Health.HasFailed);

            return bridge.Health.Health.Value;
        }

        /// <summary>
        /// Runs the engine on past the first line's end so it seals, stopping before the second line
        /// can seal too (whose misses would swamp the numbers these tests pin).
        /// </summary>
        private static void sealFirstLine(TypingEngine engine, LyricBeatmap beatmap)
        {
            bool done = false;

            void handler(LineSealResult r)
            {
                if (r.LineIndex == 0)
                    done = true;
            }

            engine.LineSealed += handler;

            for (double t = beatmap.Lines[0].EndTime; !done && t <= beatmap.Lines[1].StartTime; t += frame)
                engine.Update(t);

            engine.LineSealed -= handler;

            Assert.IsTrue(done, "the first line must have sealed");
        }

        #endregion

        #region Real Spectator map pins

        [Test]
        public void RealSpectatorFullAfkFailsPartwayThroughMap()
        {
            var beatmap = loadSpectator();
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            double failTime = playThrough(engine, bridge, beatmap, typeEverything: false);

            Assert.IsTrue(bridge.Health.HasFailed, "sustained AFK on the real map must fail");
            Assert.Greater(failTime, 0);
            // Dies well inside the first half of the ~165s map, clearly "sustained not typing",
            // not a last-second technicality.
            Assert.Less(failTime, beatmap.LastLineEnd * 0.5, "AFK must die partway through, not at the end");
            TestContext.WriteLine($"Full-AFK death lands at t={failTime:0}ms of {beatmap.LastLineEnd:0}ms.");
        }

        [Test]
        public void RealSpectatorPerfectPlayKeepsHealthHigh()
        {
            var beatmap = loadSpectator();
            var engine = new TypingEngine(beatmap);
            var bridge = new HealthBridge(engine);

            playThrough(engine, bridge, beatmap, typeEverything: true);

            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(0, engine.BuildResults().Counts[JudgementType.Miss], "rhythm-perfect play has zero misses");
            Assert.IsFalse(bridge.Health.HasFailed, "rhythm-perfect play must never fail");
            Assert.AreEqual(1.0, bridge.Health.Health.Value, 1e-9, "rhythm-perfect play keeps the bar full");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Runs the engine frame-by-frame. When <paramref name="typeEverything"/> is set it types
        /// each caret cell on the first frame at/after its target (rhythm-perfect play); otherwise
        /// it never types (full AFK). Returns the gameplay time the play first entered a failed
        /// state, or -1 if it never failed.
        /// </summary>
        private static double playThrough(TypingEngine engine, HealthBridge bridge, LyricBeatmap beatmap, bool typeEverything)
        {
            double failTime = -1;

            for (double t = 0; t <= beatmap.LastLineEnd + 1000 && !engine.IsFinished; t += frame)
            {
                engine.Update(t);

                if (typeEverything)
                {
                    while (engine.ActiveLineIndex != -1 && !engine.IsLineComplete)
                    {
                        var cell = engine.Lines[engine.ActiveLineIndex].Cells[engine.CaretIndex];

                        if (cell.TargetTime > t)
                            break;

                        engine.ProcessKey(cell.Expected, t);
                    }
                }

                if (failTime < 0 && bridge.Health.HasFailed)
                {
                    failTime = t;
                    break;
                }
            }

            if (typeEverything)
                engine.Update(beatmap.LastLineEnd + 1100);

            return failTime;
        }

        private static void apply(TypeBeatHealthProcessor health, HitResult type)
            => health.ApplyResult(new JudgementResult(sharedObject, sharedJudgement) { Type = type });

        // Health only reads result.Type, so a single shared carrier is sufficient.
        private static readonly TypeBeatCharObject sharedObject = new TypeBeatCharObject();
        private static readonly TypeBeatCharJudgement sharedJudgement = new TypeBeatCharJudgement();

        private static LyricBeatmap syntheticMap(int lineCount, int cellsPerLine)
        {
            string word = new string('a', cellsPerLine); // all-typeable, no punctuation/spaces
            var lines = new List<LyricLine>(lineCount);

            for (int i = 0; i < lineCount; i++)
            {
                double start = i * 3000;
                double end = start + 2000;
                lines.Add(new LyricLine
                {
                    RawText = word,
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = end,
                    Units = new[] { new TimedUnit { Text = word, StartTime = start, EndTime = end } },
                });
            }

            return new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata
                {
                    Artist = "Test",
                    Title = "AFK",
                    FolderPath = @"X:\nowhere",
                    AudioFileName = "a.mp3",
                },
                Lines = lines,
                Granularity = TimingGranularity.Line,
            };
        }

        private static LyricBeatmap loadSpectator()
        {
            string path = StandaloneMaps.Require("Friday Pilots Club - Spectator", "timing.json");
            Assert.IsTrue(TimingJsonLoader.TryLoad(path, out var lyricLines));

            return new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata
                {
                    Artist = "Friday Pilots Club",
                    Title = "Spectator",
                    FolderPath = Path.GetDirectoryName(path)!,
                    AudioFileName = "unused.mp3",
                    HasWordTiming = true,
                },
                Lines = lyricLines,
                Granularity = TimingGranularity.Word,
            };
        }

        /// <summary>
        /// Mirrors <see cref="UI.TypeBeatPlayfield"/>'s health wiring: a correct char reaches health
        /// as its Great/Ok/Meh result, a wrong char typed into a cell as the keypress drain and its
        /// erase as the refund (backlog 166), every cell that seals untyped as a Miss, every cell
        /// left sitting wrong as the <c>UNFIXED_TYPO</c> its drawable takes at the seal, and a
        /// rejected wrong key through the mash-streak drain; the same paths the drawable bridge
        /// takes in gameplay.
        /// </summary>
        private sealed class HealthBridge
        {
            public readonly TypeBeatHealthProcessor Health = new TypeBeatHealthProcessor();

            public HealthBridge(TypingEngine engine)
            {
                engine.CharJudged += j =>
                {
                    // A wrong char applies NO osu result (its cell's is deferred), so what the
                    // playfield does here is drain HP directly.
                    if (j.Type == JudgementType.WrongChar)
                    {
                        Health.ApplyTypoDrain();
                        return;
                    }

                    TypeBeatHealthTest.apply(Health, toHitResult(j.Type));
                };

                engine.TypoErased += Health.RefundTypoDrain;

                engine.LineSealed += r =>
                {
                    for (int i = 0; i < r.MissedCells; i++)
                        TypeBeatHealthTest.apply(Health, HitResult.Miss);

                    // Deliberately applied rather than skipped: this is the result the playfield
                    // really hands an unfixed typo's cell at the seal, so a seal that started
                    // draining again would show up in these numbers instead of hiding here.
                    var cells = engine.Lines[r.LineIndex].Cells;

                    for (int i = 0; i < cells.Count; i++)
                    {
                        if (engine.CellLeftWrong(r.LineIndex, i))
                            TypeBeatHealthTest.apply(Health, TypeBeatResultMapping.UNFIXED_TYPO);
                    }
                };

                engine.WrongKeyRejected += _ => Health.ApplyWrongKeyStreak(engine.ConsecutiveWrongKeys);
            }

            private static HitResult toHitResult(JudgementType type)
            {
                switch (type)
                {
                    case JudgementType.Great:
                        return HitResult.Great;

                    case JudgementType.Ok:
                        return HitResult.Ok;

                    case JudgementType.Meh:
                        return HitResult.Meh;

                    default:
                        // Premature / Lagging / Miss. WrongChar never reaches here (handled above).
                        return HitResult.Miss;
                }
            }
        }

        #endregion
    }
}
