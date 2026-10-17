// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using typebeat.Game.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins <see cref="ImageDecodingGuard"/>: with it applied, a TIFF reaches osu-framework's texture store
    /// as "no texture" (the same as any unreadable file), and the formats beatmaps actually use still load.
    /// The file name says ".jpg" on purpose: ImageSharp picks the decoder from the content.
    /// </summary>
    [TestFixture]
    public class ImageDecodingGuardTest
    {
        [OneTimeSetUp]
        public void SetUp() => ImageDecodingGuard.Apply();

        [Test]
        public void TiffIsRefused()
        {
            byte[] tiff = encode((image, stream) => image.SaveAsTiff(stream));

            Assert.Catch<InvalidImageContentException>(() => Image.Load<Rgba32>(tiff));
            Assert.That(load("bg.jpg", tiff), Is.Null, "the texture store treats a refused image as missing");
        }

        [Test]
        public void PngAndJpegStillLoad()
        {
            using (var png = load("bg.png", encode((image, stream) => image.SaveAsPng(stream))))
                Assert.That(png?.Width, Is.EqualTo(4));

            using (var jpeg = load("bg.jpg", encode((image, stream) => image.SaveAsJpeg(stream))))
                Assert.That(jpeg?.Width, Is.EqualTo(4));
        }

        [Test]
        public void ApplyingTwiceIsHarmless()
        {
            ImageDecodingGuard.Apply();

            using (var png = load("bg.png", encode((image, stream) => image.SaveAsPng(stream))))
                Assert.That(png, Is.Not.Null);
        }

        private static byte[] encode(Action<Image<Rgba32>, Stream> save)
        {
            using var image = new Image<Rgba32>(4, 4, new Rgba32(200, 240, 80));
            using var stream = new MemoryStream();
            save(image, stream);
            return stream.ToArray();
        }

        private static TextureUpload? load(string name, byte[] data)
            => new TextureLoaderStore(new SingleFileStore(name, data)).Get(name);

        private class SingleFileStore : IResourceStore<byte[]>
        {
            private readonly string name;
            private readonly byte[] data;

            public SingleFileStore(string name, byte[] data)
            {
                this.name = name;
                this.data = data;
            }

            public byte[] Get(string name) => name == this.name ? data : null!;

            public Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Get(name));

            public Stream? GetStream(string name) => name == this.name ? new MemoryStream(data) : null;

            public IEnumerable<string> GetAvailableResources() => new[] { name };

            public void Dispose()
            {
            }
        }
    }
}
