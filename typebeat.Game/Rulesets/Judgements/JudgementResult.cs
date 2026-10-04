// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Objects.Drawables;
using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Rulesets.Judgements
{
    /// <summary>
    /// The scoring result of a <see cref="DrawableHitObject"/>.
    /// </summary>
    public class JudgementResult
    {
        /// <summary>
        /// Whether this <see cref="JudgementResult"/> is the result of a hit or a miss.
        /// </summary>
        public HitResult Type;

        /// <summary>
        /// The <see cref="HitObject"/> which was judged.
        /// </summary>
        public readonly HitObject HitObject;

        /// <summary>
        /// The <see cref="Judgement"/> which this <see cref="JudgementResult"/> applies for.
        /// </summary>
        public readonly Judgement Judgement;

        /// <summary>
        /// The time at which this <see cref="JudgementResult"/> occurred.
        /// Populated when this <see cref="JudgementResult"/> is applied via <see cref="DrawableHitObject.ApplyResult{T}"/>.
        /// </summary>
        /// <remarks>
        /// This is used instead of <see cref="TimeAbsolute"/> to check whether this <see cref="JudgementResult"/> should be reverted.
        /// </remarks>
        internal double? RawTime { get; set; }

        /// <summary>
        /// The offset of <see cref="TimeAbsolute"/> from the end time of <see cref="HitObject"/>, clamped by <see cref="typebeat.Game.Rulesets.Objects.HitObject.MaximumJudgementOffset"/>.
        /// When the ruleset supplied a <see cref="JudgedTimeOffset"/>, that is the offset instead (clamped the same way).
        /// </summary>
        public double TimeOffset
        {
            get => RawTime != null ? Math.Min(JudgedTimeOffset ?? RawTime.Value - HitObject.GetEndTime(), HitObject.MaximumJudgementOffset) : 0;
            internal set => RawTime = HitObject.GetEndTime() + value;
        }

        /// <summary>
        /// The timing error the ruleset actually graded this result on, when that is not simply how
        /// far the judgement landed from <see cref="HitObject"/>'s end time. type!beat judges a typed
        /// character against its sung syllable's whole span (and its windows move with mods), so the
        /// raw distance from the character's point target is not the offset its result was decided
        /// by. Set while applying the result; <see cref="TimeOffset"/> then reports it, so every
        /// timing readout (hit error meters, the results screen's timing graph, unstable rate)
        /// places the hit where its judgement did. <see cref="TimeAbsolute"/> and the revert
        /// bookkeeping keep using the real judgement time. Null for every ordinary result.
        /// </summary>
        public double? JudgedTimeOffset { get; set; }

        /// <summary>
        /// The absolute time at which this <see cref="JudgementResult"/> occurred, clamped by the end time of <see cref="HitObject"/> plus <see cref="typebeat.Game.Rulesets.Objects.HitObject.MaximumJudgementOffset"/>.
        /// </summary>
        /// <remarks>
        /// The end time of <see cref="HitObject"/> is returned if this result is not populated yet.
        /// </remarks>
        public double TimeAbsolute => RawTime != null ? Math.Min(RawTime.Value, HitObject.GetEndTime() + HitObject.MaximumJudgementOffset) : HitObject.GetEndTime();

        /// <summary>
        /// The gameplay rate at the time this <see cref="JudgementResult"/> occurred.
        /// </summary>
        public double? GameplayRate { get; internal set; }

        /// <summary>
        /// The combo prior to this <see cref="JudgementResult"/> occurring.
        /// </summary>
        public int ComboAtJudgement { get; internal set; }

        /// <summary>
        /// The combo after this <see cref="JudgementResult"/> occurred.
        /// </summary>
        public int ComboAfterJudgement { get; internal set; }

        /// <summary>
        /// The highest combo achieved prior to this <see cref="JudgementResult"/> occurring.
        /// </summary>
        public int HighestComboAtJudgement { get; internal set; }

        /// <summary>
        /// The highest combo achieved after this <see cref="JudgementResult"/> occurred.
        /// </summary>
        public int HighestComboAfterJudgement { get; internal set; }

        /// <summary>
        /// The health prior to this <see cref="JudgementResult"/> occurring.
        /// </summary>
        public double HealthAtJudgement { get; internal set; }

        /// <summary>
        /// Whether the user was in a failed state prior to this <see cref="JudgementResult"/> occurring.
        /// </summary>
        public bool FailedAtJudgement { get; internal set; }

        /// <summary>
        /// Whether a miss or hit occurred.
        /// </summary>
        public bool HasResult => Type > HitResult.None;

        /// <summary>
        /// Whether a successful hit occurred.
        /// </summary>
        public bool IsHit => Type.IsHit();

        /// <summary>
        /// The increase in health resulting from this judgement result.
        /// </summary>
        public double HealthIncrease => Judgement.HealthIncreaseFor(this);

        /// <summary>
        /// Creates a new <see cref="JudgementResult"/>.
        /// </summary>
        /// <param name="hitObject">The <see cref="HitObject"/> which was judged.</param>
        /// <param name="judgement">The <see cref="Judgement"/> to refer to for scoring information.</param>
        public JudgementResult(HitObject hitObject, Judgement judgement)
        {
            HitObject = hitObject;
            Judgement = judgement;
            Reset();
        }

        internal void Reset()
        {
            Type = HitResult.None;
            RawTime = null;
            JudgedTimeOffset = null;
        }

        public override string ToString() => $"{Type} ({Judgement})";
    }
}
