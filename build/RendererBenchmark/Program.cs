// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework;
using osu.Framework.Configuration;

namespace typebeat.Build.RendererBenchmark
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            var frameSync = args.Length > 0 ? Enum.Parse<FrameSync>(args[0]) : FrameSync.Limit4x;
            var windowMode = args.Length > 1 ? Enum.Parse<WindowMode>(args[1]) : WindowMode.Windowed;

            using var host = Host.GetSuitableDesktopHost("typebeat-metal-benchmark");
            host.Run(new RendererBenchmarkGame(frameSync, windowMode));
        }
    }
}
