// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Logging;

namespace typebeat.Game.Configuration.Presets
{
    /// <summary>Attaches a schema's keys to the live configs that store them.</summary>
    public static class GameplayPresetBindings
    {
        /// <summary>
        /// Every entry of <paramref name="schema"/> that resolves to a live setting: <c>game:</c> keys in
        /// <paramref name="game"/>, the rest in <paramref name="sources"/> by their prefix (a ruleset's short
        /// name). An entry that does not resolve is left out, and logged: presets then neither encode it
        /// from nor write it to anything, which the coverage test makes sure never ships.
        /// </summary>
        public static IReadOnlyList<LiveGameplaySetting> Resolve(PresetSchema schema, INamedSettingSource game, IReadOnlyDictionary<string, INamedSettingSource?> sources)
        {
            var settings = new List<LiveGameplaySetting>();

            foreach (var entry in schema.Entries)
            {
                int colon = entry.Key.IndexOf(':');
                string source = entry.Key[..colon];
                string name = entry.Key[(colon + 1)..];

                INamedSettingSource? config = source == GameplayPresetSchemas.GAME_SOURCE ? game : sources.GetValueOrDefault(source);
                var bindable = config?.FindSetting(name);
                var setting = bindable == null ? null : LiveGameplaySetting.Create(entry.Key, bindable, entry.Domain);

                if (setting == null)
                {
                    Logger.Log($"Gameplay presets: {entry.Key} ({entry.Domain.Spec}) has no live setting to attach to, so presets leave it alone.", level: LogLevel.Important);
                    continue;
                }

                settings.Add(setting);
            }

            return settings;
        }

        /// <summary>Resolves the ruleset configs by short name, skipping any that cannot be created.</summary>
        public static IReadOnlyDictionary<string, INamedSettingSource?> RulesetSources(IEnumerable<(string ShortName, Func<INamedSettingSource?> Config)> rulesets)
        {
            var sources = new Dictionary<string, INamedSettingSource?>();

            foreach (var (shortName, config) in rulesets)
            {
                try
                {
                    sources[shortName] = config();
                }
                catch (Exception e)
                {
                    Logger.Error(e, $"Gameplay presets could not read the {shortName} ruleset's settings.");
                }
            }

            return sources;
        }
    }
}
