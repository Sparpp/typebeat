// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Game.Rulesets.TypeBeat.Beatmaps;
using osu.Game.Rulesets.TypeBeat.Import;

namespace osu.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the headless in-editor import path: <see cref="LyricMapImporter.ProduceTimingJsonAsync"/>
    /// (the timing.json-only entry the editor's "auto-time to this song" uses) falls back to the
    /// line-stamped LRC synthesiser when no aligner environment is present, and the produced
    /// timing.json parses straight back into lyric lines via <see cref="TimingJsonLoader.TryParse"/>.
    /// </summary>
    [TestFixture]
    public class EditorImportTest
    {
        [Test]
        public async Task ProduceTimingJsonLrcFallbackParsesToLines()
        {
            // A tiny temp file stands in for audio — the LRC fallback never reads it, only checks existence.
            string audio = Path.Combine(Path.GetTempPath(), $"tb_audio_{Guid.NewGuid():N}.mp3");
            await File.WriteAllBytesAsync(audio, new byte[16]).ConfigureAwait(false);

            const string lyrics = "[00:01.00]hello world\n[00:03.50]second line\n";

            try
            {
                (var result, string? timingJson) = await LyricMapImporter.ProduceTimingJsonAsync(
                    audio, lyrics, "Artist", "Title",
                    // Force the fallback: no configured path, and a start dir with no aligner nearby.
                    configuredLyricLabPath: null,
                    startDirectories: new[] { Path.GetTempPath() },
                    progress: _ => { },
                    token: CancellationToken.None).ConfigureAwait(false);

                Assert.That(result.Success, Is.True, result.Error);
                Assert.That(timingJson, Is.Not.Null);

                Assert.That(TimingJsonLoader.TryParse(timingJson!, out var lines), Is.True);
                Assert.That(lines.Count, Is.EqualTo(2));
                Assert.That(lines[0].RawText, Is.EqualTo("hello world"));
                Assert.That(lines[0].StartTime, Is.EqualTo(1000));
                Assert.That(lines[1].StartTime, Is.EqualTo(3500));

                // No word timing from LRC — one whole-line unit each => Line granularity.
                Assert.That(TypeBeatEditorOperations.InferGranularity(lines), Is.EqualTo(TimingGranularity.Line));
            }
            finally
            {
                File.Delete(audio);
            }
        }

        [Test]
        public async Task ProduceTimingJsonUnstampedWithoutAlignerFails()
        {
            string audio = Path.Combine(Path.GetTempPath(), $"tb_audio_{Guid.NewGuid():N}.mp3");
            await File.WriteAllBytesAsync(audio, new byte[16]).ConfigureAwait(false);

            try
            {
                (var result, string? timingJson) = await LyricMapImporter.ProduceTimingJsonAsync(
                    audio, "hello world\nno stamps here", "Artist", "Title",
                    null, new[] { Path.GetTempPath() }, _ => { }, CancellationToken.None).ConfigureAwait(false);

                Assert.That(result.Success, Is.False);
                Assert.That(timingJson, Is.Null);
                Assert.That(result.Error, Does.Contain("timestamp").IgnoreCase);
            }
            finally
            {
                File.Delete(audio);
            }
        }
    }
}
