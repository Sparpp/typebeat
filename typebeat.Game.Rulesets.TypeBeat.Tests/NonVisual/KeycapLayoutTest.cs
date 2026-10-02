// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform;
using osuTK.Input;
using typebeat.Game.Configuration;
using typebeat.Game.Database;
using typebeat.Game.Input;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 371: shortcuts follow the KEYCAP. The root input manager rewrites a physical letter key
    /// to the QWERTY key carrying the same keycap letter (<see cref="KeycapLayout"/>,
    /// <see cref="KeycapKeyRewriter"/>). Gameplay typing used to read the physical position back
    /// through KeyCharMap; since backlog 383 it reads the OS's committed text instead, which the rewrite
    /// never touches. The pins here are the translation itself, the promise
    /// that the rewrite moves no key in or out of the typing block, the retired setting's
    /// stale ini line, the OS keymap detection that replaced it (backlog 383), and the key NAMES the
    /// settings screen prints.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class KeycapLayoutTest
    {
        private static readonly KeyboardLayout[] all_layouts = Enum.GetValues<KeyboardLayout>();

        private static readonly Key[] all_keys = Enum.GetValues<Key>().Distinct().ToArray();

        [Test]
        public void TheKeycapIsTheLetterPrintedOnThePhysicalKey()
        {
            // AZERTY: A and Q swap, Z and W swap, M sits on the QWERTY semicolon position.
            Assert.That(KeycapLayout.ToKeycap(Key.Q, KeyboardLayout.Azerty), Is.EqualTo(Key.A));
            Assert.That(KeycapLayout.ToKeycap(Key.A, KeyboardLayout.Azerty), Is.EqualTo(Key.Q));
            Assert.That(KeycapLayout.ToKeycap(Key.W, KeyboardLayout.Azerty), Is.EqualTo(Key.Z));
            Assert.That(KeycapLayout.ToKeycap(Key.Z, KeyboardLayout.Azerty), Is.EqualTo(Key.W));
            Assert.That(KeycapLayout.ToKeycap(Key.Semicolon, KeyboardLayout.Azerty), Is.EqualTo(Key.M));
            Assert.That(KeycapLayout.ToKeycap(Key.M, KeyboardLayout.Azerty), Is.EqualTo(Key.Semicolon));

            // QWERTZ: Y and Z swap.
            Assert.That(KeycapLayout.ToKeycap(Key.Y, KeyboardLayout.Qwertz), Is.EqualTo(Key.Z));
            Assert.That(KeycapLayout.ToKeycap(Key.Z, KeyboardLayout.Qwertz), Is.EqualTo(Key.Y));

            // Keys that do not move stay put (R is R on every layout here).
            foreach (var layout in all_layouts)
            {
                Assert.That(KeycapLayout.ToKeycap(Key.R, layout), Is.EqualTo(Key.R));
                Assert.That(KeycapLayout.ToKeycap(Key.ControlLeft, layout), Is.EqualTo(Key.ControlLeft));
            }
        }

        /// <summary>
        /// A bijection that is its own inverse, so every physical key has exactly one keycap key and a
        /// press can always be released as the key it was pressed as. QWERTY is the identity.
        /// </summary>
        [Test]
        public void TheTranslationIsASwapAndQwertyIsTheIdentity()
        {
            foreach (var layout in all_layouts)
            {
                foreach (var key in all_keys)
                {
                    Key keycap = KeycapLayout.ToKeycap(key, layout);

                    Assert.That(KeycapLayout.ToPhysical(keycap, layout), Is.EqualTo(key), $"{layout} {key}");

                    if (layout == KeyboardLayout.Qwerty)
                        Assert.That(keycap, Is.EqualTo(key));

                    // The InputKey form (stored bindings) agrees with the Key form wherever both exist.
                    var input = KeyCombination.FromKey(key);
                    var inputKeycap = KeyCombination.FromKey(keycap);
                    if (input != InputKey.None && inputKeycap != InputKey.None)
                        Assert.That(KeycapLayout.ToPhysical(inputKeycap, layout), Is.EqualTo(input), $"{layout} {key} as InputKey");
                }

                Assert.That(all_keys.Select(k => KeycapLayout.ToKeycap(k, layout)).Distinct().Count(), Is.EqualTo(all_keys.Length), $"{layout} is one to one");
            }
        }

        /// <summary>
        /// THE identity pin, reworked for backlog 383. It used to drive every physical key through the
        /// rewrite and then through KeyCharMap and demand the very character the physical key typed
        /// before. Typing no longer reads the key at all (the OS commits the character, and the
        /// rewrite never touches text), so what the rewrite could still disturb is the key's PLACE:
        /// whether the playfield treats it as a key that commits text (and pairs it with its commit)
        /// and as a typing key it swallows mid-line. A rewrite that moved a key in or out of either
        /// set would lose a keystroke or leak one to a global binding; it moves none.
        /// </summary>
        [Test]
        public void EveryPhysicalKeyTypesExactlyWhatItTypedBefore()
        {
            foreach (var layout in all_layouts)
            {
                var rewriter = new KeycapKeyRewriter { Layout = { Value = layout } };

                foreach (var physical in all_keys)
                {
                    Key delivered = deliver(rewriter, physical);

                    Assert.That(TypingKeys.CommitsText(delivered), Is.EqualTo(TypingKeys.CommitsText(physical)), $"{layout} {physical}");

                    // The one key that changes swallow class is AZERTY's M position: the ',' keycap,
                    // delivered as the semicolon key, a punctuation position swallowed only when its
                    // commit types (which a ',' does under Literate and does not without it).
                    if (layout == KeyboardLayout.Azerty && (physical == Key.M || physical == Key.Semicolon))
                        continue;

                    Assert.That(TypingKeys.AlwaysSwallowed(delivered), Is.EqualTo(TypingKeys.AlwaysSwallowed(physical)), $"{layout} {physical}");
                }
            }
        }

        [Test]
        public void TheFrenchHomeRowStillTypesWhatItsKeycapsSay()
        {
            var rewriter = new KeycapKeyRewriter { Layout = { Value = KeyboardLayout.Azerty } };

            // The keycap A (physical Q) is delivered as Key.A, a typing key; the OS commits its 'a'.
            Key a = deliver(rewriter, Key.Q);
            Assert.That(a, Is.EqualTo(Key.A));
            Assert.That(TypingKeys.AlwaysSwallowed(a));
            Assert.That(TextInputFold.Fold("a", false), Is.EqualTo(new[] { 'a' }));

            // The keycap M (physical semicolon) is delivered as Key.M, a typing key, typing 'm'.
            Key m = deliver(rewriter, Key.Semicolon);
            Assert.That(m, Is.EqualTo(Key.M));
            Assert.That(TypingKeys.AlwaysSwallowed(m));
            Assert.That(TextInputFold.Fold("m", false), Is.EqualTo(new[] { 'm' }));

            // The ',' keycap (physical M) is a punctuation position: its ',' is inert outside
            // Literate and ',' under it, as before.
            Key comma = deliver(rewriter, Key.M);
            Assert.That(TypingKeys.IsPunctuationPosition(comma));
            Assert.That(TextInputFold.Fold(",", false), Is.Empty);
            Assert.That(TextInputFold.Fold(",", true), Is.EqualTo(new[] { ',' }));
        }

        /// <summary>
        /// A release is rewritten to whatever its PRESS was rewritten to, so flipping the layout while
        /// a key is held cannot strand a key in the pressed state.
        /// </summary>
        [Test]
        public void AReleaseAlwaysMatchesItsPress()
        {
            var rewriter = new KeycapKeyRewriter { Layout = { Value = KeyboardLayout.Azerty } };

            var press = rewrite(rewriter, new KeyboardKeyInput(Key.W, true));
            Assert.That(press.Entries.Single(), Is.EqualTo(new ButtonInputEntry<Key>(Key.Z, true)));

            rewriter.Layout.Value = KeyboardLayout.Qwerty;

            var release = rewrite(rewriter, new KeyboardKeyInput(Key.W, false));
            Assert.That(release.Entries.Single(), Is.EqualTo(new ButtonInputEntry<Key>(Key.Z, false)), "released as the key it was pressed as");

            // And the next press of the same key follows the new layout.
            var again = rewrite(rewriter, new KeyboardKeyInput(Key.W, true));
            Assert.That(again.Entries.Single(), Is.EqualTo(new ButtonInputEntry<Key>(Key.W, true)));
        }

        [Test]
        public void OnlyKeyboardKeysAreRewritten()
        {
            var rewriter = new KeycapKeyRewriter { Layout = { Value = KeyboardLayout.Azerty } };
            var mouse = new MouseButtonInput(MouseButton.Left, true);
            var inputs = new List<IInput> { mouse, new KeyboardKeyInput(Key.Q, true) };

            rewriter.Rewrite(inputs);

            Assert.That(inputs[0], Is.SameAs(mouse));
            Assert.That(((KeyboardKeyInput)inputs[1]).Entries.Single().Button, Is.EqualTo(Key.A));
        }

        // ---------------------------------------------------------------------------------------
        // The retired setting (backlog 383) and the OS detection that replaced it
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The old setting's migration, reworked: backlog 371 carried a ruleset row into
        /// <c>OsuSetting.KeyboardLayout</c>, and backlog 383 retired that setting too, so what a
        /// stored choice now meets is the ini loader. Whatever an old game.ini holds for it, the
        /// config loads cleanly, every other setting is still read, and the next save writes the file
        /// without the line: no migration code runs, and nothing can resurrect the setting.
        /// </summary>
        [TestCase("Azerty")]
        [TestCase("Qwertz")]
        [TestCase("Qwerty")]
        [TestCase("not-a-layout")]
        [TestCase(null)]
        public void AStoredLayoutLineIsDroppedWithoutMigration(string? storedLine)
        {
            string directory = Path.Combine(Path.GetTempPath(), "typebeat-layout-retired-" + Guid.NewGuid().ToString("N"));

            try
            {
                var storage = new NativeStorage(directory);

                using (var stream = storage.CreateFileSafely("game.ini"))
                using (var writer = new StreamWriter(stream))
                {
                    if (storedLine != null)
                        writer.WriteLine($"KeyboardLayout = {storedLine}");

                    // An unrelated setting must still be read.
                    writer.WriteLine("ShowFpsDisplay = True");
                }

                using (var config = new OsuConfigManager(storage))
                {
                    Assert.That(config.Get<bool>(OsuSetting.ShowFpsDisplay), Is.True, "the rest of the file is still read");
                    Assert.That(Enum.GetNames<OsuSetting>(), Has.No.Member("KeyboardLayout"), "the setting is gone");
                    config.Save();
                }

                string saved = File.ReadAllText(Path.Combine(directory, "game.ini"));
                Assert.That(saved, Does.Not.Contain("KeyboardLayout"), "the stale line is dropped on save");
                Assert.That(saved, Does.Contain("ShowFpsDisplay"));
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch
                {
                    // A file can stay locked briefly on Windows; a stray temp folder is harmless.
                }
            }
        }

        [Test]
        public void TheLayoutIsDetectedFromTheOsKeymap()
        {
            Assert.That(OsKeyboardLayout.Detect(new OsLayoutProvider()), Is.EqualTo(KeyboardLayout.Qwerty));
            Assert.That(OsKeyboardLayout.Detect(new OsLayoutProvider(("Y", "Z"), ("Z", "Y"))), Is.EqualTo(KeyboardLayout.Qwertz));
            Assert.That(OsKeyboardLayout.Detect(new OsLayoutProvider(("Q", "A"), ("A", "Q"), ("W", "Z"), ("Z", "W"), ("Semicolon", "M"), ("M", ","))),
                Is.EqualTo(KeyboardLayout.Azerty));

            // Names arrive upper-cased from SDL, but nothing here depends on it.
            Assert.That(OsKeyboardLayout.Detect(new OsLayoutProvider(("Y", "z"), ("Z", "y"))), Is.EqualTo(KeyboardLayout.Qwertz));
        }

        /// <summary>
        /// Every other keyboard map is QWERTY, the identity: a headless host's provider (keys named
        /// after their enum), a non-Latin layout whose letters are not A-Z, and a layout that moves
        /// only half of a supported swap. Dvorak and Colemak move letters the three-layout rewrite
        /// does not know, so their shortcuts stay positional, exactly as they were under the setting.
        /// </summary>
        [Test]
        public void AnythingElseIsQwerty()
        {
            Assert.That(OsKeyboardLayout.Detect(new ReadableKeyCombinationProvider()), Is.EqualTo(KeyboardLayout.Qwerty));
            Assert.That(OsKeyboardLayout.Detect(new OsLayoutProvider(("Q", "Й"), ("W", "Ц"), ("Y", "Н"), ("Z", "Я"), ("A", "Ф"))), Is.EqualTo(KeyboardLayout.Qwerty));
            Assert.That(OsKeyboardLayout.Detect(new OsLayoutProvider(("Y", "Z"))), Is.EqualTo(KeyboardLayout.Qwerty));
            Assert.That(OsKeyboardLayout.Detect(new OsLayoutProvider(("Q", "A"), ("A", "Q"))), Is.EqualTo(KeyboardLayout.Qwerty));
            Assert.That(OsKeyboardLayout.Detect(new OsLayoutProvider(("S", "O"), ("D", "E"), ("F", "U"), ("Semicolon", "S"))), Is.EqualTo(KeyboardLayout.Qwerty));
        }

        [Test]
        public void AKeymapChangeIsDetectedAgain()
        {
            var os = new OsLayoutProvider();
            var scheduled = new List<Action>();
            var detected = new OsKeyboardLayout(os, scheduled.Add);

            Assert.That(detected.Current.Value, Is.EqualTo(KeyboardLayout.Qwerty));

            os.Set(("Y", "Z"), ("Z", "Y"));
            typeof(ReadableKeyCombinationProvider).GetMethod("OnKeymapChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(os, null);

            Assert.That(detected.Current.Value, Is.EqualTo(KeyboardLayout.Qwerty), "not before it reaches the update thread");

            scheduled.ForEach(a => a());
            Assert.That(detected.Current.Value, Is.EqualTo(KeyboardLayout.Qwertz));
        }

        [Test]
        public void NoOsKeymapIsQwerty()
        {
            Assert.That(new OsKeyboardLayout(null, _ => { }).Current.Value, Is.EqualTo(KeyboardLayout.Qwerty));
        }

        // ---------------------------------------------------------------------------------------
        // Key names on the settings screen and in hotkey hints
        // ---------------------------------------------------------------------------------------

        [Test]
        public void KeyNamesFollowTheKeycap()
        {
            var layout = new Bindable<KeyboardLayout>(KeyboardLayout.Azerty);
            var host = new PhysicalNamingProvider();
            var provider = new KeycapKeyCombinationProvider(host, layout);

            // A letter is named by the letter itself: the binding now answers to that keycap.
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.A)), Is.EqualTo("A"));
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.Z)), Is.EqualTo("Z"));

            // Any other key is named by the host for the PHYSICAL position it now means: the
            // semicolon key is the physical M position (the ',' keycap) under AZERTY.
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.Semicolon)), Is.EqualTo("phys:M"));
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.Enter)), Is.EqualTo("phys:Enter"));

            // QWERTY hands everything to the host untouched.
            layout.Value = KeyboardLayout.Qwerty;
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.A)), Is.EqualTo("phys:A"));
            Assert.That(provider.GetReadableString(new KeyCombination(InputKey.Semicolon)), Is.EqualTo("phys:Semicolon"));
        }

        [Test]
        public void ALayoutChangeRefreshesKeyNames()
        {
            var layout = new Bindable<KeyboardLayout>(KeyboardLayout.Qwerty);
            var provider = new KeycapKeyCombinationProvider(new PhysicalNamingProvider(), layout);
            int refreshes = 0;
            provider.KeymapChanged += () => refreshes++;

            layout.Value = KeyboardLayout.Azerty;

            Assert.That(refreshes, Is.EqualTo(1));
        }

        private static Key deliver(KeycapKeyRewriter rewriter, Key physical)
        {
            var pressed = rewrite(rewriter, new KeyboardKeyInput(physical, true)).Entries.Single();
            rewrite(rewriter, new KeyboardKeyInput(physical, false));
            return pressed.Button;
        }

        private static KeyboardKeyInput rewrite(KeycapKeyRewriter rewriter, KeyboardKeyInput input)
        {
            var inputs = new List<IInput> { input };
            rewriter.Rewrite(inputs);
            return (KeyboardKeyInput)inputs.Single();
        }

        /// <summary>Stands in for the host's OS-layout-aware provider, naming the physical key it is asked about.</summary>
        private class PhysicalNamingProvider : ReadableKeyCombinationProvider
        {
            protected override string GetReadableKey(InputKey key) => $"phys:{key}";
        }

        /// <summary>Stands in for the host's provider on an OS whose keymap puts the named letters on the named physical positions; every other key is named after itself.</summary>
        private class OsLayoutProvider : ReadableKeyCombinationProvider
        {
            private Dictionary<InputKey, string> names = new Dictionary<InputKey, string>();

            public OsLayoutProvider(params (string position, string name)[] moved) => Set(moved);

            public void Set(params (string position, string name)[] moved) => names = moved.ToDictionary(m => Enum.Parse<InputKey>(m.position), m => m.name);

            protected override string GetReadableKey(InputKey key) => names.TryGetValue(key, out string? name) ? name : key.ToString();
        }
    }
}
