// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 251: the SYNC METRIC is gone from the gameplay UI by default and comes back only for
    /// a player who asks for it (<see cref="TypeBeatRulesetSetting.ShowSyncMetric"/>, off). This
    /// fixture pins the HUD half of that switch, the "sync" column beside wpm; the lyric-line half,
    /// the sync tint, is pinned by <c>TestSceneTypeBeatSyncTint</c>.
    ///
    /// <para>What makes the toggle worth pinning rather than trusting is that it is the ONLY route
    /// back to a readout the game used to ship on. A wiring break in either direction is invisible
    /// in normal play: broken-off looks like the shipped default, and broken-on looks like the old
    /// game. Both directions are therefore driven here, live, on one HUD.</para>
    ///
    /// <para>The figure itself is NOT re-derived here. Whether it is right is
    /// <c>TypingEngineTest</c>'s and <c>UntimedSpaceTest</c>'s subject, and since 251 it decides
    /// nothing anyway (the letter grade reads accuracy alone); all that is left for a display test
    /// is whether the column is on screen and, when it is, that it is showing the engine's number
    /// rather than the placeholder every stat is built with.</para>
    /// </summary>
    public partial class TestSceneTypeBeatSyncMetric : OsuTestScene
    {
        private const string text = "abcd";

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private TypeBeatHudOverlay hud => drawableRuleset.ChildrenOfType<TypeBeatHudOverlay>().Single();

        // The same cached-per-ShortName manager the gameplay bindings read, so setting a value here
        // drives the live HUD exactly as ticking the settings checkbox would.
        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        private void setSyncMetric(bool enabled)
            => AddStep($"sync metric {(enabled ? "on" : "off")}", () => config.SetValue(TypeBeatRulesetSetting.ShowSyncMetric, enabled));

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create drawable ruleset", () =>
            {
                var ruleset = new TypeBeatRuleset();

                var line = new LyricLine
                {
                    RawText = text,
                    StartTime = 0,
                    EndTime = 600000,
                    SingEndTime = 300000,
                    Units = new[] { new TimedUnit { Text = text, StartTime = 0, EndTime = 300000 } },
                };

                var beatmap = new Beatmap
                {
                    HitObjects = new List<Rulesets.Objects.HitObject>
                    {
                        new TypeBeatHitObject
                        {
                            StartTime = line.StartTime,
                            LineIndex = 0,
                            Line = line,
                            Granularity = TimingGranularity.Word,
                        },
                    },
                };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());

                Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable);
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
        }

        /// <summary>
        /// The shipped state, which is the whole point of the task: a player who has never opened
        /// the settings panel has no sync column at all. Written explicitly rather than inherited
        /// from the fresh config, because the manager is cached across the whole fixture and the
        /// test below leaves it on; the DEFAULT itself is pinned in <c>ConfigDefaultsTest</c>, where
        /// no live toggle can have moved it.
        /// </summary>
        [Test]
        public void TestTheSyncColumnIsAbsentWhenTheMetricIsOff()
        {
            setSyncMetric(false);

            AddUntilStep("sync column hidden", () => !hud.SyncReadoutVisible);

            // The engine is still computing it, which is what keeps this a display switch: nothing
            // about the play is different, the HUD is simply not showing one of its numbers.
            AddAssert("the engine still has the figure", () => engine.LiveSyncPercent, () => Is.EqualTo(100).Within(1e-9));
        }

        /// <summary>
        /// The opt-in arm, driven live: ticking the box mid-play brings the column back without a
        /// restart (it is a bound setting, not a load-time read), and the column reads the engine
        /// rather than the "0" placeholder <c>stat</c> builds every readout with. Nothing is typed,
        /// so the live figure is the 100 an unresolved line reports, and the format is the one the
        /// HUD has always used.
        /// </summary>
        [Test]
        public void TestTurningTheMetricOnBringsTheSyncColumnBack()
        {
            setSyncMetric(false);
            AddUntilStep("hidden to start", () => !hud.SyncReadoutVisible);

            setSyncMetric(true);

            AddUntilStep("sync column shown", () => hud.SyncReadoutVisible);
            AddUntilStep("and reading the engine, not the placeholder", () => hud.SyncReadoutText == "100.0%");

            // Back off again in the same play: the switch is symmetric, so a player who tries it and
            // dislikes it is not stuck with it for the rest of the map.
            setSyncMetric(false);
            AddUntilStep("hidden again", () => !hud.SyncReadoutVisible);
        }
    }
}
