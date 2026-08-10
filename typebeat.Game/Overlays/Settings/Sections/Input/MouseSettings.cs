// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Input.Handlers.Mouse;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;

namespace typebeat.Game.Overlays.Settings.Sections.Input
{
    public partial class MouseSettings : InputSubsection
    {
        private readonly MouseHandler mouseHandler;

        protected override LocalisableString Header => MouseSettingsStrings.Mouse;

        private Bindable<double> handlerSensitivity = null!;
        private Bindable<double> localSensitivity = null!;
        private Bindable<bool> relativeMode = null!;

        private FormCheckBox highPrecisionMouse = null!;

        private readonly Bindable<SettingsNote.Data?> highPrecisionMouseNote = new Bindable<SettingsNote.Data?>();

        protected override bool IsToggleable => false;

        public MouseSettings(MouseHandler mouseHandler)
            : base(mouseHandler)
        {
            this.mouseHandler = mouseHandler;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            // use local bindable to avoid changing enabled state of game host's bindable.
            handlerSensitivity = mouseHandler.Sensitivity.GetBoundCopy();
            localSensitivity = handlerSensitivity.GetUnboundCopy();

            relativeMode = mouseHandler.UseRelativeMode.GetBoundCopy();

            AddRange(new Drawable[]
            {
                new SettingsItemV2(highPrecisionMouse = new FormCheckBox
                {
                    Caption = MouseSettingsStrings.HighPrecisionMouse,
                    HintText = MouseSettingsStrings.HighPrecisionMouseTooltip,
                    Current = relativeMode,
                })
                {
                    Keywords = new[] { @"raw", @"input", @"relative", @"cursor", "sensitivity", "speed", "velocity" },
                    Note = { BindTarget = highPrecisionMouseNote },
                },
                new SettingsItemV2(new FormSliderBar<double>
                {
                    Caption = MouseSettingsStrings.CursorSensitivity,
                    Current = localSensitivity,
                    KeyboardStep = 0.01f,
                    TransferValueOnCommit = true,
                    LabelFormat = v => $@"{v:0.##}x",
                    TooltipFormat = v => localSensitivity.Disabled ? MouseSettingsStrings.EnableHighPrecisionForSensitivityAdjust : $@"{v:0.##}x",
                })
                {
                    Keywords = new[] { "speed", "velocity" },
                },
            });
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            relativeMode.BindValueChanged(relative => localSensitivity.Disabled = !relative.NewValue, true);

            handlerSensitivity.BindValueChanged(val =>
            {
                bool disabled = localSensitivity.Disabled;

                localSensitivity.Disabled = false;
                localSensitivity.Value = val.NewValue;
                localSensitivity.Disabled = disabled;
            }, true);

            localSensitivity.BindValueChanged(val => handlerSensitivity.Value = val.NewValue);

            highPrecisionMouse.Current.BindValueChanged(highPrecision =>
            {
                switch (RuntimeInfo.OS)
                {
                    case RuntimeInfo.Platform.Linux:
                    case RuntimeInfo.Platform.macOS:
                    case RuntimeInfo.Platform.iOS:
                        if (highPrecision.NewValue)
                            highPrecisionMouseNote.Value = new SettingsNote.Data(MouseSettingsStrings.HighPrecisionPlatformWarning, SettingsNote.Type.Warning);
                        else
                            highPrecisionMouseNote.Value = null;

                        break;
                }
            }, true);
        }
    }
}
