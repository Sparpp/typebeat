// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Localisation;
using osu.Framework.Screens;
using typebeat.Game.Localisation;

namespace typebeat.Game.Screens.Edit
{
    /// <summary>
    /// TODO: eventually make this inherit Screen and add a local screen stack inside the Editor.
    /// </summary>
    public abstract partial class EditorScreen : VisibilityContainer
    {
        [Resolved]
        protected EditorBeatmap EditorBeatmap { get; private set; } = null!;

        protected override Container<Drawable> Content => content;
        private readonly Container content;

        public readonly EditorScreenMode Type;

        protected EditorScreen(EditorScreenMode type)
        {
            Type = type;

            Anchor = Anchor.Centre;
            Origin = Anchor.Centre;
            RelativeSizeAxes = Axes.Both;

            InternalChild = content = new PopoverContainer { RelativeSizeAxes = Axes.Both };
        }

        protected override void PopIn() => this.FadeIn();

        protected override void PopOut() => this.FadeOut();

        public virtual void OnExiting(ScreenExitEvent e)
        {
        }

        #region Clipboard operations

        public BindableBool CanCut { get; } = new BindableBool();

        /// <summary>
        /// Performs a "cut to clipboard" operation appropriate for the given screen.
        /// </summary>
        /// <remarks>
        /// Implementors are responsible for checking <see cref="CanCut"/> themselves.
        /// </remarks>
        public virtual void Cut()
        {
        }

        public BindableBool CanCopy { get; } = new BindableBool();

        /// <summary>
        /// Performs a "copy to clipboard" operation appropriate for the given screen.
        /// </summary>
        /// <remarks>
        /// Implementors are responsible for checking <see cref="CanCopy"/> themselves.
        /// </remarks>
        public virtual void Copy()
        {
        }

        public BindableBool CanPaste { get; } = new BindableBool();

        /// <summary>
        /// Performs a "paste from clipboard" operation appropriate for the given screen.
        /// </summary>
        /// <remarks>
        /// Implementors are responsible for checking <see cref="CanPaste"/> themselves.
        /// </remarks>
        public virtual void Paste()
        {
        }

        /// <summary>
        /// The Edit menu's label for <see cref="Paste"/> while this screen is current. A screen whose
        /// default paste does something more specific than "paste" names it here.
        /// </summary>
        public virtual LocalisableString PasteLabel => CommonStrings.Paste;

        /// <summary>
        /// Whether <see cref="PasteTimings"/> applies. Stays false on every screen that has no
        /// timing-only paste, which keeps the Edit menu's item disabled there.
        /// </summary>
        public BindableBool CanPasteTimings { get; } = new BindableBool();

        /// <summary>
        /// A paste that transfers TIMING only (Ctrl+Shift+V), for a screen whose default paste also
        /// carries content: the lyric editor, where <see cref="Paste"/> puts a copied line down as is.
        /// </summary>
        /// <remarks>
        /// Implementors are responsible for checking <see cref="CanPasteTimings"/> themselves.
        /// </remarks>
        public virtual void PasteTimings()
        {
        }

        #endregion
    }
}
