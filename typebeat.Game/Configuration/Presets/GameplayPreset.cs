// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Configuration.Presets
{
    public enum GameplayPresetKind
    {
        /// <summary>Built in and read-only: every covered setting at its default.</summary>
        Minimal,

        /// <summary>A preset the player named, by saving Custom or by importing a share code.</summary>
        Saved,

        /// <summary>Whatever the player has set: the live settings while selected, remembered while not.</summary>
        Custom,
    }

    /// <summary>
    /// One entry of the Gameplay presets dropdown. Immutable: a rename makes a new entry, so a dropdown
    /// never shows a stale name for an item it still holds.
    /// </summary>
    public sealed class GameplayPreset
    {
        public GameplayPresetKind Kind { get; }

        public string Name { get; }

        /// <summary>A <see cref="GameplayPresetKind.Saved"/> preset's values, as a share code in the latest schema.</summary>
        internal string? Code { get; }

        internal GameplayPreset(GameplayPresetKind kind, string name, string? code)
        {
            Kind = kind;
            Name = name;
            Code = code;
        }

        public override string ToString() => Name;
    }
}
