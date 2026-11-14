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
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 463: a WRONG KEY's letter pops up beside the caret in red, jumps and falls away, in
    /// base gameplay exactly as under Gatekeeper. Gatekeeper rejects the key, so the popup is all the
    /// player ever sees of it; base gameplay types it through into the cell, which shows the expected
    /// letter in red and never what was pressed, so the popup is what tells the player that.
    /// </summary>
    public partial class TestSceneTypeBeatWrongKeyLetter : OsuTestScene
    {
        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();

        private void createRuleset(params Mod[] mods)
        {
            AddStep("create drawable ruleset", () =>
            {
                var ruleset = new TypeBeatRuleset();
                var line = new LyricLine
                {
                    RawText = "hello world",
                    StartTime = 0,
                    EndTime = 600000,
                    SingEndTime = 300000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "hello", StartTime = 0, EndTime = 150000 },
                        new TimedUnit { Text = "world", StartTime = 150000, EndTime = 300000 },
                    },
                };

                var beatmap = new Beatmap
                {
                    HitObjects = new List<Rulesets.Objects.HitObject>
                    {
                        new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word },
                    },
                };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, mods);
                Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable, mods);
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
        }

        [Test]
        public void ATypedThroughTypoPopsUpTheLetterPressed()
        {
            createRuleset();

            AddStep("type a correct letter", () => engine.ProcessKey('h', 10));
            AddAssert("nothing pops up for it", () => !stage.WrongKeyLetters.Any());

            // 'e' is expected; 'q' is typed through into the cell, which shows the 'e' in red.
            AddStep("type a wrong letter", () => engine.ProcessKey('q', 20));
            AddAssert("the cell holds the wrong key", () => engine.Lines[0].Cells[1].TypedChar == 'q');
            AddAssert("the letter pressed pops up", () => stage.WrongKeyLetters.SequenceEqual(new[] { "q" }));
            AddUntilStep("and falls away", () => !stage.WrongKeyLetters.Any());
        }

        [Test]
        public void GatekeeperStillPopsUpTheRejectedLetter()
        {
            createRuleset(new TypeBeatModGatekeeper());

            AddStep("type a wrong letter", () => engine.ProcessKey('z', 10));
            AddAssert("rejected: the caret stays", () => engine.Lines[0].Cells[0].TypedChar == null);
            AddAssert("the letter pressed pops up", () => stage.WrongKeyLetters.SequenceEqual(new[] { "z" }));
        }
    }
}
