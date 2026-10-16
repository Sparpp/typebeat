// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Input;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The Polyglot mod (backlog 331) through a real Player, on the precedent a non-user-playable mod
    /// sets in <c>SubmittingPlayer</c>: a play carrying it never asks for a submission token, never
    /// submits, and is still imported into the local score list (with its replay) once it completes.
    /// The same play without the mod asks for the token, which is what keeps the first pin honest.
    /// </summary>
    public partial class TestSceneTypeBeatPolyglot : PlayerTestScene
    {
        protected override bool HasCustomSteps => true;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        protected override TestPlayer CreatePlayer(Ruleset ruleset) => new ImportRecordingPlayer();

        private ImportRecordingPlayer player => (ImportRecordingPlayer)Player;

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "Song";

            if (finishedLineMap)
                return createFinishedLineBeatmap(beatmap);

            var line = new LyricLine
            {
                RawText = "ab",
                StartTime = 0,
                EndTime = 1200,
                SingEndTime = 1200,
                Units = new[] { new TimedUnit { Text = "ab", StartTime = 0, EndTime = 1200, Original = "αβ" } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word });
            return beatmap;
        }

        [Test]
        public void TestAPolyglotPlayStaysOnThisDevice()
        {
            loadWith(new TypeBeatModPolyglot());

            AddAssert("the engine plays the original", () => ((TypeBeatPlayfield)Player.DrawableRuleset.Playfield).Engine.Lines[0].DisplayText == "αβ");
            AddAssert("no submission token was requested", () => !Player.TokenCreationRequested);

            AddUntilStep("the play completes", () => Player.ScoreProcessor.HasCompleted.Value);
            AddUntilStep("the play was saved locally", () => player.ImportedScore != null);
            AddAssert("with the mod on it", () => player.ImportedScore!.ScoreInfo.Mods.Any(m => m is TypeBeatModPolyglot));
            AddAssert("and nothing was submitted", () => Player.SubmittedScore == null);
        }

        [Test]
        public void TestTheSamePlayWithoutTheModAsksForAToken()
        {
            loadWith();

            AddUntilStep("a submission token was requested", () => Player.TokenCreationRequested);
        }

        /// <summary>
        /// Backlog 383 moved Latin plays onto text input too, with the IME OFF; a Polyglot play keeps
        /// the activation it always had, plain text with the IME allowed, and its committed text is
        /// still typed in its own script, composition and all.
        /// </summary>
        [Test]
        public void TestPolyglotKeepsItsImeActivationAndItsScript()
        {
            loadWith(new TypeBeatModPolyglot());

            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0);
            AddAssert("text input is active as plain text with the IME allowed", () =>
                os.ActiveProperties, () => Is.EqualTo(new TextInputProperties(TextInputType.Text, true)));

            AddStep("compose, then commit 'α' through the IME", () =>
            {
                os.TriggerImeComposition("α", 1, 0);
                os.TriggerImeResult("α");
            });
            AddUntilStep("the original-script cell took it", () => engine.CaretIndex == 1);
        }

        [Test]
        public void TestALatinPlayActivatesWithTheImeOff()
        {
            loadWith();

            AddUntilStep("text input is active as Code with the IME disallowed", () =>
                os.ActiveProperties?.Equals(new TextInputProperties(TextInputType.Code, AllowIme: false)) == true);
        }

        /// <summary>
        /// The finished-line gate the Latin path has always had, on the IME path (G2-007): a commit on
        /// a typed-out line is a letter only as the typed-through newline, which rides the manual
        /// newline setting. With it on the commit hands the line on (the next line awaits its window,
        /// so the move is kept and the letter dropped, as on the Latin path); with it off the commit
        /// is inert and records nothing.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public void TestAnImeCommitOnAFinishedLineIsOnlyTheTypedThroughNewline(bool manualNewlines)
        {
            AddStep("the finished-line map", () => finishedLineMap = true);
            AddStep($"manual newlines {(manualNewlines ? "on" : "off")}", () => config.SetValue(TypeBeatRulesetSetting.ManualNewlines, manualNewlines));
            loadWith(new TypeBeatModPolyglot());

            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0);
            AddStep("commit 'αβ' through the IME", () => os.TriggerImeResult("αβ"));
            AddUntilStep("line 0 is typed out", () => engine.IsLineComplete);

            AddStep("commit 'γ' through the IME", () => os.TriggerImeResult("γ"));

            if (manualNewlines)
            {
                AddUntilStep("the commit handed the line on", () => engine.ActiveLineIndex == 1);
                AddAssert("the next line's first cell took nothing yet", () => engine.CaretIndex == 0 && engine.Lines[1].Cells[0].State == CellState.Untyped);
                AddAssert("the newline was recorded as the letter", () => keystrokes.Count == 3 && keystrokes[2].Character == 'γ');
            }
            else
            {
                AddWaitStep("let the commit land", 5);
                AddAssert("the line was not handed on", () => engine.ActiveLineIndex == 0 && engine.IsLineComplete);
                AddAssert("nothing was recorded for it", () => keystrokes.Count == 2);
            }
        }

        /// <summary>Whether the next load uses <see cref="createFinishedLineBeatmap"/> rather than the default map.</summary>
        private bool finishedLineMap;

        public override void SetUpSteps()
        {
            AddStep("the default map", () => finishedLineMap = false);
            AddStep("manual newlines at their default", () => config.GetBindable<bool>(TypeBeatRulesetSetting.ManualNewlines).SetDefault());
            base.SetUpSteps();
        }

        /// <summary>"ab" (played as "αβ") with a window long enough to type it out, and a far-away "cd" ("γδ") whose entry stays shut through the test.</summary>
        private static IBeatmap createFinishedLineBeatmap(Beatmap beatmap)
        {
            var line = new LyricLine
            {
                RawText = "ab",
                StartTime = 0,
                EndTime = 300000,
                SingEndTime = 200000,
                Units = new[] { new TimedUnit { Text = "ab", StartTime = 0, EndTime = 200000, Original = "αβ" } },
            };

            var trailing = new LyricLine
            {
                RawText = "cd",
                StartTime = 600000,
                EndTime = 900000,
                SingEndTime = 800000,
                Units = new[] { new TimedUnit { Text = "cd", StartTime = 700000, EndTime = 800000, Original = "γδ" } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = line.StartTime, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word });
            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = trailing.StartTime, LineIndex = 1, Line = trailing, Granularity = TimingGranularity.Word });
            return beatmap;
        }

        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        /// <summary>The recorded keystrokes, without the CONFIG header frames the recorder stamps ahead of the first one.</summary>
        private List<TypeBeatReplayFrame> keystrokes => Player.GameplayState.Score.Replay.Frames.OfType<TypeBeatReplayFrame>().Where(f => !f.IsConfig && !f.IsConfigExtended).ToList();

        private EmulatedTextInput os => ((KeycapManualInputManager)InputManager).TextInput;

        private TypingEngine engine => ((TypeBeatPlayfield)Player.DrawableRuleset.Playfield).Engine;

        private void loadWith(params Mod[] mods)
        {
            AddStep($"load with [{string.Join(' ', mods.Select(m => m.Acronym))}]", () => LoadPlayer(mods));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
        }

        /// <summary>A player that shows results (so the completed play is imported) and records the import instead of writing the realm.</summary>
        private partial class ImportRecordingPlayer : TestPlayer
        {
            public Score? ImportedScore { get; private set; }

            public ImportRecordingPlayer()
                : base(false, true)
            {
            }

            protected override Task ImportScore(Score score)
            {
                ImportedScore = score;
                return Task.CompletedTask;
            }
        }
    }
}
