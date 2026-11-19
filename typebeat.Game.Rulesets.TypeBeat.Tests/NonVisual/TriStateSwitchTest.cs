// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.Configuration;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 499: the three-position switch's logic, without a host. Which third of the pill a click
    /// selects, the forward cycle a click on the rest of the row performs, and the three-member rule.
    /// </summary>
    [TestFixture]
    public class TriStateSwitchTest
    {
        private enum TwoWay
        {
            A,
            B,
        }

        [Test]
        public void AClickSelectsTheThirdItLandsOn()
        {
            const float width = TriStateSwitch<KeyboardHitsoundMode>.WIDTH;

            Assert.Multiple(() =>
            {
                Assert.That(TriStateSwitch<KeyboardHitsoundMode>.PositionAt(0, width), Is.EqualTo(0));
                Assert.That(TriStateSwitch<KeyboardHitsoundMode>.PositionAt(width / 3 - 0.5f, width), Is.EqualTo(0));
                Assert.That(TriStateSwitch<KeyboardHitsoundMode>.PositionAt(width / 2, width), Is.EqualTo(1));
                Assert.That(TriStateSwitch<KeyboardHitsoundMode>.PositionAt(width * 2 / 3 + 0.5f, width), Is.EqualTo(2));
                Assert.That(TriStateSwitch<KeyboardHitsoundMode>.PositionAt(width, width), Is.EqualTo(2), "the far edge clamps to the last third");
                Assert.That(TriStateSwitch<KeyboardHitsoundMode>.PositionAt(-3, width), Is.EqualTo(0));
                Assert.That(TriStateSwitch<KeyboardHitsoundMode>.PositionAt(5, 0), Is.EqualTo(0), "an unsized switch does not divide by zero");
            });
        }

        [Test]
        public void CycleStepsForwardAndWraps()
        {
            var toggle = new TriStateSwitch<KeyboardHitsoundMode>();

            Assert.That(toggle.Current.Value, Is.EqualTo(KeyboardHitsoundMode.Off));
            toggle.Cycle();
            Assert.That(toggle.Current.Value, Is.EqualTo(KeyboardHitsoundMode.PerSyllable));
            toggle.Cycle();
            Assert.That(toggle.Current.Value, Is.EqualTo(KeyboardHitsoundMode.PerCharacter));
            toggle.Cycle();
            Assert.That(toggle.Current.Value, Is.EqualTo(KeyboardHitsoundMode.Off));
        }

        [Test]
        public void ADisabledSwitchDoesNotMove()
        {
            var toggle = new TriStateSwitch<KeyboardHitsoundMode>();
            toggle.Current.Disabled = true;

            toggle.Cycle();

            Assert.That(toggle.Current.Value, Is.EqualTo(KeyboardHitsoundMode.Off));
        }

        [Test]
        public void OnlyAThreeMemberEnumFits()
        {
            Assert.Throws<InvalidOperationException>(() => _ = new TriStateSwitch<TwoWay>());
            Assert.Throws<InvalidOperationException>(() => _ = new FormTriStateSwitch<TwoWay>());
        }
    }
}
