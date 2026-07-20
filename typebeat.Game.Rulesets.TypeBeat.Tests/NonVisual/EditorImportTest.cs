// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Import;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
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

        [Test]
        public async Task EmptyLyricsGivesItsOwnError()
        {
            string audio = Path.Combine(Path.GetTempPath(), $"tb_audio_{Guid.NewGuid():N}.mp3");
            await File.WriteAllBytesAsync(audio, new byte[16]).ConfigureAwait(false);

            try
            {
                (var result, string? timingJson) = await LyricMapImporter.ProduceTimingJsonAsync(
                    audio, "   \n  \n", "Artist", "Title",
                    null, new[] { Path.GetTempPath() }, _ => { }, CancellationToken.None).ConfigureAwait(false);

                Assert.That(result.Success, Is.False);
                Assert.That(timingJson, Is.Null);
                // Distinct from the "no aligner / no timestamps" message.
                Assert.That(result.Error, Does.Contain("empty").IgnoreCase);
            }
            finally
            {
                File.Delete(audio);
            }
        }

        [Test]
        public async Task AutomaticAlignmentOff_StampedLyrics_UsesLineTiming()
        {
            string audio = Path.Combine(Path.GetTempPath(), $"tb_audio_{Guid.NewGuid():N}.mp3");
            await File.WriteAllBytesAsync(audio, new byte[16]).ConfigureAwait(false);

            try
            {
                (var result, string? timingJson) = await LyricMapImporter.ProduceTimingJsonAsync(
                    audio, "[00:01.00]hello world\n[00:03.50]second line\n", "Artist", "Title",
                    null, new[] { Path.GetTempPath() }, _ => { }, CancellationToken.None,
                    remoteAlign: null, useAutomaticAlignment: false).ConfigureAwait(false);

                Assert.That(result.Success, Is.True, result.Error);
                Assert.That(TimingJsonLoader.TryParse(timingJson!, out var lines), Is.True);
                Assert.That(lines.Count, Is.EqualTo(2));
            }
            finally
            {
                File.Delete(audio);
            }
        }

        [Test]
        public async Task AutomaticAlignmentOff_UnstampedLyrics_PointsAtTheToggle()
        {
            string audio = Path.Combine(Path.GetTempPath(), $"tb_audio_{Guid.NewGuid():N}.mp3");
            await File.WriteAllBytesAsync(audio, new byte[16]).ConfigureAwait(false);

            try
            {
                // A remote aligner IS supplied, but automatic alignment is off, so it must never be
                // invoked — the failure directs the user to the toggle, not to the server.
                bool remoteCalled = false;
                RemoteAligner remote = (_, _, _, _, _, _) =>
                {
                    remoteCalled = true;
                    return Task.FromResult(RemoteAlignOutcome.Ok("{}"));
                };

                (var result, string? timingJson) = await LyricMapImporter.ProduceTimingJsonAsync(
                    audio, "hello world\nno stamps", "Artist", "Title",
                    null, new[] { Path.GetTempPath() }, _ => { }, CancellationToken.None,
                    remoteAlign: remote, useAutomaticAlignment: false).ConfigureAwait(false);

                Assert.That(result.Success, Is.False);
                Assert.That(timingJson, Is.Null);
                Assert.That(remoteCalled, Is.False, "the aligner must not run when automatic alignment is off");
                Assert.That(result.Error, Does.Contain("automatic alignment").IgnoreCase);
            }
            finally
            {
                File.Delete(audio);
            }
        }
    }
}
