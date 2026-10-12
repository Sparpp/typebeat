# Watashi wa Saitei — generated lyric comparison

Compared the 52 lines saved in the development build's **the worst** difficulty (Nimono, Otu; mapper CrashstylerX2) with the official text supplied in this chat. The supplied paragraph has been divided to match the map's existing line timings.

- Saved generated lyrics: **9/52** lines match the supplied spelling.
- Revised converter, run using the rebuilt desktop assemblies: **14/52** lines match the supplied spelling.
- The revised converter changes **18/52** generated lines. The full table includes improvements and remaining mistakes.

These counts ignore whitespace and normalize ASCII comma/question/exclamation marks to their Japanese counterparts. They measure exact spelling agreement, not pronunciation or semantic accuracy: a different kana/kanji spelling also counts as a mismatch.

## Changes implemented

The converter now considers a complete romaji phrase while retaining each authored word boundary. A compound can cross those boundaries only if its Japanese surface can be divided into pieces with the corresponding dictionary readings. This recovers 不安定 from `fuan tei` without changing the two word timing windows, and recovers repeated くら / ふわ kana without inventing 鞍 / 不和 sequences. It prevents unrelated compounds such as 下地 from consuming `shi moji`.

Candidates made from symbols or digits are excluded, so `to` cannot become ＆ and エンドロール cannot be fragmented into ￥ドロール. Scoring discourages excessive content-word fragmentation while allowing particles and verb inflections; this also recovers 太陽, 消え去る and 小鳥や. Generated word originals accept alternate dictionary readings, so the 定 timing piece retains its authored `tei` reading for Polyglot input.

The converter remains offline and uses the existing bundled IPAdic. It is a spelling suggestion engine: its word/grammar costs cannot reliably disambiguate meaning. 愛 / 藍, 十 / 銃, 鈴 / 錫 and 四 / 市 remain errors in this example. The official lyrics deliberately use hiragana for two chorus lines; that decision is not encoded in romaji. Verified original text is required to reproduce the official spelling exactly.

## Romaji that needs review

| Map line | Authored romaji | Supplied official text | Implication |
| --- | --- | --- | --- |
| 9 | tsukai **kosareta** | 使い**古された** | The dictionary reading is `tsukai furusareta`; changing kanji alone cannot repair the missing pronunciation. |
| 16 | **takeshi** watashi ni ha iranai | **もう** 私にはいらない | The opening word differs from the supplied lyric. Check the transcription/audio. |
| 28 | **zaratto** shita | **サラッと**した | The initial voiced `za` differs from `sa`. |
| 49 | misasete kure **yo** | 見させてくれ | The map has an extra final syllable compared with the supplied text. |

## Complete comparison

Spaces in generated Japanese reflect the existing romaji timing words. The revised-output column is a fresh conversion preview; the saved map has not been overwritten.

| Line | Time | Authored romaji | Saved generated original | Supplied official text | Revised generated original | Finding |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 00:00.480 | kitto anata wa kizukanai | きっと 貴方 は 気付かない | きっと あなたは気づかない | きっと 貴方 は 気付かない | あなた / 貴方 and 気づかない / 気付かない differ in spelling. |
| 2 | 00:03.720 | watashi tanjun ni tanjun ni | 私 単純 に 単純 に | 私単純に 単純に | 私 単純 に 単純 に | Matches after whitespace/punctuation normalization. |
| 3 | 00:06.220 | kowarete iru mitai | 壊れて いる みたい | 壊れているみたい | 壊れて いる みたい | Matches after whitespace/punctuation normalization. |
| 4 | 00:08.480 | kitto anata wa shirienai | きっと 貴方 は 知り得ない | きっと あなたは知り得ない | きっと 貴方 は 知り得ない | あなた / 貴方 differs in spelling. |
| 5 | 00:11.760 | yoru no taiyou ni tsukamari dachi | 夜 の 他移用 に 捕まり 駄知 | 夜の太陽に 掴まり立ち | 夜 の 太陽 に 捕まり 駄知 | 太陽 is recovered. 掴まり / 捕まり and 立ち / 駄知 remain different. |
| 6 | 00:14.260 | dokomademo mieru no | どこまでも 見える の | 何処までも見えるの | どこまでも 見える の | 何処 / どこ differs in spelling. |
| 7 | 00:16.360 | tainai ni | 対内 に | 体内に | 対内 に | 体内 / 対内 is unresolved. |
| 8 | 00:17.360 | tainai ni | 対内 に | 体内に | 対内 に | 体内 / 対内 is unresolved. |
| 9 | 00:18.180 | tsukai kosareta kankaku to | 使い 越された 扞格 ＆ | 使い古された感覚と | 使い 越された 扞格 と | Source has kosareta, while supplied 使い古された reads furusareta. 扞格 / 感覚 remains different here. と no longer becomes ＆. |
| 10 | 00:20.220 | fuan tei ni | 不安 邸 に | 不安定に | 不安 定 に | 不安定 is recovered across the fuan / tei timing split. |
| 11 | 00:21.220 | fuan tei ni | 不安 邸 に | 不安定に | 不安 定 に | 不安定 is recovered across the fuan / tei timing split. |
| 12 | 00:22.220 | nukarumi de shinda kokoro no naka | 泥濘 で 凍んだ 心 の なか | 泥濘で死んだ心の中 | 泥濘 で 凍んだ 心 の なか | 死んだ / 凍んだ and 中 / なか remain different. |
| 13 | 00:24.460 | ai no you ni | 藍 の よう に | 愛のように | 藍 の よう に | 愛 / 藍 remains unresolved. |
| 14 | 00:25.412 | ai no you ni | 藍 の よう に | 愛のように | 藍 の よう に | 愛 / 藍 remains unresolved. |
| 15 | 00:26.240 | mohou saresugita kanjou bakka da | 摸倣 され過ぎた 感情 ばっか だ | 模倣されすぎた感情ばっかだ | 摸倣 され過ぎた 感情 ばっか だ | 模倣 / 摸倣 and すぎた / 過ぎた differ in spelling. |
| 16 | 00:28.980 | takeshi watashi ni ha iranai | 猛 渡し に は 炒らない | もう 私にはいらない | 焚けし 渡し に は 炒らない | Source starts takeshi instead of the supplied もう; the converter cannot recover that replacement. Other homophones remain. |
| 17 | 00:31.340 | oyasumi | おやすみ | おやすみ | おやすみ | Matches after whitespace/punctuation normalization. |
| 18 | 00:32.574 | ai o urareta watashi wa saitei | 藍 を 売られた 私 は 最低 | 愛を売られた 私は最低 | 藍 を 売られた 私 は 最低 | 愛 / 藍 remains unresolved. |
| 19 | 00:36.200 | juu o shiru yoru ni kura kura kura kura | 銃 を 知る 寄る 二 鞍 鞍 鞍 鞍 | 十を知る夜に くらくらくらくら | 銃 を 知る 夜 に くら くら くら くら | 夜に and repeated くら kana are recovered. 十 / 銃 remains unresolved. |
| 20 | 00:40.542 | ai ni umoreta watashi wa saitei | 藍 に 埋れた 私 は 最低 | 愛に埋もれた 私は最低 | 藍 に 埋れた 私 は 最低 | 愛 / 藍 and 埋もれた / 埋れた remain different. |
| 21 | 00:44.200 | chuu o mau you ni fuwa fuwa fuwa fuwa | 宙 を 舞う よう に 歩は 不和 婦は 不和 | 宙を舞うように ふわふわふわふわ | 宙 を 舞う よう に ふわ ふわ ふわ ふわ | Repeated ふわ kana is recovered. |
| 22 | 01:04.500 | kitto anata wa kizukanai | きっと 貴方 は 気付かない | きっと あなたは気づかない | きっと 貴方 は 気付かない | あなた / 貴方 and 気づかない / 気付かない differ in spelling. |
| 23 | 01:07.740 | tada tada tanchou ni tanchou ni | ただ ただ タンチョウ に タンチョウ に | 只々 単調に 単調に | 只 只 タンチョウ に タンチョウ に | 只々 / 只只 and 単調 / タンチョウ remain different. |
| 24 | 01:10.220 | susunde iku mirai | 進んで 行く 未来 | 進んでいく未来 | 進んで 行く 未来 | いく / 行く differs in spelling. |
| 25 | 01:12.500 | kitto anata ni todokanai | きっと 貴方 に 届かない | きっと あなたに届かない | きっと 貴方 に 届かない | あなた / 貴方 differs in spelling. |
| 26 | 01:15.693 | tsuneni shimaippanashi otogibanashi | 常に 姉妹っぱなし おとぎ話 | 常に 終いっぱなし 御伽噺 | 常に 姉妹っぱなし おとぎ話 | 終い / 姉妹 and 御伽噺 / おとぎ話 remain different. |
| 27 | 01:18.240 | doko kara hanasou ka | どこ から 話そう か | どこから話そうか | どこ から 話そう か | Matches after whitespace/punctuation normalization. |
| 28 | 01:20.360 | zaratto shita kankaku no saigo ni | 座ラット した 感覚 の 際５ 二 | サラッとした感覚の最後に | ざらっと した 感覚 の 最後 に | Source starts zaratto instead of saratto. した, 感覚 and 最後に are recovered, but the supplied サラッと requires a source correction. |
| 29 | 01:22.300 | atama o kake meguru sono aironii | アタマ を 掛け 巡る その アイロニー | 頭を駆け巡る そのアイロニー | アタマ を かけ めぐる その アイロニー | 頭 / アタマ and 駆け巡る / かけめぐる differ in spelling. |
| 30 | 01:24.390 | menzai fu no tsumori? sono shi moji | 免罪 婦 の つもり? その 市 文字 | 免罪符のつもり？ その四文字 | 免罪 婦 の つもり? その 市 文字 | 符 / 婦 and 四 / 市 remain unresolved; 免罪符 is absent as a complete IPAdic entry. |
| 31 | 01:26.260 | kudannai endorooru haigo ni | くだん内 ￥ドロール 背後 に | くだんないエンドロール背後に | 降んない エンドロール 背後 に | エンドロール is recovered and the invented ￥ disappears. Colloquial くだんない is still misinterpreted (now 降んない). |
| 32 | 01:28.380 | nan mo terasenai miraa booru ni | 何 も 照らせない ミラー ボール に | 何も照らせないミラーボールに | 何 も 照らせない ミラー ボール に | Matches after whitespace/punctuation normalization. |
| 33 | 01:30.220 | utsutta watashi to | 映った 私 ＆ | 映った私と | 映った 私 と | と no longer becomes ＆. |
| 34 | 01:31.360 | kienai kizu to | 消えない キズ ＆ | 消えない傷と | 消えない キズ と | と no longer becomes ＆. 傷 / キズ still differs in spelling. |
| 35 | 01:32.360 | dokoka kara nagareru rabu songu wa | どこか から 流れる ラブ ソング は | 何処かから流れるラブソングは | どこか から 流れる ラブ ソング は | 何処 / どこ differs in spelling. |
| 36 | 01:34.260 | doku ni mo | 毒 に も | 毒にも | 毒 に も | Matches after whitespace/punctuation normalization. |
| 37 | 01:34.860 | kusuri ni mo | 薬 に も | 薬にも | 薬 に も | Matches after whitespace/punctuation normalization. |
| 38 | 01:35.480 | kotoriya | 故鳥屋 | 小鳥や | 小鳥や | 小鳥や is recovered. |
| 39 | 01:35.993 | suzu ni mo! | 錫 に も! | 鈴にも！ | 錫 に も! | 鈴 / 錫 remains unresolved. |
| 40 | 01:36.521 | ai o urareta, watashi wa saitei? | 藍 を 売られた, 私 は 最低? | あいをうられた、わたしはさいてい？ | 藍 を 売られた, 私 は 最低? | The author deliberately uses hiragana here. Romaji provides no indication of this script choice; 愛 / 藍 also remains unresolved. |
| 41 | 01:44.536 | ai ni umoreta, watashi wa saitei? | 藍 に 埋れた, 私 は 最低? | あいにうもれた、わたしはさいてい？ | 藍 に 埋れた, 私 は 最低? | The author deliberately uses hiragana here. Romaji provides no indication of this script choice; 愛 / 藍 also remains unresolved. |
| 42 | 01:52.549 | ai ni fumareta watashi o yonde | 藍 に 践まれた 私 を 呼んで | 愛に踏まれた私を呼んで | 藍 に 践まれた 私 を 呼んで | 愛 / 藍 and 踏まれた / 践まれた remain different. |
| 43 | 01:56.200 | shitta ki ni natte hitasura odoru wa | 叱咤 期 に なって ひたすら 踊る 輪 | 知った気になってひたすら踊るわ | 叱咤 期 に なって ひたすら 踊る は | 知った / 叱咤 and 気 / 期 remain unresolved. The final wa is still written は here, rather than the supplied わ. |
| 44 | 02:00.549 | ai ni kuwareta watashi o yonde | 藍 に 食われた 私 を 呼んで | 愛に喰われた私を呼んで | 藍 に くわれた 私 を 呼んで | 愛 / 藍 and 喰われた / くわれた remain different. |
| 45 | 02:04.220 | kiesaru mae ni anata o norou wa | 気へ去る 前 に 貴方 を 詛う 輪 | 消え去る前にあなたを呪うわ | 消え去る 前 に 貴方 を 詛う わ | 消え去る is recovered. あなた / 貴方 and 呪う / 詛う still differ in spelling. |
| 46 | 02:08.517 | ai o urareta watashi wa saitei | 藍 を 売られた 私 は 最低 | 愛を売られた 私は最低 | 藍 を 売られた 私 は 最低 | 愛 / 藍 remains unresolved. |
| 47 | 02:12.220 | juu o shiru yoru ni kura kura kura kura | 銃 を 知る 寄る 二 鞍 鞍 鞍 鞍 | 十を知る夜に くらくらくらくら | 銃 を 知る 夜 に くら くら くら くら | 夜に and repeated くら kana are recovered. 十 / 銃 remains unresolved. |
| 48 | 02:16.534 | ai ni umoreta watashi wa saitei | 藍 に 埋れた 私 は 最低 | 愛に埋もれた 私は最低 | 藍 に 埋れた 私 は 最低 | 愛 / 藍 and 埋もれた / 埋れた remain different. |
| 49 | 02:20.240 | kisu o suru mae ni yume o misasete kure yo | 鱚 を する 前 に 夢 を 見させて くれ よ | キスをする前に夢を見させてくれ | 鱚 を する 前 に 夢 を 見させて くれ よ | キス / 鱚 remains unresolved. The map has an extra final yo that is absent from the supplied lyrics. |
| 50 | 02:40.480 | zutto anata wa kizukanai | ずっと 貴方 は 気付かない | ずっと あなたは気づかない | ずっと 貴方 は 気付かない | あなた / 貴方 and 気づかない / 気付かない differ in spelling. |
| 51 | 02:43.780 | ashita mo tantan to tantan to | 明日 も 淡々 と 淡々 と | 明日も 淡々と 淡々と | 明日 も 淡々 と 淡々 と | Matches after whitespace/punctuation normalization. |
| 52 | 02:46.220 | kowarete iku | 壊れて いく | 壊れていく | 壊れて いく | Matches after whitespace/punctuation normalization. |

## Validation and source

1,232 relevant Japanese, original-lyric, editor, serialization, language-selection and Polyglot tests passed. The desktop Debug executable was rebuilt with zero errors; its game and ruleset assemblies were verified against the project outputs. Existing build warnings remain.

Saved map snapshot: `/Users/alexachan/Library/Application Support/typebeat-development/files/c/c9/c9a2f005c52d143d5b9c5ccbe0ff2f64819c47b0e4590986e6a12a0b8d84e12a`. Existing original lyrics are preserved by generation, so trying a fresh conversion requires clearing the generated originals first; importing the supplied official text gives exact author spellings.
