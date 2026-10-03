// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Platform;
using Realms;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 394: the UPDATE offer must fire for an online version cut that changes no <c>.osu</c> bytes,
    /// which is exactly how a stem-only version is attached (backlog 393). The signal is the set's stored
    /// <see cref="BeatmapSetInfo.OnlineVersionLastUpdated"/> against the freshly-fetched online version; the
    /// MD5 path alone cannot see such a cut because every difficulty's hash is unchanged.
    /// </summary>
    [TestFixture]
    public class UpdateVersionSignalTest
    {
        private const string md5 = "0123456789abcdef0123456789abcdef";
        private const string other_md5 = "fedcba9876543210fedcba9876543210";

        private static readonly DateTimeOffset v1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset v2 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        // ---- the predicate ----

        [Test]
        public void AStoredVersionBehindTheOnlineOneOffersUpdate()
        {
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.OnlineVersionLastUpdated = v1;
            beatmap.LastOnlineUpdate = v2;

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineVersionIsNewer, Is.True);
                Assert.That(set.AllBeatmapsUpToDate, Is.False, "the UPDATE button keys off this");
            });
        }

        [Test]
        public void ASetAlreadyAtTheOnlineVersionDoesNot()
        {
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.OnlineVersionLastUpdated = v2;
            beatmap.LastOnlineUpdate = v2;

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineVersionIsNewer, Is.False);
                Assert.That(set.AllBeatmapsUpToDate, Is.True);
            });
        }

        [Test]
        public void ALocallyModifiedMapIsUnaffected()
        {
            // A local edit never entering the version path: its MD5 differs from the online one, so it is
            // offered via the MD5 arm exactly as before, and the version arm stays quiet (no stored baseline).
            var beatmap = upToDate(other_md5);
            var set = beatmap.BeatmapSet!;
            beatmap.Status = BeatmapOnlineStatus.LocallyModified;
            beatmap.LastOnlineUpdate = v2;

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineVersionIsNewer, Is.False, "no baseline was ever recorded");
                Assert.That(beatmap.MatchesOnlineVersion, Is.False, "the MD5 arm still offers");
                Assert.That(set.AllBeatmapsUpToDate, Is.False);
            });
        }

        [Test]
        public void AnUnlookedUpMapWithoutABaselineIsNotBehind()
        {
            // The null baseline is "no version recorded", not "at version 0". Without that guard every map
            // would read as behind on the first lookup.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            beatmap.LastOnlineUpdate = v2;

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineVersionLastUpdated, Is.Null);
                Assert.That(set.OnlineVersionIsNewer, Is.False);
                Assert.That(set.AllBeatmapsUpToDate, Is.True);
            });
        }

        [Test]
        public void TheNewestDifficultyDecides()
        {
            // Every difficulty carries the same server version, but the predicate must not depend on any
            // one of them being the one looked up.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.OnlineVersionLastUpdated = v1;
            beatmap.LastOnlineUpdate = v2;

            var second = new BeatmapInfo { MD5Hash = md5, OnlineMD5Hash = md5, LastOnlineUpdate = null, BeatmapSet = set };
            set.Beatmaps.Add(second);

            Assert.That(set.OnlineVersionIsNewer, Is.True, "the first difficulty alone raises the flag");

            second.LastOnlineUpdate = v2;
            beatmap.LastOnlineUpdate = null;
            Assert.That(set.OnlineVersionIsNewer, Is.True, "the second difficulty alone can raise the flag");
        }

        // ---- adoption ----

        [Test]
        public void AdoptRecordsTheFirstOnlineVersion()
        {
            var set = new BeatmapSetInfo();

            set.AdoptOnlineVersion(v1);

            Assert.That(set.OnlineVersionLastUpdated, Is.EqualTo(v1));
        }

        [Test]
        public void AdoptDoesNotAdvancePastANewerOnlineVersion()
        {
            // The load-bearing rule: while the local copy is behind, the baseline must stay where it is, or
            // the offer would flash once and settle instead of persisting until the user updates.
            var set = new BeatmapSetInfo { OnlineVersionLastUpdated = v1 };

            set.AdoptOnlineVersion(v2);

            Assert.That(set.OnlineVersionLastUpdated, Is.EqualTo(v1), "still behind");
        }

        [Test]
        public void AdoptAdvancesWhenTheOnlineVersionIsNotNewer()
        {
            var set = new BeatmapSetInfo { OnlineVersionLastUpdated = v2 };

            set.AdoptOnlineVersion(v1);

            Assert.That(set.OnlineVersionLastUpdated, Is.EqualTo(v1));
        }

        // ---- the lookup surface ----

        [Test]
        public void APlainLookupDoesNotOfferASpuriousUpdateAndRecordsTheVersion()
        {
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;

            lookUp(set, v2);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineVersionLastUpdated, Is.EqualTo(v2), "the lookup records the version it saw");
                Assert.That(set.OnlineVersionIsNewer, Is.False);
                Assert.That(set.AllBeatmapsUpToDate, Is.True, "an unchanged map is never offered an update");
            });
        }

        [Test]
        public void ALookupOfANewerVersionLeavesTheStoredBaselineAlone()
        {
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.OnlineVersionLastUpdated = v1;

            lookUp(set, v2);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineVersionLastUpdated, Is.EqualTo(v1), "the baseline is not overwritten while behind");
                Assert.That(set.AllBeatmapsUpToDate, Is.False, "so the offer persists");
            });
        }

        [Test]
        public void ASecondLookupOfTheSameVersionStillDoesNotOffer()
        {
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;

            lookUp(set, v2);
            lookUp(set, v2);

            Assert.That(set.AllBeatmapsUpToDate, Is.True);
        }

        [Test]
        public void TheSetFetchAdoptsTheVersionToo()
        {
            // RealmPopulatingOnlineLookupSource (song select's set fetch) is the other lookup surface and
            // must behave identically, or the two would disagree about when a set is behind.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;

            set.AdoptOnlineVersion(v2);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineVersionLastUpdated, Is.EqualTo(v2));
                Assert.That(set.OnlineVersionIsNewer, Is.False);
            });
        }

        // ---- the accepted update clears the offer ----

        [Test]
        public void AnAcceptedUpdateClearsTheOffer()
        {
            // The offer persisted because the baseline was behind; pressing UPDATE downloads the newer
            // version, the update's lookup records it, and the set is no longer behind (the button fades).
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.OnlineVersionLastUpdated = v1;
            beatmap.LastOnlineUpdate = v2;

            Assert.That(set.AllBeatmapsUpToDate, Is.False);

            set.MarkUpdatedToOnlineVersion();

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineVersionLastUpdated, Is.EqualTo(v2));
                Assert.That(set.AllBeatmapsUpToDate, Is.True);
            });
        }

        [Test]
        public void MarkUpdatedTakesTheNewestDifficultyVersion()
        {
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            beatmap.LastOnlineUpdate = v1;

            var second = new BeatmapInfo { MD5Hash = md5, OnlineMD5Hash = md5, LastOnlineUpdate = v2, BeatmapSet = set };
            set.Beatmaps.Add(second);

            set.MarkUpdatedToOnlineVersion();

            Assert.That(set.OnlineVersionLastUpdated, Is.EqualTo(v2));
        }

        [Test]
        public void MarkUpdatedWithNoVersionSeenLeavesTheBaseline()
        {
            // The lookup failed (never reached the server, so LastOnlineUpdate was reset): there is no
            // version to record, and the baseline must not be blanked speculatively.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.OnlineVersionLastUpdated = v1;
            beatmap.LastOnlineUpdate = null;

            set.MarkUpdatedToOnlineVersion();

            Assert.That(set.OnlineVersionLastUpdated, Is.EqualTo(v1));
        }

        // ---- a genuine .osu change still uses the MD5 path ----

        [Test]
        public void AGenuineOsuChangeStillOffersViaMd5()
        {
            var beatmap = upToDate(other_md5);
            var set = beatmap.BeatmapSet!;
            set.OnlineVersionLastUpdated = v2;
            beatmap.LastOnlineUpdate = v2;

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineVersionIsNewer, Is.False);
                Assert.That(beatmap.MatchesOnlineVersion, Is.False, "the MD5 mismatch alone offers the update");
                Assert.That(set.AllBeatmapsUpToDate, Is.False);
            });
        }

        // ---- realm schema ----

        [Test]
        public void Schema62LeavesEveryExistingSetWithoutABaseline()
        {
            // Realm adds a new column automatically at the bump; the type default (null) is what a schema 61
            // file reads, meaning "no version recorded yet". No migration body is needed.
            using var context = new SynchronousRealmTestContext();
            string directory = Path.Combine(Path.GetTempPath(), "typebeat-update-version-migration-" + Guid.NewGuid().ToString("N"));
            const string filename = "client.realm";

            try
            {
                var storage = new NativeStorage(directory);
                Guid id = Guid.Empty;

                using (var realm = Realm.GetInstance(new RealmConfiguration(storage.GetFullPath(filename, true)) { SchemaVersion = 61 }))
                {
                    realm.Write(() =>
                    {
                        var ruleset = realm.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0));
                        var set = new BeatmapSetInfo();

                        var beatmap = new BeatmapInfo(ruleset, new BeatmapDifficulty(), new BeatmapMetadata()) { BeatmapSet = set, MD5Hash = md5 };
                        set.Beatmaps.Add(beatmap);

                        realm.Add(set);
                        id = set.ID;
                    });
                }

                using (var access = new RealmAccess(storage, filename))
                {
                    var baseline = access.Run(r => r.Find<BeatmapSetInfo>(id)!.OnlineVersionLastUpdated);

                    Assert.That(baseline, Is.Null, "the column's default is 'no version recorded'");
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch
                {
                    // a leftover temp directory is not worth failing a test over.
                }
            }
        }

        // ---- helpers ----

        private static BeatmapInfo upToDate(string onlineMd5 = md5)
        {
            var set = new BeatmapSetInfo();
            var beatmap = new BeatmapInfo
            {
                MD5Hash = md5,
                OnlineMD5Hash = onlineMd5,
                OnlineID = 11,
                BeatmapSet = set,
            };

            set.Beatmaps.Add(beatmap);
            set.OnlineID = 7;
            return beatmap;
        }

        /// <summary>Runs the real metadata lookup against a fixed server version.</summary>
        private static void lookUp(BeatmapSetInfo set, DateTimeOffset onlineVersion)
        {
            var source = new FixedSource(new OnlineBeatmapMetadata
            {
                BeatmapID = 11,
                BeatmapSetID = 7,
                MD5Hash = md5,
                LastUpdated = onlineVersion,
                BeatmapStatus = BeatmapOnlineStatus.Ranked,
            });

            using var lookup = new BeatmapUpdaterMetadataLookup(source, new FixedSource(null));
            lookup.Update(set, preferOnlineFetch: true);

            Assert.That(set.Beatmaps.Single().OnlineID, Is.EqualTo(11), "the lookup must have matched for the test to mean anything");
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
