// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Newtonsoft.Json;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Leaderboards;
using typebeat.Game.Overlays.BeatmapSet.Scores;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// What the native beatmap set overlay reads off the server: the creator's avatar (the set payload's <c>user</c>
    /// object, since the client never builds an avatar URL of its own) and which maps show a leaderboard (the same
    /// boards song select shows, so a pending or unranked map's unranked board too).
    /// </summary>
    [TestFixture]
    public class BeatmapSetOverlayWireTest
    {
        private const string avatar = "https://typebeat.sh/avatars/718/1.png";

        [TestCase(true)]
        [TestCase(false)]
        public void TheCreatorsAvatarReachesTheAuthor(bool userFirst)
        {
            const string user = @"""user"": { ""id"": 718, ""username"": ""CrashstylerX2"", ""country_code"": ""US"", ""avatar_url"": """ + avatar + @""" }";
            const string pair = @"""creator"": ""CrashstylerX2"", ""user_id"": 718";

            var set = JsonConvert.DeserializeObject<APIBeatmapSet>($"{{ \"id\": 266, {(userFirst ? $"{user}, {pair}" : $"{pair}, {user}")} }}")!;

            Assert.Multiple(() =>
            {
                Assert.That(set.Author.Id, Is.EqualTo(718));
                Assert.That(set.Author.Username, Is.EqualTo("CrashstylerX2"));
                Assert.That(set.Author.AvatarUrl, Is.EqualTo(avatar));
            });
        }

        [Test]
        public void AnOlderServersPayloadStillNamesTheCreator()
        {
            var set = JsonConvert.DeserializeObject<APIBeatmapSet>(@"{ ""id"": 266, ""creator"": ""CrashstylerX2"", ""user_id"": 718 }")!;

            Assert.Multiple(() =>
            {
                Assert.That(set.Author.Id, Is.EqualTo(718));
                Assert.That(set.Author.Username, Is.EqualTo("CrashstylerX2"));
                Assert.That(set.Author.AvatarUrl, Is.Null, "no avatar_url: the overlay shows the guest picture");
            });
        }

        [TestCase(BeatmapOnlineStatus.Ranked, GlobalLeaderboardKind.Ranked)]
        [TestCase(BeatmapOnlineStatus.Loved, GlobalLeaderboardKind.Ranked)]
        [TestCase(BeatmapOnlineStatus.Pending, GlobalLeaderboardKind.Unranked)]
        [TestCase(BeatmapOnlineStatus.Unranked, GlobalLeaderboardKind.Unranked)]
        [TestCase(BeatmapOnlineStatus.WIP, GlobalLeaderboardKind.None)]
        [TestCase(BeatmapOnlineStatus.Graveyard, GlobalLeaderboardKind.None)]
        public void TheOverlayShowsTheBoardSongSelectShows(BeatmapOnlineStatus status, GlobalLeaderboardKind expected)
            => Assert.That(ScoresContainer.BoardFor(new APIBeatmap { OnlineID = 379, Status = status }), Is.EqualTo(expected));

        [Test]
        public void ABeatmapWithNoOnlineIdHasNoBoard()
            => Assert.That(ScoresContainer.BoardFor(new APIBeatmap { OnlineID = 0, Status = BeatmapOnlineStatus.Ranked }), Is.EqualTo(GlobalLeaderboardKind.None));

        [Test]
        public void NoBeatmapHasNoBoard()
            => Assert.That(ScoresContainer.BoardFor(null), Is.EqualTo(GlobalLeaderboardKind.None));
    }
}
