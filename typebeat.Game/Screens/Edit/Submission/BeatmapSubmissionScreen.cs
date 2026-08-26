// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Development;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Framework.Threading;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Drawables.Cards;
using typebeat.Game.Configuration;
using typebeat.Game.Database;
using typebeat.Game.IO.Archives;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Screens.Select;
using osuTK;

namespace typebeat.Game.Screens.Edit.Submission
{
    public partial class BeatmapSubmissionScreen : OsuScreen
    {
        private BeatmapSubmissionOverlay overlay = null!;

        public override bool DisallowExternalBeatmapRulesetChanges => true;

        protected override bool InitialBackButtonVisibility => false;

        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Aquamarine);

        [Resolved]
        private RealmAccess realmAccess { get; set; } = null!;

        [Resolved]
        private Storage storage { get; set; } = null!;

        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        [Resolved]
        private OsuConfigManager configManager { get; set; } = null!;

        [Resolved]
        private OsuGame? game { get; set; }

        [Resolved]
        private BeatmapManager beatmaps { get; set; } = null!;

        [Cached]
        private BeatmapSubmissionSettings settings { get; } = new BeatmapSubmissionSettings();

        private Container submissionProgress = null!;
        private SubmissionStageProgress exportStep = null!;
        private SubmissionStageProgress createSetStep = null!;
        private SubmissionStageProgress uploadStep = null!;
        private SubmissionStageProgress updateStep = null!;
        private Container successContainer = null!;
        private Container flashLayer = null!;

        private uint? beatmapSetId;
        private MemoryStream? beatmapPackageStream;

        private ProgressNotification? exportProgressNotification;
        private ProgressNotification? updateProgressNotification;

        /// <summary>
        /// Builds a fresh request for one package-upload attempt. An <see cref="APIRequest"/> is single-use,
        /// so a retry has to construct a new one from the same in-memory inputs rather than requeue the old one.
        /// </summary>
        private Func<APIUploadRequest>? uploadRequestFactory;

        /// <summary>
        /// The request of the attempt currently in flight, used to gate completion handlers on request identity:
        /// <see cref="APIRequest.Cancel"/> runs the same failure path a real failure does, so a stale or
        /// cancelled attempt's handlers must not be allowed to touch the current one.
        /// </summary>
        private APIUploadRequest? activeUploadRequest;

        /// <summary>
        /// The same payload the single-request upload sends, assembled once per submission so the chunked
        /// fallback can send it in pieces without recomputing anything.
        /// </summary>
        private ChunkedPackageUpload.UploadPayload? uploadSessionPayload;

        /// <summary>
        /// The chunked upload session in flight, if the direct upload has already been fallen back from.
        /// Identity-gated for the same reason <see cref="activeUploadRequest"/> is.
        /// </summary>
        private ChunkedPackageUpload? chunkedUpload;

        /// <summary>
        /// The failure that ended the last chunked upload session, if one has been tried.
        /// </summary>
        /// <remarks>
        /// Kept because the direct ladder resumes after a chunked session fails, so the failure the user
        /// eventually sees comes from a DIRECT attempt while the diagnosis lives in this one: the chunked
        /// flow is the arm that knows how far the upload actually got.
        /// </remarks>
        private Exception? lastChunkedFailure;

        /// <summary>
        /// Chunked upload sessions already restarted for this submission, capped by
        /// <see cref="UploadRetryPolicy.MAX_CHUNKED_RESUMES"/>.
        /// </summary>
        private int chunkedResumes;

        private int uploadAttempt;
        private ScheduledDelegate? uploadRetryDelegate;
        private bool exiting;

        private Live<BeatmapSetInfo>? importedSet;

        private Sample completedSample = null!;

        [BackgroundDependencyLoader]
        private void load(AudioManager audio)
        {
            AddRangeInternal(new Drawable[]
            {
                overlay = new BeatmapSubmissionOverlay(),
                submissionProgress = new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    AutoSizeDuration = 400,
                    AutoSizeEasing = Easing.OutQuint,
                    Alpha = 0,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Width = 0.6f,
                    Masking = true,
                    CornerRadius = 10,
                    Children = new Drawable[]
                    {
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = colourProvider.Background5,
                        },
                        new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Padding = new MarginPadding(20),
                            Spacing = new Vector2(5),
                            Children = new Drawable[]
                            {
                                createSetStep = new SubmissionStageProgress
                                {
                                    StageDescription = BeatmapSubmissionStrings.Preparing,
                                    StageIndex = 0,
                                    Anchor = Anchor.TopCentre,
                                    Origin = Anchor.TopCentre,
                                },
                                exportStep = new SubmissionStageProgress
                                {
                                    StageDescription = BeatmapSubmissionStrings.Exporting,
                                    StageIndex = 1,
                                    Anchor = Anchor.TopCentre,
                                    Origin = Anchor.TopCentre,
                                },
                                uploadStep = new SubmissionStageProgress
                                {
                                    StageDescription = BeatmapSubmissionStrings.Uploading,
                                    StageIndex = 2,
                                    Anchor = Anchor.TopCentre,
                                    Origin = Anchor.TopCentre,
                                },
                                updateStep = new SubmissionStageProgress
                                {
                                    StageDescription = BeatmapSubmissionStrings.Finishing,
                                    StageIndex = 3,
                                    Anchor = Anchor.TopCentre,
                                    Origin = Anchor.TopCentre,
                                },
                                successContainer = new Container
                                {
                                    Padding = new MarginPadding(20),
                                    Anchor = Anchor.TopCentre,
                                    Origin = Anchor.TopCentre,
                                    AutoSizeAxes = Axes.Both,
                                    CornerRadius = BeatmapCard.CORNER_RADIUS,
                                    Child = flashLayer = new Container
                                    {
                                        RelativeSizeAxes = Axes.Both,
                                        Masking = true,
                                        CornerRadius = BeatmapCard.CORNER_RADIUS,
                                        Depth = float.MinValue,
                                        Alpha = 0,
                                        Child = new Box
                                        {
                                            RelativeSizeAxes = Axes.Both,
                                        }
                                    }
                                },
                            }
                        }
                    }
                }
            });

            overlay.State.BindValueChanged(_ =>
            {
                if (overlay.State.Value == Visibility.Hidden)
                {
                    if (!overlay.Completed)
                    {
                        allowExit();
                        this.Exit();
                    }
                    else
                    {
                        submissionProgress.FadeIn(200, Easing.OutQuint);
                        createBeatmapSet();
                    }
                }
            });

            completedSample = audio.Samples.Get(@"UI/bss-complete");

            if (Beatmap.Value.BeatmapSetInfo.OnlineID > 0)
            {
                var req = new GetBeatmapSetRequest(Beatmap.Value.BeatmapSetInfo.OnlineID);
                api.Queue(req);
                settings.LatestOnlineStateRequest = req;
            }
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            configManager.BindWith(OsuSetting.EditorSubmissionNotifyOnDiscussionReplies, settings.NotifyOnDiscussionReplies);
        }

        private void createBeatmapSet()
        {
            bool beatmapHasOnlineId = Beatmap.Value.BeatmapSetInfo.OnlineID > 0;

            PutBeatmapSetRequest createRequest;

            if (beatmapHasOnlineId)
            {
                createRequest = PutBeatmapSetRequest.UpdateExisting(
                    (uint)Beatmap.Value.BeatmapSetInfo.OnlineID,
                    Beatmap.Value.BeatmapSetInfo.Beatmaps.Where(b => b.OnlineID > 0).Select(b => (uint)b.OnlineID).ToArray(),
                    (uint)Beatmap.Value.BeatmapSetInfo.Beatmaps.Count(b => b.OnlineID <= 0),
                    settings);
                log($"Updating existing beatmap set (id:{createRequest.BeatmapSetID} beatmapsToKeep:[{string.Join(",", createRequest.BeatmapsToKeep)}] beatmapsToCreate:{createRequest.BeatmapsToCreate})");
            }
            else
            {
                createRequest = PutBeatmapSetRequest.CreateNew((uint)Beatmap.Value.BeatmapSetInfo.Beatmaps.Count, settings);
                log($"Creating new beatmap set (beatmapsToCreate:{createRequest.BeatmapsToCreate})");
            }

            createRequest.Success += async response =>
            {
                createSetStep.SetCompleted();
                beatmapSetId = response.BeatmapSetId;

                // at this point the set has an assigned online ID.
                // it's important to proactively store it to the realm database,
                // so that in the event in further failures in the process, the online ID is not lost.
                // losing it can incur creation of redundant new sets server-side, or even cause online ID confusion.
                if (!beatmapHasOnlineId)
                {
                    await realmAccess.WriteAsync(r =>
                    {
                        var refetchedSet = r.Find<BeatmapSetInfo>(Beatmap.Value.BeatmapSetInfo.ID);
                        refetchedSet!.OnlineID = (int)beatmapSetId.Value;
                    }).ConfigureAwait(true);
                }

                await createBeatmapPackage(response).ConfigureAwait(true);
            };
            createRequest.Failure += ex =>
            {
                createSetStep.SetFailed(ex.Message);
                log($"Beatmap set creation/update failed: {ex}");
                allowExit();
            };

            createSetStep.SetInProgress();
            api.Queue(createRequest);
        }

        private async Task createBeatmapPackage(PutBeatmapSetResponse response)
        {
            Debug.Assert(ThreadSafety.IsUpdateThread);

            exportStep.SetInProgress();

            try
            {
                beatmapPackageStream = new MemoryStream();
                exportProgressNotification = new ProgressNotification();

                var submissionExporter = new SubmissionBeatmapExporter(storage, response);

                await submissionExporter
                      .ExportToStreamAsync(Beatmap.Value.BeatmapSetInfo.ToLive(realmAccess), beatmapPackageStream, exportProgressNotification)
                      .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                exportStep.SetFailed(ex.Message);
                exportProgressNotification = null;
                log($"Export failed: {ex}");
                allowExit();
                return;
            }

            exportStep.SetCompleted();
            exportProgressNotification = null;

            await Task.Delay(200).ConfigureAwait(true);

            if (response.Files.Count > 0)
                await patchBeatmapSet(response.Files).ConfigureAwait(true);
            else
                await replaceBeatmapSet().ConfigureAwait(true);
        }

        private async Task patchBeatmapSet(ICollection<BeatmapSetFile> onlineFiles)
        {
            Debug.Assert(beatmapSetId != null);
            Debug.Assert(beatmapPackageStream != null);
            log("Determining list of files to patch...");

            var onlineFilesByFilename = onlineFiles.ToDictionary(f => f.Filename, f => f.SHA2Hash);

            // disposing the `ArchiveReader` makes the underlying stream no longer readable which we don't want.
            // make a local copy to defend against it.
            using var archiveReader = new ZipArchiveReader(new MemoryStream(beatmapPackageStream.ToArray()));
            var filesToUpdate = new HashSet<string>();

            foreach (string filename in archiveReader.Filenames)
            {
                string localHash = archiveReader.GetStream(filename).ComputeSHA2Hash();

                if (!onlineFilesByFilename.Remove(filename, out string? onlineHash))
                {
                    log($@"new file: {filename}");
                    filesToUpdate.Add(filename);
                    continue;
                }

                if (!localHash.Equals(onlineHash, StringComparison.OrdinalIgnoreCase))
                {
                    log($@"changed file: {filename} (localHash:{localHash} onlineHash:{onlineHash})");
                    filesToUpdate.Add(filename);
                }
            }

            if (filesToUpdate.Count == 0 && onlineFilesByFilename.Count == 0)
            {
                // Nothing changed since the last upload (the export is byte-stable), so sending an
                // empty PATCH would be pointless. Skip straight to the success/local-update path.
                log("No changed or deleted files; skipping patch upload.");
                uploadCompleted();
                return;
            }

            var changedFiles = new Dictionary<string, byte[]>();

            foreach (string file in filesToUpdate)
                changedFiles.Add(file, await archiveReader.GetStream(file).ReadAllBytesToArrayAsync().ConfigureAwait(true));

            uint setId = beatmapSetId.Value;
            string[] filesDeleted = onlineFilesByFilename.Keys.ToArray();

            foreach (string file in filesDeleted)
                log($@"deleted file: {file}");

            var sessionPayload = await ChunkedPackageUpload.BuildPatchPayloadAsync(changedFiles, filesDeleted).ConfigureAwait(true);

            beginPackageUpload(() =>
            {
                var patchRequest = new PatchBeatmapPackageRequest(setId);

                foreach ((string filename, byte[] contents) in changedFiles)
                    patchRequest.FilesChanged.Add(filename, contents);

                foreach (string file in filesDeleted)
                    patchRequest.FilesDeleted.Add(file);

                return patchRequest;
            }, sessionPayload);
        }

        private async Task replaceBeatmapSet()
        {
            log("Performing full package upload...");

            Debug.Assert(beatmapSetId != null);
            Debug.Assert(beatmapPackageStream != null);

            uint setId = beatmapSetId.Value;
            // snapshotted once so a retry does not depend on the package stream still being around.
            byte[] package = beatmapPackageStream.ToArray();

            var sessionPayload = await ChunkedPackageUpload.BuildFullPayloadAsync(package).ConfigureAwait(true);

            beginPackageUpload(() => new ReplaceBeatmapPackageRequest(setId, package), sessionPayload);
        }

        /// <summary>
        /// Starts the upload stage, retrying on transport failure per <see cref="UploadRetryPolicy"/>.
        /// </summary>
        /// <param name="requestFactory">
        /// Builds the request for one attempt. Called once per attempt, because an <see cref="APIRequest"/>
        /// is single-use, and must build from inputs already held in memory rather than recompute them.
        /// </param>
        /// <param name="sessionPayload">
        /// The same body assembled as a standalone payload, for the chunked fallback to send in pieces
        /// if the first direct attempt dies in transport. See <see cref="ChunkedPackageUpload"/>.
        /// </param>
        private void beginPackageUpload(Func<APIUploadRequest> requestFactory, ChunkedPackageUpload.UploadPayload sessionPayload)
        {
            Debug.Assert(ThreadSafety.IsUpdateThread);

            uploadRequestFactory = requestFactory;
            uploadSessionPayload = sessionPayload;
            lastChunkedFailure = null;
            chunkedResumes = 0;
            uploadAttempt = 0;
            queueUploadAttempt();
        }

        private void queueUploadAttempt()
        {
            Debug.Assert(ThreadSafety.IsUpdateThread);
            Debug.Assert(uploadRequestFactory != null);

            uploadAttempt++;

            var request = uploadRequestFactory();
            activeUploadRequest = request;

            request.Success += () =>
            {
                if (!ReferenceEquals(activeUploadRequest, request))
                    return;

                uploadCompleted();
            };
            request.Failure += ex => uploadAttemptFailed(request, ex);
            request.Progressed += (current, total) =>
            {
                if (!ReferenceEquals(activeUploadRequest, request))
                    return;

                uploadStep.SetInProgress(total > 0 ? (float)current / total : null);
            };

            log($"Uploading package (attempt {uploadAttempt}/{UploadRetryPolicy.MAX_ATTEMPTS})...");

            api.Queue(request);
            uploadStep.SetInProgress();
        }

        private void uploadAttemptFailed(APIUploadRequest request, Exception exception)
        {
            // a stale attempt's failure, or the cancellation of one, must not disturb the attempt in flight.
            if (!ReferenceEquals(activeUploadRequest, request))
                return;

            activeUploadRequest = null;

            log($"Upload attempt {uploadAttempt}/{UploadRetryPolicy.MAX_ATTEMPTS} failed: {exception}");

            // the first transport failure switches to a chunked upload session rather than repeating the
            // same single request, because the failure this is most often is a per-connection byte
            // ceiling that no amount of repeating gets past. Repeating stays the fallback's fallback.
            // A gateway 5xx takes the same turn (backlog 203's entry-path arm): a submission that
            // BEGINS inside a deploy window would otherwise die on this first attempt without ever
            // reaching the chunked machinery, whose slow gateway ladder is what rides a restart out.
            if (!exiting
                && uploadAttempt == 1
                && uploadSessionPayload != null
                && UploadRetryPolicy.SwitchesToChunked(exception))
            {
                beginChunkedUpload();
                return;
            }

            if (exiting || !UploadRetryPolicy.ShouldRetryAfter(uploadAttempt, exception))
            {
                uploadStep.SetFailed(uploadFailureMessage(exception));
                allowExit();
                return;
            }

            int nextAttempt = uploadAttempt + 1;
            double delay = UploadRetryPolicy.DelayBeforeAttempt(nextAttempt);

            uploadStep.SetRetrying($"upload failed, retrying (attempt {nextAttempt}/{UploadRetryPolicy.MAX_ATTEMPTS})");
            log($"Retrying upload in {delay / 1000:0.#}s");

            uploadRetryDelegate?.Cancel();
            uploadRetryDelegate = Scheduler.AddDelayed(() =>
            {
                if (exiting)
                    return;

                queueUploadAttempt();
            }, delay);
        }

        /// <summary>
        /// Switches the upload stage over to a chunked upload session after the direct upload failed in
        /// transport, and is also what a resume runs. If the session flow fails in turn with nothing held
        /// server-side (an old server 404s the create route), the direct retry ladder resumes where it
        /// left off, so behaviour degrades to what it was before this existed.
        /// </summary>
        private void beginChunkedUpload()
        {
            Debug.Assert(ThreadSafety.IsUpdateThread);
            Debug.Assert(beatmapSetId != null);
            Debug.Assert(uploadSessionPayload != null);

            uploadRetryDelegate?.Cancel();

            if (chunkedResumes == 0)
            {
                log("Upload failed in transport; switching to a chunked upload session.");
                uploadStep.SetRetrying("upload failed, switching to chunked upload");
            }
            else
            {
                // creation is idempotent on the payload's SHA-256, so this reattaches to the same session
                // and re-sends only what the server is missing rather than starting the upload over.
                log($"Reattaching to the chunked upload session (resume {chunkedResumes}/{UploadRetryPolicy.MAX_CHUNKED_RESUMES}).");
                uploadStep.SetRetrying($"upload interrupted, resuming chunked upload ({chunkedResumes}/{UploadRetryPolicy.MAX_CHUNKED_RESUMES})");
            }

            ChunkedPackageUpload upload = null!;

            upload = new ChunkedPackageUpload(beatmapSetId.Value, uploadSessionPayload, api, (action, delay) =>
            {
                uploadRetryDelegate?.Cancel();
                uploadRetryDelegate = Scheduler.AddDelayed(action, delay);
            })
            {
                OnProgress = (done, total) =>
                {
                    if (!ReferenceEquals(chunkedUpload, upload))
                        return;

                    uploadStep.SetInProgress(total > 0 ? (float)done / total : null);
                },
                OnSucceeded = () =>
                {
                    if (!ReferenceEquals(chunkedUpload, upload))
                        return;

                    chunkedUpload = null;
                    uploadCompleted();
                },
                OnFailed = exception =>
                {
                    if (!ReferenceEquals(chunkedUpload, upload))
                        return;

                    chunkedUpload = null;

                    // snapshotted here, because the flow is the only thing that knows how much of the
                    // payload actually landed and it is being dropped on this line.
                    chunkedUploadFailed(exception, upload.HadProgress, upload.HeldChunks, upload.TotalChunkCount);
                },
            };

            chunkedUpload = upload;
            upload.Start();
        }

        /// <summary>
        /// Builds the message the upload step ends on.
        /// </summary>
        /// <remarks>
        /// A chunked session that has already failed is the more informative of the two arms, because it
        /// is the one that got as far as talking to the server about individual chunks, so its message
        /// leads and the direct attempt's is appended. Without this the user is shown only the direct
        /// failure, which describes a request that was abandoned in favour of the session path minutes
        /// earlier and says nothing about why the session path gave up.
        /// </remarks>
        private string uploadFailureMessage(Exception directFailure)
        {
            if (lastChunkedFailure != null)
                return $"{lastChunkedFailure.Message} (direct upload also failed: {directFailure.Message})";

            return uploadAttempt > 1
                ? $"{directFailure.Message} (upload failed after {uploadAttempt} attempts)"
                : directFailure.Message;
        }

        /// <summary>
        /// Decides what follows a chunked upload session that ended in failure.
        /// </summary>
        /// <remarks>
        /// The decision itself is <see cref="UploadRetryPolicy.ActionAfterChunkedFailure"/>, which is pure
        /// and pinned; this only carries it out. The one thing worth restating here is why the direct
        /// ladder is no longer the universal answer: a session holding chunks that hands back to the
        /// direct upload throws away every chunk that landed and re-sends the single large request that
        /// the whole chunked protocol exists because this user cannot send. During a deploy that turned a
        /// twenty second outage into a permanently failed submission.
        /// </remarks>
        private void chunkedUploadFailed(Exception exception, bool hadProgress, int heldChunks, int totalChunks)
        {
            log($"Chunked upload failed with {heldChunks}/{totalChunks} chunks held: {exception}");

            lastChunkedFailure = exception;

            if (exiting)
            {
                uploadStep.SetFailed(exception.Message);
                allowExit();
                return;
            }

            switch (UploadRetryPolicy.ActionAfterChunkedFailure(chunkedResumes, hadProgress, uploadAttempt, exception))
            {
                case UploadRetryPolicy.ChunkedFailureAction.ResumeChunked:
                {
                    chunkedResumes++;

                    double resumeDelay = UploadRetryPolicy.DelayBeforeChunkedResume(chunkedResumes);

                    log($"Resuming the chunked upload in {resumeDelay / 1000:0.#}s (resume {chunkedResumes}/{UploadRetryPolicy.MAX_CHUNKED_RESUMES})");
                    uploadStep.SetRetrying($"upload interrupted, resuming chunked upload ({chunkedResumes}/{UploadRetryPolicy.MAX_CHUNKED_RESUMES})");

                    uploadRetryDelegate?.Cancel();
                    uploadRetryDelegate = Scheduler.AddDelayed(() =>
                    {
                        if (exiting)
                            return;

                        beginChunkedUpload();
                    }, resumeDelay);
                    return;
                }

                case UploadRetryPolicy.ChunkedFailureAction.FallBackToDirect:
                {
                    // nothing landed, so this is the old-server case: end up exactly where the submission
                    // would have without the session routes existing at all.
                    int nextAttempt = uploadAttempt + 1;
                    double delay = UploadRetryPolicy.DelayBeforeAttempt(nextAttempt);

                    uploadStep.SetRetrying($"upload failed, retrying (attempt {nextAttempt}/{UploadRetryPolicy.MAX_ATTEMPTS})");
                    log($"Retrying direct upload in {delay / 1000:0.#}s");

                    uploadRetryDelegate?.Cancel();
                    uploadRetryDelegate = Scheduler.AddDelayed(() =>
                    {
                        if (exiting)
                            return;

                        queueUploadAttempt();
                    }, delay);
                    return;
                }

                default:
                    uploadStep.SetFailed(exception.Message);
                    allowExit();
                    return;
            }
        }

        private void uploadCompleted()
        {
            activeUploadRequest = null;
            uploadRequestFactory = null;
            uploadSessionPayload = null;
            chunkedUpload = null;
            lastChunkedFailure = null;
            chunkedResumes = 0;
            uploadRetryDelegate?.Cancel();

            uploadStep.SetCompleted();
            updateLocalBeatmap().ConfigureAwait(true);
        }

        private async Task updateLocalBeatmap()
        {
            log(@"Updating local beatmap set...");

            Debug.Assert(beatmapSetId != null);
            Debug.Assert(beatmapPackageStream != null);

            updateStep.SetInProgress();
            await Task.Delay(200).ConfigureAwait(true);

            try
            {
                importedSet = await beatmaps.ImportAsUpdate(
                    updateProgressNotification = new ProgressNotification(),
                    new ImportTask(beatmapPackageStream, $"{beatmapSetId}.typb"),
                    Beatmap.Value.BeatmapSetInfo).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                updateStep.SetFailed(ex.Message);
                log($@"Local update failed: {ex}");
                allowExit();
                return;
            }

            updateStep.SetCompleted();
            showBeatmapCard();
            allowExit();

            if (configManager.Get<bool>(OsuSetting.EditorSubmissionLoadInBrowserAfterSubmission))
            {
                await Task.Delay(1000).ConfigureAwait(true);
                game?.OpenUrlExternally($"{api.Endpoints.WebsiteUrl}/beatmapsets/{beatmapSetId}");
            }
        }

        private void showBeatmapCard()
        {
            Debug.Assert(beatmapSetId != null);

            var getBeatmapSetRequest = new GetBeatmapSetRequest((int)beatmapSetId.Value);
            getBeatmapSetRequest.Success += beatmapSet =>
            {
                LoadComponentAsync(new BeatmapCardExtra(beatmapSet, false), loaded =>
                {
                    successContainer.Add(loaded);
                    flashLayer.FadeOutFromOne(2000, Easing.OutQuint);
                });

                completedSample.Play();
            };

            api.Queue(getBeatmapSetRequest);
        }

        private void allowExit()
        {
            BackButtonVisibility.Value = true;
        }

        protected override void Update()
        {
            base.Update();

            if (exportProgressNotification != null && exportProgressNotification.Ongoing)
                exportStep.SetInProgress(exportProgressNotification.Progress);

            if (updateProgressNotification != null && updateProgressNotification.Ongoing)
                updateStep.SetInProgress(updateProgressNotification.Progress);
        }

        public override bool OnExiting(ScreenExitEvent e)
        {
            // We probably want a method of cancelling in the future…
            if (!BackButtonVisibility.Value)
                return true;

            // past this point the screen is on its way out, so a pending upload retry must not fire.
            exiting = true;
            uploadRetryDelegate?.Cancel();
            chunkedUpload?.Cancel();
            chunkedUpload = null;

            if (importedSet != null)
            {
                game?.PerformFromScreen(s =>
                {
                    if (s is OsuScreen osuScreen)
                    {
                        Debug.Assert(importedSet != null);
                        var targetBeatmap = importedSet.Value.Beatmaps.FirstOrDefault(b => b.DifficultyName == Beatmap.Value.BeatmapInfo.DifficultyName)
                                            ?? importedSet.Value.Beatmaps.First();
                        osuScreen.Beatmap.Value = beatmaps.GetWorkingBeatmap(targetBeatmap);
                    }

                    s.Push(new EditorLoader());
                }, [typeof(SongSelect)]);

                return false;
            }

            return base.OnExiting(e);
        }

        public override void OnEntering(ScreenTransitionEvent e)
        {
            base.OnEntering(e);

            overlay.Show();
        }

        private static void log(string message)
            => Logger.Log($@"[{nameof(BeatmapSubmissionScreen)}] {message}", LoggingTarget.Database);

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            exiting = true;
            uploadRetryDelegate?.Cancel();
            chunkedUpload?.Cancel();
            chunkedUpload = null;

            beatmapPackageStream?.Dispose();
        }
    }
}
