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
            // A tiny temp file stands in for audio; the LRC fallback never reads it, only checks existence.
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

                // No word timing from LRC: one whole-line unit each => Line granularity.
                Assert.That(TypeBeatEditorOperations.InferGranularity(lines), Is.EqualTo(TimingGranularity.Line));
            }
            finally
            {
                File.Delete(audio);
            }
        }

        [Test]
        public async Task ProduceTimingJsonCarriesTheAuthoringMarksThrough()
        {
            // The headless editor import of line-stamped lyrics that carry both marks: the pipe
            // subdivides its word (and lifts the map off Line granularity, which persists no
            // sub-word data), the ampersand line becomes a freestyle line where the LRC put it.
            string audio = Path.Combine(Path.GetTempPath(), $"tb_audio_{Guid.NewGuid():N}.mp3");
            await File.WriteAllBytesAsync(audio, new byte[16]).ConfigureAwait(false);

            const string lyrics = "[00:01.00]ple|ase stay\n[00:05.00]&&\n[00:09.00]last one\n[00:11.00]\n";

            try
            {
                (var result, string? timingJson) = await LyricMapImporter.ProduceTimingJsonAsync(
                    audio, lyrics, "Artist", "Title",
                    configuredLyricLabPath: null,
                    startDirectories: new[] { Path.GetTempPath() },
                    progress: _ => { },
                    token: CancellationToken.None).ConfigureAwait(false);

                Assert.That(result.Success, Is.True, result.Error);
                Assert.That(TimingJsonLoader.TryParse(timingJson!, out var lines), Is.True);

                Assert.That(lines.Count, Is.EqualTo(3));
                Assert.That(lines[0].RawText, Is.EqualTo("please stay"));
                Assert.That(lines[0].Units[0].SyllableSplits, Is.EqualTo(new[] { 3 }));
                Assert.That(lines[1].RawText, Is.EqualTo("&&"), "the marker line survives on its own");
                Assert.That(lines[1].StartTime, Is.EqualTo(5000));

                Assert.That(TypeBeatEditorOperations.InferGranularity(lines), Is.EqualTo(TimingGranularity.Syllable));
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

                // The hint used to offer signing in for server-side alignment. That aligner is
                // retired, so the two things left are the LOCAL install and the line stamps, and the
                // copy must name both rather than sending the user at an endpoint that is gone.
                Assert.Multiple(() =>
                {
                    Assert.That(result.Error, Does.Contain("Settings > Experimental"));
                    Assert.That(result.Error, Does.Contain("[mm:ss.xx]"));
                    Assert.That(result.Error, Does.Not.Contain("server").IgnoreCase);
                    Assert.That(result.Error, Does.Not.Contain("sign in").IgnoreCase);
                });
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
                    useAutomaticAlignment: false).ConfigureAwait(false);

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
                // Automatic alignment is off, so no aligner runs and the failure directs the user at
                // the TOGGLE rather than at any aligner. This used to pass a remote stub and assert it
                // was never called; the remote seam is gone with the server-side aligner, so what is
                // left to pin is that the off-switch alone short-circuits to the line-stamp arm (the
                // hint names the toggle, never the "no auto-aligner is available" install copy).
                (var result, string? timingJson) = await LyricMapImporter.ProduceTimingJsonAsync(
                    audio, "hello world\nno stamps", "Artist", "Title",
                    null, new[] { Path.GetTempPath() }, _ => { }, CancellationToken.None,
                    useAutomaticAlignment: false).ConfigureAwait(false);

                Assert.That(result.Success, Is.False);
                Assert.That(timingJson, Is.Null);
                Assert.That(result.Error, Does.Contain("automatic alignment").IgnoreCase);
                Assert.That(result.Error, Does.Not.Contain("Settings > Experimental"), "the toggle is the fix here, not the install");
            }
            finally
            {
                File.Delete(audio);
            }
        }
    }
}
