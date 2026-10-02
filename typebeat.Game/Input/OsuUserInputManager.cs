// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Input.StateChanges;
using typebeat.Game.Screens.Play;
using osuTK.Input;

namespace typebeat.Game.Input
{
    public partial class OsuUserInputManager : UserInputManager
    {
        protected override bool AllowRightClickFromLongTouch => PlayingState.Value != LocalUserPlayingState.Playing;

        public readonly IBindable<LocalUserPlayingState> PlayingState = new Bindable<LocalUserPlayingState>();

        /// <summary>
        /// The layout the player's keycaps follow, bound by <see cref="OsuGameBase"/> to the one the
        /// OS's keyboard map describes (<see cref="OsKeyboardLayout"/>). Every keyboard key is rewritten from
        /// its physical position to its keycap key here, at the root, before any key-binding
        /// container (the framework's platform actions included, which sit directly below this
        /// manager) or <c>OnKeyDown</c> handler sees it, so every shortcut follows the keycap.
        /// </summary>
        public Bindable<KeyboardLayout> KeyboardLayout => keycaps.Layout;

        private readonly KeycapKeyRewriter keycaps = new KeycapKeyRewriter();

        internal OsuUserInputManager()
        {
        }

        protected override List<IInput> GetPendingInputs()
        {
            var inputs = base.GetPendingInputs();
            keycaps.Rewrite(inputs);
            return inputs;
        }

        protected override MouseButtonEventManager CreateButtonEventManagerFor(MouseButton button)
        {
            switch (button)
            {
                case MouseButton.Right:
                    return new RightMouseManager(button);
            }

            return base.CreateButtonEventManagerFor(button);
        }

        private class RightMouseManager : MouseButtonEventManager
        {
            public RightMouseManager(MouseButton button)
                : base(button)
            {
            }

            public override bool EnableDrag => true; // allow right-mouse dragging for absolute scroll in scroll containers.
            public override bool EnableClick => false;
            public override bool ChangeFocusOnClick => false;
        }
    }
}
