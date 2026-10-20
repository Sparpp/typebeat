// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using osu.Framework.Platform;
using typebeat.Game.Configuration;
using typebeat.Game.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the startup read behind <see cref="OsuSetting.DisableRawKeyboard"/>: game.ini is read by hand
    /// before the window exists, so it has to agree with what <see cref="OsuConfigManager"/> writes, and
    /// anything it cannot make sense of has to leave raw keyboard input on.
    /// </summary>
    [TestFixture]
    public class RawKeyboardFallbackTest
    {
        private string root = null!;

        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), $"typebeat-rawkb-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // best-effort cleanup of the temporary storage.
            }
        }

        [Test]
        public void SettingDefaultsToOff()
        {
            using (var config = new OsuConfigManager(new NativeStorage(root)))
                Assert.That(config.GetBindable<bool>(OsuSetting.DisableRawKeyboard).Default, Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ReadsWhatTheConfigManagerWrites(bool stored)
        {
            using (var config = new OsuConfigManager(new NativeStorage(root)))
            {
                config.SetValue(OsuSetting.DisableRawKeyboard, stored);
                config.Save();
            }

            Assert.That(RawKeyboardFallback.IsDisabledInConfig(root), Is.EqualTo(stored));
        }

        [Test]
        public void AFreshlySavedConfigReadsAsOff()
        {
            using (var config = new OsuConfigManager(new NativeStorage(root)))
                config.Save();

            Assert.That(RawKeyboardFallback.IsDisabledInConfig(root), Is.False);
        }

        [Test]
        public void MissingDirectoryOrFileReadsAsOff()
        {
            Assert.That(RawKeyboardFallback.IsDisabledInConfig(root), Is.False, "no game.ini");
            Assert.That(RawKeyboardFallback.IsDisabledInConfig(Path.Combine(root, "does-not-exist")), Is.False, "no directory");
        }

        [TestCase("DisableRawKeyboard = True", true)]
        [TestCase("DisableRawKeyboard=true", true)]
        [TestCase("  DisableRawKeyboard   =   TRUE  ", true)]
        [TestCase("DisableRawKeyboard = 1", true)]
        [TestCase("DisableRawKeyboard = 0", false)]
        [TestCase("DisableRawKeyboard = False", false)]
        [TestCase("DisableRawKeyboard = yes", false)]
        [TestCase("DisableRawKeyboard = ", false)]
        [TestCase("DisableRawKeyboard = True = True", false)]
        [TestCase("DisableRawKeyboard True", false)]
        [TestCase("# DisableRawKeyboard = True", false)]
        [TestCase("disablerawkeyboard = True", false)]
        [TestCase("DisableRawKeyboardX = True", false)]
        public void ParsesALineTheWayTheIniLoaderDoes(string line, bool expected)
        {
            File.WriteAllText(Path.Combine(root, "game.ini"), $"DimLevel = 0.4\n{line}\nShowFpsDisplay = False\n");
            Assert.That(RawKeyboardFallback.IsDisabledInConfig(root), Is.EqualTo(expected));
        }

        [Test]
        public void TheLastLineWins()
        {
            File.WriteAllText(Path.Combine(root, "game.ini"), "DisableRawKeyboard = True\nDisableRawKeyboard = False\n");
            Assert.That(RawKeyboardFallback.IsDisabledInConfig(root), Is.False);

            File.WriteAllText(Path.Combine(root, "game.ini"), "DisableRawKeyboard = False\nDisableRawKeyboard = True\n");
            Assert.That(RawKeyboardFallback.IsDisabledInConfig(root), Is.True);
        }

        [Test]
        public void BinaryGarbageReadsAsOff()
        {
            var random = new Random(1234);
            byte[] bytes = new byte[4096];
            random.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(root, "game.ini"), bytes);

            Assert.That(RawKeyboardFallback.IsDisabledInConfig(root), Is.False);
        }

        [Test]
        public void AnUnreadableFileReadsAsOff()
        {
            string path = Path.Combine(root, "game.ini");
            File.WriteAllText(path, "DisableRawKeyboard = True\n");

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.That(RawKeyboardFallback.IsDisabledInConfig(root), Is.False);
        }

        [Test]
        public void FollowsACustomDataDirectory()
        {
            string custom = Path.Combine(root, "custom");
            Directory.CreateDirectory(custom);

            using (var storageConfig = new StorageConfigManager(new NativeStorage(root)))
            {
                storageConfig.SetValue(StorageConfig.FullPath, custom);
                storageConfig.Save();
            }

            // The default directory's own game.ini is stale once the data has moved, so it is not consulted.
            File.WriteAllText(Path.Combine(root, "game.ini"), "DisableRawKeyboard = False\n");
            File.WriteAllText(Path.Combine(custom, "game.ini"), "DisableRawKeyboard = True\n");
            Assert.That(RawKeyboardFallback.IsDisabledInConfig(root), Is.True);

            File.WriteAllText(Path.Combine(root, "game.ini"), "DisableRawKeyboard = True\n");
            File.WriteAllText(Path.Combine(custom, "game.ini"), "DisableRawKeyboard = False\n");
            Assert.That(RawKeyboardFallback.IsDisabledInConfig(root), Is.False);
        }

        [Test]
        public void AnEmptyCustomPathMeansTheDefaultDirectory()
        {
            File.WriteAllText(Path.Combine(root, "storage.ini"), "FullPath = \n");
            File.WriteAllText(Path.Combine(root, "game.ini"), "DisableRawKeyboard = True\n");

            Assert.That(RawKeyboardFallback.IsDisabledInConfig(root), Is.True);
        }

        [Test]
        public void SettingOffLeavesTheEnvironmentAlone()
        {
            var env = new Dictionary<string, string?>();

            Assert.That(apply(env), Is.False);
            Assert.That(env, Is.Empty);
        }

        [Test]
        public void SettingOnTurnsRawKeyboardOff()
        {
            writeSetting(true);
            var env = new Dictionary<string, string?>();

            Assert.That(apply(env), Is.True);
            Assert.That(env[RawKeyboardFallback.ENVIRONMENT_VARIABLE], Is.EqualTo("0"));
            Assert.That(env.ContainsKey(RawKeyboardFallback.MARKER_VARIABLE), Is.True);
        }

        [TestCase("1")]
        [TestCase("0")]
        public void APlayersOwnVariableIsNeverOverridden(string own)
        {
            writeSetting(true);
            var env = new Dictionary<string, string?> { [RawKeyboardFallback.ENVIRONMENT_VARIABLE] = own };

            Assert.That(apply(env), Is.False);
            Assert.That(env[RawKeyboardFallback.ENVIRONMENT_VARIABLE], Is.EqualTo(own));
            Assert.That(env.ContainsKey(RawKeyboardFallback.MARKER_VARIABLE), Is.False);
        }

        [Test]
        public void ARestartAfterTurningTheSettingOffDropsTheInheritedValue()
        {
            // The previous instance set the variable and the restarted game inherits its environment.
            writeSetting(true);
            var env = new Dictionary<string, string?>();
            apply(env);

            writeSetting(false);

            Assert.That(apply(env), Is.False);
            Assert.That(env, Is.Empty);
        }

        [Test]
        public void ARestartWithTheSettingStillOnKeepsItOff()
        {
            writeSetting(true);
            var env = new Dictionary<string, string?>();
            apply(env);

            Assert.That(apply(env), Is.True);
            Assert.That(env[RawKeyboardFallback.ENVIRONMENT_VARIABLE], Is.EqualTo("0"));
        }

        [Test]
        public void DefaultStoragePathFollowsThePortableRule()
        {
            string startup = Path.Combine(root, "install");
            string appData = Path.Combine(root, "appdata");
            Directory.CreateDirectory(startup);

            Assert.That(RawKeyboardFallback.DefaultStoragePath(startup, appData, "typebeat"), Is.EqualTo(Path.Combine(appData, "typebeat")));

            File.WriteAllText(Path.Combine(startup, "framework.ini"), string.Empty);
            Assert.That(RawKeyboardFallback.DefaultStoragePath(startup, appData, "typebeat"), Is.EqualTo(startup));
        }

        private void writeSetting(bool value) => File.WriteAllText(Path.Combine(root, "game.ini"), $"DisableRawKeyboard = {value}\n");

        private bool apply(Dictionary<string, string?> env) => RawKeyboardFallback.Apply(root,
            name => env.TryGetValue(name, out string? v) ? v : null,
            (name, value) =>
            {
                if (value == null)
                    env.Remove(name);
                else
                    env[name] = value;
            });
    }
}
