// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Platform;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Tests.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 396: after the user accepts the UPDATE, the stem file must land LOCALLY. The server's
    /// package for the new version carries <c>vocals.ogg</c> as an ordinary entry, and the online import
    /// routes every archive entry through <see cref="RealmArchiveModelImporter{TModel}"/> with no
    /// allow-list, so the entry lands in the set's file list and <see cref="VocalsStem.FilenameIn"/> finds
    /// it. This pins that end: import an .osz that carries a stem beside the audio and assert the file
    /// list (what the editor's Vocals Waveform toggle reads) has it.
    /// </summary>
    [TestFixture]
    public class StemImportLandsTest
    {
        private const string realm_filename = "client.realm";

        private string directory = null!;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "typebeat-stem-import-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            LyricBeatmapDecoder.Register();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // a leftover temp directory is not worth failing a test over.
            }
        }

        [Test]
        public async Task AVocalsStemInThePackageLandsInTheLocalFileList()
        {
            using var context = new SynchronousRealmTestContext();
            var storage = new NativeStorage(Path.Combine(directory, "data"));

            string oszPath = Path.Combine(directory, "set.osz");

            using (var zip = ZipFile.Open(oszPath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("Synth Rider - Neon Nights (mapper) [Normal].osu").Open(), new UTF8Encoding(false)))
                    await writer.WriteAsync(encode()).ConfigureAwait(false);

                using (var writer = new StreamWriter(zip.CreateEntry("audio.mp3").Open()))
                    await writer.WriteAsync("fake audio").ConfigureAwait(false);

                // The stem the server attached: an ordinary entry, no allow-list to clear.
                using (var writer = new StreamWriter(zip.CreateEntry("vocals.ogg").Open()))
                    await writer.WriteAsync("fake stem").ConfigureAwait(false);
            }

            using var realm = new RealmAccess(storage, realm_filename);

            realm.Write(r => r.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0)
            {
                Available = true,
            }));

            var importer = new BeatmapImporter(storage, realm);
            var imported = await importer.Import(new ImportTask(oszPath)).ConfigureAwait(false);

            Assert.That(imported, Is.Not.Null, "the import itself failed");

            var filenames = realm.Run(r => r.All<BeatmapSetInfo>().Single().Files.Select(f => f.Filename).ToList());
            string? stem = realm.Run(r => VocalsStem.FilenameIn(r.All<BeatmapSetInfo>().Single()));

            Assert.Multiple(() =>
            {
                Assert.That(filenames, Contains.Item("vocals.ogg"), "the stem is a plain archive entry");
                Assert.That(stem, Is.EqualTo(VocalsStem.OGG_FILENAME), "and VocalsStem finds it, which enables the editor toggle");
            });
        }

        /// <summary>A minimal type!beat map the importer can decode (mirrors FileMetadataImportTest).</summary>
        private static string encode()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.BeatmapInfo.DifficultyName = "Normal";
            beatmap.Metadata.Artist = "Synth Rider";
            beatmap.Metadata.Title = "Neon Nights";
            beatmap.Metadata.AudioFile = "audio.mp3";

            var line = new LyricLine
            {
                RawText = "hello world",
                StartTime = 1000,
                EndTime = 3000,
                SingEndTime = 2800,
                Units = new[]
                {
                    new TimedUnit { Text = "hello", StartTime = 1000, EndTime = 1900, Source = TimingSource.Explicit },
                    new TimedUnit { Text = "world", StartTime = 1900, EndTime = 2800, Source = TimingSource.Explicit },
                },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = line.StartTime,
                LineIndex = 0,
                Line = line,
                Granularity = TimingGranularity.Word,
            });

            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(beatmap, null, sw);

            return sb.ToString();
        }
    }
}
