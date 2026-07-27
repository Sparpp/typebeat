// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Beatmaps;

namespace typebeat.Game.Online.Leaderboards
{
    /// <summary>
    /// Which online board, if any, a beatmap has.
    /// </summary>
    public enum GlobalLeaderboardKind
    {
        /// <summary>
        /// The map has no online board at all: it is not on the server, or its set is not published
        /// (locally modified, unknown, graveyard, WIP). The wedge shows the "not available" message.
        /// </summary>
        None,

        /// <summary>
        /// The ranked board: plays here count, and the server serves the scores flagged ranked.
        /// </summary>
        Ranked,

        /// <summary>
        /// The unranked board: the map is published and takes plays, but nothing on it counts, so
        /// the server serves the website's Unranked board (passed plays stored ranked=false) for it.
        /// Shown with a cue so nobody mistakes it for a ranked board.
        /// </summary>
        Unranked,
    }

    /// <summary>
    /// The single decision of which global board a beatmap has, kept pure and away from the fetch
    /// so it can be pinned by tests. Mirrors the server's own status switch in ScoreEndpoints:
    /// a ranked set serves the ranked board, a published-but-not-ranked one ('pending' /
    /// 'unranked', i.e. <see cref="BeatmapOnlineStatus.Pending"/> / <see cref="BeatmapOnlineStatus.Unranked"/>)
    /// serves the unranked board, and everything else has no board.
    ///
    /// <para>
    /// Degrading gracefully against an OLD server matters more than the mapping itself: a server
    /// that predates unranked boards answers a non-ranked map with an empty collection, which is a
    /// SUCCESS, so the wedge lands on its no-scores placeholder. Nothing here (or in the fetch) may
    /// ever leave the wedge on the loading layer, see <see cref="LeaderboardStateResolver"/>.
    /// </para>
    /// </summary>
    public static class GlobalLeaderboardAvailability
    {
        public static GlobalLeaderboardKind Resolve(int beatmapOnlineID, BeatmapOnlineStatus status)
        {
            // No online identity means there is nothing to look the board up by.
            if (beatmapOnlineID <= 0)
                return GlobalLeaderboardKind.None;

            switch (status)
            {
                case BeatmapOnlineStatus.Unranked:
                case BeatmapOnlineStatus.Pending:
                    return GlobalLeaderboardKind.Unranked;

                default:
                    // Ranked and everything above it (Approved / Qualified / Loved) keep the ranked
                    // board; LocallyModified / None / Graveyard / WIP have none.
                    return status >= BeatmapOnlineStatus.Ranked ? GlobalLeaderboardKind.Ranked : GlobalLeaderboardKind.None;
            }
        }
    }
}
