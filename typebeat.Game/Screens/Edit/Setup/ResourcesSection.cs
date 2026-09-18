// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
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
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;
using typebeat.Game.Models;
using typebeat.Game.Overlays;
using typebeat.Game.Screens.Backgrounds;
using typebeat.Game.Screens.Edit.Components;
using typebeat.Game.Storyboards;
using typebeat.Game.Utils;

namespace typebeat.Game.Screens.Edit.Setup
{
    public partial class ResourcesSection : SetupSection
    {
        /// <summary>Caption of the video offset control; also how tests find it among the section's boxes.</summary>
        public const string VIDEO_OFFSET_CAPTION = "Video offset (ms)";

        private FormBeatmapFileSelector audioTrackChooser = null!;
        private FormSliderBar<double> audioGainBar = null!;
        private AudioClippingIndicator clippingIndicator = null!;
        private FormBeatmapFileSelector backgroundChooser = null!;
        private FormBeatmapFileSelector videoChooser = null!;
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

        private readonly Bindable<EditorBeatmapSkin.SampleSet?> currentSampleSet = new Bindable<EditorBeatmapSkin.SampleSet?>();

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

            bool beatmapHasMultipleDifficulties = currentWorkingBeatmap.Value.BeatmapSetInfo.Beatmaps.Count > 1;

            Children = new Drawable[]
            {
                backgroundChooser = new FormBeatmapFileSelector(beatmapHasMultipleDifficulties, SupportedExtensions.IMAGE_EXTENSIONS)
                {
                    Caption = GameplaySettingsStrings.BackgroundHeader,
                    PlaceholderText = EditorSetupStrings.ClickToSelectBackground,
                },
                videoChooser = new FormBeatmapFileSelector(beatmapHasMultipleDifficulties, SupportedExtensions.VIDEO_EXTENSIONS)
                {
                    Caption = EditorSetupStrings.Video,
                    PlaceholderText = EditorSetupStrings.ClickToSelectVideo,
                    HintText = EditorSetupStrings.VideoHint,
                    AllowClear = true,
                },
                // Directly under the picker it re-times, and whole milliseconds only: the format's
                // offset field is int-parsed on decode, and a decimal would not error, it would drop
                // the video element on the next load. A number box that cannot type a "." makes that
                // unrepresentable (it can still type the leading "-", which is half the point).
                videoOffsetBox = new FormNumberBox(allowDecimals: false)
                {
                    Caption = VIDEO_OFFSET_CAPTION,
                    HintText = "Syncs the video to the song. Positive = the video starts later: its first frame plays this many milliseconds into the song. Negative starts it earlier. Whole milliseconds.",
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
                audioGainBar = new FormSliderBar<double>
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
                new FormSampleSetChooser
                {
                    Current = { BindTarget = currentSampleSet },
                },
                new FormSampleSet
                {
                    Current = { BindTarget = currentSampleSet },
                    SampleAddRequested = (file, targetName) =>
                    {
                        string actualFilename = string.Concat(targetName, file.Extension);
                        using var stream = file.OpenRead();
                        beatmaps.AddFile(currentWorkingBeatmap.Value.BeatmapSetInfo, stream, actualFilename);
                        return actualFilename;
                    },
                    SampleRemoveRequested = filename =>
                    {
                        var file = currentWorkingBeatmap.Value.BeatmapSetInfo.GetFile(filename);
                        if (file != null)
                            beatmaps.DeleteFile(currentWorkingBeatmap.Value.BeatmapSetInfo, file);
                    }
                },
            };

            backgroundChooser.PreviewContainer.Add(backgroundPreview);
            videoChooser.PreviewContainer.Add(videoPreview);

            if (!string.IsNullOrEmpty(currentWorkingBeatmap.Value.Metadata.BackgroundFile))
                backgroundChooser.Current.Value = new FileInfo(currentWorkingBeatmap.Value.Metadata.BackgroundFile);

            if (currentWorkingBeatmap.Value.Storyboard.PrimaryVideo is StoryboardVideo video)
                videoChooser.Current.Value = new FileInfo(video.Path);

            if (!string.IsNullOrEmpty(currentWorkingBeatmap.Value.Metadata.AudioFile))
                audioTrackChooser.Current.Value = new FileInfo(currentWorkingBeatmap.Value.Metadata.AudioFile);

            backgroundChooser.Current.BindValueChanged(backgroundChanged);
            videoChooser.Current.BindValueChanged(videoChanged);
            audioTrackChooser.Current.BindValueChanged(audioTrackChanged);

            // Committed (enter / focus loss), never per keystroke: applying an offset rebuilds the
            // whole editor background storyboard asynchronously.
            videoOffsetBox.OnCommit += (_, _) => commitVideoOffset();
            updateVideoOffsetDisplay();

            // The gain goes in as the bar is released rather than on a commit key, which is the same
            // "pick it, then it is applied" shape the language dropdown uses rather than the shared
            // Ctrl+S rule the sections' text boxes follow: there is no text to type, and unlike the
            // video offset above there is nothing expensive about applying it - one mixer write. State
            // is saved with it, so the change is undoable and Ctrl+S has nothing left to do.
            audioGain.Value = currentWorkingBeatmap.Value.Metadata.AudioGain;
            audioGain.BindValueChanged(gain => applyAudioGain(gain.NewValue));
            audioGainBar.Current.BindValueChanged(_ => Beatmap.SaveState());
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
            audioGain.Value = currentWorkingBeatmap.Value.Metadata.AudioGain;

            // A swapped audio file has its own peaks, so the clipping reading goes with it rather than
            // being kept from the song it replaced.
            analyseTrackPeaks();
        }

        /// <summary>
        /// Writes the bar's value onto the beatmap and lets the music controller re-read it, which is
        /// what makes a gain change audible in the editor the moment the bar moves instead of on the
        /// next load of the map.
        /// </summary>
        /// <remarks>
        /// THE GAIN BELONGS TO THE SONG, NOT TO THE DIFFICULTY, so it is written to every other
        /// difficulty of the set in the same breath (the reasoning <c>MetadataSection</c> already
        /// applies to the language, and the same shape): one audio file is shared by the whole set, and
        /// a set whose difficulties disagreed about how loud it is would sound different depending on
        /// which difficulty a player picked. There is deliberately no "apply to all" checkbox to opt
        /// out of - a gain can only be right once for the file it scales.
        /// </remarks>
        private void applyAudioGain(double gain)
        {
            if (currentWorkingBeatmap.Value.Metadata.AudioGain == gain)
                return;

            currentWorkingBeatmap.Value.Metadata.AudioGain = gain;
            music.RefreshBeatmapGain();
            clippingIndicator.Gain = gain;

            // AND HEARD NOW, not on the next load: the gain is applied to the AUDIO itself (see
            // ScaledAudio), so the only way a moving slider can be audible is to rebuild the track from
            // the newly scaled audio. That is the same reload a swapped audio file uses, and it carries
            // the playhead over, so the take restarts a few hundred milliseconds later at the same
            // moment it was at - quiet enough to compare a gain by ear, which is the whole point of a
            // slider.
            music.ReloadCurrentTrack();

            var working = currentWorkingBeatmap.Value;

            foreach (var difficulty in working.BeatmapSetInfo.Beatmaps)
            {
                if (difficulty.Equals(working.BeatmapInfo))
                    continue;

                difficulty.Metadata.AudioGain = gain;

                // Persisted now rather than left for the mapper's next save: these are OTHER files, and
                // the editor's save only ever writes the one it has open.
                try
                {
                    var target = beatmaps.GetWorkingBeatmap(difficulty);
                    beatmaps.Save(difficulty, target.GetPlayableBeatmap(difficulty.Ruleset), target.GetSkin(), target.Storyboard);
                }
                catch (Exception e)
                {
                    Logger.Error(e, $@"Failed to sync the audio gain to {difficulty.GetDisplayTitle()}");
                }
            }
        }

        public bool ChangeBackgroundImage(FileInfo source, bool applyToAllDifficulties)
        {
            if (!source.Exists)
                return false;

            changeResource(source, applyToAllDifficulties, @"bg",
                working => working.BeatmapInfo.Metadata.BackgroundFile,
                (working, name) => working.BeatmapInfo.Metadata.BackgroundFile = name.AsNonNull());

            backgroundPreview.UpdateBackground();
            editor?.ApplyToBackground(bg => ((EditorBackgroundScreen)bg).RefreshBackgroundAsync());
            return true;
        }

        public bool ChangeVideo(FileInfo? source, bool applyToAllDifficulties)
        {
            if (source != null && !source.Exists)
                return false;

            changeResource(source, applyToAllDifficulties, @"video",
                working => working.Storyboard.PrimaryVideo?.Path ?? string.Empty,
                (working, name) => ApplyVideoChange(working.Storyboard, name));

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

            ApplyVideoOffsetChange(storyboard, offsetMs);

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
                (working, name) => ApplyAudioTrackChange(working.BeatmapInfo.Metadata, name.AsNonNull(), artist, title));

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
            Action<WorkingBeatmap, string?> writeNewFilenameTo)
        {
            var set = currentWorkingBeatmap.Value.BeatmapSetInfo;
            var currentBeatmapInfo = currentWorkingBeatmap.Value.BeatmapInfo;

            var otherBeatmaps = set.Beatmaps.Where(b => !b.Equals(currentBeatmapInfo));

            // First, clean up files which will no longer be used.
            if (applyToAllDifficulties)
            {
                foreach (var b in set.Beatmaps)
                {
                    var working = beatmaps.GetWorkingBeatmap(b);
                    if (set.GetFile(readOldFilenameFrom(working)) is RealmNamedFileUsage otherExistingFile)
                        beatmaps.DeleteFile(set, otherExistingFile);
                }
            }
            else
            {
                RealmNamedFileUsage? oldFile = set.GetFile(readOldFilenameFrom(currentWorkingBeatmap.Value));

                if (oldFile != null)
                {
                    bool oldFileUsedInOtherDiff = false;

                    foreach (var b in otherBeatmaps)
                    {
                        var working = beatmaps.GetWorkingBeatmap(b);

                        if (readOldFilenameFrom(working) == oldFile.Filename)
                        {
                            oldFileUsedInOtherDiff = true;
                            break;
                        }
                    }

                    if (!oldFileUsedInOtherDiff)
                        beatmaps.DeleteFile(set, oldFile);
                }
            }

            string? newFilename = null;

            if (source != null)
            {
                // Choose a new filename that doesn't clash with any other existing files.
                newFilename = $"{baseFilename}{source.Extension}";

                if (set.GetFile(newFilename) != null)
                {
                    string[] existingFilenames = set.Files.Select(f => f.Filename).Where(f =>
                        f.StartsWith(baseFilename, StringComparison.OrdinalIgnoreCase) &&
                        f.EndsWith(source.Extension, StringComparison.OrdinalIgnoreCase)).ToArray();
                    newFilename = NamingUtils.GetNextBestFilename(existingFilenames, $@"{baseFilename}{source.Extension}");
                }

                using (var stream = source.OpenRead())
                    beatmaps.AddFile(set, stream, newFilename);
            }

            if (applyToAllDifficulties)
            {
                foreach (var b in otherBeatmaps)
                {
                    // save the difficulty to re-encode the .osu file, updating any reference of the old filename.
                    //
                    // note that this triggers a full save flow, including triggering a difficulty calculation.
                    // this is not a cheap operation and should be reconsidered in the future.
                    var beatmapWorking = beatmaps.GetWorkingBeatmap(b);
                    writeNewFilenameTo(beatmapWorking, newFilename);
                    beatmaps.Save(b, beatmapWorking.GetPlayableBeatmap(b.Ruleset), beatmapWorking.GetSkin(), beatmapWorking.Storyboard);
                }
            }

            writeNewFilenameTo(currentWorkingBeatmap.Value, newFilename);

            // editor change handler cannot be aware of any file changes or other difficulties having their metadata modified.
            // for simplicity's sake, trigger a save when changing any resource to ensure the change is correctly saved.
            editor?.Save();
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

            if (file.NewValue == null || !ChangeBackgroundImage(file.NewValue, backgroundChooser.ApplyToAllDifficulties.Value))
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

            if (!ChangeVideo(file.NewValue, videoChooser.ApplyToAllDifficulties.Value))
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
