// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using typebeat.Game.IO;

namespace typebeat.Game.Beatmaps
{
    internal interface IBeatmapResourceProvider : IStorageResourceProvider
    {
        /// <summary>
        /// Retrieve a global large texture store, used for loading beatmap backgrounds.
        /// </summary>
        TextureStore LargeTextureStore { get; }

        /// <summary>
        /// Retrieve a global large texture store, used specifically for retrieving cropped beatmap panel backgrounds.
        /// </summary>
        TextureStore BeatmapPanelTextureStore { get; }

        /// <summary>
        /// Access a global track store for retrieving beatmap tracks from.
        /// </summary>
        ITrackStore Tracks { get; }

        /// <summary>
        /// The store for beatmap tracks that carry the map's own audio gain (see
        /// <see cref="BeatmapMetadata.AudioGain"/>), asked for by
        /// <see cref="Audio.ScaledAudioStore.Key"/> rather than by file path alone.
        ///
        /// <para>ONE store, for the lifetime of the provider, which is the whole reason it lives here
        /// instead of being built where a gained track is wanted: <c>AudioManager.GetTrackStore</c>
        /// registers what it builds in a collection that only releases a store when the store is
        /// disposed, so one built per track load roots itself and every track in it forever. Null when
        /// there is no audio manager to build it from, which is the caller's cue to play the file as
        /// imported.</para>
        /// </summary>
        ITrackStore? GainedTracks { get; }
    }
}
