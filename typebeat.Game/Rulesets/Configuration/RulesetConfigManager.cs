// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Extensions;
using osu.Framework.Logging;
using typebeat.Game.Configuration;
using typebeat.Game.Database;

namespace typebeat.Game.Rulesets.Configuration
{
    public abstract class RulesetConfigManager<TLookup> : ConfigManager<TLookup>, IRulesetConfigManager
        where TLookup : struct, Enum
    {
        private readonly RealmAccess realm;

        private readonly int variant;

        private List<RealmRulesetSetting> databasedSettings = new List<RealmRulesetSetting>();

        private readonly string rulesetName;

        protected RulesetConfigManager(SettingsStore store, RulesetInfo ruleset, int? variant = null)
        {
            realm = store?.Realm;

            rulesetName = ruleset.ShortName;

            this.variant = variant ?? 0;

            Load();

            InitialiseDefaults();
        }

        protected override void PerformLoad()
        {
            if (realm != null)
            {
                // As long as RulesetConfigCache exists, there is no need to subscribe to realm events.
                databasedSettings = realm.Realm.All<RealmRulesetSetting>().Where(b => b.RulesetName == rulesetName && b.Variant == variant).ToList();
            }
        }

        private readonly HashSet<TLookup> pendingWrites = new HashSet<TLookup>();

        protected override bool PerformSave()
        {
            TLookup[] changed;

            lock (pendingWrites)
            {
                changed = pendingWrites.ToArray();
                pendingWrites.Clear();
            }

            if (!changed.Any())
                return true;

            realm?.Write(r =>
            {
                foreach (var c in changed)
                {
                    var setting = r.All<RealmRulesetSetting>().First(s => s.RulesetName == rulesetName && s.Variant == variant && s.Key == c.ToString());

                    setting.Value = ConfigStore[c].ToString(CultureInfo.InvariantCulture);
                }
            });

            return true;
        }

        protected override void AddBindable<TBindable>(TLookup lookup, Bindable<TBindable> bindable)
        {
            base.AddBindable(lookup, bindable);

            var setting = databasedSettings.Find(s => s.Key == lookup.ToString());

            if (setting != null)
            {
                try
                {
                    bindable.Parse(setting.Value, CultureInfo.InvariantCulture);
                }
                catch (Exception e)
                {
                    // A databased value that no longer parses must not take the game down on boot.
                    // The realistic cause is an enum member renamed or removed after a row holding
                    // its old name was written: Bindable.Parse routes an enum through Enum.Parse,
                    // which throws ArgumentException on an unknown name rather than falling back.
                    // Keep the default the bindable already holds, and log rather than swallow it;
                    // the stale row is overwritten with that fallback on the next save.
                    Logger.Log($"Ruleset setting {rulesetName}.{lookup} could not be read from its stored value \"{setting.Value}\" ({e.Message}); falling back to the default.", level: LogLevel.Important);
                }
            }
            else
            {
                setting = new RealmRulesetSetting
                {
                    Key = lookup.ToString(),
                    Value = bindable.ToString(CultureInfo.InvariantCulture),
                    RulesetName = rulesetName,
                    Variant = variant,
                };

                realm?.Realm.Write(() => realm.Realm.Add(setting));

                databasedSettings.Add(setting);
            }

            bindable.ValueChanged += _ =>
            {
                lock (pendingWrites)
                    pendingWrites.Add(lookup);
            };
        }
    }
}
