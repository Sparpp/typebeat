// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Testing;
using typebeat.Game.Input;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 371 through the framework's own key-binding resolution: the platform actions (undo,
    /// copy, select all, ...) are bound by the framework to <see cref="InputKey"/>s and matched against
    /// whatever key the input manager reports, so they follow the KEYCAP exactly when the key is
    /// rewritten before matching. Driven as real key presses through the scene's input manager, which
    /// applies the same <see cref="KeycapKeyRewriter"/> the game's root input manager does; the root's
    /// own wiring to the OS-detected layout (<see cref="OsKeyboardLayout"/>, backlog 383) is pinned
    /// separately.
    /// </summary>
    public partial class TestSceneKeycapShortcuts : OsuManualInputManagerTestScene
    {
        [Resolved]
        private OsKeyboardLayout osLayout { get; set; } = null!;

        private PlatformActionRecorder recorder = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("QWERTY keycaps", () => osLayout.Current.Value = KeyboardLayout.Qwerty);
            AddStep("add an action recorder", () => Child = recorder = new PlatformActionRecorder());
        }

        [TearDownSteps]
        public void TearDownSteps() => AddStep("QWERTY keycaps", () => osLayout.Current.Value = KeyboardLayout.Qwerty);

        [Test]
        public void TestUndoIsCtrlZOnQwerty()
        {
            ctrl(Key.Z);
            AddAssert("undo fired", () => recorder.Pressed, () => Is.EqualTo(new[] { PlatformAction.Undo }));
        }

        /// <summary>
        /// On AZERTY the Z keycap sits on QWERTY's W position: Ctrl plus that key is undo, and Ctrl
        /// plus the physical Z position (the W keycap) is not, exactly as in every other application.
        /// </summary>
        [Test]
        public void TestUndoFollowsTheZKeycapOnAzerty()
        {
            AddStep("AZERTY keycaps", () => osLayout.Current.Value = KeyboardLayout.Azerty);

            ctrl(Key.Z);
            AddAssert("Ctrl plus the physical Z key (the W keycap) is not undo", () => !recorder.Pressed.Contains(PlatformAction.Undo));

            ctrl(Key.W);
            AddAssert("Ctrl plus the physical W key (the Z keycap) is undo", () => recorder.Pressed.Count(a => a == PlatformAction.Undo) == 1);

            // Select all is the A keycap, which is QWERTY's Q position.
            ctrl(Key.Q);
            AddAssert("Ctrl plus the A keycap selects all", () => recorder.Pressed.Contains(PlatformAction.SelectAll));
        }

        [Test]
        public void TestUndoFollowsTheZKeycapOnQwertz()
        {
            AddStep("QWERTZ keycaps", () => osLayout.Current.Value = KeyboardLayout.Qwertz);

            ctrl(Key.Y);
            AddAssert("Ctrl plus the physical Y key (the Z keycap) is undo", () => recorder.Pressed.Contains(PlatformAction.Undo));
        }

        /// <summary>A key held across a layout change is released as the key it was pressed as.</summary>
        [Test]
        public void TestAKeyHeldAcrossALayoutChangeIsReleased()
        {
            AddStep("AZERTY keycaps", () => osLayout.Current.Value = KeyboardLayout.Azerty);
            AddStep("hold the physical W key", () => InputManager.PressKey(Key.W));
            AddAssert("held as the Z keycap", () => InputManager.CurrentState.Keyboard.Keys.IsPressed(Key.Z));

            AddStep("QWERTY keycaps", () => osLayout.Current.Value = KeyboardLayout.Qwerty);
            AddStep("release the physical W key", () => InputManager.ReleaseKey(Key.W));
            AddAssert("nothing is left held", () => !InputManager.CurrentState.Keyboard.Keys.HasAnyButtonPressed);
        }

        /// <summary>
        /// The GAME's root input manager is the one real input enters through, and it is bound to the
        /// layout the OS keymap describes (a setting before backlog 383): the test runner is an
        /// <see cref="OsuGameBase"/>, so the root here is the very
        /// <see cref="OsuUserInputManager"/> a player's keys pass through.
        /// </summary>
        [Test]
        public void TestTheRootInputManagerFollowsTheDetectedLayout()
        {
            OsuUserInputManager root = null!;

            AddStep("find the root input manager", () =>
            {
                Drawable drawable = this;

                while (drawable.Parent != null)
                    drawable = drawable.Parent;

                root = (OsuUserInputManager)drawable;
            });

            AddStep("AZERTY keycaps", () => osLayout.Current.Value = KeyboardLayout.Azerty);
            AddAssert("the root rewrites for AZERTY", () => root.KeyboardLayout.Value, () => Is.EqualTo(KeyboardLayout.Azerty));

            AddStep("QWERTZ keycaps", () => osLayout.Current.Value = KeyboardLayout.Qwertz);
            AddAssert("the root rewrites for QWERTZ", () => root.KeyboardLayout.Value, () => Is.EqualTo(KeyboardLayout.Qwertz));
        }

        private void ctrl(Key key) => AddStep($"press Ctrl+{key} (physical)", () =>
        {
            InputManager.PressKey(Key.ControlLeft);
            InputManager.Key(key);
            InputManager.ReleaseKey(Key.ControlLeft);
        });

        private partial class PlatformActionRecorder : Drawable, IKeyBindingHandler<PlatformAction>
        {
            public readonly List<PlatformAction> Pressed = new List<PlatformAction>();

            public PlatformActionRecorder()
            {
                RelativeSizeAxes = Axes.Both;
            }

            public bool OnPressed(KeyBindingPressEvent<PlatformAction> e)
            {
                Pressed.Add(e.Action);
                return true;
            }

            public void OnReleased(KeyBindingReleaseEvent<PlatformAction> e)
            {
            }
        }
    }
}
