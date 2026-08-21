// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/UI/LyricStage.cs.
// Constant names restyled; nullable annotations added for the fork's hard-error nullability.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// The 3-line monkeytype stack (previous faded / active centre / next dimmed) with
    /// eased scroll on line change. Owns both carets and subscribes to engine events.
    /// Reads the inherited gameplay clock directly via <c>Time.Current</c>; in gameplay it
    /// must be mounted under the playfield's lyric-offset clock container so its notion of
    /// time matches the engine feed. It never calls <c>engine.Update</c>.
    /// </summary>
    public partial class LyricStage : CompositeDrawable
    {
        // Vertical gap between the three lyric lines; user-adjustable (TypeBeatRulesetSetting.LineSpacing),
        // so a live change re-runs the layout by invalidating laidOutFocus.
        private float lineGap = 96f;
        private readonly BindableFloat lineSpacing = new BindableFloat(96f);

        // The "get ready" cue: two depleting bars under the upcoming line's first char. A solid
        // bar lands on the line BOUNDARY (StartTime) and a 50%-opaque bar lands on the FIRST
        // WORD; a mapper may set the boundary earlier than the first word, so the two can be
        // distinct signals (when the boundary sits at the first word they coincide as one solid
        // bar). Each spans its final lead-in (TypingEngine.CUE_LEAD_MS). Sized/positioned by
        // direct per-frame sets (no transforms; must behave under frozen/scrubbed clocks).
        private const double approach_lead_ms = TypingEngine.CUE_LEAD_MS;
        private const float approach_bar_max_width = 140;
        private const float approach_bar_height = 4;

        private readonly TypingEngine engine;

        // Cached by DrawableTypeBeatRuleset for its subtree; absent in bare playfield test scenes.
        // Carries the Flashlight mod's visible-char radius (0 = mod off), read live each frame.
        [Resolved]
        private DrawableTypeBeatRuleset? drawableRuleset { get; set; }

        private Container lineContainer = null!;
        private LyricLineDisplay[] displays = Array.Empty<LyricLineDisplay>();

        // Flashlight stream geometry, fixed once the lines are known: countable (typeable, non-space)
        // char count per line, and its running total before each line (countableBase[k] = sum of
        // counts for lines 0..k-1). Together they place any caret in the one continuous countable
        // stream so the visible window can spill across line boundaries.
        private int[] lineCountableCounts = Array.Empty<int>();
        private int[] countableBase = Array.Empty<int>();
        private Caret playerCaret = null!;
        private Caret sungCaret = null!;
        private Box approachBar = null!;   // first-word cue (50% opaque)
        private Box boundaryBar = null!;   // line-boundary cue (solid), drawn on top
        private Container wrongKeyLayer = null!;

        private int wrongKeyPopupDirection = 1;

        // int.MinValue = nothing laid out; int.MaxValue = finished; >= 0 = active line k;
        // -(k + 2) = focused on UPCOMING line k (pre-roll or the dead zone after a seal but
        // before the next line's cue), distinct from the active encoding so the moment line k
        // activates, the layout re-runs to undim it.
        private int laidOutFocus = int.MinValue;
        private bool pendingSnap;
        private bool playerCaretVisible;
        private bool sungCaretVisible;

        /// <summary>
        /// The user's SUNG playhead style, held here as well as on <see cref="sungCaret"/> because
        /// the stage needs the one value the caret cannot express as a shape:
        /// <see cref="CaretStyle.None"/> means there is no playhead at all, so the caret hides and
        /// the underline sweep stops being fed. Every other value is a playhead shape, which the
        /// caret's own <see cref="Caret.Style"/> rides straight off this bindable.
        ///
        /// <para>It decides NOTHING about the lit syllable group. Since backlog 177 the group the
        /// vocals are on lifts its untyped cells to <see cref="TypeBeatStyle.SungChar"/> under every style, fed by <see cref="Update"/>
        /// every frame, so the playhead and the highlight are complements rather than two
        /// presentations to pick between, and this bindable only subtracts.</para>
        ///
        /// <para>The highlight is also deliberately independent of
        /// <see cref="TypingEngine.SyllableTiming"/>: that flag is a judgement rule, this is a look.
        /// <see cref="TypingLine.Syllables"/> is built for every line either way, so the lit group
        /// renders the same under classic judgement, which is what keeps it from silently doing
        /// nothing in a Release build.</para>
        ///
        /// <para>Defaults to <see cref="CaretStyle.Line"/> (matching <see cref="Caret.Style"/>'s own
        /// initialiser) so a stage built with no config, which is every bare test scene, gets the
        /// classic playhead rather than an accidental blank.</para>
        /// </summary>
        private readonly Bindable<CaretStyle> sungCaretStyle = new Bindable<CaretStyle>(CaretStyle.Line);

        /// <summary>Whether the user asked for no sung playhead at all.</summary>
        private bool noPlayhead => sungCaretStyle.Value == CaretStyle.None;

        public LyricStage(TypingEngine engine)
        {
            this.engine = engine;
            RelativeSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader(true)]
        private void load(TypeBeatRulesetConfigManager? config, LyricFontManager? fontManager)
        {
            var lines = engine.Lines;
            displays = new LyricLineDisplay[lines.Count];

            // Precompute the flashlight stream geometry (immutable for the map's lifetime).
            lineCountableCounts = new int[lines.Count];
            countableBase = new int[lines.Count];
            int runningCountable = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                countableBase[i] = runningCountable;
                int c = 0;

                foreach (var cell in lines[i].Cells)
                {
                    if (LyricLineDisplay.IsCountable(cell))
                        c++;
                }

                lineCountableCounts[i] = c;
                runningCountable += c;
            }

            // The gameplay typing font is an accessibility pick (OpenDyslexic / a system font) applied
            // only to the lyric stack. Resolved once here: an unset/unknown/failed font stays null so
            // the displays fall back to the built-in lyric font.
            string? lyricFont = resolveLyricFont(config, fontManager);

            lineContainer = new Container { RelativeSizeAxes = Axes.Both };

            for (int i = 0; i < lines.Count; i++)
            {
                var d = new LyricLineDisplay(lines[i], fontFamily: lyricFont)
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Alpha = 0f,
                };
                displays[i] = d;
                lineContainer.Add(d);
            }

            // Carets are positioned via absolute points in this stage's top-left-origin
            // local space (from ToSpaceOfOtherDrawable), so they must anchor top-left.
            playerCaret = new Caret(TypeBeatStyle.Caret, TypeBeatStyle.CARET_DAMP_HALF_TIME, blinks: true)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopCentre,
                Height = TypeBeatStyle.LYRIC_FONT_SIZE,
                Alpha = 0f,
            };
            sungCaret = new Caret(TypeBeatStyle.SungAccent, TypeBeatStyle.SUNG_DAMP_HALF_TIME, blinks: false)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopCentre,
                Height = TypeBeatStyle.LYRIC_FONT_SIZE,
                Alpha = 0f,
            };

            // Each head is dressed from its OWN setting, so the typing caret and the map playhead can
            // be shaped apart: the two sit on the same line and are otherwise told apart only by
            // colour, damp and blink, and their best shapes are not the same shape (the playhead's
            // Underline in particular runs near-parallel to the sung sweep rail the display draws just
            // under the glyphs). Both bind live, so either dropdown applies without a restart.
            config?.BindWith(TypeBeatRulesetSetting.CaretStyle, playerCaret.Style);
            config?.BindWith(TypeBeatRulesetSetting.SungCaretStyle, sungCaretStyle);
            sungCaret.Style.BindTo(sungCaretStyle);

            // A style change mid-play has exactly one thing to hand back that Update alone would
            // not, and only in one direction.
            //
            // INTO None: the sweep stops being fed, so whatever fill it was last handed would freeze
            // on screen. Zero it on every display (not just the sung one: the sung line can change
            // while the style stays put). The caret hides on the next Update.
            //
            // OUT OF None: nothing to do at all. The lit group is style-independent since backlog
            // 177, so there is nothing to clear, and the next Update feeds the sweep and the caret
            // again. This half used to clear the group and must not: leaving None no longer means
            // leaving the highlight.
            sungCaretStyle.BindValueChanged(e =>
            {
                if (e.NewValue != CaretStyle.None)
                    return;

                foreach (var d in displays)
                    d.SetSungPosition(0);
            });

            // Line spacing is user-adjustable and applies live: a change invalidates the laid-out
            // focus so the next Update re-runs the layout with the new gap.
            config?.BindWith(TypeBeatRulesetSetting.LineSpacing, lineSpacing);
            lineSpacing.BindValueChanged(e =>
            {
                lineGap = e.NewValue;
                laidOutFocus = int.MinValue;
            }, true);

            approachBar = new Box // first-word cue (50% opaque)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Colour = TypeBeatStyle.SungAccent,
                Height = approach_bar_height,
                Alpha = 0f,
            };

            boundaryBar = new Box // line-boundary cue (solid)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Colour = TypeBeatStyle.SungAccent,
                Height = approach_bar_height,
                Alpha = 0f,
            };

            wrongKeyLayer = new Container { RelativeSizeAxes = Axes.Both };

            // boundaryBar after approachBar → the solid boundary cue draws on top of the
            // translucent first-word cue where they overlap.
            InternalChildren = new Drawable[] { lineContainer, approachBar, boundaryBar, sungCaret, playerCaret, wrongKeyLayer };
        }

        /// <summary>
        /// Resolves the configured gameplay font family to a value safe to hand the lyric displays.
        /// Returns null (built-in font) for the default sentinel, when the font manager is absent, or
        /// when the chosen family cannot be registered; never throwing, so gameplay text always renders.
        /// </summary>
        private static string? resolveLyricFont(TypeBeatRulesetConfigManager? config, LyricFontManager? fontManager)
        {
            if (config == null || fontManager == null)
                return null;

            string family = config.GetBindable<string>(TypeBeatRulesetSetting.LyricFont).Value;

            if (string.IsNullOrWhiteSpace(family) || family.Equals(TypeBeatRulesetConfigManager.LYRIC_FONT_DEFAULT, StringComparison.Ordinal))
                return null;

            return fontManager.EnsureRegistered(family) ? family : null;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            engine.LineActivated += onLineActivated;
            engine.CharJudged += onCharJudged;
            engine.LineSealed += onLineSealed;
            engine.WrongKeyRejected += onWrongKeyRejected;
            engine.AbandonReclaimed += onAbandonReclaimed;
            engine.Rewound += onRewound;
        }

        /// <summary>
        /// A backspace re-opened a skipped word (backlog 167). The cells went back to Untyped with no
        /// judgement behind them, so nothing else would repaint them, and they would keep wearing the
        /// abandoned dimming while the player typed straight over them. The two other transitions of
        /// that state need nothing here: the skip announces a judgement per cell (which
        /// <see cref="onCharJudged"/> repaints) and the seal refreshes the whole line.
        /// </summary>
        private void onAbandonReclaimed(AbandonedCells abandoned)
        {
            if (abandoned.LineIndex < 0 || abandoned.LineIndex >= displays.Length)
                return;

            var d = displays[abandoned.LineIndex];

            foreach (int cellIndex in abandoned.CellIndices)
                d.RefreshCell(cellIndex);
        }

        /// <summary>
        /// A rejected wrong key never enters the line; instead the offending letter pops up
        /// beside the caret (alternating sides), falls away and fades. Purely cosmetic juice;
        /// transforms run on the gameplay clock like every other stage animation.
        /// </summary>
        private void onWrongKeyRejected(char c)
        {
            wrongKeyPopupDirection = -wrongKeyPopupDirection;
            int dir = wrongKeyPopupDirection;

            var letter = new OsuSpriteText
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.Centre,
                Font = TypeBeatStyle.Mono(30),
                Colour = TypeBeatStyle.ErrorChar,
                Text = (c == ' ' ? '_' : c).ToString(),
                Position = playerCaret.Position + new Vector2(dir * 34, -4),
                ShadowColour = TypeBeatStyle.TextShadow,
                ShadowOffset = TypeBeatStyle.TEXT_SHADOW_OFFSET,
            };

            wrongKeyLayer.Add(letter);

            letter.MoveToOffset(new Vector2(dir * 18, -30), 140, Easing.OutQuint)
                  .Then()
                  .MoveToOffset(new Vector2(dir * 12, 110), 460, Easing.InQuad);
            letter.RotateTo(dir * 18, 600, Easing.OutQuint);
            letter.Delay(140).FadeOut(460, Easing.InQuad);
            letter.Expire();
        }

        private void onLineActivated(int index)
        {
            relayout(index, animate: true);
            pendingSnap = true;
        }

        private void onCharJudged(CharJudgement judgement)
        {
            if (judgement.LineIndex >= 0 && judgement.LineIndex < displays.Length)
            {
                var d = displays[judgement.LineIndex];
                d.RefreshCell(judgement.CellIndex);
                d.PlayJudgementFeedback(judgement);
            }

            playerCaret.NotifyTyped();
        }

        private void onLineSealed(LineSealResult result)
        {
            if (result.LineIndex >= 0 && result.LineIndex < displays.Length)
                refreshDisplayCells(result.LineIndex);
        }

        /// <summary>
        /// The engine was re-derived to an earlier time (a backwards seek during replay or autoplay
        /// playback, see <see cref="TypingEngine.Rebuild"/>). None of the keystrokes it walked back
        /// over were announced, so every incremental thing this stage tracks is stale: which line it
        /// laid out, and every cell it has rendered.
        ///
        /// <para>Invalidating <see cref="laidOutFocus"/> is the whole of the repair. It forces
        /// <see cref="Update"/>'s own safety net to relayout on the very next frame whichever line
        /// the rebuilt engine is now on, and both relayout paths end in
        /// <see cref="refreshVisible"/>, which re-reads every cell of the visible stack straight off
        /// the engine's (in-place, identity-preserving) cells. Lines outside that window are refreshed
        /// as they scroll into it. The carets are snapped rather than flown, because the seek is a
        /// jump and interpolating across it would drag the caret over lines it never typed.</para>
        /// </summary>
        private void onRewound()
        {
            laidOutFocus = int.MinValue;
            pendingSnap = true;
        }

        protected override void Update()
        {
            base.Update();

            int active = engine.ActiveLineIndex;

            if (active >= 0 && active < displays.Length)
            {
                // Safety net in case the activation event was missed (e.g. clock scrubbing in tests).
                if (laidOutFocus != active)
                {
                    relayout(active, animate: true);
                    pendingSnap = true;
                }

                var d = displays[active];

                // Player caret follows the typing caret index.
                int caretIndex = engine.CaretIndex;
                Vector2 playerPoint = d.ToSpaceOfOtherDrawable(d.PositionOfCell(caretIndex), this);
                playerCaret.Height = d.LineHeight;
                playerCaret.SetCellWidth(d.CellWidthAt(caretIndex));

                if (pendingSnap)
                {
                    playerCaret.SnapTo(playerPoint);
                    pendingSnap = false;
                }
                else
                {
                    playerCaret.MoveToTarget(playerPoint);
                }

                // Sung caret + underline sweep follow the vocal position, on the line the SONG is on.
                // Normally that is the active line and this is exactly the old behaviour. Under
                // Fletcher the two come apart, which is the entire point of the mod: the sweep is the
                // song, the caret is the player, and you can watch yourself rush or drag away from it.
                // Once the song is more than one line from the focused line it is off the visible
                // stack, so the sung caret hides rather than parking at a phantom position.
                int sungLine = sungLineFor(active);
                var sd = displays[sungLine];

                // The syllable group the vocals are on lights in EVERY style (backlog 177): the
                // highlight and the playhead are complements, not alternatives, so this is fed each
                // frame whatever the setting says and the branch below decides only whether a head
                // is drawn alongside it. Cheap: the displays repaint only when the group changes.
                setSungSyllable(sungLine, currentSyllableIn(sd.Line));

                // Under CaretStyle.None none of this runs: the sweep fill/glow are never fed a
                // position, so they hold the zero the style change set them to, and the sung caret
                // is forced hidden below. The typing caret, approach cue and boundary bar are
                // untouched either way.
                if (!noPlayhead)
                {
                    double sung = sd.Line.SungPositionAt(Time.Current);
                    sd.SetSungPosition(sung);
                    Vector2 sungPoint = sd.ToSpaceOfOtherDrawable(sd.SungPositionPoint(sung), this);
                    sungCaret.Height = sd.LineHeight;
                    // Unlike the player caret, the playhead sits at a FRACTIONAL cell index, so a
                    // cell-covering style takes the interpolated advance: exactly the sung character's
                    // width at each onset, morphing across the gap in step with the underline sweep the
                    // display draws from the same fractional position. Fed every frame regardless of
                    // which SHAPE is selected, so switching to a cell style always has a live
                    // measurement to build on.
                    sungCaret.SetCellWidth(sd.CellWidthAtFraction(sung));
                    sungCaret.MoveToTarget(sungPoint);
                }

                refreshVisible(active);

                bool show = !engine.IsLineComplete && !engine.IsFinished;
                setCaretsVisible(show, show && !noPlayhead && Math.Abs(sungLine - active) <= 1);
            }
            else if (!engine.IsFinished)
            {
                // Pre-roll, or the dead zone between a boundary seal and the next line's cue:
                // focus the upcoming line, dimmed. The stack scroll happens HERE: the moment a
                // line seals (the boundary, or grace-end for overrunning vocals), not when the
                // next line activates.
                int upcoming = Math.Max(0, engine.NextUnsealedLineIndex);
                int encoded = -(upcoming + 2);

                if (laidOutFocus != encoded)
                {
                    relayoutUpcoming(upcoming);
                    laidOutFocus = encoded;
                }

                // No line is being sung, so no group may stay lit (mirrors the sung caret hiding).
                setSungSyllable(-1, -1);
                setCaretsVisible(false, false);
            }
            else
            {
                if (laidOutFocus != int.MaxValue)
                {
                    foreach (var d in displays)
                        d.FadeTo(0f, TypeBeatStyle.SCREEN_FADE_DURATION, Easing.OutQuint);
                    laidOutFocus = int.MaxValue;
                }

                setSungSyllable(-1, -1);
                setCaretsVisible(false, false);
            }

            updateApproachCue();
            updateFlashlight();
        }

        /// <summary>
        /// Flashlight mod: light a window of a fixed number of countable chars either side of the
        /// caret, taken over the WHOLE stack read as one continuous countable stream, so the budget
        /// spills across line boundaries (a line's tail and the next line's head can be lit at once).
        /// No-op when the mod is off (radius 0).
        ///
        /// While a line is active the window centres on its live caret. While no line is active but a
        /// line's approach cue is counting it in, the window anchors on that line's first char, so the
        /// player can read the letters they are about to type (and the tail of the line they just
        /// finished) before it activates; this covers pre-roll before the first line too. A dead zone
        /// with no cue showing (a long instrumental gap) stays fully dark, which is on-theme. Purely
        /// visual, so replays and autoplay light up identically and judgement is unaffected.
        /// </summary>
        private void updateFlashlight()
        {
            int radius = drawableRuleset?.FlashlightVisibleRadius ?? 0;

            if (radius <= 0)
                return;

            int active = engine.ActiveLineIndex;
            int caretStreamSlot;
            bool haveWindow;

            // Forward-spill cap. While a line is active AND still being typed, the window may not reach
            // past that line's last countable slot, so the next line's head stays dark no matter how
            // close the caret is to the end. The cap lifts (int.MaxValue) the instant the line is
            // complete, so the leftover right budget spills into the next line's head as an early-finish
            // reward; and during a cue-in (no active line) there is no cap, so the cued line's head and
            // the previous line's tail light unconditionally, independent of any spill proximity.
            int maxRightSlot = int.MaxValue;

            if (active >= 0 && !engine.IsFinished)
            {
                caretStreamSlot = streamSlotOf(active, engine.CaretIndex);
                haveWindow = true;

                if (!engine.IsLineComplete)
                    maxRightSlot = countableBase[active] + lineCountableCounts[active] - 1;
            }
            else if (!engine.IsFinished && approachCueTargetLine >= 0 && approachCueTargetLine < displays.Length)
            {
                // Cue-in: no line is active, but one is being counted in. Anchor at its first char.
                caretStreamSlot = streamSlotOf(approachCueTargetLine, 0);
                haveWindow = true;
            }
            else
            {
                caretStreamSlot = 0;
                haveWindow = false;
            }

            if (!haveWindow)
            {
                foreach (var d in displays)
                    d.HideForFlashlight();

                return;
            }

            var windows = LyricLineDisplay.ComputeStreamWindows(lineCountableCounts, caretStreamSlot, radius, maxRightSlot);

            for (int k = 0; k < displays.Length; k++)
            {
                if (!engine.IsFinished && !windows[k].IsHidden)
                    displays[k].SetFlashlightWindow(windows[k], showSweep: k == active);
                else
                    displays[k].HideForFlashlight();
            }
        }

        /// <summary>The caret's slot in the continuous countable stream: every countable char in the
        /// lines before <paramref name="lineIndex"/>, plus the countable chars strictly before
        /// <paramref name="caretCellIndex"/> within that line.</summary>
        private int streamSlotOf(int lineIndex, int caretCellIndex)
        {
            var cells = engine.Lines[lineIndex].Cells;
            int caret = Math.Clamp(caretCellIndex, 0, cells.Count);
            int before = 0;

            for (int i = 0; i < caret; i++)
            {
                if (LyricLineDisplay.IsCountable(cells[i]))
                    before++;
            }

            return countableBase[lineIndex] + before;
        }

        /// <summary>
        /// Shows two depleting bars under the upcoming line's first typeable char: a solid one
        /// that lands on the line BOUNDARY (StartTime) and a 50%-opaque one that lands on the
        /// FIRST WORD: the "get ready" signals after pre-roll and between lines. When the mapper
        /// set the boundary earlier than the first word the two are distinct; when the boundary
        /// sits at the first word they coincide as one solid bar. Each is hidden outside its own
        /// final <see cref="approach_lead_ms"/> window (a past instant is behind the clock, so
        /// stale cues can never appear).
        /// </summary>
        private void updateApproachCue()
        {
            int upcoming;

            if (engine.ActiveLineIndex == -1)
                upcoming = engine.NextUnsealedLineIndex;
            else
            {
                // A line activates at the very moment its cue window opens (activation IS
                // cue-open, TypingEngine.CUE_LEAD_MS). In continuous maps the PREVIOUS line is
                // still active through that window and carries the cue via ActiveLineIndex + 1;
                // but after a gap (previous line ended early) the line self-activates with
                // nobody before it, so while the active line's own first word is still ahead,
                // the cue belongs to the active line itself. Unconditionally targeting
                // ActiveLineIndex + 1 skipped the cue entirely for every line after a gap.
                var active = engine.Lines[engine.ActiveLineIndex];
                int activeFirst = firstTypeableIndex(active);
                bool inOwnLeadIn = activeFirst >= 0 && active.Cells[activeFirst].TargetTime > Time.Current;

                upcoming = inOwnLeadIn ? engine.ActiveLineIndex : engine.ActiveLineIndex + 1;
            }

            if (!engine.IsFinished && upcoming >= 0 && upcoming < displays.Length)
            {
                var line = engine.Lines[upcoming];
                int firstCell = firstTypeableIndex(line);

                if (firstCell >= 0)
                {
                    var d = displays[upcoming];
                    Vector2 point = d.ToSpaceOfOtherDrawable(d.PositionOfCell(firstCell), this);
                    var barPos = new Vector2(point.X, point.Y + d.LineHeight + 6);

                    // First-word cue (50% opaque) lands on the first word; boundary cue (solid)
                    // lands on the line's StartTime, which a mapper may set earlier than the word.
                    bool wordShown = updateCueBar(approachBar, barPos, line.Cells[firstCell].TargetTime - Time.Current, 0.5f);
                    bool boundaryShown = updateCueBar(boundaryBar, barPos, line.StartTime - Time.Current, 1f);

                    if (wordShown || boundaryShown)
                    {
                        approachCueTargetLine = upcoming;
                        return;
                    }
                }
            }

            approachBar.Alpha = 0f;
            boundaryBar.Alpha = 0f;
            approachCueTargetLine = -1;
        }

        /// <summary>
        /// Renders one depleting cue bar: width shrinks 1 -> 0 over the final
        /// <see cref="approach_lead_ms"/> before <paramref name="remaining"/> reaches 0,
        /// brightening as it lands. <paramref name="opacityScale"/> scales the alpha (1 = the
        /// solid boundary bar, 0.5 = the 50%-opaque first-word bar). Returns whether it is shown.
        /// </summary>
        private bool updateCueBar(Box bar, Vector2 pos, double remaining, float opacityScale)
        {
            if (remaining > 0 && remaining <= approach_lead_ms)
            {
                float progress = (float)(remaining / approach_lead_ms); // 1 -> 0 as it lands
                bar.Position = pos;
                bar.Width = approach_bar_max_width * progress;
                bar.Alpha = (0.85f - 0.35f * progress) * opacityScale; // brightens as it arrives
                return true;
            }

            bar.Alpha = 0f;
            return false;
        }

        // Which line the approach bar is currently rendered for; -1 while hidden. Test support:
        // alpha alone cannot distinguish "cued the right line" from a bar under a later line.
        private int approachCueTargetLine = -1;

        private static int firstTypeableIndex(TypingLine line)
        {
            for (int i = 0; i < line.Cells.Count; i++)
            {
                if (line.Cells[i].IsTypeable)
                    return i;
            }

            return -1;
        }

        private void relayout(int active, bool animate)
        {
            // First-ever layout applies instantly: transforms here run on the gameplay
            // clock, which may not be running yet (pre-roll) or may be frozen (scrubbing).
            double dur = animate && laidOutFocus != int.MinValue ? TypeBeatStyle.LINE_SCROLL_DURATION : 0;

            for (int k = 0; k < displays.Length; k++)
            {
                var d = displays[k];

                switch (k - active)
                {
                    case 0:
                        d.SetLineDim(0f);
                        fade(d, 1f, dur);
                        move(d, 0f, dur);
                        break;

                    case -1:
                        d.SetLineDim(0.7f);
                        fade(d, 1f, dur);
                        move(d, -lineGap, dur);
                        break;

                    case 1:
                        d.SetLineDim(0.4f);
                        fade(d, 1f, dur);
                        move(d, lineGap, dur);
                        break;

                    case -2:
                        fade(d, 0f, dur);
                        move(d, -2 * lineGap, dur);
                        break;

                    case 2:
                        fade(d, 0f, dur);
                        move(d, 2 * lineGap, dur);
                        break;

                    default:
                        fade(d, 0f, 0);
                        break;
                }
            }

            laidOutFocus = active;
            refreshVisible(active);
        }

        private void relayoutUpcoming(int upcoming)
        {
            // Positions match relayout(upcoming): the just-sealed line slides up, the upcoming
            // line takes the centre, but the centre line stays dimmed until it activates.
            // Same first-layout rule as relayout(): the gameplay clock may be frozen or not yet
            // running, so the initial state must not depend on transforms.
            double dur = laidOutFocus == int.MinValue ? 0 : TypeBeatStyle.LINE_SCROLL_DURATION;

            for (int k = 0; k < displays.Length; k++)
            {
                var d = displays[k];

                switch (k - upcoming)
                {
                    case 0:
                        d.SetLineDim(0.4f);
                        fade(d, 1f, dur);
                        move(d, 0f, dur);
                        break;

                    case -1:
                        d.SetLineDim(0.7f);
                        fade(d, 1f, dur);
                        move(d, -lineGap, dur);
                        break;

                    case 1:
                        d.SetLineDim(0.6f);
                        fade(d, 1f, dur);
                        move(d, lineGap, dur);
                        break;

                    case -2:
                        fade(d, 0f, dur);
                        move(d, -2 * lineGap, dur);
                        break;

                    case 2:
                        fade(d, 0f, dur);
                        move(d, 2 * lineGap, dur);
                        break;

                    default:
                        fade(d, 0f, 0);
                        break;
                }
            }

            refreshVisible(upcoming);
        }

        private void refreshVisible(int active)
        {
            int from = Math.Max(0, active - 1);
            int to = Math.Min(displays.Length - 1, active + 1);
            for (int k = from; k <= to; k++)
                refreshDisplayCells(k);
        }

        private void refreshDisplayCells(int index)
        {
            if (index < 0 || index >= displays.Length)
                return;

            var d = displays[index];
            int count = d.Line.Cells.Count;
            for (int c = 0; c < count; c++)
                d.RefreshCell(c);
        }

        /// <summary>
        /// Which line's display carries the sung sweep and sung caret: the line the SONG is on. That
        /// is the active line in default play (the engine keeps the two identical) and the first
        /// unsealed line under Fletcher, where the player's caret may have rushed past it or be
        /// dragging on it. Falls back to the focused line when everything has sealed.
        /// </summary>
        private int sungLineFor(int active)
        {
            if (!engine.FletcherEnabled)
                return active;

            int songLine = engine.NextUnsealedLineIndex;

            return songLine >= 0 && songLine < displays.Length ? songLine : active;
        }

        // Which display currently carries a lit sung syllable; -1 = none. Stage-tracked so the one
        // line leaving the sung role is cleared explicitly, the highlight's mirror of how the sung
        // sweep only ever rides the current sung line.
        private int syllableLitLine = -1;

        /// <summary>
        /// Route the currently sung group to the display that should carry it and clear the display
        /// that carried one before. <paramref name="lineIndex"/> -1 = nothing is sung anywhere
        /// (pre-roll, dead zones, finished). Cheap every frame: the displays repaint only on an
        /// index change.
        /// </summary>
        private void setSungSyllable(int lineIndex, int syllable)
        {
            if (syllableLitLine != lineIndex && syllableLitLine >= 0 && syllableLitLine < displays.Length)
                displays[syllableLitLine].SetSungSyllable(-1);

            syllableLitLine = lineIndex;

            if (lineIndex >= 0 && lineIndex < displays.Length)
                displays[lineIndex].SetSungSyllable(syllable);
        }

        /// <summary>
        /// The group of <paramref name="line"/> being sung right now: the one whose
        /// [StartTime, EndTime] span contains the current time, i.e. where the old playhead would
        /// have been; -1 between spans (nothing is being sung, so nothing lights). Groups are
        /// ordered with monotonic spans and there are at most a few dozen per line, so a linear
        /// scan per frame is nothing.
        ///
        /// <para>Gaps between spans are ordinary here, and one more kind opened up in backlog 178:
        /// a token that is not a syllabifiable English word gets no group, so the whole time it is
        /// sung this returns -1 and nothing lights. That is the intent, a stylised word keeps the
        /// plain per-character presentation, and it needs no code change because "between spans" and
        /// "over a word with no spans" are the same answer.</para>
        /// </summary>
        private int currentSyllableIn(TypingLine line)
        {
            double t = Time.Current;
            var groups = line.Syllables;

            for (int g = 0; g < groups.Count; g++)
            {
                if (t >= groups[g].StartTime && t <= groups[g].EndTime)
                    return g;
            }

            return -1;
        }

        private void setCaretsVisible(bool showPlayer, bool showSung)
        {
            if (showPlayer != playerCaretVisible)
            {
                playerCaretVisible = showPlayer;
                playerCaret.FadeTo(showPlayer ? 1f : 0f, 120, Easing.OutQuint);
            }

            if (showSung != sungCaretVisible)
            {
                sungCaretVisible = showSung;
                sungCaret.FadeTo(showSung ? 1f : 0f, 120, Easing.OutQuint);
            }
        }

        private static void fade(LyricLineDisplay d, float alpha, double dur)
        {
            if (dur <= 0)
                d.Alpha = alpha;
            else
                d.FadeTo(alpha, dur, Easing.OutQuint);
        }

        private static void move(LyricLineDisplay d, float y, double dur)
        {
            if (dur <= 0)
                d.Y = y;
            else
                d.MoveToY(y, dur, Easing.OutQuint);
        }

        protected override void Dispose(bool isDisposing)
        {
            engine.LineActivated -= onLineActivated;
            engine.CharJudged -= onCharJudged;
            engine.LineSealed -= onLineSealed;
            engine.WrongKeyRejected -= onWrongKeyRejected;
            engine.AbandonReclaimed -= onAbandonReclaimed;
            engine.Rewound -= onRewound;
            base.Dispose(isDisposing);
        }

        // --- Test-support accessors (public so cross-assembly test scenes can assert) ---

        public Vector2 PlayerCaretPosition => playerCaret.IsNotNull() ? playerCaret.Position : Vector2.Zero;
        public Vector2 SungCaretPosition => sungCaret.IsNotNull() ? sungCaret.Position : Vector2.Zero;
        public bool PlayerCaretVisible => playerCaret.IsNotNull() && playerCaret.Alpha > 0.5f;
        public bool SungCaretVisible => sungCaret.IsNotNull() && sungCaret.Alpha > 0.5f;

        /// <summary>Width of the shape each caret currently draws: the beam width in
        /// <see cref="CaretStyle.Line"/>, the covered cell's on-screen advance otherwise.</summary>
        public float PlayerCaretVisualWidth => playerCaret.IsNotNull() ? playerCaret.VisualWidth : 0f;

        public float SungCaretVisualWidth => sungCaret.IsNotNull() ? sungCaret.VisualWidth : 0f;

        /// <summary>The style each head is currently rendering in; test support for the two separate
        /// bindings (the pair is what lets a test assert one head moved and the other did not).</summary>
        public CaretStyle PlayerCaretStyle => playerCaret.IsNotNull() ? playerCaret.Style.Value : CaretStyle.Line;

        public CaretStyle SungCaretStyle => sungCaret.IsNotNull() ? sungCaret.Style.Value : CaretStyle.Line;

        /// <summary>Screen-space centre of the typing caret: the point the Flashlight mod reveals around.</summary>
        public Vector2 PlayerCaretScreenPosition => playerCaret.IsNotNull() ? playerCaret.ScreenSpaceDrawQuad.Centre : Vector2.Zero;

        /// <summary>
        /// While a boundary/first-word cue is counting a not-yet-active line in, the screen-space
        /// point where that line's typing caret will first appear, so the Flashlight mod can snap
        /// ahead to the new line before the caret arrives. False when no such cue is showing, or it
        /// targets the already-active line (use its live caret position instead).
        /// </summary>
        public bool TryGetUpcomingCaretScreenPosition(out Vector2 position)
        {
            int target = approachCueTargetLine;

            if (target >= 0 && target < displays.Length && target != engine.ActiveLineIndex)
            {
                var d = displays[target];
                int cell = firstTypeableIndex(engine.Lines[target]);
                Vector2 local = d.ToSpaceOfOtherDrawable(d.PositionOfCell(cell < 0 ? 0 : cell), this);
                position = ToScreenSpace(local);
                return true;
            }

            position = default;
            return false;
        }
        public bool ApproachCueVisible =>
            (approachBar.IsNotNull() && approachBar.Alpha > 0.1f) || (boundaryBar.IsNotNull() && boundaryBar.Alpha > 0.1f);

        /// <summary>The line index the approach cue is currently shown for; -1 while hidden.</summary>
        public int ApproachCueTargetLine => approachCueTargetLine;

        public LyricLineDisplay? DisplayAt(int index) => index >= 0 && index < displays.Length ? displays[index] : null;

        public LyricLineDisplay? ActiveDisplay
        {
            get
            {
                int active = engine.ActiveLineIndex;
                return active >= 0 && active < displays.Length ? displays[active] : null;
            }
        }
    }
}
