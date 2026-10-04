// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Colour;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;
using typebeat.Game.Tests.Visual;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 363 on screen: the lyric stack follows the grouping the ENGINE judges on. Live, "banana"
    /// (nothing subdivided) is one group, so it carries no mark, the underline is cut at the word gap
    /// alone and the whole word lights while it is sung. Attaching a replay stored before 363 (a CONFIG
    /// frame and no extended header) flips the engine onto the natural grouping, and the stack RE-LAYS
    /// itself: the syllabifier's ba|na|na marks appear, the rail is cut at them, and only the "ba" being
    /// sung lights. Detaching (the editor's autoplay toggle handing the play back) restores the live
    /// grouping and all three.
    /// </summary>
    public partial class TestSceneTypeBeatGroupingEra : OsuTestScene
    {
        // cells: b0 a1 n2 a3 n4 a5 _6 c7 a8 k9 e10. Natural: ba|na|na, marks at 2 and 4.
        private const string text = "banana cake";

        private static readonly int[] natural_marks = { 2, 4 };

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();
        private LyricLineDisplay display => stage.DisplayAt(0)!;

        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        private bool lit(int cell) => ((ColourInfo)TypeBeatStyle.SungCharForBrightness(60)).Equals((ColourInfo)display.CellColour(cell).TopLeft.SRGB);

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("markers on, default playhead", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.ShowSyllableMarkers, true);
                config.SetValue(TypeBeatRulesetSetting.SungCaretStyle, TypeBeatRulesetConfigManager.DEFAULT_SUNG_CARET_STYLE);
            });

            AddStep("create drawable ruleset", () =>
            {
                var ruleset = new TypeBeatRuleset();

                var line = new LyricLine
                {
                    RawText = text,
                    StartTime = 0,
                    EndTime = 600000,
                    SingEndTime = 300000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "banana", StartTime = 0, EndTime = 150000 },
                        new TimedUnit { Text = "cake", StartTime = 150000, EndTime = 300000 },
                    },
                };

                var beatmap = new Beatmap
                {
                    HitObjects = new List<Rulesets.Objects.HitObject>
                    {
                        new TypeBeatHitObject { StartTime = line.StartTime, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word },
                    },
                };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());

                Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable);
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
        }

        [Test]
        public void TestAttachingAStoredReplayReLaysTheNaturalGroupingAndDetachingRestoresTheLiveOne()
        {
            AddAssert("live: authored syllables only", () => engine.AuthoredSyllablesOnly);
            assertLive("live");

            AddStep("attach a replay stored before backlog 363", () =>
            {
                var replay = new Replay();
                replay.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, syllableTiming: true, charTimedStretch: true, firstCharTiming: true));
                drawableRuleset.SetReplayScore(new Score { Replay = replay });
            });

            AddUntilStep("the engine is on the stored era", () => !engine.AuthoredSyllablesOnly);
            AddAssert("the display shows the natural grouping", () => display.Grouping, () => Is.SameAs(display.Line.NaturalGrouping));
            AddAssert("a drawable per natural mark", () => display.SyllableMarkerCount, () => Is.EqualTo(natural_marks.Length));
            AddUntilStep("the natural marks are drawn", () => natural_marks.All(display.SyllableMarkerVisibleAt));
            AddAssert("the rail is cut at them", () => display.PaceTrackCount, () => Is.EqualTo(4));
            AddUntilStep("only the sung 'ba' lights", () => lit(0) && lit(1) && !lit(3));

            AddStep("detach", () => drawableRuleset.SetReplayScore(null!));
            AddUntilStep("the live era is back", () => engine.AuthoredSyllablesOnly);
            assertLive("after detaching");
        }

        private void assertLive(string when)
        {
            AddAssert($"{when}: the display shows the authored grouping", () => display.Grouping, () => Is.SameAs(display.Line.AuthoredGrouping));
            AddUntilStep($"{when}: no mark", () => display.SyllableMarkerCount == 0);
            AddAssert($"{when}: the rail is cut at the word gap alone", () => display.PaceTrackCount, () => Is.EqualTo(2));
            AddUntilStep($"{when}: the whole word lights", () => lit(0) && lit(3) && lit(5) && !lit(7));
        }
    }
}
