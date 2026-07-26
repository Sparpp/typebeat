// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Overlays;
using osuTK;

namespace typebeat.Game.Screens.Edit.Submission
{
    [LocalisableDescription(typeof(BeatmapSubmissionStrings), nameof(BeatmapSubmissionStrings.SubmissionSettings))]
    public partial class ScreenSubmissionSettings : WizardScreen
    {
        private readonly BindableBool loadInBrowserAfterSubmission = new BindableBool();

        public override LocalisableString? NextStepText => BeatmapSubmissionStrings.ConfirmSubmission;

        [Resolved]
        private BeatmapSubmissionSettings settings { get; set; } = null!;

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager configManager)
        {
            configManager.BindWith(OsuSetting.EditorSubmissionLoadInBrowserAfterSubmission, loadInBrowserAfterSubmission);

            // Unlike lazer there is no legacy-export disclaimer here: submission exports through
            // the native format encoder, so nothing is lost.
            Content.Add(new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Spacing = new Vector2(5),
                Children = new Drawable[]
                {
                    new FormEnumDropdown<BeatmapSubmissionTarget>
                    {
                        RelativeSizeAxes = Axes.X,
                        Caption = BeatmapSubmissionStrings.BeatmapSubmissionTargetCaption,
                        Current = settings.Target,
                    },
                    new FormCheckBox
                    {
                        Caption = BeatmapSubmissionStrings.ExplicitContent,
                        HintText = BeatmapSubmissionStrings.ExplicitContentHint,
                        Current = settings.ExplicitContent,
                    },
                    new FormCheckBox
                    {
                        Caption = BeatmapSubmissionStrings.NotifyOnDiscussionReplies,
                        Current = settings.NotifyOnDiscussionReplies,
                    },
                    new FormCheckBox
                    {
                        Caption = BeatmapSubmissionStrings.LoadInBrowserAfterSubmission,
                        Current = loadInBrowserAfterSubmission,
                    },
                }
            });

            switch (settings.LatestOnlineStateRequest?.CompletionState)
            {
                case APIRequestCompletionState.Completed:
                    applyLatestOnlineState();
                    break;

                case APIRequestCompletionState.Waiting:
                    // both controls are prefilled from the set's online state, so they stay locked
                    // until it arrives rather than letting a late response overwrite a user's choice.
                    setPrefilledControlsDisabled(true);
                    settings.LatestOnlineStateRequest.Success += _ => applyLatestOnlineState();
                    // without this a failed lookup would leave the controls locked for good.
                    settings.LatestOnlineStateRequest.Failure += _ => setPrefilledControlsDisabled(false);
                    break;
            }
        }

        private void setPrefilledControlsDisabled(bool disabled)
        {
            settings.Target.Disabled = disabled;
            settings.ExplicitContent.Disabled = disabled;
        }

        private void applyLatestOnlineState()
        {
            Debug.Assert(settings.LatestOnlineStateRequest != null);
            setPrefilledControlsDisabled(false);

            settings.Target.Value = settings.LatestOnlineStateRequest.Response?.Status switch
            {
                // Preserve the creator's "not for ranking" choice across re-submissions.
                BeatmapOnlineStatus.Unranked => BeatmapSubmissionTarget.Unranked,
                >= BeatmapOnlineStatus.Pending => BeatmapSubmissionTarget.Pending,
                _ => BeatmapSubmissionTarget.WIP,
            };

            // Carry the existing explicit flag over a re-submission. Servers that do not report the
            // flag on a beatmapset simply leave this false, i.e. unchecked, as for a brand new set.
            settings.ExplicitContent.Value = settings.LatestOnlineStateRequest.Response?.HasExplicitContent == true;
        }
    }
}
