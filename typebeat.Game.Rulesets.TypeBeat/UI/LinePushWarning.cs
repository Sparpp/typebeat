// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Rulesets.TypeBeat.Configuration;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>A display-only countdown, drawn over the full screen independently of health.</summary>
    public partial class LinePushWarning : ScreenEdgeWarning
    {
        private readonly TypeBeatPlayfield playfield;
        private readonly BindableBool enabled = new BindableBool();
        private const double fade_out_ms = 150;
        private float previousTarget;
        private double previousTime = double.NaN;

        public LinePushWarning(TypeBeatPlayfield playfield)
        {
            this.playfield = playfield;
            AlwaysPresent = true;
            Alpha = 0;
        }

        [BackgroundDependencyLoader(true)]
        private void load(TypeBeatRulesetConfigManager? config) => config?.BindWith(TypeBeatRulesetSetting.LinePushWarningEdges, enabled);

        protected override void Update()
        {
            base.Update();
            float target = enabled.Value ? MAX_ALPHA * playfield.LinePushWarningProgress : 0;
            if (!enabled.Value || Time.Current < previousTime)
                this.FadeTo(target);
            else if (target < previousTarget)
                this.FadeTo(target, fade_out_ms, Easing.OutQuad);
            else if (target > 0 && Alpha <= target)
                this.FadeTo(target);

            // Start the release once, so it can finish even across frames with no warning.
            // A new warning takes over once it is stronger than the fading previous one.
            previousTarget = target;
            previousTime = Time.Current;
        }
    }
}
