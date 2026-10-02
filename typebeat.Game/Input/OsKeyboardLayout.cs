// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;

namespace typebeat.Game.Input
{
    /// <summary>
    /// The keycap layout the OS's own keyboard map says the player has (backlog 383), which is what
    /// every shortcut follows (<see cref="KeycapLayout"/>, backlog 371). It replaced the
    /// "Keyboard layout" setting: gameplay typing reads the OS's committed text since 383, so the
    /// setting no longer decided anything about typing, and the one thing it still decided, which
    /// physical key a letter shortcut answers to, the OS can say on its own.
    ///
    /// <para>HOW. osu!framework reports keys by PHYSICAL position (the scancode) and drops the SDL
    /// keycode, so the input events themselves cannot say what a key's keycap is. The layout-aware
    /// half is still reachable, through the host's <see cref="ReadableKeyCombinationProvider"/>:
    /// on SDL3 hosts it names a key by asking the OS layout what that physical position carries
    /// (<c>SDL_GetKeyFromScancode</c>), which is how the settings screen has always printed key names
    /// in the player's own layout. <see cref="Detect"/> asks it about the letter positions the
    /// supported layouts move and recognises QWERTZ (Y and Z swapped) and AZERTY (A and Q, Z and W
    /// swapped). Anything else, a headless host's provider (which names keys after their enum) and a
    /// non-Latin layout (whose letters are not A-Z) included, is QWERTY, which is the identity.</para>
    ///
    /// <para>LIVE. The host raises <see cref="ReadableKeyCombinationProvider.KeymapChanged"/> when the
    /// OS layout changes, and the detection runs again, so switching layouts mid-session moves the
    /// shortcuts with it. A key held across the change is still released as the key it was pressed as
    /// (<see cref="KeycapKeyRewriter"/>).</para>
    ///
    /// <para>MIGRATION. The setting's <c>KeyboardLayout</c> line in game.ini is now an unknown key: the
    /// ini loader skips keys it cannot parse into an <c>OsuSetting</c> and the next save rewrites the
    /// file without it, so nothing has to run. A <c>KeyboardLayout</c> row left in the type!beat
    /// ruleset's settings by a build before backlog 371 (which used to carry it across) is an orphan
    /// the ruleset config never reads.</para>
    /// </summary>
    public class OsKeyboardLayout
    {
        /// <summary>
        /// The detected layout. Settable so a test can stand in for an OS with a different keyboard
        /// map; in the game only <see cref="Detect"/> writes it.
        /// </summary>
        public readonly Bindable<KeyboardLayout> Current = new Bindable<KeyboardLayout>();

        /// <param name="osKeymap">The HOST's provider (the one that asks the OS layout), or null for none.</param>
        /// <param name="schedule">How a keymap change gets back onto the update thread (the host raises it on the window thread).</param>
        public OsKeyboardLayout(ReadableKeyCombinationProvider? osKeymap, Action<Action> schedule)
        {
            if (osKeymap == null)
                return;

            Current.Value = Detect(osKeymap);
            osKeymap.KeymapChanged += () => schedule(() => Current.Value = Detect(osKeymap));
        }

        /// <summary>
        /// The supported layout <paramref name="osKeymap"/> describes: QWERTZ when the physical Y and Z
        /// positions carry each other's letters, AZERTY when A and Q, and Z and W, do, QWERTY otherwise.
        /// </summary>
        public static KeyboardLayout Detect(ReadableKeyCombinationProvider osKeymap)
        {
            bool carries(InputKey position, char letter)
            {
                string name = osKeymap.GetReadableString(new KeyCombination(position));
                return name.Length == 1 && char.ToUpperInvariant(name[0]) == letter;
            }

            if (carries(InputKey.Y, 'Z') && carries(InputKey.Z, 'Y'))
                return KeyboardLayout.Qwertz;

            if (carries(InputKey.Q, 'A') && carries(InputKey.A, 'Q') && carries(InputKey.W, 'Z') && carries(InputKey.Z, 'W'))
                return KeyboardLayout.Azerty;

            return KeyboardLayout.Qwerty;
        }
    }
}
