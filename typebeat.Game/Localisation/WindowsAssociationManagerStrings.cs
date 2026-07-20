// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;

namespace typebeat.Game.Localisation
{
    public static class WindowsAssociationManagerStrings
    {
        private const string prefix = @"typebeat.Game.Resources.Localisation.WindowsAssociationManager";

        /// <summary>
        /// "type!beat Beatmap"
        /// </summary>
        public static LocalisableString OsuBeatmap => new TranslatableString(getKey(@"osu_beatmap"), @"type!beat Beatmap");

        /// <summary>
        /// "type!beat Replay"
        /// </summary>
        public static LocalisableString OsuReplay => new TranslatableString(getKey(@"osu_replay"), @"type!beat Replay");

        /// <summary>
        /// "type!beat Skin"
        /// </summary>
        public static LocalisableString OsuSkin => new TranslatableString(getKey(@"osu_skin"), @"type!beat Skin");

        /// <summary>
        /// "type!beat"
        /// </summary>
        public static LocalisableString OsuProtocol => new TranslatableString(getKey(@"osu_protocol"), @"type!beat");

        /// <summary>
        /// "type!beat Multiplayer"
        /// </summary>
        public static LocalisableString OsuMultiplayer => new TranslatableString(getKey(@"osu_multiplayer"), @"type!beat Multiplayer");

        private static string getKey(string key) => $@"{prefix}:{key}";
    }
}