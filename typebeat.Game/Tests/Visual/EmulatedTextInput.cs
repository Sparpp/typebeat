// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Input;
using osu.Framework.Input.StateChanges;
using osu.Framework.Input.States;
using osu.Framework.Testing.Input;
using osuTK.Input;
using typebeat.Game.Input;

namespace typebeat.Game.Tests.Visual
{
    /// <summary>
    /// A text source that commits what a US QWERTY layout commits for each key press handed to
    /// it, but only while something has it activated, as SDL only sends text input between
    /// <c>SDL_StartTextInput</c> and <c>SDL_StopTextInput</c>.
    /// </summary>
    public class EmulatedTextInput : ManualTextInputSource
    {
        /// <summary>Whether a consumer currently has text input activated.</summary>
        public bool IsActive { get; private set; }

        /// <summary>The properties of the latest activation.</summary>
        public TextInputProperties? ActiveProperties { get; private set; }

        /// <summary>The emulated Caps Lock toggle: letters type shift XOR caps, as on a real keyboard.</summary>
        public bool CapsLock { get; set; }

        private readonly Queue<string?> overrides = new Queue<string?>();

        /// <summary>
        /// What the NEXT character-key press commits instead of its US character: a string to
        /// commit (an accented letter, "ß", an AltGr mark), or null for a press that commits
        /// nothing (a dead key). One entry per press, in order.
        /// </summary>
        public void CommitOnNextPress(string? commit) => overrides.Enqueue(commit);

        private readonly Queue<string> lateCommits = new Queue<string>();

        private readonly List<string> dueNextFrame = new List<string>();

        /// <summary>
        /// The NEXT character-key press commits <paramref name="commit"/> one frame LATE: its key is
        /// delivered this frame and its text the next, the cross-frame split the SDL pump can make of
        /// one keystroke when the update thread runs between the two halves.
        /// </summary>
        public void CommitLateOnNextPress(string commit) => lateCommits.Enqueue(commit);

        protected override void ActivateTextInput(TextInputProperties properties)
        {
            base.ActivateTextInput(properties);
            IsActive = true;
            ActiveProperties = properties;
        }

        protected override void EnsureTextInputActivated(TextInputProperties properties)
        {
            base.EnsureTextInputActivated(properties);
            ActiveProperties = properties;
        }

        protected override void DeactivateTextInput()
        {
            base.DeactivateTextInput();
            IsActive = false;
            ActiveProperties = null;
        }

        internal void CommitFor(List<IInput> inputs, ButtonStates<Key> heldBefore, KeyboardLayout layout)
        {
            // Last frame's late halves arrive first, as the earlier events of this pump.
            if (IsActive)
            {
                foreach (string late in dueNextFrame)
                    Text(late);
            }

            dueNextFrame.Clear();

            // Modifiers are followed through the batch itself, so PressKey(Shift) then Key(A) in
            // one step still commits 'A'.
            var held = heldBefore.Clone();

            foreach (var input in inputs)
            {
                if (input is not KeyboardKeyInput keyInput)
                    continue;

                foreach (var entry in keyInput.Entries)
                {
                    bool wasHeld = held.IsPressed(entry.Button);
                    held.SetPressed(entry.Button, entry.IsPressed);

                    // A key already down is no new press (and commits nothing here: OS repeat is
                    // emulated explicitly, by committing text with no press behind it).
                    if (!entry.IsPressed || wasHeld || !IsActive)
                        continue;

                    string? commit = Commit(entry.Button, layout, held.IsPressed(Key.LShift) || held.IsPressed(Key.RShift), CapsLock);

                    if (commit == null)
                        continue;

                    if (lateCommits.Count > 0)
                    {
                        dueNextFrame.Add(lateCommits.Dequeue());
                        continue;
                    }

                    if (overrides.Count > 0)
                        commit = overrides.Dequeue();
                    else if (held.IsPressed(Key.LControl) || held.IsPressed(Key.RControl) || held.IsPressed(Key.LAlt) || held.IsPressed(Key.RAlt)
                             || held.IsPressed(Key.LWin) || held.IsPressed(Key.RWin))
                        continue; // a shortcut chord commits nothing (SDL drops control characters).

                    if (!string.IsNullOrEmpty(commit))
                        Text(commit);
                }
            }
        }

        /// <summary>
        /// What the emulated OS commits for the PHYSICAL <paramref name="key"/> under
        /// <paramref name="layout"/>: the LETTER keys follow the layout (an AZERTY Q position commits
        /// 'a', its M position ','), everything else is the US legend. Enough for every test that
        /// needs a non-US OS; a test needing a particular national character queues it with
        /// <see cref="CommitOnNextPress"/>.
        /// </summary>
        public static string? Commit(Key key, KeyboardLayout layout, bool shift, bool capsLock)
        {
            Key keycap = KeycapLayout.ToKeycap(key, layout);

            if (keycap >= Key.A && keycap <= Key.Z)
                return UsCommit(keycap, shift, capsLock);

            // A letter position whose keycap is no letter: AZERTY's M position, which carries ','.
            if (key >= Key.A && key <= Key.Z)
                return shift ? "?" : ",";

            return UsCommit(key, shift, capsLock);
        }

        /// <summary>What a US QWERTY layout commits for <paramref name="key"/>, or null when the key commits nothing.</summary>
        public static string? UsCommit(Key key, bool shift, bool capsLock)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                char letter = (char)('a' + (key - Key.A));
                return (shift != capsLock ? char.ToUpperInvariant(letter) : letter).ToString();
            }

            if (key >= Key.Number0 && key <= Key.Number9)
                return (shift ? ")!@#$%^&*(" : "0123456789")[key - Key.Number0].ToString();

            if (key >= Key.Keypad0 && key <= Key.Keypad9)
                return ((char)('0' + (key - Key.Keypad0))).ToString();

            return key switch
            {
                Key.Space => " ",
                Key.Tilde => shift ? "~" : "`",
                Key.Minus => shift ? "_" : "-",
                Key.Plus => shift ? "+" : "=",
                Key.BracketLeft => shift ? "{" : "[",
                Key.BracketRight => shift ? "}" : "]",
                Key.BackSlash => shift ? "|" : "\\",
                Key.Semicolon => shift ? ":" : ";",
                Key.Quote => shift ? "\"" : "'",
                Key.Comma => shift ? "<" : ",",
                Key.Period => shift ? ">" : ".",
                Key.Slash => shift ? "?" : "/",
                Key.KeypadDivide => "/",
                Key.KeypadMultiply => "*",
                Key.KeypadMinus => "-",
                Key.KeypadPlus => "+",
                Key.KeypadDecimal => ".",
                _ => null,
            };
        }
    }
}
