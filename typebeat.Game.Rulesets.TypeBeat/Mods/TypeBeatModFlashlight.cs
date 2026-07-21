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

                // Start invisible (no darkening) — the effect fades in once the first caret appears,
                // so there's no stray reveal parked in the top-left before typing begins.
                Alpha = 0;
                FlashlightSmoothness = 1.6f; // softer edges + rounded corners
            }

            protected override void UpdateFlashlightSize(float size) =>
                this.TransformTo(nameof(FlashlightSize), new Vector2(size * width_to_height, size), FLASHLIGHT_FADE_DURATION, Easing.OutQuint);

            protected override void Update()
            {
                base.Update();

                // Follow the caret's screen-space centre; while no line is active the caret is hidden
                // and the reveal simply holds its last position (no jump to the origin).
                if (((TypeBeatPlayfield)drawableRuleset.Playfield).TryGetCaretScreenPosition(out var caret))
                {
                    FlashlightPosition = ToLocalSpace(caret);

                    if (!revealed)
                    {
                        revealed = true;
                        this.FadeIn(600, Easing.OutQuint);
                    }
                }
            }
        }
    }
}
