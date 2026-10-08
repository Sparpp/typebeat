// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Globalization;

namespace typebeat.Game.Screens.ImportLyrics
{
    /// <summary>How an installed aligner's <c>ALIGNER_VERSION</c> stands against the one this build ships.</summary>
    public enum AlignerVersionStatus
    {
        /// <summary>The same version, or nothing to compare: no aligner installed, or a build that ships none.</summary>
        Matches,

        /// <summary>The installed script is older (a version-1 script, which predates the constant, included).</summary>
        Older,

        /// <summary>The installed script is newer, e.g. a player who went back to an older build of the game.</summary>
        Newer,

        /// <summary>Two tags that are not both whole numbers, and differ.</summary>
        Different,
    }

    /// <summary>
    /// The one rule for whether the installed local auto-aligner may run (backlog 410): ANY mismatch
    /// with the version this build ships blocks automatic alignment, older or newer, until the player
    /// presses the settings button that copies the shipped scripts over (the environment is kept and
    /// only what the new scripts add is downloaded, so it usually takes under a minute). Nothing is
    /// forced: the player is told why and where, and every other import path keeps working. Every surface that reports the block (the import screen's tooltip, the
    /// importer's refusal, the session prompt, the settings button and the first-run screen) builds
    /// its words here, so they name the same versions and the same place.
    /// </summary>
    public static class LocalAlignerVersion
    {
        /// <summary>Where the install button sits, as the settings overlay shows it.</summary>
        public const string SETTINGS_PATH = "Settings > Experimental > Lyric timing";

        /// <summary>A version as shown to the player: a script with no constant is version 1.</summary>
        public static string Display(string? version) => version ?? "1";

        /// <summary>
        /// Compares an installed version against the shipped one. Whole numbers compare numerically
        /// (so "10" is newer than "9"), anything else by exact text. A null shipped version means the
        /// build carries no aligner to compare against, which never blocks: there is nothing the
        /// button could update to.
        /// </summary>
        public static AlignerVersionStatus Compare(string? installed, string? shipped)
        {
            if (shipped == null)
                return AlignerVersionStatus.Matches;

            string current = Display(installed);

            if (int.TryParse(shipped, NumberStyles.None, CultureInfo.InvariantCulture, out int s)
                && int.TryParse(current, NumberStyles.None, CultureInfo.InvariantCulture, out int i))
            {
                return i == s ? AlignerVersionStatus.Matches : i < s ? AlignerVersionStatus.Older : AlignerVersionStatus.Newer;
            }

            return string.Equals(current, shipped, StringComparison.Ordinal) ? AlignerVersionStatus.Matches : AlignerVersionStatus.Different;
        }

        /// <summary>As <see cref="Compare"/>, for an install that may not exist: nothing installed is never a mismatch.</summary>
        public static AlignerVersionStatus StatusOf(bool isInstalled, string? installed, string? shipped)
            => isInstalled ? Compare(installed, shipped) : AlignerVersionStatus.Matches;

        /// <summary>The settings button's verb for a mismatch: an older install is updated, any other is reinstalled.</summary>
        public static string ActionVerb(AlignerVersionStatus status) => status == AlignerVersionStatus.Older ? "Update" : "Reinstall";

        /// <summary>What is wrong, as a predicate on "your local auto-aligner": "is out of date (v8, ...)". Null when it matches.</summary>
        private static string? describe(AlignerVersionStatus status, string? installed, string? shipped)
        {
            string versions = $"(v{Display(installed)}, this version of type!beat needs v{Display(shipped)})";

            switch (status)
            {
                case AlignerVersionStatus.Older:
                    return $"is out of date {versions}";

                case AlignerVersionStatus.Newer:
                    return $"is from a newer version of type!beat {versions}";

                case AlignerVersionStatus.Different:
                    return $"does not match this version of type!beat {versions}";

                default:
                    return null;
            }
        }

        /// <summary>
        /// The import screen's tooltip on the greyed-out automatic alignment choice, e.g. "Your local
        /// auto-aligner is out of date (v8, this version of type!beat needs v9). Update it in Settings
        /// &gt; Experimental &gt; Lyric timing to use automatic alignment." Null when the versions match.
        /// </summary>
        public static string? Advice(AlignerVersionStatus status, string? installed, string? shipped)
        {
            string? what = describe(status, installed, shipped);
            return what == null ? null : $"Your local auto-aligner {what}. {ActionVerb(status)} it in {SETTINGS_PATH} to use automatic alignment.";
        }

        /// <summary>As <see cref="Advice(AlignerVersionStatus, string?, string?)"/>, read off a manager.</summary>
        public static string? Advice(ILocalAlignerManager manager) => Advice(manager.VersionStatus, manager.InstalledVersion, manager.ShippedVersion);

        /// <summary>
        /// The importer's refusal, the same sentence as a bare clause (import surfaces wrap their
        /// errors: "Timing failed: ..."). Null when the versions match.
        /// </summary>
        public static string? ImportRefusal(AlignerVersionStatus status, string? installed, string? shipped)
        {
            string? advice = Advice(status, installed, shipped);
            return advice == null ? null : char.ToLowerInvariant(advice[0]) + advice.Substring(1);
        }

        /// <summary>The once-a-session prompt's text, which a click on it acts on. Null when the versions match.</summary>
        public static string? NotificationText(AlignerVersionStatus status, string? installed, string? shipped)
        {
            string? what = describe(status, installed, shipped);
            return what == null ? null : $"Automatic alignment is off: your local auto-aligner {what}. Click here to {ActionVerb(status).ToLowerInvariant()} it in Settings.";
        }

        /// <summary>As <see cref="NotificationText(AlignerVersionStatus, string?, string?)"/>, read off a manager.</summary>
        public static string? NotificationText(ILocalAlignerManager manager) => NotificationText(manager.VersionStatus, manager.InstalledVersion, manager.ShippedVersion);

        /// <summary>The first-run screen's line for an installed aligner that does not match. Null when the versions match.</summary>
        public static string? FirstRunStatus(AlignerVersionStatus status, string? installed, string? shipped)
        {
            string? what = describe(status, installed, shipped);

            if (what == null)
                return null;

            string verb = ActionVerb(status);
            return $"The local auto-aligner is installed, but it {what}, so automatic alignment is unavailable until you {verb.ToLowerInvariant()} it: "
                   + $"press {verb} in {SETTINGS_PATH} (usually under a minute; the downloaded environment is kept).";
        }
    }
}
