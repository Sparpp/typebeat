// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Import;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JapaneseLyricDefaultsTest
    {
        [TestCase("anata", "あなた")]
        [TestCase("kakeru", "かける")]
        [TestCase("mata", "また")]
        [TestCase("hitori", "ひとり")]
        [TestCase("doko", "どこ")]
        [TestCase("itsumo", "いつも")]
        [TestCase("itsu", "いつ")]
        [TestCase("naze", "なぜ")]
        [TestCase("arigatou", "ありがとう")]
        [TestCase("gomen", "ごめん")]
        [TestCase("chotto", "ちょっと")]
        [TestCase("choudo", "ちょうど")]
        [TestCase("takusan", "たくさん")]
        [TestCase("sudeni", "すでに")]
        [TestCase("masani", "まさに")]
        [TestCase("tonikaku", "とにかく")]
        [TestCase("anata no soba ni", "あなた の そば に")]
        public void CommonGeneratedLyricWordsPreferKana(string romaji, string kana)
            => Assert.That(JapaneseLyricGenerator.Generate(romaji), Is.EqualTo(kana));

        [TestCase("彼方", "かなた")]
        [TestCase("何方", "どなた")]
        [TestCase("一寸", "いっすん")]
        [TestCase("側", "がわ")]
        [TestCase("琴", "こと")]
        [TestCase("鳴る", "なる")]
        [TestCase("射る", "いる")]
        [TestCase("君", "きみ")]
        [TestCase("私", "わたし")]
        public void PreferencesDoNotReplaceHomophonesOrContentWords(string surface, string reading)
            => Assert.That(JapaneseLyricGenerator.PreferredLyricSurface(surface, reading), Is.EqualTo(surface));

        [TestCase("kimi to I love you", "I love you")]
        [TestCase("kimi ni take me home", "take me home")]
        [TestCase("kimi ni take me", "take me")]
        [TestCase("kimi ni no more", "no more")]
        [TestCase("kimi ni you and me", "you and me")]
        [TestCase("kimi ni I’m in love", "I’m in love")]
        [TestCase("kimi ni say no", "say no")]
        [TestCase("kimi ni I made you", "I made you")]
        [TestCase("kimi ni hello, hello", "hello, hello")]
        [TestCase("kimi ni love, love, love", "love, love, love")]
        [TestCase("kimi ni let me go", "let me go")]
        [TestCase("kimi ni take it easy", "take it easy")]
        [TestCase("boku wa kimi no soba ni I love you", "I love you")]
        public void EnglishPhrasesSurviveJapaneseConversion(string lyric, string english)
        {
            string? generated = JapaneseLyricGenerator.Generate(lyric);
            Assert.That(generated, Does.Contain(english));
            Assert.That(generated, Does.Contain("君"));
        }

        [TestCase("sore made", "それ")]
        [TestCase("koi no uta", "歌")]
        [TestCase("take no oto", "音")]
        [TestCase("yoru no taiyou ni", "太陽")]
        [TestCase("Doa no saki ni boku no senaka ga mieta", "ドア")]
        [TestCase("Jigoku no hate nado doko ni aru no ka", "の 果て")]
        public void EnglishDictionaryOverlapDoesNotProtectJapanesePhrases(string lyric, string japanese)
            => Assert.That(JapaneseLyricGenerator.Generate(lyric), Does.Contain(japanese).And.Not.Match("[A-Za-z]"));
    }
}
