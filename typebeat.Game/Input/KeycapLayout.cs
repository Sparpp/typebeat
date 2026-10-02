// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Bindables;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.StateChanges;
using osuTK.Input;

namespace typebeat.Game.Input
{
    /// <summary>
    /// The one translation between a PHYSICAL key (what osu!framework reports: a scancode, named after
    /// the QWERTY letter at that position) and the KEYCAP key (the QWERTY key carrying the letter that
    /// is printed on that position under the player's <see cref="KeyboardLayout"/>).
    ///
    /// <para>Letters are the only keys that move between the supported layouts, and every move is a
    /// SWAP: AZERTY exchanges A and Q, Z and W, and M with the QWERTY semicolon position; QWERTZ
    /// exchanges Y and Z. So the translation is its own inverse, and every physical key has exactly one
    /// keycap key and vice versa, which is what lets a pressed key always be released as the same key
    /// (see <see cref="KeycapKeyRewriter"/>). The punctuation and digit rows do not move here; what
    /// they PRODUCE when typed is the OS's business: gameplay types the text the OS commits (backlog
    /// 383), which this translation never touches. The layout itself is read off the OS too
    /// (<see cref="OsKeyboardLayout"/>).</para>
    /// </summary>
    public static class KeycapLayout
    {
        /// <summary>The key whose keycap the player reads at the physical position <paramref name="physical"/>.</summary>
        public static Key ToKeycap(Key physical, KeyboardLayout layout) => swap(physical, layout);

        /// <summary>The physical position carrying the keycap of <paramref name="keycap"/>; the inverse of <see cref="ToKeycap"/>.</summary>
        public static Key ToPhysical(Key keycap, KeyboardLayout layout) => swap(keycap, layout);

        /// <summary>The <see cref="InputKey"/> form of <see cref="ToPhysical(Key, KeyboardLayout)"/>, for a stored key binding.</summary>
        public static InputKey ToPhysical(InputKey keycap, KeyboardLayout layout)
        {
            switch (layout)
            {
                case KeyboardLayout.Qwertz:
                    switch (keycap)
                    {
                        case InputKey.Y: return InputKey.Z;

                        case InputKey.Z: return InputKey.Y;
                    }

                    break;

                case KeyboardLayout.Azerty:
                    switch (keycap)
                    {
                        case InputKey.A: return InputKey.Q;

                        case InputKey.Q: return InputKey.A;

                        case InputKey.Z: return InputKey.W;

                        case InputKey.W: return InputKey.Z;

                        case InputKey.M: return InputKey.Semicolon;

                        case InputKey.Semicolon: return InputKey.M;
                    }

                    break;
            }

            return keycap;
        }

        private static Key swap(Key key, KeyboardLayout layout)
        {
            switch (layout)
            {
                case KeyboardLayout.Qwertz:
                    switch (key)
                    {
                        case Key.Y: return Key.Z;

                        case Key.Z: return Key.Y;
                    }

                    break;

                case KeyboardLayout.Azerty:
                    switch (key)
                    {
                        case Key.A: return Key.Q;

                        case Key.Q: return Key.A;

                        case Key.Z: return Key.W;

                        case Key.W: return Key.Z;

                        // The M keycap sits on the QWERTY semicolon position, and the QWERTY M position
                        // carries ',' (so it reads as the semicolon key, keeping the translation a swap).
                        case Key.M: return Key.Semicolon;

                        case Key.Semicolon: return Key.M;
                    }

                    break;
            }

            return key;
        }
    }

    /// <summary>
    /// Rewrites every keyboard key in a batch of pending inputs from its physical position to its
    /// keycap key (<see cref="KeycapLayout.ToKeycap"/>), BEFORE the input manager applies the batch to
    /// its state. Everything downstream (key-binding containers, the framework's platform actions,
    /// <c>OnKeyDown</c> handlers, and nested input managers, which copy this manager's state) then sees
    /// the keycap key and nothing else.
    ///
    /// <para>A release is always rewritten to whatever its press was rewritten to, so changing the
    /// layout while a key is held cannot leave a key stuck down.</para>
    /// </summary>
    public sealed class KeycapKeyRewriter
    {
        public readonly Bindable<KeyboardLayout> Layout = new Bindable<KeyboardLayout>();

        private readonly Dictionary<Key, Key> held = new Dictionary<Key, Key>();

        public void Rewrite(List<IInput> inputs)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                if (inputs[i] is KeyboardKeyInput keyInput)
                    inputs[i] = rewrite(keyInput);
            }
        }

        private KeyboardKeyInput rewrite(KeyboardKeyInput input)
        {
            var entries = new List<ButtonInputEntry<Key>>(input.Entries.Length);
            bool changed = false;

            foreach (var entry in input.Entries)
            {
                Key physical = entry.Button;
                Key keycap;

                if (entry.IsPressed)
                {
                    if (!held.TryGetValue(physical, out keycap))
                        held[physical] = keycap = KeycapLayout.ToKeycap(physical, Layout.Value);
                }
                else if (!held.Remove(physical, out keycap))
                    keycap = KeycapLayout.ToKeycap(physical, Layout.Value);

                changed |= keycap != physical;
                entries.Add(new ButtonInputEntry<Key>(keycap, entry.IsPressed));
            }

            return changed ? new KeyboardKeyInput(entries) : input;
        }
    }
}
