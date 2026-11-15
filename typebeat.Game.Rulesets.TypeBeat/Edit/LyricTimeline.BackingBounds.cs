// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The LINE'S EDGES on the backing anchor row (PR 49). A backing word's anchor is a fraction of the
    /// stage's width, so it can pop up beside a short line as well as over it; two faint hollow rings,
    /// joined to nothing, mark where the line's lyric starts and ends in gameplay, so a mapper dragging an
    /// anchor sees when the word will leave the line's width.
    ///
    /// <para>Where the edges fall depends on how wide the line renders, which only the gameplay display
    /// knows (its measured glyphs, its hold gaps, its fit), so each line is measured by a real hidden
    /// <see cref="LyricLineDisplay"/> on a 16:9 stage (<see cref="LyricLineDisplay.DESIGN_WIDTH"/>), at
    /// this player's lyric size and font as gameplay would pick them, once per change to the line's
    /// words (<see cref="LineWidthProbe"/>). Another player's size or font puts them elsewhere.</para>
    /// </summary>
    public partial class LyricTimeline
    {
        /// <summary>How opaque a line-edge ring is drawn, as a share of an anchor's.</summary>
        private const float backing_bound_alpha = 0.35f;

        private readonly List<BackingLineBound> backingLineBounds = new List<BackingLineBound>();

        private LineWidthProbe lineWidthProbe = null!;

        /// <summary>Where a fraction of the way across <paramref name="line"/>'s band sits on screen.</summary>
        private float backingFractionX(LyricLine line, double fraction)
        {
            float from = PositionOf(line.StartTime);
            return from + (float)fraction * (PositionOf(line.EndTime) - from);
        }

        private void addBackingLineBounds(TypeBeatHitObject hitObject)
        {
            if (hitObject.Line.BackingVocals.Count == 0)
                return;

            backingAnchorLayer.Add(addTo(backingLineBounds, new BackingLineBound(hitObject, end: false)));
            backingAnchorLayer.Add(addTo(backingLineBounds, new BackingLineBound(hitObject, end: true)));
        }

        /// <summary>The fractions of the stage (and so of a line's band) the line's lyric starts and ends at in gameplay, or null until it is measured.</summary>
        internal (double Start, double End)? BackingLineEdges(TypeBeatHitObject hitObject)
            => lineWidthProbe.WidthFractionOf(hitObject) is float share ? (0.5 - share / 2, 0.5 + share / 2) : null;

        /// <summary>The line-edge rings the strip is drawing for <paramref name="hitObject"/>'s line (test support).</summary>
        internal IReadOnlyList<Drawable> BackingLineBoundsOf(TypeBeatHitObject hitObject) => backingLineBounds.Where(b => b.HitObject == hitObject).ToArray();

        /// <summary>
        /// A faint hollow ring on the anchor row at one end of the line's lyric in gameplay. Joined to no
        /// word and not draggable: it only shows where an anchor stops sitting over the line.
        /// </summary>
        private partial class BackingLineBound : CompositeDrawable, IHasTooltip
        {
            private const float ring_size = backing_anchor_radius * 2;

            [Resolved]
            private LyricEditState state { get; set; } = null!;

            private readonly bool end;

            public TypeBeatHitObject HitObject { get; }

            public LocalisableString TooltipText => end
                ? "Where this line's lyric ends in gameplay (at your lyric size): a backing vocal anchored past it pops up beside the line"
                : "Where this line's lyric starts in gameplay (at your lyric size): a backing vocal anchored before it pops up beside the line";

            public BackingLineBound(TypeBeatHitObject hitObject, bool end)
            {
                HitObject = hitObject;
                this.end = end;

                RelativePositionAxes = Axes.Y;
                Y = backing_anchor_y;
                Origin = Anchor.Centre;
                Size = new Vector2(ring_size);
                Alpha = 0;

                InternalChildren = new Drawable[]
                {
                    new Circle
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = TypeBeatStyle.TypedChar,
                    },
                    new Circle
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Size = new Vector2(ring_size - 4),
                        Colour = TypeBeatStyle.Background,
                    },
                };
            }

            public void UpdateLayout(LyricTimeline parent)
            {
                float y = parent.backingAnchorRowY;

                if (HitObject.Line.BackingVocals.Count == 0 || state.HiddenByTapScope(HitObject) || y >= 1 || parent.BackingLineEdges(HitObject) is not { } edges)
                {
                    Alpha = 0;
                    return;
                }

                Alpha = (state.ActiveLine.Value == HitObject ? 1 : 0.55f) * backing_bound_alpha;
                X = parent.backingFractionX(HitObject.Line, end ? edges.End : edges.Start);
                Y = y;
            }
        }

        /// <summary>
        /// Measures how wide each line renders in gameplay, as a share of a 16:9 stage, with a hidden
        /// <see cref="LyricLineDisplay"/> built the way gameplay builds one (no mods). A line is measured
        /// again only when its words change (its text, original or units, which every edit but a backing
        /// one replaces), and the last width stands while it is, so an anchor drag never blinks the rings.
        /// </summary>
        private partial class LineWidthProbe : CompositeDrawable
        {
            [Resolved]
            private EditorBeatmap editorBeatmap { get; set; } = null!;

            [Resolved(CanBeNull = true)]
            private TypeBeatRulesetConfigManager? config { get; set; }

            [Resolved(CanBeNull = true)]
            private LyricFontManager? fontManager { get; set; }

            [Resolved(CanBeNull = true)]
            private IBindable<WorkingBeatmap>? workingBeatmap { get; set; }

            private readonly Dictionary<TypeBeatHitObject, Measure> measures = new Dictionary<TypeBeatHitObject, Measure>();

            private readonly Bindable<float> fontSize = new Bindable<float>(TypeBeatStyle.LYRIC_FONT_SIZE);
            private readonly Bindable<string> fontFamily = new Bindable<string>(string.Empty);
            private readonly Bindable<bool> useMapFonts = new Bindable<bool>(true);

            // The family the displays are built in, resolved once (and again when a font setting changes).
            private string? resolvedFamily;
            private bool familyResolved;

            public LineWidthProbe()
            {
                // The stage a 16:9 window gives the lines; never drawn, but laid out and updated.
                Width = LyricLineDisplay.DESIGN_WIDTH;
                Height = 1;
                Alpha = 0;
                AlwaysPresent = true;
            }

            [BackgroundDependencyLoader]
            private void load()
            {
                if (config == null)
                    return;

                config.BindWith(TypeBeatRulesetSetting.LyricFontSize, fontSize);
                config.BindWith(TypeBeatRulesetSetting.LyricFont, fontFamily);
                config.BindWith(TypeBeatRulesetSetting.UseMapFonts, useMapFonts);
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();

                // A different size or font moves every line's edges.
                fontSize.BindValueChanged(_ => remeasureAll());
                fontFamily.BindValueChanged(_ => remeasureAll());
                useMapFonts.BindValueChanged(_ => remeasureAll());
            }

            private void remeasureAll()
            {
                familyResolved = false;

                foreach (var measure in measures.Values)
                    measure.Measured = null;
            }

            /// <summary>The share of the stage <paramref name="hitObject"/>'s line takes in gameplay, or null until it is first measured.</summary>
            public float? WidthFractionOf(TypeBeatHitObject hitObject)
            {
                var line = hitObject.Line;

                if (!measures.TryGetValue(hitObject, out var measure))
                    measures[hitObject] = measure = new Measure();

                if (measure.Measured == null || !sameWords(measure.Measured, line))
                    start(hitObject, measure, line);

                return measure.Fraction;
            }

            private static bool sameWords(LyricLine a, LyricLine b)
                => ReferenceEquals(a, b) || (a.RawText == b.RawText && a.Original == b.Original && ReferenceEquals(a.Units, b.Units));

            private void start(TypeBeatHitObject hitObject, Measure measure, LyricLine line)
            {
                if (measure.Display != null)
                    RemoveInternal(measure.Display, true);

                if (!familyResolved)
                {
                    var metadata = editorBeatmap.BeatmapInfo.Metadata;
                    resolvedFamily = LyricFontResolution.ResolveForMap(config, fontManager, metadata.LyricFont, metadata.LyricFontFile, workingBeatmap?.Value);
                    familyResolved = true;
                }

                measure.Measured = line;

                var engine = new TypingEngine(new LyricBeatmap
                {
                    Metadata = new LyricBeatmapMetadata { Artist = string.Empty, Title = string.Empty, FolderPath = string.Empty, AudioFileName = string.Empty },
                    Lines = new[] { line },
                    Granularity = hitObject.Granularity,
                });

                AddInternal(measure.Display = new LyricLineDisplay(engine.Lines[0], engine, fontSize.Value, resolvedFamily)
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                });
            }

            protected override void Update()
            {
                base.Update();

                foreach (var (hitObject, measure) in measures)
                {
                    // Read on the frame after it loaded, once it has fitted itself to this stage.
                    if (measure.Display is not { IsLoaded: true } display || display.FullSweepWidth <= 0)
                        continue;

                    if (!measure.Settled)
                    {
                        measure.Settled = true;
                        continue;
                    }

                    measure.Fraction = Math.Clamp(display.FullOnScreenWidth / DrawWidth, 0, 1);
                    measure.Display = null;
                    measure.Settled = false;
                    RemoveInternal(display, true);
                }
            }

            /// <summary>Forgets the lines no longer in the map.</summary>
            public void Prune()
            {
                foreach (var gone in measures.Keys.Where(h => !editorBeatmap.HitObjects.Contains(h)).ToArray())
                {
                    if (measures[gone].Display is LyricLineDisplay display)
                        RemoveInternal(display, true);

                    measures.Remove(gone);
                }
            }

            private class Measure
            {
                /// <summary>The line being (or last) measured; null to measure again.</summary>
                public LyricLine? Measured;

                public float? Fraction;
                public LyricLineDisplay? Display;
                public bool Settled;
            }
        }
    }
}
