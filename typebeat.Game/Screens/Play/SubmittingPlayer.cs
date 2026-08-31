// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Annotations;
using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Screens;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Database;
using typebeat.Game.Online;
using typebeat.Game.Online.API;
using typebeat.Game.Online.Multiplayer;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Online.Spectator;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Ranking;

namespace typebeat.Game.Screens.Play
{
    /// <summary>
    /// A player instance which supports submitting scores to an online store.
    /// </summary>
    public abstract partial class SubmittingPlayer : Player
    {
        /// <summary>
        /// The token to be used for the current submission. This is fetched via a request created by <see cref="CreateTokenRequest"/>.
        /// </summary>
        private long? token;

        [Resolved]
        private IAPIProvider api { get; set; }

        [Resolved]
        private SpectatorClient spectatorClient { get; set; }

        [Resolved]
        private SessionStatics statics { get; set; }

        [Resolved(canBeNull: true)]
        [CanBeNull]
        private UserStatisticsWatcher userStatisticsWatcher { get; set; }

        [Resolved(canBeNull: true)]
        [CanBeNull]
        private INotificationOverlay notifications { get; set; }

        [Resolved(canBeNull: true)]
        [CanBeNull]
        private ReplayUploader replayUploader { get; set; }

        private readonly object scoreSubmissionLock = new object();
        private TaskCompletionSource<bool> scoreSubmissionSource;

        protected SubmittingPlayer(PlayerConfiguration configuration = null)
            : base(configuration)
        {
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            if (DrawableRuleset == null)
            {
                // base load must have failed (e.g. due to an unknown mod); bail.
                return;
            }

            AddInternal(new PlayerTouchInputDetector());

            // We probably want to move this display to something more global.
            // Probably using the OSD somehow.
            AddInternal(new GameplayOffsetControl
            {
                Margin = new MarginPadding(20),
                Anchor = Anchor.CentreRight,
                Origin = Anchor.CentreRight,
            });
        }

        protected override GameplayClockContainer CreateGameplayClockContainer(WorkingBeatmap beatmap, double gameplayStart) => new MasterGameplayClockContainer(beatmap, gameplayStart)
        {
            ShouldValidatePlaybackRate = true,
        };

        protected override void LoadAsyncComplete()
        {
            base.LoadAsyncComplete();
            handleTokenRetrieval();
        }

        private bool handleTokenRetrieval()
        {
            // Token request construction should happen post-load to allow derived classes to potentially prepare DI backings that are used to create the request.
            if (Mods.Value.Any(m => !m.UserPlayable))
            {
                handleTokenFailure(new InvalidOperationException("Non-user playable mod selected."));
                return false;
            }

            if (!api.IsLoggedIn || api.State.Value == APIState.Failing)
            {
                handleTokenFailure(new InvalidOperationException("Online functionality is not available."), displayNotification: api.State.Value == APIState.Failing);
                return false;
            }

            var req = CreateTokenRequest();

            if (req == null)
            {
                handleTokenFailure(new InvalidOperationException("Request could not be constructed."));
                return false;
            }

            string routeWhenSent = api.Endpoints.APIUrl;
            var failure = runTokenRequest(req, out bool stalled);

            // If that attempt is what moved the session on to the fallback API host, the token was
            // lost to a route the game has now abandoned, and the play would go unsubmitted for a
            // reason that has already stopped being true. One repeat, on the new host, with a fresh
            // request (a completed one can never fire again).
            if (failure != null && (stalled || ApiHostSelector.IsFailoverRetryable(failure)) && api.Endpoints.APIUrl != routeWhenSent)
            {
                var retry = CreateTokenRequest();

                if (retry != null)
                {
                    Logger.Log($"Score submission token retrieval failed on {routeWhenSent}, retrying on {api.Endpoints.APIUrl}");
                    failure = runTokenRequest(retry, out _);
                }
            }

            if (failure != null)
                handleTokenFailure(failure, displayNotification: true);

            return true;

            void handleTokenFailure(Exception exception, bool displayNotification = false)
            {
                bool shouldExit = ShouldExitOnTokenRetrievalFailure(exception);

                if (displayNotification || shouldExit)
                {
                    string whatWillHappen = shouldExit
                        ? "Cannot start play"
                        : "Score will not be submitted";

                    if (string.IsNullOrEmpty(exception.Message))
                        notifications?.Post(new ScoreSubmissionFailureNotification(whatWillHappen, "Failed to retrieve a score submission token."));
                    else
                        notifications?.Post(new ScoreSubmissionFailureNotification(whatWillHappen, getUserFacingAPIError(exception)));
                }

                if (shouldExit)
                {
                    Schedule(() =>
                    {
                        ValidForResume = false;
                        this.Exit();
                    });
                }
            }
        }

        /// <summary>
        /// Run one token request through to completion, populating <see cref="token"/> on success.
        /// </summary>
        /// <param name="req">The request to run. Must not have been performed before.</param>
        /// <param name="stalled">
        /// Whether the wait expired rather than the request reporting anything, which is
        /// transport-class by construction: nothing answered and nothing was refused.
        /// </param>
        /// <returns>The failure to report, or <see langword="null"/> if the token was retrieved.</returns>
        private Exception runTokenRequest(APIRequest<APIScoreToken> req, out bool stalled)
        {
            var tcs = new TaskCompletionSource<Exception>();

            req.Success += r =>
            {
                Logger.Log($"Score submission token retrieved ({r.ID})");
                token = r.ID;
                tcs.TrySetResult(null);
            };
            // TrySetResult, because the request may report its own failure after the wait below has
            // already given up on it: `TriggerFailure` schedules this handler rather than running it
            // inline, and the request stays in flight on the API thread until its own timeout.
            req.Failure += ex => tcs.TrySetResult(ex);

            api.Queue(req);

            // Generally a timeout would not happen here as APIAccess will timeout first.
            if (!tcs.Task.Wait(30000))
            {
                var timeout = new InvalidOperationException("Token retrieval timed out (request never run)");
                req.TriggerFailure(timeout);
                stalled = true;
                return timeout;
            }

            stalled = false;
            return tcs.Task.GetResultSafely();
        }

        /// <summary>
        /// Called when a token could not be retrieved for submission.
        /// </summary>
        /// <param name="exception">The error causing the failure.</param>
        /// <returns>Whether gameplay should be immediately exited as a result. Returning false allows the gameplay session to continue. Defaults to true.</returns>
        protected virtual bool ShouldExitOnTokenRetrievalFailure(Exception exception) => true;

        public override bool AllowCriticalSettingsAdjustment
        {
            get
            {
                // General limitations to ensure players don't do anything too weird.
                // These match stable for now.

                // TODO: the blocking conditions should probably display a message.
                if (!IsBreakTime.Value && GameplayClockContainer.CurrentTime - GameplayClockContainer.GameplayStartTime > 10000)
                    return false;

                if (GameplayClockContainer.IsPaused.Value)
                    return false;

                return base.AllowCriticalSettingsAdjustment;
            }
        }

        protected override async Task PrepareScoreForResultsAsync(Score score)
        {
            await base.PrepareScoreForResultsAsync(score).ConfigureAwait(false);

            score.ScoreInfo.Date = DateTimeOffset.Now;

            await submitScore(score).ConfigureAwait(false);
            spectatorClient.EndPlaying(GameplayState);

            // Submission is what makes the play count, and on this backend it is also what PRICES it: the server computes the
            // score's pp and commits the row inside the PUT that just returned above, so the profile statistics are already
            // current by the time we ask for them. osu instead waits for the spectator server to announce that its scoring
            // queue got round to the score; there is no spectator server here, so waiting would mean waiting forever and the
            // toolbar delta plus the results screen's Overall Ranking panel would never appear.
            userStatisticsWatcher?.RegisterForStatisticsUpdateAfter(score.ScoreInfo);
            userStatisticsWatcher?.MarkScoreProcessed(score.ScoreInfo);
        }

        [Resolved]
        private RealmAccess realm { get; set; }

        protected override void StartGameplay()
        {
            base.StartGameplay();

            // User expectation is that last played should be updated when entering the gameplay loop
            // from multiplayer / playlists / solo.
            realm.WriteAsync(r =>
            {
                var realmBeatmap = r.Find<BeatmapInfo>(Beatmap.Value.BeatmapInfo.ID);
                if (realmBeatmap != null)
                    realmBeatmap.LastPlayed = DateTimeOffset.Now;
            });

            spectatorClient.BeginPlaying(token, GameplayState, Score);
        }

        public override bool Pause()
        {
            bool wasPaused = GameplayClockContainer.IsPaused.Value;

            bool paused = base.Pause();

            if (!wasPaused && paused)
                Score.ScoreInfo.Pauses.Add((int)Math.Round(GameplayClockContainer.CurrentTime));

            return paused;
        }

        protected override void ConcludeFailedScore(Score score)
        {
            base.ConcludeFailedScore(score);
            submitFromFailOrQuit(score);
        }

        public override bool OnExiting(ScreenExitEvent e)
        {
            bool exiting = base.OnExiting(e);
            submitFromFailOrQuit(Score);
            statics.SetValue(Static.LastLocalUserScore, Score?.ScoreInfo.DeepClone());
            return exiting;
        }

        private void submitFromFailOrQuit(Score score)
        {
            if (LoadedBeatmapSuccessfully)
            {
                // compare: https://github.com/ppy/osu/blob/ccf1acce56798497edfaf92d3ece933469edcf0a/typebeat.Game/Screens/Play/Player.cs#L848-L851
                var scoreCopy = score.DeepClone();

                Task.Run(async () =>
                {
                    await submitScore(scoreCopy).ConfigureAwait(false);
                    spectatorClient.EndPlaying(GameplayState);
                }).FireAndForget();
            }
        }

        /// <summary>
        /// Construct a request to be used for retrieval of the score token.
        /// Can return null, at which point <see cref="ShouldExitOnTokenRetrievalFailure"/> will be fired.
        /// </summary>
        [CanBeNull]
        protected abstract APIRequest<APIScoreToken> CreateTokenRequest();

        /// <summary>
        /// Construct a request to submit the score.
        /// Will only be invoked if the request constructed via <see cref="CreateTokenRequest"/> was successful.
        /// </summary>
        /// <param name="score">The score to be submitted.</param>
        /// <param name="token">The submission token.</param>
        protected abstract APIRequest<MultiplayerScore> CreateSubmissionRequest(Score score, long token);

        private Task submitScore(Score score)
        {
            var masterClock = GameplayClockContainer as MasterGameplayClockContainer;

            if (masterClock?.PlaybackRateValid.Value != true)
            {
                Logger.Log("Score submission cancelled due to audio playback rate discrepancy.");
                return Task.CompletedTask;
            }

            // token may be null if the request failed but gameplay was still allowed (see HandleTokenRetrievalFailure).
            if (token == null)
            {
                Logger.Log("No token, skipping score submission");
                return Task.CompletedTask;
            }

            // if the user never hit anything, this score should not be counted in any way.
            if (!score.ScoreInfo.Statistics.Any(s => s.Key.IsHit() && s.Value > 0))
            {
                Logger.Log("No hits registered, skipping score submission");
                return Task.CompletedTask;
            }

            // zero scores should also never be submitted.
            if (score.ScoreInfo.TotalScore == 0)
            {
                Logger.Log("Zero score, skipping score submission");
                return Task.CompletedTask;
            }

            // mind the timing of this.
            // once `scoreSubmissionSource` is created, it is presumed that submission is taking place in the background,
            // so all exceptional circumstances that would disallow submission must be handled above.
            lock (scoreSubmissionLock)
            {
                if (scoreSubmissionSource != null)
                    return scoreSubmissionSource.Task;

                scoreSubmissionSource = new TaskCompletionSource<bool>();
            }

            Logger.Log($"Beginning score submission (token:{token.Value})...");
            queueSubmission(score, api.Endpoints.APIUrl, isRetry: false);

            return scoreSubmissionSource.Task;
        }

        /// <summary>
        /// Build and queue one attempt at submitting <paramref name="score"/>, resolving
        /// <see cref="scoreSubmissionSource"/> unless the attempt is worth repeating on a host the
        /// session has moved to since.
        /// </summary>
        /// <param name="score">The score to submit.</param>
        /// <param name="routeWhenSent">The API root this attempt is being built against.</param>
        /// <param name="isRetry">Whether this attempt is itself the repeat, which is never repeated again.</param>
        /// <remarks>
        /// The play this is here for is the one whose own stall triggers the failover: the score PUT
        /// sits on the Cloudflare-proxied host for its full 30 second idle timeout, that timeout is
        /// what pins the direct-origin host (see <see cref="ApiHostSelector"/>), and without a repeat
        /// the play is lost to a route the game abandoned in the same breath. Exactly one repeat,
        /// because the pin only happens once, so a second failure is telling us something else.
        ///
        /// The repeat is a FRESH request from <see cref="CreateSubmissionRequest"/> rather than the
        /// same object performed again: an <see cref="APIRequest"/> completes exactly once (both
        /// trigger paths are gated on its completion state under a lock), so a failed one can never
        /// fire again. Each attempt's handlers close over their own request, so nothing here depends
        /// on telling two in-flight requests apart.
        /// </remarks>
        private void queueSubmission(Score score, string routeWhenSent, bool isRetry)
        {
            var request = CreateSubmissionRequest(score, token.Value);

            request.Success += s =>
            {
                score.ScoreInfo.OnlineID = s.ID;
                score.ScoreInfo.Position = s.Position;

                // The server's own price for this play, which outranks anything the client can work
                // out for itself: it is the number the leaderboards and the profile actually count,
                // and it can encode refusals the client cannot see (the play-time gate, an
                // out-of-bounds total, a blocked build). A NUMBER here means the server ran the
                // formula and this is the answer, 0 included; NULL means it did not run it, either
                // because the play can never be priced or because it cannot be priced yet, and the
                // results screen decides for itself rather than printing a zero the server never
                // asserted.
                score.ScoreInfo.PP = s.PP;

                scoreSubmissionSource.SetResult(true);
                Logger.Log($"Score submission completed! (token:{token.Value} id:{s.ID})");
            };

            request.Failure += e =>
            {
                if (!isRetry && ApiHostSelector.ShouldRetryOnNewHost(e, routeWhenSent, api.Endpoints.APIUrl))
                {
                    Logger.Log($"Score submission failed on {routeWhenSent}, retrying on {api.Endpoints.APIUrl} (id: {token.Value})");
                    queueSubmission(score, api.Endpoints.APIUrl, isRetry: true);
                    return;
                }

                Logger.Error(e, $"{getUserFacingAPIError(e)}\n\nScore was not submitted (id: {token.Value})");
                scoreSubmissionSource.SetResult(false);
            };

            api.Queue(request);
        }

        private static string getUserFacingAPIError(Exception exception)
        {
            switch (exception.Message)
            {
                case @"missing token header":
                case @"invalid client hash":
                case @"invalid verification hash":
                case @"invalid token":
                case @"outdated client":
                    return "Please ensure that you are using the latest version of the official game releases.";

                case @"invalid or missing beatmap_hash":
                    return "This beatmap does not match the online version. Please update or redownload it.";

                case @"expired token":
                    return "Your system clock is set incorrectly. Please check your system time, date and timezone.";

                default:
                    return exception.Message;
            }
        }

        /// <summary>
        /// Once a score has an online id, hand the replay that was just encoded for local import to
        /// the server as well, so the score can be watched back from a leaderboard by anyone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Ordering is what makes this work: the player's score preparation task awaits
        /// <see cref="PrepareScoreForResultsAsync"/> (which awaits the whole submission, populating
        /// <c>OnlineID</c>) before it calls <see cref="Player.ImportScore"/>, which is what raises this.
        /// </para>
        /// <para>
        /// Everything downstream is fire and forget. A score with no online id (submission skipped or
        /// failed, or the fail/quit path where the id landed on a throwaway clone) and a play that
        /// recorded no frames are both simply skipped.
        /// </para>
        /// </remarks>
        protected override void OnReplayEncoded(Score score, byte[] replayData)
        {
            base.OnReplayEncoded(score, replayData);

            if (score.ScoreInfo.OnlineID <= 0)
                return;

            if (score.Replay == null || score.Replay.Frames.Count == 0)
                return;

            replayUploader?.Upload(score.ScoreInfo.OnlineID, replayData);
        }

        protected override ResultsScreen CreateResults(ScoreInfo score) => new SoloResultsScreen(score)
        {
            AllowRetry = true,
            IsLocalPlay = true,
        };
    }
}
