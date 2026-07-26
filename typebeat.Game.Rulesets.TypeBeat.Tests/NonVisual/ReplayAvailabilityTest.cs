// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the "can this score be watched, and from where" decision that both the results screen's
    /// replay button and the song select leaderboard's context menu route through.
    ///
    /// <para>
    /// The motivating bug: the owner of an online score could not watch it back even though a bit-exact
    /// local replay existed, because nothing consulted the local store when the server reported no
    /// replay. Local must therefore win outright, and must be enough on its own.
    /// </para>
    /// </summary>
    [TestFixture]
    public class ReplayAvailabilityTest
    {
        [Test]
        public void LocalReplayIsPreferredEvenWhenTheServerHasOne()
        {
            Assert.That(ReplayAvailabilityResolver.Resolve(localReplayPresent: true, hasOnlineReplay: true), Is.EqualTo(ReplaySource.Local));
        }

        /// <summary>
        /// The regression guard for backlog 37: own online score, server holds nothing, local replay
        /// exists. This must offer the local copy rather than reporting "replay unavailable".
        /// </summary>
        [Test]
        public void LocalReplayAloneIsEnough()
        {
            Assert.That(ReplayAvailabilityResolver.Resolve(localReplayPresent: true, hasOnlineReplay: false), Is.EqualTo(ReplaySource.Local));
        }

        [Test]
        public void ServerReplayIsUsedWhenNothingIsLocal()
        {
            Assert.That(ReplayAvailabilityResolver.Resolve(localReplayPresent: false, hasOnlineReplay: true), Is.EqualTo(ReplaySource.Online));
        }

        /// <summary>
        /// An old server omits <c>has_replay</c>, which deserialises to false; with nothing local either,
        /// the only honest answer is that there is no replay.
        /// </summary>
        [Test]
        public void NothingAnywhereMeansUnavailable()
        {
            Assert.That(ReplayAvailabilityResolver.Resolve(localReplayPresent: false, hasOnlineReplay: false), Is.EqualTo(ReplaySource.NotAvailable));
        }

        [Test]
        public void BackfillTargetsOwnScoresTheServerLacks()
        {
            Assert.That(ReplayAvailabilityResolver.ShouldBackfill(isOwnScore: true, onlineScoreId: 5, hasOnlineReplay: false, alreadyAttempted: false), Is.True);
        }

        [Test]
        public void BackfillSkipsScoresThatAreNotWorthUploading()
        {
            Assert.Multiple(() =>
            {
                // Someone else's score: the server would reject it, and it is not ours to send.
                Assert.That(ReplayAvailabilityResolver.ShouldBackfill(isOwnScore: false, onlineScoreId: 5, hasOnlineReplay: false, alreadyAttempted: false), Is.False);

                // Already stored server side; re-uploading would just burn bandwidth.
                Assert.That(ReplayAvailabilityResolver.ShouldBackfill(isOwnScore: true, onlineScoreId: 5, hasOnlineReplay: true, alreadyAttempted: false), Is.False);

                // No online id means there is no resource to PUT to.
                Assert.That(ReplayAvailabilityResolver.ShouldBackfill(isOwnScore: true, onlineScoreId: 0, hasOnlineReplay: false, alreadyAttempted: false), Is.False);
                Assert.That(ReplayAvailabilityResolver.ShouldBackfill(isOwnScore: true, onlineScoreId: -1, hasOnlineReplay: false, alreadyAttempted: false), Is.False);

                // Tried once already this session; a leaderboard refetch must not retry in a loop.
                Assert.That(ReplayAvailabilityResolver.ShouldBackfill(isOwnScore: true, onlineScoreId: 5, hasOnlineReplay: false, alreadyAttempted: true), Is.False);
            });
        }
    }
}
