// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Utils;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pure maths for the TIMING TINT (<see cref="LyricLineDisplay.TimingTintColour"/>): a correct
    /// char stays the clean typed colour inside the Great window, and past it shades towards orange
    /// when early and blue when late (the defaults; both are player settings), from <see cref="LyricLineDisplay.TIMING_TINT_MIN"/> just
    /// outside Great to <see cref="LyricLineDisplay.TIMING_TINT_MAX"/> at the Meh edge and beyond.
    /// </summary>
    [TestFixture]
    public class TimingTintTest
    {
        private static readonly SyncWindows windows = SyncWindows.Default;

        private static Color4 tint(double delta, bool pastRushCap = false) => LyricLineDisplay.TimingTintColour(delta, windows, pastRushCap);

        private static Color4 towards(Color4 hue, double amount) => Interpolation.ValueAt(amount, TypeBeatStyle.TypedChar, hue, 0d, 1d);

        /// <summary>Straight-line RGB distance from the clean typed colour: how strong a tint is.</summary>
        private static double strength(Color4 c)
        {
            var typed = TypeBeatStyle.TypedChar;
            return Math.Sqrt(Math.Pow(c.R - typed.R, 2) + Math.Pow(c.G - typed.G, 2) + Math.Pow(c.B - typed.B, 2));
        }

        [TestCase(0)]
        [TestCase(-100)]
        [TestCase(100)]
        [TestCase(-150)]
        [TestCase(150)]
        public void GreatPressesAreExactlyTheCleanTypedColour(double delta)
        {
            Assert.That(tint(delta), Is.EqualTo(TypeBeatStyle.TypedChar));
        }

        [Test]
        public void JustOutsideGreatStartsAtTheMinimumStrength()
        {
            const double just_outside = 150 + 1e-9;

            Assert.That(strength(tint(-just_outside)), Is.EqualTo(strength(towards(TypeBeatStyle.EarlyTint, LyricLineDisplay.TIMING_TINT_MIN))).Within(1e-4));
            Assert.That(strength(tint(just_outside)), Is.EqualTo(strength(towards(TypeBeatStyle.LateTint, LyricLineDisplay.TIMING_TINT_MIN))).Within(1e-4));
        }

        [Test]
        public void TheMehEdgeAndBeyondAreTheMaximumStrength()
        {
            Assert.That(tint(-600), Is.EqualTo(towards(TypeBeatStyle.EarlyTint, LyricLineDisplay.TIMING_TINT_MAX)));
            Assert.That(tint(600), Is.EqualTo(towards(TypeBeatStyle.LateTint, LyricLineDisplay.TIMING_TINT_MAX)));

            // Premature / Lagging presses still land the cell Correct, and stay at the cap.
            Assert.That(tint(-2000), Is.EqualTo(tint(-600)));
            Assert.That(tint(2000), Is.EqualTo(tint(600)));
        }

        [Test]
        public void EarlyLeansOrangeAndLateLeansBlueByDefault()
        {
            var typed = TypeBeatStyle.TypedChar;
            var early = tint(-400);
            var late = tint(400);

            Assert.That(early.R, Is.GreaterThan(typed.R));
            Assert.That(early.B, Is.LessThan(typed.B));

            Assert.That(late.B, Is.GreaterThan(typed.B));
            Assert.That(late.R, Is.LessThan(typed.R));
        }

        [Test]
        public void CustomColoursReplaceTheDefaults()
        {
            var early = new Color4(0f, 1f, 0f, 1f);
            var late = new Color4(1f, 0f, 1f, 1f);

            Assert.That(LyricLineDisplay.TimingTintColour(-600, windows, earlyColour: early, lateColour: late), Is.EqualTo(towards(early, LyricLineDisplay.TIMING_TINT_MAX)));
            Assert.That(LyricLineDisplay.TimingTintColour(600, windows, earlyColour: early, lateColour: late), Is.EqualTo(towards(late, LyricLineDisplay.TIMING_TINT_MAX)));
            Assert.That(LyricLineDisplay.TimingTintColour(0, windows, pastRushCap: true, earlyColour: early, lateColour: late), Is.EqualTo(towards(early, LyricLineDisplay.TIMING_TINT_MAX)));
            Assert.That(LyricLineDisplay.TimingTintColour(100, windows, earlyColour: early, lateColour: late), Is.EqualTo(TypeBeatStyle.TypedChar), "Great stays clean");
        }

        [Test]
        public void StoredTintColoursRoundTripAndFallBack()
        {
            var colour = new Color4((byte)18, (byte)52, (byte)86, (byte)255);

            Assert.That(TypeBeatRulesetConfigManager.FormatTintColour(colour), Is.EqualTo("#123456"));
            Assert.That(TypeBeatRulesetConfigManager.TintColour("#123456", TypeBeatStyle.EarlyTint), Is.EqualTo(colour));

            Assert.That(TypeBeatRulesetConfigManager.TintColour("", TypeBeatStyle.EarlyTint), Is.EqualTo(TypeBeatStyle.EarlyTint));
            Assert.That(TypeBeatRulesetConfigManager.TintColour("not a colour", TypeBeatStyle.LateTint), Is.EqualTo(TypeBeatStyle.LateTint));
            Assert.That(TypeBeatRulesetConfigManager.TintColour(null, TypeBeatStyle.LateTint), Is.EqualTo(TypeBeatStyle.LateTint));
        }

        [Test]
        public void StrengthGrowsWithDistanceOnBothSides()
        {
            foreach (int sign in new[] { -1, 1 })
            {
                double previous = 0;

                for (double distance = 175; distance <= 600; distance += 25)
                {
                    double current = strength(tint(sign * distance));
                    Assert.That(current, Is.GreaterThan(previous), $"delta {sign * distance}");
                    previous = current;
                }
            }
        }

        [Test]
        public void ARushCapPressIsTheFullEarlyTintWhateverItsDelta()
        {
            var fullEarly = towards(TypeBeatStyle.EarlyTint, LyricLineDisplay.TIMING_TINT_MAX);

            Assert.That(tint(0, pastRushCap: true), Is.EqualTo(fullEarly));
            Assert.That(tint(300, pastRushCap: true), Is.EqualTo(fullEarly));
        }

        [Test]
        public void TheTintFollowsAScaledLadder()
        {
            // Doubled windows (the Easy mod's scale): 200 ms is a Great, so it stays clean.
            var doubled = SyncWindows.Default.Scaled(2);

            Assert.That(LyricLineDisplay.TimingTintColour(200, doubled), Is.EqualTo(TypeBeatStyle.TypedChar));
            Assert.That(LyricLineDisplay.TimingTintColour(200, windows), Is.Not.EqualTo(TypeBeatStyle.TypedChar));
            Assert.That(LyricLineDisplay.TimingTintColour(1200, doubled), Is.EqualTo(towards(TypeBeatStyle.LateTint, LyricLineDisplay.TIMING_TINT_MAX)));
        }

        [Test]
        public void TheStrengthRangeIsAdjustable()
        {
            const double just_outside = 150 + 1e-9;

            Assert.That(strength(LyricLineDisplay.TimingTintColour(just_outside, windows, minStrength: 0.1, maxStrength: 0.5)),
                Is.EqualTo(strength(towards(TypeBeatStyle.LateTint, 0.1))).Within(1e-4));
            Assert.That(LyricLineDisplay.TimingTintColour(600, windows, minStrength: 0.1, maxStrength: 0.5), Is.EqualTo(towards(TypeBeatStyle.LateTint, 0.5)));
            Assert.That(LyricLineDisplay.TimingTintColour(0, windows, pastRushCap: true, minStrength: 0.1, maxStrength: 0.5), Is.EqualTo(towards(TypeBeatStyle.EarlyTint, 0.5)));

            // Great stays clean whatever the range.
            Assert.That(LyricLineDisplay.TimingTintColour(100, windows, minStrength: 1, maxStrength: 1), Is.EqualTo(TypeBeatStyle.TypedChar));
        }

        [Test]
        public void AMaximumBelowTheMinimumFlattensTheRampInsteadOfInvertingIt()
        {
            var near = LyricLineDisplay.TimingTintColour(200, windows, minStrength: 0.6, maxStrength: 0.2);
            var far = LyricLineDisplay.TimingTintColour(600, windows, minStrength: 0.6, maxStrength: 0.2);

            Assert.That(far, Is.EqualTo(towards(TypeBeatStyle.LateTint, 0.6)));
            Assert.That(near, Is.EqualTo(far));
        }

        [Test]
        public void OutOfRangeStrengthsClamp()
        {
            Assert.That(LyricLineDisplay.TimingTintColour(600, windows, minStrength: -1, maxStrength: 5), Is.EqualTo(towards(TypeBeatStyle.LateTint, 1)));
        }

        [Test]
        public void NanPaintsClean()
        {
            Assert.That(tint(double.NaN), Is.EqualTo(TypeBeatStyle.TypedChar));
        }

        [Test]
        public void TheTimingTintWinsOverTheSyncRampOnACorrectCellOnly()
        {
            var late = tint(400);

            Assert.That(LyricLineDisplay.CellFillColour(CellState.Correct, false, false, syncQuality: 0.2, timingTint: late), Is.EqualTo(late));

            // A Great press under both switches is still the clean typed colour, not a dimmed one.
            Assert.That(LyricLineDisplay.CellFillColour(CellState.Correct, false, false, syncQuality: 0.8, timingTint: tint(100)),
                Is.EqualTo(TypeBeatStyle.TypedChar));

            Assert.That(LyricLineDisplay.CellFillColour(CellState.Wrong, false, false, null, timingTint: late), Is.EqualTo(TypeBeatStyle.ErrorChar));
            Assert.That(LyricLineDisplay.CellFillColour(CellState.Correct, true, false, null, timingTint: late), Is.EqualTo(TypeBeatStyle.FreestyleChar));
        }
    }
}
