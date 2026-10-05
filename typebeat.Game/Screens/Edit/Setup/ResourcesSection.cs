// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using typebeat.Game.Audio.Effects;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.IO;
using typebeat.Game.Localisation;
using typebeat.Game.Models;
using typebeat.Game.Overlays;
using typebeat.Game.Screens.Backgrounds;
using typebeat.Game.Storyboards;
using typebeat.Game.Utils;
using FileInfo = System.IO.FileInfo;

namespace typebeat.Game.Screens.Edit.Setup
{
    public partial class ResourcesSection : SetupSection
    {
        /// <summary>Caption of the video offset control; also how tests find it among the section's boxes.</summary>
        public const string VIDEO_OFFSET_CAPTION = "Video offset (ms)";

        /// <summary>Caption of the per-difficulty background tick; also how tests find it.</summary>
        public const string SEPARATE_BACKGROUND_CAPTION = "Separate background for this difficulty";

        /// <summary>The background chooser's hint while it writes every difficulty (the tick is off).</summary>
        public const string BACKGROUND_HINT_ALL_DIFFICULTIES = "Applies to all difficulties in this beatmap.";

        /// <summary>The background chooser's hint while it writes only the open difficulty (the tick is on).</summary>
        public const string BACKGROUND_HINT_THIS_DIFFICULTY = "Applies to this difficulty only.";

        private FormBeatmapFileSelector audioTrackChooser = null!;
        private AudioClippingIndicator clippingIndicator = null!;
        private FormFileSelector backgroundChooser = null!;

        /// <summary>
        /// The "Separate background for this difficulty" tick. Not stored anywhere: it is DERIVED on load
        /// from whether this difficulty's background differs from the one its siblings share (see
        /// <see cref="HasSeparateBackground"/>), and only ever steers where the next chosen file goes.
        /// Hidden on a single-difficulty set, where the chooser writes that one difficulty as it always did.
        /// </summary>
        private FormCheckBox separateBackgroundCheckBox = null!;

        private bool beatmapHasMultipleDifficulties;
        private bool syncingSeparateBackgroundTick;
        private FormFileSelector videoChooser = null!;
        private FormNumberBox videoOffsetBox = null!;

        /// <summary>
        /// The map's own track gain, as the linear multiplier the beatmap stores (1 = as imported), and
        /// what the bar above edits. A bindable of its own rather than the metadata's value held
        /// directly, because the metadata OBJECT is replaced when the mapper switches difficulty or
        /// reloads the map, and the write-through then happens in exactly one place (see
        /// <see cref="applyAudioGain"/>). Bounded to the model's own limits, so the bar cannot ask for a
        /// gain the format would clamp away on the next load.
        /// </summary>
        private readonly BindableDouble audioGain = new BindableDouble(BeatmapMetadata.DEFAULT_AUDIO_GAIN)
        {
            MinValue = 0,
            MaxValue = BeatmapMetadata.MAX_AUDIO_GAIN,
            Precision = 0.01,
        };

        /// <summary>
        /// The waveform the clipping indicator's reading came from, so a REPLACED audio file is noticed:
        /// a new file is a new waveform object with its own peaks, and the reading has to follow the
        /// object rather than the screen's lifetime. Read on every frame (see <see cref="Update"/>)
        /// because the swap happens elsewhere in this same screen.
        /// </summary>
        private Waveform? analysedWaveform;

        public override LocalisableString Title => EditorSetupStrings.ResourcesHeader;

        [Resolved]
        private MusicController music { get; set; } = null!;

        [Resolved]
        private BeatmapManager beatmaps { get; set; } = null!;

        [Resolved]
        private IBindable<WorkingBeatmap> currentWorkingBeatmap { get; set; } = null!;

        [Resolved]
        private Editor? editor { get; set; }

        [Resolved]
        private SetupScreen setupScreen { get; set; } = null!;

        private SetupScreenBackgroundPreview backgroundPreview = null!;
        private SetupScreenVideoPreview videoPreview = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            backgroundPreview = new SetupScreenBackgroundPreview
            {
                RelativeSizeAxes = Axes.X,
                Height = 110,
            };
            videoPreview = new SetupScreenVideoPreview
            {
                RelativeSizeAxes = Axes.X,
                Height = 110,
            };

            beatmapHasMultipleDifficulties = currentWorkingBeatmap.Value.BeatmapSetInfo.Beatmaps.Count > 1;

            Children = new Drawable[]
            {
                backgroundChooser = new FormFileSelector(SupportedExtensions.IMAGE_EXTENSIONS)
                {
                    Caption = GameplaySettingsStrings.BackgroundHeader,
                    PlaceholderText = EditorSetupStrings.ClickToSelectBackground,
                    HintText = BACKGROUND_HINT_ALL_DIFFICULTIES,
                },
                separateBackgroundCheckBox = new FormCheckBox
                {
                    Caption = SEPARATE_BACKGROUND_CAPTION,
                    HintText = "Off: one background for the whole beatmap. On: the next background you choose applies to this difficulty only. Turning it off again puts this difficulty back on the shared background.",
                    // Not present at all on a single-difficulty set, so the column does not leave a gap.
                    Alpha = beatmapHasMultipleDifficulties ? 1 : 0,
                },
                videoChooser = new FormFileSelector(SupportedExtensions.VIDEO_EXTENSIONS)
                {
                    Caption = EditorSetupStrings.Video,
                    PlaceholderText = EditorSetupStrings.ClickToSelectVideo,
                    HintText = "Applies to all difficulties in this beatmap. The video replaces the static background; provide a matching image for downloads without video.",
                    AllowClear = true,
                },
                // Directly under the picker it re-times, and whole milliseconds only: the format's
                // offset field is int-parsed on decode, and a decimal would not error, it would drop
                // the video element on the next load. A number box that cannot type a "." makes that
                // unrepresentable (it can still type the leading "-", which is half the point).
                videoOffsetBox = new FormNumberBox(allowDecimals: false)
                {
                    Caption = VIDEO_OFFSET_CAPTION,
                    HintText = "Applies to all difficulties. Syncs the video to the song. Positive = the video starts later: its first frame plays this many milliseconds into the song. Negative starts it earlier. Whole milliseconds.",
                    PlaceholderText = "e.g. -50",
                },
                audioTrackChooser = new FormBeatmapFileSelector(beatmapHasMultipleDifficulties, SupportedExtensions.AUDIO_EXTENSIONS)
                {
                    Caption = EditorSetupStrings.AudioTrack,
                    PlaceholderText = EditorSetupStrings.ClickToSelectTrack,
                    HintText = EditorSetupStrings.AudioTrackHint,
                },
                // Directly under the track it scales, and a GAIN rather than a volume, because the
                // stack's volume stops at 100%: a quietly mastered song has nothing left to give there
                // (see BeatmapMetadata.AudioGain and Audio.Effects.AudioGain), so this is the only
                // control that can actually make one louder. 100% is the song as imported.
                new FormSliderBar<double>
                {
                    Caption = "Audio gain",
                    HintText = "Loudness of the song itself, for everyone who plays the map: 100% is the file as imported, above that amplifies it. Use it to lift a quietly mastered track; boost too far and a loud song clips.",
                    Current = { BindTarget = audioGain },
                    KeyboardStep = 0.05f,
                    DisplayAsPercentage = true,
                    // Applied when the drag ends rather than on every pixel of it: one write to the
                    // mixer and ONE editor state per adjustment, instead of an undoable step per
                    // intermediate value.
                    TransferValueOnCommit = true,
                },
                clippingIndicator = new AudioClippingIndicator
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 20,
                },
            };

            backgroundChooser.PreviewContainer.Add(backgroundPreview);
            videoChooser.PreviewContainer.Add(videoPreview);

            if (!string.IsNullOrEmpty(currentWorkingBeatmap.Value.BackgroundFile))
                backgroundChooser.Current.Value = new FileInfo(currentWorkingBeatmap.Value.BackgroundFile);

            if (currentWorkingBeatmap.Value.Storyboard.PrimaryVideo is StoryboardVideo video)
                videoChooser.Current.Value = new FileInfo(video.Path);

            if (!string.IsNullOrEmpty(currentWorkingBeatmap.Value.Metadata.AudioFile))
                audioTrackChooser.Current.Value = new FileInfo(currentWorkingBeatmap.Value.Metadata.AudioFile);

            syncingSeparateBackgroundTick = true;
            separateBackgroundCheckBox.Current.Value = HasSeparateBackground;
            syncingSeparateBackgroundTick = false;
            updateBackgroundHint();
            separateBackgroundCheckBox.Current.BindValueChanged(separateBackgroundTickChanged);

            backgroundChooser.Current.BindValueChanged(backgroundChanged);
            videoChooser.Current.BindValueChanged(videoChanged);
            audioTrackChooser.Current.BindValueChanged(audioTrackChanged);

            // Committed (enter / focus loss), never per keystroke: applying an offset rebuilds the
            // whole editor background storyboard asynchronously.
            videoOffsetBox.OnCommit += (_, _) => commitVideoOffset();
            updateVideoOffsetDisplay();

            // The gain goes in as the bar is COMMITTED (released, or its text box committed) rather than
            // on every pixel of a drag: `TransferValueOnCommit` above is what holds the intermediate
            // values back, and this binding is downstream of it, so one adjustment is one application.
            // That matters here in a way it does not for a mixer write, because applying a gain rebuilds
            // the track and re-saves every difficulty of the set (see `applyAudioGain`).
            audioGain.Value = Beatmap.Metadata.AudioGain;
            audioGain.BindValueChanged(gain => applyAudioGain(gain.NewValue));
            setupScreen.MetadataChanged += reloadAudioGain;

            analyseTrackPeaks();
        }

        /// <summary>
        /// Reads the track's loudest sample for the clipping indicator (see
        /// <see cref="AudioClippingIndicator"/>), ON A WORKER: the first thing that touches the
        /// framework's waveform decodes the whole song, and doing that on the update thread would stall
        /// the editor on the frame the setup screen opens. The result comes back through
        /// <see cref="Schedule"/> like every other piece of cross-thread state here.
        /// </summary>
        private void analyseTrackPeaks()
        {
            var waveform = currentWorkingBeatmap.Value?.Waveform;
            analysedWaveform = waveform;

            clippingIndicator.Peak = null;
            clippingIndicator.Gain = audioGain.Value;

            if (waveform == null)
            {
                // No audio to read at all: nothing can clip, and leaving the indicator saying it is still
                // reading would be a lie about work that is never coming.
                clippingIndicator.Peak = 0;
                return;
            }

            Task.Run(async () =>
            {
                double peak;

                try
                {
                    // The ASYNC reading: the synchronous one waits inside the framework, which the
                    // framework forbids from inside a task, and this is a task on purpose so that a song's
                    // first analysis cannot stall the editor.
                    peak = await AudioGain.PeakOfAsync(waveform).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // Reading a waveform that a swap has since replaced or disposed is not the mapper's
                    // problem, and it must not become one: an exception escaping a Task nobody awaits
                    // is what the game reports as an unobserved error, which is exactly what this used
                    // to do here. The replacement's own reading is already queued, so this one stands
                    // down and says so quietly in the log.
                    Logger.Log($@"Could not read the audio's peaks for the clipping indicator: {e.Message}");
                    return;
                }

                try
                {
                    Schedule(() =>
                    {
                        // A reading that arrives after the audio was replaced is about a file that is no
                        // longer loaded, so it is dropped rather than shown.
                        if (!ReferenceEquals(waveform, analysedWaveform))
                            return;

                        // One line per reading, so "the indicator is blank" can be told apart from "the
                        // indicator is showing something wrong" without guessing.
                        Logger.Log($@"Audio peaks read for the clipping indicator: {peak * 100:0}% of full scale before the map's gain.");

                        clippingIndicator.Peak = peak;
                        clippingIndicator.Gain = audioGain.Value;
                    });
                }
                catch (ObjectDisposedException)
                {
                    // The setup screen closed while the song was being read. Nothing to report to anyone.
                }
            });
        }

        protected override void Update()
        {
            base.Update();

            // Follow the WAVEFORM OBJECT, not this screen's lifetime: swapping the audio file hands the
            // map a different waveform with different peaks, and the old reading is about a song that is
            // no longer loaded. This is the only reliable place to notice it - the swap happens inside
            // this same screen, and the object it replaces is the framework's.
            if (!ReferenceEquals(analysedWaveform, currentWorkingBeatmap.Value?.Waveform))
                analyseTrackPeaks();
        }

        /// <summary>
        /// Re-reads the map's gain into the bar when the metadata is rebuilt underneath this section
        /// (an audio swap re-derives the artist and title, and the setup screen reloads for another
        /// difficulty): what the bar shows has to be what the map carries.
        /// </summary>
        private void reloadAudioGain()
        {
            audioGain.Value = Beatmap.Metadata.AudioGain;

            // A swapped audio file has its own peaks, so the clipping reading goes with it rather than
            // being kept from the song it replaced.
            analyseTrackPeaks();
        }

        /// <summary>
        /// Writes the bar's value onto the map, rebuilds the track from it so the change is audible now,
        /// and persists the whole set. Called once per COMMIT of the bar, never per pixel of a drag.
        /// </summary>
        /// <remarks>
        /// <para>THE GAIN BELONGS TO THE SONG, NOT TO THE DIFFICULTY, so it is written to every
        /// difficulty of the set in the same breath (the reasoning <c>MetadataSection</c> already applies
        /// to the language, and the same shape): one audio file is shared by the whole set, and a set
        /// whose difficulties disagreed about how loud it is would sound different depending on which
        /// difficulty a player picked. There is deliberately no "apply to all" checkbox to opt out of - a
        /// gain can only be right once for the file it scales.</para>
        ///
        /// <para>AND THE WHOLE SET IS PERSISTED, including the difficulty that is open. The open one goes
        /// through <c>editor.Save()</c> like every other resource edit here (see
        /// <see cref="changeResource"/>): saving the OTHERS while leaving the open one on an unsaved edit
        /// left a set that disagreed with itself the moment the mapper discarded. Not undoable, by the
        /// same deliberate convention the rest of this section follows.</para>
        ///
        /// <para>Every write goes to a DETACHED beatmap - the set graph the editor's own working beatmap
        /// carries, whose entry for the open difficulty is that working beatmap's own
        /// <c>BeatmapInfo</c> - because a realm-managed object written outside a write transaction
        /// throws.</para>
        /// </remarks>
        private void applyAudioGain(double gain)
        {
            var working = currentWorkingBeatmap.Value;

            if (Beatmap.Metadata.AudioGain == gain)
                return;

            Beatmap.Metadata.AudioGain = gain;
            clippingIndicator.Gain = gain;

            // AND HEARD NOW, not on the next load: the gain is applied to the AUDIO itself (see
            // ScaledAudio), so the only way a released slider can be audible is to rebuild the track from
            // the newly scaled audio. That is the same reload a swapped audio file uses, and it carries
            // the playhead over, so the take restarts a few hundred milliseconds later at the same
            // moment it was at - quiet enough to compare a gain by ear, which is the whole point of a
            // slider.
            music.ReloadCurrentTrack();

            foreach (var difficulty in working.BeatmapSetInfo.Beatmaps)
            {
                if (difficulty.Equals(Beatmap.BeatmapInfo))
                    continue;

                difficulty.Metadata.AudioGain = gain;

                // Persisted here rather than left for the mapper's next save: these are OTHER files, and
                // the editor's save below only ever writes the one it has open. Note that this triggers a
                // full save flow per difficulty, including a difficulty calculation, which is why nothing
                // upstream of here may fire per slider value.
                try
                {
                    var target = beatmaps.GetWorkingBeatmap(difficulty);

                    // The cached working beatmap may carry its own detached copy of the difficulty, and
                    // that copy is the one a difficulty switch would build a track from. Both, or the two
                    // disagree about how loud the song is until something invalidates the cache.
                    target.Metadata.AudioGain = gain;

                    beatmaps.Save(difficulty, target.GetPlayableBeatmap(difficulty.Ruleset), target.GetSkin(), target.Storyboard);
                }
                catch (Exception e)
                {
                    Logger.Error(e, $@"Failed to sync the audio gain to {difficulty.GetDisplayTitle()}");
                }
            }

            editor?.Save();
        }

        /// <summary>
        /// Puts a newly chosen image in as the background: into every difficulty of the set while the
        /// per-difficulty tick is off (the default, and also how a set with separate backgrounds is
        /// brought back to one), or into the open difficulty only while it is on.
        /// </summary>
        public bool ChangeBackgroundImage(FileInfo source)
            => ChangeBackgroundImage(source, applyToAllDifficulties: !separateBackgroundCheckBox.Current.Value);

        public bool ChangeBackgroundImage(FileInfo source, bool applyToAllDifficulties)
        {
            if (!source.Exists)
                return false;

            changeResource(source, applyToAllDifficulties, @"bg",
                working => working.BeatmapInfo.Metadata.BackgroundFile,
                writeBackgroundFilename);

            refreshBackgroundDisplays();
            return true;
        }

        /// <summary>
        /// The background the set shares: the file a strict majority of its difficulties name, falling
        /// back to the first difficulty's (the first, in the set's order, that names one). Empty when no
        /// difficulty names one.
        /// </summary>
        /// <remarks>
        /// Counted over the WHOLE set rather than over the open difficulty's siblings, because the tick is
        /// derived from this and must not be symmetric: in a two-difficulty set with two different images,
        /// "the siblings' image" differs from each difficulty's own, so both would read ticked. Over the
        /// whole set a two-way split has no majority and the first difficulty's image is the shared one, so
        /// exactly the other difficulty reads as separate. The consequence, stated so nobody is surprised:
        /// a mapper who ticks the FIRST difficulty of a two-difficulty set and picks an image sees the tick
        /// on the second difficulty after a reload instead, since nothing is stored to say which was ticked.
        /// </remarks>
        public string SharedBackgroundFile
        {
            get
            {
                var working = currentWorkingBeatmap.Value;
                var difficulties = working.BeatmapSetInfo.Beatmaps;

                // The open difficulty is read off the working beatmap, the copy every write goes to.
                string[] named = difficulties.Select(b => b.Equals(working.BeatmapInfo) ? working.BeatmapInfo : b)
                                             .Select(b => b.Metadata.BackgroundFile).Where(f => !string.IsNullOrEmpty(f)).ToArray();

                if (named.Length == 0)
                    return string.Empty;

                var majority = named.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() * 2 > difficulties.Count);
                return majority?.First() ?? named[0];
            }
        }

        /// <summary>
        /// Whether the open difficulty carries a background of its own: it names an image the set holds
        /// and that image is not <see cref="SharedBackgroundFile"/>. This is what the per-difficulty tick
        /// is derived from, so there is nothing new in the file format and nothing for the server to read.
        /// A difficulty naming nothing (an older save that inherits its siblings' image) is not separate.
        /// </summary>
        public bool HasSeparateBackground
        {
            get
            {
                var working = currentWorkingBeatmap.Value;

                if (working.BeatmapSetInfo.Beatmaps.Count <= 1)
                    return false;

                string own = working.BeatmapInfo.Metadata.BackgroundFile;
                string shared = SharedBackgroundFile;

                return !string.IsNullOrEmpty(own) && working.BeatmapSetInfo.GetFile(own) != null
                                                  && !string.IsNullOrEmpty(shared)
                                                  && !string.Equals(own, shared, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Points the open difficulty back at <see cref="SharedBackgroundFile"/> (what unticking the
        /// per-difficulty tick does), through the same path a chosen file takes, so the image it stops
        /// naming is deleted exactly when no difficulty names it any more. No-op (false) when the
        /// difficulty is not on a separate background.
        /// </summary>
        public bool ResyncToSharedBackground()
        {
            if (!HasSeparateBackground)
                return false;

            string shared = SharedBackgroundFile;

            changeResource(null, false, @"bg",
                working => working.BeatmapInfo.Metadata.BackgroundFile,
                writeBackgroundFilename,
                existingFilename: shared);

            // Show the shared file in the chooser without running a change for it: the rollback guard
            // is exactly "set the displayed value, touch nothing".
            rollingBackBackgroundChange = true;
            backgroundChooser.Current.Value = new FileInfo(shared);
            rollingBackBackgroundChange = false;

            refreshBackgroundDisplays();
            return true;
        }

        private static void writeBackgroundFilename(BeatmapInfo info, WorkingBeatmap working, string? name)
        {
            info.Metadata.BackgroundFile = name.AsNonNull();
            working.Metadata.BackgroundFile = name.AsNonNull();
        }

        private void refreshBackgroundDisplays()
        {
            backgroundPreview.UpdateBackground();
            editor?.ApplyToBackground(bg => ((EditorBackgroundScreen)bg).RefreshBackgroundAsync());
        }

        private void separateBackgroundTickChanged(ValueChangedEvent<bool> tick)
        {
            updateBackgroundHint();

            // Ticking changes nothing until a file is chosen; unticking re-syncs to the shared image.
            if (syncingSeparateBackgroundTick || tick.NewValue)
                return;

            ResyncToSharedBackground();
        }

        private void updateBackgroundHint()
            => backgroundChooser.HintText = separateBackgroundCheckBox.Current.Value ? BACKGROUND_HINT_THIS_DIFFICULTY : BACKGROUND_HINT_ALL_DIFFICULTIES;

        public bool ChangeVideo(FileInfo? source)
        {
            if (source != null && !source.Exists)
                return false;

            double offset = currentWorkingBeatmap.Value.Storyboard.PrimaryVideo?.StartTime ?? 0;
            changeResource(source, true, @"video",
                working => working.Storyboard.PrimaryVideo?.Path ?? string.Empty,
                (_, working, name) => setVideo(working.Storyboard, name, offset));

            // A swap keeps the offset, so the box's value stands; a clear leaves no video to offset,
            // so it empties and goes dead.
            updateVideoOffsetDisplay();

            videoPreview.UpdateVideo();
            editor?.ApplyToBackground(bg => ((EditorBackgroundScreen)bg).RefreshBackgroundAsync());
            return true;
        }

        /// <summary>
        /// Re-times the map's background video against the song: <paramref name="offsetMs"/> is the
        /// song position at which the video's first frame plays, so a positive value starts the video
        /// LATER than the song. No-op (returning false) on a map with no video.
        /// </summary>
        public bool ChangeVideoOffset(int offsetMs)
        {
            var storyboard = currentWorkingBeatmap.Value.Storyboard;

            if (storyboard.PrimaryVideo == null)
                return false;

            string filename = storyboard.PrimaryVideo.Path;
            var working = currentWorkingBeatmap.Value;
            var targets = working.BeatmapSetInfo.Beatmaps.Where(b => !b.Equals(working.BeatmapInfo))
                .Select(b => (Info: b, Working: beatmaps.GetWorkingBeatmap(b))).ToArray();
            foreach (var target in targets)
                setVideo(target.Working.Storyboard, filename, offsetMs);
            setVideo(storyboard, filename, offsetMs);
            clearSharedVideos();
            foreach (var target in targets)
                beatmaps.Save(target.Info, target.Working.GetPlayableBeatmap(target.Info.Ruleset), target.Working.GetSkin(), target.Working.Storyboard);
            ((IWorkingBeatmapCache)beatmaps).Invalidate(working.BeatmapSetInfo);

            // The thumbnail loops the clip free-running and cannot show a sync, but it is rebuilt for
            // consistency with the other resource edits. The editor BACKGROUND is the surface that
            // actually plays the video against the track, and only this rebuild picks the new timing up.
            videoPreview.UpdateVideo();
            editor?.ApplyToBackground(bg => ((EditorBackgroundScreen)bg).RefreshBackgroundAsync());

            // Same reason the file swaps save immediately (see changeResource): the editor's change
            // handler cannot see storyboard mutations, so an unsaved offset would vanish silently.
            // Not undoable, by the same deliberate convention as the other resource edits.
            editor?.Save();
            return true;
        }

        /// <summary>
        /// Points a beatmap's storyboard at a background video file, or clears the video when
        /// <paramref name="filename"/> is null.
        /// </summary>
        /// <remarks>
        /// <see cref="StoryboardVideo.StartTime"/> (the video's offset against the song) is get-only,
        /// so this and <see cref="ApplyVideoOffsetChange"/> REPLACE the element rather than mutate it.
        /// Both keep the video layer holding exactly one video, at its head:
        /// <see cref="Storyboard.PrimaryVideo"/> is simply the first one, so a leftover element would
        /// quietly become the map's video.
        ///
        /// <para>A file swap CARRIES THE CURRENT OFFSET FORWARD. Swapping is how a mapper replaces a
        /// clip with a re-encode or a cleaner rip of the same video, and throwing away the sync they
        /// already dialled in every time they touch the file picker is a destructive edit nobody asked
        /// for (the same reasoning as <see cref="ApplyAudioTrackChange"/> and the mapper's metadata).
        /// The offset has its own field for when they do want it changed.</para>
        /// </remarks>
        public static void ApplyVideoChange(Storyboard storyboard, string? filename)
            => setVideo(storyboard, filename, storyboard.PrimaryVideo?.StartTime ?? 0);

        /// <summary>
        /// Re-times an existing background video against the song. Deliberately a no-op on a map with
        /// no video: an offset must never synthesise a video element out of nothing.
        /// </summary>
        public static void ApplyVideoOffsetChange(Storyboard storyboard, int offsetMs)
        {
            if (storyboard.PrimaryVideo is StoryboardVideo video)
                setVideo(storyboard, video.Path, offsetMs);
        }

        private static void setVideo(Storyboard storyboard, string? filename, double offsetMs)
        {
            var videoLayer = storyboard.GetLayer(@"Video");
            videoLayer.Elements.RemoveAll(elem => elem is StoryboardVideo);

            if (filename != null)
                videoLayer.Elements.Insert(0, new StoryboardVideo(StoryboardElementSource.Beatmap, filename, offsetMs));
        }

        private void commitVideoOffset()
        {
            var video = currentWorkingBeatmap.Value.Storyboard.PrimaryVideo;

            if (video == null)
            {
                updateVideoOffsetDisplay();
                return;
            }

            string text = videoOffsetBox.Current.Value?.Trim() ?? string.Empty;

            // An emptied box reads as "no offset"; anything unparseable restores what the map
            // actually carries rather than silently rewriting it to something the user did not type.
            int offset = 0;

            if (text.Length > 0 && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset))
            {
                updateVideoOffsetDisplay();
                return;
            }

            if (offset != (int)Math.Round(video.StartTime))
                ChangeVideoOffset(offset);

            // Re-read from the model so the box shows the normalised value ("-0", "  12 ") rather
            // than the raw text.
            updateVideoOffsetDisplay();
        }

        private void updateVideoOffsetDisplay()
        {
            var video = currentWorkingBeatmap.Value.Storyboard.PrimaryVideo;

            // A disabled bindable throws when written, so the value goes in before the box is closed.
            videoOffsetBox.Current.Disabled = false;
            videoOffsetBox.Current.Value = video == null
                ? string.Empty
                : ((int)Math.Round(video.StartTime)).ToString(CultureInfo.InvariantCulture);
            videoOffsetBox.Current.Disabled = video == null;
        }

        public bool ChangeAudioTrack(FileInfo source, bool applyToAllDifficulties)
        {
            if (!source.Exists)
                return false;

            string artist;
            string title;

            try
            {
                using (var tagSource = TagLibUtils.GetTagLibFile(source.FullName))
                {
                    artist = tagSource.Tag.JoinedAlbumArtists ?? tagSource.Tag.JoinedPerformers;
                    title = tagSource.Tag.Title;
                }
            }
            catch (Exception e)
            {
                Logger.Error(e, "The selected audio track appears to be corrupted. Please select another one.");
                return false;
            }

            changeResource(source, applyToAllDifficulties, @"audio",
                working => working.BeatmapInfo.Metadata.AudioFile,
                (info, working, name) =>
                {
                    ApplyAudioTrackChange(info.Metadata, name.AsNonNull(), artist, title);
                    if (!ReferenceEquals(info.Metadata, working.Metadata))
                        ApplyAudioTrackChange(working.Metadata, name.AsNonNull(), artist, title);
                });

            music.ReloadCurrentTrack();
            setupScreen.MetadataChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Points a beatmap's metadata at a newly chosen audio file, seeding artist and title from
        /// that file's tags ONLY when the beatmap had no audio at all.
        /// </summary>
        /// <remarks>
        /// The tag seeding exists for the moment a brand new (empty) beatmap is given its song: the
        /// audio file's tags are then the only thing that knows what the song is. On a beatmap that
        /// already HAS a track this is a SWAP (a re-encode, a cleaner master, a different mix), and
        /// the mapper's authored artist and title must survive it: silently renaming the map to
        /// whatever the replacement file's tags happen to say (frequently blank, mojibake, or a rip
        /// tool's boilerplate) is a destructive edit nobody asked for, and it changes what the map
        /// is called on song select and on the leaderboards.
        /// </remarks>
        /// <param name="metadata">The metadata to update. Read before written, so pass it as-is.</param>
        /// <param name="newAudioFilename">The filename the audio now lives under in the beatmap set.</param>
        /// <param name="tagArtist">Artist read from the new file's tags, if any.</param>
        /// <param name="tagTitle">Title read from the new file's tags, if any.</param>
        public static void ApplyAudioTrackChange(BeatmapMetadata metadata, string newAudioFilename, string? tagArtist, string? tagTitle)
        {
            bool seedFromTags = string.IsNullOrEmpty(metadata.AudioFile);

            metadata.AudioFile = newAudioFilename;

            if (!seedFromTags)
                return;

            if (!string.IsNullOrWhiteSpace(tagArtist))
            {
                metadata.ArtistUnicode = tagArtist;
                metadata.Artist = MetadataUtils.StripNonRomanisedCharacters(metadata.ArtistUnicode);
            }

            if (!string.IsNullOrEmpty(tagTitle))
            {
                metadata.TitleUnicode = tagTitle;
                metadata.Title = MetadataUtils.StripNonRomanisedCharacters(metadata.TitleUnicode);
            }
        }

        private void changeResource(
            FileInfo? source,
            bool applyToAllDifficulties,
            string baseFilename,
            Func<WorkingBeatmap, string> readOldFilenameFrom,
            Action<BeatmapInfo, WorkingBeatmap, string?> writeNewFilenameTo,
            string? existingFilename = null)
        {
            var current = currentWorkingBeatmap.Value;
            var set = current.BeatmapSetInfo;

            // Resolve the difficulties before changing files or saving any of them. Cached working
            // beatmaps have detached metadata, so update both copies before encoding the set.
            var infos = set.Beatmaps.Where(info => !info.Equals(current.BeatmapInfo)).Append(current.BeatmapInfo);
            var difficulties = infos.Select(info => (Info: info, Working: info.Equals(current.BeatmapInfo)
                ? current
                : beatmaps.GetWorkingBeatmap(info))).ToArray();
            var targets = difficulties.Where(d => applyToAllDifficulties || d.Info.Equals(current.BeatmapInfo)).ToArray();
            var oldFiles = targets.Select(d => set.GetFile(readOldFilenameFrom(d.Working)))
                                  .Where(f => f != null).Distinct().ToArray();

            // A file the set already holds (a re-sync onto the shared background) is pointed at, not added.
            string? newFilename = existingFilename;

            if (source != null && existingFilename == null)
            {
                // Choose a new filename that doesn't clash with any other existing files.
                newFilename = $@"{baseFilename}{source.Extension.ToLowerInvariant()}";

                if (set.GetFile(newFilename) != null)
                {
                    string[] existingFilenames = set.Files.Select(f => f.Filename).Where(f =>
                        f.StartsWith(baseFilename, StringComparison.OrdinalIgnoreCase) &&
                        f.EndsWith(source.Extension, StringComparison.OrdinalIgnoreCase)).ToArray();
                    newFilename = NamingUtils.GetNextBestFilename(existingFilenames, newFilename);
                }

                using (var stream = source.OpenRead())
                    beatmaps.AddFile(set, stream, newFilename);
            }

            foreach (var target in targets)
                writeNewFilenameTo(target.Info, target.Working, newFilename);

            if (baseFilename == "video")
                clearSharedVideos();

            // A video may also be the soundtrack (e.g. an imported MP4). Only remove files
            // once no difficulty references them as audio, an image, or a storyboard resource.
            foreach (var oldFile in oldFiles)
            {
                string filename = oldFile!.Filename;
                bool stillUsed = difficulties.Any(d =>
                    string.Equals(d.Working.Metadata.AudioFile, filename, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.Working.Metadata.BackgroundFile, filename, StringComparison.OrdinalIgnoreCase) ||
                    d.Working.Storyboard.Layers.Any(l => l.Elements.Any(e => string.Equals(e.Path, filename, StringComparison.OrdinalIgnoreCase))));
                if (!stillUsed)
                    beatmaps.DeleteFile(set, oldFile);
            }

            foreach (var target in targets.Where(d => !d.Info.Equals(current.BeatmapInfo)))
                beatmaps.Save(target.Info, target.Working.GetPlayableBeatmap(target.Info.Ruleset), target.Working.GetSkin(), target.Working.Storyboard);

            ((IWorkingBeatmapCache)beatmaps).Invalidate(set);

            // editor change handler cannot be aware of any file changes or other difficulties having their metadata modified.
            // for simplicity's sake, trigger a save when changing any resource to ensure the change is correctly saved.
            editor?.Save();
        }

        private void clearSharedVideos()
        {
            var working = currentWorkingBeatmap.Value;
            var set = working.BeatmapSetInfo;
            // Once video declarations are written to every difficulty, remove any shared .osb
            // video declarations so they cannot play a second clip or resurrect a cleared video.
            foreach (var file in set.Files.Where(f => f.Filename.EndsWith(".osb", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                using var stream = working.GetStream(set.GetPathForFile(file.Filename)!);
                if (stream == null)
                    continue;
                using var reader = new LineBufferedReader(stream);
                using var empty = new MemoryStream();
                using var emptyReader = new LineBufferedReader(empty);
                var storyboard = new LegacyStoryboardDecoder().Decode(emptyReader, reader);
                if (storyboard.GetLayer("Video").Elements.RemoveAll(e => e is StoryboardVideo) == 0)
                    continue;

                using var encoded = new MemoryStream();
                using (var writer = new StreamWriter(encoded, Encoding.UTF8, 1024, leaveOpen: true))
                    new LegacyStoryboardEncoder(storyboard).EncodeStandaloneStoryboard(writer);
                encoded.Position = 0;
                beatmaps.AddFile(set, encoded, file.Filename);
            }
        }

        // to avoid scaring users, both background & audio choosers use fake `FileInfo`s with user-friendly filenames
        // when displaying an imported beatmap rather than the actual SHA-named file in storage.
        // however, that means that when a background or audio file is chosen that is broken or doesn't exist on disk when switching away from the fake files,
        // the rollback could enter an infinite loop, because the fake `FileInfo`s *also* don't exist on disk - at least not in the fake location they indicate.
        // to circumvent this issue, just allow rollback to proceed always without actually running any of the change logic to ensure visual consistency.
        // note that this means that `Change{BackgroundImage,AudioTrack}()` are required to not have made any modifications to the beatmap files
        // (or at least cleaned them up properly themselves) if they return `false`.
        private bool rollingBackBackgroundChange;
        private bool rollingBackVideoChange;
        private bool rollingBackAudioChange;

        private void backgroundChanged(ValueChangedEvent<FileInfo?> file)
        {
            if (rollingBackBackgroundChange)
                return;

            if (file.NewValue == null || !ChangeBackgroundImage(file.NewValue))
            {
                rollingBackBackgroundChange = true;
                backgroundChooser.Current.Value = file.OldValue;
                rollingBackBackgroundChange = false;
            }
        }

        private void videoChanged(ValueChangedEvent<FileInfo?> file)
        {
            if (rollingBackVideoChange)
                return;

            if (!ChangeVideo(file.NewValue))
            {
                rollingBackVideoChange = true;
                videoChooser.Current.Value = file.OldValue;
                rollingBackVideoChange = false;
            }
        }

        private void audioTrackChanged(ValueChangedEvent<FileInfo?> file)
        {
            if (rollingBackAudioChange)
                return;

            if (file.NewValue == null || !ChangeAudioTrack(file.NewValue, audioTrackChooser.ApplyToAllDifficulties.Value))
            {
                rollingBackAudioChange = true;
                audioTrackChooser.Current.Value = file.OldValue;
                rollingBackAudioChange = false;
            }
        }
    }
}
