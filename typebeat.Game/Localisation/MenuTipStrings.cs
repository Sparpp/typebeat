// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace typebeat.Game.Localisation
{
    public static class MenuTipStrings
    {
        private const string prefix = @"typebeat.Game.Resources.Localisation.MenuTip";

        /// <summary>
        /// "Join the type!beat Discord"
        /// </summary>
        public static LocalisableString JoinDiscord => new TranslatableString(getKey(@"join_discord"), @"Join the type!beat Discord");

        private static string getKey(string key) => $@"{prefix}:{key}";
    }
}
