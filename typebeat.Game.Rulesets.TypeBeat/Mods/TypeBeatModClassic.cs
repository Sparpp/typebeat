// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// The synthetic "CL" (Classic) SYSTEM MARK (backlog 398). A score carries it when the server
    /// judged that the version of the map it was played on is NOT the version that was ranked: the
    /// play stays on the board, but it is marked and paid 5 percent less (0.95x score and pp). The
    /// server appends the acronym to a stored score's mods; the client never offers it.
    ///
    /// <para>WHY IT MUST EXIST HERE AT ALL, even though no player can select it: everything a stored
    /// score flows through resolves its acronym against the ruleset's mod list. Drop it and every
    /// marked row's badge resolves to <c>UnknownMod</c> (a blank name and a question-mark icon)
    /// instead of "Classic", exactly the failure <see cref="TypeBeatModLegacyFletcher"/> and
    /// <see cref="TypeBeatModConductor"/> exist to prevent for "FT" and "CT". The icon is the same
    /// <see cref="OsuIcon.ModClassic"/> osu's own Classic badge uses, so the mark reads identically
    /// in both places.</para>
    ///
    /// <para>IT DERIVES FROM <see cref="Mod"/> RATHER THAN THE BASE <c>ModClassic</c>, deliberately:
    /// that base seals <c>Ranked => false</c>, and a Classic-marked play IS ranked here (the server
    /// keeps its <c>ranked</c> flag), so <c>Ruleset.ScoreEarnsPerformancePoints</c> must not read the
    /// mark and refuse the play's pp. The 0.95x is carried by
    /// <c>PerformancePoints.ModMultiplier</c>'s "CL" arm and by the server's score-side
    /// <c>ModMultiplier</c>, not by the ranked gate. Only the name, acronym, icon and description
    /// are inherited in spirit from osu's Classic; the ranked contract is this fork's own.</para>
    ///
    /// <para><see cref="ModType.System"/> is what makes it unselectable without making it
    /// unresolvable: <c>Ruleset.CreateAllMods</c> walks every <see cref="ModType"/>, so the acronym
    /// resolves, while <c>ModSelectOverlay</c> builds columns for the five player-facing types only
    /// and marks every System mod invalid for selection. <see cref="UserPlayable"/> is false for the
    /// same reason it is on <c>UnknownMod</c>: this is a record of how the server judged a stored
    /// play, not a thing anyone can put on a new one.</para>
    ///
    /// <para>It declares no <c>ScoreMultiplier</c> of its own: the client prices a finished score's
    /// score-side multiplier from <c>TypeBeatScoreMultiplierCalculator</c>, and there is no live
    /// score to price (the server already priced and stored the marked total). The pp side is the
    /// one the client re-derives, and it reads the acronym directly.</para>
    /// </summary>
    public class TypeBeatModClassic : Mod
    {
        public override string Name => "Classic";

        public override string Acronym => "CL";

        public override IconUsage? Icon => OsuIcon.ModClassic;

        public override LocalisableString Description => "Played on a version of the map that is not the ranked one.";

        public override ModType Type => ModType.System;

        public override bool UserPlayable => false;

        /// <summary>
        /// Ranked: a Classic-marked play stays on the leaderboard, it is only paid 5 percent less.
        /// See the class note.
        /// </summary>
        public override bool Ranked => true;
    }
}
