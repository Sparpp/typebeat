// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Gameplay/KeyCharMap.cs (regression-anchored).

using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// Physical keyboard layout the player's keycaps follow. osu!framework reports keys by physical
    /// position (scancode), so the letter under a key can differ from what the QWERTY position
    /// implies; the map corrects for that per layout.
    /// </summary>
    public enum KeyboardLayout
    {
        Qwerty,

        /// <summary>German/Central-European: the Y and Z keys are swapped relative to QWERTY.</summary>
        Qwertz,

        /// <summary>
        /// French: A↔Q and Z↔W are swapped relative to QWERTY, M sits on the QWERTY semicolon
        /// position, and the QWERTY M position carries ',' (outside the typeable surface).
        /// </summary>
        Azerty
    }

    /// <summary>
    /// Pure static map from a <see cref="Key"/> to the single character it produces on the restricted
    /// typing surface: letters a-z (lower-case by default, upper-cased when <c>shift</c> is held),
    /// digits 0-9 (top row and keypad), and space. Everything else maps to nothing. This is the
    /// one-function seam for a future <c>TextInputSource</c> swap; the only modifiers it interprets
    /// are Shift and Caps Lock (for letter case); callers still filter Ctrl/Alt. Case is applied
    /// AFTER the layout remap, so the produced capital always matches the keycap the player reads
    /// (e.g. AZERTY Q → 'A'). Only letters carry case; digits and space ignore both modifiers. Case
    /// only matters to gameplay under the Literate mod (<see cref="TypingEngine.CaseSensitive"/>);
    /// otherwise the caret folds it away.
    ///
    /// <para><b>Caps Lock follows a real keyboard exactly.</b> For LETTERS the effective case is
    /// <c>shift XOR capsLock</c>: caps alone capitalises, and Shift held with caps ON produces the
    /// LOWER case letter. For everything else (digits, space and the punctuation surface below) caps
    /// is ignored entirely and Shift alone decides, because Caps Lock does not shift a digit-row or
    /// punctuation key on any real keyboard: with caps on, plain 4 is still '4' and Shift+4 is still
    /// '$'. Before backlog 205 the map read Shift alone, so under Literate a player capitalising with
    /// Caps Lock produced lower-case characters and every capital in the lyric read as a typo.</para>
    ///
    /// <para>The <c>punctuation</c> overload widens the surface to the supported
    /// <see cref="Beatmaps.Typeability.PUNCTUATION"/> marks. It is opt-in and OFF everywhere except
    /// under the Literate mod (<see cref="TypingEngine.Literate"/>), which is the only mode where a
    /// mark is a cell the player must produce. Keeping it off by default is what preserves two
    /// existing properties exactly: a habitual comma stays inert (never a wrong-key combo break),
    /// and Shift+digit still yields the digit rather than the mark above it ('!', '$', '(', ')').</para>
    /// </summary>
    public static class KeyCharMap
    {
        public static bool TryMap(Key key, out char c) => TryMap(key, KeyboardLayout.Qwerty, false, out c);

        public static bool TryMap(Key key, KeyboardLayout layout, out char c) => TryMap(key, layout, false, out c);

        public static bool TryMap(Key key, KeyboardLayout layout, bool shift, out char c) => TryMap(key, layout, shift, false, out c);

        public static bool TryMap(Key key, KeyboardLayout layout, bool shift, bool punctuation, out char c) => TryMap(key, layout, shift, punctuation, false, out c);

        // capsLock: whether the Caps Lock toggle is currently ON. Affects LETTERS only, where it
        // XORs with shift (see the type doc). Callers with no readable toggle state pass false,
        // which is exactly the pre-Caps-Lock, Shift-only behaviour.
        public static bool TryMap(Key key, KeyboardLayout layout, bool shift, bool punctuation, bool capsLock, out char c)
        {
            // Checked FIRST so a shifted digit can produce its mark ('!' on 1, '(' on 9, ')' on 0);
            // unshifted digits fall straight through to the digit below. Caps Lock is deliberately
            // NOT passed in: on a real keyboard it does not shift a digit-row or punctuation key,
            // so the mark above one is reachable by Shift and only by Shift.
            if (punctuation && tryMapPunctuation(key, layout, shift, out c))
                return true;

            if (!tryMapLower(key, layout, out c))
                return false;

            // Case applies to letters only; digits/space have no case, so a stray Caps Lock can
            // never turn one into an un-typeable char. XOR, not OR, because that is what a real
            // keyboard does: Shift held with Caps Lock ON types the LOWER case letter. In
            // case-insensitive play the caret folds all of this back to lower-case, so it is a
            // no-op there; under the Literate mod it is what lets the player produce the capitals
            // the target demands, by either route.
            if (shift != capsLock && c >= 'a' && c <= 'z')
                c = (char)(c - ('a' - 'A'));

            return true;
        }

        /// <summary>
        /// The supported punctuation marks, by their US-QWERTY physical positions. Every mark in
        /// <see cref="Beatmaps.Typeability.PUNCTUATION"/> is reachable and nothing else is produced:
        /// a key position with no mark on either of its two US-QWERTY legends stays inert.
        /// Keypads are deliberately not covered (no KeypadMultiply for '*', no KeypadDivide for
        /// '/'), matching the top-row-only convention the table has always followed.
        ///
        /// <para>PER-LAYOUT CORRECTIONS. Exactly one layout is corrected, AZERTY, and only for the
        /// six physical positions whose French legend differs from the US one. Its bottom row sits
        /// one position to the LEFT of the US row, so:
        /// <list type="bullet">
        /// <item>the QWERTY semicolon position is the 'm' KEYCAP, so this table must not claim it
        /// at all: it falls through to <see cref="tryMapLower"/>, which produces the letter (and
        /// the shift XOR caps pass above then produces its capital). Claiming it here is what made
        /// every lyric containing an 'm' uncompletable under Literate on AZERTY, since the key
        /// produced ';' instead;</item>
        /// <item>the QWERTY M, Comma, Period and Slash positions carry ',' ';' ':' '!' unshifted
        /// and '?' '.' '/' and the section sign shifted;</item>
        /// <item>the ISO key to the left of the bottom row, which US keyboards do not have, carries
        /// the two angle brackets, whose US home (shifted Comma and Period) is taken above.</item>
        /// </list>
        /// The section sign is outside <see cref="Beatmaps.Typeability.PUNCTUATION"/>, so that one
        /// combination stays INERT rather than producing something else: a corrected position never
        /// yields a mark the player cannot read off the keycap. Every supported mark stays reachable
        /// on AZERTY.</para>
        ///
        /// <para>KNOWN LIMIT: nothing else is remapped, because punctuation positions differ far
        /// more widely across layouts than letters do. AZERTY's own digit row is the clearest
        /// remainder (there the marks are unshifted and the digits shifted, the reverse of the table
        /// below), and other non-US layouts will still find some marks on the wrong physical key
        /// under Literate; a full per-layout punctuation table is the fix.</para>
        /// </summary>
        private static bool tryMapPunctuation(Key key, KeyboardLayout layout, bool shift, out char c)
        {
            if (layout == KeyboardLayout.Azerty)
            {
                switch (key)
                {
                    case Key.Semicolon:
                        // The 'm' keycap, not a punctuation key at all here. Leave it to the
                        // letter map rather than shadowing it with the US legend's ';'.
                        c = default;
                        return false;

                    case Key.M:
                        c = shift ? '?' : ',';
                        return true;

                    case Key.Comma:
                        c = shift ? '.' : ';';
                        return true;

                    case Key.Period:
                        c = shift ? '/' : ':';
                        return true;

                    case Key.Slash:
                        // Shifted this is the section sign, outside the supported set, so inert.
                        c = shift ? default : '!';
                        return c != default;

                    case Key.NonUSBackSlash:
                        c = shift ? '>' : '<';
                        return true;
                }
            }

            c = key switch
            {
                Key.Comma => shift ? '<' : ',',
                Key.Period => shift ? '>' : '.',
                Key.Quote => shift ? '"' : '\'',
                Key.Minus => shift ? default : '-',
                Key.Slash => shift ? '?' : '/',
                Key.Semicolon => shift ? ':' : ';',
                Key.BracketLeft => shift ? default : '[',
                Key.BracketRight => shift ? default : ']',
                Key.Number1 => shift ? '!' : default,
                Key.Number4 => shift ? '$' : default,
                Key.Number5 => shift ? '%' : default,
                Key.Number6 => shift ? '^' : default,
                Key.Number8 => shift ? '*' : default,
                Key.Number9 => shift ? '(' : default,
                Key.Number0 => shift ? ')' : default,
                _ => default
            };

            return c != default;
        }

        private static bool tryMapLower(Key key, KeyboardLayout layout, out char c)
        {
            // Keys arrive by PHYSICAL position, so on non-QWERTY layouts the keycap a player
            // reads can differ from the position's QWERTY letter; remap so what they press
            // matches what they see.
            switch (layout)
            {
                case KeyboardLayout.Qwertz:
                    // Y and Z are swapped.
                    if (key == Key.Y)
                    {
                        c = 'z';
                        return true;
                    }

                    if (key == Key.Z)
                    {
                        c = 'y';
                        return true;
                    }

                    break;

                case KeyboardLayout.Azerty:
                    switch (key)
                    {
                        case Key.Q:
                            c = 'a';
                            return true;

                        case Key.A:
                            c = 'q';
                            return true;

                        case Key.W:
                            c = 'z';
                            return true;

                        case Key.Z:
                            c = 'w';
                            return true;

                        case Key.Semicolon:
                            // The M keycap sits on the QWERTY semicolon position.
                            c = 'm';
                            return true;

                        case Key.M:
                            // This position carries ',' on AZERTY, outside the typeable
                            // surface, so the key is inert like other punctuation keys
                            // (never a wrong-key combo break for a habitual comma).
                            c = default;
                            return false;
                    }

                    break;
            }

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
