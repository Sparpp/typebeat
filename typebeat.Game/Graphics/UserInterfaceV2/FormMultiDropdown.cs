// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Overlays;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Graphics.UserInterfaceV2
{
    /// <summary>
    /// A <see cref="FormDropdown{T}"/> that holds a SET of items rather than one (backlog 472, the editor's song languages).
    /// Clicking an item toggles it and leaves the menu open, so several can be picked in one visit; every selected item
    /// is highlighted and carries a white tick on its right, and the header lists the selection in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built on the single-choice dropdown rather than beside it, so it keeps the form control's look, search bar,
    /// scrolling and keyboard preselection for free. The framework's dropdown is single-choice down to its item actions
    /// (each one sets <c>Current</c> and closes the menu), so the multi-select behaviour lives in the drawable items:
    /// their click toggles <see cref="SelectedValues"/> and is consumed before the menu ever sees it, which is what keeps
    /// the menu open. The inherited <c>Current</c> is unused here: read and write <see cref="SelectedValues"/> instead.
    /// </para>
    /// <para>
    /// The keyboard toggles too: Enter on the preselected item (arrow keys, or the first match of a typed search) toggles
    /// it in place through <see cref="FormDropdown{T}.CommitPreselectionInPlace"/>, and Enter with nothing preselected
    /// closes the menu. Escape closes it as on every dropdown.
    /// </para>
    /// </remarks>
    public partial class FormMultiDropdown<T> : FormDropdown<T>
        where T : notnull
    {
        /// <summary>
        /// The selected items, in the order they were picked. Replaced (never mutated) on every toggle, so one toggle
        /// is one value change however many items it adds or removes.
        /// </summary>
        public Bindable<IReadOnlyList<T>> SelectedValues { get; } = new Bindable<IReadOnlyList<T>>(Array.Empty<T>());

        /// <summary>
        /// How a click on <c>item</c> changes the selection. Defaults to a plain toggle (remove when selected, append
        /// when not); a caller with exclusive items (a "none of these" choice) supplies its own.
        /// </summary>
        public Func<IReadOnlyList<T>, T, IReadOnlyList<T>> ToggleRule { get; init; } = defaultToggle;

        /// <summary>The header text while nothing is selected.</summary>
        public LocalisableString EmptyText { get; init; }

        /// <summary>
        /// How the header names the selection. Defaults to the items' own texts joined by commas, in selection order.
        /// </summary>
        public Func<IReadOnlyList<T>, LocalisableString>? SelectionText { get; init; }

        private MultiSelectMenu menu = null!;

        /// <summary>
        /// Accepts focus so that a click on an item keeps the menu OPEN. The framework dropdown holds its menu open exactly
        /// while the search bar's text box has focus, and a click moves focus to the nearest ancestor of the clicked drawable
        /// that accepts it; nothing in a stock dropdown does, so the click drops focus and the menu closes (harmless there,
        /// since a pick closes it anyway). With the dropdown itself accepting focus, the nearest such ancestor of an item
        /// is the dropdown, which also contains the text box, and the input manager then leaves focus where it was.
        /// </summary>
        public override bool AcceptsFocus => true;

        protected override DropdownMenu CreateMenu() => menu = new MultiSelectMenu(this)
        {
            MaxHeight = MaxHeight,
        };

        protected override LocalisableString? HeaderLabelOverride
        {
            get
            {
                var selected = SelectedValues.Value;

                if (selected.Count == 0)
                    return EmptyText;

                if (SelectionText != null)
                    return SelectionText(selected);

                return string.Join(", ", selected.Select(v => GenerateItemText(v).ToString()));
            }
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            SelectedValues.BindValueChanged(_ =>
            {
                RefreshHeaderLabel();
                menu.UpdateChecks();
            }, true);
        }

        /// <summary>Applies <see cref="ToggleRule"/> for <paramref name="item"/>, as a click on it does.</summary>
        public void Toggle(T item)
        {
            if (Current.Disabled)
                return;

            SelectedValues.Value = ToggleRule(SelectedValues.Value, item);
        }

        /// <summary>Whether <paramref name="item"/> is currently selected.</summary>
        public bool IsChecked(T item) => SelectedValues.Value.Contains(item);

        /// <summary>
        /// Enter toggles the preselected item (the one under the arrow keys, the hovered one, or the first search match)
        /// and keeps the menu open, as a click does. The search term is then cleared so the next language can be typed
        /// straight away; that also drops the preselection, so a further Enter with nothing preselected just closes the
        /// menu ("type, Enter, type, Enter, Enter to finish").
        /// </summary>
        /// <remarks>
        /// Always handled here, never left to the framework's commit: that sets the unused <c>Current</c> from its
        /// fallback item (the one matching <c>Current</c>), and with no such item visible it indexes the list at -1.
        /// </remarks>
        protected override bool CommitPreselectionInPlace()
        {
            if (!Current.Disabled && menu.TryGetPreselected(out T value))
            {
                Toggle(value);
                Header.SearchTerm.Value = string.Empty;
            }
            else
                Menu.Close();

            return true;
        }

        private static IReadOnlyList<T> defaultToggle(IReadOnlyList<T> current, T item)
            => current.Contains(item) ? current.Where(v => !EqualityComparer<T>.Default.Equals(v, item)).ToArray() : current.Append(item).ToArray();

        private partial class MultiSelectMenu : FormDropdownMenu
        {
            private readonly FormMultiDropdown<T> dropdown;

            public MultiSelectMenu(FormMultiDropdown<T> dropdown)
            {
                this.dropdown = dropdown;
            }

            // The other half of keeping the menu open on a click (see FormMultiDropdown.AcceptsFocus): a dropdown menu
            // accepts focus itself, so a click on an item would move focus onto the menu, off the search text box, and
            // the menu would close. Refusing it here lets the click's focus search reach the dropdown above.
            public override bool AcceptsFocus => false;

            public override bool RequestsFocus => false;

            public void UpdateChecks()
            {
                foreach (var item in Children.OfType<DrawableMultiSelectMenuItem>())
                    item.UpdateCheck();
            }

            /// <summary>
            /// The visible item that is preselected, strictly: unlike the framework's <c>PreselectedItem</c>, this never
            /// falls back to the item for the unused <c>Current</c>.
            /// </summary>
            public bool TryGetPreselected(out T value)
            {
                foreach (var item in VisibleMenuItems)
                {
                    if (item.IsPreSelected)
                    {
                        value = ((DropdownMenuItem<T>)item.Item).Value;
                        return true;
                    }
                }

                value = default!;
                return false;
            }

            protected override DrawableDropdownMenuItem CreateDrawableDropdownMenuItem(MenuItem item) => new DrawableMultiSelectMenuItem(dropdown, item)
            {
                BackgroundColourHover = HoverColour,
                BackgroundColourSelected = SelectionColour,
            };
        }

        /// <summary>
        /// An item that toggles instead of choosing: highlighted with the dropdown's selection colour while checked (in
        /// place of the framework's single selected item), with a white tick on its right.
        /// </summary>
        public partial class DrawableMultiSelectMenuItem : OsuDropdownMenu.DrawableOsuDropdownMenuItem
        {
            private readonly FormMultiDropdown<T> dropdown;
            private readonly T value;

            private MultiSelectContent content = null!;

            public DrawableMultiSelectMenuItem(FormMultiDropdown<T> dropdown, MenuItem item)
                : base(item)
            {
                this.dropdown = dropdown;
                value = ((DropdownMenuItem<T>)item).Value;
            }

            /// <summary>Whether this item is in the selection (its highlight and tick).</summary>
            public bool Checked { get; private set; }

            /// <summary>How far the highlight background is shown (1 while checked or preselected, once its fade completes).</summary>
            public float HighlightAlpha => Background.Alpha;

            public override bool CloseMenuOnClick => false;

            protected override Drawable CreateContent() => content = new MultiSelectContent();

            protected override void LoadComplete()
            {
                base.LoadComplete();
                UpdateCheck();
            }

            public void UpdateCheck()
            {
                Checked = dropdown.IsChecked(value);

                if (content.IsNotNull())
                    content.Tick.Alpha = Checked ? 1 : 0;

                UpdateBackgroundColour();
            }

            protected override bool OnClick(ClickEvent e)
            {
                // Consumed here and never handed to the base: the base runs the framework item action, which is the
                // single-choice "set Current and close the menu".
                dropdown.Toggle(value);
                return true;
            }

            protected override void UpdateBackgroundColour()
            {
                // Checked stands in for the framework's IsSelected, which only ever marks the one Current item.
                Background.FadeColour(IsPreSelected ? BackgroundColourHover : BackgroundColourSelected, 100, Easing.OutQuint);

                if (IsPreSelected || Checked)
                    Background.FadeIn(100, Easing.OutQuint);
                else
                    Background.FadeOut(600, Easing.OutQuint);
            }

            private partial class MultiSelectContent : Content
            {
                public readonly SpriteIcon Tick;

                public MultiSelectContent()
                {
                    // Room on the right for the tick, so a long name truncates before it rather than under it.
                    Label.Padding = new MarginPadding { Left = 15, Right = 20 };

                    AddInternal(Tick = new SpriteIcon
                    {
                        Icon = FontAwesome.Solid.Check,
                        Size = new Vector2(10),
                        Anchor = Anchor.CentreRight,
                        Origin = Anchor.CentreRight,
                        Margin = new MarginPadding { Right = 5 },
                        Colour = Color4.White,
                        Alpha = 0,
                    });
                }
            }
        }
    }

    /// <summary>A <see cref="FormMultiDropdown{T}"/> over an enum's values.</summary>
    public partial class FormMultiEnumDropdown<T> : FormMultiDropdown<T>
        where T : struct, Enum
    {
        public FormMultiEnumDropdown()
        {
            Items = Enum.GetValues<T>();
        }
    }
}
