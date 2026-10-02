// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using NUnit.Framework;
using typebeat.Game.Beatmaps.Formats;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class BeatmapOnlineIdEncoderTest
    {
        [TestCase("\n")]
        [TestCase("\r\n")]
        public void ReplacesStaleIdsOnlyInsideMetadata(string newline)
        {
            string source = "\uFEFFtype!beat file format v2" + newline
                            + "[Metadata]" + newline + "Title:天" + newline
                            + "BeatmapID:0" + newline + "BeatmapSetID:-1" + newline
                            + "UnknownField:kept" + newline + newline
                            + "[Lyrics]" + newline + "BeatmapSetID:-1" + newline;
            string expected = source.Replace("BeatmapID:0", "BeatmapID:555")
                                    .Replace("BeatmapSetID:-1" + newline + "UnknownField", "BeatmapSetID:241" + newline + "UnknownField");

            Assert.That(BeatmapOnlineIdEncoder.Encode(source, 555, 241), Is.EqualTo(expected));
        }

        [TestCase("\n")]
        [TestCase("\r\n")]
        public void CorrectIdsAreByteStable(string newline)
        {
            string source = "[Metadata]" + newline + "BeatmapID: 555" + newline + "BeatmapSetID: 241" + newline;
            Assert.That(BeatmapOnlineIdEncoder.Encode(source, 555, 241), Is.EqualTo(source));
        }

        [Test]
        public void FillsEachMissingField()
        {
            string source = "[Metadata]\nTitle:test\nBeatmapID:555\n\n[Difficulty]\n";
            string encoded = BeatmapOnlineIdEncoder.Encode(source, 555, 241);
            Assert.That(encoded, Does.Contain("BeatmapID:555\n"));
            Assert.That(encoded, Does.Contain("BeatmapSetID:241\n"));
            Assert.That(BeatmapOnlineIdEncoder.Encode(encoded, 555, 241), Is.EqualTo(encoded));
        }

        [Test]
        public void MissingMetadataFailsBeforeUpload()
        {
            Assert.Throws<InvalidDataException>(() => BeatmapOnlineIdEncoder.Encode("[Lyrics]\n", 555, 241));
        }
    }
}
