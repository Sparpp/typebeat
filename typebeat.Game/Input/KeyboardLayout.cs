// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Input
{
    /// <summary>
    /// The keycap layouts SHORTCUTS follow (backlog 371). osu!framework reports keys by physical
    /// position (scancode), so the letter printed on a key can differ from the QWERTY letter its
    /// position implies. <see cref="KeycapLayout"/> turns a physical letter key into the key carrying
    /// the same KEYCAP letter on QWERTY, once, at the root input manager, which is what makes every
    /// shortcut follow the keycap.
    ///
    /// <para>Since backlog 383 this is DETECTED from the OS's own keyboard map
    /// (<see cref="OsKeyboardLayout"/>) rather than chosen in settings, and it no longer has anything
    /// to do with typing: gameplay types the characters the OS commits. Only the three layouts whose
    /// letter moves the shortcut rewrite knows are named; every other layout is treated as QWERTY,
    /// whose rewrite is the identity.</para>
    ///
    /// <para>The member NAMES were once stored (in game.ini, and before that in the ruleset's own
    /// settings row), so they are kept as they were.</para>
    /// </summary>
    public enum KeyboardLayout
    {
        Qwerty,

        /// <summary>German/Central-European: the Y and Z keys are swapped relative to QWERTY.</summary>
        Qwertz,

        /// <summary>French: A↔Q and Z↔W are swapped relative to QWERTY, and M sits on the QWERTY semicolon position.</summary>
        Azerty
    }
}
