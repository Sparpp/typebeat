// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Import;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// <see cref="JapaneseLyricGenerator"/> parses the binary IPAdic shipped by the LibNMeCab.IpaDicBin
    /// package itself. A package update that changes that layout must fail here, not in the editor.
    /// </summary>
    [TestFixture]
    public class JapaneseDictionaryLayoutTest
    {
        [TestCase("sekai", "世界")]
        [TestCase("tabeta", "食べた")]
        [TestCase("konnichiha sekai", "こんにちは 世界")]
        public void TheBundledDictionaryLoadsAndReverseLooksUpReadings(string romaji, string expected)
            => Assert.That(JapaneseLyricGenerator.Generate(romaji), Is.EqualTo(expected));

        [Test]
        public void TheReverseIndexHoldsKnownSurfaces()
        {
            Assert.That(JapaneseLyricGenerator.HasDictionaryReading("世界", "sekai"), Is.True);
            Assert.That(JapaneseLyricGenerator.HasDictionaryReading("世界", "tabeta"), Is.False);
        }
    }
}
