// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Play;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The in-gameplay retry buttons (fail overlay, pause overlay) must request a *quick* restart, so a
    /// retry of the same map with the same mods relaunches straight into gameplay through the shortened
    /// <see cref="PlayerLoader"/> quick-restart sequence instead of the full loading interstitial
    /// (metadata display, disclaimers, intro). We capture the flag passed to
    /// <see cref="Player.Restart"/> via <see cref="Player.PrepareLoaderForRestart"/>, which the loader
    /// wires up and which <see cref="Player.Restart"/> invokes with the requested quick-restart value.
    /// </summary>
    public partial class TestSceneTypeBeatQuickRetry : PlayerTestScene
    {
        // The fail overlay's retry button only gets wired when the play is allowed to fail.
        protected override bool AllowFail => true;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        protected override TestPlayer CreatePlayer(Ruleset ruleset) => new ExposedPlayer();

        private ExposedPlayer exposedPlayer => (ExposedPlayer)Player;

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

        [Test]
        public void TestFailOverlayRetryRequestsQuickRestart()
            => assertRetryRequestsQuickRestart("fail overlay", () => exposedPlayer.FailOverlayRetry);

        [Test]
        public void TestPauseOverlayRetryRequestsQuickRestart()
            => assertRetryRequestsQuickRestart("pause overlay", () => exposedPlayer.PauseOverlayRetry);

        private void assertRetryRequestsQuickRestart(string source, Func<Action?> retryAction)
        {
            bool? quickRestartRequested = null;

            AddStep("hook restart flag", () =>
            {
                // Stand in for the PlayerLoader, which sets this delegate on the player it hosts.
                Player.PrepareLoaderForRestart = quick => quickRestartRequested = quick;
            });

            AddAssert($"{source} retry is wired", () => retryAction() != null);

            AddStep($"invoke {source} retry", () => retryAction()!.Invoke());

            AddAssert("quick restart requested", () => quickRestartRequested == true);
        }

        private partial class ExposedPlayer : TestPlayer
        {
            public ExposedPlayer()
                : base(allowPause: true, showResults: true)
            {
            }

            public Action? FailOverlayRetry => FailOverlay?.OnRetry;

            public Action? PauseOverlayRetry => PauseOverlay?.OnRetry;
        }
    }
}
