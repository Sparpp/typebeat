// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Database;
using typebeat.Game.Screens.ImportLyrics;
using typebeat.Game.Utils;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// What <see cref="OsuGameBase.RegisterImportHandler"/> promises when it says "most recently
    /// registered will have precedence": a dropped file goes to ONE handler, the topmost one that
    /// claims its extension.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The registry is a stack rather than a flat list because two kinds of handler coexist. The
    /// permanent ones (beatmap, score and skin import, plus the lyric importer that turns a raw audio
    /// or lyrics drop into a new map) are registered once at startup. The transient ones are editor
    /// file choosers, which register on load and unregister on dispose, so an extension they claim is
    /// theirs only while their screen is up.
    /// </para>
    /// <para>
    /// The path overload (the one window drag and drop reaches) used to hand each extension group to
    /// EVERY matching handler, so with the editor setup screen open a single dropped .mp4 was consumed
    /// twice: applied to the map by the video chooser, and simultaneously routed into a brand new song
    /// import that then errored on it. Backing out of that failed import revealed the video had been
    /// applied anyway, which is what a double dispatch looks like from the outside.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class ImportDispatchTest
    {
        private OsuGameBase game = null!;

        [SetUp]
        public void SetUp() => game = new OsuGameBase();

        [Test]
        public async Task TopmostHandlerClaimingTheExtensionTakesItAlone()
        {
            var permanent = register(".mp4", ".mp3");
            var chooser = register(".mp4");

            await game.Import("clip.mp4").ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(chooser.Received, Is.EqualTo(new[] { "clip.mp4" }), "the most recently registered handler is the one with precedence");
                Assert.That(permanent.Received, Is.Empty, "precedence means the handler underneath does not get the file as well");
            });
        }

        [Test]
        public async Task EachExtensionGroupIsRoutedIndependently()
        {
            var permanent = register(".mp4", ".mp3");
            var chooser = register(".mp4");

            // One drop, two extensions: the shadowed one goes to the chooser, the one it does not claim
            // still falls through to the handler below.
            await game.Import("clip.mp4", "song.mp3").ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(chooser.Received, Is.EqualTo(new[] { "clip.mp4" }));
                Assert.That(permanent.Received, Is.EqualTo(new[] { "song.mp3" }));
            });
        }

        [Test]
        public async Task EveryFileInAGroupGoesToTheOneHandler()
        {
            var chooser = register(".mp4");

            await game.Import("one.mp4", "two.mp4").ConfigureAwait(false);

            Assert.That(chooser.Received, Is.EqualTo(new[] { "one.mp4", "two.mp4" }), "first match selects the handler, not the file");
        }

        [Test]
        public async Task AShadowedExtensionComesBackWhenTheHandlerAboveUnregisters()
        {
            var permanent = register(".mp4");
            var chooser = register(".mp4");

            // What leaving the editor setup screen does.
            game.UnregisterImportHandler(chooser);

            await game.Import("clip.mp4").ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(permanent.Received, Is.EqualTo(new[] { "clip.mp4" }));
                Assert.That(chooser.Received, Is.Empty);
            });
        }

        [Test]
        public async Task AGroupNobodyClaimsIsDroppedRatherThanThrowing()
        {
            var permanent = register(".mp4");

            await game.Import("notes.docx").ConfigureAwait(false);

            Assert.That(permanent.Received, Is.Empty);
        }

        [Test]
        public async Task BothOverloadsPickTheSameHandler()
        {
            var permanent = register(".mp4");
            var chooser = register(".mp4");

            await game.Import(new[] { new ImportTask("clip.mp4") }).ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(chooser.Received, Is.EqualTo(new[] { "clip.mp4" }), "the task overload already resolved to a single handler");
                Assert.That(permanent.Received, Is.Empty);
            });
        }

        #region The real overlap this was reported against

        [Test]
        public void TheEditorChoosersAndTheLyricImporterReallyDoOverlap()
        {
            // Guards the regression test below from going vacuous: it only proves something while the
            // editor resource choosers and the global lyric importer still share extensions. Video is
            // the reported case, audio is the identical one sitting next to it.
            Assert.Multiple(() =>
            {
                Assert.That(SupportedExtensions.VIDEO_EXTENSIONS.Intersect(LyricImportExtensions.ALL), Is.EqualTo(new[] { ".mp4" }));
                Assert.That(SupportedExtensions.AUDIO_EXTENSIONS.Intersect(LyricImportExtensions.ALL), Is.EquivalentTo(SupportedExtensions.AUDIO_EXTENSIONS));
                Assert.That(SupportedExtensions.IMAGE_EXTENSIONS.Intersect(LyricImportExtensions.ALL), Is.Empty);
            });
        }

        [TestCase("clip.mp4")]
        [TestCase("song.mp3")]
        public async Task DroppingOnTheEditorSetupScreenDoesNotAlsoStartANewSongImport(string path)
        {
            // Registration order as the running game builds it: the lyric importer at startup, then the
            // setup screen choosers when the editor opens on top of it.
            var lyricImporter = register(LyricImportExtensions.ALL);
            register(SupportedExtensions.IMAGE_EXTENSIONS);
            var videoChooser = register(SupportedExtensions.VIDEO_EXTENSIONS);
            var audioChooser = register(SupportedExtensions.AUDIO_EXTENSIONS);

            await game.Import(path).ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(lyricImporter.Received, Is.Empty, "a file the open editor claimed must not also open the new song import screen");
                Assert.That(videoChooser.Received.Concat(audioChooser.Received), Is.EqualTo(new[] { path }), "exactly one chooser applies it to the map");
            });
        }

        [Test]
        public async Task ThePackagedBeatmapPathIsUntouchedByAnyOfThis()
        {
            // The lyric import screen signs off by importing the .osz it produced, and nothing stacked
            // above beatmap import claims that extension, so first match must still reach it.
            var beatmaps = register(".osz", ".typb");
            var lyricImporter = register(LyricImportExtensions.ALL);
            register(SupportedExtensions.VIDEO_EXTENSIONS);

            await game.Import("produced.osz").ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(beatmaps.Received, Is.EqualTo(new[] { "produced.osz" }));
                Assert.That(lyricImporter.Received, Is.Empty);
            });
        }

        #endregion

        private RecordingHandler register(params string[] handledExtensions)
        {
            var handler = new RecordingHandler(handledExtensions);
            game.RegisterImportHandler(handler);
            return handler;
        }

        private class RecordingHandler : ICanAcceptFiles
        {
            public IEnumerable<string> HandledExtensions { get; }

            public List<string> Received { get; } = new List<string>();

            public RecordingHandler(params string[] handledExtensions)
            {
                HandledExtensions = handledExtensions;
            }

            public Task Import(params string[] paths)
            {
                Received.AddRange(paths);
                return Task.CompletedTask;
            }

            public Task Import(ImportTask[] tasks, ImportParameters parameters = default)
            {
                Received.AddRange(tasks.Select(t => t.Path));
                return Task.CompletedTask;
            }
        }
    }
}
