// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using typebeat.Game.Beatmaps;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Screens.Edit.Timing;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Screens.Edit
{
    /// <summary>
    /// What the View menu's Vocals Waveform toggle does (aligner version 12, where Demucs became an
    /// install of its own). A map that carries a vocals stem toggles between it and the full mix, as
    /// before (backlog 392). A map without one has its vocals SEPARATED by local Demucs the first
    /// time the toggle is turned on: the song is handed to Demucs, the stem it writes is added to the
    /// map set (as an import would have packaged it), and the waveform switches to it. Without
    /// Demucs installed the toggle is greyed out, its tooltip saying where to install it.
    ///
    /// <para>Availability is re-read every few seconds, so installing Demucs from Settings while the
    /// editor is open makes the toggle available without reopening the map.</para>
    /// </summary>
    public partial class VocalsWaveformSeparation : Component
    {
        /// <summary>The toggle's tooltip when Demucs is not installed.</summary>
        public static readonly string INSTALL_DEMUCS_TOOLTIP =
            $"This map has no vocals track. Install Demucs in {LocalAlignerVersion.SETTINGS_PATH} to separate the song's vocals and show them here.";

        /// <summary>The toggle's tooltip when Demucs's install never finished.</summary>
        public static readonly string REPAIR_DEMUCS_TOOLTIP =
            $"This map has no vocals track, and Demucs's install did not finish. Repair it in {LocalAlignerVersion.SETTINGS_PATH} to separate the song's vocals.";

        /// <summary>The toggle's tooltip when turning it on will run Demucs.</summary>
        public const string WILL_SEPARATE_TOOLTIP = "This map has no vocals track yet: turning this on separates the song's vocals with Demucs (a few minutes), then shows them.";

        /// <summary>The toggle's tooltip while Demucs is running.</summary>
        public const string SEPARATING_TOOLTIP = "Separating the song's vocals with Demucs...";

        private readonly TimingMenuItems items;
        private readonly EditorTimingSettings settings;
        private readonly IBindable<WorkingBeatmap> beatmap = new Bindable<WorkingBeatmap>();

        [Resolved(CanBeNull = true)]
        private ILocalAlignerManager? manager { get; set; }

        [Resolved(CanBeNull = true)]
        private INotificationOverlay? notifications { get; set; }

        [Resolved]
        private BeatmapManager beatmapManager { get; set; } = null!;

        private bool separating;

        public VocalsWaveformSeparation(TimingMenuItems items, EditorTimingSettings settings, IBindable<WorkingBeatmap> beatmap)
        {
            this.items = items;
            this.settings = settings;
            this.beatmap.BindTo(beatmap);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            beatmap.BindValueChanged(_ => Refresh(), true);
            settings.VocalsWaveform.BindValueChanged(onToggled);
            Scheduler.AddDelayed(Refresh, 3000, true);
        }

        /// <summary>Whether the map carries a stem the waveform can show (one that decodes).</summary>
        private bool hasStem => beatmap.Value?.VocalsStemWaveform != null;

        /// <summary>Re-reads what the toggle can do and says so on it.</summary>
        public void Refresh()
        {
            if (separating)
                items.SetVocalsWaveformAvailability(false, SEPARATING_TOOLTIP);
            else if (hasStem)
                items.SetVocalsWaveformAvailability(true, default(LocalisableString));
            else if (manager?.DemucsInstalled == true)
                items.SetVocalsWaveformAvailability(true, WILL_SEPARATE_TOOLTIP);
            else
                items.SetVocalsWaveformAvailability(false, manager?.DemucsNeedsRepair == true ? REPAIR_DEMUCS_TOOLTIP : INSTALL_DEMUCS_TOOLTIP);
        }

        private void onToggled(ValueChangedEvent<bool> e)
        {
            if (!e.NewValue || hasStem)
                return;

            // Nothing to show yet: the toggle stays off while Demucs runs, and comes on when it is done.
            settings.VocalsWaveform.Value = false;
            startSeparation();
        }

        private void startSeparation()
        {
            var demucs = manager;

            if (separating || demucs == null || !demucs.DemucsInstalled)
                return;

            var working = beatmap.Value;
            string? audioFile = working?.Metadata.AudioFile;
            string? storagePath = string.IsNullOrEmpty(audioFile) ? null : working!.BeatmapSetInfo.GetPathForFile(audioFile);

            if (working == null || storagePath == null)
            {
                notifications?.Post(new SimpleErrorNotification { Text = "This map has no audio file to separate the vocals from." });
                return;
            }

            // The song goes to Demucs under its content hash (the storage path's last part): the
            // aligner's work dir caches a separation by the file's name, so two maps' "audio.mp3"
            // must never meet there.
            string dir = Path.Combine(Path.GetTempPath(), "typebeat_vocals", Guid.NewGuid().ToString("N"));
            string input = Path.Combine(dir, Path.GetFileName(storagePath) + Path.GetExtension(audioFile));

            try
            {
                Directory.CreateDirectory(dir);

                using (var source = working.GetStream(storagePath))
                using (var copy = File.Create(input))
                    source.CopyTo(copy);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not prepare the song for vocal separation");
                notifications?.Post(new SimpleErrorNotification { Text = $"Could not read the map's audio to separate its vocals: {ex.Message}" });
                deleteQuietly(dir);
                return;
            }

            separating = true;
            Refresh();

            var set = working.BeatmapSetInfo;
            var notification = new ProgressNotification
            {
                Text = "Separating the song's vocals with Demucs...",
                CompletionText = "Vocals separated. View > Vocals waveform now shows them.",
                State = ProgressNotificationState.Active,
            };

            notifications?.Post(notification);

            Task.Run(async () =>
            {
                VocalsSeparationResult result;

                try
                {
                    result = await demucs.SeparateVocalsAsync(input, line => notification.Text = line, notification.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Vocal separation failed");
                    result = VocalsSeparationResult.Fail(ex.Message);
                }

                Schedule(() => finish(result, set, notification, dir));
            });
        }

        private void finish(VocalsSeparationResult result, BeatmapSetInfo set, ProgressNotification notification, string dir)
        {
            separating = false;

            try
            {
                if (!result.Success)
                {
                    notification.State = ProgressNotificationState.Cancelled;

                    if (!result.Cancelled)
                        notifications?.Post(new SimpleErrorNotification { Text = $"The vocals could not be separated: {result.Error}" });

                    return;
                }

                // Added to the map set as an import would have packaged it (backlog 392's file name),
                // so the waveform finds it and a submission carries it.
                using (var stream = File.OpenRead(result.StemPath!))
                    beatmapManager.AddFile(set, stream, Path.GetFileName(result.StemPath!));

                ((IWorkingBeatmapCache)beatmapManager).Invalidate(set);
                notification.State = ProgressNotificationState.Completed;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not add the separated vocals to the map");
                notification.State = ProgressNotificationState.Cancelled;
                notifications?.Post(new SimpleErrorNotification { Text = $"The vocals were separated but could not be added to the map: {ex.Message}" });
            }
            finally
            {
                deleteQuietly(dir);

                if (result.StemPath != null)
                    deleteQuietly(Path.GetDirectoryName(result.StemPath)!);

                Refresh();
            }

            if (hasStem)
                settings.VocalsWaveform.Value = true;
        }

        private static void deleteQuietly(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // A temp folder; the OS clears it eventually.
            }
        }
    }
}
