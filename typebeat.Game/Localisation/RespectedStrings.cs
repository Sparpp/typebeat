// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace typebeat.Game.Localisation
{
    /// <summary>
    /// type!beat calls osu!'s Loved status "Respected". Only the wording changes: the enum members
    /// (<see cref="Beatmaps.BeatmapOnlineStatus.Loved"/> and friends) and the wire string ("loved")
    /// keep lazer's names, so these strings replace the web resources' "Loved" wherever it is shown.
    /// </summary>
    public static class RespectedStrings
    {
        private const string prefix = @"typebeat.Game.Resources.Localisation.Respected";

        /// <summary>
        /// "Respected"
        /// </summary>
        public static LocalisableString Status => new TranslatableString(getKey(@"status"), @"Respected");

        /// <summary>
        /// "Respected Beatmaps"
        /// </summary>
        public static LocalisableString ProfileBeatmapsTitle => new TranslatableString(getKey(@"profile_beatmaps_title"), @"Respected Beatmaps");

        private static string getKey(string key) => $@"{prefix}:{key}";
    }
}
