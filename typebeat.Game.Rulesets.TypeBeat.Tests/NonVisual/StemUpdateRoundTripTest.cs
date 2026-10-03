// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Platform;
using Realms;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Models;
using typebeat.Game.Online.API;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Tests.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 397. The whole stem-update round trip through the REAL import path: a stem-less set whose
    /// online version gains <c>vocals.ogg</c> offers UPDATE, the accepted update lands the stem, and the
    /// offer clears AND STAYS CLEARED across a simulated restart.
    ///
    /// <para>The bug this pins was in the read, not the update: <see cref="BeatmapSetInfo.OnlineStemIsMissingLocally"/>
    /// compared the online stem flag against <see cref="VocalsStem.FilenameIn"/>, i.e. the set's file list.
    /// But song select's carousel holds DETACHED sets, and the detach mapper strips <c>Files</c> for weight,
    /// so on the very object the button reads the file list is empty and the arm was true forever. The fix is
    /// a scalar <see cref="BeatmapSetInfo.HasLocalVocalsStem"/> recorded at import/update (and backfilled at
    /// realm schema 64), which the detach mapper copies.</para>
    /// </summary>
    [TestFixture]
    public class StemUpdateRoundTripTest
    {
        private const string realm_filename = "client.realm";

        private static readonly DateTimeOffset v1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset v2 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        private string directory = null!;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "typebeat-stem-roundtrip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            LyricBeatmapDecoder.Register();
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(directory, true); } catch { }
        }

        [Test]
        public async Task AStemLandsAndTheOfferClearsAndStaysCleared()
        {
            using var context = new SynchronousRealmTestContext();
            var storage = new NativeStorage(Path.Combine(directory, "data"));

            string oldOsz = makePackage("old.osz", includeStem: false);
            string newOsz = makePackage("new.osz", includeStem: true);

            using var realm = new RealmAccess(storage, realm_filename);
            realm.Write(r => r.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0) { Available = true }));

            var online = new MutableSource { Version = v1, HasStem = false };

            var importer = new BeatmapImporter(storage, realm);
            importer.ProcessBeatmap = (set, scope) =>
            {
                using var lookup = new BeatmapUpdaterMetadataLookup(online, new UnavailableSource());
                lookup.Update(set, scope is MetadataLookupScope.OnlineFirst or MetadataLookupScope.UpdateApplied);

                if (scope == MetadataLookupScope.UpdateApplied)
                    set.MarkUpdatedToOnlineVersion();
            };

            // 1. Install the stem-less version.
            Assert.That(await importer.Import(new ImportTask(oldOsz)).ConfigureAwait(false), Is.Not.Null);
            Guid originalId = realm.Run(r => r.All<BeatmapSetInfo>().Single().ID);

            Assert.Multiple(() =>
            {
                Assert.That(realm.Run(r => r.All<BeatmapSetInfo>().Single().HasLocalVocalsStem), Is.False);
                Assert.That(realm.Run(r => r.All<BeatmapSetInfo>().Single().AllBeatmapsUpToDate), Is.True);
            });

            // 2. The server cuts a stem-bearing version and a lookup sees it: the offer must appear.
            online.Version = v2;
            online.HasStem = true;
            lookUp(realm, online);
            Assert.That(realm.Run(r => r.All<BeatmapSetInfo>().Single().AllBeatmapsUpToDate), Is.False, "the stem-bearing version does not offer UPDATE");

            // 3. Accept the update: the stem lands and the offer clears.
            var updated = await importer.ImportAsUpdate(
                new typebeat.Game.Overlays.Notifications.ProgressNotification(),
                new ImportTask(newOsz),
                realm.Run(r => r.Find<BeatmapSetInfo>(originalId)!.Detach())).ConfigureAwait(false);
            Assert.That(updated, Is.Not.Null, "the update import failed");

            Assert.Multiple(() =>
            {
                Assert.That(realm.Run(r => VocalsStem.FilenameIn(r.All<BeatmapSetInfo>().Single())), Is.EqualTo(VocalsStem.OGG_FILENAME), "the stem landed in the file list");
                Assert.That(realm.Run(r => r.All<BeatmapSetInfo>().Single().HasLocalVocalsStem), Is.True, "and its presence is recorded");
                Assert.That(realm.Run(r => r.All<BeatmapSetInfo>().Single().AllBeatmapsUpToDate), Is.True, "so the offer cleared");
            });

            var detached = realm.Run(r => r.All<BeatmapSetInfo>().Single().Detach());
            Assert.That(detached.Files, Is.Empty, "the carousel copy really has no file list");
            Assert.That(detached.AllBeatmapsUpToDate, Is.True, "but the detached copy still reads up to date (the 397 bug)");

            // 4. Simulated restart: a fresh re-lookup must not re-raise the offer.
            lookUp(realm, online);
            Assert.That(realm.Run(r => r.All<BeatmapSetInfo>().Single().AllBeatmapsUpToDate), Is.True, "the offer stays cleared across a restart");
            Assert.That(realm.Run(r => r.All<BeatmapSetInfo>().Single().Detach().AllBeatmapsUpToDate), Is.True);
        }

        [Test]
        public void Schema64BackfillsTheLocalStemFromTheFileList()
        {
            // A set installed before schema 64 (with the model already carrying HasLocalVocalsStem, so it
            // writes false) that HAS a stem must be backfilled by the migration, or it would offer UPDATE
            // forever. The presence IS recoverable here (the set's own Files backlink), unlike a decode-only fact.
            using var context = new SynchronousRealmTestContext();
            var storage = new NativeStorage(Path.Combine(directory, "migration"));
            const string filename = "client.realm";
            Guid id = Guid.Empty;

            using (var realm = Realm.GetInstance(new RealmConfiguration(storage.GetFullPath(filename, true)) { SchemaVersion = 63 }))
            {
                realm.Write(() =>
                {
                    var ruleset = realm.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0));
                    var set = new BeatmapSetInfo();

                    var beatmap = new BeatmapInfo(ruleset, new BeatmapDifficulty(), new BeatmapMetadata()) { BeatmapSet = set, MD5Hash = "0123456789abcdef0123456789abcdef" };
                    set.Beatmaps.Add(beatmap);
                    set.Files.Add(new RealmNamedFileUsage(new RealmFile { Hash = "stem" }, VocalsStem.OGG_FILENAME));

                    realm.Add(set);
                    id = set.ID;
                });
            }

            using (var access = new RealmAccess(storage, filename))
            {
                Assert.Multiple(() =>
                {
                    Assert.That(access.Run(r => r.Find<BeatmapSetInfo>(id)!.HasLocalVocalsStem), Is.True, "the migration read the stem off the file list");
                    Assert.That(access.Run(r => VocalsStem.FilenameIn(r.Find<BeatmapSetInfo>(id)!)), Is.EqualTo(VocalsStem.OGG_FILENAME));
                });
            }
        }

        private static void lookUp(RealmAccess realm, MutableSource online)
        {
            realm.Write(r =>
            {
                using var lookup = new BeatmapUpdaterMetadataLookup(online, new UnavailableSource());
                lookup.Update(r.All<BeatmapSetInfo>().Single(), preferOnlineFetch: true);
            });
        }

        // ---- helpers ----

        private string makePackage(string name, bool includeStem)
        {
            string path = Path.Combine(directory, name);

            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("Synth Rider - Neon Nights (mapper) [Normal].osu").Open(), new UTF8Encoding(false)))
                    writer.Write(encode());

                using (var writer = new StreamWriter(zip.CreateEntry("audio.mp3").Open()))
                    writer.Write("fake audio");

                if (includeStem)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(VocalsStem.OGG_FILENAME).Open());
                    writer.Write("fake stem");
                }
            }

            return path;
        }

        private static string encode()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.BeatmapInfo.DifficultyName = "Normal";
            beatmap.BeatmapInfo.OnlineID = 11;
            beatmap.Metadata.Artist = "Synth Rider";
            beatmap.Metadata.Title = "Neon Nights";
            beatmap.Metadata.AudioFile = "audio.mp3";

            var line = new LyricLine
            {
                RawText = "hello world",
                StartTime = 1000,
                EndTime = 3000,
                SingEndTime = 2800,
                Units = new[]
                {
                    new TimedUnit { Text = "hello", StartTime = 1000, EndTime = 1900, Source = TimingSource.Explicit },
                    new TimedUnit { Text = "world", StartTime = 1900, EndTime = 2800, Source = TimingSource.Explicit },
                },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = line.StartTime,
                LineIndex = 0,
                Line = line,
                Granularity = TimingGranularity.Word,
            });

            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(beatmap, null, sw);
            return sb.ToString();
        }

        private class MutableSource : IOnlineBeatmapMetadataSource
        {
            public DateTimeOffset Version = v1;
            public bool HasStem;

            public bool Available => true;

            public bool TryLookup(BeatmapInfo beatmapInfo, out OnlineBeatmapMetadata? onlineMetadata)
            {
                onlineMetadata = new OnlineBeatmapMetadata
                {
                    BeatmapID = 11,
                    BeatmapSetID = 7,
                    MD5Hash = beatmapInfo.MD5Hash,
                    LastUpdated = Version,
                    BeatmapStatus = BeatmapOnlineStatus.Ranked,
                    HasVocalsStem = HasStem,
                };
                return true;
            }

            public void Dispose() { }
        }

        private class UnavailableSource : IOnlineBeatmapMetadataSource
        {
            public bool Available => false;
            public bool TryLookup(BeatmapInfo beatmapInfo, out OnlineBeatmapMetadata? onlineMetadata) { onlineMetadata = null; return false; }
            public void Dispose() { }
        }
    }
}
