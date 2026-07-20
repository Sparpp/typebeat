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
    /// Darkens the playfield except a circular reveal that tracks the typing caret (the "playhead"),
    /// so you can only read the lyric right around where you're typing. Combo shrinks the reveal.
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

        public override float DefaultFlashlightSize => 250f;

        protected override Flashlight CreateFlashlight() => new TypeBeatFlashlight(this);

        private partial class TypeBeatFlashlight : Flashlight
        {
            [Resolved]
            private DrawableTypeBeatRuleset drawableRuleset { get; set; } = null!;

            public TypeBeatFlashlight(ModFlashlight modFlashlight)
                : base(modFlashlight)
            {
            }

            protected override string FragmentShader => "CircularFlashlight";

            protected override void UpdateFlashlightSize(float size) =>
                this.TransformTo(nameof(FlashlightSize), new Vector2(size), FLASHLIGHT_FADE_DURATION, Easing.OutQuint);

            protected override void Update()
            {
                base.Update();

                // Follow the caret's screen-space centre; while no line is active the caret hides and
                // the reveal simply stays where it last was (no jump to the origin).
                if (((TypeBeatPlayfield)drawableRuleset.Playfield).TryGetCaretScreenPosition(out var caret))
                    FlashlightPosition = ToLocalSpace(caret);
            }
        }
    }
}
