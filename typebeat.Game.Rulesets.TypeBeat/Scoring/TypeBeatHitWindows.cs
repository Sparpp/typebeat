// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// Mirrors the engine's <see cref="SyncWindows"/> onto osu's symmetric
    /// <see cref="HitWindows"/> API so <see cref="typebeat.Game.Rulesets.Objects.Drawables.DrawableHitObject"/>
    /// lifetimes and time-offset bookkeeping are coherent. osu's API has one width per result
    /// (± around the target), and so does this ladder now, so the two agree directly; the engine
    /// remains the sole judgement authority and these windows are never used to classify.
    /// Neither difficulty nor the map's timing granularity scales the windows; during a play they
    /// follow the engine's own effective ladder (see <see cref="WindowsSource"/>), so mods that
    /// rescale judgement rescale these too.
    /// </summary>
    public class TypeBeatHitWindows : HitWindows
    {
        // Nullable because the base ctor validates via the WindowFor override BEFORE this
        // field is assigned; the default ladder stands in during that base-ctor call only.
        private readonly SyncWindows? windows;

        private SyncWindows effectiveWindows => WindowsSource?.Invoke() ?? windows ?? SyncWindows.Default;

        /// <summary>
        /// Where the windows actually come from during a play: the engine's EFFECTIVE ladder, which
        /// mods rescale (Easy doubles it, Hard Rock halves it, the rate mods multiply in their rate)
        /// and a replay's CONFIG frame can rebuild. Read on every query rather than copied, so the
        /// hit error meter's bands and every lifetime bound match the windows the engine judges on.
        /// Null outside a play (beatmap conversion, difficulty calculation), where the default
        /// ladder stands, as it always did.
        /// </summary>
        public Func<SyncWindows>? WindowsSource { get; set; }

        public TypeBeatHitWindows(bool isFreestyle = false)
        {
            windows = SyncWindows.Default.Scaled(isFreestyle ? SyncWindows.FREESTYLE_WINDOW_SCALE : 1);
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
            // Windows are never difficulty-scaled.
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
