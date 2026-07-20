// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Logging;
using osu.Framework.Screens;
using typebeat.Game.Database;

namespace typebeat.Game.Screens.ImportLyrics
{
    /// <summary>
    /// Routes dropped raw audio/lyrics files (and command-line paths) to <see cref="ImportLyricsScreen"/>:
    /// fed to the screen when it is already current, otherwise it opens with the files pre-loaded.
    /// Registered as a global <see cref="ICanAcceptFiles"/> handler alongside beatmap import, so
    /// <c>.osz</c> drops keep flowing to <c>BeatmapManager</c> untouched (disjoint extension sets).
    /// </summary>
    public partial class LyricImportManager : Component, ICanAcceptFiles
    {
        [Resolved]
        private OsuGame game { get; set; } = null!;

        // Buffers files while a screen open is in flight so that a single drop of audio + lyrics
        // (imported as two separate extension groups) opens ONE screen with both, not two.
        private readonly List<string> pending = new List<string>();
        private bool pushScheduled;

        public IEnumerable<string> HandledExtensions => LyricImportExtensions.ALL;

        [BackgroundDependencyLoader]
        private void load()
        {
            game.RegisterImportHandler(this);
        }

        public Task Import(params string[] paths)
        {
            Schedule(() => routeToScreen(paths));
            return Task.CompletedTask;
        }

        public Task Import(ImportTask[] tasks, ImportParameters parameters = default)
        {
            string[] paths = tasks.Select(t => t.Path).ToArray();
            Schedule(() => routeToScreen(paths));
            return Task.CompletedTask;
        }

        private void routeToScreen(string[] paths)
        {
            if (paths.Length == 0)
                return;

            Logger.Log($"Routing {paths.Length} lyric-import file(s) to the import screen");

            if (game.ScreenStack.CurrentScreen is ImportLyricsScreen current)
            {
                current.AddFiles(paths);
                return;
            }

            pending.AddRange(paths);

            if (pushScheduled)
                return;

            pushScheduled = true;
            game.PerformFromScreen(screen =>
            {
                string[] toOpen = pending.ToArray();
                pending.Clear();
                pushScheduled = false;
                screen.Push(new ImportLyricsScreen(toOpen));
            });
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            game?.UnregisterImportHandler(this);
        }
    }
}
