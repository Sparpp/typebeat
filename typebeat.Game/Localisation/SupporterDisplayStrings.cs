// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace typebeat.Game.Localisation
{
    public static class SupporterDisplayStrings
    {
        private const string prefix = @"typebeat.Game.Resources.Localisation.SupporterDisplay";

        /// <summary>
        /// "Eternal thanks to you for supporting type!beat"
        /// </summary>
        public static LocalisableString ThankYouForSupporting => new TranslatableString(getKey(@"thank_you_for_supporting"), @"Eternal thanks to you for supporting type!beat");

        /// <summary>
        /// "Consider becoming an [type!beatsupporter]({0}) to help support type!beat's development"
        /// </summary>
        public static LocalisableString ConsiderBecomingASupporter(string url) => new TranslatableString(getKey(@"consider_becoming_a_supporter"), @"Consider becoming an [type!beatsupporter]({0}) to help support type!beat's development", url);

        private static string getKey(string key) => $@"{prefix}:{key}";
    }
}
