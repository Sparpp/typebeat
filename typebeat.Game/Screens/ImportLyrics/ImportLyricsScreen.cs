// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using osu.Framework.Screens;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Screens.ImportLyrics
{
    /// <summary>
    /// The in-app "import a song" flow: drop an audio file and a lyrics file, confirm artist/title,
    /// choose the map's language,
    /// and the ruleset's <see cref="ILyricMapImporter"/> aligns + packages an .osz which is then
    /// imported. Files arrive via <see cref="AddFiles"/> (routed by <see cref="LyricImportManager"/>
    /// from global file drops). Esc cancels an in-flight import, killing the aligner process tree.
    ///
    /// <para>The LYRICS slot is optional. With audio alone the import skips the aligner and produces
    /// a BLANK map (audio + metadata, no lyric lines) whose words and timing are then authored in
    /// the editor; the button says so before it is pressed.</para>
    /// </summary>
    public partial class ImportLyricsScreen : OsuScreen
    {
        public override bool HideOverlaysOnEnter => true;

        /// <summary>
        /// The estimated vocals choice's label (backlog 354), shared with the editor's re-align so the two
        /// places say the same thing. Only meaningful with automatic alignment, so it is disabled without it.
        /// </summary>
        public const string ESTIMATED_VOCALS_LABEL = "estimated vocals (when the aligned words come out wrong, e.g. screamed or effect-heavy vocals: "
                                                     + "pace every line evenly from its [mm:ss.xx] stamp instead; remembered for this map)";

        /// <summary>
        /// Tooltip on the greyed-out automatic alignment choice when the local auto-aligner is not
        /// installed (backlog 409). The path is the one the settings overlay shows: the "Experimental"
        /// section, its "Lyric timing" heading, where the install button sits.
        /// </summary>
        public const string ALIGNER_NOT_INSTALLED_TOOLTIP = "Install the local auto-aligner in Settings > Experimental > Lyric timing to use automatic alignment.";

        /// <summary>As <see cref="ALIGNER_NOT_INSTALLED_TOOLTIP"/>, for an install that never finished (the button there reads "Repair").</summary>
        public const string ALIGNER_NEEDS_REPAIR_TOOLTIP = "The local auto-aligner's install is incomplete. Repair it in Settings > Experimental > Lyric timing to use automatic alignment.";

        /// <summary>As <see cref="ALIGNER_NOT_INSTALLED_TOOLTIP"/>, for a build with no aligner installer at all (headless, tests).</summary>
        public const string ALIGNER_UNAVAILABLE_TOOLTIP = "Automatic alignment is not available in this build.";

        [Resolved]
        private OsuGameBase game { get; set; } = null!;

        [Resolved(CanBeNull = true)]
        private ILyricMapImporter? importer { get; set; }

        [Resolved(CanBeNull = true)]
        private IDialogOverlay? dialogOverlay { get; set; }

        // Cached by typebeat.Desktop beside the importer; absent in headless scenes.
        [Resolved(CanBeNull = true)]
        private ILocalAlignerManager? alignerManager { get; set; }

        [Resolved(CanBeNull = true)]
        private BeatmapManager? beatmaps { get; set; }

        [Cached]
        private OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

        private readonly string[] initialFiles;

        private string? audioPath;
        private string? lyricsPath;

        private FileSlot audioSlot = null!;
        private FileSlot lyricsSlot = null!;
        private LabelledTextBox artistBox = null!;
        private LabelledTextBox titleBox = null!;
        private FormEnumDropdown<BeatmapLanguage> languageDropdown = null!;
        private AutomaticAlignmentCheckbox automaticAlignmentCheckbox = null!;
        private OsuCheckbox estimatedVocalsCheckbox = null!;
        private RoundedButton importButton = null!;
        private OsuSpriteText statusText = null!;
        private ImportProgressDisplay progressDisplay = null!;
        private Container contentContainer = null!;

        private CancellationTokenSource? importCancellation;
        private bool importing;
        private bool exitConfirmed;

        public ImportLyricsScreen(params string[] initialFiles)
        {
            this.initialFiles = initialFiles;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChild = contentContainer = new Container
            {
                Masking = true,
                CornerRadius = 10,
                RelativeSizeAxes = Axes.Both,
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Size = new Vector2(0.7f, 0.85f),
                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = colourProvider.Background4,
                    },
                    new OsuScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding { Horizontal = 50, Vertical = 40 },
                        Child = new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 16),
                            Children = new Drawable[]
                            {
                                new OsuSpriteText
                                {
                                    Text = "import a song",
                                    Font = OsuFont.TorusAlternate.With(size: 32, weight: FontWeight.SemiBold),
                                },
                                new OsuSpriteText
                                {
                                    Text = "drop an audio file (.mp3/.ogg/.wav) or video (.mp4) anywhere in the window, "
                                           + "plus a lyrics file (.txt/.lrc/.elrc/.ttml) if you have one.",
                                    Colour = colourProvider.Content2,
                                    Font = OsuFont.Default.With(size: 16),
                                },
                                audioSlot = new FileSlot("audio", "drop .mp3 / .ogg / .wav / .mp4"),
                                lyricsSlot = new FileSlot("lyrics (optional)", "drop .txt / .lrc / .elrc / .ttml, or import without for a blank map"),
                                artistBox = new LabelledTextBox { Label = "artist" },
                                titleBox = new LabelledTextBox { Label = "title" },
                                languageDropdown = new FormEnumDropdown<BeatmapLanguage>
                                {
                                    Caption = "language",
                                    HintText = "Choose the song's language for map metadata and lyric romanisation. Japanese kanji use dictionary readings; check unusual names and sung pronunciations in the editor.",
                                    Current = { Value = BeatmapLanguage.Unspecified },
                                },
                                automaticAlignmentCheckbox = new AutomaticAlignmentCheckbox
                                {
                                    RelativeSizeAxes = Axes.X,
                                    LabelText = "automatic alignment (time each word from the audio, slower, needs the local auto-aligner; off = use your [mm:ss.xx] line stamps)",
                                    Current = { Value = false },
                                },
                                estimatedVocalsCheckbox = new OsuCheckbox
                                {
                                    RelativeSizeAxes = Axes.X,
                                    LabelText = ESTIMATED_VOCALS_LABEL,
                                    Current = { Value = false },
                                },
                                importButton = new RoundedButton
                                {
                                    Text = "import",
                                    RelativeSizeAxes = Axes.X,
                                    Height = 50,
                                    Action = startImport,
                                    Enabled = { Value = false },
                                },
                                statusText = new OsuSpriteText
                                {
                                    Text = importer == null ? "lyric import is unavailable in this build." : string.Empty,
                                    Colour = colourProvider.Content2,
                                    Font = OsuFont.Default.With(size: 15),
                                },
                                progressDisplay = new ImportProgressDisplay { Alpha = 0 },
                            }
                        }
                    }
                }
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            languageDropdown.Current.BindValueChanged(_ => updateImportButton());

            // Estimated vocals only change how the aligner runs, so without automatic alignment there is
            // nothing for them to change: the choice goes dead (and off) rather than being silently ignored.
            automaticAlignmentCheckbox.Current.BindValueChanged(auto =>
            {
                if (!auto.NewValue)
                    estimatedVocalsCheckbox.Current.Value = false;
                estimatedVocalsCheckbox.Current.Disabled = !auto.NewValue;
            }, true);

            refreshAlignerAvailability();

            AddFiles(initialFiles);
        }

        /// <summary>The automatic alignment choice, exposed for tests.</summary>
        internal OsuCheckbox AutomaticAlignment => automaticAlignmentCheckbox;

        /// <summary>The estimated vocals choice, exposed for tests.</summary>
        internal OsuCheckbox EstimatedVocals => estimatedVocalsCheckbox;

        /// <summary>
        /// Why automatic alignment cannot run here, as the tooltip that tells the player what to do
        /// about it; null when the local auto-aligner is installed, ready and the version this build
        /// ships. Only an installed aligner can time words, so without one the import would fail
        /// AFTER the player pressed it. An installed one whose version does not match, older or newer,
        /// is refused by the importer too (backlog 410), so it greys the choice out the same way, with
        /// both versions and the place to update it (<see cref="LocalAlignerVersion.Advice(ILocalAlignerManager)"/>).
        /// </summary>
        public static string? AlignerUnavailableReason(ILocalAlignerManager? manager)
        {
            if (manager == null)
                return ALIGNER_UNAVAILABLE_TOOLTIP;

            if (manager.IsInstalled)
                return LocalAlignerVersion.Advice(manager);

            return manager.NeedsRepair ? ALIGNER_NEEDS_REPAIR_TOOLTIP : ALIGNER_NOT_INSTALLED_TOOLTIP;
        }

        /// <summary>
        /// Greys out (and unticks) the automatic alignment choice while the local auto-aligner is not
        /// installed or does not match this build's version, with a tooltip saying where to fix it.
        /// Re-read on entry and on resume, so a player who installs or updates from settings and comes
        /// back finds it enabled. The checkbox's own value binding then keeps the estimated vocals
        /// choice off and disabled with it.
        /// </summary>
        private void refreshAlignerAvailability()
        {
            string? reason = AlignerUnavailableReason(alignerManager);
            var current = automaticAlignmentCheckbox.Current;

            current.Disabled = false;

            if (reason != null)
            {
                current.Value = false;
                current.Disabled = true;
            }

            automaticAlignmentCheckbox.TooltipText = reason ?? default(LocalisableString);
        }

        /// <summary>
        /// Assigns each path to the audio or lyrics slot by extension (update thread). Called on entry
        /// with any files the screen was opened for and again for each subsequent drop while current.
        /// </summary>
        public void AddFiles(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                if (LyricImportExtensions.IsAudio(path))
                    setAudio(path);
                else if (LyricImportExtensions.IsLyrics(path))
                    setLyrics(path);
            }

            updateImportButton();
        }

        private void setAudio(string path)
        {
            audioPath = path;
            audioSlot.SetFile(System.IO.Path.GetFileName(path));

            if (importer != null && string.IsNullOrEmpty(artistBox.Current.Value) && string.IsNullOrEmpty(titleBox.Current.Value))
            {
                (string artist, string title) = importer.GuessArtistTitle(path);
                artistBox.Current.Value = artist;
                titleBox.Current.Value = title;
            }
        }

        private void setLyrics(string path)
        {
            lyricsPath = path;
            lyricsSlot.SetFile(System.IO.Path.GetFileName(path));
        }

        /// <summary>
        /// Audio alone is enough to import: without lyrics the result is a blank map. The button
        /// and the status line both say which of the two is about to happen, so "no lyrics" is a
        /// deliberate choice rather than something the user discovers afterwards.
        /// </summary>
        private void updateImportButton()
        {
            bool blank = string.IsNullOrEmpty(lyricsPath);

            importButton.Enabled.Value = !importing
                                         && importer != null
                                         && !string.IsNullOrEmpty(audioPath)
                                         && languageDropdown.Current.Value != BeatmapLanguage.Unspecified;

            importButton.Text = blank ? "import (blank map, no lyrics)" : "import";

            if (importing)
                return;

            if (importer == null)
                statusText.Text = "lyric import is unavailable in this build.";
            else if (!string.IsNullOrEmpty(audioPath) && languageDropdown.Current.Value == BeatmapLanguage.Unspecified)
                statusText.Text = "select the song's language to import.";
            else if (blank && !string.IsNullOrEmpty(audioPath))
                statusText.Text = "no lyrics file: this creates a blank map (song + metadata only) to write and time in the editor.";
            else
                statusText.Text = string.Empty;
        }

        private void startImport()
        {
            if (importing || importer == null || string.IsNullOrEmpty(audioPath)
                          || languageDropdown.Current.Value == BeatmapLanguage.Unspecified)
                return;

            // The aligner may have gone since the screen last looked. Re-reading it here unticks the
            // choice when it cannot run, so automatic alignment is only asked for when it can and no
            // path reaches the importer's "not installed" error.
            refreshAlignerAvailability();

            importing = true;
            updateImportButton();

            string artist = string.IsNullOrWhiteSpace(artistBox.Current.Value) ? "Unknown" : artistBox.Current.Value;
            string title = string.IsNullOrWhiteSpace(titleBox.Current.Value) ? "Imported Map" : titleBox.Current.Value;
            bool useAutomaticAlignment = automaticAlignmentCheckbox.Current.Value;
            AlignerVocalMode vocalMode = useAutomaticAlignment && estimatedVocalsCheckbox.Current.Value ? AlignerVocalMode.Estimated : AlignerVocalMode.Aligned;
            BeatmapLanguage language = languageDropdown.Current.Value;

            var cancellation = importCancellation = new CancellationTokenSource();

            statusText.Text = string.Empty;
            progressDisplay.Reset();
            progressDisplay.FadeIn(200, Easing.OutQuint);
            report(string.IsNullOrEmpty(lyricsPath) ? "starting import (blank map)" : "starting import");

            Task.Factory.StartNew(async () =>
            {
                LyricImportResult result;

                try
                {
                    result = await importer.BuildOszAsync(audioPath, lyricsPath, artist, title,
                        line => Schedule(() => report(line)), cancellation.Token, useAutomaticAlignment, language, vocalMode).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    result = LyricImportResult.Fail(e.Message);
                }

                if (result.Success && result.OszPath != null)
                {
                    Schedule(() => report("importing beatmap"));

                    if (vocalMode != AlignerVocalMode.Aligned && beatmaps != null)
                    {
                        // The estimated vocals choice is remembered on the imported SET (realm user data,
                        // never in the package), so the editor's re-align runs the same way. That needs
                        // the set back, which the global file route does not return, so this one import
                        // goes to the beatmap manager directly, through the same notification path. An
                        // aligned import (every set's default) keeps the global route unchanged.
                        foreach (var set in await beatmaps.ImportReturningSets(result.OszPath).ConfigureAwait(false))
                            beatmaps.SetAlignerVocalMode(set.ID, vocalMode);
                    }
                    else
                        await game.Import(result.OszPath).ConfigureAwait(false);

                    // The import SUMMARY (backlog 330): words the romaniser could not spell. The
                    // screen slides away on success, so it is raised as a notification that outlives
                    // it rather than as a line on the progress panel alone.
                    if (!string.IsNullOrEmpty(result.Notice))
                        Logger.Log($@"[import] {result.Notice}", LoggingTarget.Runtime, LogLevel.Important);

                    Schedule(finishSuccess);
                }
                else
                {
                    Schedule(() => finishFailure(result.Error));
                }
            }, cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// Routes one raw pipeline line to the progress panel (which summarises it to a stage label
        /// and a bar). The verbatim line still goes to the log: the display deliberately drops
        /// filenames, model names and chunk counters, and those are what a failed import is debugged
        /// with. Lines carrying only a counter are skipped to keep the log readable.
        /// </summary>
        private void report(string? line)
        {
            if (!string.IsNullOrWhiteSpace(line) && ImportProgressParser.ParseProgress(line) == null)
                Logger.Log($@"[import] {line.Trim()}");

            progressDisplay.Report(line);
        }

        private void finishSuccess()
        {
            importing = false;
            progressDisplay.Complete();

            // Give the final tick and its sample a moment to land before the screen slides away.
            Scheduler.AddDelayed(() =>
            {
                if (this.IsCurrentScreen())
                    this.Exit();
            }, 700);
        }

        private void finishFailure(string? error)
        {
            importing = false;
            // Deliberately not LogLevel.Important: that would raise a toast on top of the error the
            // failed stage row is already showing.
            Logger.Log($@"[import] failed: {error}");
            progressDisplay.Fail(error);
            updateImportButton();
        }

        public override void OnEntering(ScreenTransitionEvent e)
        {
            base.OnEntering(e);

            contentContainer.ScaleTo(0.95f).ScaleTo(1, 300, Easing.OutQuint);
            this.FadeInFromZero(300);
        }

        public override void OnResuming(ScreenTransitionEvent e)
        {
            base.OnResuming(e);
            refreshAlignerAvailability();
        }

        public override bool OnExiting(ScreenExitEvent e)
        {
            // An import in flight, especially a multi-minute local alignment run, shouldn't be torn
            // down by a stray Esc without asking. Nothing running -> leave freely.
            if (importing && !exitConfirmed && dialogOverlay != null)
            {
                if (dialogOverlay.CurrentDialog is not ConfirmCancelImportDialog)
                    dialogOverlay.Push(new ConfirmCancelImportDialog(confirmExit));

                return true; // block the exit until the user decides
            }

            // Leaving for real: cancel the token, which kills any local aligner process tree so it
            // stops burning minutes of CPU on a result nobody will collect.
            importCancellation?.Cancel();

            contentContainer.ScaleTo(0.95f, 300, Easing.OutQuint);
            this.FadeOut(300, Easing.OutQuint);

            return base.OnExiting(e);
        }

        private void confirmExit()
        {
            exitConfirmed = true;
            this.Exit();
        }

        /// <summary>
        /// The automatic alignment checkbox with a tooltip, which explains why it is greyed out. Hover
        /// still reaches a disabled checkbox (only the click is refused), so the tooltip shows exactly
        /// when it is needed; it is empty, and so hidden, while the aligner is available.
        /// </summary>
        private partial class AutomaticAlignmentCheckbox : OsuCheckbox, IHasTooltip
        {
            public LocalisableString TooltipText { get; set; }
        }

        /// <summary>A labelled drop target that shows the currently assigned filename.</summary>
        private partial class FileSlot : Container
        {
            private readonly string label;
            private readonly string placeholder;

            private OsuSpriteText fileText = null!;

            [Resolved]
            private OverlayColourProvider colours { get; set; } = null!;

            public FileSlot(string label, string placeholder)
            {
                this.label = label;
                this.placeholder = placeholder;
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                RelativeSizeAxes = Axes.X;
                Height = 60;
                Masking = true;
                CornerRadius = 8;

                Children = new Drawable[]
                {
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = colours.Background5,
                    },
                    new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        Direction = FillDirection.Vertical,
                        Anchor = Anchor.CentreLeft,
                        Origin = Anchor.CentreLeft,
                        Padding = new MarginPadding { Horizontal = 16 },
                        Spacing = new Vector2(0, 3),
                        Children = new Drawable[]
                        {
                            new OsuSpriteText
                            {
                                Text = label,
                                Font = OsuFont.Default.With(size: 13, weight: FontWeight.SemiBold),
                                Colour = colours.Content2,
                            },
                            fileText = new OsuSpriteText
                            {
                                Text = placeholder,
                                Font = OsuFont.Default.With(size: 18),
                                Colour = colours.Colour0,
                            },
                        }
                    }
                };
            }

            public void SetFile(string fileName)
            {
                fileText.Text = fileName;
                fileText.Colour = Color4.White;
            }
        }
    }
}
