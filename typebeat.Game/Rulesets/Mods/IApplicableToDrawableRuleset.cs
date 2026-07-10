// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.Mods
{
    /// <summary>
    /// An interface for <see cref="Mod"/>s that can be applied to <see cref="DrawableRuleset"/>s.
    /// </summary>
    public interface IApplicableToDrawableRuleset<TObject> : IApplicableMod
        where TObject : HitObject
    {
        /// <summary>
        /// Applies this <see cref="IApplicableToDrawableRuleset{TObject}"/> to a <see cref="DrawableRuleset{TObject}"/>.
        /// </summary>
        /// <param name="drawableRuleset">The <see cref="DrawableRuleset{TObject}"/> to apply to.</param>
        void ApplyToDrawableRuleset(DrawableRuleset<TObject> drawableRuleset);
    }
}
