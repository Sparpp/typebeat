// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class ShiftSurchargeTest
    {
        [TestCase("^%$", 3)]
        [TestCase("(a)", 2)]
        [TestCase("(F)", 2)]
        [TestCase("!!!", 1)]
        [TestCase("***", 1)]
        [TestCase("$$$", 1)]
        [TestCase("!!!?", 2)]
        [TestCase("!!**$$", 3)]
        [TestCase("!*!", 3)]
        [TestCase("()", 2)]
        [TestCase("!a!", 2)]
        [TestCase("! !", 2)]
        [TestCase("!A!", 2)]
        [TestCase("A!", 2)]
        [TestCase("!A", 1)]
        [TestCase("USA", 1)]
        [TestCase("A B", 2)]
        [TestCase("aB", 1)]
        [TestCase("[a],.;/", 0)]
        [TestCase("&", 0)]
        [TestCase("!&!", 2)]
        [TestCase("!!|!!", 1)]
        public void DistinctShiftedSymbolsPayAndIdenticalRunsShare(string text, double charges)
        {
            Assert.That(ChunkedEndurance.ShiftSurcharge(text, true, 1), Is.EqualTo(charges));
            Assert.That(ChunkedEndurance.ShiftSurcharge(text, true, 0.5), Is.EqualTo(charges * 0.5));
            Assert.That(ChunkedEndurance.ShiftSurcharge(text, false, 1), Is.Zero);
            Assert.That(ChunkedEndurance.ShiftSurcharge(text, true, 0), Is.Zero);
        }

        [TestCase("^%$", 3)]
        [TestCase("(F)", 2)]
        [TestCase("!!!", 1)]
        public void TheRatingUsesSymbolChargesWithoutAddingCharacterCells(string text, double charges)
        {
            var lines = new[]
            {
                new LyricLine
                {
                    RawText = text,
                    StartTime = 0,
                    EndTime = 1000,
                    SingEndTime = 1000,
                    Units = new[] { new TimedUnit { Text = text, StartTime = 0, EndTime = 1000 } },
                },
            };
            var settings = ChunkedEndurance.Live with { complexity_strength = 0 };
            var words = LyricDifficulty.BuildWords(lines, 1, true, LyricDifficulty.NoScores, settings.span_ms);
            var prefix = ChunkedEndurance.BuildShiftPrefix(words, 0, 21, true, settings)!;
            var charged = LyricDifficulty.RateChunked(lines, 1, true, LyricDifficulty.NoScores, settings).Report;
            var uncharged = LyricDifficulty.RateChunked(lines, 1, true, LyricDifficulty.NoScores, settings with { shift_cost = 0 }).Report;

            Assert.That(prefix[21], Is.EqualTo(charges).Within(1e-12));
            Assert.That(charged.Stars, Is.GreaterThan(uncharged.Stars));
            Assert.That(charged.CountedCharacters, Is.EqualTo(text.Length).Within(1e-9));
            Assert.That(charged.CountedCharacters, Is.EqualTo(uncharged.CountedCharacters).Within(1e-9));
        }
    }
}
