// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Platform;
using typebeat.Game.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the type!beat-specific gameplay/background config defaults so they cannot silently
    /// regress to upstream osu!lazer values. type!beat ships with the beatmap background
    /// image/video enabled and no background dim.
    ///
    /// These assertions read <c>Bindable.Default</c> (the value from <c>SetDefault</c>), not the
    /// current/stored value, mirroring the "change defaults only, never touch stored user
    /// settings" requirement: existing installs persist every key to game.ini and reload their
    /// stored value over these defaults, so only fresh installs pick these up.
    /// </summary>
    [TestFixture]
    public class ConfigDefaultsTest
    {
        private string tempPath = null!;
        private OsuConfigManager config = null!;

        [SetUp]
        public void SetUp()
        {
            tempPath = Path.Combine(Path.GetTempPath(), $"typebeat-config-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempPath);
            config = new OsuConfigManager(new NativeStorage(tempPath));
        }

        [TearDown]
        public void TearDown()
        {
            config.Dispose();

            try
            {
                Directory.Delete(tempPath, true);
            }
            catch
            {
                // best-effort cleanup of the temporary config storage.
            }
        }

        [Test]
        public void BackgroundDimDefaultsToZero()
            => Assert.That(config.GetBindable<double>(OsuSetting.DimLevel).Default, Is.EqualTo(0.0));

        [Test]
        public void MenuBackgroundSourceDefaultsToBeatmapWithStoryboard()
            => Assert.That(config.GetBindable<BackgroundSource>(OsuSetting.MenuBackgroundSource).Default,
                Is.EqualTo(BackgroundSource.BeatmapWithStoryboard));

        [Test]
        public void StoryboardAndVideoDefaultsToEnabled()
            => Assert.That(config.GetBindable<bool>(OsuSetting.ShowStoryboard).Default, Is.True);

        /// <summary>
        /// The two caret heads are dressed from two separate keys, and their defaults deliberately
        /// disagree: the typing caret starts on Underline, the sung playhead on Line. Unlike the
        /// settings above, the playhead's default is NOT harmless to change: its key is newer than
        /// any shipped install, so nobody has a stored row for it yet and every player takes this
        /// value on their next boot. Pinned here so a "make them symmetric" tidy-up has to be a
        /// deliberate act.
        /// </summary>
        [Test]
        public void TheTwoCaretHeadsHaveSeparateDefaults()
        {
            using (var rulesetConfig = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo))
            {
                Assert.That(rulesetConfig.GetBindable<CaretStyle>(TypeBeatRulesetSetting.CaretStyle).Default,
                    Is.EqualTo(CaretStyle.Underline));
                Assert.That(rulesetConfig.GetBindable<CaretStyle>(TypeBeatRulesetSetting.SungCaretStyle).Default,
                    Is.EqualTo(CaretStyle.Line));
            }
        }

        /// <summary>
        /// <see cref="CaretStyle.None"/> is offered by the SUNG playhead dropdown only. It is not a
        /// caret shape: it means "draw no head at all", which the typing caret (whose whole job is
        /// to mark where YOU are) cannot express, and unlike the playhead it has no lit syllable
        /// group standing in for it. The typing dropdown therefore lists its items explicitly rather
        /// than taking the enum wholesale, and this pins that list against the enum in BOTH
        /// directions, so a sixth style added later either reaches the typing caret or is a
        /// deliberate second exclusion.
        /// </summary>
        [Test]
        public void OnlyTheSungPlayheadIsOfferedTheNoPlayheadStyle()
        {
            Assert.That(TypeBeatSettingsSubsection.TYPING_CARET_STYLES, Does.Not.Contain(CaretStyle.None));

            var everythingElse = Enum.GetValues<CaretStyle>().Where(s => s != CaretStyle.None);
            Assert.That(TypeBeatSettingsSubsection.TYPING_CARET_STYLES, Is.EquivalentTo(everythingElse),
                "every style that is not None must still reach the typing caret dropdown");
        }

        /// <summary>
        /// Space-to-skip-a-word ships OFF (backlog 110). It changes how a keypress is judged, so it
        /// must be something a player asks for: nobody's space key starts throwing words away because
        /// they updated.
        /// </summary>
        [Test]
        public void SpaceSkipsWordDefaultsToOff()
        {
            using (var rulesetConfig = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo))
            {
                Assert.That(rulesetConfig.GetBindable<bool>(TypeBeatRulesetSetting.SpaceSkipsWord).Default, Is.False);
            }
        }

        /// <summary>
        /// The space error dot ships OFF (backlog 197). Unlike the setting above it changes nothing
        /// about judgement, so the reason is the other one: it adds a mark to the lyric line, and
        /// existing styling stays the only styling until a player asks for it.
        /// </summary>
        [Test]
        public void SpaceErrorDotDefaultsToOff()
        {
            using (var rulesetConfig = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo))
            {
                Assert.That(rulesetConfig.GetBindable<bool>(TypeBeatRulesetSetting.UseSpaceErrorDot).Default, Is.False);
            }
        }
    }
}
