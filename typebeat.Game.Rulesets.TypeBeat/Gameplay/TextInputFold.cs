// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// THE CHARACTER RULE of Latin gameplay typing since backlog 383: what one piece of text the OS
    /// COMMITTED (the character its own layout, Shift, Caps Lock, AltGr and dead keys produced) types,
    /// as the run of presses the engine judges. Before 383 the playfield read the PHYSICAL key and
    /// mapped it through three hand-written layout tables (the deleted <c>KeyCharMap</c>), which is
    /// why the player had to tell the game which keyboard they had; now the OS says, for every layout.
    ///
    /// <para>The rule is the LYRIC'S OWN FOLD, applied to the keyboard's side, so the character the OS
    /// hands over lands on exactly the cell the map stores for it (<see cref="Typeability.Normalize"/>
    /// is what decided that cell):</para>
    /// <list type="bullet">
    /// <item>A Latin letter with diacritics types its BASE letter: canonical decomposition, combining
    /// marks dropped ('é' types 'e', 'Ç' types 'C'), so a precomposed commit and a decomposed one
    /// ("e" + U+0301) type the same single press.</item>
    /// <item>The special letters SPELL OUT, in order, as several presses at the one timestamp:
    /// <see cref="Typeability.SPECIAL_LETTERS"/> ('ß' types "ss", 'æ' "ae", 'ø' "o") and the
    /// Romaniser's <see cref="Romaniser.LATIN_EXTRAS"/> ('ħ' "h", 'ĳ' "ij"), so a romanised lyric and
    /// the keyboard that writes the original agree.</item>
    /// <item>The typographic variants fold as the normalizer folds them
    /// (<see cref="Typeability.FoldTypographic"/>): a curly apostrophe types '\'', an en dash '-'.</item>
    /// <item>CASE IS KEPT. The Literate mod judges it; without the mod the caret folds it away, as
    /// it always did.</item>
    /// <item>A supported <see cref="Typeability.PUNCTUATION"/> mark types only when
    /// <c>punctuation</c> is asked for (the Literate mod, where a mark is a cell). Otherwise it is
    /// INERT, the rule a comma key has always had: a habitual comma is never a wrong key.</item>
    /// <item>EVERYTHING ELSE IS INERT: a character the typing surface cannot hold (a '€', an '&amp;',
    /// a Cyrillic letter on a Latin play, a control char, half a surrogate pair) produces nothing at
    /// all, no judgement and no wrong key, exactly as an unmapped key always did.</item>
    /// </list>
    ///
    /// <para>PUBLIC AND PURE ON PURPOSE: the browser player reads the OS layout too (<c>e.key</c>) and
    /// must type the same cell for the same character, so the website's parity harness can call this
    /// directly (the same character in produces the same cells out) rather than mirror a table.</para>
    /// </summary>
    public static class TextInputFold
    {
        /// <summary>
        /// The presses <paramref name="committed"/> types, in order, on the typing surface (letters,
        /// digits and space, plus the supported marks when <paramref name="punctuation"/> is set).
        /// Empty when nothing in it is typeable.
        /// </summary>
        public static IEnumerable<char> Fold(string? committed, bool punctuation = true)
        {
            if (string.IsNullOrEmpty(committed))
                yield break;

            string decomposed;

            try
            {
                decomposed = committed.Normalize(NormalizationForm.FormD);
            }
            catch (ArgumentException)
            {
                // Invalid Unicode (a lone surrogate): fold what is there, the halves themselves are inert below.
                decomposed = committed;
            }

            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;

                if (Typeability.SPECIAL_LETTERS.TryGetValue(c, out string? spelled) || Romaniser.LATIN_EXTRAS.TryGetValue(c, out spelled))
                {
                    foreach (char s in spelled)
                    {
                        if (Typeable(s, punctuation))
                            yield return s;
                    }

                    continue;
                }

                char folded = Typeability.FoldTypographic(c);

                if (Typeable(folded, punctuation))
                    yield return folded;
            }
        }

        /// <summary>
        /// Whether one ALREADY FOLDED char is a press on the typing surface: a typeable char
        /// (<see cref="Typeability.IsTypeable"/>: a-z, A-Z, 0-9, space), or a supported mark when
        /// <paramref name="punctuation"/> is set. The one gate both <see cref="Fold"/> and its tests read.
        /// </summary>
        public static bool Typeable(char c, bool punctuation)
            => Typeability.IsTypeable(c) || (punctuation && Typeability.IsPunctuation(c));
    }
}
