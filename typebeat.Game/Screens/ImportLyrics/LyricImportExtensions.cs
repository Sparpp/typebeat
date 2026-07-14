// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Linq;

namespace typebeat.Game.Screens.ImportLyrics
{
    /// <summary>
    /// The raw-input file extensions the lyric-import flow accepts (audio + lyrics). Distinct from
    /// the packaged <c>.osz</c> that beatmap import already handles, so registering these routes the
    /// raw inputs to <see cref="ImportLyricsScreen"/> without shadowing normal beatmap-set drops.
    /// </summary>
    public static class LyricImportExtensions
    {
        public static readonly string[] AUDIO = { ".mp3", ".ogg", ".wav" };
        public static readonly string[] LYRICS = { ".txt", ".lrc" };
        public static readonly string[] ALL = AUDIO.Concat(LYRICS).ToArray();

        public static bool IsAudio(string path) => AUDIO.Contains(extension(path));
        public static bool IsLyrics(string path) => LYRICS.Contains(extension(path));

        private static string extension(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant();
    }
}
