// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Graphics.UserInterface;

namespace typebeat.Game.Utils
{
    public static class TernaryStateUtils
    {
        /// <summary>
        /// Given a selection target and a function of truth, retrieve the correct ternary state for display.
        /// </summary>
        public static TernaryState GetTernaryState<T>(this IEnumerable<T> selection, Func<T, bool> func)
        {
            if (selection.Any(func))
                return selection.All(func) ? TernaryState.True : TernaryState.Indeterminate;

            return TernaryState.False;
        }
    }
}
