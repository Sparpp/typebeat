// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// TypingEngine, THE KEY ANNOUNCEMENT: one event per gameplay key the player presses, for feedback
// that follows the keyboard rather than the judgement (the keyboard hitsounds).

using System;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>Which of the engine's three key inputs a <see cref="TypedKey"/> is.</summary>
    public enum TypedKeyKind
    {
        /// <summary>A character key (<see cref="TypingEngine.ProcessKey"/>): a letter, digit, punctuation or the space bar.</summary>
        Character,

        /// <summary>Backspace (<see cref="TypingEngine.ProcessBackspace"/>).</summary>
        Backspace,

        /// <summary>Enter (<see cref="TypingEngine.ProcessEnter"/>).</summary>
        Enter,
    }

    /// <summary>A gameplay key as the engine received it; <see cref="Character"/> is set for <see cref="TypedKeyKind.Character"/> only.</summary>
    public readonly record struct TypedKey(TypedKeyKind Kind, char Character = '\0');

    public sealed partial class TypingEngine
    {
        /// <summary>
        /// Raised once for every gameplay key, before it is judged and whatever it turns out to do
        /// (typed, rejected, ignored): the key is what was pressed, live or from a replay's frames.
        ///
        /// <para>ONCE PER KEY. The Japanese input path calls <see cref="ProcessKey"/> again from inside a
        /// key (a romaji reading committing its kana), so only the OUTERMOST call announces. And not at
        /// all inside <see cref="Rebuild"/>, which re-feeds a whole run at once and is silent for the
        /// same reason every other event is.</para>
        ///
        /// <para>Feedback only: nothing in scoring or judgement reads it.</para>
        /// </summary>
        public event Action<TypedKey>? KeyTyped;

        /// <summary>How many key inputs are running right now; the announcement belongs to the outermost.</summary>
        private int keyInputDepth;

        /// <summary>Whether a key input is being processed right now: an event raised while this holds belongs to the last <see cref="KeyTyped"/>.</summary>
        public bool InKeyInput => keyInputDepth > 0;

        /// <summary>Opens one key input, announcing it when it is the outermost; dispose to close it.</summary>
        private KeyInputScope beginKeyInput(TypedKey key)
        {
            if (keyInputDepth++ == 0 && !rebuilding)
                KeyTyped?.Invoke(key);

            return new KeyInputScope(this);
        }

        private readonly struct KeyInputScope : IDisposable
        {
            private readonly TypingEngine engine;

            public KeyInputScope(TypingEngine engine)
            {
                this.engine = engine;
            }

            public void Dispose() => engine.keyInputDepth--;
        }
    }
}
