// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Overlays.BeatmapSet;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The text and graph data the set overlay's typing stats show, kept apart from the drawables so the rules
    /// (unknown reads "-", whole-number WPM, one-decimal chars/word, a curve with nothing above 0 is no graph) are pinned.
    /// </summary>
    [TestFixture]
    public class TypingStatsFormatTest
    {
        [TestCase(0, "- bpm")]
        [TestCase(-1, "- bpm")]
        [TestCase(120, "120 bpm")]
        [TestCase(175.5, "175.5 bpm")]
        [TestCase(128.004, "128 bpm")]
        public void Bpm(double bpm, string expected) => Assert.That(TypingStatsFormat.Bpm(bpm), Is.EqualTo(expected));

        [Test]
        public void Counts()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TypingStatsFormat.Count(null, "words"), Is.EqualTo("- words"));
                Assert.That(TypingStatsFormat.Count(412, "words"), Is.EqualTo("412 words"));
                Assert.That(TypingStatsFormat.Count(1903, "chars"), Is.EqualTo("1,903 chars"));
                Assert.That(TypingStatsFormat.Count(0, "chars"), Is.EqualTo("0 chars"));
            });
        }

        [Test]
        public void CharsPerWord()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TypingStatsFormat.CharsPerWord(412, 1903), Is.EqualTo("4.6 chars/word"));
                Assert.That(TypingStatsFormat.CharsPerWord(null, 1903), Is.Empty);
                Assert.That(TypingStatsFormat.CharsPerWord(412, null), Is.Empty);
                Assert.That(TypingStatsFormat.CharsPerWord(0, 10), Is.Empty, "no words: no ratio, never NaN or infinity");
            });
        }

        [Test]
        public void PaceLine()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TypingStatsFormat.PaceLine(95.2, 70.4, 61.6), Is.EqualTo("Peak 95 · Target 70 · Avg 62"));
                Assert.That(TypingStatsFormat.PaceLine(95, null, null), Is.EqualTo("Peak 95 · Target - · Avg -"));
            });
        }

        [Test]
        public void NormalisedCurve()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TypingStatsFormat.NormalisedCurve(new[] { 0f, 50f, 100f }), Is.EqualTo(new[] { 0f, 0.5f, 1f }));
                Assert.That(TypingStatsFormat.NormalisedCurve(new[] { -5f, 40f }), Is.EqualTo(new[] { 0f, 1f }), "negatives clamp to 0");
                Assert.That(TypingStatsFormat.NormalisedCurve(null), Is.Null);
                Assert.That(TypingStatsFormat.NormalisedCurve(System.Array.Empty<float>()), Is.Null);
                Assert.That(TypingStatsFormat.NormalisedCurve(new[] { 0f, 0f }), Is.Null, "nothing above 0 is no graph");
                Assert.That(TypingStatsFormat.NormalisedCurve(new[] { float.NaN, 50f, 100f }), Is.EqualTo(new[] { 0f, 0.5f, 1f }), "NaN reads as 0");
                Assert.That(TypingStatsFormat.NormalisedCurve(new[] { float.PositiveInfinity, 40f }), Is.EqualTo(new[] { 0f, 1f }), "infinity reads as 0");
                Assert.That(TypingStatsFormat.NormalisedCurve(new[] { float.NaN }), Is.Null, "nothing finite above 0 is no graph");
            });
        }
    }
}
