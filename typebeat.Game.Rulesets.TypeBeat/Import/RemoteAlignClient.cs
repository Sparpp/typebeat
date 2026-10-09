// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using osu.Framework.IO.Network;
using WebRequest = osu.Framework.IO.Network.WebRequest;
using osu.Framework.Logging;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>Outcome of a server alignment attempt (see <see cref="RemoteAligner"/>).</summary>
    public readonly record struct RemoteAlignOutcome(bool Success, string? TimingJson, string? Error)
    {
        public static RemoteAlignOutcome Ok(string timingJson) => new RemoteAlignOutcome(true, timingJson, null);
        public static RemoteAlignOutcome Fail(string error) => new RemoteAlignOutcome(false, null, error);
    }

    /// <summary>
    /// What one server alignment job is asked to do: the multipart form of
    /// <c>POST /api/v2/typebeat/server-align</c>, minus the file's extension, which is read off
    /// <see cref="AudioPath"/>. <see cref="Language"/> is the canonical name the local aligner would
    /// get as <c>--lyrics-language</c> (empty when there is none), and <see cref="VocalMode"/> is
    /// <c>"aligned"</c> or <c>"estimated"</c>; the server decides the anchors itself, exactly as
    /// <see cref="LyricMapImporter.AlignerAnchorMode"/> does here.
    /// </summary>
    public sealed record RemoteAlignRequest(string AudioPath, string LyricsContent, string Artist, string Title, string Language, string VocalMode);

    /// <summary>
    /// The seam <see cref="LyricMapImporter.BuildOszAsync"/> calls INSTEAD of the local aligner when
    /// the import opted in to the server aligner (backlog 413); in the game it's
    /// <see cref="RemoteAlignClient.AlignAsync"/>, in tests a stub. Cancelling the token withdraws the
    /// job and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public delegate Task<RemoteAlignOutcome> RemoteAligner(RemoteAlignRequest request, Action<string> progress, CancellationToken token);

    /// <summary>
    /// Client for the server aligner's job API (typebeat-web <c>/api/v2/typebeat/server-align</c>,
    /// backlog 413): uploads the audio and lyrics, then polls the job every two seconds until the
    /// server's worker produces timing.json, reporting the queue position while it waits and the
    /// worker's own last log line while it runs. An opt-in from the import screen for a signed-in
    /// player who cannot run the local aligner; it never runs on its own.
    ///
    /// <para>Every refusal the server gives (a validation failure, one job already running, the daily
    /// cap, a full queue) and every failed job comes back as the server's OWN message, verbatim: the
    /// server knows why, and the player is told that rather than a paraphrase. The pre-287 client's
    /// route (<c>typebeat/align</c>) is deliberately not used: the server keeps answering it with a
    /// 410 for the builds still in the wild.</para>
    /// </summary>
    public static class RemoteAlignClient
    {
        /// <summary>How often a job is polled.</summary>
        public static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long a job may RUN before the client gives up on it (withdrawing it as it goes). The
        /// queue wait is not counted: the server bounds its own queue and the player can leave at any
        /// time, whereas a running job past the server's own timeout means something is wrong.
        /// </summary>
        public static readonly TimeSpan RUNNING_TIMEOUT = TimeSpan.FromMinutes(25);

        /// <summary>Consecutive failed polls tolerated as network blips before the wait is abandoned.</summary>
        public const int MAX_CONSECUTIVE_POLL_FAILURES = 5;

        /// <summary>The server's upload cap, checked here so a doomed upload never leaves the machine.</summary>
        public const long MAX_AUDIO_BYTES = 64 * 1024 * 1024;

        public const string SIGN_IN_REQUIRED = "sign in to type!beat to use the server aligner";

        public static async Task<RemoteAlignOutcome> AlignAsync(IAPIProvider api, RemoteAlignRequest request, Action<string> progress, CancellationToken token,
                                                                TimeSpan? pollInterval = null)
        {
            if (!api.IsLoggedIn)
                return RemoteAlignOutcome.Fail(SIGN_IN_REQUIRED);

            var audioInfo = new FileInfo(request.AudioPath);

            if (!audioInfo.Exists)
                return RemoteAlignOutcome.Fail($"audio file not found: {request.AudioPath}");

            if (audioInfo.Length > MAX_AUDIO_BYTES)
                return RemoteAlignOutcome.Fail("the audio file is too large for the server aligner (64 MB max)");

            token.ThrowIfCancellationRequested();

            progress(ImportProgressParser.SERVER_UPLOADING);

            var create = new CreateServerAlignJobRequest(request);
            int lastPercent = -1;

            create.Progressed += (current, total) =>
            {
                int percent = total > 0 ? (int)(current * 100 / total) : 0;

                if (percent == lastPercent)
                    return;

                lastPercent = percent;
                progress($"{ImportProgressParser.SERVER_UPLOADING} {percent}%");
            };

            using (token.Register(create.Cancel))
                await api.PerformAsync(create).ConfigureAwait(false);

            ServerAlignJob? created = create.Response;

            if (token.IsCancellationRequested)
            {
                if (created?.Id is { Length: > 0 } orphan)
                    await tryCancelServerJobAsync(api, orphan).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
            }

            if (created?.Id is not { Length: > 0 } jobId)
                return RemoteAlignOutcome.Fail(create.FailureException?.Message ?? "the server did not accept the alignment job");

            Logger.Log($"Server aligner accepted job {jobId} (aligner version {created.AlignerVersion ?? "unknown"}, queue position {created.QueuePosition?.ToString() ?? "none"})");

            try
            {
                return await pollAsync(api, jobId, created, progress, token, pollInterval ?? POLL_INTERVAL).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The player left the import screen mid-alignment. Best-effort: tell the server to drop
                // the job so its single worker stops burning minutes on a result no one will collect
                // and the player's one-active-job slot frees now. Then honour the cancellation.
                await tryCancelServerJobAsync(api, jobId).ConfigureAwait(false);
                throw;
            }
        }

        private static async Task<RemoteAlignOutcome> pollAsync(IAPIProvider api, string jobId, ServerAlignJob created, Action<string> progress, CancellationToken token, TimeSpan interval)
        {
            int? lastPosition = null;
            bool reportedPending = false;
            string? lastProgress = null;
            bool reportedRunning = false;
            DateTimeOffset? runningSince = null;
            int consecutiveFailures = 0;

            reportPending(created.QueuePosition);

            while (true)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);

                var poll = new GetServerAlignJobRequest(jobId);
                await api.PerformAsync(poll).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                ServerAlignJob? status = poll.Response;

                if (status == null)
                {
                    if (isNotFound(poll.FailureException))
                        return RemoteAlignOutcome.Fail("the server no longer has this alignment job");

                    // Transient network blip tolerance; sustained failure ends the wait.
                    if (++consecutiveFailures >= MAX_CONSECUTIVE_POLL_FAILURES)
                    {
                        await tryCancelServerJobAsync(api, jobId).ConfigureAwait(false);
                        return RemoteAlignOutcome.Fail($"lost contact with the server aligner ({poll.FailureException?.Message ?? "no response"})");
                    }

                    continue;
                }

                consecutiveFailures = 0;

                switch (status.State)
                {
                    case ServerAlignJob.STATE_DONE when status.TimingJson is { Length: > 0 } timing:
                        return RemoteAlignOutcome.Ok(timing);

                    case ServerAlignJob.STATE_DONE:
                        return RemoteAlignOutcome.Fail("the server aligner returned an empty result");

                    case ServerAlignJob.STATE_FAILED:
                        return RemoteAlignOutcome.Fail(string.IsNullOrWhiteSpace(status.Error) ? "the server aligner failed" : status.Error);

                    case ServerAlignJob.STATE_CANCELLED:
                        return RemoteAlignOutcome.Fail("the server cancelled the alignment job");

                    case ServerAlignJob.STATE_PENDING:
                        reportPending(status.QueuePosition);
                        break;

                    default:
                        runningSince ??= DateTimeOffset.UtcNow;

                        if (DateTimeOffset.UtcNow - runningSince > RUNNING_TIMEOUT)
                        {
                            await tryCancelServerJobAsync(api, jobId).ConfigureAwait(false);
                            return RemoteAlignOutcome.Fail("the server aligner timed out");
                        }

                        if (!reportedRunning || (status.Progress is { Length: > 0 } line && line != lastProgress))
                        {
                            reportedRunning = true;
                            lastProgress = status.Progress;
                            progress(ImportProgressParser.ServerProgressLine(status.Progress));
                        }

                        break;
                }
            }

            void reportPending(int? position)
            {
                if (reportedPending && lastPosition == position)
                    return;

                reportedPending = true;
                lastPosition = position;
                progress(ImportProgressParser.ServerQueueLine(position));
            }
        }

        private static bool isNotFound(Exception? failure)
            => failure is APIException { StatusCode: HttpStatusCode.NotFound }
               || (failure is WebException webException && webException.Message == nameof(HttpStatusCode.NotFound));

        private static async Task tryCancelServerJobAsync(IAPIProvider api, string jobId)
        {
            try
            {
                // Deliberately un-tokened: this cleanup runs precisely because the import token was
                // cancelled (or the wait was abandoned).
                await api.PerformAsync(new CancelServerAlignJobRequest(jobId)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: if it doesn't land, the server still abandons the job after its window.
            }
        }
    }

    /// <summary>
    /// One server alignment job as every route of <c>/api/v2/typebeat/server-align</c> returns it
    /// (backlog 413's wire contract). <see cref="QueuePosition"/> is 1-based and only set while the
    /// job is pending; <see cref="TimingJson"/> is the aligner's timing.json TEXT, only when done;
    /// <see cref="Error"/> only when failed.
    /// </summary>
    public class ServerAlignJob
    {
        public const string STATE_PENDING = "pending";
        public const string STATE_RUNNING = "running";
        public const string STATE_DONE = "done";
        public const string STATE_FAILED = "failed";
        public const string STATE_CANCELLED = "cancelled";

        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("state")]
        public string State { get; set; } = string.Empty;

        [JsonProperty("queue_position")]
        public int? QueuePosition { get; set; }

        [JsonProperty("progress")]
        public string? Progress { get; set; }

        [JsonProperty("timing_json")]
        public string? TimingJson { get; set; }

        [JsonProperty("error")]
        public string? Error { get; set; }

        [JsonProperty("aligner_version")]
        public string? AlignerVersion { get; set; }
    }

    /// <summary>
    /// Uploads the audio and lyrics and returns the created job. Built on <see cref="APIUploadRequest"/>
    /// (a plain <c>OsuWebRequest</c> that carries a multipart body) rather than
    /// <see cref="APIRequest{T}"/>, whose <c>OsuJsonWebRequest&lt;T&gt;</c> is built to POST a JSON body
    /// and cannot also carry a file; the small JSON reply is parsed by hand in <see cref="PostProcess"/>.
    /// </summary>
    public class CreateServerAlignJobRequest : APIUploadRequest
    {
        public const string TARGET = @"typebeat/server-align";

        private readonly RemoteAlignRequest request;

        public CreateServerAlignJobRequest(RemoteAlignRequest request)
        {
            this.request = request;
        }

        public ServerAlignJob? Response { get; private set; }

        protected override string Target => TARGET;

        protected override WebRequest CreateWebRequest()
        {
            var web = base.CreateWebRequest();
            web.Method = HttpMethod.Post;
            web.AddFile(@"audio", File.ReadAllBytes(request.AudioPath));
            // The multipart part carries no usable filename; the server keys the format off this.
            web.AddParameter(@"extension", Path.GetExtension(request.AudioPath));
            web.AddParameter(@"lyrics", request.LyricsContent);
            web.AddParameter(@"artist", request.Artist);
            web.AddParameter(@"title", request.Title);
            web.AddParameter(@"language", request.Language);
            web.AddParameter(@"vocal_mode", request.VocalMode);
            web.Timeout = 600_000;
            return web;
        }

        protected override void PostProcess()
        {
            base.PostProcess();

            string? body = WebRequest?.GetResponseString();

            if (!string.IsNullOrEmpty(body))
                Response = JsonConvert.DeserializeObject<ServerAlignJob>(body);
        }
    }

    public class GetServerAlignJobRequest : APIRequest<ServerAlignJob>
    {
        private readonly string jobId;

        public GetServerAlignJobRequest(string jobId)
        {
            this.jobId = jobId;
        }

        protected override string Target => $@"{CreateServerAlignJobRequest.TARGET}/{jobId}";
    }

    /// <summary>
    /// Withdraws a job (DELETE), sent when the client abandons the wait (the player left the import
    /// screen) so the worker stops aligning a result no one will collect. Idempotent on the server;
    /// the reply is irrelevant to the caller (best-effort cleanup).
    /// </summary>
    public class CancelServerAlignJobRequest : APIRequest
    {
        private readonly string jobId;

        public CancelServerAlignJobRequest(string jobId)
        {
            this.jobId = jobId;
        }

        protected override string Target => $@"{CreateServerAlignJobRequest.TARGET}/{jobId}";

        protected override WebRequest CreateWebRequest()
        {
            var request = base.CreateWebRequest();
            request.Method = HttpMethod.Delete;
            return request;
        }
    }
}
