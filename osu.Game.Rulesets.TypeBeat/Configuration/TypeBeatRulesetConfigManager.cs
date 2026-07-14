// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Configuration;
using osu.Game.Rulesets.Configuration;

namespace osu.Game.Rulesets.TypeBeat.Configuration
{
    public enum TypeBeatRulesetSetting
    {
        /// <summary>
        /// Positive = lyrics later relative to the music, negative = earlier. Applied at the
        /// playfield's single engine-feed seam (the lyric-offset clock the engine, stage, HUD
        /// extras and key handler all read). Settings UI surfacing is deferred to M7.
        /// </summary>
        LyricOffsetMs,

        /// <summary>
        /// Optional explicit path to the vendored lyriclab aligner directory (the folder holding
        /// align_lyrics.py). Empty = auto-discover by walking up from the game's runtime location.
        /// Read by the import pipeline's directory resolution; UI surfacing is deferred to M7.
        /// </summary>
        LyricLabPath
    }

    public class TypeBeatRulesetConfigManager : RulesetConfigManager<TypeBeatRulesetSetting>
    {
        public TypeBeatRulesetConfigManager(SettingsStore? settings, RulesetInfo ruleset, int? variant = null)
            : base(settings, ruleset, variant)
        {
        }

        protected override void InitialiseDefaults()
        {
            base.InitialiseDefaults();

            SetDefault(TypeBeatRulesetSetting.LyricOffsetMs, 0.0, -500.0, 500.0, 1.0);
            SetDefault(TypeBeatRulesetSetting.LyricLabPath, string.Empty);
        }
    }
}
