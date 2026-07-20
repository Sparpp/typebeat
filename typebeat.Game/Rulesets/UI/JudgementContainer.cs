// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Rulesets.Judgements;

namespace typebeat.Game.Rulesets.UI
{
    public partial class JudgementContainer<T> : Container<T>
        where T : DrawableJudgement
    {
        public override void Add(T judgement)
        {
            ArgumentNullException.ThrowIfNull(judgement);

            // remove any existing judgements for the judged object.
            // this can be the case when rewinding.
            RemoveAll(c => c.JudgedHitObject == judgement.JudgedHitObject, false);

            base.Add(judgement);
        }
    }
}
