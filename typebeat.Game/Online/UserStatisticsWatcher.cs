// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using typebeat.Game.Extensions;
using typebeat.Game.Online.API;
using typebeat.Game.Online.Spectator;
using typebeat.Game.Scoring;

namespace typebeat.Game.Online
{
    /// <summary>
    /// A persistent component that binds to the score submission path (and, where one exists, the spectator server) in order to
    /// deliver updates about the logged in user's gameplay statistics.
    /// </summary>
    /// <remarks>
    /// osu drives this purely from <see cref="SpectatorClient.OnUserScoreProcessed"/>, because there a submitted score is only
    /// PRICED later, by an out-of-band queue that announces itself over the spectator hub. type!beat has no spectator server at
    /// all, so that event never fires and nothing would ever be published; and it does not need one, because its server computes
    /// and commits a play's pp inside the submission request itself. Submission IS processing here, so
    /// <see cref="MarkScoreProcessed"/> lets the submitting player say so directly (see SubmittingPlayer). The spectator
    /// subscription is kept anyway: it costs one event handler, and whichever path arrives first wins, since resolving a score
    /// REMOVES it from <see cref="watchedScores"/>.
    /// </remarks>
    public partial class UserStatisticsWatcher : Component
    {
        private readonly LocalUserStatisticsProvider statisticsProvider;

        public IBindable<ScoreBasedUserStatisticsUpdate?> LatestUpdate => latestUpdate;
        private readonly Bindable<ScoreBasedUserStatisticsUpdate?> latestUpdate = new Bindable<ScoreBasedUserStatisticsUpdate?>();

        [Resolved]
        private SpectatorClient spectatorClient { get; set; } = null!;

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        private readonly Dictionary<long, ScoreInfo> watchedScores = new Dictionary<long, ScoreInfo>();

        public UserStatisticsWatcher(LocalUserStatisticsProvider statisticsProvider)
        {
            this.statisticsProvider = statisticsProvider;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            spectatorClient.OnUserScoreProcessed += userScoreProcessed;
        }

        /// <summary>
        /// Registers for a user statistics update after the given <paramref name="score"/> has been processed server-side.
        /// </summary>
        /// <param name="score">The score to listen for the statistics update for.</param>
        public void RegisterForStatisticsUpdateAfter(ScoreInfo score)
        {
            Schedule(() =>
            {
                if (!api.IsLoggedIn)
                    return;

                if (!score.Ruleset.IsLegacyRuleset() || score.OnlineID <= 0)
                    return;

                watchedScores.Add(score.OnlineID, score);
            });
        }

        /// <summary>
        /// Reports that a score registered via <see cref="RegisterForStatisticsUpdateAfter"/> has been processed server-side,
        /// which on this backend means its submission request came back: the server prices the play and COMMITS the row before
        /// it writes the response, so statistics fetched from here already include it.
        /// </summary>
        /// <remarks>
        /// Safe to call more than once, and safe to race the spectator path: the first call resolves the score and removes it
        /// from the watch list, and every later one finds nothing. That matters, because a second refetch for the same score
        /// would publish an update whose "before" is the statistics the FIRST one just cached, i.e. a spurious zero delta over
        /// the top of the real one.
        /// </remarks>
        /// <param name="score">The score whose submission has completed.</param>
        public void MarkScoreProcessed(ScoreInfo score) => Schedule(() => processScore(score.OnlineID));

        private void userScoreProcessed(int userId, long scoreId)
        {
            if (userId != api.LocalUser.Value?.OnlineID)
                return;

            processScore(scoreId);
        }

        private void processScore(long scoreId)
        {
            if (!watchedScores.Remove(scoreId, out var scoreInfo))
                return;

            statisticsProvider.RefetchStatistics(scoreInfo.Ruleset, u => Schedule(() =>
            {
                // No "before" means the local statistics had never been fetched successfully at all (login-time fetch still in
                // flight, or failed). There is no delta to describe, so publish nothing rather than an invented one.
                if (u.OldStatistics != null)
                    latestUpdate.Value = new ScoreBasedUserStatisticsUpdate(scoreInfo, u.OldStatistics, u.NewStatistics);
            }));
        }

        protected override void Dispose(bool isDisposing)
        {
            if (spectatorClient.IsNotNull())
                spectatorClient.OnUserScoreProcessed -= userScoreProcessed;

            base.Dispose(isDisposing);
        }
    }
}
