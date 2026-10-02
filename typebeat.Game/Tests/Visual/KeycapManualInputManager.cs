// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Input;
using osu.Framework.Input.StateChanges;
using osu.Framework.Testing.Input;
using typebeat.Game.Configuration;
using typebeat.Game.Input;

namespace typebeat.Game.Tests.Visual
{
    /// <summary>
    /// The test scenes' manual input manager, given the same keycap rewrite the game's root input
    /// manager applies (<see cref="OsuUserInputManager"/>), so a test pressing a PHYSICAL key sees what
    /// a player pressing it would. Test input enters here rather than at the root, which is why the
    /// rewrite has to be repeated here; it reads the very same <see cref="KeycapKeyRewriter"/>. Under
    /// the default QWERTY layout the rewrite is the identity, so a scene that never sets a layout is
    /// untouched.
    ///
    /// <para>It also stands in for the OS's TEXT INPUT (backlog 383), because gameplay typing now
    /// reads the characters the OS commits rather than the keys: <see cref="TextInput"/> is cached
    /// for everything below this manager in place of the headless host's source (which never
    /// commits anything), and every press of a character key commits what a US layout would commit
    /// for it, in the same frame and after the key, the order SDL's event pump delivers the two in.
    /// So a scene that presses Key.A still types 'a'. A test that needs a different commit for a
    /// press (a dead key, an accented letter, AltGr) queues it with
    /// <see cref="EmulatedTextInput.CommitOnNextPress"/>, and an OS key repeat is a bare
    /// <see cref="ManualTextInputSource.Text"/> with no press behind it.</para>
    /// </summary>
    public partial class KeycapManualInputManager : ManualInputManager
    {
        private readonly KeycapKeyRewriter keycaps = new KeycapKeyRewriter();

        /// <summary>The emulated OS text source, cached as the <see cref="TextInputSource"/> for this manager's subtree.</summary>
        public readonly EmulatedTextInput TextInput = new EmulatedTextInput();

        [BackgroundDependencyLoader(true)]
        private void load(OsuConfigManager? config)
        {
            if (config != null)
                config.BindWith(OsuSetting.KeyboardLayout, keycaps.Layout);
        }

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
            dependencies.CacheAs<TextInputSource>(TextInput);
            return dependencies;
        }

        protected override List<IInput> GetPendingInputs()
        {
            var inputs = base.GetPendingInputs();

            // Parent input already passed through the root's rewrite, and its text (if any) comes
            // from the real host.
            if (!UseParentInput)
            {
                TextInput.CommitFor(inputs, CurrentState.Keyboard.Keys, keycaps.Layout.Value);
                keycaps.Rewrite(inputs);
            }

            return inputs;
        }
    }
}
