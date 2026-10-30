// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Newtonsoft.Json;
using typebeat.Game.Database;
using typebeat.Game.Extensions;
using typebeat.Game.Models;
using Realms;

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// A realm model containing metadata for a beatmap set (containing multiple <see cref="BeatmapInfo"/>s).
    /// </summary>
    [MapTo("BeatmapSet")]
    public class BeatmapSetInfo : RealmObject, IHasGuidPrimaryKey, IHasRealmFiles, ISoftDelete, IEquatable<BeatmapSetInfo>, IBeatmapSetInfo
    {
        [PrimaryKey]
        public Guid ID { get; set; }

        [Indexed]
        public int OnlineID { get; set; } = -1;

        public DateTimeOffset DateAdded { get; set; }

        /// <summary>
        /// The date this beatmap set was first submitted.
        /// </summary>
        public DateTimeOffset? DateSubmitted { get; set; }

        /// <summary>
        /// The date this beatmap set was ranked.
        /// </summary>
        public DateTimeOffset? DateRanked { get; set; }

        [JsonIgnore]
        public IBeatmapMetadataInfo Metadata => Beatmaps.FirstOrDefault()?.Metadata ?? new BeatmapMetadata();

        public IList<BeatmapInfo> Beatmaps { get; } = null!;

        public IList<RealmNamedFileUsage> Files { get; } = null!;

        [Ignored]
        public BeatmapOnlineStatus Status
        {
            get => (BeatmapOnlineStatus)StatusInt;
            set => StatusInt = (int)value;
        }

        [MapTo(nameof(Status))]
        public int StatusInt { get; set; } = (int)BeatmapOnlineStatus.None;

        public bool DeletePending { get; set; }

        public string Hash { get; set; } = string.Empty;

        /// <summary>
        /// Whether deleting this beatmap set should be prohibited (due to it being a system requirement to be present).
        /// </summary>
        public bool Protected { get; set; }

        /// <summary>
        /// Whether this set may be picked to soundtrack the game intro, overriding the default (which is
        /// "yes if ANY of its difficulties declares an intro beatdrop", see <see cref="IBeatmap.IntroBeatdropTime"/>).
        /// <c>null</c> means no override: follow the beatdrops. <c>false</c> keeps a beatdrop-carrying set out
        /// of the pool without touching any authored timestamp; <c>true</c> opts a set with no beatdrop in.
        /// Decided by the "Use on game intro" song select context menu toggle on the set panel and read by
        /// <see cref="Screens.Menu.IntroBeatdropPool"/>.
        /// </summary>
        /// <remarks>
        /// This is user data, not map content: it lives in realm rather than in any beatmap file so that
        /// toggling it never re-encodes (and never un-ranks) a map, and so that an accidental untick cannot
        /// destroy a hand-found beatdrop timestamp. It replaced the per-difficulty
        /// <c>BeatmapUserSettings.IntroPoolInclusion</c> at realm schema 58, whose migration derived
        /// this value from the old per-difficulty overrides (schema 65 dropped the old column). It is
        /// deliberately NOT copied by the realm write mapper (see <c>RealmObjectExtensions</c>): an editor
        /// save of a set detached before a toggle would otherwise silently revert the toggle.
        /// </remarks>
        public bool? IntroPoolInclusion { get; set; }

        /// <summary>
        /// How the local auto-aligner times this set's words on its next import or re-align (see
        /// <see cref="Beatmaps.AlignerVocalMode"/>), chosen by the mapper beside the importer's automatic
        /// alignment and the editor's "Generate timing". Per SET because the choice is about the song's
        /// vocals, which every difficulty shares.
        /// </summary>
        /// <remarks>
        /// User data like <see cref="IntroPoolInclusion"/>, and for the same reasons: it lives in realm and
        /// never in a beatmap file, so it never re-encodes a map and never travels in a package (the server
        /// never sees it), and the realm write mapper does not copy it, so an editor save of a set detached
        /// before the choice cannot revert it. The column's realm default (0) is
        /// <see cref="Beatmaps.AlignerVocalMode.Aligned"/>, so schema 61 needed no migration body.
        /// </remarks>
        [Ignored]
        public AlignerVocalMode AlignerVocalMode
        {
            get => (AlignerVocalMode)AlignerVocalModeInt;
            set => AlignerVocalModeInt = (int)value;
        }

        [MapTo(nameof(AlignerVocalMode))]
        public int AlignerVocalModeInt { get; set; }

        /// <summary>
        /// The tracked-file manifest of the online set's CURRENT version (see <see cref="SetFileManifest"/>), as last
        /// reported by a metadata lookup (<c>file_manifest</c>, or the <c>has_vocals_stem</c> flag of a server that
        /// predates it), or <c>null</c> when no lookup has reported one yet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The server's answer for the version currently being served, recorded on every successful lookup, and
        /// compared at read time against the files the LOCAL copy carries (<see cref="OnlineFilesMissingLocally"/>).
        /// It describes what the online version offers right now, and whether that is something the local copy
        /// lacks is a separate question answered by <see cref="LocalFileManifest"/>.
        /// </para>
        /// <para>
        /// <c>null</c> reads as nothing to offer, and the next lookup records the real value. That ordering matters:
        /// the offer must not fire off a stale value before a lookup has confirmed the online version's content.
        /// One string for every tracked kind, so a new kind needs no realm schema change (realm schema 65 replaced
        /// the per-kind stem flags with it).
        /// </para>
        /// </remarks>
        public string? OnlineFileManifest { get; set; }

        /// <summary>
        /// The tracked-file manifest of the LOCAL copy (see <see cref="SetFileManifest"/>), recorded as a scalar
        /// column at import and after an accepted update.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A scalar, deliberately, rather than a live read of <see cref="Files"/>: the song-select carousel holds
        /// DETACHED sets, and the detach mapper strips <see cref="Files"/> for weight (see
        /// <c>RealmObjectExtensions.beatmap_set_mapper</c>), so <see cref="SetFileManifest.Of(IHasRealmFiles)"/> sees
        /// no file at all on the very object <see cref="Screens.Select.PanelUpdateBeatmapButton"/> reads. The live
        /// file list is only correct on the managed set the importer holds, which the panel never sees.
        /// </para>
        /// <para>
        /// Recorded by <see cref="RecordLocalFileManifest()"/> at import and on the changed update branch, and
        /// backfilled for every existing set from its own file list by realm schema 65. It is copied by the detach
        /// mapper (a plain scalar), so the carousel sees the real value.
        /// </para>
        /// </remarks>
        public string LocalFileManifest { get; set; } = SetFileManifest.EMPTY;

        /// <summary>
        /// Whether the online version offers a tracked file the LOCAL copy lacks: the online manifest (as last
        /// looked up, <see cref="OnlineFileManifest"/>) names a kind the local one (<see cref="LocalFileManifest"/>)
        /// does not (see <see cref="SetFileManifest.Covers"/>).
        /// </summary>
        /// <remarks>
        /// This is the arm that makes a file-only version cut visible (backlog 396, a stem-only version). Such a cut
        /// adds a file but leaves every <c>.osu</c> MD5 byte-identical, so the MD5 path cannot see it, and a client
        /// that already holds the audio never re-fetches a version that only added a file. Presence, not time, is
        /// what the client can tell apart here: the set's online timestamp also moves for a status or metadata change
        /// on the server (ranking a pending set), so comparing it offered UPDATE for files that never changed
        /// (backlog 449). Once the update lands <see cref="LocalFileManifest"/> names the kind, so this reads false
        /// and the offer clears on its own.
        /// </remarks>
        public bool OnlineFilesMissingLocally => !SetFileManifest.Covers(LocalFileManifest, OnlineFileManifest);

        /// <summary>
        /// Records the LOCAL copy's tracked-file manifest from the live file list. Called where the MANAGED set is
        /// in hand (import, which the no-change update branch also lands in, and the changed update branch): it reads <see cref="Files"/>, which is only populated on
        /// a managed set (see <see cref="LocalFileManifest"/>).
        /// </summary>
        public void RecordLocalFileManifest() => RecordLocalFileManifest(SetFileManifest.Of(this));

        /// <summary>Records the LOCAL copy's tracked-file manifest from an already-computed answer.</summary>
        public void RecordLocalFileManifest(string manifest)
        {
            if (LocalFileManifest != manifest)
                LocalFileManifest = manifest;
        }

        /// <summary>
        /// Records what the last successful lookup reported about the online version's tracked files
        /// (<see cref="OnlineFileManifest"/>). Only ever called with a real answer: a failed or absent lookup must
        /// not blank a value that is holding an offer open.
        /// </summary>
        public void RecordOnlineFileManifest(string manifest)
        {
            if (OnlineFileManifest != manifest)
                OnlineFileManifest = manifest;
        }

        public double MaxStarDifficulty => Beatmaps.Count == 0 ? 0 : Beatmaps.Max(b => b.StarRating);

        public double MaxLength => Beatmaps.Count == 0 ? 0 : Beatmaps.Max(b => b.Length);

        public double MaxBPM => Beatmaps.Count == 0 ? 0 : Beatmaps.Max(b => b.BPM);

        public BeatmapSetInfo(IEnumerable<BeatmapInfo>? beatmaps = null)
            : this()
        {
            ID = Guid.NewGuid();
            if (beatmaps != null)
                Beatmaps.AddRange(beatmaps);
        }

        [UsedImplicitly] // Realm
        private BeatmapSetInfo()
        {
        }

        public bool Equals(BeatmapSetInfo? other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other == null) return false;

            return ID == other.ID;
        }

        public override int GetHashCode()
        {
            // ReSharper disable once NonReadonlyMemberInGetHashCode
            return ID.GetHashCode();
        }

        public override string ToString() => Metadata.GetDisplayString();

        public bool Equals(IBeatmapSetInfo? other) => other is BeatmapSetInfo b && Equals(b);

        IEnumerable<IBeatmapInfo> IBeatmapSetInfo.Beatmaps => Beatmaps;

        IEnumerable<INamedFileUsage> IHasNamedFiles.Files => Files;

        /// <summary>
        /// Whether every difficulty is at the online version and the local copy carries no file the online
        /// version offers but it lacks.
        /// </summary>
        /// <remarks>
        /// Two arms. The first is osu!lazer's rule unchanged: the per-difficulty MD5
        /// (<see cref="BeatmapInfo.MatchesOnlineVersion"/>), so a status or metadata change on the server (ranking
        /// a pending set bumps its online timestamp) offers no UPDATE while the files are the same. The second is
        /// the tracked-file manifest (<see cref="OnlineFilesMissingLocally"/>, against the recorded
        /// <see cref="LocalFileManifest"/>), which makes a file-only version cut visible (backlog 396): the MD5
        /// path cannot see a cut that only adds a stem, so presence is the signal for it.
        /// </remarks>
        public bool AllBeatmapsUpToDate => Beatmaps.All(b => b.MatchesOnlineVersion) && !OnlineFilesMissingLocally;
    }
}
