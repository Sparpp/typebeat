// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Scoring
{
    /// <summary>
    /// Where a watchable replay for a score can be obtained from.
    /// </summary>
    public enum ReplaySource
    {
        /// <summary>
        /// No replay exists anywhere the client can reach.
        /// </summary>
        NotAvailable,

        /// <summary>
        /// A replay is already in the local score store and can be played immediately.
        /// </summary>
        Local,

        /// <summary>
        /// The server holds a replay which must be downloaded and imported before it can be played.
        /// </summary>
        Online,
    }

    /// <summary>
    /// The decision logic behind "can this score be watched, and from where".
    /// </summary>
    /// <remarks>
    /// Kept free of any drawable or realm dependency so it can be exercised directly. Two facts feed
    /// every decision: whether a local score carrying a replay file matches this one, and whether the
    /// server said it is holding a replay (<c>has_replay</c>, absent on an old server, which
    /// deserialises to <see langword="false"/>).
    /// </remarks>
    public static class ReplayAvailabilityResolver
    {
        /// <summary>
        /// Resolve which source should serve a replay.
        /// </summary>
        /// <remarks>
        /// Local always wins: it is instant, and for the local user's own scores it is bit-exact with
        /// what was played. This is also the fallback that lets the owner of an online score watch it
        /// back even when the server has never been given a copy.
        /// </remarks>
        /// <param name="localReplayPresent">Whether a local score with a replay file matches.</param>
        /// <param name="hasOnlineReplay">Whether the server reported holding a replay.</param>
        public static ReplaySource Resolve(bool localReplayPresent, bool hasOnlineReplay)
        {
            if (localReplayPresent)
                return ReplaySource.Local;

            return hasOnlineReplay ? ReplaySource.Online : ReplaySource.NotAvailable;
        }

        /// <summary>
        /// Whether an online score row is worth attempting a retroactive replay upload for.
        /// </summary>
        /// <remarks>
        /// Only the owner may upload (the server enforces this too), only scores that actually have an
        /// online id can be addressed, there is no point re-sending what the server already holds, and
        /// each id is attempted at most once per session. Whether a local replay actually exists is
        /// deliberately NOT an input: that answer costs a realm read, so it is checked after this
        /// filter has cut the candidate list down.
        /// </remarks>
        /// <param name="isOwnScore">Whether the row belongs to the locally logged in user.</param>
        /// <param name="onlineScoreId">The row's online score id.</param>
        /// <param name="hasOnlineReplay">Whether the server reported holding a replay.</param>
        /// <param name="alreadyAttempted">Whether this id was already attempted this session.</param>
        public static bool ShouldBackfill(bool isOwnScore, long onlineScoreId, bool hasOnlineReplay, bool alreadyAttempted)
            => isOwnScore && onlineScoreId > 0 && !hasOnlineReplay && !alreadyAttempted;
    }
}
