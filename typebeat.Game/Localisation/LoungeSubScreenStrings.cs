// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace typebeat.Game.Localisation
{
    public static class LoungeSubScreenStrings
    {
        private const string prefix = @"typebeat.Game.Resources.Localisation.LoungeSubScreen";

        /// <summary>
        /// "Category"
        /// </summary>
        public static LocalisableString PlaylistFilterCategory => new TranslatableString(getKey(@"playlist_filter_category"), @"Category");

        /// <summary>
        /// "State"
        /// </summary>
        public static LocalisableString RoomFilterState => new TranslatableString(getKey(@"room_filter_state"), @"State");

        private static string getKey(string key) => $@"{prefix}:{key}";
    }
}
