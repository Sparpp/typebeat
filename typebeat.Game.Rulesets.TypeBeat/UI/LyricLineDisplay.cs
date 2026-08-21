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
    /// <para>A correctly typed char is tinted by how in sync its keypress was, on a ramp between the
    /// untyped grey and the full typed off-white (see <see cref="CorrectCharColour"/>), so the trail
    /// behind the caret reads as brightness.</para>
    ///
    /// <para>FREESTYLE cells (see <see cref="FreestyleGlyphs"/>) render in
    /// <see cref="TypeBeatStyle.FreestyleChar"/> and, while still open, shimmer through
    /// width-matched glyphs; once filled they freeze on the char the player pressed. Their advance
    /// is measured from a pool glyph at load, so nothing about the effect can move the line.</para>
    /// </summary>
    public partial class LyricLineDisplay : CompositeDrawable
    {
        private const float design_width = 1366f;
        private const float max_width_fraction = 0.9f;

        public TypingLine Line { get; }

        private readonly float requestedFontSize;

        /// <summary>Resolved gameplay-font family (null/empty = the built-in lyric font). Set at construction;
        /// the owning stage decides the value and guarantees the family is registered before it is used.</summary>
        private readonly string? fontFamily;

        private Container content = null!;
        private Box sweepTrack = null!;
        private Box sweepFill = null!;
        private Box sweepGlow = null!;
        private OsuSpriteText[] cells = Array.Empty<OsuSpriteText>();
        private float[] advances = Array.Empty<float>();

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
        /// the flashlight window multiplies on top of this so the two never clobber each other.</summary>
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

        /// <summary>Live view of whether the SUNG PLAYHEAD STYLE is
        /// <see cref="Configuration.CaretStyle.Highlight"/>, supplied by the owning stage; null =
        /// classic painting forever. That setting, and NOT
        /// <see cref="TypingEngine.SyllableTiming"/>, is what decides this rendering: the flag is a
        /// judgement rule, and <see cref="TypingLine.Syllables"/> is built for every line either way,
        /// so the highlight is just as correct under classic judgement. A delegate rather than a
        /// snapshot because the setting is live-bindable (a player can move the dropdown mid-play),
        /// so reading it at each repaint is what makes the switch apply without a restart.</summary>
        private readonly Func<bool>? highlightMode;

        private bool highlightActive => highlightMode?.Invoke() ?? false;

        /// <summary>Index into <see cref="TypingLine.Syllables"/> of the group currently being sung,
        /// -1 = none. Stage-fed (see <see cref="SetSungSyllable"/>); time-driven state, so it lives
        /// beside the sung sweep rather than in the pull-based cell states.</summary>
        private int sungSyllable = -1;

        public LyricLineDisplay(TypingLine line, float fontSize = TypeBeatStyle.LYRIC_FONT_SIZE, string? fontFamily = null, Func<bool>? highlightMode = null)
        {
            Line = line;
            requestedFontSize = fontSize;
            this.fontFamily = fontFamily;
            this.highlightMode = highlightMode;
            AutoSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            int n = Line.Cells.Count;
            cells = new OsuSpriteText[n];
            cellStateAlpha = new float[n];
            Array.Fill(cellStateAlpha, 1f);

            content = new Container { AutoSizeAxes = Axes.Both };

            sweepTrack = new Box
            {
                Colour = TypeBeatStyle.SungAccent.Opacity(0.20f),
                Height = 3,
                // Always present for layout: the full-width track pins the content's size (and its
                // lower vertical extent) even when the flashlight has faded it to alpha 0, so the
                // auto-size container never collapses to whatever happens to be lit right now.
                AlwaysPresent = true,
            };
            sweepFill = new Box
            {
                Colour = TypeBeatStyle.SungAccent.Opacity(0.60f),
                Height = 3,
                Width = 0,
            };
            sweepGlow = new Box
            {
                Colour = TypeBeatStyle.SungAccent,
                Height = 3,
                Width = 6,
                Origin = Anchor.TopCentre,
                Alpha = 0,
            };

            content.Add(sweepTrack);
            content.Add(sweepFill);
            content.Add(sweepGlow);

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
                    Text = Line.Cells[i].Expected.ToString(),
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
                };
                cells[i] = cell;
                content.Add(cell);
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
                float a = cells[i].DrawWidth > 0.1f
                    ? cells[i].DrawWidth
                    : Line.Cells[i].Expected == ' ' ? refAdvance * 0.55f : refAdvance;
                cellX[i] = x;
                advances[i] = a;
                x += a;
            }

            cellX[n] = x;

            // Auto-shrink guard: keep the rendered line within 90% of the design width.
            float total = cellX[n];
            float maxWidth = design_width * max_width_fraction;
            contentScale = total > maxWidth && total > 0f ? maxWidth / total : 1f;
            content.Scale = new Vector2(contentScale);

            for (int i = 0; i < n; i++)
                cells[i].Position = new Vector2(cellX[i] + advances[i] * 0.5f, glyphHeight * 0.5f);

            sweepTrack.Width = cellX[n];
            sweepTrack.Y = sweepFill.Y = sweepGlow.Y = glyphHeight + 6f;
        }

        /// <summary>Display-local caret anchor for a cell; <c>cellIndex == Cells.Count</c> is the end of the line.</summary>
        public Vector2 PositionOfCell(int cellIndex)
        {
            int i = Math.Clamp(cellIndex, 0, cellX.Length - 1);
            return new Vector2(cellX[i] * contentScale, 0f);
        }

        /// <summary>Display-local point for a fractional (sung) cell index.</summary>
        public Vector2 SungPositionPoint(double fractionalCellIndex) => new Vector2(localXFor(fractionalCellIndex) * contentScale, 0f);

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
        /// The fill a CORRECT character is painted in, given the sync quality of the keypress that
        /// scored it (<see cref="SyncWindows.SyncQuality"/>, asymmetric, already in [0, 1]): a point
        /// on the ramp from <see cref="TypeBeatStyle.UntypedChar"/> to
        /// <see cref="TypeBeatStyle.TypedChar"/>, compressed onto [<see cref="SYNC_TINT_FLOOR"/>, 1]
        /// of it. A player nailing the playhead leaves a bright white trail behind them; one dragging
        /// or rushing leaves a dull one.
        ///
        /// <para>Purely cosmetic, and deliberately driven by the SAME quality
        /// <c>TypingEngine.BuildResults</c> sums into the results screen's sync percent, so the trail
        /// is a live preview of the number the play is graded on rather than a second opinion about
        /// it (and it inherits the asymmetric early/late tolerance and the per-cell granularity
        /// widening for free). A dead-on press returns <see cref="TypeBeatStyle.TypedChar"/> exactly,
        /// so nothing about a perfectly timed line looks different from before this ramp existed.
        /// Out-of-range and NaN qualities clamp. Pure, so it is unit-testable.</para>
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
        /// The fill a cell is painted in, under either sung presentation; every colour decision the
        /// display makes routes through here, so pinning this function pins the rendering. Pure, so
        /// it is unit-testable beside <see cref="CorrectCharColour"/>.
        ///
        /// <para>Classic playhead (<paramref name="highlightMode"/> false) is EXACTLY the pre-174
        /// painting: Correct rides the sync-tint ramp on <paramref name="syncQuality"/> (flat
        /// <see cref="TypeBeatStyle.TypedChar"/> when null, the cannot-arise fallback), Wrong is
        /// <see cref="TypeBeatStyle.ErrorChar"/>, everything else the untyped grey, and
        /// <paramref name="inSungSyllable"/> has no effect at all.</para>
        ///
        /// <para><see cref="Configuration.CaretStyle.Highlight"/> playhead style: there IS no
        /// playhead, so "where the song is" is carried by the characters themselves. An Untyped cell
        /// of the group currently being sung wears <see cref="TypeBeatStyle.TypedChar"/> (the
        /// palette's white); Untyped anywhere else (not yet sung, or already sung past) stays the
        /// untyped grey. Correct is the flat <see cref="TypeBeatStyle.SyllableCorrectChar"/> green
        /// regardless of quality (a highlighted group reads as one unit, and under
        /// <see cref="TypingEngine.SyllableTiming"/> its presses really are all delta 0, so a quality
        /// ramp across it would be meaningless; see the colour's own doc). Wrong keeps the classic
        /// error red, and Missed/Abandoned/AutoSkipped keep the grey (their alphas, unchanged, carry
        /// the state).</para>
        ///
        /// <para>A FREESTYLE cell wears <see cref="TypeBeatStyle.FreestyleChar"/> under BOTH
        /// presentations and in every state: the violet is an identity signal ("this slot was free"),
        /// and neither the sync ramp nor the syllable highlight may repaint it (see
        /// <see cref="refreshFreestyleCell"/>; an exclusion, not an oversight).</para>
        /// </summary>
        public static Color4 CellFillColour(CellState state, bool isFreestyle, bool highlightMode, bool inSungSyllable, double? syncQuality)
        {
            if (isFreestyle)
                return TypeBeatStyle.FreestyleChar;

            if (highlightMode)
            {
                switch (state)
                {
                    case CellState.Correct:
                        return TypeBeatStyle.SyllableCorrectChar;

                    case CellState.Wrong:
                        return TypeBeatStyle.ErrorChar;

                    case CellState.Untyped:
                        return inSungSyllable ? TypeBeatStyle.TypedChar : TypeBeatStyle.UntypedChar;

                    default: // Missed, Abandoned, AutoSkipped
                        return TypeBeatStyle.UntypedChar;
                }
            }

            switch (state)
            {
                case CellState.Correct:
                    // A Correct cell with no delta cannot arise from the engine; if one ever does,
                    // fall back to the flat typed colour rather than to the dull ramp floor.
                    return syncQuality is double q ? CorrectCharColour(q) : TypeBeatStyle.TypedChar;

                case CellState.Wrong:
                    // Expected glyph shown in error red (not the typed char).
                    return TypeBeatStyle.ErrorChar;

                default: // Untyped, Missed, Abandoned, AutoSkipped
                    return TypeBeatStyle.UntypedChar;
            }
        }

        public void RefreshCell(int cellIndex)
        {
            if (cellIndex < 0 || cellIndex >= cells.Length)
                return;

            var cell = cells[cellIndex];
            var source = Line.Cells[cellIndex];

            if (source.IsFreestyle)
            {
                refreshFreestyleCell(cellIndex, cell, source);
                return;
            }

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
            double? syncQuality = source.JudgedDelta is double delta
                ? SyncWindows.For(source.JudgeGranularity).SyncQuality(delta)
                : null;

            bool inSungSyllable = sungSyllable >= 0 && Line.SyllableIndexOf(cellIndex) == sungSyllable;

            cell.Colour = CellFillColour(source.State, isFreestyle: false, highlightActive, inSungSyllable, syncQuality);

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

                default: // Untyped, Correct, Wrong, AutoSkipped
                    cellStateAlpha[cellIndex] = 1f;
                    break;
            }

            // Compose the state alpha with the flashlight window (a no-op multiplier of 1 when the
            // mod is off) so a state refresh can never undo the window's hiding, or vice versa.
            applyCellAlpha(cellIndex, animate: false);
        }

        /// <summary>
        /// A FREESTYLE cell always wears <see cref="TypeBeatStyle.FreestyleChar"/>, shimmering while
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
            // truth: freestyle identity wins in both timing modes.
            cell.Colour = CellFillColour(source.State, isFreestyle: true, highlightActive,
                inSungSyllable: sungSyllable >= 0 && Line.SyllableIndexOf(cellIndex) == sungSyllable, syncQuality: null);

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
                cell.MoveToX(baseX - 2f, 25)
                    .Then().MoveToX(baseX + 2f, 25)
                    .Then().MoveToX(baseX, 15, Easing.OutQuint);
            }
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

        /// <summary>
        /// The Highlight style's sibling of <see cref="SetSungPosition"/>: the stage feeds the index
        /// of the group currently being sung (-1 = none) each frame instead of a sweep position, and
        /// the Untyped cells of that group light up white (see <see cref="CellFillColour"/>).
        /// Cheap to call every frame: nothing repaints until the index CHANGES, and then only the
        /// cells whose colour actually depends on it, the untyped non-freestyle cells of the old
        /// and new groups, not the whole line. Membership is read through
        /// <see cref="TypingLine.SyllableIndexOf"/>, never by range: a hyphen-turned-space cell can
        /// sit positionally inside a group's cell range while being in no group.
        /// </summary>
        public void SetSungSyllable(int index)
        {
            if (index == sungSyllable)
                return;

            int previous = sungSyllable;
            sungSyllable = index;

            repaintUntypedCellsOf(previous);
            repaintUntypedCellsOf(index);
        }

        /// <summary>The group index last fed to <see cref="SetSungSyllable"/>; test support.</summary>
        public int SungSyllable => sungSyllable;

        private void repaintUntypedCellsOf(int group)
        {
            if (group < 0 || group >= Line.Syllables.Count)
                return;

            var g = Line.Syllables[group];

            for (int i = g.StartCell; i < g.EndCellExclusive && i < cells.Length; i++)
            {
                if (Line.SyllableIndexOf(i) != group)
                    continue;

                var source = Line.Cells[i];

                // Only Untyped non-freestyle cells wear the highlight, so nothing else can have
                // changed colour with the index.
                if (source.State == CellState.Untyped && !source.IsFreestyle)
                    RefreshCell(i);
            }
        }

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

            if (sweepTrack.IsNotNull())
            {
                float target = showSweep ? 1f : 0f;
                sweepTrack.FadeTo(target, flashlight_fade_ms);
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

            if (sweepTrack.IsNotNull())
            {
                sweepTrack.FadeTo(0f, flashlight_fade_ms);
                sweepFill.FadeTo(0f, flashlight_fade_ms);
                sweepGlow.FadeTo(0f, flashlight_fade_ms);
            }
        }

        private void reapplyFlashlight()
        {
            for (int i = 0; i < cells.Length; i++)
                applyCellAlpha(i, animate: true);
        }

        private void applyCellAlpha(int i, bool animate)
        {
            float target = cellStateAlpha[i] * flashlightFactor(i);

            if (animate)
                cells[i].FadeTo(target, flashlight_fade_ms, Easing.OutQuint);
            else
                cells[i].Alpha = target;
        }

        private float flashlightFactor(int i)
        {
            if (!flashlightEnabled)
                return 1f;
            if (flashlightHidden)
                return 0f;
            return i < flashlightAlphas.Length ? flashlightAlphas[i] : 0f;
        }

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
    }
}
