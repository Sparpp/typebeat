// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The statistics a leaderboard row carries, as the client reads them: <see cref="SoloScoreInfo.Statistics"/>
    /// and <see cref="SoloScoreInfo.MaximumStatistics"/> are <c>Dictionary&lt;HitResult, int&gt;</c>, parsed with stock
    /// Newtonsoft inside <see cref="APIScoresCollection.Scores"/>. A key that names no <see cref="HitResult"/> member
    /// therefore fails the WHOLE board response, not just its own row, which is why the server keeps only
    /// <see cref="HitResult"/> names when it stores a submission and again when it serves a board.
    /// </summary>
    [TestFixture]
    public class LeaderboardStatisticsWireTest
    {
        /// <summary>
        /// What the server guarantees: every row's keys are <see cref="HitResult"/> names, spelled as the client spells
        /// them on submission, and a board made only of those parses with every count in place.
        /// </summary>
        [Test]
        public void ABoardOfHitResultKeysDeserialises()
        {
            const string body = @"{
                ""score_count"": 1,
                ""scores"": [
                    {
                        ""id"": 7,
                        ""rank"": ""A"",
                        ""statistics"": { ""great"": 7, ""ok"": 1, ""meh"": 1, ""good"": 1, ""miss"": 2, ""combo_break"": 3 },
                        ""maximum_statistics"": { ""great"": 12 }
                    }
                ],
                ""user_score"": null
            }";

            var board = JsonConvert.DeserializeObject<APIScoresCollection>(body);

            Assert.That(board, Is.Not.Null);
            Assert.That(board!.Scores, Has.Count.EqualTo(1));

            var row = board.Scores[0];

            Assert.Multiple(() =>
            {
                Assert.That(row.Statistics[HitResult.Great], Is.EqualTo(7));
                Assert.That(row.Statistics[HitResult.Good], Is.EqualTo(1));
                Assert.That(row.Statistics[HitResult.Miss], Is.EqualTo(2));
                Assert.That(row.Statistics[HitResult.ComboBreak], Is.EqualTo(3));
                Assert.That(row.MaximumStatistics[HitResult.Great], Is.EqualTo(12));
            });
        }

        /// <summary>
        /// Every <see cref="HitResult"/> member survives the round trip the server relies on: the name the client writes
        /// for it is a name the client reads back. The server's allowlist is exactly this vocabulary.
        /// </summary>
        [Test]
        public void EveryHitResultNameRoundTrips()
        {
            var all = Enum.GetValues<HitResult>().ToDictionary(r => r, _ => 1);

            var read = JsonConvert.DeserializeObject<Dictionary<HitResult, int>>(JsonConvert.SerializeObject(all));

            Assert.That(read, Is.EquivalentTo(all));
        }

        /// <summary>
        /// The failure the server's filter exists to prevent, pinned so the reason for it stays visible: one row carrying
        /// a key that names no <see cref="HitResult"/> member makes stock Newtonsoft throw for the whole board.
        /// </summary>
        [Test]
        public void ABoardWithAnUnknownKeyFailsToDeserialise()
        {
            const string body = @"{
                ""score_count"": 2,
                ""scores"": [
                    { ""id"": 7, ""rank"": ""X"", ""statistics"": { ""great"": 5 }, ""maximum_statistics"": { ""great"": 5 } },
                    { ""id"": 8, ""rank"": ""X"", ""statistics"": { ""great"": 5, ""zz"": 1 }, ""maximum_statistics"": { ""great"": 5 } }
                ],
                ""user_score"": null
            }";

            Assert.That(() => JsonConvert.DeserializeObject<APIScoresCollection>(body), Throws.InstanceOf<JsonSerializationException>());
        }
    }
}
