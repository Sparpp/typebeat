// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// A BLANK beatmap is one with no hit objects at all: in type!beat terms, a song with no lyric
    /// lines and therefore not a single typeable cell. It is a first-class authoring state (an
    /// audio-only import creates one deliberately, so the words and their timing can be written in
    /// the editor from scratch), but it is not a playable one: a play with zero cells has no
    /// completion ratio to score against, so it would either divide by zero or finish the instant
    /// it began at a perfect rank, and either way pollute the shared leaderboards.
    ///
    /// Every entry point into gameplay therefore checks <see cref="IsBlank(IBeatmap)"/> first and says so
    /// rather than starting the play (see <c>SoloSongSelect.OnStart</c> and
    /// <c>Editor.TestGameplay</c>). This mirrors the editor's existing refusal to SUBMIT an empty
    /// beatmap; playing one is the same category of nothing-to-do.
    /// </summary>
    public static class BlankBeatmap
    {
        /// <summary>
        /// Whether <paramref name="beatmap"/> carries nothing to play. Null is NOT blank: a beatmap
        /// that failed to load is a different problem with its own reporting, and answering "this
        /// map has no lyrics" to a corrupt file would send the user off in the wrong direction.
        /// </summary>
        public static bool IsBlank(IBeatmap? beatmap) => beatmap != null && beatmap.HitObjects.Count == 0;

        /// <summary>
        /// Whether the beatmap <paramref name="working"/> would play is blank. A
        /// <see cref="DummyWorkingBeatmap"/> (no beatmaps installed, or nothing selected yet) is
        /// never reported as blank: it is not a real map, and the callers' own "nothing selected"
        /// handling owns that case.
        /// </summary>
        public static bool IsBlank(WorkingBeatmap? working)
            => working != null && working is not DummyWorkingBeatmap && IsBlank(working.Beatmap);
    }
}
