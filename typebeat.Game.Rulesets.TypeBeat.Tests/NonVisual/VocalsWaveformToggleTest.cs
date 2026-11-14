// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Timing;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The View menu's Vocals Waveform toggle (aligner version 12, where Demucs became an install of
    /// its own): disabled with a tooltip saying why when the map has no stem and Demucs cannot make
    /// one, available with a tooltip saying what turning it on does otherwise.
    /// </summary>
    [TestFixture]
    public class VocalsWaveformToggleTest
    {
        [Test]
        public void ADisabledToggleSaysWhyAndIsTurnedOff()
        {
            var settings = new EditorTimingSettings();
            var items = new TimingMenuItems(settings, new BindableBeatDivisor(), () => { });
            settings.VocalsWaveform.Value = true;

            items.SetVocalsWaveformAvailability(false, VocalsWaveformSeparation.INSTALL_DEMUCS_TOOLTIP);

            Assert.Multiple(() =>
            {
                Assert.That(items.VocalsWaveform.Action.Disabled, Is.True);
                Assert.That(items.VocalsWaveform.TooltipText.ToString(), Is.EqualTo(VocalsWaveformSeparation.INSTALL_DEMUCS_TOOLTIP));
                Assert.That(settings.VocalsWaveform.Value, Is.False, "never reads on while the full mix is drawn");
            });

            items.SetVocalsWaveformAvailability(true, VocalsWaveformSeparation.WILL_SEPARATE_TOOLTIP);

            Assert.That(items.VocalsWaveform.Action.Disabled, Is.False);
            Assert.That(items.VocalsWaveform.TooltipText.ToString(), Is.EqualTo(VocalsWaveformSeparation.WILL_SEPARATE_TOOLTIP));
        }

        [Test]
        public void TheTooltipsNameTheSettingsPathAndTheMenu()
        {
            Assert.That(VocalsWaveformSeparation.INSTALL_DEMUCS_TOOLTIP, Does.Contain("Install Demucs in " + LocalAlignerVersion.SETTINGS_PATH));
            Assert.That(VocalsWaveformSeparation.REPAIR_DEMUCS_TOOLTIP, Does.Contain("Repair it in " + LocalAlignerVersion.SETTINGS_PATH));
        }
    }
}
