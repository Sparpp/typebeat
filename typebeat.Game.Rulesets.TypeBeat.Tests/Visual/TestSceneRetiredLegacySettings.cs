// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Login;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Play.HUD;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// What is left behind where backlog 483 removed a legacy osu! setting that changed what the player
    /// sees: the value it is pinned to is the one shown, and the control that shared its home still works.
    /// </summary>
    public partial class TestSceneRetiredLegacySettings : OsuTestScene
    {
        [Resolved]
        private ScoreManager scoreManager { get; set; } = null!;

        // The login panel's user card wants the colours its overlay normally provides.
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

        /// <summary>
        /// "Score display mode" is gone and scores always show Standardised, the total the server stores.
        /// Classic was osu!standard's conversion (type!beat claims ruleset ID 0), which for a score with no
        /// counted objects came out at a tenth of the total and grew with the square of the object count,
        /// so a score shown as anything but its own total is a conversion coming back.
        /// </summary>
        [Test]
        public void TestScoresShowTheirStoredTotal()
        {
            ScoreProcessor processor = null!;
            DefaultScoreCounter counter = null!;

            AddStep("a gameplay score counter", () =>
            {
                processor = new ScoreProcessor(new TypeBeatRuleset());

                Child = new DependencyProvidingContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    CachedDependencies = new (Type, object)[] { (typeof(ScoreProcessor), processor) },
                    Child = counter = new DefaultScoreCounter(),
                };
            });

            AddStep("the score reaches 654,321", () => processor.TotalScore.Value = 654_321);
            AddAssert("the counter heads for the total itself", () => counter.Current.Value, () => Is.EqualTo(654_321));

            AddAssert("a stored score is listed at its own total", () =>
            {
                var score = new ScoreInfo
                {
                    Ruleset = new TypeBeatRuleset().RulesetInfo,
                    TotalScore = 123_456,
                    MaximumStatistics = { [HitResult.Great] = 2000 },
                };

                return scoreManager.GetBindableTotalScoreString(score).Value;
            }, () => Is.EqualTo(123_456.ToString("N0")));
        }

        /// <summary>
        /// The login panel's status dropdown (Online / Do not disturb / Appear offline) is gone and the
        /// status is always Online. It also carried "Sign out", which must still be one click away.
        /// </summary>
        [Test]
        public void TestTheSignedInPanelStillSignsOut()
        {
            LoginPanel panel = null!;

            AddStep("signed in", () => ((DummyAPIAccess)API).SetState(APIState.Online));
            AddStep("show the login panel", () => Child = panel = new LoginPanel
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Width = 400,
            });

            AddUntilStep("a sign out button shows", () => signOut(panel) != null);

            AddStep("press it", () => signOut(panel)!.TriggerClick());
            AddAssert("signed out", () => API.State.Value, () => Is.EqualTo(APIState.Offline));

            AddStep("sign back in", () => ((DummyAPIAccess)API).SetState(APIState.Online));
        }

        private static RoundedButton? signOut(LoginPanel panel) =>
            panel.ChildrenOfType<RoundedButton>().SingleOrDefault(b => b.Text.ToString() == LoginPanelStrings.SignOut.ToString());
    }
}
