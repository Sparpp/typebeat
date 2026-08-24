// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using typebeat.Game.Database;

namespace typebeat.Game
{
    public partial class OsuGameBase
    {
        private readonly List<ICanAcceptFiles> fileImporters = new List<ICanAcceptFiles>();

        /// <summary>
        /// Register a global handler for file imports. Most recently registered will have precedence.
        /// </summary>
        /// <remarks>
        /// Precedence means EXCLUSIVITY, not just ordering: a group of dropped files goes to the first
        /// registered handler that claims their extension and to no other. Handlers whose extension sets
        /// overlap are registered at different times on purpose, so that a transient one (an editor file
        /// chooser, alive only while its screen is) shadows the permanent one underneath it for as long
        /// as it is on screen, then hands the extension straight back when it is disposed.
        /// </remarks>
        /// <param name="handler">The handler to register.</param>
        public void RegisterImportHandler(ICanAcceptFiles handler) => fileImporters.Insert(0, handler);

        /// <summary>
        /// Unregister a global handler for file imports.
        /// </summary>
        /// <param name="handler">The previously registered handler.</param>
        public void UnregisterImportHandler(ICanAcceptFiles handler) => fileImporters.Remove(handler);

        public async Task Import(params string[] paths)
        {
            if (paths.Length == 0)
                return;

            var filesPerExtension = paths.GroupBy(p => Path.GetExtension(p).ToLowerInvariant());

            foreach (var groups in filesPerExtension)
            {
                // First match only, matching the ImportTask overload below and the precedence
                // RegisterImportHandler documents. Handing a group to EVERY matching handler meant one
                // dropped .mp4 was consumed twice while the editor's setup screen was open: the video
                // chooser applied it to the map AND the lyric importer opened a new-song import screen
                // over the top of it, which then failed on a file that had already been dealt with.
                var importer = fileImporters.FirstOrDefault(i => i.HandledExtensions.Contains(groups.Key));

                if (importer != null)
                    await importer.Import(groups.ToArray()).ConfigureAwait(false);
            }
        }

        public virtual async Task Import(ImportTask[] tasks, ImportParameters parameters = default)
        {
            var tasksPerExtension = tasks.GroupBy(t => Path.GetExtension(t.Path).ToLowerInvariant());
            await Task.WhenAll(tasksPerExtension.Select(taskGroup =>
            {
                var importer = fileImporters.FirstOrDefault(i => i.HandledExtensions.Contains(taskGroup.Key));
                return importer?.Import(taskGroup.ToArray(), parameters) ?? Task.CompletedTask;
            })).ConfigureAwait(false);
        }

        public IEnumerable<string> HandledExtensions => fileImporters.SelectMany(i => i.HandledExtensions);
    }
}
