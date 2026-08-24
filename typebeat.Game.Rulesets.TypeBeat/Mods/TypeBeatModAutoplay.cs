// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Watch a perfect automated play: <see cref="TypeBeatAutoGenerator"/> presses every typeable
    /// cell's expected character at the instant that cell judges delta 0 under the era this mod
    /// stack will be graded in. Rides the standard lazer autoplay plumbing (song select swaps in a
    /// <c>ReplayPlayer</c> fed by <see cref="CreateReplayData"/>, and the editor's test-play
    /// autoplay toggle picks it up too).
    /// </summary>
    public class TypeBeatModAutoplay : ModAutoplay
    {
        public override ModReplayData CreateReplayData(IBeatmap beatmap, IReadOnlyList<Mod> mods)
            // Literate must be carried through: it decides which cells exist (punctuation becomes
            // typed) and the case each is pressed in, so a perfect play has to be generated against
            // the same flattening the engine will judge against.
            //
            // The judgement ERA must be carried through for the same reason, and the condition is
            // DrawableTypeBeatRuleset.createEngine's, mirrored: every mod stack but Hard Rock is
            // judged on syllable spans, and HR alone reverted to the classic point targets in
            // backlog 180. Get this wrong in either direction and autoplay stops being perfect:
            // pressing point targets against a span engine costs Oks wherever a subtimed word's
            // target falls outside its own syllable (backlog 181), and pressing span edges against
            // the classic HR engine would be worse still, since HR halves every window.
            => new ModReplayData(new TypeBeatAutoGenerator(beatmap,
                    literate: mods.Any(m => m is TypeBeatModLiterate),
                    syllableTiming: !mods.Any(m => m is TypeBeatModHardRock)).Generate(),
                new ModCreatedUser { Username = "typebot" });
    }
}
