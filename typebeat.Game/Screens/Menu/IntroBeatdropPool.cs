// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;

namespace typebeat.Game.Screens.Menu
{
    /// <summary>
    /// Decides which beatmap SETS may be picked to soundtrack the game intro (see <see cref="IntroScreen"/>),
    /// which of a set's difficulties plays, and at what point in the song the intro starts it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Membership defaults to "any difficulty of the set declares an intro beatdrop" (<see cref="IBeatmap.IntroBeatdropTime"/>,
    /// authored in the editor setup screen as "Intro beatdrop (ms)"). A user can override it per set from song select
    /// ("Use on game intro" on the set panel), stored as <see cref="BeatmapSetInfo.IntroPoolInclusion"/>. The override
    /// is deliberately separate from the beatdrop timestamps: unticking a set must not throw away a hand-found
    /// timestamp, and neither tick nor untick should re-encode any beatmap file.
    /// </para>
    /// <para>
    /// The drop always comes from the set's HIGHEST star difficulty that declares one (see
    /// <see cref="IndexOfDropDifficulty"/>), so a set plays the same moment whichever difficulty the beatdrop was
    /// stamped on, and a set whose difficulties disagree plays its hardest one's.
    /// </para>
    /// </remarks>
    public static class IntroBeatdropPool
    {
        /// <summary>
        /// Whether a set may soundtrack the intro.
        /// </summary>
        /// <param name="inclusion">The user override (<see cref="BeatmapSetInfo.IntroPoolInclusion"/>); <c>null</c> to follow the beatdrops.</param>
        /// <param name="anyDifficultyHasBeatdrop">Whether any difficulty of the set declares an intro beatdrop.</param>
        public static bool IsCandidate(bool? inclusion, bool anyDifficultyHasBeatdrop) => inclusion ?? anyDifficultyHasBeatdrop;

        /// <summary>
        /// The point in the song (ms) the intro should land on the menu reveal, for ONE difficulty.
        /// </summary>
        /// <remarks>
        /// An authored beatdrop always wins. A map opted in without one falls back to its preview point (the
        /// "good bit" already chosen by the mapper, and the same moment song select previews), and failing that
        /// to the start of the song. Both fallbacks are guaranteed to be a real position in the track, so the
        /// intro can never end up seeking nowhere.
        /// </remarks>
        /// <param name="beatdropTime">The authored beatdrop timestamp, if any.</param>
        /// <param name="previewTime">The beatmap's preview point (<see cref="BeatmapMetadata.PreviewTime"/>), -1 when unset.</param>
        public static double ResolveDropTime(double? beatdropTime, int previewTime) => beatdropTime ?? (previewTime > 0 ? previewTime : 0);

        /// <summary>
        /// Which of a set's difficulties the intro plays: among those that declare a beatdrop, the one with the
        /// greatest star rating; when none does, the greatest star rating overall (so an opted-in set with no
        /// beatdrop plays its hardest difficulty at <see cref="ResolveDropTime"/>'s fallback). An uncalculated
        /// rating (-1) sorts lowest, and ties go to the earlier entry. Returns -1 for an empty set.
        /// </summary>
        public static int IndexOfDropDifficulty(IReadOnlyList<(double stars, double? beatdrop, int preview)> difficulties)
        {
            int best = -1;
            int bestWithBeatdrop = -1;

            for (int i = 0; i < difficulties.Count; i++)
            {
                // Strictly greater, so a tie keeps the earlier entry.
                if (best < 0 || difficulties[i].stars > difficulties[best].stars)
                    best = i;

                if (difficulties[i].beatdrop.HasValue && (bestWithBeatdrop < 0 || difficulties[i].stars > difficulties[bestWithBeatdrop].stars))
                    bestWithBeatdrop = i;
            }

            return bestWithBeatdrop >= 0 ? bestWithBeatdrop : best;
        }

        /// <summary>
        /// The point in the song (ms) the intro should land on the menu reveal, for a whole SET: the drop of the
        /// difficulty <see cref="IndexOfDropDifficulty"/> picks, resolved through <see cref="ResolveDropTime"/>.
        /// 0 for an empty set.
        /// </summary>
        public static double ResolveSetDropTime(IEnumerable<(double stars, double? beatdrop, int preview)> difficulties)
        {
            var list = difficulties as IReadOnlyList<(double stars, double? beatdrop, int preview)> ?? difficulties.ToList();

            int index = IndexOfDropDifficulty(list);

            return index < 0 ? 0 : ResolveDropTime(list[index].beatdrop, list[index].preview);
        }

        /// <summary>
        /// A set's difficulties in the order the intro examines them: highest star rating first, ties in their
        /// existing order (the sort is stable). The first one found to declare a beatdrop in this order is by
        /// construction the one <see cref="IndexOfDropDifficulty"/> would pick, so a caller that decodes
        /// difficulties one at a time can stop there.
        /// </summary>
        public static IEnumerable<T> InDropOrder<T>(IEnumerable<T> difficulties, Func<T, double> starsOf) => difficulties.OrderByDescending(starsOf);

        /// <summary>
        /// Decides whether a set soundtracks the intro and, if so, with which difficulty and from where. This is the
        /// one resolution both the real intro and the editor's beatdrop demo (<see cref="IntroBeatdropDemo"/>) go
        /// through.
        /// </summary>
        /// <remarks>
        /// An explicit <c>false</c> costs no decode at all. Otherwise difficulties are decoded lazily in
        /// <see cref="InDropOrder{T}"/> and the walk stops at the first that declares a beatdrop (which is the
        /// highest star one), so a set pays as few decodes as the answer allows. A difficulty
        /// <paramref name="decode"/> cannot read (returns <c>null</c>) is skipped.
        /// </remarks>
        /// <param name="difficulties">The set's difficulties.</param>
        /// <param name="starsOf">A difficulty's star rating (-1 when uncalculated).</param>
        /// <param name="inclusion">The set's override (<see cref="BeatmapSetInfo.IntroPoolInclusion"/>).</param>
        /// <param name="decode">Reads a difficulty: its decoded form, its beatdrop, and its preview point; <c>null</c> if unreadable.</param>
        /// <returns>The decoded difficulty to play and the drop time to land on the reveal, or <c>null</c> if the set is not a candidate.</returns>
        public static (TOut Difficulty, double DropTime)? PickFromSet<TIn, TOut>(IEnumerable<TIn> difficulties, Func<TIn, double> starsOf, bool? inclusion,
                                                                                 Func<TIn, (TOut decoded, double? beatdrop, int preview)?> decode)
        {
            if (inclusion == false)
                return null;

            var decoded = new List<TOut>();
            var drops = new List<(double stars, double? beatdrop, int preview)>();

            foreach (var difficulty in InDropOrder(difficulties, starsOf))
            {
                var read = decode(difficulty);

                if (read == null)
                    continue;

                decoded.Add(read.Value.decoded);
                drops.Add((starsOf(difficulty), read.Value.beatdrop, read.Value.preview));

                if (read.Value.beatdrop.HasValue)
                    break;
            }

            if (!IsCandidate(inclusion, drops.Any(d => d.beatdrop.HasValue)))
                return null;

            int index = IndexOfDropDifficulty(drops);

            if (index < 0)
                return null;

            return (decoded[index], ResolveSetDropTime(drops));
        }

        /// <summary>
        /// The set-level override a migration derives from the old per-difficulty ones
        /// (<see cref="BeatmapUserSettings.IntroPoolInclusion"/>): any difficulty explicitly opted in opts the set
        /// in; every difficulty explicitly kept out keeps the set out; anything else (no overrides, or a mix of
        /// <c>false</c> and <c>null</c>) has no override and follows the beatdrops.
        /// </summary>
        public static bool? DeriveSetInclusion(IEnumerable<bool?> difficultyInclusions)
        {
            bool any = false;
            bool allFalse = true;

            foreach (bool? inclusion in difficultyInclusions)
            {
                any = true;

                if (inclusion == true)
                    return true;

                if (inclusion != false)
                    allFalse = false;
            }

            return any && allFalse ? false : null;
        }

        /// <summary>
        /// The override to store after the user has toggled the menu item to <paramref name="used"/>.
        /// Collapses back to <c>null</c> (no override) whenever the beatdrops already imply the chosen state,
        /// so that a later edit to a beatdrop keeps flowing through rather than being pinned by a stale override.
        /// </summary>
        public static bool? InclusionAfterToggle(bool used, bool anyDifficultyHasBeatdrop) => used == anyDifficultyHasBeatdrop ? null : used;

        /// <summary>
        /// Creates the song select context menu toggle for intro pool membership. Ticked when the set is
        /// currently a candidate; toggling it hands the new override to <paramref name="apply"/>.
        /// </summary>
        public static ToggleMenuItem CreateMenuItem(bool? inclusion, bool anyDifficultyHasBeatdrop, Action<bool?> apply)
        {
            var item = new ToggleMenuItem(SongSelectStrings.UseOnGameIntro, MenuItemType.Standard, used => apply(InclusionAfterToggle(used, anyDifficultyHasBeatdrop)));

            // Seeding the state does not fire the action (that only runs on click), so this is the
            // displayed tick only.
            item.State.Value = IsCandidate(inclusion, anyDifficultyHasBeatdrop);

            return item;
        }

        /// <summary>
        /// The "Use on game intro" toggle for a set's panel (<c>PanelBeatmapSet</c>, and <c>PanelBeatmapStandalone</c>,
        /// which stands for its set when difficulties are not grouped), wired to <paramref name="beatmaps"/>.
        /// </summary>
        /// <remarks>
        /// Whether any difficulty declares a beatdrop needs decodes (the timestamp lives in the beatmap files, not
        /// realm), so it is read once as the context menu is built rather than bound live; the menu is rebuilt on
        /// every open, and a beatdrop can only change from the editor in the meantime. The walk stops at the first
        /// difficulty that declares one.
        /// </remarks>
        public static ToggleMenuItem CreateSetMenuItem(BeatmapManager beatmaps, BeatmapSetInfo beatmapSet)
        {
            bool anyDifficultyHasBeatdrop = beatmapSet.Beatmaps.Any(b => hasIntroBeatdrop(beatmaps, b));

            return CreateMenuItem(
                beatmaps.GetIntroPoolInclusion(beatmapSet),
                anyDifficultyHasBeatdrop,
                inclusion => beatmaps.SetIntroPoolInclusion(beatmapSet, inclusion));
        }

        private static bool hasIntroBeatdrop(BeatmapManager beatmaps, BeatmapInfo beatmap)
        {
            try
            {
                return beatmaps.GetWorkingBeatmap(beatmap).Beatmap.IntroBeatdropTime != null;
            }
            catch
            {
                // an unreadable difficulty is not intro material; it simply does not count.
                return false;
            }
        }
    }
}
