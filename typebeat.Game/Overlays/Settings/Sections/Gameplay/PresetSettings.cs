// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Localisation;
using osu.Framework.Platform;
using typebeat.Game.Configuration.Presets;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Dialog;
using typebeat.Game.Overlays.Notifications;
using osuTK;
using WebCommonStrings = typebeat.Game.Resources.Localisation.Web.CommonStrings;

namespace typebeat.Game.Overlays.Settings.Sections.Gameplay
{
    /// <summary>
    /// Gameplay presets (backlog 484), at the top of Settings &gt; Gameplay and laid out like the Skin
    /// section: the dropdown, a row of Rename / Export / Delete, then one full-width button, which here
    /// is Import (where Skin has its layout editor). All behaviour lives in <see cref="GameplayPresetManager"/>.
    /// </summary>
    public partial class PresetSettings : SettingsSubsection
    {
        public const string DROPDOWN_CAPTION = "Current preset";

        protected override LocalisableString Header => "Presets";

        // No header row, like the Skin section's dropdown: the section header sits right above it.
        protected override Drawable CreateHeader() => Empty();

        public override IEnumerable<LocalisableString> FilterTerms => new LocalisableString[] { Header, "share code", "import", "export" };

        [Resolved]
        private GameplayPresetManager presets { get; set; } = null!;

        // The dropdown's own selection. Kept apart from the manager's: a dropdown handed an item list that
        // lacks its current value jumps to the first item, which bound straight to the manager would apply
        // Minimal over the player's settings. Here such a jump is ignored and corrected.
        private readonly Bindable<GameplayPreset> selection = new Bindable<GameplayPreset>();

        private IBindable<GameplayPreset> current = null!;

        private FormDropdown<GameplayPreset> dropdown = null!;

        private bool syncing;

        [BackgroundDependencyLoader]
        private void load()
        {
            Children = new Drawable[]
            {
                new SettingsItemV2(dropdown = new FormDropdown<GameplayPreset>
                {
                    Caption = DROPDOWN_CAPTION,
                    HintText = "Every setting in this section except the typing font and the Windows key. Minimal is the defaults. "
                               + "Changing a setting switches to Custom, which remembers your settings while another preset is selected. "
                               + "Export copies a share code; Import applies one.",
                    Current = selection,
                })
                {
                    ShowRevertToDefaultButton = false,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Horizontal,
                    Padding = SettingsPanel.CONTENT_PADDING,
                    Children = new Drawable[]
                    {
                        new RenamePresetButton { Padding = new MarginPadding { Right = 2.5f }, RelativeSizeAxes = Axes.X, Width = 1 / 3f },
                        new ExportPresetButton { Padding = new MarginPadding { Horizontal = 2.5f }, RelativeSizeAxes = Axes.X, Width = 1 / 3f },
                        new DeletePresetButton { Padding = new MarginPadding { Left = 2.5f }, RelativeSizeAxes = Axes.X, Width = 1 / 3f },
                    }
                },
                new ImportPresetButton(),
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            presets.EnsureInitialised();
            presets.EntriesChanged += scheduleRefresh;

            current = presets.Current.GetBoundCopy();
            current.BindValueChanged(_ => scheduleRefresh());

            refresh();

            selection.BindValueChanged(e =>
            {
                if (syncing)
                    return;

                presets.Select(e.NewValue);

                // Puts the dropdown back if the manager refused the pick.
                refresh();
            });
        }

        /// <summary>
        /// Right away on the update thread, so the dropdown never lags the manager: a scheduled task would wait
        /// for this subsection to update again, and the framework does not update it while the settings
        /// panel has it scrolled out of view. A change from another thread (settings can be written off it)
        /// waits for the update thread.
        /// </summary>
        private void scheduleRefresh()
        {
            if (ThreadSafety.IsUpdateThread)
                refresh();
            else
                Scheduler.AddOnce(refresh);
        }

        private void refresh()
        {
            bool wasSyncing = syncing;
            syncing = true;

            try
            {
                if (!dropdown.Items.SequenceEqual(presets.Entries))
                    dropdown.Items = presets.Entries;

                selection.Value = presets.Current.Value;
            }
            finally
            {
                syncing = wasSyncing;
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (presets.IsNotNull())
                presets.EntriesChanged -= scheduleRefresh;
        }

        /// <summary>A button that follows the selected preset.</summary>
        public abstract partial class PresetButton : SettingsButtonV2
        {
            [Resolved]
            protected GameplayPresetManager Presets { get; private set; } = null!;

            private IBindable<GameplayPreset> current = null!;

            protected GameplayPreset Selected => current.Value;

            protected override void LoadComplete()
            {
                base.LoadComplete();

                current = Presets.Current.GetBoundCopy();
                current.BindValueChanged(_ => scheduleUpdateState(), true);
            }

            // As the dropdown: right away on the update thread (see scheduleRefresh).
            private void scheduleUpdateState()
            {
                if (ThreadSafety.IsUpdateThread)
                    updateState();
                else
                    Scheduler.AddOnce(updateState);
            }

            private void updateState() => Enabled.Value = IsAvailableFor(Selected);

            protected virtual bool IsAvailableFor(GameplayPreset preset) => true;
        }

        /// <summary>
        /// Renames a named preset. On Custom it names Custom's settings instead: they are saved as a new
        /// preset, which is selected. Minimal cannot be renamed.
        /// </summary>
        public partial class RenamePresetButton : PresetButton, IHasPopover
        {
            [BackgroundDependencyLoader]
            private void load()
            {
                Text = CommonStrings.Rename;
                Action = this.ShowPopover;
            }

            protected override bool IsAvailableFor(GameplayPreset preset) => Presets.CanRename(preset);

            public Popover GetPopover() => new RenamePresetPopover();
        }

        /// <summary>Copies the selected preset's share code to the clipboard, and shows it in a notification too.</summary>
        public partial class ExportPresetButton : PresetButton
        {
            [Resolved]
            private Clipboard clipboard { get; set; } = null!;

            [Resolved]
            private INotificationOverlay? notifications { get; set; }

            [BackgroundDependencyLoader]
            private void load()
            {
                Text = CommonStrings.Export;
                Action = export;
            }

            private void export()
            {
                var preset = Selected;
                string code = Presets.Export(preset);

                clipboard.SetText(code);

                // The code is in the notification as well as on the clipboard: it stays readable there
                // after the clipboard has moved on, and it can be read out or retyped where pasting fails.
                notifications?.Post(new SimpleNotification
                {
                    Text = $"Copied the share code for \"{preset.Name}\" to the clipboard: {code}",
                    Icon = FontAwesome.Solid.ShareAlt,
                });
            }
        }

        /// <summary>Deletes the selected named preset, after confirming. Minimal and Custom cannot be deleted.</summary>
        public partial class DeletePresetButton : PresetButton
        {
            [Resolved]
            private IDialogOverlay? dialogOverlay { get; set; }

            [BackgroundDependencyLoader]
            private void load(OsuColour colours)
            {
                BackgroundColour = colours.DangerousButtonColour;
                Text = WebCommonStrings.ButtonsDelete;
                Action = delete;
            }

            protected override bool IsAvailableFor(GameplayPreset preset) => Presets.CanDelete(preset);

            private void delete()
            {
                var preset = Selected;

                if (dialogOverlay != null)
                    dialogOverlay.Push(new PresetDeleteDialog(preset));
                else
                    Presets.Delete(preset);
            }
        }

        /// <summary>Opens the paste box for a share code.</summary>
        public partial class ImportPresetButton : SettingsButtonV2, IHasPopover
        {
            [BackgroundDependencyLoader]
            private void load()
            {
                Text = "Import";
                Action = this.ShowPopover;
            }

            public Popover GetPopover() => new ImportPresetPopover();
        }

        public partial class PresetDeleteDialog : DeletionDialog
        {
            private readonly GameplayPreset preset;

            public PresetDeleteDialog(GameplayPreset preset)
            {
                this.preset = preset;
                BodyText = preset.Name;
            }

            [BackgroundDependencyLoader]
            private void load(GameplayPresetManager presets)
            {
                DangerousAction = () => presets.Delete(preset);
            }
        }

        /// <summary>A text box, a button and a line for what went wrong: the shape both popovers share.</summary>
        public abstract partial class PresetTextPopover : OsuPopover
        {
            [Resolved]
            protected GameplayPresetManager Presets { get; private set; } = null!;

            public readonly FocusedTextBox TextBox;

            public readonly OsuTextFlowContainer Message;

            /// <summary>What <see cref="Message"/> last said: why the text was refused.</summary>
            public string MessageText { get; private set; } = string.Empty;

            protected PresetTextPopover(LocalisableString placeholder, LocalisableString action)
            {
                AutoSizeAxes = Axes.Both;
                Origin = Anchor.TopCentre;

                RoundedButton button;

                Child = new FillFlowContainer
                {
                    Direction = FillDirection.Vertical,
                    AutoSizeAxes = Axes.Y,
                    Width = 300,
                    Spacing = new Vector2(10f),
                    Children = new Drawable[]
                    {
                        TextBox = new FocusedTextBox
                        {
                            PlaceholderText = placeholder,
                            FontSize = OsuFont.DEFAULT_FONT_SIZE,
                            RelativeSizeAxes = Axes.X,
                            SelectAllOnFocus = true,
                        },
                        Message = new OsuTextFlowContainer(s => s.Font = OsuFont.GetFont(size: 14))
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Alpha = 0,
                        },
                        button = new RoundedButton
                        {
                            Height = 40,
                            RelativeSizeAxes = Axes.X,
                            MatchingFilter = true,
                            Text = action,
                        },
                    }
                };

                button.Action += commit;
                TextBox.OnCommit += (_, _) => commit();
            }

            [BackgroundDependencyLoader]
            private void load(OsuColour colours)
            {
                Message.Colour = colours.RedLight;
            }

            protected override void PopIn()
            {
                Message.Hide();
                TextBox.Text = InitialText;
                TextBox.TakeFocus();

                base.PopIn();
            }

            protected virtual string InitialText => string.Empty;

            /// <summary>Acts on the text; returns why it could not, or null when it did.</summary>
            protected abstract string? Commit(string text);

            private void commit()
            {
                string? problem = Commit(TextBox.Text);

                if (problem == null)
                {
                    this.HidePopover();
                    return;
                }

                Message.Text = MessageText = problem;
                Message.Show();
            }
        }

        public partial class RenamePresetPopover : PresetTextPopover
        {
            public RenamePresetPopover()
                : base("Preset name", WebCommonStrings.ButtonsSave)
            {
                TextBox.LengthLimit = GameplayPresetManager.MAX_NAME_LENGTH;
            }

            protected override string InitialText => Presets.Current.Value.Kind == GameplayPresetKind.Custom ? string.Empty : Presets.Current.Value.Name;

            protected override string? Commit(string text)
            {
                var preset = Presets.Current.Value;
                var result = preset.Kind == GameplayPresetKind.Custom ? Presets.SaveCustomAs(text) : Presets.Rename(preset, text);

                return result == null ? "Enter a name for the preset." : null;
            }
        }

        public partial class ImportPresetPopover : PresetTextPopover
        {
            public ImportPresetPopover()
                : base("Paste a share code", "Import")
            {
            }

            protected override string? Commit(string text)
            {
                var result = Presets.Import(text);
                return result.Succeeded ? null : result.Message;
            }
        }
    }
}
