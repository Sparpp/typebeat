// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pure maths for the SYNC TINT (<see cref="LyricLineDisplay.CorrectCharColour"/>): a correctly
    /// typed char is painted somewhere on the ramp from <see cref="TypeBeatStyle.UntypedChar"/> to
    /// <see cref="TypeBeatStyle.TypedChar"/> according to how in sync the keypress that scored it
    /// was, so a player nailing the playhead leaves a bright trail and one dragging or rushing
    /// leaves a dull one.
    ///
    /// <para>The invariant that actually matters is the FLOOR. <see cref="SyncWindows.SyncQuality"/>
    /// is exactly 0 at and beyond the Ok-window edges while the cell still lands Correct, so an
    /// unfloored ramp would paint a char the player DID type in precisely the untyped grey. The floor
    /// tests below pin that in contrast terms rather than by restating the interpolation, so they
    /// stay meaningful if the ramp's shape is ever retuned.</para>
    /// </summary>
    [TestFixture]
    public class SyncTintTest
    {
        private static Color4 tint(double quality) => LyricLineDisplay.CorrectCharColour(quality);

        // --- Colour measurement (sRGB IEC 61966-2-1 / WCAG 2.x), used only to state design
        // constraints in terms a human can check against a contrast checker. ---

        private static double toLinear(double channel)
            => channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

        /// <summary>WCAG relative luminance. Monotone in every channel, so it doubles as the
        /// brightness ordering the ramp is asserted on.</summary>
        private static double luminance(Color4 c)
            => 0.2126 * toLinear(c.R) + 0.7152 * toLinear(c.G) + 0.0722 * toLinear(c.B);

        private static double contrast(Color4 a, Color4 b)
        {
            double la = luminance(a);
            double lb = luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary>How a Missed cell actually reaches the eye: the untyped grey at alpha 0.4 over the
        /// playfield panel. Composited in LINEAR light, which is where the renderer blends.</summary>
        private static Color4 missedCell()
        {
            const float a = 0.4f;

            double lerp(double over, double under) => a * toLinear(over) + (1 - a) * toLinear(under);

            double toSrgb(double linear) => linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;

            var over = TypeBeatStyle.UntypedChar;
            var under = TypeBeatStyle.Background;

            return new Color4(
                (float)toSrgb(lerp(over.R, under.R)),
                (float)toSrgb(lerp(over.G, under.G)),
                (float)toSrgb(lerp(over.B, under.B)),
                1f);
        }

        private static void assertBrighterThan(Color4 brighter, Color4 duller, string what)
        {
            Assert.That(luminance(brighter), Is.GreaterThan(luminance(duller)), what);
            Assert.That(brighter.R, Is.GreaterThan(duller.R), $"{what} (R)");
            Assert.That(brighter.G, Is.GreaterThan(duller.G), $"{what} (G)");
            Assert.That(brighter.B, Is.GreaterThan(duller.B), $"{what} (B)");
        }

        [Test]
        public void DeadOnIsExactlyTheFullTypedColour()
        {
            // A perfectly timed line must look exactly as it did before the ramp existed: quality 1
            // is the top of the ramp, not float-approximately the top of it.
            Assert.That(tint(1), Is.EqualTo(TypeBeatStyle.TypedChar));
        }

        [Test]
        public void TheWorstCorrectCharIsNeverTheUntypedGrey()
        {
            var floor = tint(0);

            Assert.That(floor, Is.Not.EqualTo(TypeBeatStyle.UntypedChar));
            assertBrighterThan(floor, TypeBeatStyle.UntypedChar, "floor vs untyped grey");
        }

        [Test]
        public void TheFloorClearsBothStatesItCouldBeMistakenFor()
        {
            var floor = tint(0);
            var missed = missedCell();

            // The two things a correctly typed char must never be confused with.
            assertBrighterThan(floor, TypeBeatStyle.UntypedChar, "floor vs an untyped char");
            assertBrighterThan(floor, missed, "floor vs a Missed char");

            // And the yardstick the floor was chosen against: untyped vs Missed is a distinction the
            // game already ships and asks players to read, so the floor must clear untyped by at
            // least that much. (It clears it by considerably more; the >= is the contract.)
            Assert.That(contrast(floor, TypeBeatStyle.UntypedChar),
                Is.GreaterThanOrEqualTo(contrast(TypeBeatStyle.UntypedChar, missed)),
                "the worst correct char must be at least as separable from untyped as untyped is from Missed");

            // A hard absolute floor under that relative one, so shrinking the shipped untyped/Missed
            // step could never quietly license an invisible ramp floor.
            Assert.That(contrast(floor, TypeBeatStyle.UntypedChar), Is.GreaterThan(1.5));
        }

        [Test]
        public void TheFloorStillLeavesMostOfTheRampForTheSignal()
        {
            var floor = tint(0);

            // Strictly inside the ramp: a floor at either end is either invisible feedback (0) or no
            // feedback at all (1).
            assertBrighterThan(TypeBeatStyle.TypedChar, floor, "full typed colour vs the floor");
            assertBrighterThan(floor, TypeBeatStyle.UntypedChar, "floor vs untyped grey");

            Assert.That(LyricLineDisplay.SYNC_TINT_FLOOR, Is.GreaterThan(0).And.LessThan(0.5));
        }

        [Test]
        public void BrightnessRisesWithSyncAllTheWayUp()
        {
            var previous = tint(0);

            for (int step = 1; step <= 20; step++)
            {
                var current = tint(step / 20.0);
                assertBrighterThan(current, previous, $"quality {step / 20.0} vs {(step - 1) / 20.0}");
                previous = current;
            }

            // End to end, the ramp really does span floor -> full typed colour.
            Assert.That(previous, Is.EqualTo(TypeBeatStyle.TypedChar));
        }

        [Test]
        public void OutOfRangeAndNanClamp()
        {
            Assert.That(tint(-0.5), Is.EqualTo(tint(0)));
            Assert.That(tint(-1000), Is.EqualTo(tint(0)));
            Assert.That(tint(double.NegativeInfinity), Is.EqualTo(tint(0)));

            Assert.That(tint(1.5), Is.EqualTo(TypeBeatStyle.TypedChar));
            Assert.That(tint(double.PositiveInfinity), Is.EqualTo(TypeBeatStyle.TypedChar));

            // NaN fails every comparison, so Math.Clamp alone would pass it straight through into
            // the interpolation and out as a NaN colour. Guarded to the floor.
            Assert.That(tint(double.NaN), Is.EqualTo(tint(0)));
        }

        [Test]
        public void RealDeltasRankTheWayThePlayerWouldExpect()
        {
            // Built the way gameplay builds it: the cell's window tier, then the same asymmetric
            // quality the results screen's sync percent is summed from.
            var windows = SyncWindows.For(TimingGranularity.Word);

            var deadOn = tint(windows.SyncQuality(0));
            var goodEdge = tint(windows.SyncQuality(windows.GoodLate));
            var okEdge = tint(windows.SyncQuality(windows.OkLate));
            var beyondOk = tint(windows.SyncQuality(windows.OkLate * 3));

            assertBrighterThan(deadOn, goodEdge, "dead on vs the Good-window edge");
            assertBrighterThan(goodEdge, okEdge, "the Good edge vs the Ok edge");

            Assert.That(deadOn, Is.EqualTo(TypeBeatStyle.TypedChar));

            // The Ok edge is quality 0, and everything past it stays there: this is exactly the case
            // that would have painted an untyped grey without the floor.
            Assert.That(okEdge, Is.EqualTo(tint(0)));
            Assert.That(beyondOk, Is.EqualTo(tint(0)));
            assertBrighterThan(okEdge, TypeBeatStyle.UntypedChar, "an Ok-edge correct char vs untyped");
        }

        [Test]
        public void TheRampIsAsymmetricJustAsJudgementIs()
        {
            // Late tolerance is wider than early tolerance, so the same magnitude of error is
            // punished harder when the player rushes than when they drag. The tint inherits that
            // from SyncQuality rather than restating it.
            var windows = SyncWindows.For(TimingGranularity.Syllable);
            double offset = windows.OkEarly * 0.5;

            assertBrighterThan(tint(windows.SyncQuality(offset)), tint(windows.SyncQuality(-offset)),
                "the same error late vs early");
        }

        [Test]
        public void ATighterWindowTierPunishesTheSameDeltaHarder()
        {
            // Estimated lines and low-confidence words are judged at the widest (Line) tier, so the
            // same delta must leave a brighter char there than on a syllable-timed map. Free, because
            // the tint reads the cell's own JudgeGranularity.
            const double delta = 300;

            assertBrighterThan(tint(SyncWindows.For(TimingGranularity.Line).SyncQuality(delta)),
                tint(SyncWindows.For(TimingGranularity.Syllable).SyncQuality(delta)),
                "Line tier vs Syllable tier at the same delta");
        }
    }
}
