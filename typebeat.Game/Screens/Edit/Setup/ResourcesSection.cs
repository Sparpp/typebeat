// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Framework.Logging;
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
        private FormBeatmapFileSelector backgroundChooser = null!;
        private FormBeatmapFileSelector videoChooser = null!;
        private FormNumberBox videoOffsetBox = null!;

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
