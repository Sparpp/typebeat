// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Graphics;
using osu.Framework.Utils;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// BACKING VOCALS (backlog 445): the line's backing words, drawn above it and never typed. Each one
    /// is hidden until it is sung, then POPS UP over its anchor (<see cref="BackingVocals.AnchorOf"/>, a
    /// fraction of the line's width): thrown up out of the lyric with a little overshoot, growing and
    /// fading in, and settling at its own tilt (<see cref="BackingTilt"/>). It then stays until the line
    /// leaves the stage, riding the line's own dim and fit like every other adornment here.
    ///
    /// <para>Everything is a pure function of the song time (<see cref="BackingPop"/>), so a seek, a
    /// rewind, a paused replay and a repeat play all show exactly the same words in exactly the same
    /// place, and the tilt is derived from the word itself rather than drawn at random, so a word leans
    /// the same way every time it is played.</para>
    ///
    /// <para>Kept out of the auto-size box (<see cref="Drawable.BypassAutoSizeAxes"/>), so a backing word
    /// can never move or widen the line.</para>
    ///
    /// <para>THE HIDING MODS HIDE THEM TOO. Recite's promise is that no word is on screen the player has
    /// not typed, and Flashlight's that nothing is lit outside its window; a backing word popping up
    /// over its anchor would break either, so while either mod is on (see LyricLineDisplay.Hiding.cs)
    /// no backing word shows, exactly as when the "Show backing vocals" setting is off.</para>
    /// </summary>
    public partial class LyricLineDisplay
    {
        /// <summary>How long a backing word's pop-up animation takes.</summary>
        public const double BACKING_POP_DURATION_MS = 280;

        /// <summary>The largest tilt either way, in degrees.</summary>
        public const float BACKING_MAX_TILT_DEGREES = 10f;

        /// <summary>A backing word's font size as a fraction of the lyric's.</summary>
        public const float BACKING_FONT_RATIO = 0.42f;

        /// <summary>The gap between the top of the glyph row (or the ruby above it) and a backing word's baseline, at the default size.</summary>
        public const float BACKING_GAP = 6f;

        /// <summary>How far below its resting place a backing word starts its throw, as a fraction of the glyph row.</summary>
        public const float BACKING_RISE = 0.6f;

        private OsuSpriteText[] backingTexts = Array.Empty<OsuSpriteText>();
        private float[] backingTilts = Array.Empty<float>();
        private Vector2[] backingRest = Array.Empty<Vector2>();
        private bool backingEnabled = true;

        /// <summary>The backing words this line shows (its source line's, see <see cref="LyricLine.BackingVocals"/>).</summary>
        private IReadOnlyList<BackingVocal> backingWords => Line.Source.BackingVocals;

        private void addBackingVocals()
        {
            var words = backingWords;

            backingTexts = new OsuSpriteText[words.Count];
            backingTilts = new float[words.Count];
            backingRest = new Vector2[words.Count];

            for (int i = 0; i < words.Count; i++)
            {
                backingTilts[i] = BackingTilt(words[i], i);
                backingTexts[i] = new OsuSpriteText
                {
                    Text = words[i].Text,
                    Font = TypeBeatStyle.Lyric(requestedFontSize * BACKING_FONT_RATIO, fontFamily),
                    Colour = TypeBeatStyle.TypedChar,
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.BottomCentre,
                    Alpha = 0,
                    BypassAutoSizeAxes = Axes.Both,
                    ShadowColour = TypeBeatStyle.TextShadow,
                    ShadowOffset = TypeBeatStyle.TEXT_SHADOW_OFFSET,
                    Depth = -1,
                };

                content.Add(backingTexts[i]);
            }
        }

        /// <summary>Lays the resting places out over the measured line: above the glyph row, or above the ruby when the line has any.</summary>
        private void layoutBackingVocals()
        {
            if (backingTexts.Length == 0)
                return;

            float width = cellX[^1];
            float ruby = furigana.Count > 0 ? requestedFontSize * 0.36f * 1.15f + 2 * SizeRatio : 0;
            float lift = ruby + BACKING_GAP * SizeRatio;

            for (int i = 0; i < backingTexts.Length; i++)
                backingRest[i] = new Vector2(width * (float)BackingVocals.AnchorOf(backingWords, i), -lift);

            updateBackingVocals(Time.Current);
        }

        private void updateBackingVocals(double time)
        {
            for (int i = 0; i < backingTexts.Length; i++)
            {
                var text = backingTexts[i];
                var pop = backingEnabled && !reciteEnabled && !flashlightEnabled ? BackingPop(time - backingWords[i].StartTime) : default;

                if (pop.Alpha <= 0)
                {
                    text.Alpha = 0;
                    continue;
                }

                text.Alpha = pop.Alpha;
                text.Position = backingRest[i] + new Vector2(0, glyphHeight * BACKING_RISE * pop.Lift);
                text.Scale = new Vector2(pop.Scale);
                text.Rotation = backingTilts[i] * pop.Tilt;
            }
        }

        /// <summary>Shows or hides the backing vocals (the "Show backing vocals" setting); live, and display only.</summary>
        public void SetBackingVocalsEnabled(bool enabled)
        {
            backingEnabled = enabled;

            if (IsLoaded)
                updateBackingVocals(Time.Current);
        }

        /// <summary>How many backing words this line draws (test support).</summary>
        internal int BackingVocalCount => backingTexts.Length;

        /// <summary>Backing word <paramref name="index"/>'s drawable (test support).</summary>
        internal OsuSpriteText BackingVocalText(int index) => backingTexts[index];

        /// <summary>Where backing word <paramref name="index"/> settles, in the line's own (unscaled) coordinates (test support).</summary>
        internal Vector2 BackingVocalRest(int index) => backingRest[index];

        /// <summary>The laid-out width of the line the anchors are fractions of (test support).</summary>
        internal float BackingLineWidth => cellX[^1];

        /// <summary>The pose of a popped-up backing word.</summary>
        /// <param name="Alpha">0 hidden (not sung yet), 1 fully shown.</param>
        /// <param name="Lift">How far below its resting place it still is, as a fraction of <see cref="BACKING_RISE"/> (1 at the start of the throw, 0 at rest; slightly negative while it overshoots).</param>
        /// <param name="Scale">Its size (grows to 1).</param>
        /// <param name="Tilt">How much of its tilt it has leaned into (0 upright, 1 at rest).</param>
        public readonly record struct BackingPose(float Alpha, float Lift, float Scale, float Tilt);

        /// <summary>
        /// The pose of a backing word <paramref name="elapsed"/> ms after it starts being sung: nothing
        /// before, then a throw that overshoots its resting place a little and settles there by
        /// <see cref="BACKING_POP_DURATION_MS"/>, fading in over the first third.
        /// </summary>
        public static BackingPose BackingPop(double elapsed)
        {
            if (elapsed < 0)
                return default;

            double progress = Math.Clamp(elapsed / BACKING_POP_DURATION_MS, 0, 1);
            float eased = (float)Interpolation.ValueAt(progress, 0d, 1d, 0d, 1d, Easing.OutBack);

            return new BackingPose(
                Alpha: (float)Math.Clamp(progress * 3, 0, 1),
                Lift: 1 - eased,
                Scale: 0.55f + 0.45f * eased,
                Tilt: eased);
        }

        /// <summary>
        /// A backing word's tilt, in -<see cref="BACKING_MAX_TILT_DEGREES"/>..+<see cref="BACKING_MAX_TILT_DEGREES"/>:
        /// random-looking, but DERIVED from the word (its text, its time and its place in the line) with a
        /// fixed hash, so the same word leans the same way on every play, in every replay and on every
        /// machine (string.GetHashCode is randomised per process, so it is deliberately not used).
        /// </summary>
        public static float BackingTilt(BackingVocal word, int index)
        {
            uint hash = 2166136261;

            void mix(uint value)
            {
                for (int b = 0; b < 4; b++)
                {
                    hash ^= (value >> (8 * b)) & 0xff;
                    hash *= 16777619;
                }
            }

            foreach (char c in word.Text)
                mix(c);

            mix((uint)(long)Math.Round(word.StartTime));
            mix((uint)index);

            // A final avalanche so neighbouring inputs land far apart.
            hash ^= hash >> 15;
            hash *= 0x2c1b3c6d;
            hash ^= hash >> 12;

            double unit = (hash % 20001) / 20000.0;
            return (float)((unit * 2 - 1) * BACKING_MAX_TILT_DEGREES);
        }
    }
}
