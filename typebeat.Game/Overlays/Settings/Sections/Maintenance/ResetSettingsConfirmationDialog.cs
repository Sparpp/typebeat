// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Dialog;

namespace typebeat.Game.Overlays.Settings.Sections.Maintenance
{
    public partial class ResetSettingsConfirmationDialog : DangerousActionDialog
    {
        public ResetSettingsConfirmationDialog(Action resetAction)
        {
            BodyText = MaintenanceSettingsStrings.ResetAllSettingsConfirmation;
            Icon = FontAwesome.Solid.Undo;
            DangerousAction = resetAction;
        }
    }
}
