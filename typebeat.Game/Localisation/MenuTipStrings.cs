// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace typebeat.Game.Localisation
{
    public static class MenuTipStrings
    {
        private const string prefix = @"typebeat.Game.Resources.Localisation.MenuTip";

        /// <summary>
        /// "Check out osu!"
        /// </summary>
        public static LocalisableString EmbeddedWebContent => new TranslatableString(getKey(@"embedded_web_content"), @"Check out osu!");

        private static string getKey(string key) => $@"{prefix}:{key}";
    }
}
