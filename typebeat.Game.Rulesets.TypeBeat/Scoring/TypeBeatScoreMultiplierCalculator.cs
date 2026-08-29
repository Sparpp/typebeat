// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// Score multipliers for type!beat mods. The flat values mirror osu!'s current (V2) multipliers
    /// so modded scores read consistently with the rest of lazer. Mods not listed here stay at 1.0x
    /// (Sudden Death, Gatekeeper, Muted). Mashing is unranked, but still carries the Relax 0.1x for
    /// display parity.
    ///
    /// <para>
    /// The rate mods are the exception: type!beat ranks them at every speed, so they are paid by
    /// <see cref="TypeBeatRateMultiplier"/> (a continuous, strictly monotonic function of the rate)
    /// rather than by osu's bucketed V2 curve. It agrees with the old curve exactly at the default
    /// speeds, so no default-speed score changes value.
    /// </para>
    /// </summary>
    public class TypeBeatScoreMultiplierCalculator : ScoreMultiplierCalculator
    {
        public TypeBeatScoreMultiplierCalculator(ScoreMultiplierContext context)
            : base(context)
        {
            // Difficulty reduction.
            // Easy at osu's own 0.5x, the value No Fail below already carries: this fork keeps no
            // copy of osu's per-ruleset table (the osu/taiko/catch/mania rulesets are not in it), so
            // the 0.5 that lazer prices both DifficultyReduction mods at is read off NF, which was
            // taken from that table when this class was written and is the only surviving witness
            // to it here.
            Single<TypeBeatModEasy>(hasMultiplier: 0.5);
            Single<TypeBeatModNoFail>(hasMultiplier: 0.5);
            Single<TypeBeatModHalfTime>(hasMultiplier: halfTime => TypeBeatRateMultiplier.For(halfTime.SpeedChange.Value));

            // Difficulty increase.
            // Hard Rock at 1.10x, and the value is NOT read off osu: this fork keeps no copy of
            // lazer's per-mod table (see the Easy note above), and the one surviving witness there
            // to what THIS game pays for a tighter judgement ladder is the retired Rhythmic mod,
            // which tightened the same windows and was priced at 1.10 (backlog 135). Rhythmic is
            // gone from the client but its 1.10 is still carried for stored rows by the server's
            // ModMultiplier and by PerformancePoints, so the number is checkable rather than
            // invented. Hard Rock is the harsher mod of the two, which argues for MORE, and the
            // headroom argues back: the fattest reachable ranked stack becomes
            // DT@2.00 (1.46) x FL (1.05) x LT (1.05) x HR (1.10) = 1.770615, against the server's
            // absolute STACK_CAP of 2.0. At the mod's pp value of 1.25 that product is 2.0121, over
            // the cap, so an honest maximal stack would be clamped and stored UNRANKED. The score
            // multiplier and the pp multiplier are separate numbers (as they are for Easy and No
            // Fail), and this is the one with a ceiling to respect.
            Single<TypeBeatModHardRock>(hasMultiplier: 1.10);
            // Sudden Death (1.0x), Gatekeeper (1.0x): both deliberately absent. Gatekeeper swaps one
            // wrong-key model for another rather than adding a handicap on top of the same model,
            // and the two already cost differently in accuracy vs completion, so it is ranked and
            // pays nothing either way. Mirrored by the mod's own ScoreMultiplier self-report, the
            // server's ModMultiplier and PerformancePoints.ModMultiplier (which is neutral for any
            // acronym it does not list).
            Single<TypeBeatModDoubleTime>(hasMultiplier: doubleTime => TypeBeatRateMultiplier.For(doubleTime.SpeedChange.Value));
            Single<TypeBeatModNightcore>(hasMultiplier: nightcore => TypeBeatRateMultiplier.For(nightcore.SpeedChange.Value));
            // Flashlight is a fixed character-window reveal (no size setting). 1.05x, trimmed from
            // the old circular flashlight's 1.2x: the character window is a far milder handicap.
            // Mirrored by the mod's own ScoreMultiplier self-report and the server's ModMultiplier.
            Single<TypeBeatModFlashlight>(hasMultiplier: 1.05);
            Single<TypeBeatModLiterate>(hasMultiplier: 1.05);
            // Recite hides every character until it is typed. Measured against its nearest sibling
            // it looks cheap: Flashlight hides strictly LESS (a 5-countable-char window each side of
            // the caret stays lit, so the next few chars are always readable) and pays 1.05x, while
            // Recite leaves nothing to read ahead at all and pays 1.07x (owner decision, backlog
            // 240, raised from the original 1.01x once the memory handicap was judged to be worth
            // more than a token premium). Mirrored by the mod's own ScoreMultiplier self-report and
            // the server's ModMultiplier. Headroom: the fattest ranked stack the server can be
            // HANDED becomes DT@2.00 (1.46) x FL (1.05) x LT (1.05) x HR (1.10) x FC (1.02) x
            // RE (1.07) = 1.932449211, still under the server's absolute STACK_CAP of 2.0. Not
            // "reachable": FL and RE stopped being co-selectable in this client (backlog 239), but
            // the server prices whatever acronym set a stored row carries, so the bound must hold
            // for the pair regardless.
            Single<TypeBeatModRecite>(hasMultiplier: 1.07);

            // Conversion.
            // Fletcher PINS the caret to the playhead (backlog 208 reversed the mod: the freedoms it
            // used to grant are the default now). Still ranked, and only a shade harder than the
            // unpinned default (every char is still typed and still judged against its own target),
            // so it takes a small premium rather than a bonus. Deliberately the mirror image of the
            // 0.98x it carried while it meant the opposite.
            Single<TypeBeatModFletcher>(hasMultiplier: 1.02);
            // The RETIRED "FT" acronym, unselectable but still on stored rows, priced at exactly what
            // those rows were priced at when they were submitted. Never remove this: a stored mod
            // that resolves to nothing prices at 1.0x and silently revalues every FT score on the
            // shared leaderboards. See TypeBeatModLegacyFletcher.
            Single<TypeBeatModLegacyFletcher>(hasMultiplier: 0.98);

            // Automation.
            Single<TypeBeatModMashing>(hasMultiplier: 0.1);

            // Fun.
            // Muted (1.0x) is deliberately absent: it is a flex, not a difficulty adjustment, so it is
            // ranked and carries no bonus and no penalty. Pinned by TypeBeatModMutedTest.
            Single<ModWindUp>(hasMultiplier: timeRampMultiplier);
            Single<ModWindDown>(hasMultiplier: timeRampMultiplier);
        }

        /// <summary>
        /// A ramp is paid mostly for the rate it spends the most map at (osu weights 80/20 toward
        /// the slower end). Both ends go through the same rate curve as the static rate mods, so a
        /// ramp that starts or ends at a rate a rate mod could have been set to is priced the same
        /// way. Wind Up / Wind Down stay unranked regardless; this is display-only.
        /// </summary>
        private static double timeRampMultiplier(ModTimeRamp timeRamp)
        {
            double minSpeed = Math.Min(timeRamp.InitialRate.Value, timeRamp.FinalRate.Value);
            double maxSpeed = Math.Max(timeRamp.InitialRate.Value, timeRamp.FinalRate.Value);

            return 0.8 * TypeBeatRateMultiplier.For(minSpeed) + 0.2 * TypeBeatRateMultiplier.For(maxSpeed);
        }
    }
}
