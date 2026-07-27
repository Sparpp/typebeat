// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.Leaderboards;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the two client-side decisions behind "an unranked map shows the website's unranked
    /// leaderboard in the global tab":
    ///
    /// <list type="bullet">
    /// <item>which board a beatmap has at all (<see cref="GlobalLeaderboardAvailability"/>), which
    /// must agree with the server's own status switch in ScoreEndpoints.Leaderboard; and</item>
    /// <item>what a completed fetch displays (<see cref="LeaderboardStateResolver"/>), which must
    /// never be the loading layer. The codebase's standing landmine is a request whose failure is
    /// unhandled and leaves a surface spinning forever, so an error, and an empty board from a
    /// server too old to serve one, both have to read as "no scores".</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class UnrankedLeaderboardTest
    {
        private const int online_id = 1234;

        [Test]
        public void RankedMapKeepsTheRankedBoard()
        {
            Assert.Multiple(() =>
            {
                Assert.That(GlobalLeaderboardAvailability.Resolve(online_id, BeatmapOnlineStatus.Ranked), Is.EqualTo(GlobalLeaderboardKind.Ranked));
                Assert.That(GlobalLeaderboardAvailability.Resolve(online_id, BeatmapOnlineStatus.Approved), Is.EqualTo(GlobalLeaderboardKind.Ranked));
                Assert.That(GlobalLeaderboardAvailability.Resolve(online_id, BeatmapOnlineStatus.Qualified), Is.EqualTo(GlobalLeaderboardKind.Ranked));
                Assert.That(GlobalLeaderboardAvailability.Resolve(online_id, BeatmapOnlineStatus.Loved), Is.EqualTo(GlobalLeaderboardKind.Ranked));
            });
        }

        /// <summary>
        /// The change this test exists for: the two published-but-not-ranked statuses the server
        /// serves an unranked board for ('pending' and the creator's 'unranked') used to fall into
        /// the blanket <c>Status &lt;= Pending</c> block and show "leaderboards are not available".
        /// </summary>
        [Test]
        public void PublishedButNotRankedMapsGetTheUnrankedBoard()
        {
            Assert.Multiple(() =>
            {
                Assert.That(GlobalLeaderboardAvailability.Resolve(online_id, BeatmapOnlineStatus.Pending), Is.EqualTo(GlobalLeaderboardKind.Unranked));
                Assert.That(GlobalLeaderboardAvailability.Resolve(online_id, BeatmapOnlineStatus.Unranked), Is.EqualTo(GlobalLeaderboardKind.Unranked));
            });
        }

        /// <summary>
        /// Statuses the server will not serve any board for: it answers them with an empty
        /// collection, so asking would only cost a round trip to reach the same placeholder.
        /// </summary>
        [Test]
        public void UnpublishedMapsHaveNoBoard()
        {
            Assert.Multiple(() =>
            {
                Assert.That(GlobalLeaderboardAvailability.Resolve(online_id, BeatmapOnlineStatus.LocallyModified), Is.EqualTo(GlobalLeaderboardKind.None));
                Assert.That(GlobalLeaderboardAvailability.Resolve(online_id, BeatmapOnlineStatus.None), Is.EqualTo(GlobalLeaderboardKind.None));
                Assert.That(GlobalLeaderboardAvailability.Resolve(online_id, BeatmapOnlineStatus.Graveyard), Is.EqualTo(GlobalLeaderboardKind.None));
                Assert.That(GlobalLeaderboardAvailability.Resolve(online_id, BeatmapOnlineStatus.WIP), Is.EqualTo(GlobalLeaderboardKind.None));
            });
        }

        /// <summary>Without an online id there is no resource to fetch, whatever the status says.</summary>
        [Test]
        public void NoOnlineIdMeansNoBoard()
        {
            Assert.Multiple(() =>
            {
                Assert.That(GlobalLeaderboardAvailability.Resolve(0, BeatmapOnlineStatus.Ranked), Is.EqualTo(GlobalLeaderboardKind.None));
                Assert.That(GlobalLeaderboardAvailability.Resolve(-1, BeatmapOnlineStatus.Pending), Is.EqualTo(GlobalLeaderboardKind.None));
            });
        }

        [Test]
        public void FetchedUnrankedBoardIsDisplayedAndFlagged()
        {
            var fetched = LeaderboardScores.Success([new ScoreInfo { TotalScore = 500_000 }], scoresRequested: 50, totalScores: 1, userScore: null, unrankedBoard: true);

            Assert.Multiple(() =>
            {
                Assert.That(LeaderboardStateResolver.Resolve(fetched), Is.EqualTo(LeaderboardState.Success), "an unranked map's fetched rows are shown, not suppressed");
                Assert.That(fetched.UnrankedBoard, Is.True, "and are cued as unranked");
            });
        }

        [Test]
        public void RankedBoardCarriesNoUnrankedCue()
        {
            var fetched = LeaderboardScores.Success([new ScoreInfo { TotalScore = 500_000 }], scoresRequested: 50, totalScores: 1, userScore: null);

            Assert.Multiple(() =>
            {
                Assert.That(LeaderboardStateResolver.Resolve(fetched), Is.EqualTo(LeaderboardState.Success));
                Assert.That(fetched.UnrankedBoard, Is.False);
            });
        }

        /// <summary>
        /// The old-server degradation path: a server that predates unranked boards answers an
        /// unranked map with an empty (but successful) collection. That must land on the no-scores
        /// placeholder, and it must NOT still claim to be an unranked board full of rows.
        /// </summary>
        [Test]
        public void EmptyBoardReadsAsNoScores()
        {
            var empty = LeaderboardScores.Success([], scoresRequested: 50, totalScores: 0, userScore: null, unrankedBoard: true);

            Assert.That(LeaderboardStateResolver.Resolve(empty), Is.EqualTo(LeaderboardState.NoScores));
        }

        /// <summary>
        /// Every failure, network errors included, resolves to a placeholder state. Nothing may
        /// resolve to <see cref="LeaderboardState.Retrieving"/>: that is the forever-spinner.
        /// </summary>
        [Test]
        public void EveryFailureResolvesToAPlaceholderNotASpinner()
        {
            Assert.Multiple(() =>
            {
                foreach (var failState in Enum.GetValues<LeaderboardFailState>())
                {
                    var state = LeaderboardStateResolver.Resolve(LeaderboardScores.Failure(failState));

                    Assert.That(state, Is.EqualTo((LeaderboardState)failState), $"{failState} must map onto its own placeholder state");
                    Assert.That(state, Is.Not.EqualTo(LeaderboardState.Retrieving), $"{failState} must not leave the leaderboard spinning");
                    Assert.That(Enum.IsDefined(state), Is.True, $"{failState} has no matching {nameof(LeaderboardState)}, the wedge would throw and stay on the loading layer");
                }
            });
        }

        /// <summary>
        /// A network failure on an unranked map is a network failure like any other: the cue is
        /// dropped along with the rows, so no stale "unranked" label survives an error.
        /// </summary>
        [Test]
        public void FailureCarriesNoUnrankedCue()
        {
            var failed = LeaderboardScores.Failure(LeaderboardFailState.NetworkFailure);

            Assert.Multiple(() =>
            {
                Assert.That(failed.UnrankedBoard, Is.False);
                Assert.That(failed.TopScores, Is.Empty);
                Assert.That(LeaderboardStateResolver.Resolve(failed), Is.EqualTo(LeaderboardState.NetworkFailure));
            });
        }

        /// <summary>
        /// Sanity check on the enum ordering the resolver leans on: the two unranked-board statuses
        /// both sit below <see cref="BeatmapOnlineStatus.Ranked"/>, so no future status can quietly
        /// slide into the ranked branch without being listed here.
        /// </summary>
        [Test]
        public void OnlyRankedAndAboveCount()
        {
            var rankedish = Enum.GetValues<BeatmapOnlineStatus>()
                                .Where(s => GlobalLeaderboardAvailability.Resolve(online_id, s) == GlobalLeaderboardKind.Ranked);

            Assert.That(rankedish, Is.All.GreaterThanOrEqualTo(BeatmapOnlineStatus.Ranked));
        }
    }
}
