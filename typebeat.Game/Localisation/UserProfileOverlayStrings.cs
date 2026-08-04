// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace typebeat.Game.Localisation
{
    public static class UserProfileOverlayStrings
    {
        private const string prefix = @"typebeat.Game.Resources.Localisation.UserProfileOverlay";

        /// <summary>
        /// "Couldn't load this profile. Click to retry."
        /// </summary>
        public static LocalisableString CouldNotLoadProfile => new TranslatableString(getKey(@"could_not_load_profile"), @"Couldn't load this profile. Click to retry.");

        /// <summary>
        /// "Couldn't load this section. Click to retry."
        /// </summary>
        public static LocalisableString CouldNotLoadSection => new TranslatableString(getKey(@"could_not_load_section"), @"Couldn't load this section. Click to retry.");

        private static string getKey(string key) => $@"{prefix}:{key}";
    }
}
