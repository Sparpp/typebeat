// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Tiff;

namespace typebeat.Game.Graphics
{
    /// <summary>
    /// Narrows what ImageSharp will decode for the whole process. osu-framework loads every texture
    /// (beatmap backgrounds, storyboards, skins, online covers) through <c>Image.Load</c> with
    /// <see cref="SixLabors.ImageSharp.Configuration.Default"/>, which picks the decoder from the file's first bytes, not its
    /// name. So a downloaded beatmap's "bg.jpg" can really be a TIFF.
    ///
    /// <para>ImageSharp 3.1.11 (the version osu-framework pins) has a BigTIFF decoder that a crafted file
    /// can keep in an endless loop (GHSA-wmxv-xphr-5c9g). The only fixed release is ImageSharp 4.1.2,
    /// a major version osu-framework does not use. No beatmap or skin needs TIFF, so TIFF decoding is
    /// switched off instead: the framework's texture store treats the refusal like any unreadable
    /// image and shows nothing.</para>
    /// </summary>
    public static class ImageDecodingGuard
    {
        private static readonly object apply_lock = new object();

        private static bool applied;

        /// <summary>Switches TIFF decoding off in <see cref="SixLabors.ImageSharp.Configuration.Default"/>. Safe to call more than once.</summary>
        public static void Apply()
        {
            lock (apply_lock)
            {
                if (applied)
                    return;

                SixLabors.ImageSharp.Configuration.Default.ImageFormatsManager.SetDecoder(TiffFormat.Instance, new RefusingDecoder());
                applied = true;
            }
        }

        /// <summary>A decoder that refuses every image, so the format is detected but never parsed.</summary>
        private class RefusingDecoder : ImageDecoder
        {
            private const string message = "type!beat does not decode TIFF images (see ImageDecodingGuard).";

            protected override Image<TPixel> Decode<TPixel>(DecoderOptions options, Stream stream, CancellationToken cancellationToken)
                => throw new InvalidImageContentException(message);

            protected override Image Decode(DecoderOptions options, Stream stream, CancellationToken cancellationToken)
                => throw new InvalidImageContentException(message);

            protected override ImageInfo Identify(DecoderOptions options, Stream stream, CancellationToken cancellationToken)
                => throw new InvalidImageContentException(message);
        }
    }
}
