// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Gameplay/KeyCharMap.cs (regression-anchored).

using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// Pure static map from a <see cref="Key"/> to the single character it produces on the restricted
    /// typing surface: letters a-z (case-insensitive, always lowercase), digits 0-9 (top row and keypad),
    /// and space. Everything else maps to nothing. This is the one-function seam for a future
    /// <c>TextInputSource</c> swap; it contains no modifier logic — callers filter Ctrl/Alt.
    /// </summary>
    public static class KeyCharMap
    {
        public static bool TryMap(Key key, out char c)
        {
            // Key.A..Key.Z are contiguous in osuTK.Input.Key.
            if (key >= Key.A && key <= Key.Z)
            {
                c = (char)('a' + (key - Key.A));
                return true;
            }

            // Top-row Number0..Number9 are contiguous.
            if (key >= Key.Number0 && key <= Key.Number9)
            {
                c = (char)('0' + (key - Key.Number0));
                return true;
            }

            // Keypad0..Keypad9 are contiguous.
            if (key >= Key.Keypad0 && key <= Key.Keypad9)
            {
                c = (char)('0' + (key - Key.Keypad0));
                return true;
            }

            if (key == Key.Space)
            {
                c = ' ';
                return true;
            }

            c = default;
            return false;
        }
    }
}
