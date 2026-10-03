// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Extensions;
using osu.Framework.Graphics.Textures;
using osu.Framework.Logging;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.UI;
using typebeat.Game.Skinning;
using typebeat.Game.Storyboards;

namespace typebeat.Game.Beatmaps
{
    public abstract class WorkingBeatmap : IWorkingBeatmap
    {
        public readonly BeatmapInfo BeatmapInfo;
        public readonly BeatmapSetInfo BeatmapSetInfo;

        // TODO: remove once the fallback lookup is not required (and access via `working.BeatmapInfo.Metadata` directly).
        public BeatmapMetadata Metadata => BeatmapInfo.Metadata;

        /// <summary>
        /// This difficulty's background image when it names one the set holds, otherwise the set's first
        /// available one. A set normally shares one image across its difficulties, but the editor can give
        /// a difficulty its own (the setup screen's per-difficulty background tick), and that one wins here.
        /// A difficulty naming no image, or a missing one, still inherits its siblings'.
        /// </summary>
        public string BackgroundFile => hasUsableFile(Metadata.BackgroundFile)
            ? Metadata.BackgroundFile
            : BeatmapSetInfo.Beatmaps.Select(b => b.Metadata.BackgroundFile).FirstOrDefault(hasUsableFile) ?? Metadata.BackgroundFile;

        private bool hasUsableFile(string path) => !string.IsNullOrEmpty(path) && BeatmapSetInfo.GetFile(path) != null;

        public Storyboard Storyboard => storyboard.Value;

        public ISkin Skin => skin.Value;

        private AudioManager audioManager { get; }

        private CancellationTokenSource loadCancellationSource = new CancellationTokenSource();

        private readonly object beatmapFetchLock = new object();

        private readonly Lazy<Storyboard> storyboard;
        private readonly Lazy<ISkin> skin;

        private Track track; // track is not Lazy as we allow transferring and loading multiple times.
        private Waveform waveform; // waveform is also not Lazy as the track may change.
        private string waveformSource;

        // The vocals stem is a SEPARATE decode from the map's audio (see VocalsStemWaveform), cached
        // by the stem file's storage path so a swap of the stem replaces it.
        private Waveform vocalsStemWaveform;
        private string vocalsStemWaveformSource;

        protected WorkingBeatmap(BeatmapInfo beatmapInfo, AudioManager audioManager)
        {
            this.audioManager = audioManager;

            BeatmapInfo = beatmapInfo;
            BeatmapSetInfo = beatmapInfo.BeatmapSet ?? new BeatmapSetInfo();

            storyboard = new Lazy<Storyboard>(GetStoryboard);
            skin = new Lazy<ISkin>(GetSkin);
        }

        #region Resource getters

        protected virtual Waveform GetWaveform() => new Waveform(null);

        /// <summary>
        /// Identifies the audio DATA <see cref="GetWaveform"/> reads, so a track reload that plays the
        /// same data can keep the waveform it already decoded (see <see cref="LoadTrack"/>). Null, the
        /// default, means unknown, and every reload then decodes again.
        /// </summary>
        protected virtual string WaveformSource => null;

        protected virtual Storyboard GetStoryboard() => new Storyboard
        {
            BeatmapInfo = BeatmapInfo,
            Beatmap = Beatmap,
        };

        protected abstract IBeatmap GetBeatmap();
        public abstract Texture GetBackground();
        public virtual Texture GetPanelBackground() => GetBackground();
        protected abstract Track GetBeatmapTrack();

        /// <summary>
        /// Creates a new skin instance for this beatmap.
        /// </summary>
        /// <remarks>
        /// This should only be called externally in scenarios where it is explicitly desired to get a new instance of a skin
        /// (e.g. for editing purposes, to avoid state pollution).
        /// For standard reading purposes, <see cref="Skin"/> should always be used directly.
        /// </remarks>
        protected internal abstract ISkin GetSkin();

        #endregion

        #region Async load control

        public void BeginAsyncLoad() => loadBeatmapAsync();

        public void CancelAsyncLoad()
        {
            lock (beatmapFetchLock)
            {
                loadCancellationSource?.Cancel();
                loadCancellationSource = new CancellationTokenSource();

                if (beatmapLoadTask?.IsCompleted != true)
                    beatmapLoadTask = null;
            }
        }

        #endregion

        #region Track

        public virtual bool TrackLoaded => track != null;

        public Track LoadTrack()
        {
            track = GetBeatmapTrack() ?? GetVirtualTrack(1000);

            // the track may have changed, recycle the current waveform. Unless it was read from the very
            // data the new track plays: the map's audio gain rebuilds the track from the same file (it
            // scales what the track plays, never the file), and decoding the whole song again for that
            // produced an identical waveform at the cost of a full decode on every committed gain.
            string source = WaveformSource;

            if (waveform == null || source == null || source != waveformSource)
            {
                waveform?.Dispose();
                waveform = null;
            }

            return track;
        }

        public void PrepareTrackForPreview(bool looping, double? offsetFromPreviewPoint = null)
        {
            Track.Looping = looping;
            Track.RestartPoint = Metadata.PreviewTime;

            if (!Track.IsLoaded)
            {
                // force length to be populated (https://github.com/ppy/osu-framework/issues/4202)
                Track.Seek(Track.CurrentTime);
            }

            if (Track.RestartPoint < 0 || Track.RestartPoint > Track.Length)
                Track.RestartPoint = 0.4f * Track.Length;

            offsetFromPreviewPoint ??= -MusicController.DELAY_BEFORE_FADE;

            Track.RestartPoint = Math.Clamp(Track.RestartPoint + offsetFromPreviewPoint.Value, 0, Track.Length);
        }

        /// <summary>
        /// Attempts to transfer the audio track to a target working beatmap, if valid for transferring.
        /// Used as an optimisation to avoid reload / track swap across difficulties in the same beatmap set.
        /// </summary>
        /// <param name="target">The target working beatmap to transfer this track to.</param>
        /// <returns>Whether the track has been transferred to the <paramref name="target"/>.</returns>
        public virtual bool TryTransferTrack([NotNull] WorkingBeatmap target)
        {
            if (BeatmapInfo?.AudioEquals(target.BeatmapInfo) != true || Track.IsDummyDevice)
                return false;

            // The same audio FILE is not the same audio once a map's own gain is baked into what its
            // track plays (see BeatmapMetadata.AudioGain): a set whose difficulties disagree about the
            // gain would otherwise carry the first one's loudness into the second. Checked here rather
            // than in AudioEquals, which also decides whether two difficulties share a stored offset.
            if (Metadata.AudioGain != target.Metadata.AudioGain)
                return false;

            target.track = Track;
            return true;
        }

        /// <summary>
        /// Get the loaded audio track instance. <see cref="LoadTrack"/> must have first been called.
        /// This generally happens via MusicController when changing the global beatmap.
        /// </summary>
        [NotNull]
        public Track Track
        {
            get
            {
                if (!TrackLoaded)
                    throw new InvalidOperationException($"Cannot access {nameof(Track)} without first calling {nameof(LoadTrack)}.");

                return track;
            }
        }

        protected Track GetVirtualTrack(double emptyLength = 0)
        {
            const double excess_length = 1000;

            double length = (BeatmapInfo?.Length + excess_length) ?? emptyLength;

            return audioManager.Tracks.GetVirtual(length);
        }

        #endregion

        #region Waveform

        public Waveform Waveform
        {
            get
            {
                if (waveform == null)
                {
                    waveformSource = WaveformSource;
                    waveform = GetWaveform();
                }

                return waveform;
            }
        }

        /// <summary>
        /// The waveform of the map's ISOLATED VOCALS STEM, or null when the map carries none. A
        /// SEPARATE decode from <see cref="Waveform"/> (a second <see cref="Waveform"/> built over
        /// the stem file), decoded lazily and kept for the beatmap's lifetime; it is a mapper's
        /// reading surface only and never feeds playback, timing or save.
        /// </summary>
        /// <remarks>
        /// Read through the realm file list rather than the audio declaration: the stem is not the
        /// map's audio and has no metadata field, so its presence is answered by whether
        /// <c>vocals.ogg</c>/<c>vocals.wav</c> is among the set's files (see
        /// <see cref="VocalsStem"/>). A map with no stem, or one whose stem cannot be decoded,
        /// answers null and the editor hides the toggle.
        /// </remarks>
        public Waveform VocalsStemWaveform
        {
            get
            {
                string filename = VocalsStem.FilenameIn(BeatmapSetInfo);

                if (filename == null)
                {
                    vocalsStemWaveform?.Dispose();
                    vocalsStemWaveform = null;
                    vocalsStemWaveformSource = null;
                    return null;
                }

                string source = BeatmapSetInfo.GetPathForFile(filename);

                if (vocalsStemWaveform != null && source != null && source == vocalsStemWaveformSource)
                    return vocalsStemWaveform;

                vocalsStemWaveform?.Dispose();
                vocalsStemWaveformSource = source;

                try
                {
                    var stream = GetVocalsStream(filename);
                    vocalsStemWaveform = stream == null ? null : new Waveform(stream);
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Vocals stem waveform failed to load");
                    vocalsStemWaveform = null;
                }

                return vocalsStemWaveform;
            }
        }

        /// <summary>
        /// The stream the vocals stem's waveform is decoded from, or null. Left open by the
        /// framework <see cref="Waveform"/> itself (it takes ownership), so the base implementation
        /// returns null and only the database-backed working beatmap overrides it.
        /// </summary>
        protected virtual Stream GetVocalsStream(string vocalsStemFilename) => null;

        #endregion

        #region Beatmap

        public virtual bool BeatmapLoaded
        {
            get
            {
                lock (beatmapFetchLock)
                    return beatmapLoadTask?.IsCompleted ?? false;
            }
        }

        public IBeatmap Beatmap
        {
            get
            {
                try
                {
                    // TODO: This is a touch expensive and can become an issue if being accessed every Update call.
                    // Optimally we would not involve the async flow if things are already loaded.
                    return loadBeatmapAsync().GetResultSafely();
                }
                catch (AggregateException ae)
                {
                    // This is the exception that is generally expected here, which occurs via natural cancellation of the asynchronous load
                    if (ae.InnerExceptions.FirstOrDefault() is TaskCanceledException)
                        return null;

                    Logger.Error(ae, $"Beatmap failed to load ({BeatmapInfo})");
                    return null;
                }
                catch (Exception e)
                {
                    Logger.Error(e, $"Beatmap failed to load ({BeatmapInfo})");
                    return null;
                }
            }
        }

        private Task<IBeatmap> beatmapLoadTask;

        private Task<IBeatmap> loadBeatmapAsync()
        {
            lock (beatmapFetchLock)
            {
                return beatmapLoadTask ??= Task.Factory.StartNew(() =>
                {
                    // Todo: Handle cancellation during beatmap parsing
                    var b = GetBeatmap() ?? new Beatmap();

                    // Copy across values of key properties for which the database-backed model has data that the decoded beatmap isn't going to.
                    b.BeatmapInfo.ID = BeatmapInfo.ID;
                    b.BeatmapInfo.UserSettings = BeatmapInfo.UserSettings;
                    b.BeatmapInfo.BeatmapSet = BeatmapInfo.BeatmapSet;
                    b.BeatmapInfo.Status = BeatmapInfo.Status;
                    b.BeatmapInfo.OnlineID = BeatmapInfo.OnlineID;
                    b.BeatmapInfo.OnlineMD5Hash = BeatmapInfo.OnlineMD5Hash;
                    b.BeatmapInfo.LastLocalUpdate = BeatmapInfo.LastLocalUpdate;
                    b.BeatmapInfo.LastOnlineUpdate = BeatmapInfo.LastOnlineUpdate;
                    b.BeatmapInfo.LastPlayed = BeatmapInfo.LastPlayed;
                    b.BeatmapInfo.EditorTimestamp = BeatmapInfo.EditorTimestamp;
                    b.BeatmapInfo.StarRating = BeatmapInfo.StarRating; // this could be recomputed in the decoding process but it's a bit annoying to do.

                    return b;
                }, loadCancellationSource.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        #endregion

        #region Playable beatmap

        public IBeatmap GetPlayableBeatmap(IRulesetInfo ruleset, IReadOnlyList<Mod> mods = null)
        {
            try
            {
                using (var cancellationTokenSource = new CancellationTokenSource(10_000))
                {
                    // don't apply the default timeout when debugger is attached (may be breakpointing / debugging).
                    return GetPlayableBeatmap(ruleset, mods ?? Array.Empty<Mod>(), Debugger.IsAttached ? CancellationToken.None : cancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                throw new BeatmapLoadTimeoutException(BeatmapInfo);
            }
        }

        public virtual IBeatmap GetPlayableBeatmap(IRulesetInfo ruleset, IReadOnlyList<Mod> mods, CancellationToken token)
        {
            var rulesetInstance = ruleset.CreateInstance();

            if (rulesetInstance == null)
                throw new RulesetLoadException("Creating ruleset instance failed when attempting to create playable beatmap.");

            IBeatmapConverter converter = CreateBeatmapConverter(Beatmap, rulesetInstance);

            // Check if the beatmap can be converted
            if (Beatmap.HitObjects.Count > 0 && !converter.CanConvert())
                throw new BeatmapInvalidForRulesetException($"{nameof(Beatmaps.Beatmap)} can not be converted for the ruleset (ruleset: {ruleset.InstantiationInfo}, converter: {converter}).");

            // Apply conversion mods
            foreach (var mod in mods.OfType<IApplicableToBeatmapConverter>())
            {
                token.ThrowIfCancellationRequested();
                mod.ApplyToBeatmapConverter(converter);
            }

            // Convert
            IBeatmap converted = converter.Convert(token);

            // Apply conversion mods to the result
            foreach (var mod in mods.OfType<IApplicableAfterBeatmapConversion>())
            {
                token.ThrowIfCancellationRequested();
                mod.ApplyToBeatmap(converted);
            }

            // Apply difficulty mods
            if (mods.Any(m => m is IApplicableToDifficulty))
            {
                foreach (var mod in mods.OfType<IApplicableToDifficulty>())
                {
                    token.ThrowIfCancellationRequested();
                    mod.ApplyToDifficulty(converted.Difficulty);
                }
            }

            var processor = rulesetInstance.CreateBeatmapProcessor(converted);

            if (processor != null)
            {
                foreach (var mod in mods.OfType<IApplicableToBeatmapProcessor>())
                    mod.ApplyToBeatmapProcessor(processor);

                processor.PreProcess();
            }

            // Compute default values for hitobjects, including creating nested hitobjects in-case they're needed
            foreach (var obj in converted.HitObjects)
            {
                token.ThrowIfCancellationRequested();
                obj.ApplyDefaults(converted.ControlPointInfo, converted.Difficulty, token);
            }

            foreach (var mod in mods.OfType<IApplicableToHitObject>())
            {
                foreach (var obj in converted.HitObjects)
                {
                    token.ThrowIfCancellationRequested();
                    mod.ApplyToHitObject(obj);
                }
            }

            processor?.PostProcess();

            foreach (var mod in mods.OfType<IApplicableToBeatmap>())
            {
                token.ThrowIfCancellationRequested();
                mod.ApplyToBeatmap(converted);
            }

            return converted;
        }

        /// <summary>
        /// Creates a <see cref="IBeatmapConverter"/> to convert a <see cref="IBeatmap"/> for a specified <see cref="Ruleset"/>.
        /// </summary>
        /// <param name="beatmap">The <see cref="IBeatmap"/> to be converted.</param>
        /// <param name="ruleset">The <see cref="Ruleset"/> for which <paramref name="beatmap"/> should be converted.</param>
        /// <returns>The applicable <see cref="IBeatmapConverter"/>.</returns>
        protected virtual IBeatmapConverter CreateBeatmapConverter(IBeatmap beatmap, Ruleset ruleset) => ruleset.CreateBeatmapConverter(beatmap);

        #endregion

        public override string ToString() => BeatmapInfo.ToString();

        public abstract Stream GetStream(string storagePath);

        IBeatmapInfo IWorkingBeatmap.BeatmapInfo => BeatmapInfo;

        private class BeatmapLoadTimeoutException : TimeoutException
        {
            public BeatmapLoadTimeoutException(BeatmapInfo beatmapInfo)
                : base($"Timed out while loading beatmap ({beatmapInfo}).")
            {
            }
        }
    }
}
