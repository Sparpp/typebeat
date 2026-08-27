// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// The RETIRED "FT" mod: Fletcher as it shipped in backlog 25, which unpinned the caret from the
    /// playhead (rush freedom, drag freedom, the character-distance rush cap). Backlog 208 made all
    /// three the default for every play and gave the NAME to the mod that does the opposite
    /// (<see cref="TypeBeatModFletcher"/>, acronym "FC"), so nobody can select this again.
    ///
    /// <para>It stays in the ruleset because SCORES ALREADY EXIST carrying "FT", and everything a
    /// stored score flows through resolves its acronym here: the leaderboard multiplier
    /// (<see cref="Scoring.TypeBeatScoreMultiplierCalculator"/>, still 0.98x, which is what those
    /// rows were priced at), pp (<see cref="Scoring.PerformancePoints"/>, still 0.90x, keyed on the
    /// acronym string), and the headless re-derivation
    /// (<see cref="Scoring.TypeBeatReplayScorer"/>, which reads it as "this run had a flexible caret
    /// and NO line-start snap", the one combination no CONFIG frame bit can express on its own; see
    /// <see cref="Gameplay.TypingEngine.FlexibleCaretFromMod"/>). Drop it and every FT row on the
    /// board resolves to <c>UnknownMod</c>, prices at 1.0x and re-derives on a pinned caret it was
    /// never played with.</para>
    ///
    /// <para><see cref="ModType.System"/> is what makes it unselectable without making it
    /// unresolvable: <c>Ruleset.CreateAllMods</c> walks every <see cref="ModType"/>, so the acronym
    /// still resolves, while <c>ModSelectOverlay</c> builds columns for the five player-facing types
    /// only and marks every System mod invalid for selection.
    /// <see cref="UserPlayable"/> is false for the same reason it is on <c>UnknownMod</c>: this is a
    /// record of how a stored play was configured, not a thing anyone can put on a new one.</para>
    /// </summary>
    public class TypeBeatModLegacyFletcher : Mod
    {
        public override string Name => "Fletcher (retired)";

        public override string Acronym => "FT";

        public override LocalisableString Description => "Were you Rushing or were you Dragging?! (retired: this is how every play works now)";

        public override ModType Type => ModType.System;

        public override bool UserPlayable => false;

        /// <summary>
        /// The stored rows carrying this acronym were submitted as RANKED plays and are on the
        /// shared leaderboards; retiring the mod must not retroactively unrank them.
        /// </summary>
        public override bool Ranked => true;

        // Mirrors the 0.98x TypeBeatScoreMultiplierCalculator still prices the acronym at, for any
        // legacy reader of this obsolete property.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 0.98;
#pragma warning restore CS0672
    }
}
