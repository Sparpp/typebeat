// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Configuration;

namespace typebeat.Game.Rulesets.Mods
{
    /// <summary>
    /// An interface for mods that require reading access to the type!beat configuration.
    /// </summary>
    public interface IReadFromConfig
    {
        void ReadFromConfig(OsuConfigManager config);
    }
}
