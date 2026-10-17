# Japanese lyric generation defaults

Generated spellings are suggestions. Authored originals and mapper-selected readings retain priority.

The generator prefers hiragana for a curated set of everyday demonstratives, greetings, conjunctions and adverbs. Preferences match both a dictionary surface and its reading. For example, `彼方` with the reading `あちら` can become `あちら`, while the reading `かなた` remains unaffected. The existing defaults for `あなた`, `かける`, `また` and `ひとり` are retained.

## Research basis

The [Agency for Cultural Affairs' writing guidance](https://www.bunka.go.jp/seisaku/bunkashingikai/kokugo/hokoku/pdf/93651301_01.pdf), printed pages 12–13, describes kana conventions for these categories and distinguishes auxiliary expressions from literal meanings. This is general writing guidance, not a study of song-lyric frequency.

Official published lyrics provide examples of these conventions in songs: Yorushika's [雲と幽霊](https://yorushika.com/lyrics/detail/5/), [夕凪、某、花惑い](https://yorushika.com/lyrics/detail/23/) and [ルバート](https://yorushika.com/lyrics/detail/66/), and Southern All Stars' [愛はスローにちょっとずつ](https://southernallstars.jp/lyrics/detail/579/). These examples support conservative editorial defaults; they do not establish a universal spelling rule or an exhaustive frequency ranking. No lyric passages are bundled with the implementation.

## Preferred forms

| Category | Hiragana defaults |
| --- | --- |
| Demonstratives and locations | これ, それ, あれ, どれ, この, その, あの, どの, ここ, そこ, あそこ, どこ |
| Directions and questions | こちら, そちら, あちら, どちら, いつ, いつか, いつも, いつまで, いつまでも, なぜ, どうして |
| Greetings | ありがとう, ごめん, さようなら, さよなら |
| Adverbs | きっと, ちょっと, とても, ちょうど, たくさん, よほど, めった, あくまで, いくら, すでに, まさに, いわゆる |
| Conjunctions and conversational expressions | しかし, しかしながら, そして, あるいは, とにかく, ともかく, ともかくも, そば, まま |

The approved surface/reading pairs are maintained in `JapaneseLyricGenerator.SpellingPreferences.cs`. Only selected spellings change: sharing a pronunciation does not qualify another word for replacement.

Context-sensitive cases such as `事/こと`, `物/もの`, `時/とき`, `所/ところ`, `様/よう`, and auxiliary versus literal `行く`, `来る` and `見る` are not given blanket reading-only replacements. Common content words such as `私`, `君`, `愛`, `心` and `夢` retain their dictionary spellings. Authored `一人` remains valid with the reading `ひとり`.

## English phrases

Before converting Japanese, the generator checks English dictionary runs for phrase evidence. This preserves mixed-language phrases such as `I love you`, `take me home` and `no more`, including case and punctuation, rather than converting individually romanizable words inside them. Dictionary membership alone is insufficient: Japanese phrases such as `sore made`, `koi no uta` and `take no oto` must still convert.

Plain Latin text can be ambiguous between the two languages. Detection uses phrase context rather than claiming that every dictionary word is English; supplied original-script lyrics remain the more reliable source when a particular spelling or language choice matters.
