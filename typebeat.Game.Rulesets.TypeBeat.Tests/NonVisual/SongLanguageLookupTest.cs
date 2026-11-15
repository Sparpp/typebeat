// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The oldest server sets were uploaded before the <c>[Metadata] Language:</c> line existed, so their files decode to
    /// <see cref="BeatmapLanguage.Unspecified"/> while the server knows the language (<c>beatmapsets.language</c>). The
    /// beatmap lookup carries it as <c>beatmapset.song_language</c> (<see cref="APIBeatmapSet.SongLanguage"/>), and
    /// <see cref="BeatmapUpdaterMetadataLookup"/> fills the gap with it. Pins the wire shape, the mapping, and that the
    /// fill never overrides a language the row already holds.
    /// </summary>
    [TestFixture]
    public class SongLanguageLookupTest
    {
        private const string md5 = "0123456789abcdef0123456789abcdef";

        // ---- the wire ----

        [Test]
        public void EveryLanguageSurvivesTheLookupResponse()
        {
            foreach (var language in Enum.GetValues<BeatmapLanguage>())
            {
                string json = lookupJson($@"""song_language"": ""{language.ToCanonicalName()}""");

                Assert.That(lookUp(json)!.Language, Is.EqualTo(language), language.ToString());
            }
        }

        [Test]
        public void AResponseWithoutTheFieldReadsAsUnspecified()
        {
            // Every server before this change, and any endpoint that does not send it.
            var response = JsonConvert.DeserializeObject<APIBeatmap>(lookupJson(null))!;

            Assert.Multiple(() =>
            {
                Assert.That(response.BeatmapSet!.SongLanguage, Is.Empty);
                Assert.That(lookUp(lookupJson(null))!.Language, Is.EqualTo(BeatmapLanguage.Unspecified));
            });
        }

        [TestCase(@"""song_language"": """"")]
        [TestCase(@"""song_language"": ""klingon""")]
        [TestCase(@"""song_language"": null")]
        public void AnEmptyOrUnknownValueReadsAsUnspecified(string field)
            => Assert.That(lookUp(lookupJson(field))!.Language, Is.EqualTo(BeatmapLanguage.Unspecified));

        [Test]
        public void OsuWebsLanguageObjectIsUntouched()
        {
            // `language` stays osu-web's {id, name} object; the song language travels beside it, never in it.
            var response = JsonConvert.DeserializeObject<APIBeatmap>(lookupJson(@"""song_language"": ""japanese"", ""language"": { ""id"": 3, ""name"": ""Japanese"" }"))!;

            Assert.Multiple(() =>
            {
                Assert.That(response.BeatmapSet!.SongLanguage, Is.EqualTo("japanese"));
                Assert.That(response.BeatmapSet.Language.Id, Is.EqualTo(3));
                Assert.That(response.BeatmapSet.Language.Name, Is.EqualTo("Japanese"));
            });
        }

        // ---- the language list (backlog 472) ----

        [Test]
        public void TheLanguageListArrivesWholeAndInOrder()
        {
            var result = lookUp(lookupJson(@"""song_language"": ""japanese"", ""song_languages"": [""english"", ""japanese""]"))!;

            Assert.Multiple(() =>
            {
                Assert.That(result.Languages, Is.EqualTo(new[] { BeatmapLanguage.English, BeatmapLanguage.Japanese }));
                Assert.That(result.Language, Is.EqualTo(BeatmapLanguage.Japanese), "the primary is the first non-English entry");
            });
        }

        [Test]
        public void AServerWithoutTheListFallsBackToTheSingleLanguage()
        {
            // Every server before backlog 472 sends song_language only.
            var result = lookUp(lookupJson(@"""song_language"": ""korean"""))!;

            Assert.That(result.Languages, Is.EqualTo(new[] { BeatmapLanguage.Korean }));
        }

        [Test]
        public void AListOfOnlyUnknownLanguagesFallsBackToTheSingleLanguage()
        {
            // A newer server naming languages this build has never heard of: the primary it also sends still lands.
            var result = lookUp(lookupJson(@"""song_language"": ""french"", ""song_languages"": [""klingon"", ""elvish""]"))!;

            Assert.That(result.Languages, Is.EqualTo(new[] { BeatmapLanguage.French }));
        }

        [Test]
        public void TheLookupFillsAnUnspecifiedRowWithTheWholeList()
        {
            var beatmap = row(BeatmapLanguage.Unspecified);

            var source = new FixedSource(new OnlineBeatmapMetadata
            {
                BeatmapID = 11,
                BeatmapSetID = 7,
                MD5Hash = md5,
                LastUpdated = DateTimeOffset.UtcNow,
                BeatmapStatus = BeatmapOnlineStatus.Ranked,
                Languages = new[] { BeatmapLanguage.Japanese, BeatmapLanguage.English },
            });

            using (var lookup = new BeatmapUpdaterMetadataLookup(source, new FixedSource(null)))
                lookup.Update(beatmap.BeatmapSet!, preferOnlineFetch: true);

            Assert.That(beatmap.Metadata.Languages, Is.EqualTo(new[] { BeatmapLanguage.Japanese, BeatmapLanguage.English }));
        }

        // ---- the fill ----

        [Test]
        public void TheLookupFillsAnUnspecifiedRow()
        {
            var beatmap = row(BeatmapLanguage.Unspecified);

            update(beatmap, BeatmapLanguage.Japanese);

            Assert.That(beatmap.Metadata.Language, Is.EqualTo(BeatmapLanguage.Japanese));
        }

        [Test]
        public void TheLookupLeavesAStatedLanguageAlone()
        {
            // The row's language came from its own file, which is the authority.
            var beatmap = row(BeatmapLanguage.English);

            update(beatmap, BeatmapLanguage.Japanese);

            Assert.That(beatmap.Metadata.Language, Is.EqualTo(BeatmapLanguage.English));
        }

        [Test]
        public void TheServerHavingNoLanguageLeavesTheRowUnspecified()
        {
            var beatmap = row(BeatmapLanguage.Unspecified);

            update(beatmap, BeatmapLanguage.Unspecified);

            Assert.That(beatmap.Metadata.Language, Is.EqualTo(BeatmapLanguage.Unspecified));
        }

        [Test]
        public void ALocallyModifiedRowIsNotFilled()
        {
            // Like every other piece of online metadata, only applied while the local file is the online one.
            var beatmap = row(BeatmapLanguage.Unspecified);

            update(beatmap, BeatmapLanguage.Japanese, onlineMd5: "fedcba9876543210fedcba9876543210");

            Assert.That(beatmap.Metadata.Language, Is.EqualTo(BeatmapLanguage.Unspecified));
        }

        // ---- already installed line-less sets ----

        [Test]
        public void AnInstalledLineLessOnlineRowIsHandedBackToTheLookup()
        {
            var beatmap = installed(BeatmapLanguage.Unspecified);

            Assert.That(BackgroundDataStoreProcessor.NeedsOnlineLanguage(beatmap), Is.True);
        }

        [Test]
        public void OnlyALineLessOnlineRowThatStillMatchesTheServerIsHandedBack()
        {
            var stated = installed(BeatmapLanguage.English);

            var local = installed(BeatmapLanguage.Unspecified);
            local.OnlineID = -1;

            var modified = installed(BeatmapLanguage.Unspecified);
            modified.OnlineMD5Hash = "fedcba9876543210fedcba9876543210";

            var neverLookedUp = installed(BeatmapLanguage.Unspecified);
            neverLookedUp.LastOnlineUpdate = null;

            Assert.Multiple(() =>
            {
                Assert.That(BackgroundDataStoreProcessor.NeedsOnlineLanguage(stated), Is.False, "the file stated one");
                Assert.That(BackgroundDataStoreProcessor.NeedsOnlineLanguage(local), Is.False, "the server does not know it");
                Assert.That(BackgroundDataStoreProcessor.NeedsOnlineLanguage(modified), Is.False, "a local edit must keep its own state");
                Assert.That(BackgroundDataStoreProcessor.NeedsOnlineLanguage(neverLookedUp), Is.False, "already queued for the online pass");
            });
        }

        // ---- helpers ----

        private static BeatmapInfo installed(BeatmapLanguage language)
        {
            var beatmap = row(language);
            beatmap.OnlineID = 11;
            beatmap.OnlineMD5Hash = md5;
            beatmap.LastOnlineUpdate = DateTimeOffset.UtcNow;
            return beatmap;
        }

        private static string lookupJson(string? setField)
        {
            string extra = setField == null ? string.Empty : ", " + setField;
            return $@"{{ ""id"": 11, ""beatmapset_id"": 7, ""checksum"": ""{md5}"", ""beatmapset"": {{ ""id"": 7{extra} }} }}";
        }

        /// <summary>Runs <paramref name="json"/> through the real <see cref="APIBeatmapMetadataSource"/> as the server's lookup response.</summary>
        private static OnlineBeatmapMetadata? lookUp(string json)
        {
            var api = new DummyAPIAccess
            {
                HandleRequest = request =>
                {
                    if (request is not GetBeatmapRequest lookup)
                        return false;

                    lookup.TriggerSuccess(JsonConvert.DeserializeObject<APIBeatmap>(json)!);
                    return true;
                }
            };

            var set = new BeatmapSetInfo();
            var beatmap = new BeatmapInfo { MD5Hash = md5, BeatmapSet = set };
            set.Beatmaps.Add(beatmap);

            Assert.That(new APIBeatmapMetadataSource(api).TryLookup(beatmap, out var result), Is.True);
            return result;
        }

        private static BeatmapInfo row(BeatmapLanguage language)
        {
            var set = new BeatmapSetInfo();
            var beatmap = new BeatmapInfo(new TypeBeatRuleset().RulesetInfo, new BeatmapDifficulty(), new BeatmapMetadata { Language = language })
            {
                MD5Hash = md5,
                BeatmapSet = set,
            };

            set.Beatmaps.Add(beatmap);
            return beatmap;
        }

        private static void update(BeatmapInfo beatmap, BeatmapLanguage serverLanguage, string onlineMd5 = md5)
        {
            var source = new FixedSource(new OnlineBeatmapMetadata
            {
                BeatmapID = 11,
                BeatmapSetID = 7,
                MD5Hash = onlineMd5,
                LastUpdated = DateTimeOffset.UtcNow,
                BeatmapStatus = BeatmapOnlineStatus.Ranked,
                Language = serverLanguage,
            });

            using var lookup = new BeatmapUpdaterMetadataLookup(source, new FixedSource(null));
            lookup.Update(beatmap.BeatmapSet!, preferOnlineFetch: true);

            Assert.That(beatmap.BeatmapSet!.Beatmaps.Single().OnlineID, Is.EqualTo(11), "the lookup must have matched for the test to mean anything");
        }

        /// <summary>A metadata source that answers every lookup with one fixed result, or is unavailable when it has none.</summary>
        private class FixedSource : IOnlineBeatmapMetadataSource
        {
            private readonly OnlineBeatmapMetadata? result;

            public FixedSource(OnlineBeatmapMetadata? result) => this.result = result;

            public bool Available => result != null;

            public bool TryLookup(BeatmapInfo beatmapInfo, out OnlineBeatmapMetadata? onlineMetadata)
            {
                onlineMetadata = result;
                return result != null;
            }

            public void Dispose()
            {
            }
        }
    }
}
