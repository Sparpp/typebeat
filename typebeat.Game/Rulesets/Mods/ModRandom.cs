// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Bindables;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics;
using typebeat.Game.Overlays.Settings;

namespace typebeat.Game.Rulesets.Mods
{
    public abstract class ModRandom : Mod, IHasSeed
    {
        public override string Name => "Random";
        public override string Acronym => "RD";
        public override ModType Type => ModType.Conversion;
        public override IconUsage? Icon => OsuIcon.ModRandom;

        [SettingSource("Seed", "Use a custom seed instead of a random one", SettingControlType = typeof(SettingsNumberBox))]
        public Bindable<int?> Seed { get; } = new Bindable<int?>();
    }
}
