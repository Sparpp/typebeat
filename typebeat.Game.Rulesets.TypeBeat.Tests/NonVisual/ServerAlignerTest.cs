// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Online.API;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Screens.ImportLyrics;
using AlignerVocalMode = typebeat.Game.Beatmaps.AlignerVocalMode;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 413, client half: the server aligner opt-in. The client is driven against a loopback
    /// stand-in for the server (the same pattern as backlog 406's <c>GuestReadsTest</c>), speaking the
    /// wire contract of <c>/api/v2/typebeat/server-align</c>; the import ladder is driven with a stub
    /// <see cref="RemoteAligner"/>.
    /// </summary>
    [TestFixture]
    public class ServerAlignerTest
    {
        private const string timing_json =
            "{\"version\":2,\"song_end_ms\":9000,\"lines\":[{\"text\":\"hello world\",\"start_ms\":1000,\"end_ms\":3000,\"words\":["
            + "{\"text\":\"hello\",\"start_ms\":1000,\"end_ms\":2000},{\"text\":\"world\",\"start_ms\":2000,\"end_ms\":3000}]}]}";

        private static readonly TimeSpan fast_poll = TimeSpan.FromMilliseconds(20);

        private string tempRoot = null!;
        private string audioPath = null!;

        [SetUp]
        public void SetUp()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "tb_server_align_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            audioPath = Path.Combine(tempRoot, "Some Artist - A Song.mp3");
            File.WriteAllBytes(audioPath, new byte[] { 0x49, 0x44, 0x33, 1, 2, 3, 4, 5 });
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(tempRoot, true);
            }
            catch
            {
                // best effort
            }
        }

        private RemoteAlignRequest request(string language = "english", string vocalMode = "aligned")
            => new RemoteAlignRequest(audioPath, "[00:01.00]hello world\n", "Some Artist", "A Song", language, vocalMode);

        private static DummyAPIAccess signedInAgainst(StubServer server)
        {
            var api = new DummyAPIAccess();
            api.Endpoints.APIUrl = server.Root;
            api.HandleRequest = r =>
            {
                r.Perform();
                return true;
            };
            return api;
        }

        private static string job(string state, int? position = null, string? progress = null, string? timing = null, string? error = null)
            => Newtonsoft.Json.JsonConvert.SerializeObject(new Dictionary<string, object?>
            {
                ["id"] = "job1",
                ["state"] = state,
                ["queue_position"] = position,
                ["progress"] = progress,
                ["timing_json"] = timing,
                ["error"] = error,
                ["aligner_version"] = "10",
            });

        [Test]
        public async Task AJobIsUploadedQueuedRunAndCollected()
        {
            int polls = 0;

            using var server = new StubServer((method, path) =>
            {
                if (method == "POST")
                    return (200, job("pending", position: 2));

                return Interlocked.Increment(ref polls) switch
                {
                    1 => (200, job("pending", position: 2)),
                    2 => (200, job("pending", position: 1)),
                    3 => (200, job("running", progress: "[12:34:57] separation: running demucs (htdemucs) on cpu ...")),
                    4 => (200, job("running", progress: "[12:35:41] emissions: chunk 7/31 frames=1490")),
                    _ => (200, job("done", timing: timing_json)),
                };
            });

            var lines = new List<string>();
            var outcome = await RemoteAlignClient.AlignAsync(signedInAgainst(server), request(vocalMode: "estimated"), lines.Add, CancellationToken.None, fast_poll).ConfigureAwait(false);

            Assert.That(outcome.Success, Is.True, outcome.Error);
            Assert.That(outcome.TimingJson, Is.EqualTo(timing_json));

            var post = server.Requests.First();

            Assert.Multiple(() =>
            {
                Assert.That(post.Method, Is.EqualTo("POST"));
                Assert.That(post.Path, Is.EqualTo("/api/v2/typebeat/server-align"));
                Assert.That(post.Authorization, Is.EqualTo("Bearer token"), "every server-align route needs the bearer");

                foreach (string field in new[] { "audio", "extension", "lyrics", "artist", "title", "language", "vocal_mode" })
                    Assert.That(post.Body, Does.Contain($"name={field}").Or.Contain($"name=\"{field}\""), $"multipart field {field}");

                Assert.That(post.Body, Does.Contain(".mp3"));
                Assert.That(post.Body, Does.Contain("english"));
                Assert.That(post.Body, Does.Contain("estimated"));

                Assert.That(server.Requests.Skip(1).Select(r => (r.Method, r.Path)), Is.All.EqualTo(("GET", "/api/v2/typebeat/server-align/job1")));

                // The queue position is reported once per change, then the server's own log lines are
                // relayed under the server prefix so they drive one row, not the local stages.
                Assert.That(lines, Is.EqualTo(new[]
                {
                    ImportProgressParser.SERVER_UPLOADING,
                    ImportProgressParser.ServerQueueLine(2),
                    ImportProgressParser.ServerQueueLine(1),
                    ImportProgressParser.ServerProgressLine("[12:34:57] separation: running demucs (htdemucs) on cpu ..."),
                    ImportProgressParser.ServerProgressLine("[12:35:41] emissions: chunk 7/31 frames=1490"),
                }));
            });
        }

        /// <summary>Every refusal (validation, one active job, the daily cap, a full queue) reaches the player as the server's own words.</summary>
        [TestCase(422, "the audio file is too large (64 MB max)")]
        [TestCase(409, "you already have an alignment job running (job abc); wait for it or cancel it")]
        [TestCase(429, "you have used today's 10 server alignments; the count resets at 00:00 UTC")]
        [TestCase(503, "the server aligner queue is full, try again later or use the local aligner")]
        public async Task ARefusalIsSurfacedVerbatim(int status, string message)
        {
            using var server = new StubServer((_, _) => (status, Newtonsoft.Json.JsonConvert.SerializeObject(new { error = message })));

            var outcome = await RemoteAlignClient.AlignAsync(signedInAgainst(server), request(), _ => { }, CancellationToken.None, fast_poll).ConfigureAwait(false);

            Assert.That(outcome.Success, Is.False);
            Assert.That(outcome.Error, Is.EqualTo(message));
            Assert.That(server.Requests.Select(r => r.Method), Is.EqualTo(new[] { "POST" }), "a refused job is never polled");
        }

        [Test]
        public async Task AFailedJobIsSurfacedVerbatim()
        {
            const string error = "aligner exited with code 1: RuntimeError: no vocals were found in the audio";

            using var server = new StubServer((method, _) => method == "POST" ? (200, job("pending", position: 1)) : (200, job("failed", error: error)));

            var outcome = await RemoteAlignClient.AlignAsync(signedInAgainst(server), request(), _ => { }, CancellationToken.None, fast_poll).ConfigureAwait(false);

            Assert.That(outcome.Success, Is.False);
            Assert.That(outcome.Error, Is.EqualTo(error));
        }

        [Test]
        public async Task AJobTheServerNoLongerHasEndsTheWait()
        {
            using var server = new StubServer((method, _) => method == "POST" ? (200, job("pending", position: 1)) : (404, "{\"error\":\"not found\"}"));

            var outcome = await RemoteAlignClient.AlignAsync(signedInAgainst(server), request(), _ => { }, CancellationToken.None, fast_poll).ConfigureAwait(false);

            Assert.That(outcome.Success, Is.False);
            Assert.That(outcome.Error, Does.Contain("no longer has"));
            Assert.That(server.Requests.Count(r => r.Method == "GET"), Is.EqualTo(1), "a 404 is a verdict, not a blip to retry");
        }

        /// <summary>Leaving the import screen cancels the token: the job is withdrawn with a DELETE and the cancellation is honoured.</summary>
        [Test]
        public void CancellingWithdrawsTheJob()
        {
            using var cancellation = new CancellationTokenSource();

            using var server = new StubServer((method, _) =>
            {
                if (method == "GET")
                    cancellation.Cancel();

                return method == "DELETE" ? (200, job("cancelled")) : (200, job("pending", position: 3));
            });

            Assert.That(async () => await RemoteAlignClient.AlignAsync(signedInAgainst(server), request(), _ => { }, cancellation.Token, fast_poll).ConfigureAwait(false),
                Throws.InstanceOf<OperationCanceledException>());

            Assert.That(server.Requests.Last(), Is.EqualTo(server.Requests.Single(r => r.Method == "DELETE")));
            Assert.That(server.Requests.Last().Path, Is.EqualTo("/api/v2/typebeat/server-align/job1"));
        }

        [Test]
        public async Task AGuestIsToldToSignInAndNothingIsSent()
        {
            using var server = new StubServer((_, _) => (200, job("pending", position: 1)));

            var api = signedInAgainst(server);
            api.SetState(APIState.Offline);

            var outcome = await RemoteAlignClient.AlignAsync(api, request(), _ => { }, CancellationToken.None, fast_poll).ConfigureAwait(false);

            Assert.That(outcome.Error, Is.EqualTo(RemoteAlignClient.SIGN_IN_REQUIRED));
            Assert.That(server.Requests, Is.Empty);

            outcome = await LyricMapImportService.ServerAligner(null)(request(), _ => { }, CancellationToken.None).ConfigureAwait(false);
            Assert.That(outcome.Error, Is.EqualTo(RemoteAlignClient.SIGN_IN_REQUIRED), "no API session is a sign-in failure, never a quiet local run");
        }

        // ---------------------------------------------------------------------------------------------
        // The import ladder
        // ---------------------------------------------------------------------------------------------

        private async Task<(LyricImportResult Result, List<RemoteAlignRequest> Sent)> importWith(RemoteAligner? remote, string lyrics, bool automatic = true,
                                                                                            AlignerVocalMode vocalMode = AlignerVocalMode.Aligned)
        {
            string lyricsPath = Path.Combine(tempRoot, "lyrics.lrc");
            File.WriteAllText(lyricsPath, lyrics);

            var sent = new List<RemoteAlignRequest>();
            RemoteAligner? recording = remote == null
                ? null
                : (req, progress, token) =>
                {
                    sent.Add(req);
                    return remote(req, progress, token);
                };

            var result = await LyricMapImporter.BuildOszAsync(audioPath, lyricsPath, "Some Artist", "A Song", null, new[] { tempRoot }, _ => { }, CancellationToken.None,
                useAutomaticAlignment: automatic, language: "english", vocalMode: vocalMode, remoteAlign: recording).ConfigureAwait(false);

            return (result, sent);
        }

        /// <summary>
        /// Ticked, the server is THE aligner: its timing is what the map is built from, even for
        /// line-stamped lyrics that the line-stamp rung could have timed on its own.
        /// </summary>
        [Test]
        public async Task TheLadderChoosesTheServerWhenTicked()
        {
            var (result, sent) = await importWith((_, _, _) => Task.FromResult(RemoteAlignOutcome.Ok(timing_json)), "[00:01.00]hello world\n[00:05.00]\n",
                vocalMode: AlignerVocalMode.Estimated).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(sent, Has.Count.EqualTo(1));
            Assert.That(sent[0].Language, Is.EqualTo("english"));
            Assert.That(sent[0].VocalMode, Is.EqualTo("estimated"));

            using var archive = ZipFile.OpenRead(result.OszPath!);
            string packaged = new StreamReader(archive.Entries.Single(e => e.FullName == "timing.json").Open()).ReadToEnd();

            Assert.That(packaged, Does.Contain("\"start_ms\": 2000").Or.Contain("\"start_ms\":2000"), "the server's word timing, not the line stamps'");
        }

        /// <summary>A failed server job fails the import with the server's reason: no silent drop to the line stamps.</summary>
        [Test]
        public async Task AFailedServerJobIsReportedNotFallenBackFrom()
        {
            const string error = "the server aligner queue is full, try again later or use the local aligner";

            var (result, sent) = await importWith((_, _, _) => Task.FromResult(RemoteAlignOutcome.Fail(error)), "[00:01.00]hello world\n[00:05.00]\n").ConfigureAwait(false);

            Assert.That(sent, Has.Count.EqualTo(1));
            Assert.That(result.Success, Is.False, "the stamped lyrics must not quietly time the map instead");
            Assert.That(result.Error, Is.EqualTo(error));
        }

        [Test]
        public async Task CancellingAServerImportReadsAsCancelled()
        {
            using var cancellation = new CancellationTokenSource();
            string lyricsPath = Path.Combine(tempRoot, "bare.txt");
            File.WriteAllText(lyricsPath, "hello world\n");

            var cancelled = await LyricMapImporter.BuildOszAsync(audioPath, lyricsPath, "A", "B", null, new[] { tempRoot }, _ => { }, cancellation.Token,
                useAutomaticAlignment: true, language: "english", remoteAlign: (_, _, token) =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(RemoteAlignOutcome.Ok(timing_json));
                }).ConfigureAwait(false);

            Assert.That(cancelled.Error, Is.EqualTo("import cancelled"));
        }

        /// <summary>Unticked automatic alignment means no aligner of either kind; the server is never asked.</summary>
        [Test]
        public async Task AutomaticAlignmentOffNeverAsksTheServer()
        {
            var (result, sent) = await importWith((_, _, _) => Task.FromResult(RemoteAlignOutcome.Ok(timing_json)), "[00:01.00]hello world\n[00:05.00]\n", automatic: false).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(sent, Is.Empty);
        }

        /// <summary>Estimated vocals are asked of the server on the local rule's terms: only with a stamp to pace from.</summary>
        [Test]
        public void TheServerRequestMirrorsTheLocalCommandLine()
        {
            var stamped = LyricMapImporter.ServerAlignRequest(audioPath, "[00:01.00]a\n", "A", "T", "English", AlignerVocalMode.Estimated);
            var bare = LyricMapImporter.ServerAlignRequest(audioPath, "a\n", "A", "T", null, AlignerVocalMode.Estimated);

            Assert.Multiple(() =>
            {
                Assert.That(stamped.VocalMode, Is.EqualTo("estimated"));
                Assert.That(stamped.Language, Is.EqualTo("english"));
                Assert.That(bare.VocalMode, Is.EqualTo("aligned"));
                Assert.That(bare.Language, Is.Empty);
            });
        }

        /// <summary>
        /// The remembered choice defaults OFF (an opt-in), and the hidden local-aligner switch it
        /// replaces is gone for good: no member, so nothing can read a stale stored opt-out.
        /// </summary>
        [Test]
        public void TheServerChoiceDefaultsOffAndTheLocalSwitchIsGone()
        {
            using (var config = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo))
            {
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.UseServerAligner), Is.False);

                config.SetValue(TypeBeatRulesetSetting.UseServerAligner, true);
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.UseServerAligner), Is.True);
            }

            Assert.That(Enum.GetNames<TypeBeatRulesetSetting>(), Does.Not.Contain("LocalAlignerEnabled"));
        }

        /// <summary>
        /// A minimal HTTP/1.1 server on a loopback port: answers each request through
        /// <c>respond</c> and records its method, path, Authorization header and body.
        /// </summary>
        private sealed class StubServer : IDisposable
        {
            public readonly record struct Seen(string Method, string Path, string? Authorization, string Body);

            private readonly Func<string, string, (int Status, string Body)> respond;
            private readonly TcpListener listener;
            private readonly ConcurrentQueue<Seen> requests = new ConcurrentQueue<Seen>();
            private volatile bool disposed;

            public string Root { get; }

            public IReadOnlyList<Seen> Requests => requests.ToArray();

            public StubServer(Func<string, string, (int Status, string Body)> respond)
            {
                this.respond = respond;
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Root = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

                new Thread(serve) { IsBackground = true }.Start();
            }

            private void serve()
            {
                while (!disposed)
                {
                    TcpClient client;

                    try
                    {
                        client = listener.AcceptTcpClient();
                    }
                    catch
                    {
                        return;
                    }

                    try
                    {
                        using (client)
                        using (var stream = client.GetStream())
                            handle(stream);
                    }
                    catch
                    {
                        // A client that hung up mid-request (a cancelled upload) is not the test's concern.
                    }
                }
            }

            private void handle(NetworkStream stream)
            {
                string head = readHead(stream);
                string[] headLines = head.Split("\r\n");
                string[] requestLine = headLines[0].Split(' ');
                string? authorization = null;
                int contentLength = 0;
                bool chunked = false;

                foreach (string header in headLines.Skip(1))
                {
                    int colon = header.IndexOf(':');

                    if (colon < 0)
                        continue;

                    string name = header[..colon].Trim();
                    string value = header[(colon + 1)..].Trim();

                    if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                        authorization = value;
                    else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(value);
                    else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                        chunked = value.Contains("chunked", StringComparison.OrdinalIgnoreCase);
                }

                byte[] body = chunked ? readChunked(stream) : readExactly(stream, contentLength);
                string path = requestLine[1];
                int query = path.IndexOf('?');

                if (query >= 0)
                    path = path[..query];

                requests.Enqueue(new Seen(requestLine[0], path, authorization, Encoding.UTF8.GetString(body)));

                (int status, string reply) = respond(requestLine[0], path);
                byte[] replyBytes = Encoding.UTF8.GetBytes(reply);
                byte[] replyHead = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(HttpStatusCode)status}\r\nContent-Type: application/json\r\nContent-Length: {replyBytes.Length}\r\nConnection: close\r\n\r\n");

                stream.Write(replyHead);
                stream.Write(replyBytes);
                stream.Flush();
            }

            private static string readHead(NetworkStream stream)
            {
                var head = new List<byte>();

                while (true)
                {
                    int b = stream.ReadByte();

                    if (b < 0)
                        throw new EndOfStreamException();

                    head.Add((byte)b);

                    if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
                        return Encoding.ASCII.GetString(head.ToArray(), 0, head.Count - 4);
                }
            }

            private static byte[] readExactly(NetworkStream stream, int length)
            {
                byte[] buffer = new byte[length];
                int read = 0;

                while (read < length)
                {
                    int n = stream.Read(buffer, read, length - read);

                    if (n <= 0)
                        throw new EndOfStreamException();

                    read += n;
                }

                return buffer;
            }

            private static byte[] readChunked(NetworkStream stream)
            {
                var body = new MemoryStream();

                while (true)
                {
                    string sizeLine = readLine(stream);
                    int size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);

                    if (size == 0)
                    {
                        readLine(stream);
                        return body.ToArray();
                    }

                    body.Write(readExactly(stream, size));
                    readLine(stream);
                }
            }

            private static string readLine(NetworkStream stream)
            {
                var line = new StringBuilder();

                while (true)
                {
                    int b = stream.ReadByte();

                    if (b < 0)
                        throw new EndOfStreamException();

                    if (b == '\n')
                        return line.ToString().TrimEnd('\r');

                    line.Append((char)b);
                }
            }

            public void Dispose()
            {
                disposed = true;
                listener.Stop();
            }
        }

        [Test]
        public void StubServerSelfCheck()
        {
            // Guards the stand-in itself: a request it cannot parse would make every test above pass
            // or fail for the wrong reason.
            using var server = new StubServer((_, _) => (200, "{}"));
            using var http = new System.Net.Http.HttpClient();

            var watch = Stopwatch.StartNew();
            var reply = http.PostAsync(server.Root + "/x", new System.Net.Http.StringContent("hello")).GetAwaiter().GetResult();

            Assert.That(reply.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(server.Requests.Single().Body, Is.EqualTo("hello"));
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
        }
    }
}
