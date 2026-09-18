// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Easy: every judgement window is twice as wide, so every character gives you twice as long to
    /// hit it. The ladder keeps its shape (the one symmetric ladder is scaled by the same factor on
    /// every rung), and WHICH cell is judged changes not at all: there is one ladder for every cell
    /// of every map, so a syllable-subdivided map is judged exactly as tightly as a coarse one, at
    /// double the tolerance both of them had.
    ///
    /// <para>THE SHELTER follows the same bargain one level up (the user's decision): a cell is
    /// judged against its whole WORD's span rather than its own syllable's
    /// (<see cref="Gameplay.TypingEngine.WordShelter"/>), so a press anywhere inside the word is
    /// dead on and only the distance past the word's own edges counts. Easy therefore widens BOTH
    /// bounds of the challenge -- twice the window, and a span that covers the whole word the
    /// syllable sits in -- and the two compose exactly as they do without the mod, since the
    /// narrowing flags keep narrowing.</para>
    ///
    /// <para>Implemented by MULTIPLYING one engine number
    /// (<see cref="Gameplay.TypingEngine.WindowScale"/>) rather than by assigning it, the same
    /// single-flag pattern Literate, Mashing and Fletcher use. The scale is general on purpose: it
    /// is not an "easy" flag, so any other mod that widens or tightens the windows multiplies its
    /// own factor in and the two compose, in either application order. Judgement is otherwise
    /// untouched: the cells, their target times, the Great/Ok/Meh names and the points each tier
    /// pays are exactly as they are without the mod.</para>
    ///
    /// <para>The scale reaches the two sync readouts as well as the two <c>Classify</c> calls, which
    /// is correct rather than an oversight: <c>SyncQuality</c> measures a delta against the widest
    /// window, so a wider window really is easier to sit inside, and it would be incoherent to grade
    /// a press Great while the sync readout scored it as though the window were half as wide.</para>
    /// </summary>
    public class TypeBeatModEasy : ModEasy, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        /// <summary>
        /// What the mod multiplies every judgement window by. Read by
        /// <see cref="Scoring.TypeBeatReplayScorer"/> too, so a replay is re-judged on the same
        /// ladder the live run was, alongside <see cref="Gameplay.TypingEngine.WordShelter"/> for
        /// the span it measures a press against.
        /// </summary>
        public const double WINDOW_SCALE = 2.0;

        public override LocalisableString Description => "Twice as long to hit every character.";

        // The real multiplier is defined in TypeBeatScoreMultiplierCalculator (the authoritative,
        // non-obsolete path osu now uses). This obsolete override is kept only so the mod also
        // self-reports 0.5x for any legacy reader.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 0.5;
#pragma warning restore CS0672

        /// <summary>
        /// Nothing in this ruleset conflicts with a wider window today, so the list is narrowed to
        /// the one mod that would: an eventual Hard Rock, which tightens the same windows. Both
        /// entries osu's <see cref="ModEasy"/> declares are dropped rather than inherited.
        /// <see cref="ModDifficultyAdjust"/> moves CircleSize / ApproachRate / DrainRate, none of
        /// which a typing game has (see <see cref="ApplyToDifficulty"/>), so no type!beat mod can
        /// ever derive from it. <see cref="ModHardRock"/> is kept as the SEAM: no type!beat mod
        /// derives from it yet either, so the entry is inert until one does, at which point Easy
        /// and Hard Rock exclude each other from both sides without this file being reopened.
        /// </summary>
        public override Type[] IncompatibleMods => new[] { typeof(ModHardRock) };

        /// <summary>
        /// Overridden AWAY, not extended. osu's Easy halves CircleSize, ApproachRate and DrainRate;
        /// type!beat has no circles, no approach and no drain, so inheriting that would move three
        /// numbers nothing reads while the thing the mod is actually for (the judgement windows)
        /// went untouched.
        /// </summary>
        public override void ApplyToDifficulty(BeatmapDifficulty difficulty)
        {
        }

        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset)
        {
            var engine = ((DrawableTypeBeatRuleset)drawableRuleset).Engine;
            engine.WindowScale *= WINDOW_SCALE;
            // Assignment and not a multiply: the shelter is a boolean reading of the span rule, so
            // applying the mod twice cannot compound it (unlike WindowScale, which multiplies).
            engine.WordShelter = true;
        }
    }
}
