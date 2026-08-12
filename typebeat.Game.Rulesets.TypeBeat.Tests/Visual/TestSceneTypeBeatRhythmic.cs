// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Rhythmic (backlog 135) end to end through the real mod pipeline: selected in the mod list,
    /// carried through Player, and landing on the engine the playfield judges with. That is the
    /// FIRST of the two places the millisecond ladder is selected; the second is
    /// <c>TypeBeatReplayScorer.createEngine</c>, pinned in <c>TypeBeatReplayScorerTest</c>.
    ///
    /// <para>The property has to be in place before the first keypress, because judgements already
    /// awarded are never revisited. <c>IApplicableToDrawableRuleset</c> runs while the ruleset
    /// loads, long before a line is active, which is what these steps assert by checking the measure
    /// the moment the line opens.</para>
    /// </summary>
    public partial class TestSceneTypeBeatRhythmic : PlayerTestScene
    {
        protected override bool HasCustomSteps => true;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;
        private TypingEngine engine => playfield.Engine;

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var line = new LyricLine
            {
                RawText = "ab",
                StartTime = 0,
                EndTime = 600000,
                SingEndTime = 300000,
                Units = new[] { new TimedUnit { Text = "ab", StartTime = 0, EndTime = 300000 } },
            };

            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>
                {
                    new TypeBeatHitObject
                    {
                        StartTime = 0,
                        LineIndex = 0,
                        Line = line,
                        Granularity = TimingGranularity.Line,
                    },
                },
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            return beatmap;
        }

        private void loadWith(params Mod[] mods)
        {
            AddStep($"load player with {(mods.Length == 0 ? "no mods" : string.Join(" + ", System.Linq.Enumerable.Select(mods, m => m.Acronym)))}",
                () => LoadPlayer(mods));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("line active", () => engine.ActiveLineIndex == 0);
        }

        [Test]
        public void TestModPutsTheEngineOnTheMillisecondLadder()
        {
            loadWith(new TypeBeatModRhythmic());

            AddAssert("engine judges in milliseconds", () => engine.Measure == SyncMeasure.Milliseconds);

            // And the windows it hands the cells are the millisecond ones, at this map's Line
            // granularity: the pre-backlog-133 Great/Ok/Meh rows, plus the top row that subdivides
            // the old top window.
            AddAssert("the millisecond windows are live", () =>
                engine.Windows.Measure == SyncMeasure.Milliseconds
                && engine.Windows.PerfectLate == 200
                && engine.Windows.GreatLate == 400
                && engine.Windows.OkLate == 1000
                && engine.Windows.MehLate == 2000);
        }

        /// <summary>Without the mod nothing moves: the default play is still on the character axis.</summary>
        [Test]
        public void TestDefaultPlayStaysOnTheCharacterLadder()
        {
            loadWith();

            AddAssert("engine judges in character distances", () => engine.Measure == SyncMeasure.CharacterDistance);
            AddAssert("the character windows are live", () =>
                engine.Windows.Measure == SyncMeasure.CharacterDistance
                && engine.Windows.PerfectLate == 2.00);
        }
    }
}
