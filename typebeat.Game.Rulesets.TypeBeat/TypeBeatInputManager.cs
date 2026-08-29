// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.ComponentModel;
using osu.Framework.Extensions;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat
{
    public partial class TypeBeatInputManager : RulesetInputManager<TypeBeatAction>
    {
        private GestureKeyBindingContainer gestureBindings = null!;

        public TypeBeatInputManager(RulesetInfo ruleset)
            : base(ruleset, 0, SimultaneousBindingMode.Unique)
        {
        }

        protected override KeyBindingContainer<TypeBeatAction> CreateKeyBindingContainer(RulesetInfo ruleset, int variant, SimultaneousBindingMode unique)
            => gestureBindings = new GestureKeyBindingContainer(ruleset, variant, unique);

        /// <summary>
        /// The bindings gameplay input must obey right now. Re-read on every keystroke rather than
        /// captured, because a rebind REPLACES the container's list rather than mutating it.
        /// </summary>
        public IEnumerable<IKeyBinding> CurrentGestureBindings => gestureBindings.CurrentKeyBindings;

        /// <summary>
        /// The gameplay key counter counts INPUT KEYS, so it keeps showing only the two button
        /// actions. The word-level gestures are editing commands, not keys being played, and they are
        /// resolved on the raw typing path rather than pressed as actions (see
        /// <see cref="ResolveGesture"/>), so a counter for them could only ever read zero.
        /// </summary>
        protected override IEnumerable<TypeBeatAction> CountedActions => new[] { TypeBeatAction.Button1, TypeBeatAction.Button2 };

        /// <summary>
        /// Which TYPING GESTURE (if any) the given key press newly satisfies under the user's
        /// CURRENT bindings, or null if it satisfies none: the two word-level ones (backlog 182) and
        /// the line skip (backlog 241).
        ///
        /// <para>The gestures are ordinary rebindable ruleset actions (backlog 183), but they are
        /// resolved HERE rather than delivered as <see cref="IKeyBindingHandler{T}"/> presses,
        /// because gameplay typing is taken from raw key events in
        /// <c>TypeBeatPlayfield.TypeBeatKeyHandler</c>, which sits BELOW this manager's key-binding
        /// container in the input queue and therefore runs first. Letting the container own the
        /// gestures would mean either it never sees a chord the typing path consumed (so a rebind
        /// onto a key the typing path owns would be silently dead in one direction) or it fires
        /// before typing (so a rebind onto a letter would kill that letter). One owner, one
        /// precedence order, written out in the key handler: see its <c>OnKeyDown</c>.</para>
        ///
        /// <para>The bindings themselves are still entirely the framework's: this reads the very
        /// list the container resolves actions from (realm-backed and live-updating through
        /// <c>DatabasedKeyBindingContainer</c>), matched with the framework's own
        /// <c>KeyCombination.IsPressed</c> under the container's own matching mode. So the
        /// combination shown in the key configuration screen is by construction the combination
        /// gameplay acts on, and no action is ever pressed, which is what keeps the replay recorder
        /// (an <see cref="IKeyBindingHandler{T}"/> on the same container) unable to see the
        /// gestures at all.</para>
        /// </summary>
        public static TypeBeatAction? ResolveGesture(KeyDownEvent e, IEnumerable<IKeyBinding> bindings)
        {
            var pressed = KeyCombination.FromInputState(e.CurrentState);
            var newKey = KeyCombination.FromKey(e.Key);

            TypeBeatAction? match = null;
            int matchWidth = 0;

            foreach (var binding in bindings)
            {
                var action = binding.GetAction<TypeBeatAction>();

                if (action != TypeBeatAction.EraseWord && action != TypeBeatAction.SelectBackToTypo && action != TypeBeatAction.SkipLine)
                    continue;

                // The binding has to be satisfied BY THIS PRESS, not merely satisfied: with the
                // container's permissive matching an already-held Ctrl+A would otherwise re-fire on
                // every further key pressed while it is down.
                if (!combinationContains(binding.KeyCombination, newKey))
                    continue;

                if (!binding.KeyCombination.IsPressed(pressed, e.CurrentState, GestureKeyBindingContainer.MATCHING_MODE))
                    continue;

                // Widest combination wins, exactly as the container's own newlyPressed ordering does,
                // so a user who binds one gesture to Backspace and the other to Ctrl+Backspace gets
                // the more specific of the two.
                if (match == null || binding.KeyCombination.Keys.Length > matchWidth)
                {
                    match = action;
                    matchWidth = binding.KeyCombination.Keys.Length;
                }
            }

            return match;
        }

        /// <summary>
        /// Whether <paramref name="key"/> is one of <paramref name="combination"/>'s keys, honouring
        /// the framework's virtual modifiers: a binding stored as <see cref="InputKey.Control"/> is
        /// satisfied by either physical control key. Mirrors the container's own (internal)
        /// <c>KeyCombination.KeyBindingContains</c>.
        /// </summary>
        private static bool combinationContains(KeyCombination combination, InputKey key)
        {
            if (combination.Keys.Contains(key))
                return true;

            return key.GetVirtualKey() is InputKey virtualKey && combination.Keys.Contains(virtualKey);
        }

        /// <summary>
        /// The ruleset key-binding container, with the resolved binding list exposed so the typing
        /// path can consult it (see <see cref="ResolveGesture"/>). Adds nothing else: the gameplay
        /// filtering and duplicate clearing stay exactly the base class's.
        /// </summary>
        public partial class GestureKeyBindingContainer : RulesetKeyBindingContainer
        {
            /// <summary>
            /// The matching mode this container resolves bindings under. Pinned here because the
            /// framework keeps its own copy private; it is the default
            /// <c>DatabasedKeyBindingContainer</c> is constructed with, and
            /// <see cref="ResolveGesture"/> must use the same one or a chord could light up the
            /// settings row and do nothing in game (or the reverse).
            /// </summary>
            public const KeyCombinationMatchingMode MATCHING_MODE = KeyCombinationMatchingMode.Any;

            public GestureKeyBindingContainer(RulesetInfo ruleset, int variant, SimultaneousBindingMode unique)
                : base(ruleset, variant, unique)
            {
            }

            /// <summary>
            /// The bindings currently in force: the user's realm rows once loaded (re-read on every
            /// rebind through the container's realm subscription), the ruleset defaults before that.
            /// </summary>
            public IEnumerable<IKeyBinding> CurrentKeyBindings => KeyBindings ?? DefaultKeyBindings;
        }
    }

    public enum TypeBeatAction
    {
        [Description("Button 1")]
        Button1,

        [Description("Button 2")]
        Button2,

        /// <summary>
        /// Erase the word behind the caret (backlog 182, rebindable since backlog 183). Default
        /// Ctrl+Backspace, the chord every other typing site uses for it.
        /// </summary>
        [Description("Erase word")]
        EraseWord,

        /// <summary>
        /// Offer the run from the caret back to the nearest unfixed typo for retyping (backlog 182,
        /// rebindable since backlog 183). Default Ctrl+A.
        /// </summary>
        [Description("Select back to typo")]
        SelectBackToTypo,

        /// <summary>
        /// Give up the rest of the current line and move on to the next one (backlog 241). Default
        /// Enter, and the numeric keypad's Enter with it, the two keys that mean "next" on a
        /// keyboard. Unlike the two gestures above this one is not a chord, which it can afford to
        /// be: Enter types nothing, so the typing-wins rule never shadows it.
        /// </summary>
        [Description("Skip line")]
        SkipLine,
    }
}
