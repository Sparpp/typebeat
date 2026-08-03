// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace typebeat.Game.Localisation
{
    public static class ResultsScreenStrings
    {
        private const string prefix = @"typebeat.Game.Resources.Localisation.ResultsScreen";

        /// <summary>
        /// "Performance points are not granted for this score because the beatmap is not ranked."
        /// </summary>
        public static LocalisableString NoPPForUnrankedBeatmaps => new TranslatableString(getKey(@"no_pp_for_unranked_beatmaps"), @"Performance points are not granted for this score because the beatmap is not ranked.");

        /// <summary>
        /// "Performance points are not granted for this score because of unranked mods."
        /// </summary>
        public static LocalisableString NoPPForUnrankedMods => new TranslatableString(getKey(@"no_pp_for_unranked_mods"), @"Performance points are not granted for this score because of unranked mods.");

        /// <summary>
        /// "Performance points are not granted for failed scores."
        /// </summary>
        public static LocalisableString NoPPForFailedScores => new TranslatableString(getKey(@"no_pp_for_failed_scores"), @"Performance points are not granted for failed scores.");

        /// <summary>
        /// "Your profile statistics could not be retrieved, so the change from this score cannot be shown."
        /// </summary>
        public static LocalisableString StatisticsUpdateUnavailable => new TranslatableString(getKey(@"statistics_update_unavailable"),
            @"Your profile statistics could not be retrieved, so the change from this score cannot be shown.");

        /// <summary>
        /// "Your profile statistics from before this score are not known, so the change cannot be shown."
        /// </summary>
        public static LocalisableString PreviousStatisticsUnavailable => new TranslatableString(getKey(@"previous_statistics_unavailable"),
            @"Your profile statistics from before this score are not known, so the change cannot be shown.");

        private static string getKey(string key) => $@"{prefix}:{key}";
    }
}
