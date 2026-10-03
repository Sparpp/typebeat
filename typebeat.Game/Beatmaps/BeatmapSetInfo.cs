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
        /// <see cref="BeatmapUserSettings.IntroPoolInclusion"/> at realm schema 58, whose migration derived
        /// this value from the old per-difficulty overrides. It is deliberately NOT copied by the realm write
        /// mapper (see <c>RealmObjectExtensions</c>): an editor save of a set detached before a toggle would
        /// otherwise silently revert the toggle.
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
        /// The online set version (the server's <c>beatmapsets.updated_at</c>, surfaced to the client as
        /// <c>last_updated</c>) that THIS local copy corresponds to, or <c>null</c> when no version has been
        /// recorded yet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The set's update signal for a version cut that changes no <c>.osu</c> bytes, which is exactly how a
        /// stem-only version is attached server-side: a ranked set is re-snapshotted with its existing
        /// content plus <c>vocals.ogg</c> and <c>updated_at</c> is bumped, left deliberately fingerprint-neutral so
        /// the set is not demoted. Every difficulty's MD5 is then unchanged, so the MD5 path
        /// (<see cref="BeatmapInfo.MatchesOnlineVersion"/>) cannot see the new version and the UPDATE button
        /// would never appear, stranding the stem. Comparing the freshly-fetched online version against this
        /// stored baseline closes that gap.
        /// </para>
        /// <para>
        /// It is deliberately NOT <see cref="BeatmapInfo.LastOnlineUpdate"/>: that field is refreshed on EVERY
        /// lookup, so it records "when I last looked", not "the version I have", and a naive comparison against
        /// it fires once and settles. Here the baseline only advances when the local copy is genuinely brought
        /// to a version: adopted on first sight (see <see cref="BeatmapUpdaterMetadataLookup"/> and
        /// <see cref="Screens.Select.RealmPopulatingOnlineLookupSource"/>) and re-adopted after an accepted
        /// update (see <see cref="BeatmapImporter.ImportAsUpdate"/>). While the online version is newer it is
        /// left where it is, so the offer does not settle on its own.
        /// </para>
        /// </remarks>
        public DateTimeOffset? OnlineVersionLastUpdated { get; set; }

        /// <summary>
        /// Whether the online set has a version strictly newer than <see cref="OnlineVersionLastUpdated"/>
        /// that the local copy does not correspond to yet.
        /// </summary>
        /// <remarks>
        /// Compared against each difficulty's <see cref="BeatmapInfo.LastOnlineUpdate"/>, which holds the
        /// newest online version seen for this set (every difficulty of a set carries the same server-side
        /// value). A difficulty that has never been looked up, or a locally modified map, has a null
        /// <see cref="BeatmapInfo.LastOnlineUpdate"/> and so contributes nothing: without that, the null
        /// baseline of a never-looked-up map would read as "behind".
        /// </remarks>
        public bool OnlineVersionIsNewer
        {
            get
            {
                DateTimeOffset? stored = OnlineVersionLastUpdated;

                if (stored == null)
                    return false;

                foreach (var beatmap in Beatmaps)
                {
                    DateTimeOffset? seen = beatmap.LastOnlineUpdate;

                    if (seen != null && seen.Value > stored.Value)
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Records the freshly-fetched online set version as the one this local copy corresponds to,
        /// unless it is strictly newer (see <see cref="OnlineVersionLastUpdated"/>).
        /// </summary>
        /// <remarks>
        /// The single write path for the baseline, so both lookup surfaces and any future one decide
        /// identically. A strictly-newer online version is deliberately NOT adopted: that is the "my
        /// stored set is behind" state <see cref="OnlineVersionIsNewer"/> reports, and advancing the
        /// baseline past it here is what would make the offer flash once and settle. A first look
        /// (<see cref="OnlineVersionLastUpdated"/> null) adopts, so a plain lookup of a set that has
        /// never been versioned does not itself manufacture an offer.
        /// </remarks>
        /// <param name="onlineVersion">The version to record. <c>null</c> is ignored.</param>
        public void AdoptOnlineVersion(DateTimeOffset? onlineVersion)
        {
            if (onlineVersion is not { } online)
                return;

            if (OnlineVersionLastUpdated is { } stored && online > stored)
                return;

            if (OnlineVersionLastUpdated != online)
                OnlineVersionLastUpdated = online;
        }

        /// <summary>
        /// Records that an accepted UPDATE brought this local copy to the online version. Call after the
        /// update's lookup has run (it reads the version off <see cref="BeatmapInfo.LastOnlineUpdate"/>, the
        /// same field the lookup itself refreshes) and raises the baseline to it.
        /// </summary>
        /// <remarks>
        /// The baseline is exactly the one that lagged (that is why an update was offered), so it is
        /// deliberately moved forward here: this is the one place the local copy is genuinely brought to a
        /// version, and without the move the freshly-imported set would still read as behind the very version
        /// it was downloaded for and the offer would never clear. Everywhere else the baseline only advances
        /// to a version it has not fallen behind (see <see cref="AdoptOnlineVersion"/>).
        /// </remarks>
        public void MarkUpdatedToOnlineVersion()
        {
            DateTimeOffset? seen = null;

            foreach (var beatmap in Beatmaps)
            {
                if (beatmap.LastOnlineUpdate is { } t && (seen == null || t > seen.Value))
                    seen = t;
            }

            if (seen != null)
                OnlineVersionLastUpdated = seen;
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
        /// Whether every difficulty is at the online version AND the set itself is at the online version.
        /// The second half is what surfaces a version cut that changed no <c>.osu</c> bytes (a stem-only
        /// version): <see cref="OnlineVersionIsNewer"/>.
        /// </summary>
        public bool AllBeatmapsUpToDate => Beatmaps.All(b => b.MatchesOnlineVersion) && !OnlineVersionIsNewer;
    }
}
