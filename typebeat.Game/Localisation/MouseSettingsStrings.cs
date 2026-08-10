// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace typebeat.Game.Localisation
{
    public static class MouseSettingsStrings
    {
        private const string prefix = @"typebeat.Game.Resources.Localisation.MouseSettings";

        /// <summary>
        /// "Mouse"
        /// </summary>
        public static LocalisableString Mouse => new TranslatableString(getKey(@"mouse"), @"Mouse");

        /// <summary>
        /// "High precision mouse"
        /// </summary>
        public static LocalisableString HighPrecisionMouse => new TranslatableString(getKey(@"high_precision_mouse"), @"High precision mouse");

        /// <summary>
        /// "Attempts to bypass any operating system mouse acceleration. On Windows, this is equivalent to what used to be known as &quot;Raw Input&quot;."
        /// </summary>
        public static LocalisableString HighPrecisionMouseTooltip => new TranslatableString(getKey(@"high_precision_mouse_tooltip"), @"Attempts to bypass any operating system mouse acceleration. On Windows, this is equivalent to what used to be known as ""Raw Input"".");

        /// <summary>
        /// "Disable clicks during gameplay"
        /// </summary>
        public static LocalisableString DisableClicksDuringGameplay => new TranslatableString(getKey(@"disable_clicks"), @"Disable clicks during gameplay");

        /// <summary>
        /// "Enable high precision mouse to adjust sensitivity"
        /// </summary>
        public static LocalisableString EnableHighPrecisionForSensitivityAdjust => new TranslatableString(getKey(@"enable_high_precision_for_sensitivity_adjust"), @"Enable high precision mouse to adjust sensitivity");

        /// <summary>
        /// "Cursor sensitivity"
        /// </summary>
        public static LocalisableString CursorSensitivity => new TranslatableString(getKey(@"cursor_sensitivity"), @"Cursor sensitivity");

        /// <summary>
        /// "This setting has known issues on your platform. If you encounter problems, it is recommended to adjust sensitivity externally and keep this disabled for now."
        /// </summary>
        public static LocalisableString HighPrecisionPlatformWarning => new TranslatableString(getKey(@"high_precision_platform_warning"), @"This setting has known issues on your platform. If you encounter problems, it is recommended to adjust sensitivity externally and keep this disabled for now.");

        /// <summary>
        /// "Always"
        /// </summary>
        public static LocalisableString AlwaysConfine => new TranslatableString(getKey(@"always_confine"), @"Always");

        /// <summary>
        /// "During Gameplay"
        /// </summary>
        public static LocalisableString ConfineDuringGameplay => new TranslatableString(getKey(@"confine_during_gameplay"), @"During Gameplay");

        /// <summary>
        /// "Never"
        /// </summary>
        public static LocalisableString NeverConfine => new TranslatableString(getKey(@"never_confine"), @"Never");

        private static string getKey(string key) => $@"{prefix}:{key}";
    }
}
