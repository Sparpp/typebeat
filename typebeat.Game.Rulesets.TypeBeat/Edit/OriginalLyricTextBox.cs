// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>Convert Japanese punctuation on committed input, leaving IME composition alone.</summary>
    public partial class OriginalLyricTextBox : OsuTextBox
    {
        public Func<bool> IsJapaneseOriginal { get; init; } = () => false;

        protected override void OnUserTextAdded(string added)
        {
            base.OnUserTextAdded(added);
            if (!ImeCompositionActive)
                convertInserted(added);
        }

        protected override void OnImeResult(string result, bool successful)
        {
            base.OnImeResult(result, successful);
            if (!successful || !IsJapaneseOriginal())
                return;
            // The framework is still clearing its composition bookkeeping in this callback.
            // Defer the replacement until that cleanup completes.
            string committed = Text;
            Schedule(() =>
            {
                if (Text == committed && !ImeCompositionActive)
                    convertInserted(result);
            });
        }

        private void convertInserted(string added)
        {
            if (!IsJapaneseOriginal() || added.Length == 0)
                return;
            string normalized = TypeBeatEditorOperations.JapaneseOriginalPunctuation(added);
            if (normalized == added)
                return;
            // Each replacement has the same UTF-16 length. Replace only the committed run,
            // preserving the caret when editing punctuation in the middle of a lyric.
            MoveCursorBy(-added.Length);
            ExpandSelectionBy(added.Length);
            InsertString(normalized);
        }
    }
}
