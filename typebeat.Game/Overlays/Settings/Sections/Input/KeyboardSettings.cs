// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Input.Handlers.Keyboard;
using osu.Framework.Localisation;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;

namespace typebeat.Game.Overlays.Settings.Sections.Input
{
    /// <summary>
    /// The keyboard's device settings. Only created on Windows (<see cref="OsuGameBase.CreateSettingsSubsectionFor"/>),
    /// since its one setting, <see cref="OsuSetting.DisableRawKeyboard"/>, is a Windows SDL switch.
    /// </summary>
    public partial class KeyboardSettings : InputSubsection
    {
        protected override LocalisableString Header => InputSettingsStrings.Keyboard;

        // Turning the keyboard off would leave a typing game with no way to turn it back on.
        protected override bool IsToggleable => false;

        private readonly Bindable<SettingsNote.Data?> restartNote = new Bindable<SettingsNote.Data?>();

        private Bindable<bool> disableRawKeyboard = null!;

        public KeyboardSettings(KeyboardHandler handler)
            : base(handler)
        {
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            disableRawKeyboard = config.GetBindable<bool>(OsuSetting.DisableRawKeyboard);

            Add(new SettingsItemV2(new FormCheckBox
            {
                Caption = InputSettingsStrings.DisableRawKeyboard,
                HintText = InputSettingsStrings.DisableRawKeyboardTooltip,
                Current = disableRawKeyboard,
            })
            {
                Keywords = new[] { @"raw", @"input", @"keys", @"typing", @"dropped", @"doubled" },
                Note = { BindTarget = restartNote },
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // The setting is only applied at startup, and nothing else writes it, so the stored value when
            // this section first loads is the one this run started with; a note marks a pending change.
            bool appliedAtStartup = disableRawKeyboard.Value;

            disableRawKeyboard.BindValueChanged(v =>
                restartNote.Value = v.NewValue == appliedAtStartup ? null : new SettingsNote.Data(InputSettingsStrings.RestartToApply, SettingsNote.Type.Informational));
        }
    }
}
