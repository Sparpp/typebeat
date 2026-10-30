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
    /// bytes. The signal is stem PRESENCE (<see cref="BeatmapSetInfo.OnlineFilesMissingLocally"/>): the
    /// online version carries a vocals stem and the local copy has none. The per-difficulty MD5 cannot see
    /// such a cut, and the set's online timestamp is no signal either, since a status change on the server
    /// moves it too (backlog 449). Since realm schema 65 the presence travels as one tracked-file manifest
    /// per side (<see cref="SetFileManifest"/>) rather than a pair of stem flags.
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
            set.RecordOnlineFileManifest(SetFileManifest.VOCALS_STEM);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineFilesMissingLocally, Is.True);
                Assert.That(set.AllBeatmapsUpToDate, Is.False, "the UPDATE button keys off this");
            });
        }

        [Test]
        public void BothHaveTheStemNoOffer()
        {
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.RecordLocalFileManifest(SetFileManifest.VOCALS_STEM);
            set.RecordOnlineFileManifest(SetFileManifest.VOCALS_STEM);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineFilesMissingLocally, Is.False);
                Assert.That(set.AllBeatmapsUpToDate, Is.True);
            });
        }

        [Test]
        public void TheStemPresenceIsReadOffTheManagedFileList()
        {
            // RecordLocalFileManifest() (the parameterless overload) is what import calls while the MANAGED
            // set is in hand: its Files are populated there, so it can read the stem off them.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.Files.Add(new RealmNamedFileUsage(new RealmFile { Hash = "stem" }, VocalsStem.WAV_FILENAME));

            set.RecordLocalFileManifest();

            Assert.That(set.LocalFileManifest, Is.EqualTo(SetFileManifest.VOCALS_STEM), "a vocals.wav counts as a local stem");
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
            set.RecordLocalFileManifest();
            set.RecordOnlineFileManifest(SetFileManifest.VOCALS_STEM);

            // Emulate the detach: the copy carries the scalar but no file list.
            var detached = new BeatmapSetInfo { OnlineFileManifest = SetFileManifest.VOCALS_STEM, LocalFileManifest = set.LocalFileManifest };
            detached.Beatmaps.Add(new BeatmapInfo { MD5Hash = md5, OnlineMD5Hash = md5, OnlineID = 11, BeatmapSet = detached });

            Assert.Multiple(() =>
            {
                Assert.That(VocalsStem.FilenameIn(detached), Is.Null, "the file list really is stripped");
                Assert.That(detached.OnlineFilesMissingLocally, Is.False, "but the scalar says the stem is present");
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
            set.RecordLocalFileManifest(SetFileManifest.VOCALS_STEM);
            set.RecordOnlineFileManifest(SetFileManifest.VOCALS_STEM);

            Assert.That(set.AllBeatmapsUpToDate, Is.True);
        }

        [Test]
        public void LocalStemAgainstAnOnlineWithoutOneNoOffer()
        {
            // A stem the mapper's own aligner produced: the online version has none, so nothing to offer.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.RecordLocalFileManifest(SetFileManifest.VOCALS_STEM);
            set.RecordOnlineFileManifest(SetFileManifest.EMPTY);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineFilesMissingLocally, Is.False);
                Assert.That(set.AllBeatmapsUpToDate, Is.True);
            });
        }

        [Test]
        public void WithNoStemReportedTheDefaultIsNoOffer()
        {
            // The realm column's default (null) is "nothing reported yet": a set never looked up must not be
            // offered an update for a stem it may not have.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineFileManifest, Is.Null);
                Assert.That(set.AllBeatmapsUpToDate, Is.True);
            });
        }

        [Test]
        public void ALocallyModifiedMapStillFiresItsOwnArm()
        {
            // A local edit never enters the stem path: its MD5 differs, so the MD5 arm offers as
            // before, and a reported online stem must not cancel that.
            var beatmap = upToDate(other_md5);
            var set = beatmap.BeatmapSet!;
            beatmap.Status = BeatmapOnlineStatus.LocallyModified;
            beatmap.LastOnlineUpdate = v2;
            set.RecordOnlineFileManifest(SetFileManifest.VOCALS_STEM);

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
                Assert.That(set.OnlineFileManifest, Is.EqualTo(SetFileManifest.VOCALS_STEM));
                Assert.That(set.OnlineFilesMissingLocally, Is.True, "no local stem");
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
            set.RecordOnlineFileManifest(SetFileManifest.VOCALS_STEM);

            using (var lookup = new BeatmapUpdaterMetadataLookup(new FixedSource(null), new FixedSource(null)))
                lookup.Update(set, preferOnlineFetch: true);

            Assert.That(set.OnlineFileManifest, Is.EqualTo(SetFileManifest.VOCALS_STEM), "no answer must leave the manifest where it was");
        }

        [Test]
        public void TheSetFetchRecordsTheStemFlagToo()
        {
            // RealmPopulatingOnlineLookupSource (song select's set fetch) is the other refresh point; it
            // records the same flag, so the offer is correct whichever lookup ran last.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;

            set.RecordOnlineFileManifest(SetFileManifest.VOCALS_STEM);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineFileManifest, Is.EqualTo(SetFileManifest.VOCALS_STEM));
                Assert.That(set.AllBeatmapsUpToDate, Is.False);
            });
        }

        // ---- a stem-only cut, seen by lookups alone ----

        [Test]
        public void AStemOnlyCutIsOfferedThroughTheManifestAlone()
        {
            // The import's lookup sees a stem-less version; a later lookup sees a newer version that only added
            // vocals.ogg. Every MD5 still matches, so the offer stands on the manifest arm alone, and recording the
            // stem locally (what the accepted update does) clears it.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;

            lookUp(set, v1, hasVocalsStem: false);
            Assert.That(set.AllBeatmapsUpToDate, Is.True, "the stem-less version offers nothing");

            lookUp(set, v2, hasVocalsStem: true);

            Assert.Multiple(() =>
            {
                Assert.That(beatmap.LastOnlineUpdate, Is.EqualTo(v2), "the lookup really saw the newer version");
                Assert.That(beatmap.MatchesOnlineVersion, Is.True, "the MD5 arm is quiet");
                Assert.That(set.OnlineFilesMissingLocally, Is.True);
                Assert.That(set.AllBeatmapsUpToDate, Is.False, "so the manifest arm alone offers UPDATE");
            });

            set.RecordLocalFileManifest(SetFileManifest.VOCALS_STEM);

            Assert.That(set.AllBeatmapsUpToDate, Is.True, "having the stem clears the offer");
        }

        // ---- the manifest itself ----

        [Test]
        public void ALookupWithoutAManifestDoesNotBlankIt()
        {
            // The local metadata cache answers a lookup but carries no manifest; that is no answer about the
            // files, so an offer a previous online lookup raised must stand.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.RecordOnlineFileManifest(SetFileManifest.VOCALS_STEM);

            lookUp(set, v1, manifest: null);

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineFileManifest, Is.EqualTo(SetFileManifest.VOCALS_STEM));
                Assert.That(set.AllBeatmapsUpToDate, Is.False);
            });
        }

        [Test]
        public void AServerWithoutTheManifestIsReadThroughItsStemFlag()
        {
            // A server that predates file_manifest still sends has_vocals_stem, which describes the same thing.
            Assert.Multiple(() =>
            {
                Assert.That(SetFileManifest.Online(null, hasVocalsStem: true), Is.EqualTo(SetFileManifest.VOCALS_STEM));
                Assert.That(SetFileManifest.Online(null, hasVocalsStem: false), Is.EqualTo(SetFileManifest.EMPTY));
                Assert.That(SetFileManifest.Online(SetFileManifest.EMPTY, hasVocalsStem: true), Is.EqualTo(SetFileManifest.EMPTY), "a published manifest wins");
                Assert.That(SetFileManifest.Online(" Vocals ", hasVocalsStem: false), Is.EqualTo(SetFileManifest.VOCALS_STEM), "and is read canonically");
            });
        }

        [Test]
        public void TheManifestNamesKindsNotFiles()
        {
            Assert.Multiple(() =>
            {
                Assert.That(SetFileManifest.Of(new[] { "audio.mp3", "bg.jpg", "map.osu" }), Is.EqualTo(SetFileManifest.EMPTY));
                Assert.That(SetFileManifest.Of(new[] { "audio.mp3", "VOCALS.OGG" }), Is.EqualTo(SetFileManifest.VOCALS_STEM), "names compare case-insensitively");
                Assert.That(SetFileManifest.Of(new[] { "vocals.ogg", "vocals.wav" }), Is.EqualTo(SetFileManifest.VOCALS_STEM), "two names of one kind are one kind");
                Assert.That(SetFileManifest.Of(new[] { "stems/vocals.ogg" }), Is.EqualTo(SetFileManifest.EMPTY), "a stem lives beside the audio, not in a folder");
            });
        }

        [Test]
        public void AKindThisClientDoesNotKnowOffersNothing()
        {
            // A newer server may publish a kind this build has no row for. This client can never record it in
            // its local manifest, so comparing it would offer an update no download could clear: it is kept in
            // the stored value but ignored, while the kinds this client knows still compare as before.
            var beatmap = upToDate();
            var set = beatmap.BeatmapSet!;
            set.RecordLocalFileManifest(SetFileManifest.VOCALS_STEM);
            set.RecordOnlineFileManifest("lyricvideo,vocals");

            Assert.Multiple(() =>
            {
                Assert.That(set.OnlineFileManifest, Is.EqualTo("lyricvideo,vocals"), "the reported value is stored as published");
                Assert.That(set.AllBeatmapsUpToDate, Is.True, "an unknown online kind offers nothing");
            });

            set.RecordLocalFileManifest(SetFileManifest.EMPTY);

            Assert.That(set.AllBeatmapsUpToDate, Is.False, "a known kind the local copy lacks still offers");

            set.RecordLocalFileManifest("lyricvideo,vocals");
            set.RecordOnlineFileManifest(SetFileManifest.VOCALS_STEM);

            Assert.That(set.AllBeatmapsUpToDate, Is.True, "a local kind the online version lacks offers nothing");
        }

        [Test]
        public void TheUnionOfSeveralReportsKeepsEveryKind()
        {
            Assert.Multiple(() =>
            {
                Assert.That(SetFileManifest.Union(new string?[] { null, null }), Is.Null, "no report is no answer");
                Assert.That(SetFileManifest.Union(new string?[] { null, SetFileManifest.EMPTY }), Is.EqualTo(SetFileManifest.EMPTY));
                Assert.That(SetFileManifest.Union(new string?[] { SetFileManifest.EMPTY, SetFileManifest.VOCALS_STEM }), Is.EqualTo(SetFileManifest.VOCALS_STEM));
                Assert.That(SetFileManifest.Union(new string?[] { "vocals", "lyricvideo" }), Is.EqualTo("lyricvideo,vocals"));
            });
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
            => lookUp(set, onlineVersion, hasVocalsStem ? SetFileManifest.VOCALS_STEM : SetFileManifest.EMPTY);

        /// <summary>Runs the real metadata lookup against a fixed server version and file manifest (null: none reported).</summary>
        private static void lookUp(BeatmapSetInfo set, DateTimeOffset onlineVersion, string? manifest)
        {
            var source = new FixedSource(new OnlineBeatmapMetadata
            {
                BeatmapID = 11,
                BeatmapSetID = 7,
                MD5Hash = md5,
                LastUpdated = onlineVersion,
                BeatmapStatus = BeatmapOnlineStatus.Ranked,
                FileManifest = manifest,
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
