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
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class StarTimingWindowsTest
    {
        [TestCase("none", true, JudgementType.Ok, HitResult.Ok)]
        [TestCase("easy", true, JudgementType.Great, HitResult.Great)]
        [TestCase("dt", true, JudgementType.Great, HitResult.Great)]
        [TestCase("hr", true, JudgementType.Ok, HitResult.Ok)]
        [TestCase("hr", false, JudgementType.Meh, HitResult.Meh)]
        public void LowStarWindowsComposeWithModsAndAgreeInLivePlayAndReplay(
            string modName, bool unhalvedHardRock, JudgementType judgement, HitResult result)
        {
            Mod[] mods = modName switch
            {
                "easy" => new Mod[] { new TypeBeatModEasy() },
                "dt" => new Mod[] { new TypeBeatModDoubleTime() },
                "hr" => new Mod[] { new TypeBeatModHardRock() },
                _ => Array.Empty<Mod>(),
            };
            var beatmap = new TypeBeatBeatmap();
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 0,
                LineIndex = 0,
                Granularity = TimingGranularity.Line,
                Line = new LyricLine
                {
                    RawText = "abc",
                    StartTime = 0,
                    EndTime = 20000,
                    SingEndTime = 12000,
                    Units = new[] { new TimedUnit { Text = "abc", StartTime = 0, EndTime = 12000 } },
                },
            });
            beatmap.HitObjects[0].ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            using var drawable = new DrawableTypeBeatRuleset(new TypeBeatRuleset(), beatmap, mods);
            foreach (var mod in mods.OfType<IApplicableToDrawableRuleset<TypeBeatHitObject>>())
                mod.ApplyToDrawableRuleset(drawable);

            TypingEngine engine = drawable.Engine;
            Assert.That(engine.DifficultyWindowScale, Is.EqualTo(2), "a sparse low-star map gets twice the base tolerance");
            var seen = new List<JudgementType>();
            engine.CharJudged += j => seen.Add(j.Type);

            var replay = new Replay();
            var header = TypeBeatReplayFrame.CreateConfigFrame(0, true, unhalvedHardRockWindows: unhalvedHardRock);
            replay.Frames.Add(header);
            ReplayEngineFeed.Apply(engine, header);

            // 400 ms late is Ok on the low-star base ladder, Great with Easy or DT,
            // and Meh on the old HR ladder. This distinguishes every scaling arm.
            for (int i = 0; i < 3; i++)
            {
                var frame = new TypeBeatReplayFrame(i * 4000 + 400, "abc"[i]);
                replay.Frames.Add(frame);
                engine.Update(frame.Time);
                Assert.That(engine.ProcessKey(frame.Character, frame.Time), Is.True);
            }

            var account = TypeBeatReplayScorer.Score(beatmap, mods, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            Assert.That(seen, Is.EqualTo(Enumerable.Repeat(judgement, 3)));
            Assert.That(account.Statistics.GetValueOrDefault(result), Is.EqualTo(3));
            Assert.That(account.Statistics.GetValueOrDefault(HitResult.Miss), Is.Zero);
            Assert.That(account.UnconsumedFrames, Is.Zero);
        }
    }
}
