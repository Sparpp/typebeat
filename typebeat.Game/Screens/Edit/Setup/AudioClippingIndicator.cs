// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osuTK;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;

namespace typebeat.Game.Screens.Edit.Setup
{
    /// <summary>
    /// The line under the editor's audio gain bar: a dot and a sentence about whether the gain the
    /// mapper has dialled in leaves the song's own peaks inside the signal's range, turning red when
    /// any part of the map clips (see <see cref="Audio.Effects.AudioGain.WouldClip"/>).
    ///
    /// <para>It exists because a gain is the one audio control whose failure is inaudible as a fault:
    /// clipping does not sound like an error, it sounds like the song being loud, and it is heard as
    /// distortion on exactly the peaks a mapper was trying to lift. So the bar says "this much gain"
    /// and this says what that costs.</para>
    ///
    /// <para>THREE STATES, one of which is not a verdict. Before the track's waveform has been analysed
    /// there is nothing to be right or wrong about, and the indicator says so rather than showing a
    /// reassuring green it cannot back up.</para>
    /// </summary>
    public partial class AudioClippingIndicator : CompositeDrawable
    {
        private const float dot_size = 8;

        private Box dot = null!;
        private OsuSpriteText text = null!;

        private double? peak;
        private double gain = 1;

        [Resolved]
        private OsuColour colours { get; set; } = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChildren = new Drawable[]
            {
                dot = new Box
                {
                    Size = new Vector2(dot_size),
                    Anchor = Anchor.CentreLeft,
                    Origin = Anchor.CentreLeft,
                },
            };

            AddInternal(text = new OsuSpriteText
            {
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Margin = new MarginPadding { Left = dot_size + 8 },
                Font = OsuFont.Default.With(size: 13),
            });

            updateAppearance();
        }

        /// <summary>
        /// The loudest sample the track was analysed to have, 0..1, or null while that analysis is still
        /// running (or unavailable: a map with no decodable audio has no peaks to be over).
        /// </summary>
        public double? Peak
        {
            get => peak;
            set
            {
                if (value == peak)
                    return;

                peak = value;
                updateAppearance();
            }
        }

        /// <summary>
        /// The gain the bar is currently asking for. Not the dB the effect takes, because what the
        /// mapper chose IS a multiplier and the comparison is theirs to follow.
        /// </summary>
        public double Gain
        {
            get => gain;
            set
            {
                if (value == gain)
                    return;

                gain = value;
                updateAppearance();
            }
        }

        /// <summary>
        /// Whether the current gain pushes part of the map past full scale. False while unanalysed - see
        /// the class: not knowing is not the same as being fine, which is why the wording separates them.
        /// </summary>
        public bool Clipping => peak is double p && Audio.Effects.AudioGain.WouldClip(p, gain);

        private void updateAppearance()
        {
            if (peak is not double measured)
            {
                dot.Colour = OsuColour.Gray(0.4f);
                text.Colour = OsuColour.Gray(0.6f);
                text.Text = "Reading the song's peaks...";
                return;
            }

            if (Audio.Effects.AudioGain.WouldClip(measured, gain))
            {
                dot.Colour = colours.Red;
                text.Colour = colours.Red;
                text.Text = $"Clipping: the song's peaks are already at {measured * 100:0}%, so {gain * 100:0}% gain cuts the loudest parts off";
                return;
            }

            dot.Colour = colours.Green;
            text.Colour = OsuColour.Gray(0.7f);
            text.Text = $"No clipping at this gain (peaks reach {measured * 100:0}%)";
        }
    }
}
