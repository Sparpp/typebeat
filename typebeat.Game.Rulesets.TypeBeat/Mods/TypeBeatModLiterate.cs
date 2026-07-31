// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Literate: the lyric is typed EXACTLY as its author wrote it, capitals and punctuation
    /// included.
    ///
    /// <list type="bullet">
    /// <item>CASE. Normally typing is case-insensitive (the caret folds both the key and the target
    /// to lower case before matching); with this mod on, a letter must be typed in the target's
    /// exact case, and a right letter in the wrong case is judged wrong like any other wrong char.
    /// The key handler already forwards Shift, so held-Shift keys produce the capitals the target
    /// demands.</item>
    /// <item>PUNCTUATION. A map stores the author's punctuated text; without the mod the game
    /// derives the stripped, lower-case stream the player actually types (and shows exactly that,
    /// see <see cref="Beatmaps.Typeability.ToDefaultStream"/>). With the mod on, the cells ARE the
    /// authored chars, so every supported mark becomes a real typed cell with its own target time,
    /// and a hyphen is a hyphen again rather than the word break the default stream turns it into
    /// ("The bad-cat sat." instead of "the bad cat sat").</item>
    /// </list>
    ///
    /// <para>Unlike the other mods this one cannot be a flag flipped on a built engine: it changes
    /// the CELL LIST. It is applied in the one window where that is safe, after beatmap conversion
    /// and before ApplyDefaults, by stamping every line object
    /// (<see cref="TypeBeatHitObject.Literate"/>) so the nested per-cell scoring objects flatten
    /// the same way the engine does; the engine itself reads the mod off the drawable ruleset's
    /// mod list (see <c>DrawableTypeBeatRuleset.createEngine</c>).</para>
    /// </summary>
    public class TypeBeatModLiterate : Mod, IApplicableAfterBeatmapConversion
    {
        public override string Name => "Literate";

        public override string Acronym => "LT";

        public override LocalisableString Description => "Case and punctuation matter: type the lyric exactly as written.";

        public override ModType Type => ModType.DifficultyIncrease;

        // The real multiplier is defined in TypeBeatScoreMultiplierCalculator (the authoritative,
        // non-obsolete path osu now uses). This obsolete override is kept only so the mod also
        // self-reports 1.05x for any legacy reader.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 1.05;
#pragma warning restore CS0672

        public override bool Ranked => true;

        public void ApplyToBeatmap(IBeatmap beatmap)
        {
            foreach (var line in beatmap.HitObjects.OfType<TypeBeatHitObject>())
                line.Literate = true;
        }
    }
}
