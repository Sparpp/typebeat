// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Reflection;
using osu.Framework.Graphics.UserInterface;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// A <see cref="TextBox"/>'s highlighted range, which the framework keeps private, so it is read by
    /// reflection. An unreadable box reports no selection.
    /// </summary>
    public static class TextBoxSelection
    {
        private const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly PropertyInfo? left = typeof(TextBox).GetProperty("selectionLeft", flags);
        private static readonly PropertyInfo? length = typeof(TextBox).GetProperty("selectionLength", flags);

        public static bool Supported => left?.PropertyType == typeof(int) && length?.PropertyType == typeof(int);

        public static (int Start, int Length)? Of(TextBox box)
        {
            if (!Supported)
                return null;

            int l = (int)length!.GetValue(box)!;
            return l > 0 ? ((int)left!.GetValue(box)!, l) : null;
        }

        /// <summary>The caret's index when nothing is highlighted, else null.</summary>
        public static int? CaretOf(TextBox box)
        {
            if (!Supported)
                return null;

            return (int)length!.GetValue(box)! == 0 ? (int)left!.GetValue(box)! : null;
        }
    }
}
