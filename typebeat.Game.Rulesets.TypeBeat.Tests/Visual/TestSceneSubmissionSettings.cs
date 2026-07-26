// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Framework.Testing;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Overlays;
using typebeat.Game.Screens.Edit.Submission;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Covers the submission wizard's settings step, specifically the explicit content choice: it sits
    /// with the other mapset-level choices, drives the shared <see cref="BeatmapSubmissionSettings"/>
    /// that the submission request is built from, and is prefilled from the set's online state when
    /// re-submitting instead of silently dropping back to unflagged.
    /// </summary>
    [TestFixture]
    public partial class TestSceneSubmissionSettings : OsuTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Aquamarine);

        [Cached]
        private readonly BeatmapSubmissionSettings settings = new BeatmapSubmissionSettings();

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("reset settings", () =>
            {
                settings.LatestOnlineStateRequest = null;
                settings.Target.Disabled = false;
                settings.ExplicitContent.Disabled = false;
                settings.Target.Value = BeatmapSubmissionTarget.WIP;
                settings.ExplicitContent.Value = false;
                Clear();
            });
        }

        [Test]
        public void TestExplicitToggleSitsWithTheOtherChoicesAndDrivesTheRequest()
        {
            loadSettingsScreen();

            AddAssert("explicit choice is first of the checkboxes", () => checkBoxCaptions().First(),
                () => Is.EqualTo(BeatmapSubmissionStrings.ExplicitContent.ToString()));

            AddAssert("checkbox starts unchecked", () => explicitCheckBox().Current.Value, () => Is.False);

            AddStep("check explicit", () => explicitCheckBox().Current.Value = true);
            AddAssert("settings flagged", () => settings.ExplicitContent.Value, () => Is.True);
            AddAssert("request flagged", () => PutBeatmapSetRequest.CreateNew(1, settings).ExplicitContent, () => Is.True);

            AddStep("uncheck explicit", () => explicitCheckBox().Current.Value = false);
            AddAssert("settings unflagged", () => settings.ExplicitContent.Value, () => Is.False);
            AddAssert("request unflagged", () => PutBeatmapSetRequest.CreateNew(1, settings).ExplicitContent, () => Is.False);
        }

        [Test]
        public void TestPrefilledFromOnlineState([Values(false, true)] bool onlineFlagSet)
        {
            AddStep("provide completed online state", () =>
            {
                var request = new GetBeatmapSetRequest(1234);
                request.AttachAPI(API);
                request.TriggerSuccess(new APIBeatmapSet { OnlineID = 1234, HasExplicitContent = onlineFlagSet });
                settings.LatestOnlineStateRequest = request;
            });

            loadSettingsScreen();

            AddAssert("checkbox matches online state", () => explicitCheckBox().Current.Value, () => Is.EqualTo(onlineFlagSet));
            AddAssert("checkbox is editable", () => explicitCheckBox().Current.Disabled, () => Is.False);
        }

        [Test]
        public void TestLockedWhileOnlineStateIsPending()
        {
            GetBeatmapSetRequest request = null!;

            AddStep("provide pending online state", () =>
            {
                request = new GetBeatmapSetRequest(1234);
                request.AttachAPI(API);
                settings.LatestOnlineStateRequest = request;
            });

            loadSettingsScreen();

            AddAssert("checkbox locked", () => explicitCheckBox().Current.Disabled, () => Is.True);

            AddStep("online state arrives", () => request.TriggerSuccess(new APIBeatmapSet { OnlineID = 1234, HasExplicitContent = true }));

            AddUntilStep("checkbox unlocked and flagged",
                () => !explicitCheckBox().Current.Disabled && explicitCheckBox().Current.Value);
        }

        [Test]
        public void TestUnlockedIfOnlineStateLookupFails()
        {
            GetBeatmapSetRequest request = null!;

            AddStep("provide pending online state", () =>
            {
                request = new GetBeatmapSetRequest(1234);
                request.AttachAPI(API);
                settings.LatestOnlineStateRequest = request;
            });

            loadSettingsScreen();

            AddAssert("checkbox locked", () => explicitCheckBox().Current.Disabled, () => Is.True);

            AddStep("lookup fails", () => request.Fail(new InvalidOperationException("no such set")));

            // a failed lookup must not leave the creator unable to make a choice at all.
            AddUntilStep("checkbox unlocked", () => !explicitCheckBox().Current.Disabled);
            AddAssert("checkbox unflagged", () => explicitCheckBox().Current.Value, () => Is.False);
        }

        private void loadSettingsScreen()
        {
            ScreenStack stack = null!;

            AddStep("load settings screen", () =>
            {
                Child = stack = new ScreenStack { RelativeSizeAxes = Axes.Both };
                stack.Push(new ScreenSubmissionSettings());
            });

            AddUntilStep("screen loaded", () => stack.CurrentScreen is ScreenSubmissionSettings screen && screen.IsLoaded);
        }

        private FormCheckBox explicitCheckBox()
            => this.ChildrenOfType<FormCheckBox>().Single(c => c.Caption.ToString() == BeatmapSubmissionStrings.ExplicitContent.ToString());

        private string[] checkBoxCaptions()
            => this.ChildrenOfType<FormCheckBox>().Select(c => c.Caption.ToString()).ToArray();
    }
}
