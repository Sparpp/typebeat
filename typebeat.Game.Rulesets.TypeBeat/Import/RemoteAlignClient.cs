// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using osu.Framework.IO.Network;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>Outcome of a remote alignment attempt (see <see cref="RemoteAligner"/>).</summary>
    public readonly record struct RemoteAlignOutcome(bool Success, string? TimingJson, string? Error)
    {
        public static RemoteAlignOutcome Ok(string timingJson) => new RemoteAlignOutcome(true, timingJson, null);
        public static RemoteAlignOutcome Fail(string error) => new RemoteAlignOutcome(false, null, error);
    }

    /// <summary>
    /// The seam <see cref="LyricMapImporter.ProduceTimingJsonAsync"/> calls between the local
    /// aligner and the LRC fallback — in the game it's <see cref="RemoteAlignClient.AlignAsync"/>,
    /// in tests a stub.
    /// </summary>
    public delegate Task<RemoteAlignOutcome> RemoteAligner(
        string audioPath, string lyricsContent, string artist, string title,
        Action<string> progress, CancellationToken token);

    /// <summary>
    /// Client for the server-side alignment jobs API (typebeat-web /api/v2/typebeat/align):
    /// uploads the audio + lyrics, then polls the job until the worker produces timing.json.
    /// Used when the local lyriclab environment is absent (every installed build — only dev
    /// checkouts have Python/torch beside the game).
    /// </summary>
    public static class RemoteAlignClient
    {
        private static readonly TimeSpan poll_interval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan overall_timeout = TimeSpan.FromMinutes(20);
        private const int max_consecutive_poll_failures = 5;
        private const long max_audio_bytes = 64 * 1024 * 1024;

        public static async Task<RemoteAlignOutcome> AlignAsync(
            IAPIProvider api, string audioPath, string lyricsContent, string artist, string title,
            Action<string> progress, CancellationToken token)
        {
            if (api.State.Value != APIState.Online)
                return RemoteAlignOutcome.Fail("sign in to type!beat to use server-side alignment");

            var audioInfo = new FileInfo(audioPath);

            if (!audioInfo.Exists)
                return RemoteAlignOutcome.Fail($"audio file not found: {audioPath}");

            if (audioInfo.Length > max_audio_bytes)
                return RemoteAlignOutcome.Fail("the audio file is too large for server alignment (64 MB max)");

            progress("uploading to the type!beat server for alignment...");

            var create = new CreateAlignJobRequest(audioPath, lyricsContent, artist, title);
            string? createError = null;
            create.Failure += e => createError = e.Message;

            await api.PerformAsync(create).ConfigureAwait(false);

            if (create.Response?.Id is not { Length: > 0 } jobId)
                return RemoteAlignOutcome.Fail(createError ?? "the server did not accept the alignment job");

            progress("waiting for the server aligner (this can take a few minutes)...");

            string? lastProgress = null;
            int consecutiveFailures = 0;
            DateTimeOffset started = DateTimeOffset.UtcNow;

            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    if (DateTimeOffset.UtcNow - started > overall_timeout)
                        return RemoteAlignOutcome.Fail("server alignment timed out");

                    await Task.Delay(poll_interval, token).ConfigureAwait(false);

                    var poll = new GetAlignJobRequest(jobId);
                    await api.PerformAsync(poll).ConfigureAwait(false);

                    var status = poll.Response;

                    if (status == null)
                    {
                        // Transient network blip tolerance; sustained failure ends the wait.
                        if (++consecutiveFailures >= max_consecutive_poll_failures)
                            return RemoteAlignOutcome.Fail("lost contact with the server during alignment");

                        continue;
                    }

                    consecutiveFailures = 0;

                    switch (status.State)
                    {
                        case "done" when status.TimingJson is { Length: > 0 }:
                            return RemoteAlignOutcome.Ok(status.TimingJson);

                        case "done":
                            return RemoteAlignOutcome.Fail("the server returned an empty alignment result");

                        case "failed":
                            return RemoteAlignOutcome.Fail(status.Error ?? "server alignment failed");

                        default:
                            if (status.Progress is { Length: > 0 } line && line != lastProgress)
                            {
                                lastProgress = line;
                                progress(line);
                            }

                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The user left the import screen mid-alignment. Best-effort: tell the server to drop
                // the job so the single worker stops burning minutes of CPU on a result no one will
                // collect (and the user's one-active-job slot frees now instead of waiting out the
                // server's abandonment window). Then honour the cancellation.
                await tryCancelServerJobAsync(api, jobId).ConfigureAwait(false);
                throw;
            }
        }

        private static async Task tryCancelServerJobAsync(IAPIProvider api, string jobId)
        {
            try
            {
                // Deliberately un-tokened: this cleanup must run precisely because the import token
                // was cancelled. PerformAsync runs the request on its own long-running task.
                await api.PerformAsync(new CancelAlignJobRequest(jobId)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: if it doesn't land, the server still abandons the job after its window.
            }
        }
    }

    public class AlignJobWire
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("state")]
        public string State { get; set; } = string.Empty;

        [JsonProperty("progress")]
        public string? Progress { get; set; }

        [JsonProperty("timing_json")]
        public string? TimingJson { get; set; }

        [JsonProperty("error")]
        public string? Error { get; set; }
    }

    /// <summary>
    /// Uploads the audio + lyrics and returns the created job. Built on <see cref="APIUploadRequest"/>
    /// (a plain <c>OsuWebRequest</c> that carries a multipart body) rather than
    /// <see cref="APIRequest{T}"/> — the latter's <c>OsuJsonWebRequest&lt;T&gt;</c> is built to POST a
    /// JSON body and cannot also carry a file, so its typed <c>Response</c> never populated and the
    /// client abandoned every job the instant it was created. The small JSON body is read and parsed
    /// by hand in <see cref="PostProcess"/> — the codebase's convention for upload-with-response.
    /// </summary>
    public class CreateAlignJobRequest : APIUploadRequest
    {
        private readonly string audioPath;
        private readonly string lyricsContent;
        private readonly string artist;
        private readonly string title;

        public CreateAlignJobRequest(string audioPath, string lyricsContent, string artist, string title)
        {
            this.audioPath = audioPath;
            this.lyricsContent = lyricsContent;
            this.artist = artist;
            this.title = title;
        }

        public AlignJobWire? Response { get; private set; }

        protected override string Target => @"typebeat/align";

        protected override WebRequest CreateWebRequest()
        {
            var request = base.CreateWebRequest();
            request.Method = HttpMethod.Post;
            request.AddFile(@"audio", File.ReadAllBytes(audioPath));
            request.AddParameter(@"lyrics", lyricsContent);
            request.AddParameter(@"artist", artist);
            request.AddParameter(@"title", title);
            // The multipart part carries no usable filename — the server keys the format off this.
            request.AddParameter(@"extension", Path.GetExtension(audioPath));
            request.Timeout = 600_000;
            return request;
        }

        protected override void PostProcess()
        {
            base.PostProcess();

            string? body = WebRequest?.GetResponseString();

            if (!string.IsNullOrEmpty(body))
                Response = JsonConvert.DeserializeObject<AlignJobWire>(body);
        }
    }

    public class GetAlignJobRequest : APIRequest<AlignJobWire>
    {
        private readonly string jobId;

        public GetAlignJobRequest(string jobId)
        {
            this.jobId = jobId;
        }

        protected override string Target => $@"typebeat/align/{jobId}";
    }

    /// <summary>
    /// Asks the server to drop an in-flight alignment job (DELETE) — sent when the client abandons
    /// the wait (the user left the import screen) so the worker stops aligning a result no one will
    /// collect. Response-less: success/failure is irrelevant to the caller (best-effort cleanup).
    /// </summary>
    public class CancelAlignJobRequest : APIRequest
    {
        private readonly string jobId;

        public CancelAlignJobRequest(string jobId)
        {
            this.jobId = jobId;
        }

        protected override string Target => $@"typebeat/align/{jobId}";

        protected override WebRequest CreateWebRequest()
        {
            var request = base.CreateWebRequest();
            request.Method = HttpMethod.Delete;
            return request;
        }
    }
}
