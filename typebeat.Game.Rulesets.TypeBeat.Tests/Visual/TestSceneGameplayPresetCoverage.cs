// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Testing;
using typebeat.Game.Configuration;
using typebeat.Game.Configuration.Presets;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Overlays.Settings.Sections;
using typebeat.Game.Overlays.Settings.Sections.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 484: Gameplay presets cover every setting in Settings &gt; Gameplay but the excluded ones,
    /// and the latest frozen share code schema says exactly which settings those are and how each is
    /// encoded. This walks the REAL section, works out which stored setting every control writes (by
    /// changing the control and seeing which setting moved) and what its domain is, and compares that
    /// with <see cref="GameplayPresetSchemas.LATEST"/>. When they differ it fails and prints the schema to
    /// append, so a Gameplay change can never silently reinterpret the codes players already have.
    /// </summary>
    public partial class TestSceneGameplayPresetCoverage : OsuTestScene
    {
        [Resolved]
        private OsuConfigManager gameConfig { get; set; } = null!;

        [Resolved]
        private RulesetStore rulesets { get; set; } = null!;

        private SettingsOverlay settings = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("open the settings", () =>
            {
                Child = settings = new SettingsOverlay
                {
                    State = { Value = Visibility.Visible },
                };
            });

            AddUntilStep("Gameplay loaded", () => settings.ChildrenOfType<TypeBeatSettingsSubsection>().SingleOrDefault()?.IsLoaded == true
                                                 && settings.ChildrenOfType<PresetSettings>().SingleOrDefault()?.IsLoaded == true);
        }

        [Test]
        public void TestTheLatestSchemaMatchesTheGameplaySection()
        {
            AddStep("compare the controls with the latest schema", () =>
            {
                var live = deriveLiveEntries();
                string? problems = compare(live, GameplayPresetSchemas.LATEST);

                if (problems != null)
                    Assert.Fail(problems);
            });
        }

        /// <summary>Every setting control in Gameplay, outside the presets block, as (caption, key, domain).</summary>
        private List<(string Caption, string Key, PresetDomain? Domain)> deriveLiveEntries()
        {
            var gameplay = settings.SectionsContainer.OfType<GameplaySection>().Single();
            var sources = new Dictionary<string, INamedSettingSource> { [GameplayPresetSchemas.GAME_SOURCE] = gameConfig };

            foreach (var info in rulesets.AvailableRulesets)
            {
                if (RulesetConfigs.GetConfigFor(info.CreateInstance()) is INamedSettingSource config)
                    sources[info.ShortName] = config;
            }

            var entries = new List<(string, string, PresetDomain?)>();

            foreach (var control in settingControls(gameplay))
            {
                string caption = captionOf(control);

                if (settingWrittenBy(control, caption, sources) is string key)
                    entries.Add((caption, key, domainOf(control, key, sources)));
            }

            return entries;
        }

        /// <summary>
        /// Every control in Gameplay, outside the presets block, that can write a setting: each form control
        /// and anything else with a <c>Current</c>, whether a <see cref="SettingsItemV2"/> wraps it or not.
        /// Only the outermost of nested ones counts (a slider's own text box is part of its form control).
        /// </summary>
        private static IEnumerable<Drawable> settingControls(GameplaySection gameplay)
        {
            bool isControl(Drawable d) => d is IFormControl || currentInterfaceOf(d) != null;

            bool insideAnotherControl(Drawable d)
            {
                for (var parent = d.Parent; parent != null && parent != gameplay; parent = parent.Parent)
                {
                    if (isControl(parent))
                        return true;
                }

                return false;
            }

            return gameplay.ChildrenOfType<Drawable>()
                           .Where(d => isControl(d) && d is not PresetSettings && d.FindClosestParent<PresetSettings>() == null && !insideAnotherControl(d))
                           .ToList();
        }

        private static string captionOf(Drawable control) =>
            control is IHasFilterTerms filterable && filterable.FilterTerms.Any() ? filterable.FilterTerms.First().ToString() : control.GetType().Name;

        private static Type? currentInterfaceOf(Drawable control) =>
            control.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IHasCurrentValue<>));

        /// <summary>
        /// The one stored setting <paramref name="control"/> writes: change the control's value, see which
        /// setting moved, put it back. A greyed-out control is ungreyed for the probe. Null for a control
        /// that is not a form control and writes no setting (a display with a <c>Current</c>); a form
        /// control must write exactly one.
        /// </summary>
        private static string? settingWrittenBy(Drawable control, string caption, IReadOnlyDictionary<string, INamedSettingSource> sources)
        {
            object current = currentOf(control);
            var type = current.GetType();
            var valueProperty = type.GetProperty(nameof(Bindable<int>.Value))!;
            var disabledProperty = type.GetProperty(nameof(Bindable<int>.Disabled))!;

            object? before = valueProperty.GetValue(current);
            bool wasDisabled = (bool)disabledProperty.GetValue(current)!;

            var snapshotBefore = snapshot(sources);

            disabledProperty.SetValue(current, false);
            valueProperty.SetValue(current, anotherValue(control, current, before));

            var changed = snapshot(sources).Where(p => snapshotBefore[p.Key] != p.Value).Select(p => p.Key).ToArray();

            valueProperty.SetValue(current, before);
            disabledProperty.SetValue(current, wasDisabled);

            if (changed.Length == 0 && control is not IFormControl)
                return null;

            Assert.That(changed, Has.Length.EqualTo(1), $"Changing the Gameplay control \"{caption}\" should change exactly one stored setting, but changed: {string.Join(", ", changed)}");
            return changed[0];
        }

        private static object currentOf(Drawable control)
        {
            var hasCurrent = currentInterfaceOf(control);

            Assert.That(hasCurrent, Is.Not.Null, $"{control.GetType().Name} has no Current to probe");
            return hasCurrent!.GetProperty(nameof(IHasCurrentValue<int>.Current))!.GetValue(control)!;
        }

        private static object anotherValue(Drawable control, object current, object? value)
        {
            object[] offered = offeredItems(control)?.Cast<object>().ToArray() ?? Array.Empty<object>();

            switch (value)
            {
                case bool b:
                    return !b;

                case Enum e:
                    return offered.Concat(Enum.GetValues(e.GetType()).Cast<object>()).First(v => !EqualityComparer<object>.Default.Equals(v, e));

                case Colour4 colour:
                    return colour == Colour4.Red ? Colour4.Blue : Colour4.Red;

                case string s:
                    return offered.Cast<string>().FirstOrDefault(v => v != s) ?? s + "-probe";

                case float or double or int:
                    object max = current.GetType().GetProperty("MaxValue")!.GetValue(current)!;
                    object min = current.GetType().GetProperty("MinValue")!.GetValue(current)!;
                    return EqualityComparer<object>.Default.Equals(value, max) ? min : max;

                default:
                    throw new AssertionException($"No way to probe a {value?.GetType().Name} control");
            }
        }

        /// <summary>The items a dropdown offers, or null for any other control.</summary>
        private static IEnumerable? offeredItems(Drawable control) =>
            control.GetType().GetProperty(nameof(Dropdown<int>.Items))?.GetValue(control) as IEnumerable;

        /// <summary>
        /// The domain the control gives its setting, or null when it has no finite one: a colour swatch is a
        /// colour; a check box a bool; a slider the range its bindable allows in its precision's steps; a
        /// dropdown of an enum the members it offers, in declaration order (so reordering the dropdown is not
        /// a schema change). Anything else must be excluded.
        /// </summary>
        private static PresetDomain? domainOf(Drawable control, string key, IReadOnlyDictionary<string, INamedSettingSource> sources)
        {
            if (control is FormColourSwatch)
                return PresetDomain.Colour();

            object current = currentOf(control);
            object? value = current.GetType().GetProperty(nameof(Bindable<int>.Value))!.GetValue(current);

            switch (value)
            {
                case bool:
                    return PresetDomain.Bool();

                case Colour4:
                    return PresetDomain.Colour();

                case Enum e:
                    var offered = offeredItems(control)?.Cast<object>().Select(o => o.ToString()).ToHashSet() ?? new HashSet<string?>();
                    return PresetDomain.Enum(Enum.GetNames(e.GetType()).Where(n => offered.Count == 0 || offered.Contains(n)));

                case float or double or int:
                    int colon = key.IndexOf(':');
                    var setting = sources[key[..colon]].FindSetting(key[(colon + 1)..])!;
                    decimal read(string property) => Convert.ToDecimal(setting.GetType().GetProperty(property)!.GetValue(setting), CultureInfo.InvariantCulture);

                    try
                    {
                        return PresetDomain.Range(read("MinValue"), read("MaxValue"), read("Precision"));
                    }
                    catch (ArgumentException)
                    {
                        return null;
                    }

                default:
                    return null;
            }
        }

        private static Dictionary<string, string> snapshot(IReadOnlyDictionary<string, INamedSettingSource> sources)
        {
            var values = new Dictionary<string, string>();

            foreach (var (prefix, source) in sources)
            {
                foreach (string name in source.SettingNames)
                    values[$"{prefix}:{name}"] = string.Format(CultureInfo.InvariantCulture, "{0}", source.FindSetting(name));
            }

            return values;
        }

        /// <summary>Null when <paramref name="latest"/> describes the live controls exactly; otherwise what to do about it.</summary>
        private static string? compare(List<(string Caption, string Key, PresetDomain? Domain)> live, PresetSchema latest)
        {
            var problems = new List<string>();

            var duplicates = live.GroupBy(e => e.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            if (duplicates.Length > 0)
                problems.Add($"More than one Gameplay control writes {string.Join(", ", duplicates)}.");

            var covered = live.Where(e => !GameplayPresetSchemas.EXCLUDED.ContainsKey(e.Key)).ToList();

            foreach (var (caption, key, _) in covered.Where(e => e.Domain == null))
            {
                problems.Add($"\"{caption}\" ({key}) has no finite domain a share code can hold. Give it one (a slider needs a precision), "
                             + $"or add it to {nameof(GameplayPresetSchemas)}.{nameof(GameplayPresetSchemas.EXCLUDED)} with the reason presets leave it alone.");
            }

            var liveByKey = covered.Where(e => e.Domain != null).GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First());

            foreach (var entry in latest.Entries)
            {
                if (!liveByKey.TryGetValue(entry.Key, out var control))
                    problems.Add($"The latest schema covers {entry.Key}, but no Gameplay control writes it any more.");
                else if (!control.Domain!.Equals(entry.Domain))
                    problems.Add($"\"{control.Caption}\" ({entry.Key}) is now \"{control.Domain.Spec}\", but the latest schema encodes it as \"{entry.Domain.Spec}\".");
            }

            foreach (var (caption, key, domain) in liveByKey.Values.Where(e => latest.Find(e.Key) == null))
                problems.Add($"\"{caption}\" ({key}, {domain!.Spec}) is in Settings > Gameplay, but share codes do not cover it.");

            if (problems.Count == 0)
                return null;

            // What to append: the latest order kept (only digit positions move otherwise), what went dropped,
            // what changed re-ranged in place, what is new at the end.
            var next = latest.Entries.Where(e => liveByKey.ContainsKey(e.Key)).Select(e => new PresetSchemaEntry(e.Key, liveByKey[e.Key].Domain!))
                             .Concat(liveByKey.Values.Where(e => latest.Find(e.Key) == null).Select(e => new PresetSchemaEntry(e.Key, e.Domain!)));
            var schema = new PresetSchema(latest.Version + 1, next);

            return "Settings > Gameplay no longer matches the latest frozen share code schema:" + Environment.NewLine
                   + string.Join(Environment.NewLine, problems.Select(p => " - " + p)) + Environment.NewLine + Environment.NewLine
                   + $"Never edit a frozen schema. Append this to {nameof(GameplayPresetSchemas)}.{nameof(GameplayPresetSchemas.ALL)} (and add a frozen code fixture for it "
                   + "next to version 1's in GameplayPresetSchemaTest), or exclude the settings presets should not cover:" + Environment.NewLine + Environment.NewLine
                   + schema.ToSource();
        }
    }
}
