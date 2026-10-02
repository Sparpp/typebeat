// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// What the playfield's key handler still needs to know about a KEY, now that the characters
    /// come from the OS's text input rather than from the key (backlog 383): which keys commit text
    /// at all, which are swallowed as typing keys whatever they commit, and the one positional rule
    /// the default surface keeps (the digit row). Named by physical position after the US legends,
    /// as osu!framework names keys; what each one commits is the OS layout's business.
    /// </summary>
    internal static class TypingKeys
    {
        /// <summary>
        /// Whether a key COMMITS TEXT on some layout: the letter, digit and punctuation block,
        /// the keypad's digits and operators, and the spacebar. Only these take part in the
        /// pairing; a key outside the block (Enter, Backspace, an arrow, a function key) never
        /// commits a character, and letting one wait for a commit could hand it a character
        /// another key produced. Modifier keys are outside it too: Shift commits nothing, it only
        /// changes what the next key commits.
        /// </summary>
        public static bool CommitsText(Key key)
            => (key >= Key.A && key <= Key.Z)
               || (key >= Key.Number0 && key <= Key.Number9)
               || (key >= Key.Keypad0 && key <= Key.Keypad9)
               || IsPunctuationPosition(key)
               || key == Key.KeypadDivide || key == Key.KeypadMultiply || key == Key.KeypadMinus || key == Key.KeypadPlus || key == Key.KeypadDecimal
               || key == Key.Space;

        /// <summary>The block's punctuation positions, named after their US legends; what each one commits is the OS layout's business.</summary>
        public static bool IsPunctuationPosition(Key key)
            => key == Key.Tilde || key == Key.Minus || key == Key.Plus || key == Key.BracketLeft || key == Key.BracketRight
               || key == Key.BackSlash || key == Key.NonUSBackSlash || key == Key.Semicolon || key == Key.Quote
               || key == Key.Comma || key == Key.Period || key == Key.Slash;

        /// <summary>
        /// The digit a digit-row or keypad key types WITHOUT the Literate mod, whatever the layout
        /// commits for it: Shift+1 is '1' rather than '!', an AZERTY digit key is its digit rather
        /// than its accented letter, and a keypad key is its digit with Num Lock off. That is the
        /// rule the default surface has always had (Shift only ever cased letters there), and the
        /// browser keeps it too (typebeat-player.js keyToChar, backlog 305/309). Under Literate the
        /// marks above the digits are cells, so the OS's character decides there.
        /// </summary>
        public static bool TryPositionalDigit(Key key, out char digit)
        {
            if (key >= Key.Number0 && key <= Key.Number9)
            {
                digit = (char)('0' + (key - Key.Number0));
                return true;
            }

            if (key >= Key.Keypad0 && key <= Key.Keypad9)
            {
                digit = (char)('0' + (key - Key.Keypad0));
                return true;
            }

            digit = default;
            return false;
        }

        /// <summary>
        /// The keys a Latin play SWALLOWS on an active, unfinished line whatever they commit: a
        /// letter, a digit (top row or keypad) and the spacebar, the keys that were typing keys on
        /// every layout before backlog 383 too.
        /// </summary>
        public static bool AlwaysSwallowed(Key key)
            => (key >= Key.A && key <= Key.Z)
               || (key >= Key.Number0 && key <= Key.Number9)
               || (key >= Key.Keypad0 && key <= Key.Keypad9)
               || key == Key.Space;
    }
}
