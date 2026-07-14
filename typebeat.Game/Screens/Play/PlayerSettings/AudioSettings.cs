// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using typebeat.Game.Configuration;
using typebeat.Game.Localisation;
using typebeat.Game.Scoring;

namespace typebeat.Game.Screens.Play.PlayerSettings
{
    public partial class AudioSettings : PlayerSettingsGroup
    {
        private Bindable<ScoreInfo> referenceScore { get; } = new Bindable<ScoreInfo>();

        private readonly PlayerCheckbox beatmapHitsoundsToggle;

        public AudioSettings()
            : base(PlayerSettingsOverlayStrings.AudioSettingsTitle)
        {
            Children = new Drawable[]
            {
                beatmapHitsoundsToggle = new PlayerCheckbox { LabelText = SkinSettingsStrings.BeatmapHitsounds },
                new BeatmapOffsetControl
                {
                    ReferenceScore = { BindTarget = referenceScore },
                },
            };
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config, SessionStatics statics)
        {
            beatmapHitsoundsToggle.Current = config.GetBindable<bool>(OsuSetting.BeatmapHitsounds);
            statics.BindWith(Static.LastLocalUserScore, referenceScore);
        }
    }
}
