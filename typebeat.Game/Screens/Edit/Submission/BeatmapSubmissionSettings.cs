// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Bindables;
using typebeat.Game.Online.API.Requests;

namespace typebeat.Game.Screens.Edit.Submission
{
    public class BeatmapSubmissionSettings
    {
        public GetBeatmapSetRequest? LatestOnlineStateRequest { get; set; }

        public Bindable<BeatmapSubmissionTarget> Target { get; } = new Bindable<BeatmapSubmissionTarget>();

        public Bindable<bool> NotifyOnDiscussionReplies { get; } = new Bindable<bool>();

        /// <summary>
        /// Whether the mapset is flagged as containing explicit content (adult language, themes, ...).
        /// </summary>
        /// <remarks>
        /// This is a property of the mapset, not a user preference, so it is never persisted to local
        /// config; it defaults to unchecked and is prefilled from the set's online state when one exists.
        /// </remarks>
        public Bindable<bool> ExplicitContent { get; } = new Bindable<bool>();
    }
}
