// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Platform;

namespace typebeat.Game.Configuration.Presets
{
    /// <summary>
    /// Gameplay presets (backlog 484): Minimal, the player's named presets and Custom, which of them is
    /// selected, and what selecting, saving, importing or exporting one does to the settings in
    /// Settings &gt; Gameplay that presets cover (the latest schema's, <see cref="GameplayPresetSchemas"/>).
    ///
    /// <list type="bullet">
    /// <item>Selecting an entry writes its values to every covered setting, and nothing else (the
    /// excluded settings are not even attached).</item>
    /// <item>Changing a covered setting from ANYWHERE (the settings panel, a gameplay hotkey, the in-play
    /// visual settings) while Minimal or a named preset is selected switches the selection to Custom.
    /// Custom is the live settings while it is selected; when something else is selected it remembers
    /// what they were, so selecting Minimal and then Custom puts them back.</item>
    /// <item>Rename on Custom saves its values as a new named preset and selects it, so the next change
    /// starts a fresh Custom. Deleting the selected preset selects Custom without touching a setting.</item>
    /// </list>
    ///
    /// <para>STORAGE: one JSON file in the game's storage (<see cref="FILENAME"/>), each preset kept as its
    /// share code. Not game.ini, because "Reset all settings" resets that wholesale, and presets are the
    /// player's own content, like skins, rather than preferences; not Realm, because it would need a
    /// schema migration for a handful of strings. Keeping codes means stored presets outlive Gameplay
    /// changes exactly as pasted codes do. "Reset all settings" keeps every named preset, remembers the
    /// settings it is about to reset as Custom (when Custom was selected), and selects Minimal, which is
    /// precisely what the reset leaves the covered settings at (<see cref="ResetAllSettings"/>).</para>
    ///
    /// <para>MIGRATION: the first run of a build with presets has no file yet. An install that has run
    /// before (<see cref="HasRunBefore"/>) selects Custom and changes nothing; a new install selects
    /// Minimal. Later, an install whose settings moved while nothing was watching (a default changed by
    /// an update, an older build, a hand-edited file) is relabelled Custom rather than moved.</para>
    /// </summary>
    public partial class GameplayPresetManager : Component
    {
        public const string FILENAME = "gameplay-presets.json";

        public const string MINIMAL_NAME = "Minimal";

        public const string CUSTOM_NAME = "Custom";

        /// <summary>Imported presets are named this and a number: "Imported 1", "Imported 2", ...</summary>
        public const string IMPORTED_NAME = "Imported";

        public const int MAX_NAME_LENGTH = 40;

        public GameplayPreset Minimal { get; } = new GameplayPreset(GameplayPresetKind.Minimal, MINIMAL_NAME, null);

        public GameplayPreset Custom { get; } = new GameplayPreset(GameplayPresetKind.Custom, CUSTOM_NAME, null);

        /// <summary>The selected entry. Changes only through this manager's methods.</summary>
        public IBindable<GameplayPreset> Current => current;

        /// <summary>The dropdown's entries: Minimal, the named presets in the order they were made, then Custom.</summary>
        public IReadOnlyList<GameplayPreset> Entries => entries;

        /// <summary>Raised after <see cref="Entries"/> changed.</summary>
        public event Action? EntriesChanged;

        /// <summary>The covered settings, attached to their live configs. Empty until initialised.</summary>
        public IReadOnlyList<LiveGameplaySetting> Settings => settings;

        public bool IsInitialised { get; private set; }

        private readonly Bindable<GameplayPreset> current;
        private readonly Storage storage;
        private readonly bool hasRunBefore;
        private readonly Func<PresetSchema, IReadOnlyList<LiveGameplaySetting>> resolveSettings;
        private readonly IReadOnlyList<PresetSchema> schemas;
        private readonly PresetSchema latest;

        private readonly List<GameplayPreset> saved = new List<GameplayPreset>();

        // Stored presets this build cannot decode (written by a newer build, then downgraded): kept in the
        // file untouched for the build that can, and their names stay taken.
        private readonly List<StoredPreset> unreadable = new List<StoredPreset>();

        private List<GameplayPreset> entries;
        private IReadOnlyList<LiveGameplaySetting> settings = Array.Empty<LiveGameplaySetting>();
        private Dictionary<string, LiveGameplaySetting> settingsByKey = new Dictionary<string, LiveGameplaySetting>();

        // Custom's values while another entry is selected. While Custom IS selected the live settings are
        // its values, so this is only refreshed on the way out (no write on every slider tick).
        private string? customCode;

        private int applying;

        /// <param name="storage">Where <see cref="FILENAME"/> lives.</param>
        /// <param name="hasRunBefore">Whether this install has run before; decides the first selection (see the class doc).</param>
        /// <param name="resolveSettings">Attaches the covered settings of the schema it is given (the latest); called once, on initialisation, when every config exists.</param>
        /// <param name="schemas">The frozen schemas, oldest first. <see cref="GameplayPresetSchemas.ALL"/> unless a test stands in its own.</param>
        public GameplayPresetManager(Storage storage, bool hasRunBefore, Func<PresetSchema, IReadOnlyList<LiveGameplaySetting>> resolveSettings, IReadOnlyList<PresetSchema>? schemas = null)
        {
            this.storage = storage;
            this.hasRunBefore = hasRunBefore;
            this.resolveSettings = resolveSettings;
            this.schemas = schemas ?? GameplayPresetSchemas.ALL;
            latest = this.schemas[^1];

            current = new Bindable<GameplayPreset>(Minimal);
            entries = new List<GameplayPreset> { Minimal, Custom };
        }

        /// <summary>
        /// Whether the game has run on this install before: the version stamp is empty only until the
        /// first launch writes it. Must be read before <c>OsuGame</c> stamps the running version.
        /// </summary>
        public static bool HasRunBefore(OsuConfigManager config) => !string.IsNullOrEmpty(config.Get<string>(OsuSetting.Version));

        protected override void LoadComplete()
        {
            base.LoadComplete();
            EnsureInitialised();
        }

        /// <summary>
        /// Attaches the settings and reads the stored presets, once. The game does this as soon as the
        /// ruleset configs exist (this component loads after them); anything using the manager before
        /// that calls it first.
        /// </summary>
        public void EnsureInitialised()
        {
            if (IsInitialised)
                return;

            IsInitialised = true;

            settings = resolveSettings(latest);
            settingsByKey = settings.ToDictionary(s => s.Key);

            foreach (var setting in settings)
                setting.Changed += onSettingChanged;

            var stored = read();

            if (stored == null)
                startFresh();
            else
                restore(stored);

            rebuildEntries();
            write();
        }

        public bool CanRename(GameplayPreset preset) => preset.Kind != GameplayPresetKind.Minimal;

        public bool CanDelete(GameplayPreset preset) => preset.Kind == GameplayPresetKind.Saved;

        /// <summary>Selects <paramref name="preset"/> and writes its values to the covered settings.</summary>
        public void Select(GameplayPreset preset)
        {
            EnsureInitialised();

            if (preset == current.Value || !entries.Contains(preset))
                return;

            string code = codeOf(preset);

            rememberCustom();
            applyValues(valuesOf(code));
            current.Value = preset;
            write();
        }

        /// <summary>
        /// Rename on Custom: saves the live settings as a new named preset and selects it. Returns null
        /// when Custom is not selected or the name is blank.
        /// </summary>
        public GameplayPreset? SaveCustomAs(string name)
        {
            EnsureInitialised();

            if (current.Value != Custom || uniqueName(name, null) is not string unique)
                return null;

            string code = encodeLive();
            var preset = new GameplayPreset(GameplayPresetKind.Saved, unique, code);

            customCode = code;
            saved.Add(preset);
            rebuildEntries();
            current.Value = preset;
            write();
            return preset;
        }

        /// <summary>Renames a named preset. Returns the renamed entry (which replaces it), or null when it cannot be renamed.</summary>
        public GameplayPreset? Rename(GameplayPreset preset, string name)
        {
            EnsureInitialised();

            int index = saved.IndexOf(preset);

            if (index < 0 || uniqueName(name, preset) is not string unique)
                return null;

            if (unique == preset.Name)
                return preset;

            var renamed = new GameplayPreset(GameplayPresetKind.Saved, unique, preset.Code);
            saved[index] = renamed;

            if (current.Value == preset)
                current.Value = renamed;

            rebuildEntries();
            write();
            return renamed;
        }

        /// <summary>
        /// Deletes a named preset. Deleting the selected one selects Custom and leaves every setting where
        /// it is: the player still has what they were playing with, now under Custom.
        /// </summary>
        public bool Delete(GameplayPreset preset)
        {
            EnsureInitialised();

            if (!CanDelete(preset) || !saved.Contains(preset))
                return false;

            if (current.Value == preset)
                current.Value = Custom;

            saved.Remove(preset);
            rebuildEntries();
            write();
            return true;
        }

        /// <summary>The share code of <paramref name="preset"/> (for Custom while selected, the live settings).</summary>
        public string Export(GameplayPreset preset)
        {
            EnsureInitialised();
            return codeOf(preset);
        }

        /// <summary>
        /// Imports a pasted share code: on success adds it as "Imported n" and selects it (applying it);
        /// on failure changes nothing and says why.
        /// </summary>
        public ImportResult Import(string? text)
        {
            EnsureInitialised();

            var error = GameplayShareCode.TryDecode(text, schemas, out var schema, out int[] digits);

            if (error != ShareCodeError.None)
                return new ImportResult(null, error);

            var preset = new GameplayPreset(GameplayPresetKind.Saved, nextImportedName(), encodeValues(valuesOf(schema!, digits)));

            saved.Add(preset);
            rebuildEntries();
            Select(preset);
            return new ImportResult(preset, ShareCodeError.None);
        }

        /// <summary>
        /// Maintenance, "Reset all settings": runs <paramref name="reset"/>, which puts every setting back
        /// on its default, and selects Minimal, which those defaults are. Named presets are kept, and when
        /// Custom was selected its values are remembered first, so selecting Custom undoes the reset for
        /// the settings presets cover.
        /// </summary>
        public void ResetAllSettings(Action reset)
        {
            EnsureInitialised();
            rememberCustom();

            applying++;

            try
            {
                reset();

                // Normally a no-op: makes "Minimal" exact even if a covered setting is ever kept by a reset.
                applyValues(new Dictionary<string, object>());
            }
            finally
            {
                applying--;
            }

            current.Value = Minimal;
            write();
        }

        public static string MessageFor(ShareCodeError error)
        {
            switch (error)
            {
                case ShareCodeError.Empty:
                    return "Paste a share code to import it.";

                case ShareCodeError.InvalidCharacters:
                    return "That is not a share code: share codes are only letters and digits.";

                case ShareCodeError.NewerVersion:
                    return "That code was made by a newer version of type!beat. Update the game to import it.";

                case ShareCodeError.Invalid:
                    return "That is not a valid share code. Check that the whole code was copied.";

                default:
                    return string.Empty;
            }
        }

        private void onSettingChanged()
        {
            if (applying > 0 || current.Value == Custom)
                return;

            current.Value = Custom;
            write();
        }

        private void startFresh()
        {
            if (hasRunBefore)
            {
                // The settings as the player left them, labelled Custom; nothing moves.
                current.Value = Custom;
            }
            else
            {
                // A new install's settings are already the defaults; applying them makes the label exact.
                applyValues(new Dictionary<string, object>());
                current.Value = Minimal;
            }

            customCode = encodeLive();
        }

        private void restore(StoredState stored)
        {
            foreach (var preset in stored.Presets ?? new List<StoredPreset>())
            {
                if (string.IsNullOrWhiteSpace(preset.Name) || string.IsNullOrEmpty(preset.Code))
                    continue;

                var values = tryValuesOf(preset.Code);

                if (values == null)
                {
                    unreadable.Add(preset);
                    continue;
                }

                if (uniqueName(preset.Name, null) is string name)
                    saved.Add(new GameplayPreset(GameplayPresetKind.Saved, name, encodeValues(values)));
            }

            customCode = tryValuesOf(stored.Custom) is Dictionary<string, object> custom ? encodeValues(custom) : null;

            GameplayPreset selected;

            switch (stored.Selected)
            {
                case StoredState.MINIMAL:
                    selected = Minimal;
                    break;

                case StoredState.SAVED:
                    selected = saved.FirstOrDefault(p => p.Name == stored.SelectedName) ?? Custom;
                    break;

                default:
                    selected = Custom;
                    break;
            }

            // The selection must describe the settings as they are, without moving them.
            if (selected != Custom && codeOf(selected) != encodeLive())
                selected = Custom;

            current.Value = selected;
            customCode ??= encodeLive();
        }

        private void rememberCustom()
        {
            if (current.Value == Custom)
                customCode = encodeLive();
        }

        private string codeOf(GameplayPreset preset)
        {
            switch (preset.Kind)
            {
                case GameplayPresetKind.Minimal:
                    return encodeValues(new Dictionary<string, object>());

                case GameplayPresetKind.Custom:
                    return current.Value == Custom ? encodeLive() : customCode ?? encodeLive();

                default:
                    return preset.Code!;
            }
        }

        /// <summary>
        /// Writes <paramref name="values"/> (domain values by key, from any schema) to the covered
        /// settings: each snapped into its live domain, the default where a value is missing or cannot
        /// be read. Two passes and then a lifted gate for whatever is still greyed out, exactly like
        /// "Reset all settings" (see <see cref="ConfigDefaults.Reset{TLookup}"/>): one covered setting can
        /// grey out another, and a greyed-out setting refuses a value.
        /// </summary>
        private void applyValues(IReadOnlyDictionary<string, object> values)
        {
            applying++;

            try
            {
                var pending = settings.Select(s => (setting: s, target: s.Domain.ValueOf(digitFor(s, values)))).ToList();

                for (int pass = 0; pass < 2 && pending.Count > 0; pass++)
                {
                    var deferred = new List<(LiveGameplaySetting setting, object target)>();

                    foreach (var (setting, target) in pending)
                    {
                        if (setting.Disabled)
                            deferred.Add((setting, target));
                        else if (!setting.Holds(target))
                            setting.SetValue(target);
                    }

                    pending = deferred;
                }

                foreach (var (setting, target) in pending)
                {
                    if (setting.Holds(target))
                        continue;

                    setting.Disabled = false;
                    setting.SetValue(target);
                    setting.Disabled = true;
                }
            }
            finally
            {
                applying--;
            }
        }

        private static int digitFor(LiveGameplaySetting setting, IReadOnlyDictionary<string, object> values) =>
            values.TryGetValue(setting.Key, out object? value) && setting.Domain.DigitOf(value) is int digit ? digit : setting.DefaultDigit;

        /// <summary>The live settings as a code in the latest schema.</summary>
        private string encodeLive() =>
            GameplayShareCode.Encode(latest, latest.Entries.Select(e => settingsByKey.TryGetValue(e.Key, out var s) ? s.CurrentDigit : 0).ToArray());

        /// <summary>
        /// <paramref name="values"/> as a code in the latest schema: what applying them would leave the
        /// settings at, so a code from any schema re-encodes to the one the live settings would export.
        /// </summary>
        private string encodeValues(IReadOnlyDictionary<string, object> values)
        {
            var schema = latest;

            return GameplayShareCode.Encode(schema, schema.Entries.Select(e =>
            {
                if (settingsByKey.TryGetValue(e.Key, out var setting))
                    return digitFor(setting, values);

                return values.TryGetValue(e.Key, out object? value) && e.Domain.DigitOf(value) is int digit ? digit : 0;
            }).ToArray());
        }


        /// <summary>Every value <paramref name="digits"/> holds, by key, as <paramref name="schema"/> lays them out.</summary>
        private static Dictionary<string, object> valuesOf(PresetSchema schema, int[] digits) =>
            schema.Entries.Select((e, i) => (e.Key, value: e.Domain.ValueOf(digits[i]))).ToDictionary(p => p.Key, p => p.value);

        private Dictionary<string, object>? tryValuesOf(string? code) =>
            GameplayShareCode.TryDecode(code, schemas, out var schema, out int[] digits) == ShareCodeError.None ? valuesOf(schema!, digits) : null;

        // Every code this manager holds is one it made, so it always decodes.
        private Dictionary<string, object> valuesOf(string code) => tryValuesOf(code) ?? new Dictionary<string, object>();

        private string? uniqueName(string? raw, GameplayPreset? renaming)
        {
            string name = raw?.Trim() ?? string.Empty;

            if (name.Length > MAX_NAME_LENGTH)
                name = name[..MAX_NAME_LENGTH].TrimEnd();

            if (name.Length == 0)
                return null;

            string candidate = name;

            for (int n = 2; isTaken(candidate, renaming); n++)
                candidate = $"{name} ({n})";

            return candidate;
        }

        private bool isTaken(string name, GameplayPreset? renaming) =>
            same(name, MINIMAL_NAME)
            || same(name, CUSTOM_NAME)
            || saved.Any(p => p != renaming && same(p.Name, name))
            || unreadable.Any(p => same(p.Name!, name));

        private static bool same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private string nextImportedName()
        {
            int n = 1;

            while (isTaken($"{IMPORTED_NAME} {n}", null))
                n++;

            return $"{IMPORTED_NAME} {n}";
        }

        private void rebuildEntries()
        {
            entries = new List<GameplayPreset> { Minimal };
            entries.AddRange(saved);
            entries.Add(Custom);

            EntriesChanged?.Invoke();
        }

        private StoredState? read()
        {
            if (!storage.Exists(FILENAME))
                return null;

            string text = string.Empty;

            try
            {
                using (var stream = storage.GetStream(FILENAME))
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();

                return JsonConvert.DeserializeObject<StoredState>(text) ?? throw new JsonException("The file is empty.");
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Gameplay presets could not be read from {FILENAME}; it was kept as {FILENAME}.bad and presets start over.");

                try
                {
                    using (var stream = storage.CreateFileSafely(FILENAME + ".bad"))
                    using (var writer = new StreamWriter(stream))
                        writer.Write(text);
                }
                catch
                {
                    // Keeping the unreadable file is a courtesy; starting over does not depend on it.
                }

                return null;
            }
        }

        private void write()
        {
            if (!IsInitialised)
                return;

            var state = new StoredState
            {
                Selected = current.Value.Kind switch
                {
                    GameplayPresetKind.Minimal => StoredState.MINIMAL,
                    GameplayPresetKind.Saved => StoredState.SAVED,
                    _ => StoredState.CUSTOM,
                },
                SelectedName = current.Value.Kind == GameplayPresetKind.Saved ? current.Value.Name : null,
                Custom = current.Value == Custom ? encodeLive() : customCode,
                Presets = saved.Select(p => new StoredPreset { Name = p.Name, Code = p.Code }).Concat(unreadable).ToList(),
            };

            try
            {
                using (var stream = storage.CreateFileSafely(FILENAME))
                using (var writer = new StreamWriter(stream))
                    writer.Write(JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch (Exception e)
            {
                Logger.Error(e, $"Gameplay presets could not be saved to {FILENAME}.");
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            foreach (var setting in settings)
                setting.Changed -= onSettingChanged;
        }

        public readonly struct ImportResult
        {
            /// <summary>The imported preset, now selected; null when the code was refused.</summary>
            public readonly GameplayPreset? Preset;

            public readonly ShareCodeError Error;

            public ImportResult(GameplayPreset? preset, ShareCodeError error)
            {
                Preset = preset;
                Error = error;
            }

            public bool Succeeded => Preset != null;

            public string Message => MessageFor(Error);
        }

        private class StoredState
        {
            public const string MINIMAL = "minimal";
            public const string SAVED = "saved";
            public const string CUSTOM = "custom";

            [JsonProperty("selected")]
            public string? Selected { get; set; }

            [JsonProperty("selected_name")]
            public string? SelectedName { get; set; }

            /// <summary>
            /// Custom's values as of the last write. While Custom is selected the live settings (which
            /// their own configs persist) are its values, and this is only what they were last time.
            /// </summary>
            [JsonProperty("custom")]
            public string? Custom { get; set; }

            [JsonProperty("presets")]
            public List<StoredPreset>? Presets { get; set; }
        }

        private class StoredPreset
        {
            [JsonProperty("name")]
            public string? Name { get; set; }

            [JsonProperty("code")]
            public string? Code { get; set; }
        }
    }
}
