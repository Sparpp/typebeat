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
            RelativeSizeAxes = Axes.Y;

            InternalChild = new FillFlowContainer
            {
                AutoSizeAxes = Axes.X,
                RelativeSizeAxes = Axes.Y,
                Direction = FillDirection.Horizontal,
                Spacing = new Vector2(6, 0),
                Children = new Drawable[]
                {
                    new Container
                    {
                        Width = 52,
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
                        Width = 260,
                        RelativeSizeAxes = Axes.Y,
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
