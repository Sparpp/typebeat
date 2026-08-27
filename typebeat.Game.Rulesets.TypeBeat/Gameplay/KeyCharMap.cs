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
    /// '$' (on the US digit row, which AZERTY reverses: see <see cref="mapPunctuation"/>).
    /// Before backlog 205 the map read Shift alone, so under Literate a player capitalising with
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
            //
            // Three-way, not two-way: a position the surface CLAIMS but whose legend for this
            // modifier state is outside the supported set must produce NOTHING rather than fall
            // through, because on AZERTY the fall-through would be the digit under the key, which
            // is not what the keycap shows (see mapPunctuation).
            if (punctuation)
            {
                switch (mapPunctuation(key, layout, shift, out c))
                {
                    case PunctuationMapping.Produced:
                        return true;

                    case PunctuationMapping.Inert:
                        c = default;
                        return false;
                }
            }

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
        /// What the punctuation surface does with one physical key position. Three-way rather than
        /// two-way because "this position is mine and produces nothing" is not the same answer as
        /// "this position is not mine": the latter falls through to <see cref="tryMapLower"/> and
        /// can produce a digit, which on a corrected layout is a key the player never pressed.
        /// </summary>
        private enum PunctuationMapping
        {
            /// <summary>Not a punctuation position on this layout: fall through to the letter/digit map.</summary>
            Unclaimed,

            /// <summary>A supported mark, returned in the out parameter.</summary>
            Produced,

            /// <summary>
            /// A punctuation position whose legend for this modifier state is outside
            /// <see cref="Beatmaps.Typeability.PUNCTUATION"/>. It produces nothing AND does not
            /// fall through.
            /// </summary>
            Inert,
        }

        /// <summary>
        /// The supported punctuation marks, by physical key position. Every mark in
        /// <see cref="Beatmaps.Typeability.PUNCTUATION"/> is reachable on every supported layout and
        /// nothing else is produced: a position with no supported mark on either of its two legends
        /// stays inert. Keypads are deliberately not covered (no KeypadMultiply for '*', no
        /// KeypadDivide for '/'), matching the top-row-only convention the table has always followed.
        ///
        /// <para>The table below is the US-QWERTY one. AZERTY has a complete table of its own,
        /// <see cref="mapAzertyPunctuation"/>, because its marks sit somewhere else entirely.
        /// QWERTZ still reads this one (see the KNOWN LIMIT there).</para>
        ///
        /// <para>THE RULE both tables obey: a position never yields a mark the player cannot read
        /// off the keycap in front of them. A mark the keycap does not show is a WRONG KEY, which
        /// costs a combo, a typo and HP, and any mark left with no key at all makes every lyric
        /// containing it uncompletable under Literate. Backlog 214 (the letter 'm') and backlog 215
        /// (the apostrophe) were both that failure.</para>
        /// </summary>
        private static PunctuationMapping mapPunctuation(Key key, KeyboardLayout layout, bool shift, out char c)
        {
            if (layout == KeyboardLayout.Azerty)
                return mapAzertyPunctuation(key, shift, out c);

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

            return c != default ? PunctuationMapping.Produced : PunctuationMapping.Unclaimed;
        }

        /// <summary>
        /// The French AZERTY punctuation table, complete: on this layout the marks are somewhere
        /// else on nearly every row, so the US table is not a base to patch but a table to replace.
        ///
        /// <list type="bullet">
        /// <item>DIGIT ROW, reversed: the marks are UNSHIFTED and the digits are SHIFTED. 3 4 5 6
        /// carry '"' '\'' '(' '-', and the key right of 0 (the US Minus position) carries ')'.
        /// Shift on any of the ten is the digit, which the letter map supplies by falling through,
        /// so 0-9 all stay reachable under Literate exactly as on the real keyboard. Backlog 215:
        /// the apostrophe living on 4 is why this row had to move. Before it, an AZERTY player
        /// pressing their own ' keycap got '4', so every lyric with a "don't" or an "I'm" in it,
        /// which is most of them, was uncompletable under Literate.</item>
        /// <item>TOP ROW: the US BracketRight position carries '$'; the US BracketLeft position
        /// carries the circumflex dead key, whose legend '^' IS a supported mark.</item>
        /// <item>HOME ROW: the US Semicolon position is the 'm' KEYCAP, so the table must not claim
        /// it at all (backlog 214: claiming it made every lyric containing an 'm' uncompletable).
        /// The US Quote position is the u-grave keycap, shifted '%'. The US BackSlash position is
        /// '*'.</item>
        /// <item>BOTTOM ROW, one position LEFT of the US one: the US M, Comma, Period and Slash
        /// positions carry ',' ';' ':' '!' unshifted and '?' '.' '/' and the section sign shifted,
        /// and the ISO key US keyboards do not have carries the two angle brackets, whose US home
        /// (shifted Comma and Period) is taken.</item>
        /// </list>
        ///
        /// <para>PARKED, the two deliberate exceptions to the keycap rule: '[' and ']' have no
        /// AZERTY home this map can reach, because theirs is AltGr+5 and AltGr+the-Minus-position
        /// and AltGr is not modelled at all. They sit on the SHIFTED US bracket positions, whose
        /// real legends (the diaeresis dead key and the pound sign) are outside the supported set,
        /// so nothing faithful is displaced and the US positional memory survives. Parking beats
        /// stranding: a mark with no key makes maps uncompletable, while a mark on a spare shifted
        /// legend is merely undiscoverable.</para>
        ///
        /// <para>INERT, everything else this table claims: 1 2 7 8 9 0 unshifted (the ampersand,
        /// the accented letters and the underscore), shifted Quote, shifted BackSlash, shifted
        /// Slash and shifted Minus. All are legends outside
        /// <see cref="Beatmaps.Typeability.PUNCTUATION"/>, and <see cref="PunctuationMapping.Inert"/>
        /// is what stops the six digit keys among them falling through to a digit their keycap does
        /// not show unshifted.</para>
        ///
        /// <para>KNOWN LIMIT: AZERTY is the only layout with a table of its own. QWERTZ is the next
        /// instance of this same bug class waiting to be reported: it is modelled as US punctuation
        /// plus the Y/Z letter swap, but a German keyboard's own legends differ too (the US Quote,
        /// Semicolon, BracketLeft and Minus positions are all letters there), so a QWERTZ player
        /// under Literate still meets marks on keys their keycaps do not show. AltGr is the other
        /// limit, and a structural one: the input path does not carry it, so a mark that is
        /// AltGr-only on a layout can only ever be parked, never placed faithfully.</para>
        /// </summary>
        private static PunctuationMapping mapAzertyPunctuation(Key key, bool shift, out char c)
        {
            c = default;

            // The whole digit row shares one rule, so it is expressed once rather than ten times.
            if (key >= Key.Number0 && key <= Key.Number9)
            {
                // Shifted is the digit on every one of them: fall through to the letter/digit map.
                if (shift)
                    return PunctuationMapping.Unclaimed;

                c = key switch
                {
                    Key.Number3 => '"',
                    Key.Number4 => '\'',
                    Key.Number5 => '(',
                    Key.Number6 => '-',
                    // 1 '&', 2 e-acute, 7 e-grave, 8 '_', 9 c-cedilla, 0 a-grave: none supported.
                    _ => default,
                };

                return c != default ? PunctuationMapping.Produced : PunctuationMapping.Inert;
            }

            switch (key)
            {
                case Key.Semicolon:
                    // The 'm' keycap, not a punctuation key at all here. Leave it to the letter
                    // map rather than shadowing it with the US legend's ';'.
                    return PunctuationMapping.Unclaimed;

                case Key.Minus:
                    // Right of 0: ')' unshifted, the degree sign (unsupported) shifted.
                    c = shift ? default : ')';
                    break;

                case Key.BracketLeft:
                    // The circumflex dead key, whose legend is a supported mark. Shifted is the
                    // diaeresis dead key, which is not, so '[' is parked there.
                    c = shift ? '[' : '^';
                    break;

                case Key.BracketRight:
                    // '$' unshifted; shifted is the pound sign, so ']' is parked there.
                    c = shift ? ']' : '$';
                    break;

                case Key.Quote:
                    // The u-grave keycap (unsupported), '%' shifted.
                    c = shift ? '%' : default;
                    break;

                case Key.BackSlash:
                    // '*' unshifted; shifted is the micro sign, outside the supported set.
                    c = shift ? default : '*';
                    break;

                case Key.M:
                    c = shift ? '?' : ',';
                    break;

                case Key.Comma:
                    c = shift ? '.' : ';';
                    break;

                case Key.Period:
                    c = shift ? '/' : ':';
                    break;

                case Key.Slash:
                    // Shifted this is the section sign, outside the supported set.
                    c = shift ? default : '!';
                    break;

                case Key.NonUSBackSlash:
                    c = shift ? '>' : '<';
                    break;

                default:
                    return PunctuationMapping.Unclaimed;
            }

            return c != default ? PunctuationMapping.Produced : PunctuationMapping.Inert;
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
