// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/UI/LyricStage.cs.
// Constant names restyled; nullable annotations added for the fork's hard-error nullability.

using System;

using System.Collections.Generic;
using System.Linq;

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Logging;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using osuTK;
using osuTK.Graphics;

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
        // so a live change re-runs the layout by invalidating laidOutFocus. Always the EFFECTIVE pitch
        // (see EffectiveRowPitch), never less than the font size allows.
        private float lineGap = 96f;
        private readonly BindableFloat lineSpacing = new BindableFloat(96f);


        /// <summary>
        /// The lyric font size this stage was built at (TypeBeatRulesetSetting.LyricFontSize,
        /// backlog 334), read ONCE at load: every display is constructed at it and every adornment
        /// the stage draws beside the lyric row is sized from it, so a change applies from the next
        /// play. A stage built with no config (every bare test scene) keeps the default.
        /// </summary>
        public float FontSize { get; private set; } = TypeBeatStyle.LYRIC_FONT_SIZE;

        /// <summary><see cref="FontSize"/> as a multiple of the default; exactly 1 at the default, so
        /// every "value at the default size" literal below is unchanged there.</summary>
        private float sizeRatio => LyricLineDisplay.SizeRatioFor(FontSize);

        /// <summary>
        /// The smallest row pitch the stack may be drawn at, as a multiple of the lyric font size.
        /// A row is the glyph row plus the sweep rail beneath it (about 1.21 times the size at every
        /// size, the rail's drop and thickness being ratios of it too), so this leaves a clear band
        /// between one row's rail and the next row's glyphs.
        /// </summary>
        public const float MIN_ROW_PITCH_RATIO = 1.3f;

        /// <summary>
        /// The pitch the three rows are actually drawn at: the player's line spacing, raised to
        /// <see cref="MIN_ROW_PITCH_RATIO"/> times the font size when that is larger, so a large font
        /// cannot overlap rows. The spacing setting itself is never rewritten, so shrinking the font
        /// back hands the player exactly the spacing they chose. Pure, so it is unit-testable.
        /// </summary>
        public static float EffectiveRowPitch(float lineSpacing, float fontSize) => Math.Max(lineSpacing, fontSize * MIN_ROW_PITCH_RATIO);

        // The optional space error dot (TypeBeatRulesetSetting.UseSpaceErrorDot, on by default since PR 2), a

        // display-only marker the lyric displays draw themselves. Held here so a live change reaches
        // every display; see the binding in load() for why it never touches the replay CONFIG frame.
        private readonly Bindable<bool> spaceErrorDot = new Bindable<bool>();

        // The syllable markers (TypeBeatRulesetSetting.ShowSyllableMarkers, on by default), the same
        // shape of display-only setting as the dot above. Initialised TRUE rather than to default(bool)
        // for the reason sungCaretStyle carries its own initialiser: a stage built with no config,
        // which is every bare test scene, must start on what the game actually ships.
        private readonly Bindable<bool> syllableMarkers = new Bindable<bool>(true);

        // Display-only. Map-relative colours are on by default, including without a config.
        private readonly Bindable<bool> showPaceColours = new Bindable<bool>(true);
        private readonly Bindable<PaceColourMode> paceColourMode = new Bindable<PaceColourMode>(PaceColourMode.MapRelative);
        private readonly BindableFloat paceColourOpacityCurve = new BindableFloat();
        private readonly BindableFloat paceColourGradient = new BindableFloat();
        private readonly BindableFloat paceColourMaxChange = new BindableFloat((float)UnderlinePace.DEFAULT_MAX_CHANGE_PERCENT);
        private readonly BindableFloat syllableBrightness = new BindableFloat(60f);
        private readonly Bindable<JudgementIndicatorMode> judgementIndicator = new Bindable<JudgementIndicatorMode>(JudgementIndicatorMode.FadeIn);
        private readonly BindableBool approachBars = new BindableBool(true);
        private readonly BindableFloat syllableFadeInEnd = new BindableFloat();
        private readonly BindableFloat syllableFadeInDuration = new BindableFloat(TypeBeatRulesetConfigManager.DEFAULT_SYLLABLE_FADE_IN_DURATION_MS);
        private readonly BindableBool textPopIn = new BindableBool(true);
        private readonly BindableFloat textPopInAmount = new BindableFloat(TypeBeatRulesetConfigManager.DEFAULT_TEXT_POP_IN_AMOUNT);

        // The sync tint (TypeBeatRulesetSetting.ShowSyncMetric, off by default since backlog 251),
        // the same shape of display-only setting again. Initialised FALSE for the reason the two
        // above carry their own initialisers: a stage built with no config, which is every bare test
        // scene, must start on what the game actually ships.
        private readonly Bindable<bool> syncTint = new Bindable<bool>();

        // The timing tint (TypeBeatRulesetSetting.ShowTimingTint), off by default on the same terms.
        private readonly Bindable<bool> timingTint = new Bindable<bool>();
        private readonly BindableFloat timingTintMinStrength = new BindableFloat(TypeBeatRulesetConfigManager.DEFAULT_TIMING_TINT_MIN_STRENGTH);
        private readonly BindableFloat timingTintMaxStrength = new BindableFloat(TypeBeatRulesetConfigManager.DEFAULT_TIMING_TINT_MAX_STRENGTH);
        private readonly Bindable<string> timingTintEarlyColour = new Bindable<string>(TypeBeatRulesetConfigManager.FormatTintColour(TypeBeatStyle.EarlyTint));
        private readonly Bindable<string> timingTintLateColour = new Bindable<string>(TypeBeatRulesetConfigManager.FormatTintColour(TypeBeatStyle.LateTint));

        // The "get ready" cue: two depleting bars under the upcoming line's first char. A solid
        // bar lands on the line BOUNDARY (StartTime) and a 50%-opaque bar lands on the FIRST
        // WORD; a mapper may set the boundary earlier than the first word, so the two can be
        // distinct signals (when the boundary sits at the first word they coincide as one solid
        // bar). Each spans its final lead-in (TypingEngine.CUE_LEAD_MS). Sized/positioned by
        // direct per-frame sets (no transforms; must behave under frozen/scrubbed clocks).
        private const double approach_lead_ms = TypingEngine.CUE_LEAD_MS;
        // Values at the DEFAULT font size; each is drawn times sizeRatio (backlog 334).
        private const float approach_bar_max_width = 140;
        private const float approach_bar_height = 4;

        /// <summary>Drop from the bottom of a glyph row to the top of a cue bar (and of the push
        /// warning) at the default font size, 1/7 of it: the same band as the sweep rail
        /// (<see cref="LyricLineDisplay.SWEEP_RAIL_OFFSET"/>). Drawn times the size ratio.</summary>
        public const float CUE_BAR_OFFSET = 6;

        // The rejected-key letter at the default font size: its own size, where it pops up beside
        // the caret, and the two legs of its flight. Drawn times sizeRatio.
        private const float rejected_letter_size = 30;
        private static readonly Vector2 rejected_letter_offset = new Vector2(34, -4);
        private static readonly Vector2 rejected_letter_rise = new Vector2(18, -30);
        private static readonly Vector2 rejected_letter_fall = new Vector2(12, 110);

        // How long the push warning takes to reach its full strength once its window opens. It is a
        // warning the player did not ask for, so it announces itself instead of appearing at half
        // brightness between one frame and the next; the WIDTH drain, not the entrance, is what
        // carries the countdown.
        private const double push_fade_in_ms = 400;

        private readonly TypingEngine engine;

        // Cached by DrawableTypeBeatRuleset for its subtree; absent in bare playfield test scenes.
        // Carries the Flashlight mod's visible-char radius (0 = mod off), read live each frame.
        [Resolved]
        private DrawableTypeBeatRuleset? drawableRuleset { get; set; }

        // The game-wide working beatmap, the file store the map's bundled font file (backlog 291)
        // is read from. Absent in bare playfield test scenes, where the map font simply cannot
        // resolve and the built-in fallback applies.
        [Resolved(CanBeNull = true)]
        private IBindable<WorkingBeatmap>? workingBeatmap { get; set; }

        private Container lineContainer = null!;
        private LyricLineDisplay[] displays = Array.Empty<LyricLineDisplay>();
        private float availableLineWidth = -1f;

        // Flashlight stream geometry, fixed once the lines are known: countable (typeable, non-space)
        // char count per line, and its running total before each line (countableBase[k] = sum of
        // counts for lines 0..k-1). Together they place any caret in the one continuous countable
        // stream so the visible window can spill across line boundaries.
        private int[] lineCountableCounts = Array.Empty<int>();
        private int[] countableBase = Array.Empty<int>();
        private Caret playerCaret = null!;
        private Caret sungCaret = null!;
        private Box approachBar = null!;   // first-word cue (50% opaque)
        private Box boundaryBar = null!;   // line-boundary cue (solid, Fletcher only), drawn on top
        private Box pushBar = null!;       // the push warning (solid red, right-aligned)
        private Container wrongKeyLayer = null!;

        private int wrongKeyPopupDirection = 1;

        // int.MinValue = nothing laid out; int.MaxValue = finished; >= 0 = active line k;
        // -(k + 2) = focused on UPCOMING line k (pre-roll or the dead zone after a seal but
        // before the next line's cue), distinct from the active encoding so the moment line k
        // activates, the layout re-runs to undim it.
        private int laidOutFocus = int.MinValue;

        /// <summary>
        /// The line whose dim currently reflects <see cref="TypingEngine.AwaitingEntry"/>, and
        /// whether it was applied. The dim itself is the same 0.4 an upcoming line carries, so a line
        /// handed to the player before its window opens reads as "not yet yours" rather than looking
        /// live and swallowing keys.
        /// </summary>
        private int awaitingDimLine = int.MinValue;

        private bool awaitingDimApplied;
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
        /// <para>The highlight follows syllable spans or individual character targets according
        /// to the engine's timing mode. It remains independent of the chosen playhead shape.</para>
        ///
        /// <para>Defaults to <see cref="CaretStyle.Line"/> (matching <see cref="Caret.Style"/>'s own
        /// initialiser) so a stage built with no config, which is every bare test scene, gets the
        /// classic playhead rather than an accidental blank.</para>
        /// </summary>
        private readonly Bindable<CaretStyle> sungCaretStyle = new Bindable<CaretStyle>(CaretStyle.Line);

        /// <summary>Whether the user asked for no sung playhead at all.</summary>
        private bool noPlayhead => sungCaretStyle.Value == CaretStyle.None;

        /// <summary>The grouping-switch handler subscribed to the engine at load (see <c>load</c>), or null.</summary>
        private Action? regroup;

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

            config?.BindWith(TypeBeatRulesetSetting.PaceColourMaxChange, paceColourMaxChange);
            config?.BindWith(TypeBeatRulesetSetting.PaceColourMode, paceColourMode);
            config?.BindWith(TypeBeatRulesetSetting.PaceColourOpacityCurve, paceColourOpacityCurve);

            // Use the same whole-map average as the map's pace readouts. Playback rate scales both
            // local and average pace equally, so their ratio can be computed in authored time.
            double? averageWpm = null;
            PaceBand[][] buildPaceBands()
            {
                if (paceColourMode.Value == PaceColourMode.MapRelative)
                {
                    averageWpm ??= LyricPaceStatistics.Compute(lines.Select(l => l.Source), engine.Literate).AverageWpm;
                    return UnderlinePace.BuildMapRelativeBands(lines, averageWpm.Value,
                        paceColourMaxChange.Value, engine.AuthoredSyllablesOnly, paceColourOpacityCurve.Value);
                }

                return UnderlinePace.BuildRelativeBands(lines, paceColourMaxChange.Value, engine.AuthoredSyllablesOnly, paceColourOpacityCurve.Value);
            }

            var paceBands = buildPaceBands();

            // The gameplay typing font is an accessibility pick (OpenDyslexic / a system font) applied
            // only to the lyric stack. Resolved once here: an unset/unknown/failed font stays null so
            // the displays fall back to the built-in lyric font.
            string? lyricFont = resolveLyricFont(config, fontManager);
            float lyricFontSize = config?.GetBindable<float>(TypeBeatRulesetSetting.LyricFontSize).Value
                                  ?? TypeBeatStyle.LYRIC_FONT_SIZE;

            // POLYGLOT (backlog 331): the lines are drawn in their original script, which the lyric
            // font may not cover. Every character they can show (the cells and the originals, a hangul
            // block drawn over its keys included) gets a system face registered as a fallback when no
            // loaded font can draw it, so a missing glyph never renders as a blank cell.
            if (engine.Polyglot)
            {
                fontManager?.EnsureCoverage(lines.SelectMany(l => l.Cells.Select(c => c.Expected))
                                                 .Concat(lines.SelectMany(l => l.Source.Units.SelectMany(u => PolyglotText.ToNfc(u.Original))))
                                                 .Concat(lines.SelectMany(l => l.JapaneseInput?.Groups.SelectMany(g => g.Reading) ?? Enumerable.Empty<char>())));
            }

            // The lyric SIZE (backlog 334) is read once, like the font: a display measures its glyphs
            // at load and cannot be re-sized in place, so the slider applies from the next play.
            if (config != null)
                FontSize = config.Get<float>(TypeBeatRulesetSetting.LyricFontSize);

            lineContainer = new Container { RelativeSizeAxes = Axes.Both };

            // The map's freestyle colour (backlog 384), read once like the font: absent is the default.
            Color4 freestyleColour = FreestyleColourKey.Resolve(drawableRuleset?.Beatmap.FreestyleColour);

            for (int i = 0; i < lines.Count; i++)
            {

                var d = new LyricLineDisplay(lines[i], FontSize, lyricFont, paceBands[i], freestyleColour)

                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Alpha = 0f,
                };
                d.SetGrouping(engine.GroupingOf(lines[i]));
                displays[i] = d;
                lineContainer.Add(d);
            }

            // The checkbox applies during play without moving the lyric.
            config?.BindWith(TypeBeatRulesetSetting.ShowPaceColours, showPaceColours);
            showPaceColours.BindValueChanged(e =>
            {
                for (int i = 0; i < displays.Length; i++)
                    displays[i].SetPaceColours(e.NewValue ? paceBands[i] : null);
            }, true);

            void refreshPaceColours()
            {
                paceBands = buildPaceBands();

                for (int i = 0; i < displays.Length; i++)
                    displays[i].SetPaceColours(showPaceColours.Value ? paceBands[i] : null);
            }

            paceColourMaxChange.BindValueChanged(_ => refreshPaceColours());
            paceColourMode.BindValueChanged(_ => refreshPaceColours());
            paceColourOpacityCurve.BindValueChanged(_ => refreshPaceColours());

            // THE GROUPING SWITCH (backlog 363). The engine's AuthoredSyllablesOnly flips only when a
            // replay's headers re-select it (attaching a stored run on the watch path, or detaching
            // one), and the marks and bands drawn at load followed the grouping in force then.
            regroup = () =>
            {
                paceBands = buildPaceBands();

                for (int i = 0; i < displays.Length; i++)
                {
                    displays[i].SetGrouping(engine.GroupingOf(lines[i]));
                    displays[i].SetPaceBands(paceBands[i], showPaceColours.Value ? paceBands[i] : null);
                }
            };

            engine.AuthoredSyllablesOnlyChanged += regroup;
            // The target era switches the same way (a replay recorded before AlignSubdivisionTargets
            // flips the cells back to the legacy targets), and the bands are laid from those targets.
            engine.AlignSubdivisionTargetsChanged += regroup;
            engine.JapaneseWordTimingChanged += regroup;

            // Carets are positioned via absolute points in this stage's top-left-origin
            // local space (from ToSpaceOfOtherDrawable), so they must anchor top-left.
            playerCaret = new Caret(TypeBeatStyle.Caret, TypeBeatRulesetConfigManager.DEFAULT_CARET_SMOOTHING_MS, blinks: true)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopCentre,
                Height = FontSize,
                Alpha = 0f,
            };
            sungCaret = new Caret(TypeBeatStyle.SungAccent, TypeBeatRulesetConfigManager.DEFAULT_CARET_SMOOTHING_MS, blinks: false)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopCentre,
                Height = FontSize,
                Alpha = 0f,
            };

            // Each head is dressed from its OWN setting, so the typing caret and the map playhead can
            // be shaped apart: the two sit on the same line and are otherwise told apart only by
            // colour and blink, and their best shapes are not the same shape (the playhead's
            // Underline in particular runs near-parallel to the sung sweep rail the display draws just
            // under the glyphs). Both bind live, so either dropdown applies without a restart.
            config?.BindWith(TypeBeatRulesetSetting.CaretStyle, playerCaret.Style);
            config?.BindWith(TypeBeatRulesetSetting.CaretSmoothing, playerCaret.SmoothingHalfTime);
            config?.BindWith(TypeBeatRulesetSetting.CaretSmoothing, sungCaret.SmoothingHalfTime);
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

            // The space error dot (backlog 197) is a DISPLAY setting, so it binds straight to the
            // lyric displays here and deliberately never reaches the replay CONFIG frame the
            // playfield writes for judgement-affecting settings: nothing about a keystroke, a
            // judgement or a stored score moves with it. Live-bound like the caret styles, and fired
            // immediately so a stage built with no config (every bare test scene) starts off.
            config?.BindWith(TypeBeatRulesetSetting.UseSpaceErrorDot, spaceErrorDot);
            spaceErrorDot.BindValueChanged(e =>
            {
                foreach (var d in displays)
                    d.SetSpaceErrorDotsEnabled(e.NewValue);
            }, true);

            // The syllable markers (backlog 225) are a DISPLAY setting on exactly the same terms:
            // bound straight to the lyric displays, live, and deliberately never reaching the replay
            // CONFIG frame. The displays already exist by here, so firing immediately is what pushes
            // the setting into a line built before the binding ran.
            config?.BindWith(TypeBeatRulesetSetting.ShowSyllableMarkers, syllableMarkers);
            syllableMarkers.BindValueChanged(e =>
            {
                foreach (var d in displays)
                    d.SetSyllableMarkersEnabled(e.NewValue);
            }, true);

            config?.BindWith(TypeBeatRulesetSetting.TextPopInAmount, textPopInAmount);
            textPopInAmount.BindValueChanged(e =>
            {
                foreach (var d in displays)
                    d.SetTextPopInAmount(e.NewValue);
            }, true);

            config?.BindWith(TypeBeatRulesetSetting.ApproachBars, approachBars);
            approachBars.BindValueChanged(e =>
            {
                foreach (var d in displays)
                    d.SetApproachBarsEnabled(e.NewValue);
            }, true);

            config?.BindWith(TypeBeatRulesetSetting.JudgementIndicator, judgementIndicator);
            judgementIndicator.BindValueChanged(e =>
            {
                foreach (var d in displays)
                    d.SetJudgementIndicator(e.NewValue);
            }, true);

            config?.BindWith(TypeBeatRulesetSetting.PaceColourGradient, paceColourGradient);
            paceColourGradient.BindValueChanged(e =>
            {
                foreach (var d in displays)
                    d.SetPaceColourGradient(e.NewValue);
            }, true);

            config?.BindWith(TypeBeatRulesetSetting.SyllableFadeInEnd, syllableFadeInEnd);
            syllableFadeInEnd.BindValueChanged(e =>
            {
                foreach (var d in displays)
                    d.SetSyllableFadeInEnd(e.NewValue);
            }, true);

            config?.BindWith(TypeBeatRulesetSetting.SyllableFadeInDuration, syllableFadeInDuration);
            syllableFadeInDuration.BindValueChanged(e =>
            {
                foreach (var d in displays)
                    d.SetSyllableFadeInDuration(e.NewValue);
            }, true);

            config?.BindWith(TypeBeatRulesetSetting.TextPopIn, textPopIn);
            textPopIn.BindValueChanged(e =>
            {
                foreach (var d in displays)
                    d.SetTextPopInEnabled(e.NewValue);
            }, true);

            config?.BindWith(TypeBeatRulesetSetting.SyllableBrightness, syllableBrightness);
            syllableBrightness.BindValueChanged(e =>
            {
                foreach (var d in displays)
                    d.SetSungBrightness(e.NewValue);
            }, true);

            // The sync tint (backlog 251) is a DISPLAY setting on those same terms, and the terms
            // are the whole point of it: the tint reads the judged delta a keypress already earned,
            // so hiding it removes a readout and nothing else. It never reaches the replay CONFIG
            // frame either, which is what lets a replay re-derive identically whichever way the
            // player who recorded it had the metric set.
            config?.BindWith(TypeBeatRulesetSetting.ShowSyncMetric, syncTint);
            syncTint.BindValueChanged(e =>
            {
                foreach (var d in displays)
                    d.SetSyncTintEnabled(e.NewValue);
            }, true);

            // The timing tint is the same kind of readout, and measures on the engine's CURRENT
            // ladder, pulled at paint time, so a mod's window scale moves where the tint begins
            // exactly as it moves where a Great ends.
            config?.BindWith(TypeBeatRulesetSetting.ShowTimingTint, timingTint);
            timingTint.BindValueChanged(e =>
            {
                foreach (var d in displays)
                    d.SetTimingTintEnabled(e.NewValue, () => engine.Windows);
            }, true);

            // Its strength range applies live, both ends pushed together so a display never holds one
            // new end and one stale one.
            config?.BindWith(TypeBeatRulesetSetting.TimingTintMinStrength, timingTintMinStrength);
            config?.BindWith(TypeBeatRulesetSetting.TimingTintMaxStrength, timingTintMaxStrength);
            timingTintMinStrength.BindValueChanged(_ => pushTimingTintStrength());
            timingTintMaxStrength.BindValueChanged(_ => pushTimingTintStrength(), true);

            // And so do its two colours, stored as #rrggbb and read with the defaults as the fallback.
            config?.BindWith(TypeBeatRulesetSetting.TimingTintEarlyColour, timingTintEarlyColour);
            config?.BindWith(TypeBeatRulesetSetting.TimingTintLateColour, timingTintLateColour);
            timingTintEarlyColour.BindValueChanged(_ => pushTimingTintColours());
            timingTintLateColour.BindValueChanged(_ => pushTimingTintColours(), true);

            // Line spacing is user-adjustable and applies live: a change invalidates the laid-out
            // focus so the next Update re-runs the layout with the new gap.
            config?.BindWith(TypeBeatRulesetSetting.LineSpacing, lineSpacing);
            lineSpacing.BindValueChanged(e =>
            {
                lineGap = EffectiveRowPitch(e.NewValue, FontSize);
                if (engine.UsesJapaneseRomaji)
                    lineGap = Math.Max(lineGap, FontSize * 1.6f);
                laidOutFocus = int.MinValue;
            }, true);

            approachBar = new Box // first-word cue (50% opaque)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Colour = TypeBeatStyle.SungAccent,
                Height = approach_bar_height * sizeRatio,
                Alpha = 0f,
            };

            boundaryBar = new Box // line-boundary cue (solid; Fletcher only, see updateApproachCue)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Colour = TypeBeatStyle.SungAccent,
                Height = approach_bar_height * sizeRatio,
                Alpha = 0f,
            };

            // THE PUSH WARNING (backlog 263): the same depleting cue bar, in the opposite corner and
            // in the palette's one red, counting down the drag cutoff that is about to take the line
            // away (TypingEngine.DragCutoffAt). TopRight origin is what makes it read as the mirror of
            // the cue: the cues grow out of the start of the line the player is about to gain, this one
            // shrinks back into the end of the line the player is about to lose.
            pushBar = new Box
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopRight,
                Colour = TypeBeatStyle.ErrorChar,
                Height = approach_bar_height * sizeRatio,
                Alpha = 0f,
            };

            wrongKeyLayer = new Container { RelativeSizeAxes = Axes.Both };

            // boundaryBar after approachBar → the solid boundary cue draws on top of the
            // translucent first-word cue where they overlap.
            imeCompositionText = new OsuSpriteText
            {
                Anchor = Anchor.TopLeft,
                Origin = engine.UsesJapaneseRomaji ? Anchor.TopLeft : Anchor.BottomCentre,
                Font = TypeBeatStyle.Lyric(FontSize * (engine.UsesJapaneseRomaji ? 0.36f : 0.75f), lyricFont),
                Colour = TypeBeatStyle.UntypedChar,
                Alpha = 0f,
                ShadowColour = TypeBeatStyle.TextShadow,
                ShadowOffset = TypeBeatStyle.TEXT_SHADOW_OFFSET,
            };

            InternalChildren = new Drawable[] { lineContainer, approachBar, boundaryBar, pushBar, sungCaret, playerCaret, wrongKeyLayer, imeCompositionText };
        }

        /// <summary>
        /// Resolves the gameplay font family to a value safe to hand the lyric displays, on the
        /// order <see cref="LyricFontResolution"/> pins: the map's font (bundled file first, then
        /// family name) while "Use map fonts" is on, then the player's pick, then null (built-in).
        /// Never throwing and never failing the play: a missing or corrupt bundled file, or an
        /// unknown family, logs and falls through.
        /// </summary>
        private string? resolveLyricFont(TypeBeatRulesetConfigManager? config, LyricFontManager? fontManager)
        {
            if (fontManager == null)
                return null;

            string? playerFamily = config?.GetBindable<string>(TypeBeatRulesetSetting.LyricFont).Value;
            bool useMapFonts = config?.GetBindable<bool>(TypeBeatRulesetSetting.UseMapFonts).Value ?? true;
            var metadata = drawableRuleset?.Beatmap.BeatmapInfo.Metadata;

            string? resolved = LyricFontResolution.Resolve(playerFamily, useMapFonts, metadata?.LyricFont,
                fontManager.EnsureRegistered,
                () => registerMapFontFile(fontManager, metadata?.LyricFontFile));

            if (resolved == null && useMapFonts && !string.IsNullOrEmpty(metadata?.LyricFont))
                Logger.Log($"The map's lyric font '{metadata.LyricFont}' could not be resolved on this machine; using the built-in font.");

            return resolved;
        }

        /// <summary>
        /// Registers the map's bundled font file (an ordinary set file, like the background) from
        /// the working beatmap's file store, returning the per-file key it renders under, or null
        /// when there is no file, the set does not actually carry it, or it cannot be parsed.
        /// </summary>
        private string? registerMapFontFile(LyricFontManager fontManager, string? fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;

            var working = workingBeatmap?.Value;
            string? storagePath = working?.BeatmapSetInfo.GetPathForFile(fileName);

            if (working == null || storagePath == null)
            {
                Logger.Log($"The map names a bundled lyric font '{fileName}' that its set does not carry; falling back.");
                return null;
            }

            // Keyed by the CONTENT HASH the file is stored under rather than by family name, so two
            // maps bundling different fonts under the same declared family can never collide in the
            // shared font store, and the same file registers once across maps.
            string key = $"MapFont-{storagePath.Split('/')[^1]}";

            return fontManager.EnsureRegisteredFromStream(key, () => working.GetStream(storagePath)) ? key : null;
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

        private void pushTimingTintStrength()
        {
            foreach (var d in displays)
                d.SetTimingTintStrength(timingTintMinStrength.Value, timingTintMaxStrength.Value);
        }

        private void pushTimingTintColours()
        {
            var early = TypeBeatRulesetConfigManager.TintColour(timingTintEarlyColour.Value, TypeBeatStyle.EarlyTint);
            var late = TypeBeatRulesetConfigManager.TintColour(timingTintLateColour.Value, TypeBeatStyle.LateTint);

            foreach (var d in displays)
                d.SetTimingTintColours(early, late);
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
        /// The live IME COMPOSITION of a Polyglot play (backlog 331), drawn just above the caret while the
        /// player composes a hanzi or kanji. Display only: it judges nothing, and only the committed
        /// characters reach the engine.
        /// </summary>
        private OsuSpriteText imeCompositionText = null!;

        /// <summary>Shows <paramref name="composition"/> above the caret, or hides it when empty.</summary>
        public void SetImeComposition(string composition)
        {
            if (imeCompositionText.IsNull())
                return;

            imeCompositionText.Text = composition;
            imeCompositionText.Alpha = string.IsNullOrEmpty(composition) ? 0f : 1f;
            imeCompositionText.Position = playerCaret.Position;
            // The romaji buffer belongs below the lyric rail, leaving the kanji's furigana readable.
            if (engine.UsesJapaneseRomaji && DisplayAt(engine.ActiveLineIndex) is LyricLineDisplay active)
                imeCompositionText.Y += active.LineHeight + 16 * sizeRatio;
        }

        /// <summary>The composition currently shown (empty when none).</summary>
        public string ImeComposition => imeCompositionText.IsNotNull() && imeCompositionText.Alpha > 0 ? imeCompositionText.Text.ToString() : string.Empty;

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
                Font = TypeBeatStyle.Mono(rejected_letter_size * sizeRatio),
                Colour = TypeBeatStyle.ErrorChar,
                Text = (c == ' ' ? '_' : c).ToString(),
                Position = playerCaret.Position + new Vector2(dir * rejected_letter_offset.X, rejected_letter_offset.Y) * sizeRatio,
                ShadowColour = TypeBeatStyle.TextShadow,
                ShadowOffset = TypeBeatStyle.TEXT_SHADOW_OFFSET,
            };

            wrongKeyLayer.Add(letter);

            letter.MoveToOffset(new Vector2(dir * rejected_letter_rise.X, rejected_letter_rise.Y) * sizeRatio, 140, Easing.OutQuint)
                  .Then()
                  .MoveToOffset(new Vector2(dir * rejected_letter_fall.X, rejected_letter_fall.Y) * sizeRatio, 460, Easing.InQuad);
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

            // The stage follows the real playfield size, which can differ from the design width
            // and can change when the window is resized. Shrink only lines that exceed 90% of it.
            float lineWidth = DrawWidth * LyricLineDisplay.MAX_WIDTH_FRACTION;

            if (lineWidth > 0 && Math.Abs(availableLineWidth - lineWidth) >= 0.1f)
            {
                availableLineWidth = lineWidth;
                bool resized = false;

                foreach (var display in displays)
                    resized |= display.SetMaximumWidth(lineWidth);

                if (resized)
                {
                    laidOutFocus = int.MinValue;
                    pendingSnap = true;
                }
            }

            int active = engine.ActiveLineIndex;

            if (active >= 0 && active < displays.Length)
            {
                // Safety net in case the activation event was missed (e.g. clock scrubbing in tests).
                if (laidOutFocus != active)
                {
                    relayout(active, animate: true);
                    pendingSnap = true;
                }

                // GREY WHILE THE WINDOW IS SHUT (see TypingEngine.AwaitingEntry): a line the player
                // has been handed but may not type on yet is dimmed to the upcoming-line grey, and
                // undimmed the moment its window opens. Applied only on a change of line or of the
                // flag, so a settled line is not re-faded every frame.
                bool awaiting = engine.AwaitingEntry;

                if (awaitingDimLine != active || awaitingDimApplied != awaiting)
                {
                    awaitingDimLine = active;
                    awaitingDimApplied = awaiting;
                    displays[active].SetLineDim(awaiting ? 0.4f : 0f);
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
                // With a pinned caret that is the active line and this is exactly the old behaviour.
                // Unpinned the two come apart, which is the entire point: the sweep is the song, the
                // caret is the player, and you can watch yourself rush or drag away from it. Once the
                // song is more than one line from the focused line it is off the visible stack, so
                // the sung caret hides rather than parking at a phantom position.
                int sungLine = sungLineFor(active);
                var sd = displays[sungLine];
                SungLineIndex = sungLine;

                // Every syllable within its Great window lights under every playhead style.
                // Adjacent syllables and lines can overlap; displays repaint only on changes.
                setSungSyllable(sungLine, Time.Current);

                // Under CaretStyle.None none of this runs: the sweep fill/glow are never fed a
                // position, so they hold the zero the style change set them to, and the sung caret
                // is forced hidden below. The typing caret, approach cue and boundary bar are
                // untouched either way.
                if (!noPlayhead)
                {
                    double sung = sd.Line.SungPositionAt(Time.Current);
                    setSungSweep(sungLine, sung);
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

                // The two heads answer to different facts, and backlog 223 is where they stopped
                // sharing one boolean.
                //
                // The TYPING caret hides the moment its line is complete: there is nothing left to
                // type on it, and that absence IS the "you are done, wait for the song" signal. That
                // is deliberate and unchanged.
                //
                // The MAP PLAYHEAD is not the player's, so it must not take that term. The vocals go
                // on being sung under a finished caret, and since backlog 218 a refused roll PARKS a
                // complete caret for as long as ActivationTime(next) - FLETCHER_DRAG_GRACE_MS is
                // away, which blanked the playhead for seconds at a time while the sweep beneath it
                // kept moving. It hides only for its own three reasons: the run is over, the style
                // draws no head, or its row is off the visible stack.
                bool running = !engine.IsFinished;
                setCaretsVisible(running && !engine.IsLineComplete, running && !noPlayhead && Math.Abs(sungLine - active) <= 1);
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

                // The sung caret is hidden in a line gap, but a neighbouring syllable can
                // still be inside its early or late Great window.
                setSungSyllable(upcoming, Time.Current);
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

                setSungSyllable(-1, double.NaN);
                setCaretsVisible(false, false);
            }

            updateApproachCue();
            updatePushWarning();
            updateFlashlight();
            updateRecite();
        }

        /// <summary>
        /// Recite mod: hide every character the player has not typed yet, across the whole stack
        /// (the upcoming preview line and the pre-roll centre line included, whose cells are all
        /// Untyped and so hide under the same per-cell rule with no whole-line path of its own).
        /// No-op when the mod is off.
        ///
        /// <para>Far simpler than <see cref="updateFlashlight"/>, and deliberately so: the rule is
        /// per-cell STATE rather than a window in stream geometry, so nothing is computed here and
        /// the displays hold the flag. The reveal and re-hide of an individual cell then happen
        /// wherever its state changes, through <c>RefreshCell</c>, not from this pass.</para>
        ///
        /// <para>It also does NOT touch the sung sweep or the sung caret, which the flashlight
        /// fades out: Recite hides the words and keeps the map's playhead, so the player can still
        /// see where the vocal is.</para>
        /// </summary>
        private void updateRecite()
        {
            if (drawableRuleset?.HideUpcomingText != true)
                return;

            foreach (var d in displays)
                d.SetReciteEnabled(true);
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
        /// Shows the "get ready" signals under the upcoming line's first typeable char: a
        /// 50%-opaque bar that lands on the FIRST WORD, and - <b>under Fletcher only</b> - a solid bar
        /// that lands on the line BOUNDARY (<see cref="TypingLine.StartTime"/>), which a mapper may set
        /// earlier than the word.
        ///
        /// <para>The split is the mod's: with the caret PINNED the line is handed over at its boundary,
        /// so the boundary is a moment the player acts on and gets its own bar; with the caret unpinned
        /// an unopened line cannot be typed, so the game counts the line as starting when its FIRST WORD
        /// starts and there is one signal rather than two. Where the boundary sits at the first word the
        /// two bars coincide as one solid bar either way. Each is hidden outside its own final
        /// <see cref="approach_lead_ms"/> window (a past instant is behind the clock, so stale cues can
        /// never appear).</para>
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
                    var barPos = new Vector2(point.X, point.Y + d.LineHeight + CUE_BAR_OFFSET * sizeRatio);

                    // First-word cue (50% opaque) lands on the first word. The SOLID boundary cue lands on
                    // the line's StartTime, which a mapper may set earlier than the word - and it is
                    // FLETCHER-ONLY, because the pinned caret is the one that treats the boundary as the
                    // line's beginning: under Fletcher the line is handed over AT its boundary and the
                    // drag protection that holds it hangs off the same instant (backlog 208).
                    //
                    // A cue for the line the player is ALREADY on (the line self-activated into its own
                    // lead-in, or the pinned caret was handed it) is not a hint about a line they cannot
                    // type yet: it is the line under their caret, so it is drawn at FULL strength
                    // instead of the 50%-opaque whisper it keeps while the line is still to come.
                    float wordOpacity = upcoming == engine.ActiveLineIndex ? 1f : 0.5f;

                    bool wordShown = updateCueBar(approachBar, barPos, line.Cells[firstCell].TargetTime - Time.Current, wordOpacity);
                    bool boundaryShown = false;

                    // NOTE the flag's name is the ERA, not the mod: TRUE is the FLEXIBLE caret that is
                    // the default since backlog 208, and the mod NAMED Fletcher is the one that turns it
                    // OFF and pins the caret back to the playhead. The boundary bar belongs to the
                    // PINNED caret, so it is shown when the flag is false.
                    bool pinnedCaret = !engine.FletcherEnabled;

                    // With the caret UNPINNED a line starts when its FIRST WORD starts, so there is one
                    // signal and not two: the game does not count the line from a boundary the player
                    // cannot yet be on. The bar is zeroed rather than merely skipped, since the
                    // first-word cue above may be the one showing this frame.
                    if (pinnedCaret)
                        boundaryShown = updateCueBar(boundaryBar, barPos, line.StartTime - Time.Current, 1f);
                    else
                        boundaryBar.Alpha = 0f;

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
        /// THE PUSH WARNING (backlog 263): "you are about to be pushed to the next line". A player
        /// lagging behind on a line the song has already left keeps it only until the drag cutoff,
        /// where the engine force-seals it and lands the caret on the next line
        /// (<see cref="TypingEngine.DragCutoffAt"/>). That used to arrive with no notice at all, so the
        /// same depleting bar the cues use counts it down, RIGHT-ALIGNED at the end of the line and in
        /// the palette's one red: the opposite corner and the opposite colour from a cue, because it is
        /// the opposite message, a line about to be taken rather than a line about to be given.
        ///
        /// <para>It sits in the same band under the ACTIVE line (the one the player is on and about to
        /// lose), driven through the shared <see cref="updateCueBar"/> at full opacity, so with a
        /// <see cref="Anchor.TopRight"/> origin the width depletes leftward out of the line's right
        /// edge while the alpha ramps up.</para>
        ///
        /// <para>Its window opens when the line that is about to TAKE the player counts as starting,
        /// which is that line's FIRST WORD (see <see cref="pushWarningOpensAt"/>) and not the boundary
        /// a mapper may have set earlier, and it runs for the fixed <see cref="approach_lead_ms"/> from
        /// there. It never outlives the punishment: the push
        /// (EndTime + SealGraceMs + FLETCHER_DRAG_GRACE_MS) cuts the bar off the moment it lands, and
        /// where the word begins later than that the bar is the lead into the push instead (see
        /// <see cref="pushWarningOpensAt"/>). It fades in over <see cref="push_fade_in_ms"/> rather
        /// than snapping on.</para>
        ///
        /// <para>Display only. It reads a nullable engine readout and nothing else, so every path where
        /// no push is coming (a pinned caret, the caret rolled on ahead of an abandoned line, the run
        /// finished) falls through to the same hide below. A line the player has TYPED OUT warns as
        /// well when the manual-newline setting has parked them on it: the engine holds that line to
        /// its cutoff and hands the caret over there, so the bar counts down to a push that really is
        /// coming.</para>
        /// </summary>
        private void updatePushWarning()
        {
            int active = engine.ActiveLineIndex;

            if (engine.DragCutoffAt is double cutoff && active >= 0 && active < displays.Length)
            {
                var d = displays[active];

                // PositionOfCell(Cells.Count) is the documented end of the line, so the bar hangs off
                // the last character rather than off the caret, which is somewhere mid-line by
                // definition while the player is dragging.
                Vector2 end = d.ToSpaceOfOtherDrawable(d.PositionOfCell(d.Line.Cells.Count), this);
                var barPos = new Vector2(end.X, end.Y + d.LineHeight + CUE_BAR_OFFSET * sizeRatio);
                double opensAt = pushWarningOpensAt(active, cutoff);
                double closesAt = opensAt + approach_lead_ms;

                if (Time.Current >= opensAt && Time.Current < cutoff && Time.Current < closesAt)
                {
                    // A FIXED lead (approach_lead_ms) measured from the word, so the bar always drains
                    // at the one rate the player has learned; it is NOT stretched to fill a window
                    // that happens to be longer, and the push cuts it short rather than reshaping it.
                    // The fade-in is the one thing the cues do not have: they may snap on, a warning
                    // may not.
                    float progress = (float)((closesAt - Time.Current) / approach_lead_ms); // 1 -> 0 as it lands
                    float fadeIn = (float)Math.Clamp((Time.Current - opensAt) / push_fade_in_ms, 0d, 1d);

                    pushBar.Position = barPos;
                    pushBar.Width = approach_bar_max_width * sizeRatio * progress;
                    pushBar.Alpha = arrivalAlpha(progress) * fadeIn;
                    pushWarningTargetLine = active;
                    return;
                }
            }

            pushBar.Alpha = 0f;
            pushWarningTargetLine = -1;
        }

        /// <summary>
        /// The instant the push warning OPENS: when the line about to take the player counts as
        /// starting, measured on the SAME terms as every other line-start signal in the stage - its
        /// FIRST WORD, the first typeable cell's target, which is the instant the first-word cue lands
        /// on. The caret is unpinned wherever this runs at all (<see cref="TypingEngine.DragCutoffAt"/>
        /// is null under a pinned one), and an unpinned caret cannot type an unopened line, so the
        /// boundary a mapper may have set earlier is not a moment the player acts on and the warning
        /// must not fire on it.
        ///
        /// <para>Falls back to the old fixed <see cref="approach_lead_ms"/> lead before the cutoff when
        /// there is no next typeable line to anchor on (the map's last line, a line of pure
        /// punctuation) or when its first word begins at or after the push itself, where a
        /// word-anchored window would have no width at all and the player would get no warning rather
        /// than a short one.</para>
        /// </summary>
        private double pushWarningOpensAt(int active, double cutoff)
        {
            double fallback = cutoff - approach_lead_ms;
            int next = active + 1;

            if (next < 0 || next >= engine.Lines.Count)
                return fallback;

            int firstCell = firstTypeableIndex(engine.Lines[next]);

            if (firstCell < 0)
                return fallback;

            double wordStart = engine.Lines[next].Cells[firstCell].TargetTime;

            return wordStart < cutoff ? wordStart : fallback;
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
                bar.Width = approach_bar_max_width * sizeRatio * progress;
                bar.Alpha = arrivalAlpha(progress) * opacityScale; // brightens as it arrives
                return true;
            }

            bar.Alpha = 0f;
            return false;
        }

        /// <summary>
        /// The shared brightness ramp of every depleting bar: half strength while it is at its widest,
        /// 0.85 by the time it lands (<paramref name="progress"/> 1 -> 0), before the per-bar opacity
        /// scale. Monotonic in the bar's own time, which is what lets a test pin these bands.
        /// </summary>
        private static float arrivalAlpha(float progress) => 0.85f - 0.35f * progress;

        // Which line the approach bar is currently rendered for; -1 while hidden. Test support:
        // alpha alone cannot distinguish "cued the right line" from a bar under a later line.
        private int approachCueTargetLine = -1;

        // The same, for the push warning: which line it is warning about losing; -1 while hidden.
        private int pushWarningTargetLine = -1;

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
        /// Which line's display carries the sung sweep and sung caret: the line the SONG is on. With
        /// a pinned caret that is simply the active line, which the engine keeps identical to it.
        /// Under the unpinned caret the two come apart and this is read from the CLOCK, because the
        /// player's caret cannot say where the vocals are: it may have rushed past them or still be
        /// dragging behind them. Falls back to the focused line when everything has sealed.
        ///
        /// <para>The rule, exactly: the index the seal cursor WOULD have if drag protection did not
        /// defer the seal. It starts at <see cref="TypingEngine.NextUnsealedLineIndex"/> and walks off
        /// every line the playhead has already left, stepping on
        /// <see cref="TypingLine.EndTime"/> + <see cref="TypingLine.SealGraceMs"/>. That instant is
        /// the upper bound of <see cref="TypingEngine.SongWindowOpen"/> and is also the deadline
        /// <c>canSeal</c> uses, so while the playhead is inside the first unsealed line's window the
        /// loop does not run at all and the answer is the seal cursor's, exactly as before. It also
        /// cannot fire on an ordinary hand-over: a line with nothing left untyped seals on its own
        /// EndTime and is never drag-deferred, so the cursor has already moved before this could. The
        /// one line that can sit unsealed with nothing owed beneath the cursor is one a ManualNewlines
        /// caret is parked on, held to its cutoff - and that line's own window has closed long before
        /// the cutoff, so the walk steps over it exactly as it steps over a dragging player's line.
        /// </para>
        ///
        /// <para>Reading the cursor alone (what this did before backlog 223) can never report the row
        /// the song has moved to, because drag protection (<c>TypingEngine.sealPermitted</c>)
        /// deliberately holds the caret's own line unsealed while the player is still typing it, and
        /// the seal loop hands the caret on whenever it seals the caret's line: so the cursor is never
        /// AHEAD of the caret, and a dragging player's playhead stranded at the tail of the row they
        /// were still typing while the row actually being sung got no head, no sweep and no lit
        /// syllable. The walk is pure presentation, a read of line times against the stage's own
        /// clock; it writes no engine state and must never be turned into one, since which line may
        /// still be typed is judgement-bearing and re-derived by every stored replay.</para>
        ///
        /// <para>A multi-step walk is reachable: the seal loop stops at the first line it may not
        /// seal, so while the head of the queue is drag-deferred (up to
        /// <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> past its own deadline) the windows of
        /// short lines behind it can close too. Rows further than one from the focused line are off
        /// the visible stack, which the caller's own distance term already handles.</para>
        /// </summary>
        private int sungLineFor(int active)
        {
            if (!engine.FletcherEnabled)
                return active;

            int songLine = engine.NextUnsealedLineIndex;

            if (songLine < 0 || songLine >= displays.Length)
                return active;

            double time = Time.Current;

            // The seal cursor can be a row PAST the song as well as behind it. A player who types an
            // OVERRUN line out early seals it at its own boundary (<c>canSeal</c> lets a line owing
            // nothing go the moment its boundary passes) while the song is still singing its tail, and
            // taking the cursor at its word there would blank the very sweep that is running. Step back
            // to whatever the song has not finished, then walk forward over everything it has: the two
            // loops cannot fight, since neither moves past a line whose own song-window is still open.

            while (songLine > 0 && time < songWindowClosesAt(displays[songLine - 1].Line))
                songLine--;

            while (songLine + 1 < displays.Length && time >= songWindowClosesAt(displays[songLine].Line))
                songLine++;

            return songLine;
        }

        /// <summary>
        /// The instant the playhead leaves <paramref name="line"/>: the LATER of the line's own
        /// boundary and the moment its sweep reaches its last character
        /// (<see cref="TypingLine.SweepEndTime"/>, which runs past the boundary when the line's vocals
        /// genuinely overrun it).
        ///
        /// <para>NOT the line's typing deadline. A seal grace is time the PLAYER is still allowed to
        /// type the line in, and a line that authored one keeps the song's own line past the point the
        /// next line is already being sung; the sweep then appeared <i>on the next line</i> part way
        /// through it, as if that line had started mid-word. The song's line answers to the song's
        /// times and nothing else: it flips at the boundary that hands the row over - where the line's
        /// own sweep holds, complete, through the gap before the next line sings - and later only when
        /// something of this line is genuinely still being sung.</para>
        /// </summary>
        private static double songWindowClosesAt(TypingLine line) => Math.Max(line.EndTime, line.SweepEndTime);

        // Great windows can overlap a line split, so keep the small set of candidate displays
        // that must be cleared when the song moves on.
        private readonly HashSet<int> syllableLitLines = new HashSet<int>();

        // Which display currently carries the underline sweep; -1 = none. Same shape and same reason
        // as syllableLitLines: the fill is per-DISPLAY state that only this feeds, so a row that stops
        // being the sung row would otherwise freeze at whatever fraction it was last handed. That
        // used to be unobservable, because the row only ever changed on a seal, by which time its
        // sweep was clamped 100% full and scrolling away. Since backlog 223 the row moves off a
        // dragging player's line while they are still reading it (see sungLineFor), so the stale fill
        // would sit there claiming the vocals are still on it. Same class of bug the CaretStyle.None
        // binding in load() zeroes for.
        private int sweptLine = -1;

        /// <summary>
        /// Feed the current song line and its neighbours so each syllable can stay lit for its
        /// complete Great window across a line split. A negative line index clears all candidates.
        /// </summary>
        private void setSungSyllable(int lineIndex, double time)
        {
            // Great windows can straddle a line split. Feed both visible neighbours until
            // their own final/first syllable leaves its full window.
            int lo = lineIndex >= 0 ? Math.Max(0, lineIndex - 1) : 0;
            int hi = lineIndex >= 0 ? Math.Min(displays.Length - 1, lineIndex + 1) : -1;

            foreach (int previous in syllableLitLines)
                if (previous < lo || previous > hi)
                    displays[previous].SetSungSyllable(-1);

            syllableLitLines.Clear();

            for (int candidate = lo; candidate <= hi; candidate++)
            {
                displays[candidate].SetSungWindow(time, engine.Windows.GreatEarly, engine.Windows.GreatLate,
                    characterTiming: engine.HardRockFromMod || !engine.SyllableTiming, charTimedStretch: engine.CharTimedStretch, okEarly: engine.Windows.OkEarly,
                    freestyleWindowMultiplier: SyncWindows.FREESTYLE_WINDOW_SCALE);
                syllableLitLines.Add(candidate);
            }
        }

        /// <summary>
        /// Feed the underline sweep to the display that should carry it and zero the display that
        /// carried one before, so exactly one row is ever showing a fill. <see cref="setSungSyllable"/>'s
        /// sibling: the two ride the same sung line and must move off a row together or the lit group
        /// and the sweep will disagree. Cheap every frame (the displays repaint only on a change).
        /// </summary>
        private void setSungSweep(int lineIndex, double sung)
        {
            if (sweptLine != lineIndex && sweptLine >= 0 && sweptLine < displays.Length)
                displays[sweptLine].SetSungPosition(0);

            sweptLine = lineIndex;

            if (lineIndex >= 0 && lineIndex < displays.Length)
                displays[lineIndex].SetSungPosition(sung);
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

            if (regroup != null)
            {
                engine.AuthoredSyllablesOnlyChanged -= regroup;
                engine.AlignSubdivisionTargetsChanged -= regroup;
                engine.JapaneseWordTimingChanged -= regroup;
            }

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

        /// <summary>
        /// Whether the SOLID line-boundary cue is currently drawn, the one that lands on the line's
        /// <see cref="TypingLine.StartTime"/> rather than its first word. Fletcher-only, so this is the
        /// readout that tells a test the second "get ready" signal is present (pinned caret) or absent
        /// (the default, where a line counts as starting at its first word).
        /// </summary>
        public bool BoundaryCueVisible => boundaryBar.IsNotNull() && boundaryBar.Alpha > 0.1f;

        /// <summary>
        /// The first-word cue's current alpha, unscaled by the visibility threshold. Reads full when the
        /// cued line is the line the player is already on and half that while it is still a line to
        /// come, which is the difference a test wants to see rather than just "something is drawn".
        /// </summary>
        public float FirstWordCueAlpha => approachBar.IsNotNull() ? approachBar.Alpha : 0f;

        /// <summary>The line index the approach cue is currently shown for; -1 while hidden.</summary>
        public int ApproachCueTargetLine => approachCueTargetLine;

        /// <summary>
        /// The line the SONG is on: the one whose sweep and sung playhead are drawn. Under a pinned
        /// caret that is always the caret's own line; with the caret unpinned it is the first line the
        /// song has not left, which is what lets the two come apart. Test support.
        /// </summary>
        public int SungLineIndex { get; private set; } = -1;

        /// <summary>Whether the red push warning (backlog 263) is currently drawn.</summary>
        public bool PushWarningVisible => pushBar.IsNotNull() && pushBar.Alpha > 0.1f;

        /// <summary>
        /// The push warning's current alpha, so a test can tell the fade-in from a snap-on: the bar is
        /// weakest the frame its window opens and climbs to its full strength from there. Zero
        /// whenever the window is shut.
        /// </summary>
        public float PushWarningAlpha => pushBar.IsNotNull() ? pushBar.Alpha : 0f;

        /// <summary>
        /// Whether the push warning's COUNTDOWN is running this frame, whatever its fade-in has reached.
        /// This is the window membership a test can pin exactly: the alpha is deliberately soft on the
        /// frame the window opens, so "has it faded in yet" is a different question from "is this line
        /// being counted down".
        /// </summary>
        public bool PushWarningWindowOpen => pushWarningTargetLine >= 0;

        /// <summary>The line index the push warning is currently shown for; -1 while hidden.</summary>
        public int PushWarningTargetLine => pushWarningTargetLine;

        /// <summary>
        /// Screen-space edges of the push warning bar as it is actually DRAWN, so a test can pin the
        /// right alignment rather than the field it is configured from: the bar is anchored by its
        /// TopRight, so its right edge pins to the end of the line and the width depletes leftward out
        /// of it. Read together, since either edge alone is satisfied by the wrong origin.
        /// </summary>
        public Vector2 PushWarningScreenLeftEdge => pushBar.IsNotNull() ? pushBar.ScreenSpaceDrawQuad.TopLeft : Vector2.Zero;

        public Vector2 PushWarningScreenRightEdge => pushBar.IsNotNull() ? pushBar.ScreenSpaceDrawQuad.TopRight : Vector2.Zero;

        /// <summary>Screen-space quads of the two carets and the first-word cue bar as DRAWN; test
        /// support for the lyric size scene (backlog 334), which pins each against the text.</summary>
        public osu.Framework.Graphics.Primitives.Quad PlayerCaretScreenQuad => playerCaret.IsNotNull() ? playerCaret.ScreenSpaceDrawQuad : default;

        public osu.Framework.Graphics.Primitives.Quad SungCaretScreenQuad => sungCaret.IsNotNull() ? sungCaret.ScreenSpaceDrawQuad : default;

        public osu.Framework.Graphics.Primitives.Quad FirstWordCueScreenQuad => approachBar.IsNotNull() ? approachBar.ScreenSpaceDrawQuad : default;

        /// <summary>The row pitch the stack is currently laid out at (see <see cref="EffectiveRowPitch"/>).</summary>
        public float RowPitch => lineGap;

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
