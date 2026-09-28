// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.IO.Stores;

namespace typebeat.Game.Audio
{
    /// <summary>
    /// The resource store behind the game's ONE gained-audio track store: every request is a beatmap
    /// file plus the gain to apply to it, and the answer is a <see cref="ScaledAudioStream"/> over that
    /// file (see <see cref="ScaledAudio"/> for what the gain does to it).
    ///
    /// <para>WHY THE GAIN IS IN THE NAME. <c>AudioManager.GetTrackStore(store)</c> registers the store it
    /// builds in the audio manager's global collection, which only ever releases it on disposal, so a
    /// store built per track load roots itself and everything it holds forever. There is therefore
    /// exactly one of these for the lifetime of the beatmap cache, and since <c>ITrackStore.Get</c> takes
    /// nothing but a name, the gain travels in the name (see <see cref="Key"/>). The store itself is
    /// STATELESS: it caches no audio, holds no track, and a request decodes nothing by itself.</para>
    /// </summary>
    internal sealed class ScaledAudioStore : IResourceStore<byte[]>
    {
        /// <summary>
        /// Separates the gain from the file path in a request. Not legal in a beatmap set's storage
        /// paths, which are <c>&lt;set hash prefix&gt;/&lt;file hash&gt;</c> style.
        /// </summary>
        private const char separator = '|';

        private readonly IResourceStore<byte[]> files;

        public ScaledAudioStore(IResourceStore<byte[]> files)
        {
            this.files = files;
        }

        /// <summary>
        /// The name to ask this store's track store for, so it plays <paramref name="fileStorePath"/> at
        /// <paramref name="gain"/>. Round-trippable in both halves, because two gains that print the same
        /// would be two tracks that sound different under one name.
        /// </summary>
        public static string Key(string fileStorePath, double gain) =>
            $"{gain.ToString("R", CultureInfo.InvariantCulture)}{separator}{fileStorePath}";

        public Stream? GetStream(string name)
        {
            if (!tryParse(name, out string path, out double gain))
                return null;

            using (var source = files.GetStream(path))
            {
                if (source == null)
                    return null;

                return ScaledAudio.Open(source, gain);
            }
        }

        /// <summary>
        /// Never used by the track store, which only ever asks for a stream; implemented honestly rather
        /// than thrown from, so the store keeps the contract its interface promises.
        /// </summary>
        public byte[] Get(string name)
        {
            using (var stream = GetStream(name))
            {
                if (stream == null)
                    return null!;

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        public Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default) => Task.Run(() => Get(name), cancellationToken);

        /// <summary>
        /// Nothing to enumerate: a request is a file AND a gain, and the gains a set of maps asks for are
        /// not this store's to know.
        /// </summary>
        public IEnumerable<string> GetAvailableResources() => Array.Empty<string>();

        /// <summary>
        /// Nothing to release: the file store is owned by the beatmap cache, and the streams handed out
        /// are owned by the tracks built from them.
        /// </summary>
        public void Dispose()
        {
        }

        private static bool tryParse(string name, out string path, out double gain)
        {
            path = string.Empty;
            gain = 1;

            int split = name?.IndexOf(separator) ?? -1;

            if (split <= 0)
                return false;

            if (!double.TryParse(name![..split], NumberStyles.Float, CultureInfo.InvariantCulture, out gain))
                return false;

            path = name[(split + 1)..];
            return path.Length > 0;
        }
    }
}
