// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>
    /// Pulls the audio track of a video container out to a standalone audio file, so an imported
    /// mp4 becomes TWO files (the map's audio plus the map's background video) instead of one file
    /// doing both jobs. Kept behind an interface because the only real extractor is an external
    /// ffmpeg binary that most machines do not have: packaging stays unit-testable with text files,
    /// and the "no extractor" degrade is a pin of its own rather than an accident.
    /// </summary>
    public interface IAudioTrackExtractor
    {
        /// <summary>
        /// Writes the container's audio track into <paramref name="outputDirectory"/> and returns
        /// the path. NEVER throws for an absent or failing extractor: that is
        /// <see cref="AudioExtractionResult.Unavailable"/>, and the caller degrades to using the
        /// container itself as the audio (which is what the importer did before the split existed).
        /// </summary>
        Task<AudioExtractionResult> ExtractAsync(string videoPath, string outputDirectory, Action<string> progress, CancellationToken token);
    }

    /// <summary>
    /// Outcome of <see cref="IAudioTrackExtractor.ExtractAsync"/>: the extracted file, or the
    /// human-readable reason there is none (shown in the import progress stream, never an error).
    /// </summary>
    public readonly record struct AudioExtractionResult(string? AudioPath, string? Reason)
    {
        public bool Success => AudioPath != null;

        public static AudioExtractionResult Ok(string audioPath) => new AudioExtractionResult(audioPath, null);

        public static AudioExtractionResult Unavailable(string reason) => new AudioExtractionResult(null, reason);
    }
}
