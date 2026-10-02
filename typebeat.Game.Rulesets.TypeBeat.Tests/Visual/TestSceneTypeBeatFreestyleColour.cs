// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The map's own freestyle colour (backlog 384) reaching gameplay: a beatmap carrying
    /// <see cref="IBeatmap.FreestyleColour"/> is converted, played, and its freestyle slot wears that
    /// colour open and filled while the ordinary cells do not; a map carrying none keeps the default.
    /// </summary>
    public partial class TestSceneTypeBeatFreestyleColour : OsuManualInputManagerTestScene
    {
        private const string text = "ab&cd"; // cell 2 is the freestyle slot
        private const int slot = 2;

        private static readonly Colour4 teal = new Colour4((byte)0x12, (byte)0xab, (byte)0xef, (byte)255);

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricLineDisplay display => drawableRuleset.ChildrenOfType<LyricStage>().Single().DisplayAt(0)!;

        [Test]
        public void TestMapColourIsWornOpenAndFilled()
        {
            create(teal);

            AddAssert("the playable map kept the colour through conversion", () => drawableRuleset.Beatmap.FreestyleColour == teal);
            AddAssert("open slot wears the map colour", () => display.CellColour(slot).Equals((ColourInfo)teal));
            AddAssert("ordinary cells do not", () => !display.CellColour(0).Equals((ColourInfo)teal));

            AddStep("type a, b, x", () =>
            {
                InputManager.Key(Key.A);
                InputManager.Key(Key.B);
                InputManager.Key(Key.X);
            });
            AddAssert("slot filled", () => engine.Lines[0].Cells[slot].State == CellState.Correct);
            AddAssert("still the map colour once filled", () => display.CellColour(slot).Equals((ColourInfo)teal));
        }

        [Test]
        public void TestMapWithoutAColourKeepsTheDefault()
        {
            create(null);

            AddAssert("open slot wears the default", () => display.CellColour(slot).Equals((ColourInfo)TypeBeatStyle.FreestyleChar));
        }

        private void create(Colour4? colour)
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
                        new TypeBeatHitObject { StartTime = line.StartTime, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word },
                    },
                    FreestyleColour = colour,
                };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());

                Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable);
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
        }
    }
}
