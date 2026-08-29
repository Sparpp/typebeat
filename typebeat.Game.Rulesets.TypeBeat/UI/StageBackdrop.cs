// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Linq;
using typebeat.Game.Storyboards;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// The one decision behind the lyric stage: is there anything to SEE behind it (so the stage
    /// shows a readability scrim over the map's image/video), or is there nothing (so it keeps the
    /// flat opaque monkeytype panel)? Pulled out of <see cref="TypeBeatPlayfield"/>'s loader as a
    /// pure function so it can be pinned without standing up a gameplay scene.
    /// </summary>
    public static class StageBackdrop
    {
        /// <summary>
        /// Whether the map paints something of its own behind the stage.
        /// <paramref name="showStoryboard"/> is the user's "beatmap storyboard/video" setting: it
        /// gates the video, never the background image, which is drawn regardless.
        /// </summary>
        public static bool HasBackdrop(string? backgroundFile, Storyboard? storyboard, bool showStoryboard)
            => !string.IsNullOrEmpty(backgroundFile) || (showStoryboard && HasRenderableContent(storyboard));

        /// <summary>
        /// Whether the storyboard has an element that can actually paint.
        /// <see cref="Storyboard.HasDrawable"/> is not that question: <see cref="StoryboardVideo"/>
        /// reports <c>IsDrawable</c> unconditionally, so a map whose [Events] names a video FILE THAT
        /// IS NOT IN THE SET (an audio-only download of a video map, a set whose video was deleted)
        /// answers yes and the stage lays a translucent scrim over plain black. So a video counts
        /// only when its file resolves in the beatmap set, which is the same thing
        /// <see cref="Storyboards.Drawables.DrawableStoryboardVideo"/> discovers a moment later when
        /// its texture store hands back no stream and it renders nothing.
        /// </summary>
        public static bool HasRenderableContent(Storyboard? storyboard)
        {
            if (storyboard == null)
                return false;

            return storyboard.Layers.Any(layer => layer.Elements.Any(element => element switch
            {
                StoryboardVideo video => storyboard.GetStoragePathFromStoryboardPath(video.Path) != null,
                _ => element.IsDrawable,
            }));
        }
    }
}
