// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Build.RendererBenchmark
{
    public partial class RendererBenchmarkGame(FrameSync frameSync, WindowMode windowMode) : osu.Framework.Game
    {
        private readonly Stopwatch timer = new Stopwatch();
        private readonly List<double> samples = new List<double>();
        private ulong firstFrame;
        private double sampleStart;
        private double nextSample = 5;

        [BackgroundDependencyLoader]
        private void load(FrameworkConfigManager config)
        {
            config.SetValue(FrameworkSetting.FrameSync, frameSync);
            config.SetValue(FrameworkSetting.WindowMode, windowMode);
            config.SetValue(FrameworkSetting.WindowedSize, new Size(1280, 720));

            Add(new Box { RelativeSizeAxes = Axes.Both, Colour = new Color4(16, 18, 27, 255) });

            for (int i = 0; i < 96; i++)
            {
                var text = new SpriteText
                {
                    Text = "type!beat  The quick brown fox  1234567890",
                    Font = new FontUsage(size: 17),
                    Position = new Vector2(i % 3 * 420, i / 3 * 22),
                    Colour = new Color4((byte)(120 + i), 190, 230, 255)
                };
                Add(text);
                text.FadeTo(0.5f, 500).Then().FadeTo(1, 500).Loop();
            }

            // Backgrounding the probe must not silently impose the framework's idle 60 FPS cap.
            // Samples report focus state; compare foreground runs with the same mode/resolution.
            Host.MaximumInactiveHz = 1000;
            timer.Start();
        }

        protected override void Update()
        {
            base.Update();

            double now = timer.Elapsed.TotalSeconds;
            if (now < nextSample)
                return;

            ulong frame = Host.Renderer.FrameIndex;
            if (sampleStart > 0)
            {
                double fps = (frame - firstFrame) / (now - sampleStart);
                samples.Add(fps);
                System.Console.WriteLine($"SAMPLE {fps:F1} fps; active={Host.IsActive.Value}; drawLimit={Host.MaximumDrawHz:F1}");
            }

            firstFrame = frame;
            sampleStart = now;
            nextSample = now + 1;

            if (samples.Count == 8)
            {
                System.Console.WriteLine($"RESULT {Host.RendererInfo}; {frameSync}; {windowMode}; {Host.Window.ClientSize}: {samples.Average():F1} fps");
                Host.Exit();
            }
        }
    }
}
