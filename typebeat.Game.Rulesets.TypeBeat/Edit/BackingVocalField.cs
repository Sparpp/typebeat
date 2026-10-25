// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Localisation;
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
    /// The active line's BACKING VOCALS as text (backlog 445): one word per space-separated token,
    /// committed on Enter or when the box loses focus (<see cref="TypeBeatEditorOperations.SetBackingVocalText"/>).
    /// The words already there keep their timing and anchors in order; new ones are spread after the
    /// last, for the mapper to drag into place on the strip's backing lane, and an empty box removes
    /// them all. Never rewritten under a focused box, so a half-typed edit survives the frame.
    /// </summary>
    public partial class BackingVocalField : CompositeDrawable, IHasTooltip
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

        private const float caption_width = 52;
        private const float spacing = 6;

        // The box takes what is left of the line row, between these: the row is shared with the line
        // buttons and, while the line list is open, inset by its width.
        private const float min_box_width = 100;
        private const float max_box_width = 260;

        private readonly OsuTextBox box;

        // What the box was last filled from: the line, and the (immutable) LyricLine it held then, so
        // an idle frame compares two references and builds no text.
        private TypeBeatHitObject? shownLine;
        private LyricLine? shownSource;
        private string shownText = string.Empty;

        public LocalisableString TooltipText
            => "The line's backing vocals, one word per space: shown above the lyric in gameplay, never typed. New words land on the backing lane above the word blocks, to be dragged into place.";

        public BackingVocalField()
        {
            AutoSizeAxes = Axes.X;

            // A fixed height, the line buttons' own: the line row wraps, so it sizes itself by its content.
            Height = 30;

            InternalChild = new FillFlowContainer
            {
                AutoSizeAxes = Axes.X,
                RelativeSizeAxes = Axes.Y,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(spacing, 0),
                Children = new Drawable[]
                {
                    new Container
                    {
                        Width = caption_width,
                        RelativeSizeAxes = Axes.Y,
                        Child = new OsuSpriteText
                        {
                            Anchor = Anchor.CentreRight,
                            Origin = Anchor.CentreRight,
                            Text = "backing",
                            Font = TypeBeatStyle.Mono(11),
                            Colour = TypeBeatStyle.UntypedChar,
                        },
                    },
                    box = new OsuTextBox
                    {
                        Width = max_box_width,
                        // Absolute: OsuTextBox sets a height of its own, which a relative axis would
                        // multiply rather than replace.
                        Height = 30,
                        FontSize = 14,
                        PlaceholderText = "backing vocals",
                        CommitOnFocusLost = true,
                    },
                },
            };

            box.OnCommit += (sender, _) => commit(sender.Text);
        }

        /// <summary>The text the box is showing (test support).</summary>
        internal string Text => box.Text;

        /// <summary>The box itself (test support).</summary>
        internal OsuTextBox TextBox => box;

        protected override void Update()
        {
            base.Update();

            string? nextLyricFont = lyricFontResolution.ResolveForEditor(lyricFontConfig, lyricFontManager);

            if (!string.Equals(nextLyricFont, resolvedLyricFont, StringComparison.Ordinal))
            {
                resolvedLyricFont = nextLyricFont;
                box.FontFamily = resolvedLyricFont;
            }

            // Never past the panel's right edge: the box takes what is left of the row beside the
            // buttons (a pixel short, so the flow never wraps on a rounding error), and when that is
            // less than its minimum the row wraps the field onto a line of its own, where it starts
            // at 0 and has the whole row.
            if (Parent is Drawable row && row.DrawWidth > 0)
                box.Width = Math.Clamp(row.DrawWidth - X - caption_width - spacing - 1, min_box_width, max_box_width);

            var line = state.ActiveLine.Value;
            bool live = line != null && editorBeatmap.HitObjects.Contains(line) && !state.HiddenByTapScope(line) && state.TapSession == null;

            box.ReadOnly = !live;

            if (box.HasFocus)
                return;

            var source = live ? line!.Line : null;

            if (line == shownLine && ReferenceEquals(source, shownSource))
                return;

            shownLine = line;
            shownSource = source;
            shownText = source != null ? TypeBeatEditorOperations.BackingVocalText(source) : string.Empty;
            box.Text = shownText;
        }

        private void commit(string text)
        {
            // The line the box was filled from, not whatever is active now: a click elsewhere takes
            // the focus (and so commits) before it selects anything, but the edit belongs to the line
            // the mapper was typing for either way.
            var line = shownLine;

            if (line == null || !editorBeatmap.HitObjects.Contains(line) || state.TapSession != null || text == shownText)
                return;

            TypeBeatEditorOperations.SetBackingVocalText(editorBeatmap, line, text);

            // Refill from the model on the next frame, so the box shows the words as stored (single
            // spaces, brackets dropped) rather than as typed.
            shownSource = null;
        }
    }
}
