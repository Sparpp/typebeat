// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JapaneseMinimumAutoplayTest
    {
        [TestCase("tsuki", "月", 4)]
        [TestCase("shi", "し", 2)]
        [TestCase("ten", "天", 3)]
        [TestCase("tenshi", "天使", 5)]
        [TestCase("chou", "蝶", 4)]
        [TestCase("gakkou", "学校", 6)]
        [TestCase("ten ten", "天 天", 6)]
        public void AutoplayUsesMinimumRomajiAndStillPlaysPerfectly(string romaji, string original, int minimum)
        {
            var words = romaji.Split(' ');
            var originals = original.Split(' ');
            var map = OriginalTextFormatTest.BuildBeatmap(new[] { new LyricLine
            {
                RawText = romaji, Original = original, StartTime = 0, EndTime = 6000, SingEndTime = 4000,
                Units = words.Select((word, i) => new TimedUnit
                {
                    Text = word, Original = originals[i], StartTime = 1000 + 1500 * i, EndTime = 2400 + 1500 * i,
                }).ToArray(),
            } });
            map.BeatmapInfo.Metadata.Language = BeatmapLanguage.Japanese;
            var polyglot = new TypeBeatModPolyglot();
            polyglot.ApplyToBeatmap(map);
            foreach (var hit in map.HitObjects)
                hit.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());
            var replay = new TypeBeatAutoGenerator(map, authoredSyllablesOnly: true, alignSubdivisionTargets: true).Generate();
            Assert.That(replay.Frames.OfType<TypeBeatReplayFrame>().Where(f => f.IsConfigExtended).Select(f => f.JapaneseInputEra3), Is.Not.Empty.And.All.EqualTo(true));
            var keys = replay.Frames.OfType<TypeBeatReplayFrame>().Where(f => !f.IsConfig && !f.IsConfigExtended).ToArray();
            Assert.That(keys.Count(f => f.Character != ' '), Is.EqualTo(minimum));
            Assert.That(keys.Where(f => f.Character != ' ').All(f => f.Character <= 127), Is.True);
            var account = TypeBeatReplayScorer.Score(map, new Mod[] { polyglot }, replay, TypoRule.Deferred, ComboRestoreRule.OnFix);
            Assert.That(account.Accuracy, Is.EqualTo(1));
            Assert.That(account.MaxCombo, Is.EqualTo(minimum));
            Assert.That(account.MaximumStatistics[HitResult.Great], Is.EqualTo(minimum));
            Assert.That(account.Statistics[HitResult.Great], Is.EqualTo(minimum));
        }
    }
}
