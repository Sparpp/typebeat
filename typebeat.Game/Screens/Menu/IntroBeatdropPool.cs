// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;

namespace typebeat.Game.Screens.Menu
{
    /// <summary>
    /// Decides which beatmaps may be picked to soundtrack the game intro (see <see cref="IntroScreen"/>),
    /// and at what point in the song the intro starts them.
    /// </summary>
    /// <remarks>
    /// Membership used to be exactly "the map declares an intro beatdrop" (<see cref="IBeatmap.IntroBeatdropTime"/>,
    /// authored in the editor setup screen as "Intro beatdrop (ms)"). That is still the default, but a user can
    /// override it per beatmap from song select ("Use on game intro"), stored as
    /// <see cref="BeatmapUserSettings.IntroPoolInclusion"/>. The override is deliberately separate from the
    /// beatdrop timestamp: unticking a map must not throw away a hand-found timestamp, and neither tick nor
    /// untick should re-encode the beatmap file.
    /// </remarks>
    public static class IntroBeatdropPool
    {
        /// <summary>
        /// Whether a beatmap may soundtrack the intro.
        /// </summary>
        /// <param name="inclusion">The user override (<see cref="BeatmapUserSettings.IntroPoolInclusion"/>); <c>null</c> to follow the beatdrop.</param>
        /// <param name="hasBeatdrop">Whether the beatmap declares an intro beatdrop.</param>
        public static bool IsCandidate(bool? inclusion, bool hasBeatdrop) => inclusion ?? hasBeatdrop;

        /// <summary>
        /// The point in the song (ms) the intro should land on the menu reveal.
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
        /// The override to store after the user has toggled the menu item to <paramref name="used"/>.
        /// Collapses back to <c>null</c> (no override) whenever the beatdrop already implies the chosen state,
        /// so that a later edit to the beatdrop keeps flowing through rather than being pinned by a stale override.
        /// </summary>
        public static bool? InclusionAfterToggle(bool used, bool hasBeatdrop) => used == hasBeatdrop ? null : used;

        /// <summary>
        /// Creates the song select context menu toggle for intro pool membership. Ticked when the beatmap is
        /// currently a candidate; toggling it hands the new override to <paramref name="apply"/>.
        /// </summary>
        public static ToggleMenuItem CreateMenuItem(bool? inclusion, bool hasBeatdrop, Action<bool?> apply)
        {
            var item = new ToggleMenuItem(SongSelectStrings.UseOnGameIntro, MenuItemType.Standard, used => apply(InclusionAfterToggle(used, hasBeatdrop)));

            // Seeding the state does not fire the action (that only runs on click), so this is the
            // displayed tick only.
            item.State.Value = IsCandidate(inclusion, hasBeatdrop);

            return item;
        }
    }
}
