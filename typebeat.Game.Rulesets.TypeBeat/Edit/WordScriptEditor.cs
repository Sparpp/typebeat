// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// THE TWO-ROW WORD EDITOR (backlog 330): for the active line, one column per sung word with its
    /// ORIGINAL above and its ROMANISATION below, the unromanised words included (their lower box is
    /// empty and marked). Shown only for a line that has an original at all, and taking no room
    /// otherwise.
    ///
    /// <list type="bullet">
    /// <item>The upper box sets the word's original; an original the romaniser can spell also
    /// PROPOSES the lower box's text (<see cref="TypeBeatEditorOperations.SetWordOriginal"/>).</item>
    /// <item>The lower box overwrites the word's typed text; it is the only way to give an
    /// unromanised word one (<see cref="TypeBeatEditorOperations.SetWordText"/>).</item>
    /// <item>The first column is the LINE's own original, the caption shown over the line
    /// (<see cref="TypeBeatEditorOperations.SetLineOriginal"/>).</item>
    /// </list>
    /// <para>The '|' split and '&amp;' freestyle gestures stay on the line box, which edits the
    /// romanised row as a whole.</para>
    /// </summary>
    public partial class WordScriptEditor : CompositeDrawable
    {
        [Resolved]
        private EditorBeatmap editorBeatmap { get; set; } = null!;

        [Resolved]
        private LyricEditState state { get; set; } = null!;

        [Resolved(CanBeNull = true)]
        private TypeBeatRulesetConfigManager? lyricFontConfig { get; set; }

        [Resolved(CanBeNull = true)]
        private LyricFontManager? lyricFontManager { get; set; }

        private readonly EditorLyricFontResolutionCache lyricFontResolution = new EditorLyricFontResolutionCache();
        private string? resolvedLyricFont;
        private readonly FillFlowContainer columns;
        private string shown = string.Empty;
        private TypeBeatHitObject? shownLine;

        public WordScriptEditor()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;

            InternalChild = new OsuScrollContainer(Direction.Horizontal)
            {
                RelativeSizeAxes = Axes.X,
                Height = 0,
                ScrollbarVisible = false,
                Child = columns = new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(4, 0),
                },
            };
        }

        /// <summary>How many word columns are showing (the line-original column excluded). For tests.</summary>
        public int WordColumnCount => Math.Max(0, columns.Count - 2);

        protected override void Update()
        {
            base.Update();

            string? nextLyricFont = lyricFontResolution.ResolveForEditor(lyricFontConfig, lyricFontManager);

            if (!string.Equals(nextLyricFont, resolvedLyricFont, StringComparison.Ordinal))
            {
                resolvedLyricFont = nextLyricFont;

                // A settings change should reach already-open inputs immediately, including a
                // focused one. Updating FontFamily preserves the text box and its caret.
                foreach (var column in columns.OfType<ScriptColumn>())
                    column.SetFontFamily(resolvedLyricFont);
            }

            var line = state.ActiveLine.Value;
            bool live = line != null && editorBeatmap.HitObjects.Contains(line) && !state.HiddenByTapScope(line);
            string signature = live ? signatureOf(line!.Line) : string.Empty;

            // Never rebuilt under a focused box: the mapper's half-typed word would be destroyed.
            if ((signature == shown && line == shownLine) || columns.Any(c => c is ScriptColumn column && column.HasFocusedBox))
                return;

            shown = signature;
            shownLine = line;
            columns.Clear();

            var scroll = (OsuScrollContainer)InternalChild;

            if (!live || TypeBeatEditorOperations.OriginalCaption(line!.Line) == null)
            {
                scroll.Height = 0;
                return;
            }

            scroll.Height = 58;

            var hitObject = line;
            var slots = TypeBeatEditorOperations.WordSlots(hitObject.Line);

            columns.Add(new Container
            {
                Width = 44,
                Height = 58,
                Child = new OsuSpriteText
                {
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                    Text = "script",
                    Font = TypeBeatStyle.Mono(11),
                    Colour = TypeBeatStyle.UntypedChar,
                },
            });

            columns.Add(new ScriptColumn(hitObject.Line.Original ?? string.Empty, "line original", null, false,
                upper => TypeBeatEditorOperations.SetLineOriginal(editorBeatmap, hitObject, upper), null, 180, resolvedLyricFont));

            for (int i = 0; i < slots.Count; i++)
            {
                int slot = i;

                columns.Add(new ScriptColumn(slots[i].Original ?? string.Empty, "original", slots[i].Text, slots[i].Unromanised,
                    upper => TypeBeatEditorOperations.SetWordOriginal(editorBeatmap, hitObject, slot, upper),
                    lower => TypeBeatEditorOperations.SetWordText(editorBeatmap, hitObject, slot, lower), fontFamily: resolvedLyricFont));
            }
        }

        private static string signatureOf(LyricLine line)
            => string.Join('\u0001', new[] { line.Original ?? string.Empty }
                .Concat(TypeBeatEditorOperations.WordSlots(line).Select(s => $"{s.Original}\u0002{s.Text}\u0002{s.Unromanised}")));

        /// <summary>One column: an upper (original) box and, for a word, a lower (romanised) box.</summary>
        private partial class ScriptColumn : FillFlowContainer
        {
            private readonly List<OsuTextBox> boxes = new List<OsuTextBox>();
            private string? fontFamily;

            public bool HasFocusedBox => boxes.Any(b => b.HasFocus);

            public ScriptColumn(string upper, string upperPlaceholder, string? lower, bool unromanised,
                                Func<string, bool> commitUpper, Func<string, bool>? commitLower, float? width = null, string? fontFamily = null)
            {
                this.fontFamily = fontFamily;
                Direction = FillDirection.Vertical;
                AutoSizeAxes = Axes.Y;
                Width = width ?? Math.Clamp(Math.Max(upper.Length * 16, (lower ?? string.Empty).Length * 9) + 24, 70, 220);
                Spacing = new Vector2(0, 2);

                Add(box(upper, upperPlaceholder, commitUpper));

                if (commitLower == null)
                    return;

                // An unromanised word is the thing standing between the map and submission, so its
                // empty box is marked.
                Add(box(lower ?? string.Empty, unromanised ? "romanise me" : "romanised", commitLower, unromanised));
            }

            public void SetFontFamily(string? family)
            {
                if (string.Equals(fontFamily, family, StringComparison.Ordinal))
                    return;

                fontFamily = family;

                foreach (var textBox in boxes)
                    textBox.FontFamily = family;
            }

            private OsuTextBox box(string text, string placeholder, Func<string, bool> commit, bool marked = false)
            {
                var textBox = new ScriptTextBox(marked)
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 26,
                    FontSize = 14,
                    FontFamily = fontFamily,
                    Text = text,
                    PlaceholderText = placeholder,
                    CommitOnFocusLost = true,
                };

                string committed = text;

                textBox.OnCommit += (sender, _) =>
                {
                    if (sender.Text == committed)
                        return;

                    if (!commit(sender.Text))
                        sender.Text = committed;
                };

                boxes.Add(textBox);
                return textBox;
            }
        }

        private partial class ScriptTextBox : OsuTextBox
        {
            private readonly bool marked;

            public ScriptTextBox(bool marked)
            {
                this.marked = marked;
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();

                // After load: the base loader assigns its own default background.
                if (marked)
                    BackgroundUnfocused = TypeBeatStyle.ErrorChar.Darken(1.5f);
            }
        }
    }
}
