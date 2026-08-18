// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Graphics;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Localisation;

namespace typebeat.Game.Rulesets.Edit.Tools
{
    public class SelectTool : CompositionTool<GlobalAction>
    {
        public SelectTool()
            : base(EditorStrings.SelectTool)
        {
            Action = GlobalAction.EditorSelectTool;
        }

        public override Drawable CreateIcon() => new SpriteIcon { Icon = OsuIcon.EditorSelect };

        public override HitObjectPlacementBlueprint CreatePlacementBlueprint() => null;
    }
}
