// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Utils;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Screens.Play.HUD
{
    /// <summary>
    /// An overlay layer on top of the playfield which fades to red when the current player health falls below a certain threshold defined by <see cref="low_health_threshold"/>.
    /// </summary>
    public partial class FailingLayer : HealthDisplay
    {
        /// <summary>
        /// Whether the current player health should be shown on screen.
        /// </summary>
        public readonly Bindable<bool> ShowHealth = new Bindable<bool>();

        protected override bool PlayInitialIncreaseAnimation => false;

        private const int fade_time = 400;

        /// <summary>
        /// The threshold under which the current player life should be considered low and the layer should start fading in.
        /// </summary>
        private const double low_health_threshold = 0.20f;

        private readonly ScreenEdgeWarning boxes;

        private Bindable<bool> fadePlayfieldWhenHealthLow;

        public FailingLayer()
        {
            RelativeSizeAxes = Axes.Both;
            InternalChildren = new Drawable[]
            {
                boxes = new ScreenEdgeWarning { Alpha = 0 },
            };
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            fadePlayfieldWhenHealthLow = config.GetBindable<bool>(OsuSetting.FadePlayfieldWhenHealthLow);
            fadePlayfieldWhenHealthLow.BindValueChanged(_ => updateState());
            ShowHealth.BindValueChanged(_ => updateState());
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            updateState();
        }

        private void updateState()
        {
            // Don't display ever if the ruleset is not using a draining health display.
            bool showLayer = HealthProcessor is DrainingHealthProcessor && fadePlayfieldWhenHealthLow.Value && ShowHealth.Value;
            this.FadeTo(showLayer ? 1 : 0, fade_time, Easing.OutQuint);
        }

        protected override void Update()
        {
            base.Update();

            double target = Math.Clamp(ScreenEdgeWarning.MAX_ALPHA * (1 - Current.Value / low_health_threshold), 0, ScreenEdgeWarning.MAX_ALPHA);

            boxes.Alpha = (float)Interpolation.Lerp(boxes.Alpha, target, Clock.ElapsedFrameTime * 0.01f);
        }
    }
}
