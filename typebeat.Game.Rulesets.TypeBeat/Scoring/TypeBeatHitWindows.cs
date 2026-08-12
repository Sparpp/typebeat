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
    ///
    /// <para>These are, and must stay, MILLISECONDS: the framework reads them as times, to decide
    /// when an object may be judged and how long it lives. So this reads the engine's millisecond
    /// ladder (<see cref="SyncMeasure.Milliseconds"/>) whatever measure the play is actually judged
    /// under. That is not a mismatch to be tidied away later: character distances have no
    /// millisecond value until a cell and a line are named, and a lifetime cannot wait for one.
    /// Under the character measure the widths are simply the widest a press could plausibly be, not
    /// a statement of what will score, which is exactly what a lifetime needs and never was more
    /// than.</para>
    /// </summary>
    public class TypeBeatHitWindows : HitWindows
    {
        // Nullable because the base ctor validates via the WindowFor override BEFORE this
        // field is assigned; the Line tier stands in during that base-ctor call only.
        private readonly SyncWindows? windows;

        private SyncWindows effectiveWindows => windows ?? SyncWindows.For(TimingGranularity.Line, SyncMeasure.Milliseconds);

        public TypeBeatHitWindows(TimingGranularity judgeGranularity)
        {
            windows = SyncWindows.For(judgeGranularity, SyncMeasure.Milliseconds);
        }

        public override bool IsHitResultAllowed(HitResult result)
        {
            switch (result)
            {
                case HitResult.Perfect:
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
                case HitResult.Perfect:
                    return effectiveWindows.PerfectLate;

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
