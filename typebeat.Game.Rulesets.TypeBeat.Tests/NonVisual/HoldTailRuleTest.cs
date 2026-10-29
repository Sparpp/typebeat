// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class HoldTailRuleTest
    {
        private const double start = 1200;
        private const double end = 1900;

        /// <summary>
        /// A hold over [1200, 1900] whose head is due at 1500. The share is measured from the head's press, or
        /// from the hold's start when the head came before it, and the Great slack (80 ms here) is the authored
        /// span's either way. Read from the authored start, a press at 1500 let go at 1550 was 350 of 700, a Meh,
        /// crediting 300 ms the player never held.
        /// </summary>
        [TestCase(1500, 1550, JudgementType.Miss)] // pressed on target: 50 of 400
        [TestCase(1500, 1720, JudgementType.Meh)] // 220 of 400
        [TestCase(1500, 1810, JudgementType.Ok)] // 310 of 400
        [TestCase(1200, 1550, JudgementType.Meh)] // pressed at the start: 350 of 700
        [TestCase(1100, 1550, JudgementType.Meh)] // pressed before it: counted from the start
        [TestCase(1500, 1820, JudgementType.Great)] // inside the slack
        [TestCase(1850, 1851, JudgementType.Great)] // a late head inside the slack
        public void TheTailIsGradedFromThePress(double pressed, double release, JudgementType expected)
            => Assert.That(HoldTailRule.Grade(start, end, pressed, release), Is.EqualTo(expected));
    }
}
