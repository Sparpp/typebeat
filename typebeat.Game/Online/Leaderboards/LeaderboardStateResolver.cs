// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Online.Leaderboards
{
    /// <summary>
    /// Turns a completed leaderboard fetch into the state the wedge displays.
    ///
    /// <para>
    /// The contract this exists to guarantee: a COMPLETED fetch never resolves to
    /// <see cref="LeaderboardState.Retrieving"/>. The loading layer is only ever shown while a
    /// request is genuinely in flight, so a failure (or an empty board from a server that does not
    /// serve one for this map) lands on a placeholder rather than spinning forever. That is the
    /// landmine this codebase has hit before with unhandled request failures.
    /// </para>
    /// </summary>
    public static class LeaderboardStateResolver
    {
        public static LeaderboardState Resolve(LeaderboardScores scores)
        {
            // Any fail state maps 1:1 onto a placeholder state (the enum values are shared).
            if (scores.FailState != null)
                return (LeaderboardState)scores.FailState;

            // A successful but empty response is "no scores", never a spinner. This is exactly what
            // an old server returns for an unranked map, and what a new one returns for a map whose
            // set is hidden/removed.
            return scores.TopScores.Count == 0 ? LeaderboardState.NoScores : LeaderboardState.Success;
        }
    }
}
