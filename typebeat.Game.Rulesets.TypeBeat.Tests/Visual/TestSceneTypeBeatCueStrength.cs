// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The first-word cue's STRENGTH says whose line it is. While the line it counts in is still to
    /// come - nobody is on it at all - the bar is the 50%-opaque notice it has always been. The moment
    /// the line becomes the player's own (the caret is handed it, or it self-activated into its own
    /// lead-in after a gap) the SAME bar steps up to full strength: it is no longer a hint about a line
    /// that cannot be typed, it is the line under the caret.
    ///
    /// <para>The map is one line with its boundary set LATE - [2600, 4600) with the vocals beginning at
    /// 3200 - which is what puts the two states inside a single cue window: the cue opens 1500 ms
    /// before its first word at 1700, while the line does not activate until its boundary at 2600, so
    /// [1700, 2600) is a cue for a line nobody is on and [2600, 3200) is the same cue for the player's
    /// own line.</para>
    /// </summary>
    public partial class TestSceneTypeBeatCueStrength : PlayerTestScene
    {
        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;
        private TypingEngine engine => playfield.Engine;
        private LyricStage stage => Player.ChildrenOfType<LyricStage>().Single();

        private double gameplayTime => stage.Clock.CurrentTime;

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "CueStrength";

            var line = new LyricLine
            {
                RawText = "ab",
                StartTime = 2600,
                EndTime = 4600,
                SingEndTime = 4600,
                Units = new[] { new TimedUnit { Text = "ab", StartTime = 3200, EndTime = 4600 } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 2600,
                LineIndex = 0,
                Line = line,
                Granularity = TimingGranularity.Line,
            });

            return beatmap;
        }

        [Test]
        public void TestCueIsStrongerOnceTheLineIsThePlayers()
        {
            AddStep("load player with no mods", () => LoadPlayer());
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("gameplay started", () => Player.GameplayClockContainer.CurrentTime > 0);

            // BEFORE the line's boundary: the cue is up and counting the line in, and nobody is on that
            // line yet, so it is the dim notice. Full strength would be >= 0.5; the scale here is 0.5.
            AddUntilStep("the cue is up while the line is still nobody's", () =>
                gameplayTime > 1750 && gameplayTime < 2550
                && engine.ActiveLineIndex == -1
                && stage.ApproachCueTargetLine == 0
                && stage.ApproachCueVisible);
            AddAssert("and it is the dim one", () =>
                gameplayTime < 2550 && stage.FirstWordCueAlpha < 0.45f);

            // The boundary hands the line over mid-window: the caret is on it, and the bar rises to full
            // strength rather than staying the same whisper now that it marks the player's own line.
            AddUntilStep("the line becomes the player's, cue still up", () =>
                engine.ActiveLineIndex == 0
                && stage.ApproachCueTargetLine == 0
                && stage.FirstWordCueAlpha >= 0.5f
                && gameplayTime < 3200);
        }
    }
}
