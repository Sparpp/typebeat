// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input;
using osu.Framework.Input.Events;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The scrollable list of all lyric lines: index, time, and an editable text box per line,
    /// the fastest surface for sweeping text edits ("yeah" → "yeaaaaaaaah") across a whole song.
    /// Clicking a row selects the line and seeks to it. Poll-synced: rows rebuild only when the
    /// line set changes identity; labels refresh in place; a focused text box is never stomped.
    ///
    /// This is also where a SECTION is picked: Ctrl+click toggles a line in or out of the
    /// selection and Shift+click takes the contiguous run from the anchor (the last plain or
    /// Ctrl-clicked row) to the clicked row. Every selected row is tinted AND carries a coloured
    /// corner notch (backlog 391, since the multi-select tint alone reads as the same gray as an
    /// unselected row), the last-clicked row stays the ACTIVE line the detail panel edits, and
    /// section-level operations (timing copy/paste, tap timing) consume the whole set. Escape drops it.
    /// </summary>
    public partial class LineListPanel : CompositeDrawable
    {
        [Resolved]
        private EditorBeatmap editorBeatmap { get; set; } = null!;

        [Resolved]
        private LyricEditState state { get; set; } = null!;

        private readonly FillFlowContainer<LineRow> rows;
        private readonly OsuScrollContainer scroll;
        private readonly Container listArea;
        private readonly RoundedButton lyricViewButton;
        private readonly List<TypeBeatHitObject> displayed = new List<TypeBeatHitObject>();
        private bool? lastToggleAvailable;

        // Nothing on the idle per-frame path allocates: the sort is cached, and whether any line
        // has an original (a walk of every word) is re-asked only when the order's version or the
        // map's language moved.
        private readonly OrderedLinesCache orderedLines = new OrderedLinesCache();
        private int originalsCheckedVersion = -1;
        private BeatmapLanguage? originalsCheckedLanguage;
        private bool canShowOriginal;

        // The gameplay syllable-marker setting (backlog 225), which the rows' rest view follows
        // (backlog 378): off, a row at rest shows its plain text with no marks. Defaults on, so a
        // panel built with no ruleset config (a bare test scene) draws them like gameplay does.
        private readonly Bindable<bool> syllableMarkers = new Bindable<bool>(true);

        public LineListPanel()
        {
            RelativeSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = TypeBeatStyle.PanelBackground,
                    Alpha = 0.6f,
                },
                listArea = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = scroll = new OsuScrollContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        ScrollbarOverlapsContent = false,
                        Child = rows = new FillFlowContainer<LineRow>
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 2),
                            Padding = new MarginPadding(4),
                        },
                    },
                },
                lyricViewButton = new RoundedButton
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Position = new Vector2(-4, 4),
                    Width = 174,
                    Height = 28,
                    TooltipText = "Switches the editor's lyric display. Original edits update the script editor's line original.",
                    Action = () =>
                    {
                        foreach (LineRow row in rows)
                            row.CommitPendingText();

                        state.ShowOriginalLyrics.Toggle();
                    },
                },
            };

            lyricViewButton.Alpha = 0;
            lyricViewButton.Enabled.Value = false;
        }

        [BackgroundDependencyLoader]
        private void load(IRulesetConfigCache? configCache)
        {
            TypeBeatRulesetConfigManager? config;

            try
            {
                config = configCache?.GetConfigFor(new TypeBeatRuleset()) as TypeBeatRulesetConfigManager;
            }
            catch
            {
                // Config unavailable (cache not loaded, ruleset unregistered): keep the default.
                config = null;
            }

            config?.BindWith(TypeBeatRulesetSetting.ShowSyllableMarkers, syllableMarkers);
        }

        protected override void Update()
        {
            base.Update();

            var current = orderedLines.Get(editorBeatmap);
            var language = editorBeatmap.BeatmapInfo.Metadata.Language;

            if (orderedLines.Version != originalsCheckedVersion || language != originalsCheckedLanguage)
            {
                canShowOriginal = language != BeatmapLanguage.English
                                  && language != BeatmapLanguage.Instrumental
                                  && current.Any(h => h.Line.Original != null || h.Line.UnromanisedWords.Count > 0
                                                      || h.Line.Units.Any(u => u.Original != null));
                originalsCheckedVersion = orderedLines.Version;
                originalsCheckedLanguage = language;
            }

            if (!canShowOriginal && state.ShowOriginalLyrics.Value)
                state.ShowOriginalLyrics.Value = false;

            if (lastToggleAvailable != canShowOriginal)
            {
                listArea.Padding = new MarginPadding { Top = canShowOriginal ? 36 : 0 };
                lyricViewButton.Alpha = canShowOriginal ? 1 : 0;
                lyricViewButton.Enabled.Value = canShowOriginal;
                lastToggleAvailable = canShowOriginal;
            }
            string buttonText = state.ShowOriginalLyrics.Value ? "Lyrics: Original" : "Lyrics: Romanized";

            if (lyricViewButton.Text != buttonText)
                lyricViewButton.Text = buttonText;

            if (!sameAsDisplayed(current))
            {
                displayed.Clear();
                displayed.AddRange(current);

                rows.Clear();

                foreach (var hitObject in current)
                    rows.Add(new LineRow(hitObject, syllableMarkers));
            }

            // A tap-timing pass shows only the section it is recording. Alpha 0 makes a row
            // non-present, so the FillFlowContainer drops it out of the flow entirely and the list
            // COLLAPSES to the scope rather than leaving holes.
            //
            // Driven from HERE rather than from the row's own Update: a non-present drawable stops
            // being updated, so a row that hid itself could never bring itself back when the pass
            // ended. The panel is always present, so this restores every row the frame the scope
            // clears, whichever way the pass exited.
            var children = rows.Children;

            for (int i = 0; i < children.Count; i++)
                children[i].Alpha = state.HiddenByTapScope(children[i].HitObject) ? 0 : 1;
        }

        private bool sameAsDisplayed(IReadOnlyList<TypeBeatHitObject> current)
        {
            if (current.Count != displayed.Count)
                return false;

            for (int i = 0; i < current.Count; i++)
            {
                if (current[i] != displayed[i])
                    return false;
            }

            return true;
        }

        #region The rest view: plain text plus gap marks (backlog 378)

        /// <summary>
        /// A row's REST text: its pipe form (<see cref="TypeBeatEditorOperations.PipeDisplayText"/>)
        /// with every <see cref="Typeability.SPLIT_MARKER"/> removed, i.e. the words as gameplay
        /// spells them. The same string instance when there is no pipe to remove.
        /// </summary>
        public static string RestTextOf(string pipeText) =>
            pipeText.IndexOf(Typeability.SPLIT_MARKER) < 0 ? pipeText : pipeText.Replace(Typeability.SPLIT_MARKER.ToString(), string.Empty);

        /// <summary>
        /// The REST text's gaps that carry a syllable mark: for each pipe of
        /// <paramref name="pipeText"/>, the index in <see cref="RestTextOf"/> of the character the
        /// pipe stands in FRONT of, so the mark is drawn at that character's left edge exactly as
        /// gameplay draws a mark at its cell's left edge. Ascending, distinct, and only ever a real
        /// gap (never the text's own leading edge or past its end).
        /// </summary>
        public static int[] RestGapsOf(string pipeText)
        {
            int pipes = 0;

            foreach (char c in pipeText)
            {
                if (c == Typeability.SPLIT_MARKER)
                    pipes++;
            }

            if (pipes == 0)
                return Array.Empty<int>();

            int plainLength = pipeText.Length - pipes;
            var gaps = new List<int>(pipes);
            int plain = 0;

            foreach (char c in pipeText)
            {
                if (c != Typeability.SPLIT_MARKER)
                {
                    plain++;
                    continue;
                }

                if (plain > 0 && plain < plainLength && (gaps.Count == 0 || gaps[^1] != plain))
                    gaps.Add(plain);
            }

            return gaps.ToArray();
        }

        /// <summary>
        /// Re-maps a caret position in the REST text to the pipe form the focused box shows, so a
        /// click lands where it was made: the caret stays in front of the same character, and a
        /// caret sitting in a marked gap lands in FRONT of that gap's pipe(s), never past them.
        /// Clamped to the text.
        /// </summary>
        public static int PipeCaretFor(string pipeText, int restCaret)
        {
            if (restCaret <= 0)
                return 0;

            int plain = 0;

            for (int i = 0; i < pipeText.Length; i++)
            {
                // Checked BEFORE the pipe is skipped, so a caret in a marked gap stops in front of it.
                if (plain == restCaret)
                    return i;

                if (pipeText[i] != Typeability.SPLIT_MARKER)
                    plain++;
            }

            return pipeText.Length;
        }

        #endregion

        /// <summary>Brings the active line's row into view (called by the screen on line change).</summary>
        public void ScrollToActive()
        {
            var row = rows.FirstOrDefault(r => r.HitObject == state.ActiveLine.Value);

            if (row != null)
                scroll.ScrollIntoView(row);
        }

        /// <summary>One list row. Public so scene tests can address a specific line's row.</summary>
        public partial class LineRow : CompositeDrawable
        {
            /// <summary>
            /// The rest view's syllable mark height as a fraction of the row's glyph height. Gameplay's
            /// <see cref="LyricLineDisplay.SYLLABLE_MARKER_HEIGHT"/> (0.09) is tuned for a lyric drawn at
            /// 40 px and more; on this 15 px row it came to about a pixel and a half and read as dirt on
            /// the screen (owner, 2026-10-03). Three times that keeps the same wedge shape at a size the
            /// eye finds; the band clamp in <see cref="LyricLineDisplay.SyllableMarkerGeometry"/> still
            /// keeps it inside the box, so a row too short for it shows the largest mark that fits.
            /// </summary>
            public const float REST_MARKER_HEIGHT = LyricLineDisplay.SYLLABLE_MARKER_HEIGHT * 3f;

            /// <summary>
            /// How far the rest mark may rise into the glyph row, as a fraction of the row's height.
            /// The 28 px box leaves only three or four pixels under the glyph row, which is not enough
            /// for a legible mark on its own. The bottom fifth of the row is descender space, and the
            /// mark's apex sits in an inter-character GAP where no glyph has ink, so letting the mark
            /// start there costs nothing it could collide with. Gameplay has its sweep rail band and
            /// needs no such allowance.
            /// </summary>
            public const float REST_MARKER_RISE = 0.2f;

            /// <summary>
            /// The multi-selection CORNER NOTCH (backlog 391): a small solid right triangle that
            /// fills the row's top-left corner, shown on every row of a Ctrl/Shift selection so the
            /// section is unmistakable at a glance. A 0.25-lightened row and an unselected row read as
            /// the same gray, so the owner asked for a colour mark rather than a brighter tint. Its
            /// LEG length (the two equal sides along the top and left edges), in pixels.
            /// </summary>
            public const float SELECTION_NOTCH_LEG = 8f;

            /// <summary>
            /// How far the notch is inset from the row's top-left before it is drawn, so it clears the
            /// row's 4 px rounded corner (the rounded mask would otherwise clip the triangle's point).
            /// </summary>
            public const float SELECTION_NOTCH_INSET = 4f;

            public readonly TypeBeatHitObject HitObject;

            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            [Resolved]
            private EditorClock editorClock { get; set; } = null!;

            private readonly Box background;
            private readonly FillFlowContainer body;
            private readonly Triangle selectionNotch;
            private OsuSpriteText indexText = null!;
            private OsuSpriteText timeText = null!;
            private LineTextBox textBox = null!;
            private OsuSpriteText originalCaption = null!;
            private bool? renderedOriginalView;
            private readonly IBindable<bool> syllableMarkers;

            // The row's strings are rebuilt only when what they are built from changes: the line
            // (immutable, so a new reference is the only way its text, words or start move), its
            // index and the view. An idle row allocates nothing.
            private LyricLine? builtLine;
            private int builtIndex = -1;
            private bool builtShowOriginal;

            // What the box shows while FOCUSED: the pipe form, the editing syntax (or the line's
            // original in the original view). Also the committed text an edit is compared against.
            private string display = string.Empty;

            // What the box shows at REST (backlog 378): the pipe form with its pipes removed, the
            // splits drawn instead as gameplay's gap triangles at restGaps. The original view has no
            // pipes, so there the two forms are the same string and there are no gaps.
            private string restText = string.Empty;
            private int[] restGaps = Array.Empty<int>();
            private string caption = string.Empty;

            /// <summary>The caption over the text box: the line's ORIGINAL text (backlog 330), or empty.</summary>
            public string OriginalCaptionText => originalCaption.Text.ToString();

            /// <summary>The row's text box (for scene tests).</summary>
            public OsuTextBox TextBox => textBox;

            /// <summary>The rest view's syllable marks currently drawn, in gap order (for scene tests).</summary>
            public IEnumerable<Drawable> VisibleRestMarkers => textBox.VisibleRestMarkers;

            /// <summary>The drawable of character <paramref name="index"/> of the text the box shows (for scene tests).</summary>
            public Drawable CharacterDrawable(int index) => textBox.CharacterDrawable(index);

            /// <summary>The flow the box lays its characters out in (for scene tests).</summary>
            public FillFlowContainer TextFlowDrawable => textBox.TextFlowDrawable;

            /// <summary>
            /// The multi-selection corner notch (backlog 391), for scene tests: Alpha 1 exactly while
            /// this row is a member of <see cref="LyricEditState.MultiSelectedLines"/>, 0 otherwise.
            /// </summary>
            public Drawable SelectionNotch => selectionNotch;

            /// <summary>The row's background box, for scene tests asserting the selection tint.</summary>
            public Box Background => background;

            public LineRow(TypeBeatHitObject hitObject, IBindable<bool>? syllableMarkers = null)
            {
                HitObject = hitObject;
                this.syllableMarkers = syllableMarkers ?? new Bindable<bool>(true);

                RelativeSizeAxes = Axes.X;
                AutoSizeAxes = Axes.Y;
                Masking = true;
                CornerRadius = 4;

                InternalChild = new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Children = new Drawable[]
                    {
                        background = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = TypeBeatStyle.Background,
                            Alpha = 0.9f,
                        },
                        body = new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.X,
                            AutoSizeAxes = Axes.Y,
                            Direction = FillDirection.Vertical,
                        },
                        // The multi-selection corner notch (backlog 391): a small solid right triangle
                        // filling the row's top-left corner, on TOP of the background (so the tint
                        // cannot hide it), inside this masked container. The Triangle shape draws
                        // apex-up with a horizontal base, so a right isoceles corner wedge is built by
                        // anchoring the apex (Origin = TopCentre) on the corner point and rotating it
                        // 45 degrees clockwise: the base then lies on the corner's diagonal and the two
                        // legs (length SELECTION_NOTCH_LEG) run along the top and left edges, which is
                        // the wedge the owner drew. Alpha drives its visibility, so it is not present
                        // on an unselected row.
                        selectionNotch = new Triangle
                        {
                            Anchor = Anchor.TopLeft,
                            Origin = Anchor.TopCentre,
                            Position = new Vector2(SELECTION_NOTCH_INSET),
                            Size = new Vector2(SELECTION_NOTCH_LEG * MathF.Sqrt(2), SELECTION_NOTCH_LEG * MathF.Sqrt(2) / 2),
                            Colour = TypeBeatStyle.SungAccent,
                            Rotation = 45,
                            // Never part of the layout, so a corner wedge cannot grow the row's
                            // auto-sized box (same treatment as the rest markers).
                            BypassAutoSizeAxes = Axes.Both,
                            Alpha = 0,
                        },
                    },
                };
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                // THE ORIGINAL TEXT (backlog 330) as a caption over the romanised line, aligned with
                // the text box, so the mapper can check the romanisation against the source word by
                // word. Absent (and taking no room) on a line without one.
                body.Add(originalCaption = new OsuSpriteText
                {
                    Margin = new MarginPadding { Left = 34 + 76 + 4, Top = 3 },
                    Font = TypeBeatStyle.Lyric(13),
                    Colour = TypeBeatStyle.SungAccent,
                    Alpha = 0,
                });

                body.Add(new GridContainer
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 34,
                    ColumnDimensions = new[]
                    {
                        new Dimension(GridSizeMode.Absolute, 34),
                        new Dimension(GridSizeMode.Absolute, 76),
                        new Dimension(),
                    },
                    Content = new[]
                    {
                        new Drawable[]
                        {
                            indexText = new OsuSpriteText
                            {
                                Anchor = Anchor.Centre,
                                Origin = Anchor.Centre,
                                Font = TypeBeatStyle.Mono(13),
                                Colour = TypeBeatStyle.UntypedChar,
                            },
                            timeText = new OsuSpriteText
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                Font = TypeBeatStyle.Mono(13),
                                Colour = TypeBeatStyle.UntypedChar,
                            },
                            textBox = new LineTextBox
                            {
                                Anchor = Anchor.CentreLeft,
                                Origin = Anchor.CentreLeft,
                                RelativeSizeAxes = Axes.X,
                                Height = 28,
                                FontSize = 15,
                                CommitOnFocusLost = true,
                                CommittedText = committedText,
                            },
                        },
                    },
                });

                textBox.OnCommit += (_, _) => commitText();
            }

            private void commitText()
            {
                if (!editorBeatmap.HitObjects.Contains(HitObject))
                    return;

                if (textBox.Text == committedText())
                    return;

                if (state.ShowOriginalLyrics.Value)
                {
                    // Use the same operation as the script editor's line-original field.
                    // This leaves the romanized words and their timing intact.
                    TypeBeatEditorOperations.SetLineOriginal(editorBeatmap, HitObject, textBox.Text);
                    textBox.Text = committedText();
                    return;
                }

                if (!TypeBeatEditorOperations.SetLineText(editorBeatmap, HitObject, textBox.Text))
                {
                    // Normalized to empty; refuse and flash (delete the line instead). The pipe form
                    // while the box still has focus; at rest, Update puts the rest view back.
                    textBox.Text = textBox.HasFocus ? TypeBeatEditorOperations.PipeDisplayText(HitObject.Line) : restText;
                    background.FlashColour(TypeBeatStyle.ErrorChar, 400, Easing.OutQuint);
                }
            }

            private string committedText() => state.ShowOriginalLyrics.Value
                ? TypeBeatEditorOperations.OriginalCaption(HitObject.Line) ?? TypeBeatEditorOperations.PipeDisplayText(HitObject.Line)
                : TypeBeatEditorOperations.PipeDisplayText(HitObject.Line);

            /// <summary>Commits a focused edit before the view button changes the row's text.</summary>
            public void CommitPendingText()
            {
                if (textBox.HasFocus && textBox.Text != committedText())
                    commitText();
            }

            protected override void Update()
            {
                base.Update();

                if (builtIndex != HitObject.LineIndex)
                {
                    builtIndex = HitObject.LineIndex;
                    indexText.Text = (builtIndex + 1).ToString();
                }

                bool showOriginal = state.ShowOriginalLyrics.Value;

                if (!ReferenceEquals(builtLine, HitObject.Line) || builtShowOriginal != showOriginal)
                {
                    var line = HitObject.Line;

                    if (!ReferenceEquals(builtLine, line))
                        timeText.Text = formatTime(line.StartTime);

                    // The box shows the line in its PIPE form: a subdivided word carries a '|' at each
                    // of its syllable splits ("ap|ple"), which is both how the split is displayed and
                    // how it is edited (see TypeBeatEditorOperations.SetLineText). The pipe is a
                    // reserved character of this surface only; it is stripped on commit and never
                    // reaches the stored lyric or a gameplay cell.
                    string romanized = TypeBeatEditorOperations.PipeDisplayText(line);
                    string? original = TypeBeatEditorOperations.OriginalCaption(line);
                    bool originalView = showOriginal && original != null;
                    display = originalView ? original! : romanized;
                    caption = originalView ? $"Romanized: {romanized}" : captionFor(line);

                    // At rest the box spells the words as gameplay does and marks each split with
                    // gameplay's triangle in the gap; the pipe form comes back only on focus.
                    restText = originalView ? display : RestTextOf(romanized);
                    restGaps = originalView ? Array.Empty<int>() : RestGapsOf(romanized);
                    textBox.SetRestGaps(restGaps);

                    builtLine = line;
                    builtShowOriginal = showOriginal;
                }

                bool modeChanged = renderedOriginalView != showOriginal;
                renderedOriginalView = showOriginal;

                textBox.ReadOnly = false;

                bool focused = textBox.HasFocus;
                string shown = focused ? display : restText;

                if ((!focused || modeChanged) && textBox.Text != shown)
                    textBox.Text = shown;

                // The marks belong to the rest text only: hidden while focused (the pipes are the
                // marks there), with the setting off, and for the frame a commit's text is still up.
                textBox.RestMarkersShown = !focused && syllableMarkers.Value && textBox.Text == restText;

                if (originalCaption.Text != caption)
                {
                    originalCaption.Text = caption;
                    originalCaption.Alpha = caption.Length > 0 ? 1 : 0;
                }

                bool active = state.ActiveLine.Value == HitObject;
                bool multiSelected = state.MultiSelectedLines.Contains(HitObject);
                background.Colour = active
                    ? TypeBeatStyle.PanelBackground.Lighten(0.5f)
                    : multiSelected
                        ? TypeBeatStyle.PanelBackground.Lighten(0.25f)
                        : TypeBeatStyle.Background;

                // The coloured corner notch (backlog 391) carries the multi-selection, since the
                // 0.25 tint reads as the same gray as an unselected row. Shown on EVERY member,
                // including the active one when it is in the set (which keeps its own brighter
                // tint too), and cleared the frame a row leaves the set.
                selectionNotch.Alpha = multiSelected ? 1 : 0;
            }

            /// <summary>
            /// The caption for a line (backlog 330): its original, with a line of nothing but
            /// unromanised words saying so, since its text box is then empty. Empty for a line with
            /// no original at all.
            /// </summary>
            private static string captionFor(LyricLine line)
            {
                string? original = TypeBeatEditorOperations.OriginalCaption(line);

                if (original == null)
                    return string.Empty;

                int unromanised = line.UnromanisedWords.Count;

                return unromanised == 0
                    ? original
                    : $"{original}   ({unromanised} word{(unromanised == 1 ? string.Empty : "s")} to romanise)";
            }

            private static string formatTime(double ms)
            {
                int total = (int)(ms / 1000);
                return $"{total / 60}:{total % 60:00}.{(int)(ms % 1000):000}";
            }

            protected override bool OnClick(ClickEvent e)
            {
                // Ctrl/Shift build a multi-selection (a section, for timing copy/paste and tap
                // timing) without seeking; yanking the playhead mid-selection would fight the user.
                if (e.ControlPressed)
                {
                    state.ToggleLine(HitObject);
                    return true;
                }

                if (e.ShiftPressed)
                {
                    state.SelectLineRange(TypeBeatEditorOperations.OrderedLines(editorBeatmap), HitObject);
                    return true;
                }

                state.SelectLine(HitObject);
                editorClock.SeekSmoothlyTo(HitObject.Line.StartTime);

                // Picking a line from the list ALWAYS brings the fine-timing strip to it. Seeking
                // alone is not enough: the strip only tracks the caret while its own follow is
                // armed, and the first manual pan or strip click disarms that for good, after which
                // a list click used to leave the mapper looking at a completely different part of
                // the song. This is a one-shot pan, so follow itself stays disarmed.
                state.RequestViewSnap(HitObject.Line.StartTime);
                return true;
            }

            /// <summary>
            /// The row's text box, adding one layer to Ctrl+Z. The framework's text box has no
            /// text-level undo and lets the Undo/Redo platform actions bubble, so while a box was
            /// focused mid-edit a Ctrl+Z fired an EDITOR undo out from under the typing: the undo
            /// replaces every hit object instance, the panel rebuilds its rows, and the focused box
            /// is destroyed together with the uncommitted edit. So: while focused with an
            /// in-progress (uncommitted) edit, Undo reverts the box to the committed text and stops
            /// there, and Redo is swallowed so it cannot vaporise the edit either. A pristine
            /// focused box passes both through, so the next Ctrl+Z steps into the editor history
            /// as usual (the layered-undo convention).
            ///
            /// <para>It also carries the REST view (backlog 378). Unfocused, the row gives it the
            /// line's plain text and the gaps its syllable splits fall in, and it draws gameplay's
            /// triangle in each gap: an overlay in the text container, beside the character flow and
            /// bypassing auto-size, so it consumes no width and scrolls with the text. On focus it
            /// swaps itself to the pipe form (<see cref="CommittedText"/>) before any key can land,
            /// with the click's caret re-mapped from the plain text to the pipe text. Setting
            /// <see cref="osu.Framework.Graphics.UserInterface.TextBox.Text"/> is not a commit (the
            /// framework records it as the last committed text), and the row's commit compares
            /// against the pipe form, so a focus gain alone never edits the line.</para>
            /// </summary>
            private partial class LineTextBox : OsuTextBox
            {
                /// <summary>The line's committed pipe-form text, the value an in-progress edit reverts to.</summary>
                public Func<string> CommittedText { get; init; } = () => string.Empty;

                private readonly List<Triangle> restMarkers = new List<Triangle>();
                private int[] restGaps = Array.Empty<int>();
                private bool restMarkersShown;

                // The layout the marks were last placed against. They are re-placed only when one
                // of these moves, so an idle row reads four floats and does nothing else.
                private string? placedText;
                private int[]? placedGaps;
                private Vector2 placedFlowSize = new Vector2(-1);
                private float placedContainerHeight = -1;

                // The character a not-yet-focused click put the caret in front of, in the REST text,
                // carried from the mouse-down (which positions the caret) to the focus (which the
                // framework grants on the click that follows it).
                private int? pendingRestCaret;
                private bool placingCaret;

                /// <summary>The gaps (indices into the rest text) the row's rest view marks. Cheap to call with the same array.</summary>
                public void SetRestGaps(int[] gaps)
                {
                    if (ReferenceEquals(gaps, restGaps))
                        return;

                    restGaps = gaps;

                    while (restMarkers.Count < gaps.Length)
                    {
                        var marker = new Triangle
                        {
                            // Gameplay's mark (LyricLineDisplay.addSyllableMarkers): same colour,
                            // apex up, its axis on the gap, and never part of the layout.
                            Colour = TypeBeatStyle.UntypedChar,
                            Anchor = Anchor.TopLeft,
                            Origin = Anchor.TopCentre,
                            BypassAutoSizeAxes = Axes.Both,
                            Alpha = 0,
                        };

                        restMarkers.Add(marker);
                        TextContainer.Add(marker);
                    }

                    placedGaps = null;
                    applyRestMarkerAlpha();
                }

                /// <summary>Whether the rest view's marks are drawn (the row's rest state and the marker setting).</summary>
                public bool RestMarkersShown
                {
                    set
                    {
                        if (restMarkersShown == value)
                            return;

                        restMarkersShown = value;
                        placedGaps = null;
                        applyRestMarkerAlpha();
                    }
                }

                public IEnumerable<Drawable> VisibleRestMarkers => restMarkers.Where(m => m.Alpha > 0);

                public Drawable CharacterDrawable(int index) => TextFlow.Children[index];

                public FillFlowContainer TextFlowDrawable => TextFlow;

                private void applyRestMarkerAlpha()
                {
                    for (int i = 0; i < restMarkers.Count; i++)
                        restMarkers[i].Alpha = restMarkersShown && i < restGaps.Length ? 1 : 0;
                }

                protected override void UpdateAfterChildren()
                {
                    base.UpdateAfterChildren();

                    // The flow has laid its characters out by now (it is a child), so their left
                    // edges are final for this frame.
                    if (!restMarkersShown || restGaps.Length == 0)
                        return;

                    var flow = TextFlow;
                    var characters = flow.Children;

                    if (characters.Count != Text.Length)
                        return;

                    if (ReferenceEquals(placedText, Text) && ReferenceEquals(placedGaps, restGaps)
                                                           && placedFlowSize == flow.DrawSize && placedContainerHeight == DrawHeight)
                        return;

                    // The vertical rule is gameplay's (LyricLineDisplay.SyllableMarkerGeometry): hung
                    // from the bottom of the glyph row. The row has no sweep rail, so the band the
                    // clamp keeps the mark inside is what is left of the box below the glyph row.
                    float glyphHeight = flow.DrawHeight;
                    float flowTop = flow.DrawPosition.Y;
                    // The room is measured to the BOX's bottom edge, in the text container's space:
                    // the container is only as tall as the glyph row and sits centred in the 28 px
                    // box, so its own height leaves no band at all (measured: 15 of 28, mark 1 px).
                    float rise = glyphHeight * REST_MARKER_RISE;
                    float boxBottom = TextContainer.ToLocalSpace(ScreenSpaceDrawQuad.BottomLeft).Y;
                    float band = boxBottom - (flowTop + glyphHeight) + rise;
                    var geometry = LyricLineDisplay.SyllableMarkerGeometry(glyphHeight, band, REST_MARKER_HEIGHT);

                    for (int k = 0; k < restGaps.Length; k++)
                    {
                        int gap = restGaps[k];

                        if (gap <= 0 || gap >= characters.Count)
                            continue;

                        restMarkers[k].Size = new Vector2(geometry.Width, geometry.Height);
                        restMarkers[k].Position = new Vector2(flow.DrawPosition.X + characters[gap].DrawPosition.X, flowTop + geometry.Top - rise);
                    }

                    placedText = Text;
                    placedGaps = restGaps;
                    placedFlowSize = flow.DrawSize;
                    placedContainerHeight = DrawHeight;
                }

                protected override bool OnMouseDown(MouseDownEvent e)
                {
                    // The framework's own rule (the character whose midpoint the pointer is past),
                    // read against the REST text the click was made on.
                    if (!HasFocus)
                    {
                        float x = TextFlow.ToLocalSpace(e.ScreenSpaceMousePosition).X;
                        int index = 0;

                        foreach (var character in TextFlow.Children)
                        {
                            if (character.DrawPosition.X + character.DrawSize.X / 2 > x)
                                break;

                            index++;
                        }

                        pendingRestCaret = index;
                    }

                    return base.OnMouseDown(e);
                }

                protected override void OnFocus(FocusEvent e)
                {
                    base.OnFocus(e);

                    int? restCaret = pendingRestCaret;
                    pendingRestCaret = null;

                    string edit = CommittedText();

                    if (Text == edit)
                        return;

                    // A click re-maps onto the pipe form only when the box really was showing that
                    // form's rest text; anything else (a tab focus) leaves the caret at the end.
                    bool remap = restCaret != null && RestTextOf(edit) == Text;

                    Text = edit;

                    if (remap)
                    {
                        placingCaret = true;
                        MoveCursorBy(-Text.Length);
                        MoveCursorBy(PipeCaretFor(edit, restCaret!.Value));
                        placingCaret = false;
                    }
                }

                protected override void OnCaretMoved(bool selecting)
                {
                    // The re-map is part of the click, which already gave its feedback.
                    if (!placingCaret)
                        base.OnCaretMoved(selecting);
                }

                public override bool OnPressed(KeyBindingPressEvent<PlatformAction> e)
                {
                    if (HasFocus && (e.Action == PlatformAction.Undo || e.Action == PlatformAction.Redo))
                    {
                        string committed = CommittedText();

                        if (Text != committed)
                        {
                            if (e.Action == PlatformAction.Undo)
                                Text = committed;

                            return true;
                        }

                        return false;
                    }

                    return base.OnPressed(e);
                }
            }
        }
    }
}
