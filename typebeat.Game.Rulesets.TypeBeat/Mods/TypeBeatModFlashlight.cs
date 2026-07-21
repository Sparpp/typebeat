// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using typebeat.Game.Configuration;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Darkens the playfield except a soft-cornered rectangle that tracks the typing caret (the
    /// "playhead"), so you can only read the lyric right around where you're typing. The effect
    /// stays off until the first line's caret appears, then fades in; combo shrinks the reveal.
    /// </summary>
    public partial class TypeBeatModFlashlight : ModFlashlight<TypeBeatHitObject>
    {
        [SettingSource("Flashlight size", "Multiplier applied to the size of the reveal.")]
        public override BindableFloat SizeMultiplier { get; } = new BindableFloat(1f)
        {
            MinValue = 0.5f,
            MaxValue = 2f,
            Precision = 0.1f,
        };

        [SettingSource("Change size based on combo", "Reduce the reveal as your combo grows.")]
        public override BindableBool ComboBasedSize { get; } = new BindableBool(true);

        // The reveal's half-height (px, before the combo/size multipliers); the rectangle is much
        // wider than tall so it frames the current line, not a big circle.
        public override float DefaultFlashlightSize => 58f;

        protected override Flashlight CreateFlashlight() => new TypeBeatFlashlight(this);

        private partial class TypeBeatFlashlight : Flashlight
        {
            private const float width_to_height = 3.4f; // reveal is this many times wider than tall

            [Resolved]
            private DrawableTypeBeatRuleset drawableRuleset { get; set; } = null!;

            private bool revealed;

            public TypeBeatFlashlight(ModFlashlight modFlashlight)
                : base(modFlashlight)
            {
            }

            protected override string FragmentShader => "RectangularFlashlight";

            protected override void LoadComplete()
            {
                base.LoadComplete();

                FlashlightSmoothness = 1.6f; // softer edges + rounded corners

                // Fade the darkening in from the start of gameplay. Previously it stayed fully
                // transparent until TryGetCaretScreenPosition first returned true — but the caret is
                // only "visible" (alpha > 0.5) mid-blink once a line is active, so on many maps that
                // moment was missed and the mod had no visible effect at all. Revealing on load
                // (centred until the caret is acquired) guarantees the effect while still following
                // the caret once typing begins.
                this.FadeInFromZero(FLASHLIGHT_FADE_DURATION, Easing.OutQuint);
            }

            protected override void UpdateFlashlightSize(float size) =>
                this.TransformTo(nameof(FlashlightSize), new Vector2(size * width_to_height, size), FLASHLIGHT_FADE_DURATION, Easing.OutQuint);

            protected override void Update()
            {
                base.Update();

                // Follow the caret's screen-space centre while a line is active; before the first
                // caret appears (and between lines, until one is acquired) keep the reveal centred on
                // the playfield rather than parked in the top-left corner.
                if (((TypeBeatPlayfield)drawableRuleset.Playfield).TryGetCaretScreenPosition(out var caret))
                {
                    FlashlightPosition = ToLocalSpace(caret);
                    revealed = true;
                }
                else if (!revealed)
                {
                    FlashlightPosition = ToLocalSpace(ScreenSpaceDrawQuad.Centre);
                }
            }
        }
    }
}
