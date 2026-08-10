// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace typebeat.Game.Localisation
{
    public static class GeneralSettingsStrings
    {
        private const string prefix = @"typebeat.Game.Resources.Localisation.GeneralSettings";

        /// <summary>
        /// "Language"
        /// </summary>
        public static LocalisableString LanguageHeader => new TranslatableString(getKey(@"language_header"), @"Language");

        /// <summary>
        /// "Language"
        /// </summary>
        public static LocalisableString LanguageDropdown => new TranslatableString(getKey(@"language_dropdown"), @"Language");

        /// <summary>
        /// "Prefer metadata in original language"
        /// </summary>
        public static LocalisableString PreferOriginalMetadataLanguage => new TranslatableString(getKey(@"prefer_original"), @"Prefer metadata in original language");

        /// <summary>
        /// "Prefer 24-hour time display"
        /// </summary>
        public static LocalisableString Prefer24HourTimeDisplay => new TranslatableString(getKey(@"prefer_24_hour_time_display"), @"Prefer 24-hour time display");

        /// <summary>
        /// "Installation"
        /// </summary>
        public static LocalisableString InstallationHeader => new TranslatableString(getKey(@"installation_header"), @"Installation");

        /// <summary>
        /// "Quick Actions"
        /// </summary>
        public static LocalisableString QuickActionsHeader => new TranslatableString(getKey(@"quick_actions_header"), @"Quick Actions");

        /// <summary>
        /// "Updates"
        /// </summary>
        public static LocalisableString UpdateHeader => new TranslatableString(getKey(@"update_header"), @"Updates");

        /// <summary>
        /// "Check for updates"
        /// </summary>
        public static LocalisableString CheckUpdate => new TranslatableString(getKey(@"check_update"), @"Check for updates");

        /// <summary>
        /// "Checking for updates"
        /// </summary>
        public static LocalisableString CheckingForUpdates => new TranslatableString(getKey(@"checking_for_updates"), @"Checking for updates");

        /// <summary>
        /// "Open type!beat folder"
        /// </summary>
        public static LocalisableString OpenOsuFolder => new TranslatableString(getKey(@"open_osu_folder"), @"Open type!beat folder");

        /// <summary>
        /// "Export logs"
        /// </summary>
        public static LocalisableString ExportLogs => new TranslatableString(getKey(@"export_logs"), @"Export logs");

        /// <summary>
        /// "Change folder location..."
        /// </summary>
        public static LocalisableString ChangeFolderLocation => new TranslatableString(getKey(@"change_folder_location"), @"Change folder location...");

        /// <summary>
        /// "Run setup wizard"
        /// </summary>
        public static LocalisableString RunSetupWizard => new TranslatableString(getKey(@"run_setup_wizard"), @"Run setup wizard");

        /// <summary>
        /// "Visit the type!beat website"
        /// </summary>
        public static LocalisableString VisitWebsite => new TranslatableString(getKey(@"visit_website"), @"Visit the type!beat website");

        /// <summary>
        /// "Browse beatmaps, rankings and more in your browser"
        /// </summary>
        public static LocalisableString VisitWebsiteTooltip => new TranslatableString(getKey(@"visit_website_tooltip"), @"Browse beatmaps, rankings and more in your browser");

        /// <summary>
        /// "You are running the latest release ({0})"
        /// </summary>
        public static LocalisableString RunningLatestRelease(string version) => new TranslatableString(getKey(@"running_latest_release"), @"You are running the latest release ({0})", version);

        private static string getKey(string key) => $@"{prefix}:{key}";
    }
}
