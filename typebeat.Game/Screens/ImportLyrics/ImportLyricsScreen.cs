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
using osu.Framework.Graphics.Shapes;
using osu.Framework.Screens;
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
    /// and the ruleset's <see cref="ILyricMapImporter"/> aligns + packages an .osz which is then
    /// imported. Files arrive via <see cref="AddFiles"/> (routed by <see cref="LyricImportManager"/>
    /// from global file drops). Esc cancels an in-flight import, killing the aligner process tree.
    /// </summary>
    public partial class ImportLyricsScreen : OsuScreen
    {
        public override bool HideOverlaysOnEnter => true;

        [Resolved]
        private OsuGameBase game { get; set; } = null!;

        [Resolved(CanBeNull = true)]
        private ILyricMapImporter? importer { get; set; }

        [Resolved(CanBeNull = true)]
        private IDialogOverlay? dialogOverlay { get; set; }

        [Cached]
        private OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

        private readonly string[] initialFiles;

        private string? audioPath;
        private string? lyricsPath;

        private FileSlot audioSlot = null!;
        private FileSlot lyricsSlot = null!;
        private LabelledTextBox artistBox = null!;
        private LabelledTextBox titleBox = null!;
        private OsuCheckbox automaticAlignmentCheckbox = null!;
        private RoundedButton importButton = null!;
        private OsuSpriteText statusText = null!;
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
                                    Text = "drop an audio file (.mp3/.ogg/.wav) or video (.mp4) and a lyrics file (.txt/.lrc) anywhere in the window.",
                                    Colour = colourProvider.Content2,
                                    Font = OsuFont.Default.With(size: 16),
                                },
                                audioSlot = new FileSlot("audio", "drop .mp3 / .ogg / .wav / .mp4"),
                                lyricsSlot = new FileSlot("lyrics", "drop .txt / .lrc"),
                                artistBox = new LabelledTextBox { Label = "artist" },
                                titleBox = new LabelledTextBox { Label = "title" },
                                automaticAlignmentCheckbox = new OsuCheckbox
                                {
                                    RelativeSizeAxes = Axes.X,
                                    LabelText = "automatic alignment (time each word from the audio — slower, needs sign-in; off = use your [mm:ss.xx] line stamps)",
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
                            }
                        }
                    }
                }
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            AddFiles(initialFiles);
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

        private void updateImportButton()
        {
            importButton.Enabled.Value = !importing
                                         && importer != null
                                         && !string.IsNullOrEmpty(audioPath)
                                         && !string.IsNullOrEmpty(lyricsPath);
        }

        private void startImport()
        {
            if (importing || importer == null || string.IsNullOrEmpty(audioPath) || string.IsNullOrEmpty(lyricsPath))
                return;

            importing = true;
            updateImportButton();

            string artist = string.IsNullOrWhiteSpace(artistBox.Current.Value) ? "Unknown" : artistBox.Current.Value;
            string title = string.IsNullOrWhiteSpace(titleBox.Current.Value) ? "Imported Map" : titleBox.Current.Value;
            bool useAutomaticAlignment = automaticAlignmentCheckbox.Current.Value;

            var cancellation = importCancellation = new CancellationTokenSource();
            setStatus("starting import...");

            Task.Factory.StartNew(async () =>
            {
                LyricImportResult result;

                try
                {
                    result = await importer.BuildOszAsync(audioPath, lyricsPath, artist, title,
                        line => Schedule(() => setStatus(line)), cancellation.Token, useAutomaticAlignment).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    result = LyricImportResult.Fail(e.Message);
                }

                if (result.Success && result.OszPath != null)
                {
                    Schedule(() => setStatus("packaging done — importing beatmap..."));
                    await game.Import(result.OszPath).ConfigureAwait(false);
                    Schedule(finishSuccess);
                }
                else
                {
                    Schedule(() => finishFailure(result.Error));
                }
            }, cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void finishSuccess()
        {
            importing = false;
            setStatus("imported!");

            if (this.IsCurrentScreen())
                this.Exit();
        }

        private void finishFailure(string? error)
        {
            importing = false;
            setStatus($"import failed: {error}");
            updateImportButton();
        }

        private void setStatus(string text) => statusText.Text = text;

        public override void OnEntering(ScreenTransitionEvent e)
        {
            base.OnEntering(e);

            contentContainer.ScaleTo(0.95f).ScaleTo(1, 300, Easing.OutQuint);
            this.FadeInFromZero(300);
        }

        public override bool OnExiting(ScreenExitEvent e)
        {
            // An import in flight — especially a multi-minute server alignment — shouldn't be torn
            // down by a stray Esc without asking. Nothing running -> leave freely.
            if (importing && !exitConfirmed && dialogOverlay != null)
            {
                if (dialogOverlay.CurrentDialog is not ConfirmCancelImportDialog)
                    dialogOverlay.Push(new ConfirmCancelImportDialog(confirmExit));

                return true; // block the exit until the user decides
            }

            // Leaving for real: cancel the token. That kills any local aligner process tree AND makes
            // RemoteAlignClient drop the server-side job, so the worker stops aligning for nobody.
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
