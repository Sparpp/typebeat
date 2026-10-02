// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The mapper-chosen freestyle colour (backlog 384): the stored key's exact reading and writing
    /// (<see cref="FreestyleColourKey"/>), and the two painters that take it in place of the old
    /// constant (<see cref="LyricLineDisplay.CellFillColour"/> and <see cref="FreestyleTextFlow"/>).
    /// </summary>
    [TestFixture]
    public class FreestyleColourKeyTest
    {
        private static readonly Colour4 teal = new Colour4((byte)0x12, (byte)0xab, (byte)0xef, (byte)255);

        [TestCase("#12abef")]
        [TestCase("#12ABEF")]
        [TestCase("  #12AbEf  ")]
        public void ParseReadsSixDigitHexEitherCase(string value)
            => Assert.That(FreestyleColourKey.Parse(value), Is.EqualTo(teal));

        [TestCase(null)]
        [TestCase("")]
        [TestCase("12abef")]
        [TestCase("#1ab")]
        [TestCase("#12abef00")]
        [TestCase("#12abeg")]
        [TestCase("# 12abef")]
        [TestCase("rgb(18,171,239)")]
        public void ParseRefusesEverythingElse(string? value)
            => Assert.That(FreestyleColourKey.Parse(value), Is.Null);

        [Test]
        public void FormatIsLowercaseOpaqueSixDigits()
        {
            Assert.That(FreestyleColourKey.Format(teal), Is.EqualTo("#12abef"));
            Assert.That(FreestyleColourKey.Format(teal.Opacity(0.5f)), Is.EqualTo("#12abef"), "alpha is never stored");
            Assert.That(FreestyleColourKey.Format((Colour4)TypeBeatStyle.FreestyleChar), Is.EqualTo("#c792ea"));
        }

        [Test]
        public void NormaliseDropsTheDefaultAndSnapsToTheStoredSpelling()
        {
            Assert.That(FreestyleColourKey.Normalise(null), Is.Null);
            Assert.That(FreestyleColourKey.Normalise((Colour4)TypeBeatStyle.FreestyleChar), Is.Null, "the default is carried as NO key");
            Assert.That(FreestyleColourKey.Normalise(teal.Opacity(0.25f)), Is.EqualTo(teal), "made opaque");

            // A float colour between two byte values lands on what a reload would produce.
            var between = new Colour4(0.5f, 0.5f, 0.5f, 1f);
            Assert.That(FreestyleColourKey.Normalise(between), Is.EqualTo(FreestyleColourKey.Parse(FreestyleColourKey.Format(between))));

            Assert.That(FreestyleColourKey.LineFor(null), Is.Null);
            Assert.That(FreestyleColourKey.LineFor((Colour4)TypeBeatStyle.FreestyleChar), Is.Null);
            Assert.That(FreestyleColourKey.LineFor(teal), Is.EqualTo("FreestyleColour: #12abef"));
        }

        [Test]
        public void ResolveFallsBackToTheDefault()
        {
            Assert.That(FreestyleColourKey.Resolve(null), Is.EqualTo(TypeBeatStyle.FreestyleChar));
            Assert.That(FreestyleColourKey.Resolve(teal), Is.EqualTo((Color4)teal));
        }

        [Test]
        public void CellFillColourWearsTheMapColourInEveryStateAndOnlyOnFreestyleCells()
        {
            foreach (CellState state in Enum.GetValues<CellState>())
            {
                foreach (bool inSung in new[] { false, true })
                {
                    Assert.That(LyricLineDisplay.CellFillColour(state, true, inSung, 0.5, 70f, teal), Is.EqualTo((Color4)teal), $"{state}, inSung={inSung}");
                    Assert.That(LyricLineDisplay.CellFillColour(state, true, inSung, 0.5, 70f), Is.EqualTo(TypeBeatStyle.FreestyleChar),
                        "no colour passed keeps the default (every pre-384 caller)");
                    Assert.That(LyricLineDisplay.CellFillColour(state, false, inSung, 0.5, 70f, teal),
                        Is.EqualTo(LyricLineDisplay.CellFillColour(state, false, inSung, 0.5, 70f)), "a lyric cell ignores it");
                }
            }
        }

        [Test]
        public void FreestyleTextFlowRecoloursItsMarkersAndOnlyThem()
        {
            var flow = new FreestyleTextFlow(18, TypeBeatStyle.TypedChar) { Text = "ab&c&" };

            OsuSpriteText[] sprites() => flow.Children.OfType<OsuSpriteText>().ToArray();

            Assert.That(sprites().Count(s => s.Colour.AverageColour == (Color4)TypeBeatStyle.FreestyleChar), Is.EqualTo(2), "default before any pick");

            flow.FreestyleColour = teal;

            Assert.That(sprites().Count(s => s.Colour.AverageColour == (Color4)teal), Is.EqualTo(2), "both markers follow the pick");
            Assert.That(sprites().Count(s => s.Colour.AverageColour == TypeBeatStyle.TypedChar), Is.EqualTo(2), "the text runs keep the label colour");

            flow.Text = "&x";
            Assert.That(sprites().First().Colour.AverageColour == (Color4)teal, Is.True, "a rebuild keeps the pick");
        }
    }
}
