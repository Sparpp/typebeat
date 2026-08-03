// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Scoring;

namespace typebeat.Game.Online
{
    /// <summary>
    /// The counterpart to <see cref="ScoreBasedUserStatisticsUpdate"/>: a statement that the profile statistics delta for
    /// a given score is NOT coming, so anything waiting on one can stop waiting.
    /// </summary>
    /// <remarks>
    /// This exists because "no update yet" and "no update ever" used to be the same state (a null
    /// <see cref="UserStatisticsWatcher.LatestUpdate"/>), which is exactly why a failed refetch left the results screen's
    /// Overall Ranking panel spinning with no way out. Publishing the second case explicitly, with a reason, lets a
    /// surface land on a stated outcome instead of an unresolved promise, and without inventing a delta it does not have.
    /// </remarks>
    /// <param name="Score">The score that was set, and that will not get a delta.</param>
    /// <param name="Reason">Why no delta is coming.</param>
    public record UnavailableStatisticsUpdate(ScoreInfo Score, StatisticsUpdateUnavailableReason Reason);

    /// <summary>
    /// Why a submitted score will never receive a profile statistics delta.
    /// </summary>
    public enum StatisticsUpdateUnavailableReason
    {
        /// <summary>
        /// The statistics could not be retrieved at all: the refetch failed, or it was never attempted because the
        /// score was not eligible to be watched (logged out by the time submission returned, for instance).
        /// The player's real statistics may well have changed; this client just cannot say by how much, or to what.
        /// </summary>
        FetchFailed,

        /// <summary>
        /// The statistics were retrieved fine, and are current, but there is no "before" snapshot to compare against:
        /// the login-time fetch was still in flight when the score landed, or had itself failed. Nothing is wrong with
        /// the profile, only with this client's ability to describe the change.
        /// </summary>
        NoPreviousStatistics,
    }
}
