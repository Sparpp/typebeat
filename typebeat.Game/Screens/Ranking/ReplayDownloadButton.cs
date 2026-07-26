// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Online;
using typebeat.Game.Scoring;
using osuTK;

namespace typebeat.Game.Screens.Ranking
{
    public partial class ReplayDownloadButton : CompositeDrawable, IKeyBindingHandler<GlobalAction>
    {
        public readonly Bindable<ScoreInfo?> Score = new Bindable<ScoreInfo?>();

        protected readonly Bindable<DownloadState> State = new Bindable<DownloadState>();

        private DownloadButton button = null!;
        private ShakeContainer shakeContainer = null!;

        private ScoreDownloadTracker? downloadTracker;

        [Resolved]
        private ScoreManager scoreManager { get; set; } = null!;

        /// <summary>
        /// Where a replay for the displayed score can be served from.
        /// </summary>
        /// <remarks>
        /// <see cref="DownloadState.LocallyAvailable"/> is reported by the <see cref="ScoreDownloadTracker"/>,
        /// which matches realm on hash, online id or legacy online id. That match is the local fallback
        /// for an online leaderboard row: the local user's own score carries the online id that was
        /// written back at submission time, so their bit-exact local replay is offered even when the
        /// server holds nothing.
        /// </remarks>
        private ReplaySource replayAvailability
            => ReplayAvailabilityResolver.Resolve(State.Value == DownloadState.LocallyAvailable, Score.Value?.HasOnlineReplay == true);

        public ReplayDownloadButton(ScoreInfo? score)
        {
            Score.Value = score;
            Size = new Vector2(50, 30);
        }

        [BackgroundDependencyLoader]
        private void load(OsuGame? game, ScoreModelDownloader scoreDownloader)
        {
            InternalChild = shakeContainer = new ShakeContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = button = new DownloadButton
                {
                    RelativeSizeAxes = Axes.Both,
                }
            };

            button.Action = () =>
            {
                switch (State.Value)
                {
                    case DownloadState.LocallyAvailable:
                        game?.PresentScore(Score.Value!, ScorePresentType.Gameplay);
                        break;

                    case DownloadState.NotDownloaded:
                        scoreDownloader.Download(Score.Value!);
                        break;

                    case DownloadState.Importing:
                    case DownloadState.Downloading:
                        shakeContainer.Shake();
                        break;
                }
            };

            Score.BindValueChanged(score =>
            {
                // An export may be pending from the last score.
                // Reset this to meet user expectations (a new score which has just been switched to shouldn't export)
                State.ValueChanged -= exportWhenReady;

                downloadTracker?.RemoveAndDisposeImmediately();
                downloadTracker = null;
                State.SetDefault();

                if (score.NewValue != null)
                {
                    AddInternal(downloadTracker = new ScoreDownloadTracker(score.NewValue)
                    {
                        State = { BindTarget = State }
                    });
                }

                updateState();
            }, true);

            State.BindValueChanged(state =>
            {
                button.State.Value = state.NewValue;
                updateState();
            }, true);
        }

        #region Export via hotkey logic (also in SaveFailedScoreButton)

        public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
        {
            if (e.Repeat)
                return false;

            switch (e.Action)
            {
                case GlobalAction.SaveReplay:
                    button.TriggerClick();
                    return true;

                case GlobalAction.ExportReplay:
                    if (State.Value == DownloadState.LocallyAvailable)
                    {
                        State.BindValueChanged(exportWhenReady, true);
                    }
                    else
                    {
                        // A download needs to be performed before we can export this replay.
                        button.TriggerClick();
                        if (button.Enabled.Value)
                            State.BindValueChanged(exportWhenReady, true);
                    }

                    return true;
            }

            return false;
        }

        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e)
        {
        }

        private void exportWhenReady(ValueChangedEvent<DownloadState> state)
        {
            if (state.NewValue != DownloadState.LocallyAvailable) return;

            scoreManager.Export(Score.Value!);

            State.ValueChanged -= exportWhenReady;
        }

        #endregion

        private void updateState()
        {
            switch (replayAvailability)
            {
                case ReplaySource.Local:
                    button.TooltipText = @"watch replay";
                    button.Enabled.Value = true;
                    break;

                case ReplaySource.Online:
                    button.TooltipText = @"download replay";
                    button.Enabled.Value = true;
                    break;

                default:
                    button.TooltipText = @"replay unavailable";
                    button.Enabled.Value = false;
                    break;
            }
        }
    }
}
