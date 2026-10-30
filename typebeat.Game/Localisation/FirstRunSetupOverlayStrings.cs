// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace typebeat.Game.Localisation
{
    public static class FirstRunSetupOverlayStrings
    {
        private const string prefix = @"typebeat.Game.Resources.Localisation.FirstRunSetupOverlay";

        /// <summary>
        /// "Get started"
        /// </summary>
        public static LocalisableString GetStarted => new TranslatableString(getKey(@"get_started"), @"Get started");

        /// <summary>
        /// "Click to resume first-run setup at any point"
        /// </summary>
        public static LocalisableString ClickToResumeFirstRunSetupAtAnyPoint =>
            new TranslatableString(getKey(@"click_to_resume_first_run_setup_at_any_point"), @"Click to resume first-run setup at any point");

        /// <summary>
        /// "First-run setup"
        /// </summary>
        public static LocalisableString FirstRunSetupTitle => new TranslatableString(getKey(@"first_run_setup_title"), @"First-run setup");

        /// <summary>
        /// "Set up type!beat to suit you"
        /// </summary>
        public static LocalisableString FirstRunSetupDescription => new TranslatableString(getKey(@"first_run_setup_description"), @"Set up type!beat to suit you");

        /// <summary>
        /// "Welcome"
        /// </summary>
        public static LocalisableString WelcomeTitle => new TranslatableString(getKey(@"welcome_title"), @"Welcome");

        /// <summary>
        /// "Welcome to the first-run setup guide!
        ///
        /// type!beat is a very configurable game, and diving straight into the settings can sometimes be overwhelming. This guide will help you get the important choices out of the way to ensure a great first experience!"
        /// </summary>
        public static LocalisableString WelcomeDescription => new TranslatableString(getKey(@"welcome_description"), @"Welcome to the first-run setup guide!

type!beat is a very configurable game, and diving straight into the settings can sometimes be overwhelming. This guide will help you get the important choices out of the way to ensure a great first experience!");

        /// <summary>
        /// "The size of the type!beat user interface can be adjusted to your liking."
        /// </summary>
        public static LocalisableString UIScaleDescription => new TranslatableString(getKey(@"ui_scale_description"), @"The size of the type!beat user interface can be adjusted to your liking.");

        /// <summary>
        /// "Auto-aligner"
        /// </summary>
        public static LocalisableString LocalAligner => new TranslatableString(getKey(@"local_aligner"), @"Auto-aligner");

        /// <summary>
        /// The local auto-aligner pitch shown during first-run setup.
        /// </summary>
        public static LocalisableString LocalAlignerDescription => new TranslatableString(getKey(@"local_aligner_description"),
            @"When you create a map, type!beat can use an ML aligner to time the song's lyrics to the audio.

The aligner runs on your own machine, it's a download of roughly 2-3 GB, and it is worth installing if you have a decent graphics card or a fast CPU.

You can always install the aligner later from Settings.");

        /// <summary>
        /// "Install the local auto-aligner"
        /// </summary>
        public static LocalisableString InstallLocalAligner => new TranslatableString(getKey(@"install_local_aligner"), @"Install the local auto-aligner");

        private static string getKey(string key) => $@"{prefix}:{key}";
    }
}
