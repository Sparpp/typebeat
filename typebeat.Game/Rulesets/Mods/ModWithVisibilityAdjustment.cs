// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Objects.Drawables;

namespace typebeat.Game.Rulesets.Mods
{
    /// <summary>
    /// A <see cref="Mod"/> which applies visibility adjustments to <see cref="DrawableHitObject"/>s
    /// with an increased visibility adjustment for the first adjustable object. type!beat removed the user
    /// setting that switched the latter off (no type!beat mod derives from this class), so it keeps the
    /// setting's old default: always on.
    /// </summary>
    public abstract class ModWithVisibilityAdjustment : Mod, IApplicableToBeatmap, IApplicableToDrawableHitObject
    {
        /// <summary>
        /// The first adjustable object.
        /// </summary>
        protected HitObject? FirstObject { get; private set; }

        /// <summary>
        /// Check whether the provided hitobject should be considered the "first" adjustable object.
        /// Can be used to skip spinners, for instance.
        /// </summary>
        /// <param name="hitObject">The hitobject to check.</param>
        protected virtual bool IsFirstAdjustableObject(HitObject hitObject) => true;

        /// <summary>
        /// Apply a special increased-visibility state to the first adjustable object.
        /// </summary>
        /// <param name="hitObject">The hit object to apply the state change to.</param>
        /// <param name="state">The state of the hitobject.</param>
        protected abstract void ApplyIncreasedVisibilityState(DrawableHitObject hitObject, ArmedState state);

        /// <summary>
        /// Apply a normal visibility state adjustment to an object.
        /// </summary>
        /// <param name="hitObject">The hit object to apply the state change to.</param>
        /// <param name="state">The state of the hitobject.</param>
        protected abstract void ApplyNormalVisibilityState(DrawableHitObject hitObject, ArmedState state);

        public virtual void ApplyToBeatmap(IBeatmap beatmap)
        {
            FirstObject = getFirstAdjustableObjectRecursive(beatmap.HitObjects);

            HitObject? getFirstAdjustableObjectRecursive(IReadOnlyList<HitObject> hitObjects)
            {
                foreach (var h in hitObjects)
                {
                    if (IsFirstAdjustableObject(h))
                        return h;

                    var nestedResult = getFirstAdjustableObjectRecursive(h.NestedHitObjects);
                    if (nestedResult != null)
                        return nestedResult;
                }

                return null;
            }
        }

        public virtual void ApplyToDrawableHitObject(DrawableHitObject dho)
        {
            dho.ApplyCustomUpdateState += (o, state) =>
            {
                // Increased visibility is applied to the entire first object, including all of its nested hitobjects.
                if (isObjectEqualToOrNestedIn(o.HitObject, FirstObject))
                    ApplyIncreasedVisibilityState(o, state);
                else
                    ApplyNormalVisibilityState(o, state);
            };
        }

        /// <summary>
        /// Checks whether a given object is nested within a target.
        /// </summary>
        /// <param name="toCheck">The <see cref="HitObject"/> to check.</param>
        /// <param name="target">The <see cref="HitObject"/> which may be equal to or contain <paramref name="toCheck"/> as a nested object.</param>
        /// <returns>Whether <paramref name="toCheck"/> is equal to or nested within <paramref name="target"/>.</returns>
        private bool isObjectEqualToOrNestedIn(HitObject toCheck, HitObject? target)
        {
            if (target == null)
                return false;

            if (toCheck == target)
                return true;

            foreach (var h in target.NestedHitObjects)
            {
                if (isObjectEqualToOrNestedIn(toCheck, h))
                    return true;
            }

            return false;
        }
    }
}
