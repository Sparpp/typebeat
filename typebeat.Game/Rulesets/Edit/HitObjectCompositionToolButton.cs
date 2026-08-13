// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Rulesets.Edit.Tools;
using typebeat.Game.Screens.Edit.Components.RadioButtons;

namespace typebeat.Game.Rulesets.Edit
{
    public partial class HitObjectCompositionToolButton : EditorRadioButton
    {
        public CompositionTool Tool { get; }

        public HitObjectCompositionToolButton(CompositionTool tool, Action<CompositionTool>? action)
            : base(tool.Name, () => action?.Invoke(tool), tool.CreateIcon)
        {
            Tool = tool;

            Selected.BindDisabledChanged(isDisabled =>
            {
                TooltipText = isDisabled ? "Add at least one timing point first!" : Tool.TooltipText;
            }, true);
        }
    }
}
