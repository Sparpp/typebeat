// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    internal static partial class JapaneseLyricGenerator
    {
        // Editorial defaults for generated lyrics, never replacements in authored originals.
        // Exact surface/reading pairs preserve homophones and alternate kanji readings.
        // Sources and the limits of the lyric evidence: docs/japanese-lyric-spelling-preferences.md.
        private static readonly HashSet<(string Surface, string Reading)> kanaPreferences = new HashSet<(string, string)>
        {
            ("此れ", "これ"), ("之", "これ"), ("其れ", "それ"), ("彼れ", "あれ"), ("何れ", "どれ"),
            ("此の", "この"), ("其の", "その"), ("彼の", "あの"), ("何の", "どの"),
            ("此処", "ここ"), ("此所", "ここ"), ("其処", "そこ"), ("其所", "そこ"),
            ("彼処", "あそこ"), ("彼所", "あそこ"), ("何処", "どこ"), ("何所", "どこ"),
            ("此方", "こちら"), ("其方", "そちら"), ("彼方", "あちら"), ("何方", "どちら"),
            ("何時", "いつ"), ("何時か", "いつか"), ("何時も", "いつも"),
            ("何時まで", "いつまで"), ("何時迄", "いつまで"),
            ("何時までも", "いつまでも"), ("何時迄も", "いつまでも"),
            ("何故", "なぜ"), ("如何して", "どうして"),
            ("有難う", "ありがとう"), ("有り難う", "ありがとう"), ("御免", "ごめん"),
            ("左様なら", "さようなら"), ("左様なら", "さよなら"),
            ("屹度", "きっと"), ("一寸", "ちょっと"), ("迚も", "とても"),
            ("丁度", "ちょうど"), ("沢山", "たくさん"), ("余程", "よほど"), ("滅多", "めった"),
            ("飽くまで", "あくまで"), ("幾ら", "いくら"), ("既に", "すでに"),
            ("正に", "まさに"), ("所謂", "いわゆる"),
            ("然し", "しかし"), ("然しながら", "しかしながら"), ("然し乍ら", "しかしながら"),
            ("然して", "そして"), ("或いは", "あるいは"),
            ("兎に角", "とにかく"), ("兎も角", "ともかく"), ("兎も角も", "ともかくも"),
            ("傍", "そば"), ("側", "そば"), ("儘", "まま"), ("侭", "まま"),
        };

        // Retain the established lyric defaults, including the various spellings of kakeru.
        private static readonly HashSet<string> existingKanaPreferences = new HashSet<string>(StringComparer.Ordinal)
        {
            "あなた", "かける", "また", "ひとり",
        };

        internal static string PreferredLyricSurface(string surface, string reading)
            => existingKanaPreferences.Contains(reading) || kanaPreferences.Contains((surface, reading)) ? reading : surface;
    }
}
