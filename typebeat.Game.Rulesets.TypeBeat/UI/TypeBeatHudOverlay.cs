// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported from type!beat TypeBeat.Game/UI/HudOverlay.cs, slimmed for the type!beat fork:
// score/combo/accuracy readouts dropped (type!beat's own HUD shows those from the
// ScoreProcessor); the SyncBar and hit-error meters were removed by design; the only
// engine-authoritative extras left are the WPM / sync% readouts.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// Playfield-level HUD extras: top-centre WPM / sync% readouts, polled from the engine
    /// each frame. Mounted under the playfield's lyric-offset clock container so
    /// <c>Time.Current</c> is lyric-gameplay time.
    /// </summary>
    public partial class TypeBeatHudOverlay : CompositeDrawable
    {
        private readonly TypingEngine engine;

        private OsuSpriteText wpmValue = null!;
        private OsuSpriteText syncValue = null!;

        public TypeBeatHudOverlay(TypingEngine engine)
        {
            this.engine = engine;
            RelativeSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            InternalChild = new FillFlowContainer
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Horizontal,
                Spacing = new osuTK.Vector2(36, 0),
                Margin = new MarginPadding { Top = 24 },
                Children = new[]
                {
                    stat("wpm", out wpmValue),
                    stat("sync", out syncValue),
                },
            };
        }

        private Drawable stat(string caption, out OsuSpriteText value)
        {
            value = new OsuSpriteText
            {
                Anchor = Anchor.TopCentre,
                Origin = Anchor.TopCentre,
                Font = TypeBeatStyle.Mono(30),
                Colour = TypeBeatStyle.TypedChar,
                Text = "0",
                ShadowColour = TypeBeatStyle.TextShadow,
                ShadowOffset = TypeBeatStyle.TEXT_SHADOW_OFFSET,
            };

            return new FillFlowContainer
            {
                AutoSizeAxes = Axes.Both,
                Direction = FillDirection.Vertical,
                Children = new Drawable[]
                {
                    new OsuSpriteText
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.TopCentre,
                        Font = TypeBeatStyle.Mono(14),
                        Colour = TypeBeatStyle.UntypedChar,
                        Text = caption,
                        ShadowColour = TypeBeatStyle.TextShadow,
                        ShadowOffset = TypeBeatStyle.TEXT_SHADOW_OFFSET,
                    },
                    value,
                },
            };
        }

        protected override void Update()
        {
            base.Update();

            wpmValue.Text = engine.LiveWpm.ToString("0");
            syncValue.Text = engine.LiveSyncPercent.ToString("0.0") + "%";
        }
    }
}
