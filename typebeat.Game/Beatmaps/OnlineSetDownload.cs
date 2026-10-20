// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// Whether an online set can be downloaded, and what the player is told when it cannot. The set
    /// overlay's header and notice and the listing card's button all read it, so they agree.
    /// </summary>
    /// <remarks>
    /// Two reasons are told apart. A set with no uploaded package (<see cref="APIBeatmapSet.HasPackage"/>
    /// false: it predates package uploads, so the server has no file to send) says so in this game's own
    /// words; the website shows the same sets as "available in-game only". osu's generic availability flag
    /// (<see cref="BeatmapSetOnlineAvailability.DownloadDisabled"/>) keeps osu's own message. The server
    /// derives that flag from the package today, but the package is checked first and on its own, so the
    /// honest reason is shown even against a server that sends only one of the two.
    /// </remarks>
    public static class OnlineSetDownload
    {
        /// <summary>The set overlay's notice for a set with no uploaded package.</summary>
        public const string NO_PACKAGE_NOTICE = @"This map has no download: it predates uploaded packages, so there is no file to send. A copy you already have still plays here as normal.";

        /// <summary>The download button's tooltip for a set with no uploaded package.</summary>
        public const string NO_PACKAGE_TOOLTIP = @"No download: this map predates uploaded packages";

        /// <summary>Whether <paramref name="set"/> can be downloaded at all.</summary>
        public static bool IsDownloadable(APIBeatmapSet set) => set.HasPackage && !set.Availability.DownloadDisabled;

        /// <summary>The notice shown on the set overlay when <paramref name="set"/> cannot be downloaded.</summary>
        public static LocalisableString UnavailableNotice(APIBeatmapSet set)
            => !set.HasPackage ? NO_PACKAGE_NOTICE : BeatmapsetsStrings.AvailabilityDisabled;

        /// <summary>The download button's tooltip when <paramref name="set"/> cannot be downloaded.</summary>
        public static LocalisableString UnavailableTooltip(APIBeatmapSet set)
            => !set.HasPackage ? NO_PACKAGE_TOOLTIP : BeatmapsetsStrings.AvailabilityDisabled;
    }
}
