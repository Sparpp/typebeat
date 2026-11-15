// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using osu.Framework.Allocation;
using osu.Framework.Extensions.LocalisationExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Localisation;
using osuTK;

namespace typebeat.Game.Overlays.Toolbar
{
    public partial class DigitalClockDisplay : ClockDisplay
    {
        private OsuSpriteText realTime;
        private OsuSpriteText gameTime;

        private FillFlowContainer runningText;

        private bool showRuntime = true;

        public bool ShowRuntime
        {
            get => showRuntime;
            set
            {
                if (showRuntime == value)
                    return;

                showRuntime = value;
                updateMetrics();
            }
        }

        [BackgroundDependencyLoader]
        private void load(OsuColour colours)
        {
            AutoSizeAxes = Axes.Both;

            InternalChildren = new Drawable[]
            {
                realTime = new OsuSpriteText
                {
                    Font = OsuFont.Default.With(fixedWidth: true),
                    Spacing = new Vector2(-1.5f, 0),
                },
                runningText = new FillFlowContainer
                {
                    Y = 14,
                    Colour = colours.PinkLight,
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Horizontal,
                    Spacing = new Vector2(2, 0),
                    Children = new Drawable[]
                    {
                        new OsuSpriteText
                        {
                            Text = ToolbarStrings.TimeRunning,
                            Font = OsuFont.Default.With(size: 10, weight: FontWeight.SemiBold),
                        },
                        gameTime = new OsuSpriteText
                        {
                            Font = OsuFont.Default.With(size: 10, fixedWidth: true, weight: FontWeight.SemiBold),
                            Spacing = new Vector2(-0.5f, 0),
                        }
                    }
                },
            };

            updateMetrics();
        }

        protected override void UpdateDisplay(DateTimeOffset now)
        {
            realTime.Text = now.ToLocalisableString(@"HH:mm:ss");
            gameTime.Text = $"{new TimeSpan(TimeSpan.TicksPerSecond * (int)(Clock.CurrentTime / 1000)):c}";
        }

        private void updateMetrics()
        {
            runningText.FadeTo(showRuntime ? 1 : 0);
        }
    }
}
