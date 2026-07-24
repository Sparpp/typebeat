// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// A read-only text label that renders FREESTYLE markers the way gameplay does: shimmering
    /// through <see cref="FreestyleGlyphs"/> in <see cref="TypeBeatStyle.FreestyleChar"/>, with the
    /// rest of the string in the label's own colour. Used by the editor so a mapper sees exactly
    /// what they authored ("this slot is free") without launching a test play.
    ///
    /// <para>The text is split into runs: each maximal non-marker run is one sprite, each marker is
    /// its own sprite (so it can carry the freestyle colour and be re-lettered independently). The
    /// fixed-width readout font is what keeps the substitution from moving anything, every
    /// candidate glyph has the same advance there.</para>
    /// </summary>
    public partial class FreestyleTextFlow : FillFlowContainer
    {
        private readonly float fontSize;
        private readonly Color4 textColour;

        /// <summary>Marker sprites paired with their index in <see cref="Text"/> (the index
        /// decorrelates neighbouring markers' shimmer).</summary>
        private readonly List<(int Index, OsuSpriteText Sprite)> markers = new List<(int, OsuSpriteText)>();

        private string text = string.Empty;
        private int shimmerTick = int.MinValue;

        public FreestyleTextFlow(float fontSize, Color4 textColour)
        {
            this.fontSize = fontSize;
            this.textColour = textColour;

            AutoSizeAxes = Axes.Both;
            Direction = FillDirection.Horizontal;
        }

        public string Text
        {
            get => text;
            set
            {
                string incoming = value ?? string.Empty;

                if (incoming == text)
                    return;

                text = incoming;
                rebuild();
            }
        }

        private void rebuild()
        {
            Clear();
            markers.Clear();

            int runStart = 0;

            for (int i = 0; i <= text.Length; i++)
            {
                bool marker = i < text.Length && Typeability.IsFreestyle(text[i]);

                if (i < text.Length && !marker)
                    continue;

                if (i > runStart)
                    Add(sprite(text.Substring(runStart, i - runStart), textColour));

                if (marker)
                {
                    var glyph = sprite(FreestyleGlyphs.Glyph(FreestyleGlyphs.FIXED_WIDTH_POOL, shimmerTick, i).ToString(), TypeBeatStyle.FreestyleChar);
                    markers.Add((i, glyph));
                    Add(glyph);
                }

                runStart = i + 1;
            }
        }

        private OsuSpriteText sprite(string content, Color4 colour) => new OsuSpriteText
        {
            Anchor = Anchor.CentreLeft,
            Origin = Anchor.CentreLeft,
            Font = TypeBeatStyle.Mono(fontSize),
            Colour = colour,
            Text = content,
        };

        protected override void Update()
        {
            base.Update();

            if (markers.Count == 0)
                return;

            int tick = FreestyleGlyphs.TickFor(Time.Current);

            if (tick == shimmerTick)
                return;

            shimmerTick = tick;

            // The editor readout font is fixed-width, so every candidate is interchangeable and the
            // pool needs no measuring pass (unlike the proportional gameplay font).
            foreach ((int index, var glyphSprite) in markers)
                glyphSprite.Text = FreestyleGlyphs.Glyph(FreestyleGlyphs.FIXED_WIDTH_POOL, tick, index).ToString();
        }
    }
}
