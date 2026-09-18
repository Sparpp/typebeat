// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.IO.Stores;

namespace typebeat.Game.Audio
{
    /// <summary>
    /// A resource store holding exactly one audio file - the scaled WAV
    /// <see cref="ScaledAudio"/> produced - so the framework's track store can be asked for a track
    /// built from it (see <c>AudioManager.GetTrackStore</c>).
    ///
    /// <para>It answers to the name the beatmap asked for rather than a name of its own, because that is
    /// the name the track store is asked for and the one the map's own <c>AudioFilename</c> already
    /// means; nothing else is ever requested of it.</para>
    /// </summary>
    internal sealed class ScaledAudioStore : IResourceStore<byte[]>
    {
        private readonly byte[] data;
        private readonly string name;

        public ScaledAudioStore(byte[] data, string name)
        {
            this.data = data;
            this.name = name;
        }

        public byte[] Get(string requestedName) => data;

        public Task<byte[]> GetAsync(string requestedName, CancellationToken cancellationToken = default) => Task.FromResult(data);

        public Stream GetStream(string requestedName) => new MemoryStream(data, writable: false);

        public IEnumerable<string> GetAvailableResources()
        {
            yield return name;
        }

        /// <summary>
        /// Nothing to release: the scaled audio is a plain array the tracks built from it hold for as
        /// long as they need it, and this store does not own it.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
