// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The type!beat-specific editor setup section: a global lyric-vs-song offset (baked into the
    /// map data, unlike the per-player LyricOffsetMs preference) and an in-editor auto-timer that
    /// aligns a lyrics file to the map's audio and replaces the lines.
    /// </summary>
    public partial class TypeBeatSetupSection : SetupSection
    {
        public override LocalisableString Title => "type!beat";

        [Resolved]
        private IBindable<WorkingBeatmap> working { get; set; } = null!;

        [Resolved(CanBeNull = true)]
        private ILyricMapImporter? importer { get; set; }

        [Resolved(CanBeNull = true)]
        private INotificationOverlay? notifications { get; set; }

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        private FormNumberBox beatdropBox = null!;
        private FormNumberBox offsetBox = null!;
        private FormFileSelector lyricsSelector = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            Children = new Drawable[]
            {
                beatdropBox = new FormNumberBox(allowDecimals: true)
                {
                    Caption = "Intro beatdrop (ms)",
                    HintText = "Optional. Flags this map to soundtrack the game intro: playback is timed so this moment lands exactly on the main menu reveal. Leave empty to unset.",
                    PlaceholderText = "unset",
                },
                new FormButton
                {
                    Caption = "Stamp the beatdrop at the editor's current playhead position",
                    ButtonText = "Set @ playhead",
                    Action = () => Beatmap.IntroBeatdrop.Value = Math.Round(editorClock.CurrentTime),
                },
                offsetBox = new FormNumberBox(allowDecimals: true)
                {
                    Caption = "Shift all timings (ms)",
                    HintText = "Positive = lyrics later. Bakes a global offset into the map (all lines and words move together).",
                    PlaceholderText = "e.g. -50",
                },
                new FormButton
                {
                    Caption = "Apply offset to the whole map",
                    ButtonText = "Shift timings",
                    Action = applyShift,
                },
                lyricsSelector = new FormFileSelector(".txt", ".lrc")
                {
                    Caption = "Lyrics file",
                    PlaceholderText = "Click to select a .txt / .lrc lyrics file",
                },
                new FormButton
                {
                    Caption = "Auto-time the lyrics to this song's audio",
                    ButtonText = "Generate timing",
                    Action = runImport,
                },
            };

            beatdropBox.OnCommit += (_, _) => commitBeatdrop();
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Beatmap.IntroBeatdrop.BindValueChanged(
                drop => beatdropBox.Current.Value = drop.NewValue is double d ? d.ToString("0", CultureInfo.InvariantCulture) : string.Empty, true);
        }

        private void commitBeatdrop()
        {
            string text = beatdropBox.Current.Value?.Trim() ?? string.Empty;

            if (text.Length == 0)
            {
                Beatmap.IntroBeatdrop.Value = null;
                return;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed >= 0)
                Beatmap.IntroBeatdrop.Value = Math.Round(parsed);
            else
            {
                // Invalid input: restore the current value's display.
                var current = Beatmap.IntroBeatdrop.Value;
                beatdropBox.Current.Value = current is double d ? d.ToString("0", CultureInfo.InvariantCulture) : string.Empty;
            }
        }

        private void applyShift()
        {
            string raw = offsetBox.Current.Value?.Trim() ?? string.Empty;

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double delta))
            {
                notify("Enter a number of milliseconds to shift by.");
                return;
            }

            if (delta == 0)
                return;

            TypeBeatEditorOperations.ShiftAllTimes(Beatmap, delta);
            offsetBox.Current.Value = string.Empty;
            notify($"Shifted all lyric timings by {delta:0.##} ms.");
        }

        private CancellationTokenSource? importCancellation;

        private void runImport()
        {
            if (importer == null)
            {
                notify("The lyric aligner is not available in this build.");
                return;
            }

            string audioFilename = working.Value.Metadata.AudioFile;

            if (string.IsNullOrEmpty(audioFilename))
            {
                notify("Set the song's audio first (Resources section), then generate timing.");
                return;
            }

            var lyricsFile = lyricsSelector.Current.Value;

            if (lyricsFile == null || !lyricsFile.Exists)
            {
                notify("Select a lyrics file (.txt or .lrc) first.");
                return;
            }

            string? audioStoragePath = working.Value.BeatmapSetInfo.GetPathForFile(audioFilename);

            if (audioStoragePath == null)
            {
                notify("Couldn't locate the map's audio file in storage.");
                return;
            }

            // Copy the stored (hashed) audio out to a real temp path for the aligner subprocess.
            string tempAudio;

            try
            {
                tempAudio = Path.Combine(Path.GetTempPath(), "typebeat_editor_align", Guid.NewGuid().ToString("N") + Path.GetExtension(audioFilename));
                Directory.CreateDirectory(Path.GetDirectoryName(tempAudio)!);

                using (var source = working.Value.GetStream(audioStoragePath))
                using (var dest = File.Create(tempAudio))
                    source.CopyTo(dest);
            }
            catch (Exception e)
            {
                notify($"Couldn't read the map's audio: {e.Message}");
                return;
            }

            string lyricsContent = File.ReadAllText(lyricsFile.FullName);
            string artist = working.Value.Metadata.Artist;
            string title = working.Value.Metadata.Title;

            var progressNotification = new ProgressNotification
            {
                Text = "Aligning lyrics to audio…",
                State = ProgressNotificationState.Active,
            };
            notifications?.Post(progressNotification);

            importCancellation?.Cancel();
            importCancellation = new CancellationTokenSource();
            var token = importCancellation.Token;
            progressNotification.CancelRequested += () =>
            {
                importCancellation?.Cancel();
                return true;
            };

            Task.Run(async () =>
            {
                try
                {
                    var (result, timingJson) = await importer.ProduceTimingJsonAsync(
                        tempAudio, lyricsContent, artist, title,
                        line => progressNotification.Text = line,
                        token).ConfigureAwait(false);

                    if (!result.Success || timingJson == null)
                    {
                        progressNotification.State = ProgressNotificationState.Cancelled;
                        progressNotification.Text = $"Timing failed: {result.Error}";
                        return;
                    }

                    if (!TimingJsonLoader.TryParse(timingJson, out var lines) || lines.Count == 0)
                    {
                        progressNotification.State = ProgressNotificationState.Cancelled;
                        progressNotification.Text = "The alignment produced no usable lines.";
                        return;
                    }

                    Schedule(() =>
                    {
                        TypeBeatEditorOperations.ReplaceLines(Beatmap, lines, TypeBeatEditorOperations.InferGranularity(lines));
                        progressNotification.State = ProgressNotificationState.Completed;
                        progressNotification.Text = $"Imported {lines.Count} lyric lines.";
                    });
                }
                catch (Exception e)
                {
                    progressNotification.State = ProgressNotificationState.Cancelled;
                    progressNotification.Text = $"Timing failed: {e.Message}";
                }
                finally
                {
                    try { File.Delete(tempAudio); }
                    catch { /* best-effort */ }
                }
            }, token);
        }

        private void notify(string message) => notifications?.Post(new SimpleNotification { Text = message });

        protected override void Dispose(bool isDisposing)
        {
            importCancellation?.Cancel();
            base.Dispose(isDisposing);
        }
    }
}
