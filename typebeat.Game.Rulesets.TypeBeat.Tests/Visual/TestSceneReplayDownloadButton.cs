// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Ranking;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Covers the replay button's enablement for an ONLINE score row, which is the surface backlog 37
    /// was reported against ("no replay available in client").
    ///
    /// <para>
    /// A leaderboard row carries no files, so its only claim to a replay is the server's additive
    /// <c>has_replay</c> flag (absent on an old server, read as false) or a matching local score. With
    /// neither, the button must say so and stay disabled; with the flag set, it must become clickable
    /// and offer the download.
    /// </para>
    /// </summary>
    [TestFixture]
    public partial class TestSceneReplayDownloadButton : OsuTestScene
    {
        // Deliberately far outside anything a real play would produce, so the local-availability
        // subscription can never accidentally match a score left in the test runner's store.
        private const long unmatched_online_id = 987_654_321;

        private ReplayDownloadButton button = null!;

        private DownloadButton innerButton => this.ChildrenOfType<DownloadButton>().Single();

        private static ScoreInfo onlineRow(bool hasOnlineReplay) => new ScoreInfo
        {
            OnlineID = unmatched_online_id,
            BeatmapInfo = new BeatmapInfo(),
            Ruleset = new TypeBeatRuleset().RulesetInfo,
            HasOnlineReplay = hasOnlineReplay,
        };

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create button", () => Child = button = new ReplayDownloadButton(onlineRow(false))
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Scale = new osuTK.Vector2(3),
            });

            AddUntilStep("button loaded", () => this.ChildrenOfType<DownloadButton>().Any());
        }

        /// <summary>
        /// Absent <c>has_replay</c> deserialises to false, and nothing local matches: there is genuinely
        /// nothing to watch, and the button must not pretend otherwise.
        /// </summary>
        [Test]
        public void TestNoReplayAnywhereDisablesTheButton()
        {
            AddUntilStep("button disabled", () => !innerButton.Enabled.Value);
            AddAssert("tooltip reports unavailable", () => innerButton.TooltipText.ToString() == "replay unavailable");
        }

        /// <summary>
        /// The server reporting a stored replay is enough on its own; the row has no local files.
        /// </summary>
        [Test]
        public void TestServerReplayEnablesTheButton()
        {
            AddStep("server reports a replay", () => button.Score.Value = onlineRow(true));

            AddUntilStep("button enabled", () => innerButton.Enabled.Value);
            AddAssert("tooltip offers the download", () => innerButton.TooltipText.ToString() == "download replay");
        }

        /// <summary>
        /// Flipping the flag back must re-disable, so a stale enabled state cannot leak between the
        /// scores a results screen cycles through.
        /// </summary>
        [Test]
        public void TestEnablementTracksTheFlag()
        {
            AddStep("server reports a replay", () => button.Score.Value = onlineRow(true));
            AddUntilStep("button enabled", () => innerButton.Enabled.Value);

            AddStep("server reports none", () => button.Score.Value = onlineRow(false));
            AddUntilStep("button disabled again", () => !innerButton.Enabled.Value);
        }
    }
}
