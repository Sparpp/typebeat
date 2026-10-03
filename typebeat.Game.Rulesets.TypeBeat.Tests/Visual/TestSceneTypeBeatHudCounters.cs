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
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 389: the "Typing counters" skin layer ships exactly TWO readouts, the wpm counter and
    /// the pp counter. The sync column and the rate column were invisible in ordinary play (the sync
    /// column only ever appeared behind the off-by-default <c>ShowSyncMetric</c> setting; the rate
    /// column only under a follower mod) yet each was still a real <c>ISerialisableDrawable</c> on
    /// the layer, so the skin editor listed, selected and serialised a component the player could
    /// never see, and a skin saved from the editor enshrined them. Both classes are gone; this
    /// fixture pins the layer's component set so one cannot quietly return.
    ///
    /// <para>The sync TINT that paints a correctly typed character lives on its own and is pinned by
    /// <c>TestSceneTypeBeatSyncTint</c>; nothing here covers it. The metric figure itself is
    /// <c>TypingEngineTest</c>'s subject and is unaffected, since the engine still computes it
    /// whether or not any readout shows it.</para>
    /// </summary>
    public partial class TestSceneTypeBeatHudCounters : OsuTestScene
    {
        private const string text = "abcd";

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypeBeatHudOverlay hud => drawableRuleset.ChildrenOfType<TypeBeatHudOverlay>().Single();

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

            AddUntilStep("counters layer loaded", () => hud.ComponentsLoaded);
        }

        [Test]
        public void TestTheCountersLayerIsOnlyWpmAndPp()
        {
            AddAssert("exactly two counters", () => hud.Components.Count == 2);
            AddAssert("wpm counter present", () => hud.Components.OfType<TypeBeatWpmCounter>().Count() == 1);
            AddAssert("pp counter present", () => hud.Components.OfType<TypeBeatPpCounter>().Count() == 1);

            // The removed classes, by name, so a reintroduction fails here even if it is wired nowhere.
            AddAssert("no sync readout class", () => AppDomain.CurrentDomain.GetAssemblies()
                                                               .SelectMany(a => a.GetTypes())
                                                               .All(t => t.Name != "TypeBeatSyncCounter" && t.Name != "TypeBeatRateCounter"));
        }
    }
}
