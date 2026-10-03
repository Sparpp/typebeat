// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Models;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 396: the UPDATE offer must fire for a stem-only version cut, which changes no <c>.osu</c>
    /// bytes. The signal is stem PRESENCE (<see cref="BeatmapSetInfo.OnlineStemIsMissingLocally"/>): the
    /// online version carries a vocals stem and the local copy has none. Time comparison (394) alone cannot
    /// see this against a client that already had the map, whose version baseline may have adopted the
    /// already-bumped version on its first lookup.
    /// </summary>
    [TestFixture]
    public class StemUpdateSignalTest
    {
        private const string md5 = "0123456789abcdef0123456789abcdef";
        private const string other_md5 = "fedcba9876543210fedcba9876543210";

        private static readonly DateTimeOffset v1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset v2 = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        // ---- the stem-presence predicate ----

        [Test]
        public void OnlineStemAgainstAStemlessLocalCopyOffersUpdate()
        {
            // The exact prod shape: the client already held the map (all MD5s match), the server cut a new
            // version that only added vocals.ogg. Presence is the only signal left.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.RecordOnlineVocalsStem(true);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineStemIsMissingLocally, Is.True);
                Assert.That(set.AllBeatmapsUpToDate, Is.False, "the UPDATE button keys off this");
            });
        }

        [Test]
        public void BothHaveTheStemNoOffer()
        {
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.RecordLocalVocalsStem(true);
            set.RecordOnlineVocalsStem(true);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineStemIsMissingLocally, Is.False);
                Assert.That(set.AllBeatmapsUpToDate, Is.True);
            });
        }

        [Test]
        public void TheStemPresenceIsReadOffTheManagedFileList()
        {
            // RecordLocalVocalsStem() (the parameterless overload) is what import calls while the MANAGED
            // set is in hand: its Files are populated there, so it can read the stem off them.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.Files.Add(new RealmNamedFileUsage(new RealmFile { Hash = "stem" }, VocalsStem.WAV_FILENAME));

            set.RecordLocalVocalsStem();

            Assert.That(set.HasLocalVocalsStem, Is.True, "a vocals.wav counts as a local stem");
        }

        [Test]
        public void ADetachedCopyStillReportsTheStem()
        {
            // The bug: song select's carousel holds DETACHED sets, and the detach mapper strips Files, so
            // VocalsStem.FilenameIn sees nothing on them. The recorded scalar is what carries presence
            // across the detach, so a stem-less managed set that HAS the stem must not offer once detached.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.Files.Add(new RealmNamedFileUsage(new RealmFile { Hash = "stem" }, VocalsStem.OGG_FILENAME));
            set.RecordLocalVocalsStem();
            set.RecordOnlineVocalsStem(true);

            // Emulate the detach: the copy carries the scalar but no file list.
            var detached = new BeatmapSetInfo { OnlineVersionLastUpdated = set.OnlineVersionLastUpdated, OnlineVersionHasVocalsStem = true, HasLocalVocalsStem = set.HasLocalVocalsStem };
            detached.Beatmaps.Add(new BeatmapInfo { MD5Hash = md5, OnlineMD5Hash = md5, OnlineID = 11, BeatmapSet = detached });

            Assert.Multiple(() =>
            {
                Assert.That(VocalsStem.FilenameIn(detached), Is.Null, "the file list really is stripped");
                Assert.That(detached.OnlineStemIsMissingLocally, Is.False, "but the scalar says the stem is present");
                Assert.That(detached.AllBeatmapsUpToDate, Is.True, "so the detached set does not offer an update");
            });
        }

        [Test]
        public void AWavStemCountsAsALocalStem()
        {
            // The game accepts vocals.wav when the producer had no Vorbis encoder; the server's flag is a
            // name test over both, so the local side must accept both too.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.RecordLocalVocalsStem(true);
            set.RecordOnlineVocalsStem(true);

            Assert.That(set.AllBeatmapsUpToDate, Is.True);
        }

        [Test]
        public void LocalStemAgainstAnOnlineWithoutOneNoOffer()
        {
            // A stem the mapper's own aligner produced: the online version has none, so nothing to offer.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.RecordLocalVocalsStem(true);
            set.RecordOnlineVocalsStem(false);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineStemIsMissingLocally, Is.False);
                Assert.That(set.AllBeatmapsUpToDate, Is.True);
            });
        }

        [Test]
        public void WithNoStemReportedTheDefaultIsNoOffer()
        {
            // The realm column's default (false) is "no stem known": a set never looked up, or one whose
            // server sends no key, must not be offered an update for a stem it may not have.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineVersionHasVocalsStem, Is.False);
                Assert.That(set.AllBeatmapsUpToDate, Is.True);
            });
        }

        [Test]
        public void ALocallyModifiedMapStillFiresItsOwnArm()
        {
            // A local edit never enters the version/stem path: its MD5 differs, so the MD5 arm offers as
            // before, and a reported online stem must not cancel that.
            var beatmap = upToDate(other_md5);
            var set = beatmap.BeatmapSet!;
            beatmap.Status = BeatmapOnlineStatus.LocallyModified;
            beatmap.LastOnlineUpdate = v2;
            set.RecordOnlineVocalsStem(true);

            Assert.Multiple(() =>
            {
                Assert.That(beatmap.MatchesOnlineVersion, Is.False, "the MD5 arm still offers");
                Assert.That(set.AllBeatmapsUpToDate, Is.False);
            });
        }

        // ---- the lookup surfaces record the flag ----

        [Test]
        public void TheMetadataLookupRecordsTheStemFlag()
        {
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;

            lookUp(set, v2, hasVocalsStem: true);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineVersionHasVocalsStem, Is.True);
                Assert.That(set.OnlineStemIsMissingLocally, Is.True, "no local stem");
                Assert.That(set.AllBeatmapsUpToDate, Is.False, "so the UPDATE offer stands");
            });
        }

        [Test]
        public void AFailedLookupDoesNotBlankTheStemFlag()
        {
            // A lookup that finds nothing (offline, or a 404) must not turn off an offer that a previous
            // lookup raised.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.RecordOnlineVocalsStem(true);

            using (var lookup = new BeatmapUpdaterMetadataLookup(new FixedSource(null), new FixedSource(null)))
                lookup.Update(set, preferOnlineFetch: true);

            Assert.That(set.OnlineVersionHasVocalsStem, Is.True, "no answer must leave the flag where it was");
        }

        [Test]
        public void TheSetFetchRecordsTheStemFlagToo()
        {
            // RealmPopulatingOnlineLookupSource (song select's set fetch) is the other refresh point; it
            // records the same flag, so the offer is correct whichever lookup ran last.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;

            set.RecordOnlineVocalsStem(true);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineVersionHasVocalsStem, Is.True);
                Assert.That(set.AllBeatmapsUpToDate, Is.False);
            });
        }

        // ---- the fresh-import baseline (394's null-baseline hole) ----

        [Test]
        public void TheImportLookupSeedsTheVersionBaseline()
        {
            // A fresh import runs the metadata lookup with a null baseline (MetadataLookupScope.OnlineFirst ->
            // BeatmapUpdaterMetadataLookup.Update -> AdoptOnlineVersion), so it records the version it saw
            // there and then. Without this, a fresh import would be permanently immune to a newer online
            // version; with it, 394's version arm is meaningful going forward.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            Assert.That(set.OnlineVersionLastUpdated, Is.Null, "the fixture starts with no baseline");

            lookUp(set, v1, hasVocalsStem: false);

            Assert.That(set.OnlineVersionLastUpdated, Is.EqualTo(v1), "the import's lookup seeds the baseline");

            // A later cut (any version bump, stem-only included) is then offered via the version arm even
            // after no further import.
            beatmap.LastOnlineUpdate = v2;
            Assert.That(set.OnlineVersionIsNewer, Is.True);
            Assert.That(set.AllBeatmapsUpToDate, Is.False);
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

        /// <summary>Runs the real metadata lookup against a fixed server version and stem flag.</summary>
        private static void lookUp(BeatmapSetInfo set, DateTimeOffset onlineVersion, bool hasVocalsStem)
        {
            var source = new FixedSource(new OnlineBeatmapMetadata
            {
                BeatmapID = 11,
                BeatmapSetID = 7,
                MD5Hash = md5,
                LastUpdated = onlineVersion,
                BeatmapStatus = BeatmapOnlineStatus.Ranked,
                HasVocalsStem = hasVocalsStem,
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
