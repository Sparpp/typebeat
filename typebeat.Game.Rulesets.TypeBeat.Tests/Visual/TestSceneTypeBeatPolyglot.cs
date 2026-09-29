// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
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
