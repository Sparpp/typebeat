// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>
    /// The last step of an import: the .osz an imported map travels in (generated .osu, audio,
    /// optional video and vocals stem, provenance timing.json and lyrics.txt).
    /// </summary>
    public static class OszPackager
    {
        /// <summary>Creator tag stamped into generated maps' [Metadata].</summary>
        public const string CREATOR = "typebeat-lyriclab";

        /// <summary>
        /// Zips a self-contained .osz: generated .osu (with computed preview/lead-in), the original
        /// audio, and provenance (timing.json + lyrics.txt). Overwrites <paramref name="oszPath"/>.
        ///
        /// <para><paramref name="videoSourcePath"/> is the map's background video when it is its OWN
        /// file (an import that split a container: audio and video are two entries in the archive).
        /// Left null, a container still sitting in the audio slot doubles as the video exactly as it
        /// did before the split existed, and the archive holds the one file. Either way the [Events]
        /// line is written at offset 0: an extraction is sample-accurate, so there is nothing to
        /// correct, and 0 is the byte-identical legacy <c>Video,0,"file"</c> form.</para>
        /// </summary>
        /// <remarks>
        /// The language is written into the map metadata whenever supplied, including for a blank
        /// map. Callers without a language leave it unspecified. The optional progress callback receives the
        /// import summary line (the words that could not be romanised), which also comes back as
        /// <see cref="LyricImportResult.Notice"/>.
        /// </remarks>
        public static LyricImportResult PackageOsz(string oszPath, string artist, string title, string audioSourcePath, string timingJson, string lyricsContent,
                                                   string? videoSourcePath = null, string? language = null, Action<string>? progress = null,
                                                   string? vocalsStemSourcePath = null)
        {
            try
            {
                // THE IMPORT SUMMARY (backlog 330): what the stored map will decode to, read through
                // the loader itself, so the words listed are exactly the ones the editor will show
                // as unromanised.
                string? notice = null;
                if (TimingJsonLoader.TryParse(timingJson, out IReadOnlyList<LyricLine> decodedLines))
                {
                    notice = LyricOriginals.ImportSummary(LyricOriginals.UnromanisedWords(decodedLines).ToList());

                    if (notice != null)
                        progress?.Invoke(notice);
                }

                string? offeredLanguage = string.IsNullOrWhiteSpace(language) ? null : LyricOriginals.OfferedLanguage(language).ToCanonicalName();

                string audioFilename = Path.GetFileName(audioSourcePath);

                // Deliberately the UNSTRIPPED document: preview point and lead-in are read off the
                // aligner's own line times, so they are the same numbers this map has always got.
                (double previewTime, double audioLeadIn) = computePolish(timingJson);

                string? videoSource = videoSourcePath ?? (LyricImportExtensions.IsVideo(audioSourcePath) ? audioSourcePath : null);
                string? videoFilename = videoSource == null ? null : Path.GetFileName(videoSource);

                // The import boundary (backlog 255): backing vocals leave the lyric HERE, on the way
                // into the stored map, because the decode now reads a bracket as a literal mark.
                // The provenance timing.json written below keeps the aligner's text untouched.
                string osuText = LyricOsuFormat.GenerateOsu(artist, title, audioFilename, CREATOR, TimingJsonPostProcess.StripBackingVocalLines(timingJson),
                    previewTime, audioLeadIn, videoFilename: videoFilename, language: string.IsNullOrEmpty(offeredLanguage) ? null : offeredLanguage);

                if (File.Exists(oszPath))
                    File.Delete(oszPath);

                using (var archive = ZipFile.Open(oszPath, ZipArchiveMode.Create))
                {
                    string osuName = $"{LyricMapImporter.SanitizeFolderName(artist)} - {LyricMapImporter.SanitizeFolderName(title)} ({CREATOR}) [typebeat].osu";

                    var osuEntry = archive.CreateEntry(osuName);
                    using (var writer = new StreamWriter(osuEntry.Open()))
                        writer.Write(osuText);

                    archive.CreateEntryFromFile(audioSourcePath, audioFilename);

                    // The video only earns an entry of its own when it IS its own file; an
                    // unsplit container is already in the archive under the audio's name, and a
                    // second entry with that name would be a duplicate the importer would reject.
                    if (videoSource != null && !string.Equals(videoFilename, audioFilename, StringComparison.OrdinalIgnoreCase))
                        archive.CreateEntryFromFile(videoSource, videoFilename!);

                    // Provenance: original inputs travel inside the set (ignored by the game, kept for re-alignment).
                    using (var writer = new StreamWriter(archive.CreateEntry("timing.json").Open()))
                        writer.Write(timingJson);

                    using (var writer = new StreamWriter(archive.CreateEntry("lyrics.txt").Open()))
                        writer.Write(lyricsContent);

                    // The isolated vocals stem (backlog 392), when this import produced one. Named
                    // vocals.ogg/vocals.wav so the editor's waveform loader finds it without reading
                    // the document; an import with no aligner (or an older one) simply has none.
                    if (vocalsStemSourcePath != null && File.Exists(vocalsStemSourcePath))
                    {
                        string stemName = Path.GetExtension(vocalsStemSourcePath).Equals(".wav", StringComparison.OrdinalIgnoreCase)
                            ? VocalsStem.WAV_FILENAME
                            : VocalsStem.OGG_FILENAME;

                        archive.CreateEntryFromFile(vocalsStemSourcePath, stemName);
                    }
                }

                return LyricImportResult.Ok(oszPath) with { Notice = notice };
            }
            catch (ArgumentException e)
            {
                return LyricImportResult.Fail($"the map data was rejected: {e.Message}");
            }
            catch (Exception e)
            {
                return LyricImportResult.Fail($"packaging the map failed: {e.Message}");
            }
        }

        /// <summary>
        /// Re-encodes the aligner's 16 kHz mono vocals stem wav to Ogg Vorbis (backlog 392), beside
        /// the .osz, and returns the new path. Null when no ffmpeg is available or its Vorbis encoder
        /// fails: the caller then keeps the wav, since a larger stem is a smaller problem than a
        /// missing one. The stem is a view-only reading surface, so a failure here never fails an
        /// import.
        /// </summary>
        internal static async Task<string?> EncodedVocalsStemAsync(
            string stemWavPath, string destinationDirectory, string? configuredLyricLabPath,
            IEnumerable<string> startDirectories, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return null;

            string? ffmpeg = FfmpegAudioTrackExtractor.Resolve(configuredLyricLabPath, startDirectories);

            if (ffmpeg == null)
                return null;

            string destination = Path.Combine(destinationDirectory, VocalsStem.OGG_FILENAME);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                psi.ArgumentList.Add("-nostdin");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(stemWavPath);
                psi.ArgumentList.Add("-c:a");
                psi.ArgumentList.Add("libvorbis");
                psi.ArgumentList.Add("-q:a");
                psi.ArgumentList.Add("5");
                psi.ArgumentList.Add(destination);

                (int exitCode, string _) = await ImportProcessRunner.RunProcessAsync(psi, _ => { }, token).ConfigureAwait(false);

                if (exitCode == 0 && File.Exists(destination) && new FileInfo(destination).Length > 0)
                    return destination;

                try
                {
                    if (File.Exists(destination))
                        File.Delete(destination);
                }
                catch
                {
                    // best effort
                }

                return null;
            }
            catch
            {
                // A missing or unrunnable binary, or any other failure, degrades to the wav.
                return null;
            }
        }

        /// <summary>
        /// Preview point (~40% through the song, else the first line) and a lead-in when the first
        /// line starts within 2s. Parses the timing.json defensively; any problem -> sane defaults.
        /// A map with no lines at all (a blank import) gets no preview point and no lead-in: there
        /// is no first line to lead into, and -1 is the format's "unset" preview.
        /// </summary>
        private static (double PreviewTime, double AudioLeadIn) computePolish(string timingJson)
        {
            const double lead_in_threshold_ms = 2000;

            double firstLineStart = 0;
            bool anyLine = false;
            double? songEndMs = null;

            try
            {
                using var doc = JsonDocument.Parse(timingJson);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("song_end_ms", out JsonElement end) && end.ValueKind == JsonValueKind.Number)
                    songEndMs = end.GetDouble();

                if (root.TryGetProperty("lines", out JsonElement lines) && lines.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement line in lines.EnumerateArray())
                    {
                        if (line.ValueKind == JsonValueKind.Object
                            && line.TryGetProperty("start_ms", out JsonElement start)
                            && start.ValueKind == JsonValueKind.Number)
                        {
                            firstLineStart = start.GetDouble();
                            anyLine = true;
                            break;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Defaults below.
            }

            if (!anyLine)
                return (-1, 0);

            double previewTime = songEndMs is > 0 ? songEndMs.Value * 0.4 : firstLineStart;
            double audioLeadIn = firstLineStart < lead_in_threshold_ms ? lead_in_threshold_ms : 0;
            return (previewTime, audioLeadIn);
        }
    }
}
