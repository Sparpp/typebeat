// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// WHEN the keyboard hitsounds sound (<see cref="KeyboardHitsounds"/> decides how): once per SYLLABLE,
    /// on the key that types its first character, and on every hold tick the player is holding through.
    /// Every other key (the rest of a syllable, the space bar between words, a rejected key, Backspace,
    /// Enter) is silent.
    ///
    /// <para>A syllable is the live grouping the engine plays on (<see cref="TypingLine.GroupingFor"/>),
    /// so a word the mapper did not subdivide is one syllable and sounds once. A word in no syllable
    /// group (a stylised spelling such as "ohhh") sounds on its first character, like an undivided word.</para>
    ///
    /// <para>Pure engine listening, no audio: <see cref="Cued"/> names the sample to play. The key's own
    /// sample (<see cref="KeyboardHitsounds.SampleFor"/>) for a syllable start; the held character's for a
    /// hold tick. Engine events are silent inside a rebuild, so a replay seek stays silent.</para>
    /// </summary>
    public sealed class KeyboardHitsoundCues : IDisposable
    {
        private readonly TypingEngine engine;

        /// <summary>The character key now being processed, or null for Backspace and Enter.</summary>
        private TypedKey? currentKey;

        /// <summary>Whether the current key has already sounded (one cue per key at most).</summary>
        private bool currentKeyCued;

        /// <summary>A sample to play now.</summary>
        public event Action<string>? Cued;

        public KeyboardHitsoundCues(TypingEngine engine)
        {
            this.engine = engine;
            engine.KeyTyped += onKeyTyped;
            engine.CharJudged += onCharJudged;
            engine.HoldTickJudged += onHoldTickJudged;
        }

        /// <summary>
        /// Whether <paramref name="cellIndex"/> is the first character of a syllable of <paramref name="line"/>
        /// under the grouping the engine plays on. Pure.
        /// </summary>
        public static bool StartsSyllable(TypingLine line, int cellIndex, bool authoredSyllablesOnly)
        {
            var grouping = line.GroupingFor(authoredSyllablesOnly);
            int group = grouping.IndexOf(cellIndex);

            if (group >= 0)
                return grouping.Groups[group].StartCell == cellIndex;

            // In no syllable: a space, isolated punctuation, or a word the syllabifier leaves alone.
            // Only the last of those is a sung word, and it sounds on its first character.
            foreach (var word in line.Words)
            {
                if (word.StartCell == cellIndex)
                    return true;
            }

            return false;
        }

        private void onKeyTyped(TypedKey key)
        {
            currentKey = key.Kind == TypedKeyKind.Character ? key : null;
            currentKeyCued = false;
        }

        private void onCharJudged(CharJudgement judgement)
        {
            // Only the press's own judgement: not a hold's finish (phase 1), a retype's upgrade, a wrong
            // character typed into the cell, or the cells a word skip abandons.
            if (!engine.InKeyInput || currentKey is not TypedKey key || currentKeyCued)
                return;

            if (judgement.Phase != 0 || judgement.Upgraded || judgement.Type is JudgementType.WrongChar or JudgementType.Abandoned)
                return;

            if (!StartsSyllable(engine.Lines[judgement.LineIndex], judgement.CellIndex, engine.AuthoredSyllablesOnly))
                return;

            currentKeyCued = true;
            Cued?.Invoke(KeyboardHitsounds.SampleFor(key));
        }

        private void onHoldTickJudged(HoldTickJudgement tick)
        {
            if (!tick.Hit || tick.Neutral)
                return;

            char held = engine.Lines[tick.LineIndex].Cells[tick.CellIndex].Expected;
            Cued?.Invoke(KeyboardHitsounds.SampleFor(new TypedKey(TypedKeyKind.Character, held)));
        }

        public void Dispose()
        {
            engine.KeyTyped -= onKeyTyped;
            engine.CharJudged -= onCharJudged;
            engine.HoldTickJudged -= onHoldTickJudged;
        }
    }
}
