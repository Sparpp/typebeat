// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// Mirrors the engine's asymmetric <see cref="SyncWindows"/> onto osu's symmetric
    /// <see cref="HitWindows"/> API so <see cref="typebeat.Game.Rulesets.Objects.Drawables.DrawableHitObject"/>
    /// lifetimes and time-offset bookkeeping are coherent. osu's API has one width per result
    /// (± around the target), so the LATE (wider) side of each engine window is used; the
    /// engine remains the sole judgement authority; these windows are never used to classify.
    /// Difficulty does not scale the windows (granularity does, via the engine's tiers).
    /// </summary>
    public class TypeBeatHitWindows : HitWindows
    {
        // Nullable because the base ctor validates via the WindowFor override BEFORE this
        // field is assigned; the Line tier stands in during that base-ctor call only.
        private readonly SyncWindows? windows;

        private SyncWindows effectiveWindows => windows ?? SyncWindows.For(TimingGranularity.Line);

        public TypeBeatHitWindows(TimingGranularity judgeGranularity)
        {
            windows = SyncWindows.For(judgeGranularity);
        }

        public override bool IsHitResultAllowed(HitResult result)
        {
            switch (result)
            {
                case HitResult.Great:
                case HitResult.Ok:
                case HitResult.Meh:
                case HitResult.Miss:
                    return true;

                default:
                    return false;
            }
        }

        public override void SetDifficulty(double difficulty)
        {
            // Windows are granularity-scaled by the engine, never difficulty-scaled.
        }

        public override double WindowFor(HitResult result)
        {
            switch (result)
            {
                case HitResult.Great:
                    return effectiveWindows.GreatLate;

                case HitResult.Ok:
                    return effectiveWindows.OkLate;

                case HitResult.Meh:
                case HitResult.Miss:
                    return effectiveWindows.MehLate;

                default:
                    return 0;
            }
        }
    }
}
