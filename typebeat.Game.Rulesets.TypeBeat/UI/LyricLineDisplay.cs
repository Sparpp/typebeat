// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/UI/LyricLineDisplay.cs.
// SpriteText -> OsuSpriteText (fork bans bare SpriteText); constant names restyled.

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Utils;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// One line's slice of the flashlight window: the inclusive range of that line's own COUNTABLE
    /// slots (typeable, non-space) that are lit, plus whether the outermost lit char on each side
    /// should soften because darkness lies beyond it in the stream. <see cref="Lo"/> &gt;
    /// <see cref="Hi"/> means the line is fully hidden. A soft flag is only set on the side that
    /// carries the WHOLE window's outer edge; an internal line-to-line boundary (the window
    /// continues into the adjacent line) stays hard so the two lines join seamlessly.
    /// </summary>
    public readonly struct LineWindow : IEquatable<LineWindow>
    {
        public readonly int Lo;
        public readonly int Hi;
        public readonly bool SoftLeft;
        public readonly bool SoftRight;

        public LineWindow(int lo, int hi, bool softLeft, bool softRight)
        {
            Lo = lo;
            Hi = hi;
            SoftLeft = softLeft;
            SoftRight = softRight;
        }

        /// <summary>No countable char of the line is lit.</summary>
        public static LineWindow Hidden => new LineWindow(0, -1, false, false);

        public bool IsHidden => Lo > Hi;

        public bool Equals(LineWindow other) =>
            Lo == other.Lo && Hi == other.Hi && SoftLeft == other.SoftLeft && SoftRight == other.SoftRight;

        public override bool Equals(object? obj) => obj is LineWindow o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(Lo, Hi, SoftLeft, SoftRight);
    }

    /// <summary>
    /// Renders one <see cref="TypingLine"/> as per-cell <see cref="OsuSpriteText"/>s at the
    /// font's natural (proportional) advances; every glyph is measured individually, so the
    /// caret/sweep math never assumes a constant advance. Per-cell colouring, judgement
    /// feedback (Great pop / Wrong shake), and the sung-position underline sweep. State is
    /// read pull-based via <see cref="RefreshCell"/>; no engine reference is held.
    ///
    /// <para>A correctly typed char CAN be tinted by how in sync its keypress was, on a ramp between
    /// the untyped grey and the full typed off-white (see <see cref="CorrectCharColour"/>), so the
    /// trail behind the caret reads as brightness. Opt-in since backlog 251
    /// (<see cref="Configuration.TypeBeatRulesetSetting.ShowSyncMetric"/>, off by default, applied
    /// through <see cref="SetSyncTintEnabled"/>); off, every Correct char takes the flat typed
    /// off-white.</para>
    ///
    /// <para>FREESTYLE cells (see <see cref="FreestyleGlyphs"/>) render in
    /// <see cref="TypeBeatStyle.FreestyleChar"/> and, while still open, shimmer through
    /// width-matched glyphs; once filled they freeze on the char the player pressed. Their advance
    /// is measured from a pool glyph at load, so nothing about the effect can move the line.</para>
    ///
    /// <para>The sweep's TRACK carries the PACE HUE (backlog 228, see <see cref="buildPaceTracks"/>
    /// and <see cref="UnderlinePace"/>): one band per word or authored subdivision, tinted red
    /// where the map's playhead speeds up and green where it slows down. The stage supplies the
    /// colours; nothing here computes one.</para>
    ///
    /// <para>An opt-in SPACE ERROR DOT (see <see cref="ComputeSpaceErrorDots"/>) marks a wrong
    /// character typed into a space between words. It is an overlay drawable per word gap,
    /// kept out of the auto-size box like the retype selection, so the setting can never move a
    /// character; the state it reads is pulled like every other cell state.</para>
    ///
    /// <para>SYLLABLE MARKERS (backlog 225) draw a tiny apex-up triangle in the inter-character gap
    /// at each mid-word syllable boundary, so the syllable span judgement is visible before it is
    /// heard. The cells are <see cref="SyllableGrouping.MarkerCells"/> of the grouping the stage
    /// selects (<see cref="SetGrouping"/>, backlog 363): the AUTHORED one, which marks only the
    /// mapper's own subdivisions, on every live play, and the stored-era natural one while a replay
    /// recorded before that change plays back. Derived with the groups themselves; nothing about the
    /// geometry is recomputed here. On by
    /// default, and the same drawables on an active line and a preview one, so the dim ladder
    /// carries them for free (<see cref="SetLineDim"/> fades the whole content container).</para>
    /// </summary>
    public partial class LyricLineDisplay : CompositeDrawable
    {
        private const float design_width = 1366f;
        public const float MAX_WIDTH_FRACTION = 0.9f;
        private float maxLineWidth = design_width * MAX_WIDTH_FRACTION;

        public TypingLine Line { get; }

        private readonly float requestedFontSize;

        /// <summary>
        /// The requested font size as a multiple of the default (<see cref="TypeBeatStyle.LYRIC_FONT_SIZE"/>).
        /// Every pixel literal in this display that was tuned by eye against the default size (the
        /// sweep rail's drop and thickness, the glow's width, the wrong-character shake) is written as
        /// "its value at the default size" times this, so the whole row keeps its proportions at any
        /// size (backlog 334) and is byte-identical at the default, where it is exactly 1.
        /// </summary>
        public float SizeRatio => SizeRatioFor(requestedFontSize);

        /// <summary>The font size this display was built for, before the auto-shrink scale.</summary>
        public float FontSize => requestedFontSize;

        /// <summary><paramref name="fontSize"/> as a multiple of <see cref="TypeBeatStyle.LYRIC_FONT_SIZE"/>;
        /// exactly 1 at the default, so a literal multiplied by it is unchanged there.</summary>
        public static float SizeRatioFor(float fontSize) => fontSize / TypeBeatStyle.LYRIC_FONT_SIZE;

        /// <summary>Resolved gameplay-font family (null/empty = the built-in lyric font). Set at construction;
        /// the owning stage decides the value and guarantees the family is registered before it is used.</summary>
        private readonly string? fontFamily;

        /// <summary>
        /// The colour this line's FREESTYLE cells wear: the map's own pick (backlog 384,
        /// <see cref="Beatmaps.FreestyleColourKey"/>) or <see cref="TypeBeatStyle.FreestyleChar"/>.
        /// </summary>
        public Color4 FreestyleColour { get; }

        private Container content = null!;

        // --- The sung-sweep underline ---
        // The track is a solid core per pace band plus a short gradient at each interior boundary.
        // Together they tile the full-width rail; a display with no bands gets one neutral core.
        private Box[] sweepTracks = Array.Empty<Box>();
        private Box[] sweepBlends = Array.Empty<Box>();
        private PaceBand[] trackBands = Array.Empty<PaceBand>();
        private Box sweepFill = null!;
        private Box sweepGlow = null!;
        private Box selectionBox = null!;
        private OsuSpriteText[] cells = Array.Empty<OsuSpriteText>();
        private readonly List<(JapaneseInputPlan.Group Group, OsuSpriteText Text)> furigana = new List<(JapaneseInputPlan.Group, OsuSpriteText)>();
        private float[] advances = Array.Empty<float>();

        // --- Space error dots (backlog 197, on by default since PR 2) ---
        // Display indices of this line's WORD GAPS, one overlay dot per gap, and the flag buffer the
        // pure rule writes into (one entry per CELL, reused so a repaint allocates nothing). A line
        // with no gap pays for none of this. The dots are recomputed at most once per frame, off a
        // dirty flag RefreshCell sets, because the stage refreshes every visible cell every frame and
        // the rule is a whole-line read.
        private int[] gapCells = Array.Empty<int>();
        private Circle[] gapDots = Array.Empty<Circle>();
        private bool[] gapDotFlags = Array.Empty<bool>();

        /// <summary>Per-dot bounce left to run, in milliseconds (see <see cref="SpaceErrorDotPulseScale"/>).</summary>
        private float[] gapDotPulseMs = Array.Empty<float>();

        private bool spaceErrorDotsEnabled;
        private bool spaceErrorDotsDirty;

        // --- Syllable markers (backlog 225, opt-out) ---
        // One tiny apex-up triangle per MID-WORD syllable boundary, drawn in the
        // inter-character gap the boundary falls in. The cells come from the line itself
        // (TypingLine.SyllableMarkerCells), never from anything measured here, so the mark and the
        // judgement group read one derivation. Their POSITIONS are fixed for the line's whole
        // lifetime: unlike the gap dots, nothing about a keystroke can move a mark or change what it
        // means, only the alpha the setting and the hiding mods multiply out.
        private int[] markerCells = Array.Empty<int>();
        private Triangle[] syllableMarkers = Array.Empty<Triangle>();

        // Set only while Recite is on, where a mark's visibility follows the STATE of the two cells
        // it sits between and so changes with a keystroke; coalesced to at most one repaint per
        // frame for the same reason the gap dots are (the stage refreshes every visible cell).
        private bool syllableMarkersDirty;

        // Default ON, matching the shipped setting, so a display built with no stage or config
        // (every bare test scene) shows what a player sees rather than an accidental blank.
        private bool syllableMarkersEnabled = true;

        // The SYNC TINT (TypeBeatRulesetSetting.ShowSyncMetric), default OFF for the same reason the
        // flag above defaults on: it matches the shipped setting, so a display built with no stage
        // or config shows what a player sees. Off, a Correct cell takes the flat typed off-white.
        private bool syncTintEnabled;

        // --- Freestyle cells (see FreestyleGlyphs) ---
        // Display indices of this line's freestyle cells (empty for the overwhelming majority of
        // lines, which then pay nothing per frame), the width-matched glyph pool their shimmer
        // draws from, and the cell state each was last rendered for (so a state change with no
        // engine event behind it, a backspace, still repaints).
        private int[] freestyleCells = Array.Empty<int>();
        private char[] shimmerPool = Array.Empty<char>();
        private CellState[] freestyleRenderedState = Array.Empty<CellState>();
        private OsuSpriteText[] widthProbes = Array.Empty<OsuSpriteText>();
        private int shimmerTick = int.MinValue;

        /// <summary>Per-cell alpha driven purely by judgement state (Missed dims to 0.4, else 1);
        /// the flashlight window and the Recite mod each multiply on top of this, so none of the
        /// three ever clobbers another.</summary>
        private float[] cellStateAlpha = Array.Empty<float>();

        /// <summary>Content-local left edge of each cell; length = Cells.Count + 1 (last entry = end of line).</summary>
        private float[] cellX = { 0f };

        private float contentScale = 1f;
        private float glyphHeight;

        /// <summary>Reference advance (content-local px): a measured letter's width, used as the
        /// fallback for glyphs that produced no measurement. Valid after load.</summary>
        public float CharWidth { get; private set; }

        /// <summary>Content-local width of the whole line (before the auto-shrink scale).</summary>
        public float FullSweepWidth => cellX[^1];

        /// <summary>Current sung-sweep fill width in content-local px.</summary>
        public float SweepFillWidth => sweepFill.IsNotNull() ? sweepFill.Width : 0f;

        /// <summary>Effective on-screen height of a glyph row (after auto-shrink scaling).</summary>
        public float LineHeight => glyphHeight * contentScale;

        /// <summary>
        /// Limit this line to the available playfield width. Each display keeps its own scale, so
        /// a long line can shrink without making the other lines smaller.
        /// </summary>
        public bool SetMaximumWidth(float width)
        {
            if (width <= 0 || Math.Abs(maxLineWidth - width) < 0.1f)
                return false;

            maxLineWidth = width;

            if (IsLoaded)
                measureAndLayout();

            return true;
        }

        /// <summary>Effective on-screen advance of a specific cell (after auto-shrink scaling):
        /// the width a cell-covering caret style (block/outline/underline) spans there. Advances
        /// are proportional, so this varies per cell; past-the-end uses the last cell's width.</summary>
        public float CellWidthAt(int cellIndex)
        {
            if (advances.Length == 0)
                return CharWidth * contentScale;

            return advances[Math.Clamp(cellIndex, 0, advances.Length - 1)] * contentScale;
        }

        /// <summary>
        /// The same on-screen advance at a FRACTIONAL (sung) cell index: what a cell-covering caret
        /// style spans when it rides the continuous sung position rather than sitting on a discrete
        /// cell. See <see cref="AdvanceAtFraction"/> for why it interpolates.
        /// </summary>
        public float CellWidthAtFraction(double fractionalCellIndex)
        {
            if (advances.Length == 0)
                return CharWidth * contentScale;

            return AdvanceAtFraction(advances, fractionalCellIndex) * contentScale;
        }

        /// <summary>
        /// The advance covered at a fractional cell index: the two straddled cells' advances
        /// interpolated exactly the way <see cref="SungPositionPoint"/> interpolates their left edges.
        /// That pairing is the point: at every WHOLE index (each syllable's onset, where the eye
        /// actually lands) the left edge is the cell's own left edge and the width is that cell's own
        /// advance, so the shape covers precisely the character being sung; between two onsets it
        /// slides and morphs together with the underline sweep instead of jumping a whole cell.
        ///
        /// <para>Out-of-range indices clamp to the end cells, and NaN (which no comparison would
        /// catch) clamps to the first, matching <see cref="CellWidthAt"/>'s past-the-end rule: a
        /// playhead parked past the last character keeps that character's width rather than
        /// collapsing. Pure, so it is unit-testable.</para>
        /// </summary>
        public static float AdvanceAtFraction(IReadOnlyList<float> cellAdvances, double fractionalCellIndex)
        {
            int n = cellAdvances.Count;

            if (n == 0)
                return 0f;

            double f = double.IsNaN(fractionalCellIndex) ? 0 : Math.Clamp(fractionalCellIndex, 0, n - 1);
            int lo = (int)Math.Floor(f);
            int hi = Math.Min(lo + 1, n - 1);

            return cellAdvances[lo] + (cellAdvances[hi] - cellAdvances[lo]) * (float)(f - lo);
        }

        /// <summary>Index into the shown grouping's <see cref="SyllableGrouping.Groups"/> of the group currently being sung,
        /// -1 = none. Stage-fed (see <see cref="SetSungSyllable"/>); time-driven state, so it lives
        /// beside the sung sweep rather than in the pull-based cell states.</summary>
        private int sungSyllable = -1;
        private readonly bool[] litCells;
        private readonly float[] litAmounts;
        private bool syllableFadeInEnabled;
        private readonly bool[] popInApplied;
        private bool textPopInEnabled;
        private float textPopInAmount = TypeBeatRulesetConfigManager.DEFAULT_TEXT_POP_IN_AMOUNT;
        private double sungTime = double.NaN;
        private double sungGreatEarly;
        private double sungGreatLate;
        private double sungOkEarly;
        private bool characterTiming;
        private bool charTimedStretch;
        private Box layoutBounds = null!;

        public const float TEXT_POP_IN_MIN_SCALE = 1 - TypeBeatRulesetConfigManager.DEFAULT_TEXT_POP_IN_AMOUNT / 100f;
        public const double TEXT_POP_IN_DURATION_MS = 140;
        private float sungBrightness = 50f;

        /// <summary>
        /// This line's pace-band geometry and initial relative colours. The owning stage computes
        /// the colours and can select another mode without moving the bands.
        /// </summary>
        private IReadOnlyList<PaceBand>? paceBands;
        private IReadOnlyList<PaceBand>? selectedPaceBands;

        /// <summary>
        /// The syllable grouping the marks, the sung highlight and the stretch reading follow
        /// (backlog 363): <see cref="TypingLine.AuthoredGrouping"/> until the stage says otherwise.
        /// </summary>
        private SyllableGrouping grouping;

        public LyricLineDisplay(TypingLine line, float fontSize = TypeBeatStyle.LYRIC_FONT_SIZE, string? fontFamily = null,
                                IReadOnlyList<PaceBand>? paceBands = null, Color4? freestyleColour = null)
        {
            FreestyleColour = freestyleColour ?? TypeBeatStyle.FreestyleChar;
            Line = line;
            litCells = new bool[line.Cells.Count];
            litAmounts = new float[line.Cells.Count];
            popInApplied = new bool[line.Cells.Count];
            requestedFontSize = fontSize;
            this.fontFamily = fontFamily;
            this.paceBands = paceBands;
            selectedPaceBands = paceBands;
            grouping = line.AuthoredGrouping;
            AutoSizeAxes = Axes.Both;
        }

        /// <summary>The syllable grouping this display currently shows (see <see cref="SetGrouping"/>).</summary>
        public SyllableGrouping Grouping => grouping;

        /// <summary>
        /// Show <paramref name="selected"/> (one of <see cref="Line"/>'s two groupings, backlog 363)
        /// instead of the current one: the marks are re-laid and the sung highlight is cleared for the
        /// stage to light again from the new groups on its next feed. A no-op for the grouping already
        /// shown, and safe before load, where it simply decides what load builds.
        /// </summary>
        public void SetGrouping(SyllableGrouping selected)
        {
            if (ReferenceEquals(grouping, selected))
                return;

            grouping = selected;

            if (!IsLoaded)
                return;

            foreach (var marker in syllableMarkers)
                content.Remove(marker, disposeImmediately: true);

            addSyllableMarkers(cells.Length);
            measureAndLayout();

            sungSyllable = -1;

            for (int i = 0; i < litCells.Length; i++)
                setCellLit(i, false);
        }

        /// <summary>
        /// Replace the pace-band GEOMETRY (<paramref name="bands"/>) and the colours currently shown
        /// (<paramref name="selected"/>, null for the neutral rail). Only the stage's grouping switch
        /// calls this (backlog 363): the bands are cut at the marks, so a different grouping cuts the
        /// rail differently. Safe before load.
        /// </summary>
        public void SetPaceBands(IReadOnlyList<PaceBand>? bands, IReadOnlyList<PaceBand>? selected)
        {
            paceBands = bands;
            selectedPaceBands = selected;

            if (!IsLoaded)
                return;

            // The flashlight seams fade every band together, so the new bands take the alpha the
            // old ones were showing rather than popping back to full.
            float alpha = sweepTracks.Length > 0 ? sweepTracks[0].Alpha : 1f;

            foreach (var track in sweepTracks)
                content.Remove(track, disposeImmediately: true);

            foreach (var blend in sweepBlends)
                content.Remove(blend, disposeImmediately: true);

            buildPaceTracks(cells.Length);

            // Rebuilt tracks go to the BACK, where the load put the originals (they were the first
            // drawables added): a positive depth sorts behind every depth-0 sibling.
            foreach (var track in sweepTracks)
            {
                track.Depth = 1;
                track.Alpha = alpha;
                content.Add(track);
            }

            foreach (var blend in sweepBlends)
            {
                blend.Depth = 1;
                blend.Alpha = alpha;
                content.Add(blend);
            }

            measureAndLayout();
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            int n = Line.Cells.Count;
            cells = new OsuSpriteText[n];
            cellStateAlpha = new float[n];
            Array.Fill(cellStateAlpha, 1f);

            content = new Container { AutoSizeAxes = Axes.Both };
            // Glyphs may shrink, but their slots and the line's centre must stay fixed.
            content.Add(layoutBounds = new Box { Alpha = 0, AlwaysPresent = true });

            buildPaceTracks(n);

            sweepFill = new Box
            {
                Colour = TypeBeatStyle.SungAccent.Opacity(0.60f),
                Height = SWEEP_RAIL_HEIGHT * SizeRatio,
                Width = 0,
            };
            sweepGlow = new Box
            {
                Colour = TypeBeatStyle.SungAccent,
                Height = SWEEP_RAIL_HEIGHT * SizeRatio,
                Width = SWEEP_GLOW_WIDTH * SizeRatio,
                Origin = Anchor.TopCentre,
                Alpha = 0,
            };

            // The retype selection (backlog 182). Added BEFORE the glyphs so it is drawn behind
            // them, and left out of the auto-size box (alpha 0, no AlwaysPresent) so showing it can
            // never move the line: its extent is inside the glyph row's anyway.
            selectionBox = new Box
            {
                Colour = TypeBeatStyle.Selection,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Alpha = 0f,
            };

            foreach (var track in sweepTracks)
                content.Add(track);

            foreach (var blend in sweepBlends)
                content.Add(blend);

            content.Add(sweepFill);
            content.Add(sweepGlow);
            content.Add(selectionBox);

            addSpaceErrorDots(n);
            addSyllableMarkers(n);

            var freestyle = new List<int>();

            for (int i = 0; i < n; i++)
            {
                if (Line.Cells[i].IsFreestyle)
                    freestyle.Add(i);
            }

            freestyleCells = freestyle.ToArray();
            freestyleRenderedState = new CellState[freestyleCells.Length];

            if (freestyleCells.Length > 0)
                addWidthProbes();

            for (int i = 0; i < n; i++)
            {
                var cell = new OsuSpriteText
                {
                    Font = TypeBeatStyle.Lyric(requestedFontSize, fontFamily),
                    Text = InitialGlyph(Line, i),
                    Colour = TypeBeatStyle.UntypedChar,
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.Centre,
                    // Always present for layout: a cell the flashlight hides (alpha 0) must still
                    // occupy its slot in the auto-size box, or the line would collapse and re-centre
                    // onto whatever run is currently lit, snapping the whole line sideways when the
                    // window slides or the line activates. Alpha 0 still draws nothing.
                    AlwaysPresent = true,
                    // Drop shadow (OsuSpriteText enables Shadow by default, but faintly): darken it
                    // so glyphs stay legible over a beatmap video/image, not just the flat panel.
                    ShadowColour = TypeBeatStyle.TextShadow,
                    ShadowOffset = TypeBeatStyle.TEXT_SHADOW_OFFSET,
                    // In FRONT of every adornment, including the syllable marks SetGrouping re-adds
                    // after load (backlog 363), which would otherwise sort after the glyphs.
                    Depth = -1,
                };
                cells[i] = cell;
                content.Add(cell);
            }

            foreach (var group in Line.JapaneseInput?.Groups ?? Array.Empty<JapaneseInputPlan.Group>())
            {
                if (!group.Furigana)
                    continue;
                var ruby = new OsuSpriteText
                {
                    Text = group.Reading,
                    Font = TypeBeatStyle.Lyric(requestedFontSize * 0.36f, fontFamily),
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.BottomCentre,
                    Colour = TypeBeatStyle.UntypedChar,
                    AlwaysPresent = true,
                    ShadowColour = TypeBeatStyle.TextShadow,
                    ShadowOffset = TypeBeatStyle.TEXT_SHADOW_OFFSET,
                    Depth = -1,
                };
                furigana.Add((group, ruby));
                content.Add(ruby);
            }

            InternalChild = content;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Before measuring: a freestyle cell must already carry a pool glyph, so the advance
            // the layout records for it is the advance every substituted glyph will have.
            resolveShimmerPool();

            measureAndLayout();

            for (int i = 0; i < cells.Length; i++)
                RefreshCell(i);

            SetSungPosition(0);
        }

        /// <summary>
        /// The sung-sweep TRACK, as one Box per PACE BAND (backlog 228): the faint rail under the
        /// glyphs, cut at word and authored-subdivision boundaries so each slice can wear its
        /// selected pace colour (see <see cref="UnderlinePace"/>). Built here at load, the way the
        /// space error dots are, and sized/positioned by <see cref="measureAndLayout"/> off the same
        /// measured cell edges; the owning stage supplies the colours and can change them live.
        ///
        /// <para>Three properties of the OLD single track are preserved deliberately, because
        /// dropping any of them breaks something that is not about colour:</para>
        /// <list type="bullet">
        /// <item>The cores and blends TILE the line, with no hole at the start or the end, so their union is
        /// still exactly the full-width rail.</item>
        /// <item>Every band is <c>AlwaysPresent</c>, so the auto-size container keeps its width and
        /// its lower vertical extent even when the flashlight has faded the whole rail to alpha 0.
        /// Without that the line collapses onto whatever is currently lit and snaps sideways.</item>
        /// <item>They are children of <c>content</c>, which is what makes a preview line's rail dim
        /// with its text (see <see cref="SetLineDim"/>) and ride the auto-shrink scale for free.</item>
        /// </list>
        ///
        /// <para>A display given no bands (a bare test scene, or any caller that does not want the
        /// hue) gets ONE band covering the whole line in <see cref="UnderlinePace.NeutralColour"/>,
        /// which is the pre-228 rail exactly.</para>
        ///
        /// <para>The hue sits on the TRACK and not on the fill, so on the active line it is visible
        /// on the part of the line the song has NOT reached yet: <see cref="sweepFill"/> paints over
        /// the rail from the left edge to the sung position. That is the intended reading, and it is
        /// what the feature is for, showing the player what is COMING.</para>
        /// </summary>
        private void buildPaceTracks(int n)
        {
            var bands = new List<PaceBand>();

            if (paceBands != null)
            {
                foreach (var band in paceBands)
                {
                    // Clamped rather than trusted: a stale band list can only ever under-paint.
                    int lo = Math.Clamp(band.StartCell, 0, n);
                    int hi = Math.Clamp(band.EndCellExclusive, lo, n);

                    if (hi > lo)
                        bands.Add(new PaceBand(lo, hi, band.Colour));
                }
            }

            if (bands.Count == 0)
                bands.Add(new PaceBand(0, n, UnderlinePace.NeutralColour));

            trackBands = bands.ToArray();
            sweepTracks = new Box[trackBands.Length];
            sweepBlends = new Box[Math.Max(0, trackBands.Length - 1)];

            for (int k = 0; k < trackBands.Length; k++)
            {
                sweepTracks[k] = new Box
                {
                    Colour = trackBands[k].Colour,
                    Height = SWEEP_RAIL_HEIGHT * SizeRatio,
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.TopLeft,
                    AlwaysPresent = true,
                };
            }

            for (int k = 0; k < sweepBlends.Length; k++)
            {
                sweepBlends[k] = new Box
                {
                    Height = 3,
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.TopLeft,
                    AlwaysPresent = true,
                };
            }

            applyPaceColours();
        }

        /// <summary>
        /// Change the pace colours without rebuilding the underline. All modes use the same band
        /// boundaries; null selects the plain neutral rail. Safe before the display has loaded.
        /// </summary>
        public void SetPaceColours(IReadOnlyList<PaceBand>? bands)
        {
            selectedPaceBands = bands;
            applyPaceColours();
        }

        private void applyPaceColours()
        {
            bool coloured = selectedPaceBands?.Count == sweepTracks.Length;

            for (int i = 0; i < sweepTracks.Length; i++)
                sweepTracks[i].Colour = coloured
                    ? selectedPaceBands![i].Colour
                    : UnderlinePace.NeutralColour;

            for (int i = 0; i < sweepBlends.Length; i++)
                sweepBlends[i].Colour = ColourInfo.GradientHorizontal(
                    coloured ? selectedPaceBands![i].Colour : UnderlinePace.NeutralColour,
                    coloured ? selectedPaceBands![i + 1].Colour : UnderlinePace.NeutralColour);
        }

        /// <summary>
        /// One overlay dot per WORD GAP of this line (backlog 197). Added BEFORE the glyphs, exactly
        /// as <see cref="selectionBox"/> is and for the same two reasons: it draws behind them (a gap
        /// carrying a typo glyph is never dotted anyway, see
        /// <see cref="ComputeSpaceErrorDots"/>), and it is left out of the auto-size box (alpha 0, no
        /// <c>AlwaysPresent</c>), so turning the setting on can never move a character. Position and
        /// size are written by <see cref="measureAndLayout"/>, off the same measured advances the
        /// glyphs are placed from.
        /// </summary>
        private void addSpaceErrorDots(int n)
        {
            var gaps = new List<int>();

            for (int i = 0; i < n; i++)
            {
                if (IsWordGap(Line.Cells[i]))
                    gaps.Add(i);
            }

            gapCells = gaps.ToArray();
            gapDots = new Circle[gapCells.Length];
            gapDotFlags = new bool[n];
            gapDotPulseMs = new float[gapCells.Length];

            for (int k = 0; k < gapCells.Length; k++)
            {
                var dot = new Circle
                {
                    Colour = TypeBeatStyle.ErrorChar,
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.Centre,
                    Alpha = 0f,
                };

                gapDots[k] = dot;
                content.Add(dot);
            }
        }

        /// <summary>
        /// One triangle per MID-WORD syllable boundary of this line (backlog 225), added BEFORE the
        /// glyphs so it draws behind them, exactly as the gap dots and the retype selection are.
        /// Position and size are written by <see cref="measureAndLayout"/>.
        ///
        /// <para>Kept out of the auto-size box by <c>BypassAutoSizeAxes</c> rather than by alpha,
        /// which is where this parts company with the two adornments above. Theirs is enough for
        /// them because a dot sits INSIDE a cell's own slot and can never reach past the line's
        /// extent whatever its alpha; a marker sits ON a cell EDGE, half of it to either side, so
        /// the leftmost one would poke out of the box's left edge and shift the centred line by a
        /// pixel or two the moment it lit. Bypassing makes "turning this on moves no character"
        /// structural instead of a property of the numbers.</para>
        ///
        /// <para>A line with no mid-word syllable boundary allocates nothing and does no
        /// per-frame work at all.</para>
        /// </summary>
        private void addSyllableMarkers(int n)
        {
            var marks = new List<int>();

            foreach (int i in grouping.MarkerCells)
            {
                // Defensive: a mark is only ever drawable in a real gap, never at the line's own
                // leading edge (cellX[0]) and never past its last cell.
                if (i > 0 && i < n)
                    marks.Add(i);
            }

            markerCells = marks.ToArray();
            syllableMarkers = new Triangle[markerCells.Length];

            for (int k = 0; k < markerCells.Length; k++)
            {
                var marker = new Triangle
                {
                    Colour = TypeBeatStyle.UntypedChar,
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.TopCentre,
                    BypassAutoSizeAxes = Axes.Both,
                    Alpha = 0f,
                };

                syllableMarkers[k] = marker;
                content.Add(marker);
            }
        }

        /// <summary>
        /// Hidden one-glyph sprites, one per shimmer candidate, added purely so their advance can be
        /// measured in this display's real font at its real size. Alpha 0 without AlwaysPresent, so
        /// they never count towards the auto-size box; removed the moment they have been read.
        /// </summary>
        private void addWidthProbes()
        {
            widthProbes = new OsuSpriteText[FreestyleGlyphs.CANDIDATES.Length];

            for (int i = 0; i < FreestyleGlyphs.CANDIDATES.Length; i++)
            {
                var probe = new OsuSpriteText
                {
                    Font = TypeBeatStyle.Lyric(requestedFontSize, fontFamily),
                    Text = FreestyleGlyphs.CANDIDATES[i].ToString(),
                    Alpha = 0f,
                };

                widthProbes[i] = probe;
                content.Add(probe);
            }
        }

        private void resolveShimmerPool()
        {
            if (freestyleCells.Length == 0)
                return;

            var widths = new Dictionary<char, float>(widthProbes.Length);

            for (int i = 0; i < widthProbes.Length; i++)
                widths[FreestyleGlyphs.CANDIDATES[i]] = widthProbes[i].DrawWidth;

            shimmerPool = FreestyleGlyphs.BuildPool(c => widths.TryGetValue(c, out float w) ? w : null);

            foreach (var probe in widthProbes)
                content.Remove(probe, disposeImmediately: true);

            widthProbes = Array.Empty<OsuSpriteText>();

            // Seed every freestyle cell with a pool glyph so the layout below measures the shimmer
            // width, not the width of the authoring marker (which is never rendered).
            shimmerTick = FreestyleGlyphs.TickFor(Time.Current);

            foreach (int i in freestyleCells)
                cells[i].Text = FreestyleGlyphs.Glyph(shimmerPool, shimmerTick, i).ToString();
        }

        protected override void Update()
        {
            base.Update();

            // A resized window changes the width the line has to fit in; re-fit (a scale change
            // only, the measured layout stands) so the stage reads the new geometry this frame.
            if (IsLoaded && Math.Abs(availableWidth - fittedWidth) > 0.5f)
                applyFit();

            // Once per frame at most, and only when a cell actually repainted: the stage refreshes
            // every visible cell every frame, and the dot rule is a whole-line read, so doing it
            // inside RefreshCell would make it quadratic for nothing.
            if (spaceErrorDotsDirty)
            {
                spaceErrorDotsDirty = false;
                applySpaceErrorDots();
            }

            // The dots' bounce, run down every frame whether or not anything repainted: a pulse is a
            // clock, not a state change, and the dot that is swelling is not the one being edited.
            if (gapDots.Length > 0)
            {
                float elapsedMs = (float)Time.Elapsed;

                for (int k = 0; k < gapDots.Length; k++)
                {
                    if (gapDotPulseMs[k] <= 0)
                    {
                        if (gapDots[k].Scale.X != 1f)
                            gapDots[k].Scale = Vector2.One;

                        continue;
                    }

                    gapDotPulseMs[k] = Math.Max(0, gapDotPulseMs[k] - elapsedMs);
                    gapDots[k].Scale = new Vector2(SpaceErrorDotPulseScale(SPACE_ERROR_DOT_PULSE_MS - gapDotPulseMs[k]));
                }
            }

            if (syllableMarkersDirty)
            {
                syllableMarkersDirty = false;
                applySyllableMarkers(animate: true);
            }

            if (freestyleCells.Length == 0)
                return;

            int tick = FreestyleGlyphs.TickFor(Time.Current);
            bool advanced = tick != shimmerTick;
            shimmerTick = tick;

            for (int k = 0; k < freestyleCells.Length; k++)
            {
                int i = freestyleCells[k];
                var source = Line.Cells[i];

                // Pull-based repaint: backspace mutates cell state without an engine event, so the
                // freestyle cells watch their own state rather than trusting RefreshCell to be called.
                if (freestyleRenderedState[k] != source.State)
                {
                    RefreshCell(i);
                    continue;
                }

                if (advanced && source.State == CellState.Untyped)
                    cells[i].Text = FreestyleGlyphs.Glyph(shimmerPool, tick, i).ToString();
            }
        }

        private void measureAndLayout()
        {
            int n = cells.Length;

            // Reference advance from a loaded non-space glyph; fall back to an estimate
            // if the layout has not produced a width yet.
            float refAdvance = 0f;

            for (int i = 0; i < n; i++)
            {
                if (Line.Cells[i].IsTypeable && Line.Cells[i].Expected != ' ' && cells[i].DrawWidth > 0.1f)
                {
                    refAdvance = cells[i].DrawWidth;
                    break;
                }
            }

            if (refAdvance <= 0.1f)
                refAdvance = requestedFontSize * 0.6f;

            glyphHeight = 0f;

            for (int i = 0; i < n; i++)
                glyphHeight = Math.Max(glyphHeight, cells[i].DrawHeight);

            if (glyphHeight <= 0.1f)
                glyphHeight = requestedFontSize;

            CharWidth = refAdvance;
            cellX = new float[n + 1];
            advances = new float[Math.Max(n, 1)];

            // Natural proportional layout: every cell advances by its own measured glyph width.
            // A glyph that yielded no measurement (unloaded, or a space on fonts whose lone-space
            // SpriteText measures empty) falls back to a sensible estimate.
            float x = 0f;

            for (int i = 0; i < n; i++)
            {
                int block = Line.JamoBlockHead(i);

                // A hangul block (Polyglot, backlog 331) is ONE glyph drawn on its first key's cell; the
                // keys after it take no room of their own.
                float a = block >= 0 && block != i
                    ? 0f
                    : cells[i].DrawWidth > 0.1f
                        ? cells[i].DrawWidth
                        : Line.Cells[i].Expected == ' ' ? refAdvance * 0.55f : refAdvance;
                cellX[i] = x;
                advances[i] = a;
                x += a;
            }

            cellX[n] = x;
            layoutBounds.Size = new Vector2(x, glyphHeight);


            applyFit();


            for (int i = 0; i < n; i++)
                cells[i].Position = new Vector2(cellX[i] + advances[i] * 0.5f, glyphHeight * 0.5f);

            foreach (var (group, ruby) in furigana)
            {
                float left = cellX[group.StartCell];
                float right = cellX[group.EndCellExclusive];
                ruby.Position = new Vector2((left + right) * 0.5f, -2 * SizeRatio);
                ruby.Scale = new Vector2(ruby.DrawWidth > 0 ? Math.Min(1f, (right - left) / ruby.DrawWidth) : 1f);
            }

            // The gap dots ride the same content-local coordinates the glyphs do (the auto-shrink
            // scale is on the shared container, so neither multiplies it in): centred in the gap
            // cell's own slot, on the glyph row's midline.
            float dotSize = Math.Max(1f, glyphHeight * SPACE_ERROR_DOT_RADIUS * 2f);

            for (int k = 0; k < gapCells.Length; k++)
            {
                int i = gapCells[k];
                gapDots[k].Size = new Vector2(dotSize);
                gapDots[k].Position = new Vector2(cellX[i] + advances[i] * 0.5f, glyphHeight * 0.5f);
            }


            // Solid cores and short gradients meet without overlap. Each gradient straddles one
            // band boundary, so adjacent hues ease together while most of each band stays solid.
            float railOffset = SWEEP_RAIL_OFFSET * SizeRatio;
            float railY = glyphHeight + railOffset;
            var blendWidths = new float[sweepBlends.Length];

            for (int k = 0; k < sweepBlends.Length; k++)
            {
                float boundary = cellX[Math.Clamp(trackBands[k].EndCellExclusive, 0, n)];
                float leftWidth = boundary - cellX[Math.Clamp(trackBands[k].StartCell, 0, n)];
                float rightWidth = cellX[Math.Clamp(trackBands[k + 1].EndCellExclusive, 0, n)] - boundary;
                float width = Math.Max(0, Math.Min(10f, Math.Min(leftWidth, rightWidth) * 0.25f));

                blendWidths[k] = width;
                sweepBlends[k].X = boundary - width * 0.5f;
                sweepBlends[k].Width = width;
                sweepBlends[k].Y = railY;
            }


            for (int k = 0; k < sweepTracks.Length; k++)
            {
                float left = cellX[Math.Clamp(trackBands[k].StartCell, 0, n)];
                float right = cellX[Math.Clamp(trackBands[k].EndCellExclusive, 0, n)];
                float coreLeft = left + (k > 0 ? blendWidths[k - 1] * 0.5f : 0);
                float coreRight = right - (k < blendWidths.Length ? blendWidths[k] * 0.5f : 0);

                sweepTracks[k].X = coreLeft;
                sweepTracks[k].Width = Math.Max(0f, coreRight - coreLeft);
                sweepTracks[k].Y = railY;
            }

            sweepFill.Y = sweepGlow.Y = railY;

            // The syllable markers ride the same coordinates: X is the cell's LEFT EDGE, which is
            // the inter-character gap the boundary falls in (the marker is drawn Origin.TopCentre,
            // so the gap is its axis), and the vertical band is the one geometry rule below. Placed
            // after the rail so the two read together: the marks live in the gap above it.
            var markerGeometry = SyllableMarkerGeometry(glyphHeight, railOffset);

            for (int k = 0; k < markerCells.Length; k++)
            {
                syllableMarkers[k].Size = new Vector2(markerGeometry.Width, markerGeometry.Height);
                syllableMarkers[k].Position = new Vector2(cellX[markerCells[k]], markerGeometry.Top);
            }

            applySyllableMarkers(animate: false);
        }

        /// <summary>
        /// The width the line has to fit in: the design width, or the width the stage actually gives
        /// the stack when that is narrower (a 4:3 window, or a test browser with a side panel).
        /// </summary>
        private float availableWidth => Parent is Drawable parent && parent.DrawWidth > 0 ? Math.Min(design_width, parent.DrawWidth) : design_width;

        /// <summary>The <see cref="availableWidth"/> the current <see cref="contentScale"/> was fitted to.</summary>
        private float fittedWidth = -1f;

        /// <summary>
        /// Auto-shrink guard: keep the rendered line within 90% of the available width by scaling the
        /// whole content down, never clipping it. It bounds the WIDTH whatever size the line was
        /// requested at, so a long line at the largest lyric size (backlog 334) shrinks exactly as far
        /// as it has to. Until backlog 334 the bound was the fixed design width alone, which a stack
        /// narrower than 1366 (any 4:3 window) overflowed at every size; the design width is still the
        /// ceiling, so a 16:9 window lays out exactly as before.
        /// </summary>
        private void applyFit()
        {
            fittedWidth = availableWidth;

            float total = cellX[^1];
            float maxWidth = fittedWidth * MAX_WIDTH_FRACTION;
            contentScale = total > maxWidth && total > 0f ? maxWidth / total : 1f;
            content.Scale = new Vector2(contentScale);
        }

        /// <summary>
        /// Content-local geometry of a syllable marker for a given glyph row height: the TOP edge it
        /// hangs from and the size of its bounding box (a <see cref="Triangle"/> fills that box with
        /// its apex at the top-centre, so the mark points UP at the gap it belongs to).
        ///
        /// <para>It lives in the BAND between the bottom of the glyph row (<paramref name="glyphHeight"/>)
        /// and the sung sweep rail at <c>glyphHeight + <see cref="SWEEP_RAIL_OFFSET"/></c>: hung from
        /// the glyph row so it hugs the baseline and reads as typography rather than as a widget,
        /// and clamped to leave a clear pixel above the rail so the two marks never touch. The size
        /// scales off the glyph height (like <see cref="SPACE_ERROR_DOT_RADIUS"/>) so it tracks the
        /// font size and the auto-shrink for free. The band scales with the FONT SIZE (the display
        /// passes <see cref="SWEEP_RAIL_OFFSET"/> times its <see cref="SizeRatio"/>, backlog 334) but
        /// not with the glyph row the font happens to measure, so the clamp can still bind for a face
        /// whose glyph row is tall for its size.</para>
        ///
        /// <para>Pure and static so the band can be pinned without standing up a drawable.
        /// <paramref name="railOffset"/> defaults to the default-size drop.</para>
        /// </summary>
        /// <param name="glyphHeight">The glyph row's height; the mark hangs from its bottom.</param>
        /// <param name="railOffset">The band below the row the mark must stay inside (gameplay: the sweep rail's offset).</param>
        /// <param name="heightFraction">The mark's height as a fraction of the glyph row. Gameplay keeps
        /// <see cref="SYLLABLE_MARKER_HEIGHT"/>; the editor's line list passes its own larger fraction
        /// (<see cref="Edit.LineListPanel.LineRow.REST_MARKER_HEIGHT"/>), since its 15 px rows made the gameplay
        /// fraction a one-pixel sliver nobody could see (owner, 2026-10-03).</param>
        public static (float Top, float Width, float Height) SyllableMarkerGeometry(float glyphHeight, float railOffset = SWEEP_RAIL_OFFSET, float heightFraction = SYLLABLE_MARKER_HEIGHT)
        {
            float height = Math.Clamp(glyphHeight * heightFraction, 1f, Math.Max(1f, railOffset - 1f));
            return (glyphHeight, height * SYLLABLE_MARKER_ASPECT, height);
        }

        /// <summary>Display-local caret anchor for a cell; <c>cellIndex == Cells.Count</c> is the end of the line.</summary>
        public Vector2 PositionOfCell(int cellIndex)
        {
            int i = Math.Clamp(cellIndex, 0, cellX.Length - 1);
            return new Vector2(cellX[i] * contentScale, 0f);
        }

        /// <summary>Display-local point for a fractional (sung) cell index.</summary>
        public Vector2 SungPositionPoint(double fractionalCellIndex) => new Vector2(localXFor(fractionalCellIndex) * contentScale, 0f);

        /// <summary>
        /// Paint (or, for an empty range, clear) the RETYPE SELECTION over the half-open cell range
        /// [<paramref name="startCell"/>, <paramref name="endCellExclusive"/>): the cells a Ctrl+A
        /// has offered to erase and retype (backlog 182, see
        /// <see cref="Gameplay.TypingEngine.RetypeSelectionAnchor"/>). Purely a highlight; the cells
        /// themselves keep the colours and glyphs their own states give them, because the selection
        /// says "these are about to go", not "these are wrong".
        ///
        /// <para>Driven by the playfield, which owns the gesture, rather than pulled from cell state
        /// like everything else here: a selection is not a property of any cell, it is a thing the
        /// player is holding open between two keystrokes. The range is clamped, so a stale one can
        /// only ever under-paint, never throw.</para>
        /// </summary>
        public void SetSelection(int startCell, int endCellExclusive)
        {
            if (selectionBox.IsNull())
                return;

            int lo = Math.Clamp(startCell, 0, cellX.Length - 1);
            int hi = Math.Clamp(endCellExclusive, lo, cellX.Length - 1);

            SelectionStart = lo;
            SelectionEnd = hi;

            float width = cellX[hi] - cellX[lo];

            selectionBox.X = cellX[lo];
            selectionBox.Width = width;
            selectionBox.Height = glyphHeight;
            selectionBox.Alpha = width > 0f ? 1f : 0f;
        }

        private float localXFor(double fractionalCellIndex)
        {
            double f = Math.Clamp(fractionalCellIndex, 0, cellX.Length - 1);
            int lo = (int)Math.Floor(f);
            int hi = Math.Min(lo + 1, cellX.Length - 1);
            float frac = (float)(f - lo);
            return cellX[lo] + (cellX[hi] - cellX[lo]) * frac;
        }

        /// <summary>
        /// Floor of the grey-to-white ramp a CORRECT character is painted on: how far from
        /// <see cref="TypeBeatStyle.UntypedChar"/> towards <see cref="TypeBeatStyle.TypedChar"/> the
        /// very worst correct keypress still gets. It cannot be 0.
        /// <see cref="SyncWindows.SyncQuality"/> returns exactly 0 at the Meh-window edges and stays
        /// there beyond them, and a Premature/Lagging press still lands the cell
        /// <see cref="CellState.Correct"/>, so an unfloored ramp would paint a character the player
        /// DID type in precisely the untyped grey, making it indistinguishable from one they have not
        /// reached yet. That is a legibility regression, not feedback.
        ///
        /// <para>0.35 puts the floor colour at roughly #969692 (the ramp is walked in LINEAR light,
        /// which is where the framework interpolates colour, so the floor sits higher in sRGB terms
        /// than a naive 35% of the hex range would suggest). That is about 1.95:1 against the untyped
        /// grey and about 2.9:1 against a Missed cell (the untyped grey at alpha 0.4 over the
        /// serika-dark panel). The tighter of those two, floor vs untyped, is a clearly wider step
        /// than the ~1.5:1 separating untyped from Missed, a distinction the game already ships and
        /// asks players to read, so the worst correct char is comfortably more separable from an
        /// untyped one than two states already in use, while two thirds of the ramp are left to carry
        /// the actual sync signal. Pinned by <c>SyncTintTest</c>.</para>
        /// </summary>
        public const double SYNC_TINT_FLOOR = 0.35;

        /// <summary>Alpha of a cell the line ran out of time on: the untyped grey, dimmed.</summary>
        public const float MISSED_ALPHA = 0.4f;

        /// <summary>
        /// Alpha of a cell a word skip ABANDONED (backlog 167). Deliberately BETWEEN full brightness
        /// and <see cref="MISSED_ALPHA"/>, because that is exactly where the state sits: the player
        /// has given the character up, and one backspace takes it back, so it must read neither as an
        /// untouched character nor as a lost one.
        /// </summary>
        public const float ABANDONED_ALPHA = 0.7f;

        /// <summary>
        /// Alpha of a typo that landed on a WORD GAP (backlog 185): a Wrong cell whose expected
        /// character is a space, so the glyph on screen is the typed character rather than the space
        /// (see <see cref="CellGlyph"/>). Purely a display dimming, nothing about the keystroke, the
        /// judgement or the replay changes.
        ///
        /// <para>The gap typo is the one error the game draws INSIDE a word boundary, and at full
        /// brightness a burst of them closes every boundary in the line: after eight or nine
        /// consecutive typos playtesters could no longer see where words started and ended, because
        /// the red glyphs filling the gaps read as ordinary characters. Dimming the gap typo alone
        /// leaves the boundary legible as a boundary through the burst while the error is still
        /// plainly there.</para>
        ///
        /// <para>Placed BETWEEN the two dimmings that already exist, and for the reason each of them
        /// sits where it does. Dimmer than a full-brightness Wrong LYRIC cell, which keeps its 1,
        /// because that cell still shows the character the line is MADE of and dimming it would dim
        /// the lyric itself; brighter than <see cref="MISSED_ALPHA"/> because this is not a lost
        /// character but a live, fixable claim on the gap, exactly the standing
        /// <see cref="ABANDONED_ALPHA"/> reasoning, and one backspace takes it back.</para>
        ///
        /// <para>Carried on the per-cell STATE ALPHA lane, not the fill colour: the colour stays
        /// <see cref="TypeBeatStyle.ErrorChar"/>, identical to a lyric typo's, so the error still
        /// reads as an error, and the state alpha composes multiplicatively with the flashlight
        /// window in <c>applyCellAlpha</c> instead of fighting it. Backspacing the typo returns the
        /// cell to <see cref="CellState.Untyped"/>, which restores the default 1 on its own.</para>
        /// </summary>
        public const float WRONG_GAP_ALPHA = 0.55f;

        /// <summary>
        /// The fill a CORRECT character is painted in, given the sync quality of the keypress that
        /// scored it (<see cref="SyncWindows.SyncQuality"/>, asymmetric, already in [0, 1]): a point
        /// on the ramp from <see cref="TypeBeatStyle.UntypedChar"/> to
        /// <see cref="TypeBeatStyle.TypedChar"/>, compressed onto [<see cref="SYNC_TINT_FLOOR"/>, 1]
        /// of it. A player nailing the playhead leaves a bright white trail behind them; one dragging
        /// or rushing leaves a dull one.
        ///
        /// <para>Purely cosmetic, and deliberately driven by the SAME quality
        /// <c>TypingEngine.BuildResults</c> sums into the results screen's sync percent, so the trail
        /// is a live preview of that number rather than a second opinion about it (and it inherits
        /// the asymmetric early/late tolerance and the per-cell granularity widening for free).
        /// Since backlog 251 that number is itself display-only, so the ramp previews a readout and
        /// not a grade: both halves of the metric are drawn only when
        /// <see cref="Configuration.TypeBeatRulesetSetting.ShowSyncMetric"/> is on, and neither
        /// decides anything when it is. A dead-on press returns
        /// <see cref="TypeBeatStyle.TypedChar"/> exactly, which is also what the whole line wears
        /// with the metric off, so nothing about a perfectly timed line looks different from before
        /// this ramp existed. Out-of-range and NaN qualities clamp. Pure, so it is
        /// unit-testable.</para>
        /// </summary>
        public static Color4 CorrectCharColour(double syncQuality)
        {
            double q = double.IsNaN(syncQuality) ? 0 : Math.Clamp(syncQuality, 0, 1);

            // Exactness at the top of the ramp is a contract, not an optimisation: a componentwise
            // lerp at t = 1 is only float-approximately the end colour.
            if (q >= 1)
                return TypeBeatStyle.TypedChar;

            return Interpolation.ValueAt(SYNC_TINT_FLOOR + (1 - SYNC_TINT_FLOOR) * q,
                TypeBeatStyle.UntypedChar, TypeBeatStyle.TypedChar, 0d, 1d);
        }

        /// <summary>
        /// The fill a cell is painted in; every colour decision the display makes routes through
        /// here, so pinning this function pins the rendering. Pure, so it is unit-testable beside
        /// <see cref="CorrectCharColour"/>.
        ///
        /// <para>ONE rule, not a choice between presentations (backlog 177 merged them): this is
        /// EXACTLY the pre-174 painting plus a single addition. Correct rides the sync-tint ramp on
        /// <paramref name="syncQuality"/> (flat <see cref="TypeBeatStyle.TypedChar"/> when null, the
        /// cannot-arise fallback), Wrong is <see cref="TypeBeatStyle.ErrorChar"/>,
        /// Missed/Abandoned/AutoSkipped are the untyped grey (their alphas, unchanged, carry the
        /// state), and an Untyped cell is the untyped grey UNLESS <paramref name="inSungSyllable"/>,
        /// in which case it wears <see cref="TypeBeatStyle.SungChar"/>, a LIGHTER grey.</para>
        ///
        /// <para>The sung brightness preference blends untyped grey towards typed white. It affects
        /// only untyped cells in the sung group, so adjusting it never changes the sync tint or
        /// the colour of a correct, wrong, lost or freestyle cell.</para>
        ///
        /// <para>The lit group therefore shows under EVERY sung playhead style, not just one: the
        /// highlight and the playhead are complements rather than alternatives, and
        /// <see cref="Configuration.CaretStyle.None"/> subtracts the caret and the sweep without
        /// changing a single colour decided here. What drives
        /// <paramref name="inSungSyllable"/> is the stage's per-frame availability feed: syllable
        /// spans normally, character targets under HR and in character-timed sections.</para>
        ///
        /// <para>A Correct cell retains its sync tint everywhere, independently of the sung
        /// brightness setting.</para>
        ///
        /// <para>A FREESTYLE cell wears <paramref name="freestyleColour"/> in every state, which is
        /// the map's own pick (backlog 384) or, when the caller passes none,
        /// <see cref="TypeBeatStyle.FreestyleChar"/>: the colour is an identity signal ("this slot was
        /// free"), and neither the sync ramp nor the syllable highlight may repaint it (see
        /// <see cref="refreshFreestyleCell"/>; an exclusion, not an oversight).</para>
        /// </summary>
        public static Color4 CellFillColour(CellState state, bool isFreestyle, bool inSungSyllable, double? syncQuality, float sungBrightness = 50f,
                                            Color4? freestyleColour = null)
        {
            if (isFreestyle)
                return freestyleColour ?? TypeBeatStyle.FreestyleChar;

            switch (state)
            {
                case CellState.Correct:
                    // A Correct cell with no delta cannot arise from the engine; if one ever does,
                    // fall back to the flat typed colour rather than to the dull ramp floor.
                    return syncQuality is double q ? CorrectCharColour(q) : TypeBeatStyle.TypedChar;

                case CellState.Wrong:
                    // Error red, over the expected glyph on a lyric cell and over the TYPED one on a
                    // word gap (see CellGlyph): the colour decision is the same either way.
                    return TypeBeatStyle.ErrorChar;

                case CellState.Untyped:
                    // The one addition to the classic painting: the group the vocals are on lifts to
                    // a lighter grey, so where the song is up to stays legible from the characters
                    // even when the playhead is switched off, without claiming they were typed.
                    return inSungSyllable ? TypeBeatStyle.SungCharForBrightness(sungBrightness) : TypeBeatStyle.UntypedChar;

                default: // Missed, Abandoned, AutoSkipped
                    // Lost or given up, and the highlight is only for characters that can still be
                    // typed on time, so the sung group must not light them; their alphas say the rest.
                    return TypeBeatStyle.UntypedChar;
            }
        }

        /// <summary>
        /// The GLYPH a non-freestyle cell shows, the companion of <see cref="CellFillColour"/> and
        /// pure for the same reason. Almost always the cell's own expected character, which is what
        /// the sprite was built with: a lyric cell shows its lyric character in every state, and a
        /// WRONG one shows that character in the error red rather than the char the player pressed,
        /// so a mistyped line still reads as the line it was meant to be.
        ///
        /// <para>The one exception is a WRONG WORD GAP (backlog 181, when
        /// <see cref="TypingEngine.WrongInputOnWordGaps"/> let a typo land on a space cell at all):
        /// there the expected character is a space, and a space painted red is nothing at all, so
        /// the cell shows the TYPED character instead. That is the whole of the difference, and it
        /// is forced rather than chosen: the state has to be visible, and the gap has no glyph of
        /// its own to make visible. A gap in any other state, the typo once backspaced included, is
        /// a space again.</para>
        ///
        /// <para>Layout does not move with it. The advances were measured once at load, so the
        /// letter is drawn centred in the gap's own slot and every cell after it stays exactly where
        /// it was; a word gap is never a line's first or last cell, so the auto-sized box cannot
        /// grow either. The alternative, re-measuring the line, would shuffle every character to the
        /// right of the caret at the instant of a mistake, which is the worst possible moment to
        /// move the text a player is reading.</para>
        /// </summary>
        /// <summary>
        /// The text a cell is built with: its own character, except under the Polyglot mod (backlog 331)
        /// where the keys of a hangul block draw as the one block on the first key's cell
        /// (<see cref="BlockGlyph"/>) and as nothing on the others.
        /// </summary>
        public static string InitialGlyph(TypingLine line, int cellIndex)
        {
            int head = line.JamoBlockHead(cellIndex);

            if (head < 0)
                return line.Cells[cellIndex].Expected.ToString();

            return head == cellIndex ? BlockGlyph(line, head) : string.Empty;
        }

        /// <summary>
        /// The glyph of the hangul block starting at <paramref name="head"/>: the block recomposed from
        /// the keys TYPED into it so far, in order up to the first one still untyped (a wrong key shows
        /// as typed, so the block does not pretend it was right), or the whole block while none is.
        /// </summary>
        public static string BlockGlyph(TypingLine line, int head)
        {
            var expected = new List<char>();
            var typed = new List<char>();
            bool prefix = true;

            for (int i = head; i < line.Cells.Count && line.JamoBlockHead(i) == head; i++)
            {
                var cell = line.Cells[i];
                expected.Add(cell.Expected);

                if (!prefix)
                    continue;

                if (cell.State == CellState.Correct)
                    typed.Add(cell.TypedChar ?? cell.Expected);
                else if (cell.State == CellState.Wrong && cell.TypedChar is char wrong)
                    typed.Add(wrong);
                else
                    prefix = false;
            }

            return PolyglotText.Compose(typed.Count > 0 ? typed : expected);
        }

        public static char CellGlyph(char expected, CellState state, char? typedChar)
            => expected == ' ' && state == CellState.Wrong && typedChar is char typo ? typo : expected;

        /// <summary>
        /// THE GAP'S OWN GLYPH, which is the one cell whose text depends on a user setting as well as
        /// on its state.
        ///
        /// <para>With the SPACE ERROR DOT on, a gap that earns the dot shows ONLY THE DOT: the dot is
        /// the marker for the error in the word behind it, and drawing the mistyped character as well
        /// stacked two marks in one slot - the character sitting on top of the dot. With the dot off
        /// the character is the only thing that can show a gap typo at all (a red space is nothing),
        /// so it stays, and it is REPLACED by each further press rather than accumulating: the engine
        /// keeps one character on the cell (see <see cref="TypingEngine.ProcessKey"/>'s parked-gap
        /// arm), so one backspace still erases it and no extra cell or miss is created.</para>
        /// </summary>
        public static char GapGlyph(bool spaceErrorDotsEnabled, bool dotted, char expected, CellState state, char? typedChar)
            => expected == ' ' && spaceErrorDotsEnabled && dotted ? ' ' : CellGlyph(expected, state, typedChar);

        /// <summary>
        /// Radius of a space error dot as a fraction of the glyph row height (so it tracks the font
        /// size and the auto-shrink scale for free). Small on purpose: the dot is a margin note about
        /// a word already behind the caret, not a sixth character state competing with the line.
        /// </summary>
        public const float SPACE_ERROR_DOT_RADIUS = 0.07f;

        /// <summary>
        /// How long a dot's BOUNCE lasts, in milliseconds, and how far it swells at the peak. Every
        /// NEW mistype on a gap pulses the dot that stands for it - the marker has to acknowledge the
        /// keypress even though the character it replaces is no longer drawn (see
        /// <see cref="GapGlyph"/>), or mashing a space looks like nothing happened. Fast and small on
        /// purpose: a margin note reacting, not an animation the eye has to follow.
        /// </summary>
        public const float SPACE_ERROR_DOT_PULSE_MS = 150f;
        public const float SPACE_ERROR_DOT_PULSE_SCALE = 0.5f;

        /// <summary>
        /// The bounce's scale multiplier <paramref name="elapsedMs"/> into a pulse: 1 at the start and
        /// the end, <c>1 + SPACE_ERROR_DOT_PULSE_SCALE</c> at the halfway point. Pure and static so the
        /// curve can be pinned without standing up a drawable (the same shape the other geometry
        /// rules here use), and a sine rather than two tweened halves so it eases in and out of the
        /// peak instead of cornering at it.
        /// </summary>
        public static float SpaceErrorDotPulseScale(float elapsedMs)
        {
            if (!(elapsedMs > 0) || elapsedMs >= SPACE_ERROR_DOT_PULSE_MS)
                return 1f;

            return 1f + SPACE_ERROR_DOT_PULSE_SCALE * MathF.Sin(MathF.PI * (elapsedMs / SPACE_ERROR_DOT_PULSE_MS));
        }

        /// <summary>
        /// Content-local drop from the bottom of the glyph row to the sung sweep rail AT THE DEFAULT
        /// FONT SIZE. Since backlog 334 the drawn drop is this times <see cref="SizeRatio"/> (1/7 of
        /// the font size), so the rail keeps its distance from the text at every lyric size.
        /// </summary>
        public const float SWEEP_RAIL_OFFSET = 6f;

        /// <summary>Thickness of the sweep rail, its fill and its glow at the default font size
        /// (1/14 of it); drawn times <see cref="SizeRatio"/>.</summary>
        public const float SWEEP_RAIL_HEIGHT = 3f;

        /// <summary>Width of the glow at the sweep's leading edge at the default font size (1/7 of
        /// it); drawn times <see cref="SizeRatio"/>.</summary>
        public const float SWEEP_GLOW_WIDTH = 6f;

        /// <summary>Half-amplitude of the wrong-character shake at the default font size (1/21 of
        /// it); drawn times <see cref="SizeRatio"/>.</summary>
        public const float WRONG_CHAR_SHAKE = 2f;

        /// <summary>
        /// Height of a syllable marker as a fraction of the glyph row height. Tiny on purpose, and
        /// tinier than the space error dot: this one sits under EVERY subdivided word of the line
        /// (a dot marks an occasional mistake), so it has to read as a tick of punctuation and not
        /// as a row of arrows under the lyric.
        /// </summary>
        public const float SYLLABLE_MARKER_HEIGHT = 0.09f;

        /// <summary>Width of a syllable marker as a multiple of its height: wider than it is tall,
        /// so the mark reads as a shallow typographic wedge pointing at the gap rather than as a
        /// narrow UI arrowhead.</summary>
        public const float SYLLABLE_MARKER_ASPECT = 1.6f;

        /// <summary>
        /// A WORD GAP: the typeable SPACE cell that separates two words, the same definition the
        /// engine's own <c>isWordGap</c> uses (a non-typeable cell rides inside the word it is
        /// attached to and is never a boundary). Shared here so the display and the engine cannot
        /// disagree about where a word ends.
        /// </summary>
        public static bool IsWordGap(TypingCell cell) => cell.IsTypeable && cell.Expected == ' ';

        /// <summary>
        /// The space error dot marks only a wrong character currently typed into a word-gap cell.
        /// A skipped word, an accepted space, or a typo in a lyric character does not dot the gap.
        /// The rule reads current cell state, so backspacing a gap typo clears it immediately.
        /// </summary>
        public static bool[] ComputeSpaceErrorDots(IReadOnlyList<TypingCell> cells)
        {
            var result = new bool[cells.Count];
            computeSpaceErrorDots(cells, result);
            return result;
        }

        /// <summary>Write the current gap-typo flags into a caller-owned buffer without allocations.</summary>
        private static void computeSpaceErrorDots(IReadOnlyList<TypingCell> cells, bool[] into)
        {
            for (int i = 0; i < cells.Count; i++)
                into[i] = IsWordGap(cells[i]) && cells[i].State == CellState.Wrong && cells[i].TypedChar.HasValue;
        }

        /// <summary>
        /// Turn the space error dots on or off for this line (the user setting, live-bound by
        /// <see cref="LyricStage"/>). Nothing is painted here: the change is queued exactly like a
        /// cell repaint and lands on the next frame, so a toggle mid-play costs one dirty flag.
        /// </summary>
        public void SetSpaceErrorDotsEnabled(bool enabled)
        {
            if (spaceErrorDotsEnabled == enabled)
                return;

            spaceErrorDotsEnabled = enabled;
            spaceErrorDotsDirty = true;
        }

        private void applySpaceErrorDots()
        {
            if (gapCells.Length == 0)
                return;

            if (spaceErrorDotsEnabled)
                computeSpaceErrorDots(Line.Cells, gapDotFlags);
            else
                Array.Fill(gapDotFlags, false);

            for (int k = 0; k < gapCells.Length; k++)
            {
                int cellIndex = gapCells[k];
                bool dotted = gapDotFlags[cellIndex];

                gapDots[k].Alpha = dotted ? 1f : 0f;

                // The glyph moves with the overlay, in the same pass and from the same flag: with the
                // marker on, a dotted gap is blank (the dot is the whole mark), and with it off the
                // character comes back. Without this the two would be written by different code paths
                // and a corrected word would leave the character standing over a dot that had gone.
                var source = Line.Cells[cellIndex];
                cells[cellIndex].Text = GapGlyph(spaceErrorDotsEnabled, dotted, source.Expected, source.State, source.TypedChar).ToString();
            }
        }

        /// <summary>
        /// Turn the syllable markers on or off for this line (the user setting, live-bound by
        /// <see cref="LyricStage"/>). Unlike the space error dots there is nothing to recompute, so
        /// this applies straight away rather than through a dirty flag: the marks never move and
        /// never change meaning, only whether they are drawn.
        ///
        /// <para>Safe to call before the display has loaded (the stage pushes the bound value the
        /// moment it binds, which is before any display's own loader runs): the marker array is
        /// empty until then and <see cref="measureAndLayout"/> applies the flag when it builds
        /// them.</para>
        /// </summary>
        public void SetSyllableMarkersEnabled(bool enabled)
        {
            if (syllableMarkersEnabled == enabled)
                return;

            syllableMarkersEnabled = enabled;
            applySyllableMarkers(animate: false);
        }

        /// <summary>
        /// Turn the SYNC TINT on or off for this line (the user setting, live-bound by
        /// <see cref="LyricStage"/>). Off is the shipped default (backlog 251), which paints every
        /// Correct cell the flat <see cref="TypeBeatStyle.TypedChar"/> instead of a point on the
        /// ramp; on restores the ramp. Nothing but a colour moves either way: the judged deltas the
        /// ramp reads are recorded whatever this says, so a toggle mid-play cannot change what a
        /// press was worth.
        ///
        /// <para>Repaints every cell rather than only the Correct ones, because
        /// <see cref="RefreshCell"/> is the one place a fill is decided and re-deriving a cell that
        /// was not going to move is cheaper than working out which ones were. Safe before the
        /// display has loaded (the stage pushes the bound value the moment it binds): the cell array
        /// is empty until then and <c>LoadComplete</c> refreshes every cell.</para>
        /// </summary>
        public void SetSyncTintEnabled(bool enabled)
        {
            if (syncTintEnabled == enabled)
                return;

            syncTintEnabled = enabled;

            for (int i = 0; i < cells.Length; i++)
                RefreshCell(i);
        }

        /// <summary>
        /// Paints the markers' alpha: the setting, then the HIDING mods folded in through the
        /// DARKER of the two cells the mark sits between.
        ///
        /// <para>Taking the minimum, rather than the right-hand cell alone, is what stops the mark
        /// leaking word structure: a triangle is a statement about the boundary BETWEEN two
        /// characters, so it may only be drawn while both of them may be read. Under Flashlight a
        /// mark on the window's edge would otherwise announce a syllable boundary sitting in the
        /// dark, and under Recite the marks alone would spell out the syllable count of every
        /// upcoming word, which is exactly the information that mod withholds.</para>
        /// </summary>
        private void applySyllableMarkers(bool animate)
        {
            for (int k = 0; k < markerCells.Length; k++)
            {
                int i = markerCells[k];
                float target = syllableMarkersEnabled
                    ? Math.Min(cellHidingFactor(i - 1), cellHidingFactor(i))
                    : 0f;

                if (animate)
                    syllableMarkers[k].FadeTo(target, flashlight_fade_ms, Easing.OutQuint);
                else
                    syllableMarkers[k].Alpha = target;
            }
        }

        public void RefreshCell(int cellIndex)
        {
            applyTextPopIn(cellIndex);
            if (cellIndex < 0 || cellIndex >= cells.Length)
                return;

            // Any cell repaint can change a dot: the flaw it carries belongs to its whole word, and
            // the gap that word ends at is somewhere to its right. Recomputing the line once on the
            // next frame is cheaper than working out which gap this was, and it is what makes a
            // backspaced gap or a reclaimed word clear its dot with no event of its own.
            if (gapCells.Length > 0)
                spaceErrorDotsDirty = true;

            // A repaint can change a MARK only under Recite, whose hiding is per-cell STATE: typing
            // the character to the right of a boundary is what reveals that boundary's mark. Under
            // every other stack the marks are static, so the flag stays clear and costs nothing.
            if (reciteEnabled && markerCells.Length > 0)
                syllableMarkersDirty = true;

            var cell = cells[cellIndex];
            var source = Line.Cells[cellIndex];

            if (source.IsFreestyle)
            {
                refreshFreestyleCell(cellIndex, cell, source);
                return;
            }

            // A hangul block recomposes AS TYPED (Polyglot, backlog 331): its glyph is whatever the keys
            // typed into it so far spell, and the whole block again once they are all erased.
            int blockHead = Line.JamoBlockHead(cellIndex);

            if (blockHead >= 0)
                cells[blockHead].Text = BlockGlyph(Line, blockHead);

            // The classic Correct colour is tinted by how in sync the press was (see
            // CorrectCharColour). Two properties of the delta this reads are load-bearing:
            //
            // ORDERING: TypingEngine.ProcessKey writes JudgedDelta BEFORE it raises
            // CharJudged, and LyricStage's handler for that event is what calls RefreshCell,
            // so the delta is always present by the time the cell first repaints. Reversed,
            // every char would paint at the floor colour on the frame it was typed.
            //
            // ANTI-FARMING: JudgedDelta on a Correct cell is always the delta that actually
            // SCORED. A scoring-inert retype (backspace over a cell that was ever correct)
            // has the first correct delta written back into it, so a player cannot
            // backspace-retype to brighten a char beyond what it earned.
            //
            // OPT-IN since backlog 251: with the sync metric off (the shipped default) no quality is
            // resolved at all and the cell falls to the flat TypedChar CellFillColour already uses
            // for a Correct cell with no delta, which is exactly the pre-tint painting. The delta is
            // untouched either way; only whether it is read is.
            //
            // AN OVER-CAP PRESS (backlog 347) is drawn at the floor, the off-time styling: it was
            // awarded Meh for where the caret was rather than for when the key went down, so its
            // delta can be dead on and still describe a cell graded at the bottom of the ladder.
            // The delta itself stays untouched on the cell and in every readout.
            double? syncQuality = syncTintEnabled && source.JudgedDelta is double delta
                ? (source.JudgedPastRushCap ? 0 : SyncWindows.Default.SyncQuality(delta))
                : null;

            bool inSungSyllable = litCells[cellIndex];

            cell.Colour = CellFillColour(source.State, isFreestyle: false, inSungSyllable, syncQuality, sungBrightness * litAmounts[cellIndex]);
            // A WORD GAP is the one cell whose glyph is not fixed at construction: a typo landing on
            // it shows the typed char, and every other state shows the space back (see CellGlyph).
            // Scoped to the gap rather than asserted for every cell so a lyric character's Text is
            // still written exactly once, at construction, and this cannot become a per-refresh
            // string allocation across the whole line.
            if (source.Expected == ' ')
                cell.Text = GapGlyph(spaceErrorDotsEnabled, cellIndex < gapDotFlags.Length && gapDotFlags[cellIndex],
                    source.Expected, source.State, source.TypedChar).ToString();

            switch (source.State)
            {
                case CellState.Missed:
                    cellStateAlpha[cellIndex] = MISSED_ALPHA;
                    break;

                case CellState.Abandoned:
                    // A skipped word is dimmed, but NOT to the missed dimming: the character has
                    // been given up and not yet lost, and one backspace re-opens it (backlog 167).
                    // Painting it as a miss would say the opposite of what the state means, and
                    // leaving it at full untyped brightness would hide that the skip happened at
                    // all, so it takes the step between the two.
                    cellStateAlpha[cellIndex] = ABANDONED_ALPHA;
                    break;

                case CellState.Wrong when source.Expected == ' ':
                    // A typo sitting IN a word gap (backlog 185). Scoped to the gap and nothing else:
                    // a Wrong LYRIC cell, including one a space key was typed through onto, still
                    // shows its own lyric character and keeps full brightness below. See
                    // WRONG_GAP_ALPHA for why the boundary has to keep reading as a boundary.
                    cellStateAlpha[cellIndex] = WRONG_GAP_ALPHA;
                    break;

                default: // Untyped, Correct, Wrong on a lyric cell, AutoSkipped
                    cellStateAlpha[cellIndex] = 1f;
                    break;
            }

            // Compose the state alpha with the flashlight window and the Recite factor (each a
            // no-op multiplier of 1 when its mod is off) so a state refresh can never undo their
            // hiding, or vice versa. It is also what makes Recite need no event wiring of its own:
            // this line runs on every state transition, so typing a char reveals its cell and
            // backspacing it hides the cell again.
            applyCellAlpha(cellIndex, animate: false);
        }

        /// <summary>
        /// A FREESTYLE cell always wears <see cref="FreestyleColour"/>, shimmering while
        /// it is still open and frozen on the char the player actually pressed once it is filled
        /// (so a finished line still shows which slots were free). Backspace puts it back to
        /// Untyped, which resumes the shimmer and lets a different char land.
        ///
        /// <para>DELIBERATELY no sync tint (see <see cref="CorrectCharColour"/>): the violet is an
        /// IDENTITY signal, "this slot was free", not a state signal, and it has to keep saying that
        /// for the rest of the play. Lerping it towards the untyped grey by how in sync the press was
        /// would fight the one thing the colour exists to say. This is an exclusion, not an
        /// oversight.</para>
        /// </summary>
        private void refreshFreestyleCell(int cellIndex, OsuSpriteText cell, TypingCell source)
        {
            // Routed through CellFillColour so the exclusion is the rendered path, not a parallel
            // truth: freestyle identity wins over the sung highlight as well as over the sync ramp.
            cell.Colour = CellFillColour(source.State, isFreestyle: true,
                inSungSyllable: litCells[cellIndex], syncQuality: null, sungBrightness, FreestyleColour);

            cellStateAlpha[cellIndex] = source.State switch
            {
                CellState.Missed => MISSED_ALPHA,
                CellState.Abandoned => ABANDONED_ALPHA,
                _ => 1f,
            };

            if (source.State == CellState.Untyped)
                cell.Text = FreestyleGlyphs.Glyph(shimmerPool, shimmerTick, cellIndex).ToString();
            else if (source.TypedChar is char typed)
                cell.Text = typed.ToString();

            // else (Missed, never typed): keep whatever glyph the shimmer last left, dimmed.

            for (int k = 0; k < freestyleCells.Length; k++)
            {
                if (freestyleCells[k] == cellIndex)
                {
                    freestyleRenderedState[k] = source.State;
                    break;
                }
            }

            applyCellAlpha(cellIndex, animate: false);
        }

        public void PlayJudgementFeedback(CharJudgement judgement)
        {
            int i = judgement.CellIndex;
            if (i < 0 || i >= cells.Length)
                return;

            var cell = cells[i];

            if (judgement.Type == JudgementType.Great)
            {
                cell.ScaleTo(1.25f).Then().ScaleTo(1f, 120, Easing.OutQuint);
            }
            else if (judgement.Type == JudgementType.WrongChar)
            {
                float baseX = cellX[i] + advances[i] * 0.5f;
                float shake = WRONG_CHAR_SHAKE * SizeRatio;
                cell.MoveToX(baseX - shake, 25)
                    .Then().MoveToX(baseX + shake, 25)
                    .Then().MoveToX(baseX, 15, Easing.OutQuint);

                // AND THE GAP'S OWN DOT BOUNCES, on EVERY mistype - the same character twice included.
                // This runs per JUDGEMENT, which the engine raises once per wrong keypress that LANDS
                // on a cell (a Gatekeeper rejection never reaches here, because nothing landed), so the
                // bounce is a per-press signal. Reading the cell's character instead missed a repeated
                // key: pressing 'x' twice leaves the same 'x' on the cell and nothing to compare.
                int dot = gapDotIndex(i);

                if (dot >= 0)
                    gapDotPulseMs[dot] = SPACE_ERROR_DOT_PULSE_MS;
            }
        }

        /// <summary>The dot slot standing for <paramref name="cellIndex"/>, or -1 when that cell is
        /// not a word gap. A line holds a handful of gaps, so the scan is cheaper than a map.</summary>
        private int gapDotIndex(int cellIndex)
        {
            for (int k = 0; k < gapCells.Length; k++)
            {
                if (gapCells[k] == cellIndex)
                    return k;
            }

            return -1;
        }

        public void SetSungPosition(double fractionalCellIndex)
        {
            if (sweepFill.IsNull())
                return;

            float localX = localXFor(fractionalCellIndex);
            sweepFill.Width = localX;
            sweepGlow.X = localX;
            sweepGlow.Alpha = localX > 0.5f ? 0.9f : 0f;
        }

        /// <summary>Light exactly one syllable, or clear all groups with -1.</summary>
        public void SetSungSyllable(int index)
        {
            sungSyllable = index;
            for (int i = 0; i < litCells.Length; i++)
                setCellLit(i, index >= 0 && grouping.IndexOf(i) == index);
        }

        /// <summary>Follow Great windows, optionally approaching their brightness from the early Ok edge.</summary>
        public void SetSungWindow(double time, double greatEarly, double greatLate, bool characterTiming = false, bool charTimedStretch = false, double? okEarly = null)
        {
            sungTime = time;
            sungGreatEarly = greatEarly;
            sungGreatLate = greatLate;
            sungOkEarly = Math.Max(greatEarly, okEarly ?? greatEarly);
            this.characterTiming = characterTiming;
            this.charTimedStretch = charTimedStretch;
            int primary = -1;

            for (int i = 0; i < litCells.Length; i++)
            {
                var (start, end) = sungSpan(i);
                double approachEarly = syllableFadeInEnabled ? sungOkEarly : greatEarly;
                bool lit = Line.Cells[i].IsCountable && time >= start - approachEarly && time <= end + greatLate;
                float amount = lit
                    ? syllableFadeInEnabled && sungOkEarly > greatEarly
                        ? (float)Math.Clamp((time - start + sungOkEarly) / (sungOkEarly - greatEarly), 0, 1)
                        : 1f
                    : 0f;
                int group = grouping.IndexOf(i);
                if (lit && group >= 0 && (primary < 0 || time >= start && time <= end))
                    primary = group;
                setCellLit(i, lit, amount);
                applyTextPopIn(i);
            }
            sungSyllable = primary;
        }

        private (double start, double end) sungSpan(int index)
        {
            int group = grouping.IndexOf(index);
            if (!characterTiming && !(charTimedStretch && grouping.IsCharTimedStretch(index)) && group >= 0)
            {
                var span = grouping.Groups[group];
                double start = span.StartTime;
                if (charTimedStretch)
                {
                    // A syllable-timed suffix must wait for the character-timed run before it.
                    // It shares the final character's opening, while keeping the syllable's end.
                    for (int i = index - 1; i >= span.StartCell; i--)
                    {
                        if (grouping.IndexOf(i) == group && grouping.IsCharTimedStretch(i))
                        {
                            start = Math.Max(start, Line.Cells[i].TargetTime);
                            break;
                        }
                    }
                }
                return (start, Math.Max(start, span.EndTime));
            }
            double target = Line.Cells[index].TargetTime;
            return (target, target);
        }

        private void setCellLit(int index, bool lit, float amount = 1f)
        {
            amount = lit ? amount : 0f;
            if (litCells[index] == lit && litAmounts[index] == amount)
                return;
            litCells[index] = lit;
            litAmounts[index] = amount;
            // Only the untyped fill changes during the approach; judgement feedback keeps its colour.
            if (index < cells.Length && Line.Cells[index].State == CellState.Untyped && !Line.Cells[index].IsFreestyle)
                cells[index].Colour = TypeBeatStyle.SungCharForBrightness(sungBrightness * amount);
        }

        public void SetSyllableFadeInEnabled(bool enabled)
        {
            if (syllableFadeInEnabled == enabled)
                return;
            syllableFadeInEnabled = enabled;
            if (!double.IsNaN(sungTime))
                SetSungWindow(sungTime, sungGreatEarly, sungGreatLate, characterTiming, charTimedStretch, sungOkEarly);
        }

        /// <summary>Adjust the highlight colour during play without changing cell states.</summary>
        public void SetSungBrightness(float percent)
        {
            float value = Math.Clamp(percent, 0f, 100f);
            if (sungBrightness == value)
                return;
            sungBrightness = value;
            for (int i = 0; i < litCells.Length; i++)
                if (litCells[i] && Line.Cells[i].State == CellState.Untyped && !Line.Cells[i].IsFreestyle)
                    RefreshCell(i);
        }

        public void SetTextPopInEnabled(bool enabled)
        {
            textPopInEnabled = enabled;
            for (int i = 0; i < cells.Length; i++)
                applyTextPopIn(i);
        }

        public void SetTextPopInAmount(float amount)
        {
            textPopInAmount = Math.Clamp(amount, 0, TypeBeatRulesetConfigManager.MAX_TEXT_POP_IN_AMOUNT);
            for (int i = 0; i < cells.Length; i++)
                applyTextPopIn(i);
        }

        /// <summary>Absolute song-time easing, so pauses, playback rates and seeks cannot delay the finish.</summary>
        public static float TextPopInScale(double time, double windowOpens, float amount = TypeBeatRulesetConfigManager.DEFAULT_TEXT_POP_IN_AMOUNT)
        {
            float minScale = 1 - Math.Clamp(amount, 0, TypeBeatRulesetConfigManager.MAX_TEXT_POP_IN_AMOUNT) / 100f;
            if (double.IsNaN(time))
                return minScale;
            double progress = Math.Clamp((time - windowOpens + TEXT_POP_IN_DURATION_MS) / TEXT_POP_IN_DURATION_MS, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 3);
            return (float)(minScale + (1 - minScale) * eased);
        }

        private void applyTextPopIn(int index)
        {
            if (index < 0 || index >= cells.Length)
                return;
            var source = Line.Cells[index];
            bool apply = textPopInEnabled && source.IsCountable && source.State == CellState.Untyped;
            if (apply)
            {
                cells[index].Scale = new Vector2(TextPopInScale(sungTime, sungSpan(index).start - sungGreatEarly, textPopInAmount));
                popInApplied[index] = true;
            }
            else if (popInApplied[index])
            {
                cells[index].Scale = Vector2.One;
                popInApplied[index] = false;
            }
        }

        /// <summary>The group index last highlighted; -1 also covers ungrouped character timing.</summary>
        public int SungSyllable => sungSyllable;

        public float CellVisualScale(int index) => index >= 0 && index < cells.Length ? cells[index].Scale.X : 1f;

        public void SetLineDim(float dimAmount)
        {
            if (content.IsNull())
                return;

            float alpha = Math.Clamp(1f - dimAmount, 0f, 1f);
            content.FadeTo(alpha, 150, Easing.OutQuint);
        }

        // --- Flashlight mod: per-character visibility window ---
        // The mod lights only a fixed run of COUNTABLE chars (typeable and not a space) either side
        // of the caret head; spaces and punctuation inside that run stay lit but do not spend the
        // budget. The window is computed at the STREAM level (the whole lyric stack read as one
        // continuous run of countable chars) by the stage, so its budget spills across line
        // boundaries: the tail of one line and the head of the next can be lit at once. Each line
        // gets its own slice as a LineWindow, applied here. Purely visual: judgement is untouched.

        private const float flashlight_soft_alpha = 0.35f;
        private const double flashlight_fade_ms = 90;

        private bool flashlightEnabled;
        private bool flashlightHidden = true;   // true = the whole line is hidden
        private bool flashlightShowSweep;
        private LineWindow flashlightWindow = LineWindow.Hidden;
        private float[] flashlightAlphas = Array.Empty<float>();

        /// <summary>Light this line's slice of the stream window. <paramref name="showSweep"/> keeps
        /// the sung underline (only the active line wants it; a line lit purely by spill does not).
        /// Cheap to call every frame: it only recomputes and re-fades when the slice actually
        /// changes.</summary>
        public void SetFlashlightWindow(LineWindow window, bool showSweep)
        {
            if (flashlightEnabled && !flashlightHidden && flashlightWindow.Equals(window) && flashlightShowSweep == showSweep)
                return;

            flashlightEnabled = true;
            flashlightHidden = false;
            flashlightWindow = window;
            flashlightShowSweep = showSweep;
            flashlightAlphas = ComputeWindowAlphas(Line.Cells, window, flashlight_soft_alpha);

            reapplyFlashlight();

            if (sweepFill.IsNotNull())
            {
                float target = showSweep ? 1f : 0f;

                // EVERY band, not the first: the rail is segmented since backlog 228, and a band
                // left lit would leak the shape of a line the mod is meant to be hiding.
                foreach (var track in sweepTracks)
                    track.FadeTo(target, flashlight_fade_ms);

                foreach (var blend in sweepBlends)
                    blend.FadeTo(target, flashlight_fade_ms);

                sweepFill.FadeTo(target, flashlight_fade_ms);

                if (!showSweep)
                    sweepGlow.FadeTo(0f, flashlight_fade_ms);
            }
        }

        /// <summary>Hide the whole line: lines wholly outside the stream window, plus every line during
        /// pre-cue dead zones and pre-roll (you must not be able to read ahead of the caret).</summary>
        public void HideForFlashlight()
        {
            if (flashlightEnabled && flashlightHidden)
                return;

            flashlightEnabled = true;
            flashlightHidden = true;
            flashlightWindow = LineWindow.Hidden;

            reapplyFlashlight();

            if (sweepFill.IsNotNull())
            {
                foreach (var track in sweepTracks)
                    track.FadeTo(0f, flashlight_fade_ms);

                foreach (var blend in sweepBlends)
                    blend.FadeTo(0f, flashlight_fade_ms);

                sweepFill.FadeTo(0f, flashlight_fade_ms);
                sweepGlow.FadeTo(0f, flashlight_fade_ms);
            }
        }

        private void reapplyFlashlight()
        {
            for (int i = 0; i < cells.Length; i++)
                applyCellAlpha(i, animate: true);

            applySyllableMarkers(animate: true);
        }

        private void applyCellAlpha(int i, bool animate)
        {
            // THE single composition point for every reason a cell can be dimmed or hidden: the
            // judgement state and the hiding mods each contribute an independent multiplier (1 when
            // they have nothing to say), so no two of them can clobber each other and no order of
            // application exists to get wrong.
            float target = cellStateAlpha[i] * cellHidingFactor(i);

            // Ruby must never reveal a word hidden by Flashlight or Recite. A dictionary compound
            // can span several kanji, so all of its cells must be visible before its reading is.
            foreach (var (group, ruby) in furigana)
            {
                if (i < group.StartCell || i >= group.EndCellExclusive)
                    continue;
                float alpha = 0.85f;
                for (int at = group.StartCell; at < group.EndCellExclusive; at++)
                    alpha = Math.Min(alpha, cellStateAlpha[at] * cellHidingFactor(at));
                if (animate)
                    ruby.FadeTo(alpha, flashlight_fade_ms, Easing.OutQuint);
                else
                    ruby.Alpha = alpha;
            }

            if (animate)
                cells[i].FadeTo(target, flashlight_fade_ms, Easing.OutQuint);
            else
                cells[i].Alpha = target;
        }

        /// <summary>
        /// How much of cell <paramref name="i"/> the HIDING mods leave visible: the flashlight
        /// window and Recite multiplied, each 1 when its mod is off, so stacking them is exactly
        /// "hidden by either". Split out from <see cref="applyCellAlpha"/> because the syllable
        /// markers need the same answer without the judgement-state lane, which is about a cell's
        /// own history and says nothing about whether it may be read.
        /// </summary>
        private float cellHidingFactor(int i) => flashlightFactor(i) * reciteFactor(i);

        private float flashlightFactor(int i)
        {
            if (!flashlightEnabled)
                return 1f;
            if (flashlightHidden)
                return 0f;
            return i < flashlightAlphas.Length ? flashlightAlphas[i] : 0f;
        }

        // --- Recite (backlog 229) ---
        // Hide every character the player has not typed yet. Unlike the flashlight this needs no
        // geometry at all: the predicate is per-cell state, so the whole mod is one flag plus
        // HiddenByRecite below, and every state transition (a char typed reveals its cell, a
        // backspace re-hides it) falls out of RefreshCell already ending in applyCellAlpha.

        private bool reciteEnabled;

        /// <summary>
        /// Turn the Recite mod's hiding on or off for this line. Idempotent, so the stage can call
        /// it every frame. Deliberately does NOT touch the sung sweep or the glow: unlike
        /// <see cref="HideForFlashlight"/>, Recite hides the WORDS and leaves the map's playhead
        /// visible, which is the one cue a reciting player still has.
        /// </summary>
        public void SetReciteEnabled(bool enabled)
        {
            if (reciteEnabled == enabled)
                return;

            reciteEnabled = enabled;

            // Cells may not exist yet (this can be pushed before the display has loaded); that is
            // safe, because LoadComplete refreshes every cell and RefreshCell ends in applyCellAlpha.
            for (int i = 0; i < cells.Length; i++)
                applyCellAlpha(i, animate: true);

            applySyllableMarkers(animate: true);
        }

        private float reciteFactor(int i)
        {
            if (!reciteEnabled)
                return 1f;

            return i < Line.Cells.Count && HiddenByRecite(Line.Cells[i]) ? 0f : 1f;
        }

        /// <summary>
        /// The Recite hiding rule, pure so it is unit-testable without drawables: a cell is hidden
        /// iff the player has not typed it (<see cref="CellState.Untyped"/>) and it is not a
        /// FREESTYLE slot.
        ///
        /// <para>Freestyle slots stay visible because they already shimmer a random pool glyph that
        /// says nothing about the lyric (see <see cref="refreshFreestyleCell"/>): hiding them would
        /// reveal nothing but would make a whole freestyle section invisible, and the shimmer is how
        /// the player knows a free slot is there at all.</para>
        ///
        /// <para>Everything that is not Untyped is shown, including Missed, Abandoned and
        /// AutoSkipped. Those are all at or behind the caret in practice, and "upcoming" is exactly
        /// what Untyped means: a cell the caret has passed can no longer be read ahead of.</para>
        /// </summary>
        public static bool HiddenByRecite(TypingCell cell) => cell.State == CellState.Untyped && !cell.IsFreestyle;

        /// <summary>
        /// Per-cell visibility multipliers for a line given its <see cref="LineWindow"/> slice of the
        /// stream window: 1 for a lit char, <paramref name="softAlpha"/> for the outermost lit char on
        /// a side the window flagged soft (darkness beyond it in the stream), 0 for a hidden char. A
        /// space/punctuation cell strictly between two lit countable chars stays lit (it spends no
        /// budget); one at or past a lit edge is hidden. Pure (no drawable state) so it is unit-testable.
        /// </summary>
        public static float[] ComputeWindowAlphas(IReadOnlyList<TypingCell> cells, LineWindow window, float softAlpha)
        {
            int n = cells.Count;
            var result = new float[n];

            if (n == 0 || window.IsHidden)
                return result;

            // pref[i] = countable cells strictly before i; pref[n] = total countable in the line.
            int[] pref = new int[n + 1];
            for (int i = 0; i < n; i++)
                pref[i + 1] = pref[i] + (IsCountable(cells[i]) ? 1 : 0);

            int lo = window.Lo;
            int hi = window.Hi;

            for (int i = 0; i < n; i++)
            {
                if (IsCountable(cells[i]))
                {
                    int slot = pref[i];

                    if (slot < lo || slot > hi)
                        continue; // hidden

                    // Soften the outermost lit char only on a side the stream marked soft (a hidden
                    // char lies beyond it). A real line start/end or a boundary the window crosses
                    // into the next line stays a hard, full-alpha edge.
                    bool fadesLeft = slot == lo && window.SoftLeft;
                    bool fadesRight = slot == hi && window.SoftRight;
                    result[i] = fadesLeft || fadesRight ? softAlpha : 1f;
                }
                else
                {
                    // Space / punctuation: lit only when it sits strictly between two lit countable
                    // chars, so leading/trailing marks and the space just past the edge stay hidden.
                    int leftSlot = pref[i] - 1;
                    int rightSlot = pref[i];
                    bool leftLit = leftSlot >= lo && leftSlot <= hi;
                    bool rightLit = rightSlot >= lo && rightSlot <= hi;
                    result[i] = leftLit && rightLit ? 1f : 0f;
                }
            }

            return result;
        }

        /// <summary>
        /// Single-line convenience overload: treat <paramref name="cells"/> as the whole stream and
        /// centre a <paramref name="radius"/>-countable window on the caret. The stream-level path
        /// (<see cref="ComputeStreamWindows"/>) is what gameplay uses; this stays for isolated-line
        /// unit tests and any caller with one line in hand.
        /// </summary>
        public static float[] ComputeWindowAlphas(IReadOnlyList<TypingCell> cells, int caretCellIndex, int radius, float softAlpha)
            => ComputeWindowAlphas(cells, SingleLineWindow(cells, caretCellIndex, radius), softAlpha);

        /// <summary>
        /// Split a stream window across ordered lines. Given each line's countable-char count and the
        /// caret's stream slot (countable chars before the caret across the whole stack), returns the
        /// <see cref="LineWindow"/> slice for each line: <paramref name="radius"/> countable chars reach
        /// each side of the caret through the concatenated stream, so the budget spills from a line's
        /// tail into the next line's head (and symmetrically the other way). Only the outer edges of the
        /// whole window soften; boundaries the window crosses stay hard.
        ///
        /// <paramref name="maxRightSlot"/> caps the rightmost lit stream slot (inclusive). The stage
        /// passes the active line's last countable slot while the player is still typing it, so the
        /// forward budget cannot reach into the next line mid-line; once the line is complete (or during
        /// a cue-in, where there is no active line) it passes <see cref="int.MaxValue"/> and the budget
        /// spills forward as an early-finish reward. A clamped right edge is HARD (the clamped char is
        /// the last one you must still type, so it stays full alpha and darkness begins right after).
        /// The left/backward budget is never capped. Pure and unit-testable.
        /// </summary>
        public static LineWindow[] ComputeStreamWindows(IReadOnlyList<int> lineCountableCounts, int caretStreamSlot, int radius, int maxRightSlot = int.MaxValue)
        {
            int m = lineCountableCounts.Count;
            var result = new LineWindow[m];

            if (m == 0)
                return result;

            int total = 0;
            for (int k = 0; k < m; k++)
                total += lineCountableCounts[k];

            int segBase = 0;
            for (int k = 0; k < m; k++)
            {
                result[k] = windowForSegment(segBase, lineCountableCounts[k], total, caretStreamSlot, radius, maxRightSlot);
                segBase += lineCountableCounts[k];
            }

            return result;
        }

        /// <summary>Slice the global lit range [caret-radius, caret+radius-1] (clamped to the stream,
        /// and on the right to <paramref name="maxRightSlot"/>) down to the segment
        /// [segBase, segBase+segCount-1], reporting which side, if any, carries the window's soft outer
        /// edge.</summary>
        private static LineWindow windowForSegment(int segBase, int segCount, int totalCountable, int caretStreamSlot, int radius, int maxRightSlot)
        {
            if (segCount <= 0 || radius <= 0 || totalCountable <= 0)
                return LineWindow.Hidden;

            int caret = Math.Clamp(caretStreamSlot, 0, totalCountable);
            int gLo = Math.Max(0, caret - radius);          // leftmost lit stream slot (inclusive)
            int gHi = Math.Min(totalCountable - 1, caret + radius - 1); // rightmost lit stream slot

            // Cap the forward reach: while the active line is still being typed the stage caps this at
            // that line's last countable slot so nothing lights in the next line. A cap is a deliberate
            // wall, not a stream end, so the capped edge stays HARD (its char is fully lit).
            bool clampedRight = false;
            if (gHi > maxRightSlot)
            {
                gHi = maxRightSlot;
                clampedRight = true;
            }

            if (gLo > gHi)
                return LineWindow.Hidden;

            // Soft only where a hidden countable char actually lies beyond the window in the stream;
            // a clamp to slot 0 / the last slot is a hard stream end, and a right-cap is a hard wall.
            bool leftSoft = gLo > 0;
            bool rightSoft = !clampedRight && gHi < totalCountable - 1;

            int segHi = segBase + segCount - 1;
            int litLo = Math.Max(gLo, segBase);
            int litHi = Math.Min(gHi, segHi);

            if (litLo > litHi)
                return LineWindow.Hidden;

            // The soft edge belongs to whichever segment carries the window's outermost lit slot; a
            // segment whose lit run merely abuts the next line's run keeps a hard join.
            bool holdsGlobalLeft = litLo == gLo;
            bool holdsGlobalRight = litHi == gHi;

            return new LineWindow(litLo - segBase, litHi - segBase,
                holdsGlobalLeft && leftSoft,
                holdsGlobalRight && rightSoft);
        }

        private static LineWindow SingleLineWindow(IReadOnlyList<TypingCell> cells, int caretCellIndex, int radius)
        {
            int n = cells.Count;
            int caret = Math.Clamp(caretCellIndex, 0, n);
            int total = 0;
            int caretBudget = 0; // countable chars strictly left of the caret head

            for (int i = 0; i < n; i++)
            {
                if (!IsCountable(cells[i]))
                    continue;

                if (i < caret)
                    caretBudget++;

                total++;
            }

            return windowForSegment(0, total, total, caretBudget, radius, int.MaxValue);
        }

        /// <summary>A COUNTABLE cell: typeable and not a space. Spaces and punctuation do not spend the
        /// flashlight budget; the stage uses this to size the stream too, so it is shared here.
        /// Delegates to <see cref="TypingCell.IsCountable"/>, the single definition the engine's
        /// Fletcher rush cap measures character distance with.</summary>
        public static bool IsCountable(TypingCell cell) => cell.IsCountable;

        // --- Test-support accessors (public so cross-assembly test scenes can assert) ---

        public int CellCount => cells.Length;

        public float CellAlpha(int index) => index >= 0 && index < cells.Length ? cells[index].Alpha : 0f;

        /// <summary>The glyph currently rendered in a cell: the shimmer substitute for an open
        /// freestyle slot, the pressed char once it is filled, the authored char otherwise.</summary>
        public string CellText(int index) => index >= 0 && index < cells.Length ? cells[index].Text.ToString() : string.Empty;

        /// <summary>The width-matched glyph pool this line's freestyle cells shimmer through.</summary>
        public IReadOnlyList<char> ShimmerPool => shimmerPool;

        /// <summary>Current alpha of the sung underline sweep's rail and its filled part. Test
        /// support for the one thing that separates the two hiding mods:
        /// <see cref="HideForFlashlight"/> fades these out along with the characters, and Recite
        /// must NOT, because the sweep is the map playhead a reciting player is left reading. A
        /// width alone cannot pin it: a faded sweep still advances.
        ///
        /// <para>The rail is several drawables since backlog 228 (one band per word, see
        /// <see cref="buildPaceTracks"/>), and both flashlight seams fade every band together, so
        /// any one band would do. It reports the MAXIMUM anyway, deliberately: "the rail is
        /// visible" is true if ANY of it is, so a regression that left one band lit under the
        /// flashlight and one that faded a single band under Recite both move this number, which a
        /// fixed band index would miss in one direction or the other.</para></summary>
        public float SweepTrackAlpha
        {
            get
            {
                float alpha = 0f;

                foreach (var track in sweepTracks)
                    alpha = Math.Max(alpha, track.Alpha);

                foreach (var blend in sweepBlends)
                    alpha = Math.Max(alpha, blend.Alpha);

                return alpha;
            }
        }

        public float SweepFillAlpha => sweepFill.IsNotNull() ? sweepFill.Alpha : 0f;

        /// <summary>The colour a cell is currently drawn in; test support for the freestyle tint.</summary>
        public ColourInfo CellColour(int index) =>
            index >= 0 && index < cells.Length ? cells[index].Colour : ColourInfo.SingleColour(osuTK.Graphics.Color4.White);

        /// <summary>The whole line's on-screen width with every cell occupying its slot (after the
        /// auto-shrink scale). The display's own <c>DrawWidth</c> must equal this at all times, even
        /// when the flashlight has hidden most cells; a smaller <c>DrawWidth</c> means the layout
        /// collapsed to the lit run and the line would re-centre. Test support for the stability pin.</summary>
        public float FullOnScreenWidth => FullSweepWidth * contentScale;

        /// <summary>Screen-space centre of a cell's glyph; test support for asserting a char does not
        /// move as the flashlight window changes.</summary>
        public Vector2 CellScreenPosition(int index) =>
            index >= 0 && index < cells.Length ? cells[index].ScreenSpaceDrawQuad.Centre : Vector2.Zero;

        /// <summary>Screen-space quad of a cell's glyph sprite; test support for the lyric size scene
        /// (backlog 334), which pins every adornment against the text it belongs to.</summary>
        public osu.Framework.Graphics.Primitives.Quad CellScreenQuad(int index) =>
            index >= 0 && index < cells.Length ? cells[index].ScreenSpaceDrawQuad : default;

        /// <summary>Screen-space quad of the sweep rail's first band (every band shares its Y and
        /// thickness); test support for the lyric size scene.</summary>
        public osu.Framework.Graphics.Primitives.Quad RailScreenQuad => sweepTracks.Length > 0 ? sweepTracks[0].ScreenSpaceDrawQuad : default;

        /// <summary>Screen-space quad of the syllable marker at <paramref name="cellIndex"/>, or the
        /// default quad when no marker sits there; test support for the lyric size scene.</summary>
        public osu.Framework.Graphics.Primitives.Quad SyllableMarkerScreenQuad(int cellIndex) =>
            markerIndexAt(cellIndex) is int k ? syllableMarkers[k].ScreenSpaceDrawQuad : default;

        /// <summary>First cell of the painted retype selection (see <see cref="SetSelection"/>).</summary>
        public int SelectionStart { get; private set; }

        /// <summary>One past the last cell of the painted retype selection; equal to
        /// <see cref="SelectionStart"/> when nothing is selected.</summary>
        public int SelectionEnd { get; private set; }

        /// <summary>Whether the selection highlight is actually being drawn right now.</summary>
        public bool SelectionVisible => selectionBox.IsNotNull() && selectionBox.Alpha > 0f;

        /// <summary>How many word gaps this line has a dot drawable for (see
        /// <see cref="ComputeSpaceErrorDots"/>); test support.</summary>
        public int SpaceErrorDotCount => gapDots.Length;

        /// <summary>How many PACE BANDS this line's underline is cut into (see
        /// <see cref="buildPaceTracks"/>); 1 for a display built with no hue. Test support.</summary>
        public int PaceTrackCount => sweepTracks.Length;

        /// <summary>The colour a pace band is actually drawn in; test support for the hue.</summary>
        public ColourInfo PaceTrackColour(int index) =>
            index >= 0 && index < sweepTracks.Length ? sweepTracks[index].Colour : ColourInfo.SingleColour(osuTK.Graphics.Color4.White);

        /// <summary>One pace band's own alpha, which the flashlight drives; test support for "every
        /// band fades", which is the band-array half of the pin
        /// <see cref="SweepTrackAlpha"/> carries for the rail as a whole.</summary>
        public float PaceTrackAlpha(int index) => index >= 0 && index < sweepTracks.Length ? sweepTracks[index].Alpha : 0f;

        /// <summary>A pace band's full logical width, including its halves of neighbouring blends.</summary>
        public float PaceTrackWidth(int index) => index >= 0 && index < trackBands.Length
            ? cellX[trackBands[index].EndCellExclusive] - cellX[trackBands[index].StartCell] : 0f;

        /// <summary>A pace band's full logical left edge.</summary>
        public float PaceTrackX(int index) => index >= 0 && index < trackBands.Length
            ? cellX[trackBands[index].StartCell] : 0f;

        /// <summary>The half-open cell range a pace band covers; test support.</summary>
        public (int StartCell, int EndCellExclusive) PaceTrackRange(int index) =>
            index >= 0 && index < trackBands.Length ? (trackBands[index].StartCell, trackBands[index].EndCellExclusive) : (0, 0);

        /// <summary>The whole line's current dim alpha (see <see cref="SetLineDim"/>), which every
        /// child including the pace bands is multiplied by. Test support for the preview-line pin.</summary>
        public float ContentAlpha => content.IsNotNull() ? content.Alpha : 0f;

        /// <summary>Whether the space error dot for the gap at <paramref name="cellIndex"/> is
        /// actually being drawn right now; false for any cell that is not a gap. Test support.</summary>
        public bool SpaceErrorDotVisibleAt(int cellIndex)
        {
            for (int k = 0; k < gapCells.Length; k++)
            {
                if (gapCells[k] == cellIndex)
                    return gapDots[k].Alpha > 0f;
            }

            return false;
        }

        /// <summary>How many syllable markers this line has a drawable for; test support.</summary>
        public int SyllableMarkerCount => syllableMarkers.Length;

        /// <summary>Whether the syllable marker at <paramref name="cellIndex"/> is actually being
        /// drawn right now; false for any cell that carries no marker. Test support.</summary>
        public bool SyllableMarkerVisibleAt(int cellIndex) =>
            markerIndexAt(cellIndex) is int k && syllableMarkers[k].Alpha > 0f;

        /// <summary>Display-local X of the syllable marker at <paramref name="cellIndex"/>, in the
        /// same space <see cref="PositionOfCell"/> reports so the two can be compared directly;
        /// <see cref="float.NaN"/> when no marker sits there. Test support for the pin that a mark
        /// lands on the character EDGE (the inter-character gap) and not inside a cell.</summary>
        public float SyllableMarkerLocalX(int cellIndex) =>
            markerIndexAt(cellIndex) is int k ? syllableMarkers[k].X * contentScale : float.NaN;

        private int? markerIndexAt(int cellIndex)
        {
            for (int k = 0; k < markerCells.Length; k++)
            {
                if (markerCells[k] == cellIndex)
                    return k;
            }

            return null;
        }
    }
}
