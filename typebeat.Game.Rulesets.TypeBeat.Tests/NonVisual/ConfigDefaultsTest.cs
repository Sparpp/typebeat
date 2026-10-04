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
    /// image/video enabled and 40 percent background dim.
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
        public void BackgroundDimDefaultsToFortyPercent()
            => Assert.That(config.GetBindable<double>(OsuSetting.DimLevel).Default, Is.EqualTo(0.4));

        [TestCase(0)]
        [TestCase(0.2)]
        [TestCase(0.4)]
        [TestCase(0.6)]
        [TestCase(1)]
        public void StoredDimIsNeverTouchedByTheNewDefault(double stored)
        {
            // The 40% default is for NEW installs only. A stored value is whatever the player last
            // chose, and it must survive untouched at every level - including under 40%, which is
            // the whole point of not running a migration.
            config.Dispose();
            File.WriteAllText(Path.Combine(tempPath, "game.ini"), $"DimLevel = {stored.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
            config = new OsuConfigManager(new NativeStorage(tempPath));

            Assert.That(config.Get<double>(OsuSetting.DimLevel), Is.EqualTo(stored), "a stored dim survives the new default");
        }

        [Test]
        public void PlayerEditedDimSurvivesARestart()
        {
            config.SetValue(OsuSetting.DimLevel, 0.1);
            config.Save();
            config.Dispose();
            config = new OsuConfigManager(new NativeStorage(tempPath));

            Assert.That(config.Get<double>(OsuSetting.DimLevel), Is.EqualTo(0.1), "a player's own dim is never migrated");
        }

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
        /// Space-to-skip-a-word ships ON (backlog 198, reversing backlog 110's opt-in): it is how
        /// every other typing game treats the spacebar, and the settings checkbox remains for turning
        /// it off. Only the SHIPPED config default is on; the engine property itself stays off by
        /// default (pinned in SpaceSkipWordTest), because a stored replay with no CONFIG frame must
        /// keep decoding as the classic stack.
        /// </summary>
        [Test]
        public void SpaceSkipsWordDefaultsToOn()
        {
            using (var rulesetConfig = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo))
            {
                Assert.That(rulesetConfig.GetBindable<bool>(TypeBeatRulesetSetting.SpaceSkipsWord).Default, Is.True);
            }
        }

        /// <summary>
        /// The space error dot ships ON as of 2026-09-21: the mark it adds is the ONLY thing that can
        /// show a mistake sitting in a word gap once Space to Skip parks the caret there, so leaving it
        /// off hid errors the player still had to fix. It changes nothing about judgement.
        /// </summary>
        [Test]
        public void SpaceErrorDotDefaultsToOn()
        {
            using (var rulesetConfig = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo))
            {
                Assert.That(rulesetConfig.GetBindable<bool>(TypeBeatRulesetSetting.UseSpaceErrorDot).Default, Is.True);
            }
        }

        /// <summary>
        /// The syllable markers ship ON (backlog 225), the same call as the dot above but for a
        /// different reason. The dot is a margin note about a mistake the player still has to fix;
        /// a marker tells the player where the word they are about to type turns over, and span
        /// judgement (backlog 179) has been asking them to pace to exactly that since well before
        /// the mark existed. Withholding it by default would keep the
        /// subdivision discoverable only by ear.
        /// </summary>
        [Test]
        public void SyllableMarkersDefaultToOn()
        {
            using (var rulesetConfig = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo))
            {
                Assert.That(rulesetConfig.GetBindable<bool>(TypeBeatRulesetSetting.ShowSyllableMarkers).Default, Is.True);
            }
        }

        /// <summary>
        /// The sync metric ships OFF (backlog 251), which is the whole shape of that change: the
        /// owner asked for it out of the gameplay UI, and it comes back only for a player who ticks
        /// the box. The key is brand new, so unlike the two above this default is NOT harmless to
        /// flip: nobody holds a stored row for it yet, so every install reads this value on its next
        /// boot and is then pinned to whatever it said (the reasoning on
        /// <see cref="TheTwoCaretHeadsHaveSeparateDefaults"/>'s playhead half, verbatim).
        /// </summary>
        /// <summary>
        /// The lyric font size (backlog 334) defaults to exactly the size the game always drew at, so
        /// nothing changes for anyone until they move the slider, and its range is 24 to 72 in steps
        /// of 1.
        /// </summary>
        [Test]
        public void LyricFontSizeDefaultsToTheOldConstant()
        {
            using (var rulesetConfig = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo))
            {
                var size = (osu.Framework.Bindables.BindableFloat)rulesetConfig.GetBindable<float>(TypeBeatRulesetSetting.LyricFontSize);

                Assert.That(size.Default, Is.EqualTo(42f));
                Assert.That(size.Default, Is.EqualTo(TypeBeatStyle.LYRIC_FONT_SIZE));
                Assert.That(size.MinValue, Is.EqualTo(24f));
                Assert.That(size.MaxValue, Is.EqualTo(72f));
                Assert.That(size.Precision, Is.EqualTo(1f));
            }
        }

        /// <summary>
        /// The row pitch is the player's spacing, floored at 1.3 times the font size so a large font
        /// cannot overlap rows; the floor never lowers a spacing the player chose. Worked values: at
        /// the default 96 spacing it binds nowhere in the size range (72 x 1.3 = 93.6); at the
        /// minimum 40 spacing it binds from size 31 up (at 24, 24 x 1.3 = 31.2, so 40 stands).
        /// </summary>
        [TestCase(96f, 42f, 96f)]
        [TestCase(96f, 72f, 96f)]
        [TestCase(40f, 24f, 40f)]
        [TestCase(40f, 42f, 54.6f)]
        [TestCase(40f, 72f, 93.6f)]
        [TestCase(200f, 72f, 200f)]
        public void RowPitchIsTheSpacingFlooredByTheFontSize(float spacing, float fontSize, float expected)
            => Assert.That(LyricStage.EffectiveRowPitch(spacing, fontSize), Is.EqualTo(expected).Within(1e-4));

        [Test]
        public void SyncMetricDefaultsToOff()
        {
            using (var rulesetConfig = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo))
            {
                Assert.That(rulesetConfig.GetBindable<bool>(TypeBeatRulesetSetting.ShowSyncMetric).Default, Is.False);
            }
        }
    }
}
