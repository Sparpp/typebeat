// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Database;
using typebeat.Game.Overlays.Dialog;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Overlays.Mods
{
    public partial class DeleteModPresetDialog : DeletionDialog
    {
        public DeleteModPresetDialog(Live<ModPreset> modPreset)
        {
            BodyText = modPreset.PerformRead(preset => preset.Name);
            DangerousAction = () => modPreset.PerformWrite(preset => preset.DeletePending = true);
        }
    }
}
