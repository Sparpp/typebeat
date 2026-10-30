// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Database;

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// The set files beyond the <c>.osu</c> difficulties that the UPDATE offer tracks, and the one
    /// manifest string a set's tracked files are fingerprinted by, on the server and on the client alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The manifest names each tracked KIND the set carries (not each file name, since a kind may be
    /// accepted under several names: a stem is <c>vocals.ogg</c> or <c>vocals.wav</c>), sorted
    /// ordinally and joined by commas, so the same files always give the same string. An empty string
    /// means the set carries no tracked file. The server's <c>SetFileManifest</c>
    /// (<c>Typebeat.Web/Packages/SetFileManifest.cs</c>) builds the same string from the current
    /// version's file list and publishes it as <c>file_manifest</c>; WireCompat holds the two against
    /// each other.
    /// </para>
    /// <para>
    /// The comparison is coverage, not equality (<see cref="Covers"/>): the local copy is up to date
    /// when it carries every kind the online version does. A file the local copy has and the online
    /// version lacks (a stem the mapper's own aligner produced) offers nothing, exactly as it never did.
    /// Presence is the whole signal: like osu!lazer, which offers no UPDATE for a changed audio file while
    /// every <c>.osu</c> MD5 matches, a content change to a tracked file the local copy already carries is
    /// not offered (the set's online timestamp is no substitute, since a status change on the server
    /// moves it too, backlog 449).
    /// </para>
    /// <para>
    /// A new tracked kind is one more row in <c>kinds</c> here and in the server's copy. Both
    /// manifests are plain strings on the realm row, so it needs no realm schema change. A kind a newer
    /// server publishes before this client knows it is kept in the stored online manifest but never
    /// compared (<see cref="Covers"/>): this client cannot record it locally, so comparing it would offer
    /// an update that no download could ever clear.
    /// </para>
    /// </remarks>
    public static class SetFileManifest
    {
        /// <summary>The kind name of an isolated vocals stem (see <see cref="VocalsStem"/>).</summary>
        public const string VOCALS_STEM = VocalsStem.BASE_NAME;

        /// <summary>The manifest of a set that carries no tracked file.</summary>
        public const string EMPTY = "";

        private const char separator = ',';

        /// <summary>Every tracked kind, with the predicate that tells a file of that kind by its name.</summary>
        private static readonly (string Kind, Func<string, bool> Matches)[] kinds =
        {
            (VOCALS_STEM, VocalsStem.IsStemFileName),
        };

        /// <summary>The manifest of a set carrying the files named <paramref name="filenames"/>.</summary>
        public static string Of(IEnumerable<string> filenames)
        {
            var names = filenames.ToArray();

            return format(kinds.Where(k => names.Any(k.Matches)).Select(k => k.Kind));
        }

        /// <summary>
        /// The manifest of <paramref name="set"/>'s own file list. Only meaningful on a MANAGED set: the
        /// detach mapper strips <see cref="IHasRealmFiles.Files"/>, which is why the result is recorded
        /// as a scalar (<see cref="BeatmapSetInfo.LocalFileManifest"/>).
        /// </summary>
        public static string Of(IHasRealmFiles set) => Of(set.Files.Select(f => f.Filename));

        /// <summary>
        /// The online manifest a set response reports: the published <c>file_manifest</c>, or, from a
        /// server that predates it, the manifest its <c>has_vocals_stem</c> flag describes.
        /// </summary>
        public static string Online(string? published, bool hasVocalsStem)
            => published != null ? Normalise(published) : hasVocalsStem ? VOCALS_STEM : EMPTY;

        /// <summary>
        /// The union of several reports of the same set (one per difficulty looked up), or null when none
        /// of them carried a manifest.
        /// </summary>
        public static string? Union(IEnumerable<string?> manifests)
        {
            var reported = manifests.Where(m => m != null).ToArray();

            return reported.Length == 0 ? null : format(reported.SelectMany(m => kindsIn(m!)));
        }

        /// <summary>
        /// Whether a local copy with manifest <paramref name="local"/> carries every tracked kind of an
        /// online version with manifest <paramref name="online"/> that this client knows. A null online
        /// manifest (no lookup has reported one) is covered by anything, so it never offers an update on
        /// its own, and so is an online kind missing from <c>kinds</c>, which <see cref="Of(IEnumerable{string})"/>
        /// can never put in a local manifest.
        /// </summary>
        public static bool Covers(string local, string? online)
        {
            if (online == null)
                return true;

            var have = kindsIn(local).ToHashSet(StringComparer.Ordinal);
            return kindsIn(online).Where(isKnown).All(have.Contains);
        }

        /// <summary>The canonical spelling of <paramref name="manifest"/>: kinds trimmed, lowercased, deduplicated and sorted.</summary>
        public static string Normalise(string manifest) => format(kindsIn(manifest));

        private static bool isKnown(string kind) => kinds.Any(k => k.Kind == kind);

        private static IEnumerable<string> kindsIn(string manifest)
            => manifest.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(k => k.ToLowerInvariant());

        private static string format(IEnumerable<string> kindNames)
            => string.Join(separator, kindNames.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal));
    }
}
