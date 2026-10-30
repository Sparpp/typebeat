// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Platform;
using Realms;
using Realms.Schema;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Online.API;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 449: the UPDATE offer matches osu!lazer. A difficulty is behind only when its MD5 differs from the
    /// online one (<see cref="BeatmapInfo.MatchesOnlineVersion"/>), so ranking a pending set on the server, which
    /// bumps the set's online timestamp and changes no file, offers nothing. The one addition over lazer is the
    /// tracked-file manifest (backlog 396, pinned in <see cref="StemUpdateSignalTest"/>), which offers a stem the
    /// local copy lacks.
    /// </summary>
    [TestFixture]
    public class UpdateOfferTest
    {
        private const string md5 = "0123456789abcdef0123456789abcdef";
        private const string other_md5 = "fedcba9876543210fedcba9876543210";

        private static readonly DateTimeOffset v1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset v2 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        [Test]
        public void ARankChangeOnTheServerDoesNotOfferUpdate()
        {
            // The field report: a pending set is installed and looked up, then ranked on the server. Ranking
            // bumps beatmapsets.updated_at (sent as last_updated) and changes no .osu byte.
            var beatmap = installed();
            var set = beatmap.BeatmapSet!;

            lookUp(set, v1, md5, BeatmapOnlineStatus.Pending);
            Assert.That(set.AllBeatmapsUpToDate, Is.True, "the pending version offers nothing");

            lookUp(set, v2, md5, BeatmapOnlineStatus.Ranked);

            Assert.Multiple(() =>
            {
                Assert.That(beatmap.LastOnlineUpdate, Is.EqualTo(v2), "the lookup really saw the newer timestamp");
                Assert.That(beatmap.Status, Is.EqualTo(BeatmapOnlineStatus.Ranked), "and the new status");
                Assert.That(beatmap.MatchesOnlineVersion, Is.True);
                Assert.That(set.AllBeatmapsUpToDate, Is.True, "a newer timestamp over unchanged files offers no UPDATE");
            });
        }

        [Test]
        public void AChangedMd5OffersUpdate()
        {
            var beatmap = installed();
            var set = beatmap.BeatmapSet!;

            lookUp(set, v1, md5, BeatmapOnlineStatus.Ranked);
            Assert.That(set.AllBeatmapsUpToDate, Is.True);

            lookUp(set, v2, other_md5, BeatmapOnlineStatus.Ranked);

            Assert.Multiple(() =>
            {
                Assert.That(beatmap.MatchesOnlineVersion, Is.False);
                Assert.That(set.AllBeatmapsUpToDate, Is.False, "the UPDATE button keys off this");
            });
        }

        [Test]
        public void OneChangedDifficultyOffersForTheWholeSet()
        {
            var beatmap = installed();
            var set = beatmap.BeatmapSet!;
            beatmap.LastOnlineUpdate = v1;

            set.Beatmaps.Add(new BeatmapInfo { MD5Hash = md5, OnlineMD5Hash = other_md5, LastOnlineUpdate = v1, BeatmapSet = set });

            Assert.That(set.AllBeatmapsUpToDate, Is.False);
        }

        [Test]
        public void ADifficultyNeverLookedUpIsUpToDate()
        {
            // lazer's rule: no online metadata (LastOnlineUpdate null) reads as matching, whatever the hashes say.
            var beatmap = installed(onlineMd5: other_md5);
            var set = beatmap.BeatmapSet!;

            Assert.Multiple(() =>
            {
                Assert.That(beatmap.LastOnlineUpdate, Is.Null);
                Assert.That(beatmap.MatchesOnlineVersion, Is.True);
                Assert.That(set.AllBeatmapsUpToDate, Is.True);
            });
        }

        [Test]
        public void Schema67DropsTheOldVersionBaseline()
        {
            // A schema 66 install carries OnlineVersionLastUpdated (added at 62), here holding a baseline that lags
            // the online version: under the old rule that set offered UPDATE. At 67 realm drops the column, the set
            // survives the migration, and with its MD5s matching it offers nothing.
            using var context = new SynchronousRealmTestContext();
            string directory = Path.Combine(Path.GetTempPath(), "typebeat-update-offer-migration-" + Guid.NewGuid().ToString("N"));
            const string filename = "client.realm";
            const string column = "OnlineVersionLastUpdated";

            try
            {
                var storage = new NativeStorage(directory);
                string path = storage.GetFullPath(filename, true);
                Guid id = Guid.Empty;

                using (var realm = Realm.GetInstance(new RealmConfiguration(path) { SchemaVersion = 65 }))
                {
                    realm.Write(() =>
                    {
                        var ruleset = realm.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0));
                        var set = new BeatmapSetInfo { OnlineID = 7 };

                        var beatmap = new BeatmapInfo(ruleset, new BeatmapDifficulty(), new BeatmapMetadata())
                        {
                            BeatmapSet = set,
                            MD5Hash = md5,
                            OnlineMD5Hash = md5,
                            OnlineID = 11,
                            LastOnlineUpdate = v2,
                        };
                        set.Beatmaps.Add(beatmap);

                        realm.Add(set);
                        id = set.ID;
                    });
                }

                LegacyRealmColumn.Add(path, 66, "BeatmapSet", Property.Primitive(column, RealmValueType.Date, isNullable: true), realm =>
                {
                    realm.DynamicApi.Find("BeatmapSet", id)!.DynamicApi.Set(column, v1);
                });

                using (var access = new RealmAccess(storage, filename))
                {
                    Assert.Multiple(() =>
                    {
                        Assert.That(access.Run(r => r.Schema.TryFindObjectSchema("BeatmapSet", out var schema) && schema.TryFindProperty(column, out _)), Is.False,
                            "the column is gone");
                        Assert.That(access.Run(r => r.Find<BeatmapSetInfo>(id)!.Beatmaps.Single().LastOnlineUpdate), Is.EqualTo(v2), "the set survived the migration");
                        Assert.That(access.Run(r => r.Find<BeatmapSetInfo>(id)!.AllBeatmapsUpToDate), Is.True);
                    });
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

        private static BeatmapInfo installed(string onlineMd5 = md5)
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

        /// <summary>Runs the real metadata lookup against a fixed server answer.</summary>
        private static void lookUp(BeatmapSetInfo set, DateTimeOffset onlineVersion, string onlineMd5, BeatmapOnlineStatus status)
        {
            var source = new FixedSource(new OnlineBeatmapMetadata
            {
                BeatmapID = 11,
                BeatmapSetID = 7,
                MD5Hash = onlineMd5,
                LastUpdated = onlineVersion,
                BeatmapStatus = status,
                BeatmapSetStatus = status,
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
