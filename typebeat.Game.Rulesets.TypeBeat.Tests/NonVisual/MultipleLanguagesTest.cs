// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Platform;
using Realms;
using Realms.Schema;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Select;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 472: a map may declare several song languages. Pins every layer the list passes through on the game side:
    /// the list rules (normalisation, the editor toggle, the primary language), the .osu lines (a <c>Language:</c> line
    /// that old readers keep reading and a <c>Languages:</c> list beside it, written only for more than one language),
    /// realm schema 68's migration of the old single value, the importer's field-by-field copy, the file stamp, and song
    /// select's filter and grouping. The website half is pinned in typebeat-web, and the two are held together by its
    /// WireCompat <c>LanguageWireTest</c>.
    /// </summary>
    [TestFixture]
    public class MultipleLanguagesTest
    {
        private const string realm_filename = "client.realm";

        private static readonly BeatmapLanguage[] japanese_english = { BeatmapLanguage.Japanese, BeatmapLanguage.English };

        private string directory = null!;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "typebeat-languages-" + Guid.NewGuid().ToString("N"));
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

        // ---- the list rules ----

        [Test]
        public void NormaliseDropsUnspecifiedAndDuplicatesAndKeepsOrder()
        {
            var normalised = BeatmapLanguageExtensions.Normalise(new[]
            {
                BeatmapLanguage.Korean, BeatmapLanguage.Unspecified, BeatmapLanguage.English, BeatmapLanguage.Korean, (BeatmapLanguage)999,
            });

            Assert.That(normalised, Is.EqualTo(new[] { BeatmapLanguage.Korean, BeatmapLanguage.English }));
        }

        [Test]
        public void InstrumentalIsExclusive_TheFirstEntryDecides()
        {
            Assert.Multiple(() =>
            {
                Assert.That(BeatmapLanguageExtensions.Normalise(new[] { BeatmapLanguage.Instrumental, BeatmapLanguage.English }),
                    Is.EqualTo(new[] { BeatmapLanguage.Instrumental }));
                Assert.That(BeatmapLanguageExtensions.Normalise(new[] { BeatmapLanguage.English, BeatmapLanguage.Instrumental }),
                    Is.EqualTo(new[] { BeatmapLanguage.English }));
                // Other is a real language, so it sits beside listed ones.
                Assert.That(BeatmapLanguageExtensions.Normalise(new[] { BeatmapLanguage.English, BeatmapLanguage.Other }),
                    Is.EqualTo(new[] { BeatmapLanguage.English, BeatmapLanguage.Other }));
            });
        }

        [Test]
        public void TheToggleAddsRemovesAndLetsTheNewPickWinAnExclusivityClash()
        {
            var none = Array.Empty<BeatmapLanguage>();

            var japanese = BeatmapLanguageExtensions.Toggle(none, BeatmapLanguage.Japanese);
            var both = BeatmapLanguageExtensions.Toggle(japanese, BeatmapLanguage.English);
            var instrumental = BeatmapLanguageExtensions.Toggle(both, BeatmapLanguage.Instrumental);
            var backToKorean = BeatmapLanguageExtensions.Toggle(instrumental, BeatmapLanguage.Korean);

            Assert.Multiple(() =>
            {
                Assert.That(japanese, Is.EqualTo(new[] { BeatmapLanguage.Japanese }));
                Assert.That(both, Is.EqualTo(japanese_english));
                Assert.That(BeatmapLanguageExtensions.Toggle(both, BeatmapLanguage.Japanese), Is.EqualTo(new[] { BeatmapLanguage.English }), "a picked language unpicks");
                Assert.That(instrumental, Is.EqualTo(new[] { BeatmapLanguage.Instrumental }), "Instrumental clears every language");
                Assert.That(backToKorean, Is.EqualTo(new[] { BeatmapLanguage.Korean }), "a language clears Instrumental");
                Assert.That(BeatmapLanguageExtensions.Toggle(japanese, BeatmapLanguage.Japanese), Is.Empty, "unpicking the last one leaves the map unspecified");
            });
        }

        [TestCase(new BeatmapLanguage[0], BeatmapLanguage.Unspecified)]
        [TestCase(new[] { BeatmapLanguage.English }, BeatmapLanguage.English)]
        [TestCase(new[] { BeatmapLanguage.English, BeatmapLanguage.Japanese }, BeatmapLanguage.Japanese)]
        [TestCase(new[] { BeatmapLanguage.Japanese, BeatmapLanguage.English }, BeatmapLanguage.Japanese)]
        [TestCase(new[] { BeatmapLanguage.Korean, BeatmapLanguage.Japanese }, BeatmapLanguage.Korean)]
        [TestCase(new[] { BeatmapLanguage.English, BeatmapLanguage.Other }, BeatmapLanguage.Other)]
        [TestCase(new[] { BeatmapLanguage.Instrumental }, BeatmapLanguage.Instrumental)]
        public void ThePrimaryIsTheFirstLanguageThatIsNotEnglish(BeatmapLanguage[] languages, BeatmapLanguage primary)
        {
            var metadata = new BeatmapMetadata { Languages = languages };

            Assert.Multiple(() =>
            {
                Assert.That(BeatmapLanguageExtensions.PrimaryOf(languages), Is.EqualTo(primary));
                Assert.That(metadata.Language, Is.EqualTo(primary), "the allocation-free stored read agrees with PrimaryOf");
            });
        }

        [Test]
        public void SettingTheSingleLanguageReplacesTheWholeList()
        {
            var metadata = new BeatmapMetadata { Languages = japanese_english };

            metadata.Language = BeatmapLanguage.Korean;
            Assert.That(metadata.Languages, Is.EqualTo(new[] { BeatmapLanguage.Korean }));

            metadata.Language = BeatmapLanguage.Unspecified;
            Assert.That(metadata.Languages, Is.Empty);
        }

        [Test]
        public void DeepCloneCarriesTheWholeList()
        {
            var metadata = new BeatmapMetadata { Languages = japanese_english };

            Assert.That(metadata.DeepClone().Languages, Is.EqualTo(japanese_english));
        }

        [Test]
        public void ACommaListIsNeverParsedAsAFlagsCombination()
        {
            // Reproduces the trap the new line sidesteps: Enum.TryParse reads "japanese,english" as Japanese | English,
            // which is 3, which is Chinese. FromCanonicalName now refuses anything but letters; lists go through
            // FromCanonicalList.
            Assert.Multiple(() =>
            {
                Assert.That(BeatmapLanguageExtensions.FromCanonicalName("japanese,english"), Is.EqualTo(BeatmapLanguage.Unspecified));
                Assert.That(BeatmapLanguageExtensions.FromCanonicalList("japanese,english"), Is.EqualTo(japanese_english));
                Assert.That(BeatmapLanguageExtensions.FromCanonicalList(" japanese , klingon,english "), Is.EqualTo(japanese_english),
                    "unknown entries are dropped and whitespace around names is forgiven");
            });
        }

        // ---- the .osu lines ----

        [Test]
        public void AMultiLanguageMapWritesThePrimaryAndTheList()
        {
            string encoded = encode(map(japanese_english));

            Assert.Multiple(() =>
            {
                Assert.That(encoded, Does.Contain("\nLanguage:japanese\n").Or.Contain("\nLanguage:japanese\r\n"));
                Assert.That(encoded, Does.Contain("\nLanguages:japanese,english"));
                Assert.That(decode(encoded).Metadata.Languages, Is.EqualTo(japanese_english));
            });
        }

        [Test]
        public void TheListKeepsTheMappersOrderAndTheLegacyLineCarriesThePrimary()
        {
            // English picked first: the list keeps that order, the old-reader line still names Japanese.
            string encoded = encode(map(new[] { BeatmapLanguage.English, BeatmapLanguage.Japanese }));

            Assert.Multiple(() =>
            {
                Assert.That(encoded, Does.Contain("Language:japanese"));
                Assert.That(encoded, Does.Contain("Languages:english,japanese"));
                Assert.That(decode(encoded).Metadata.Languages, Is.EqualTo(new[] { BeatmapLanguage.English, BeatmapLanguage.Japanese }));
            });
        }

        [Test]
        public void ASingleLanguageMapEncodesExactlyAsBefore()
        {
            // No Languages line at all for one language, so no installed map re-hashes on its next save.
            string encoded = encode(map(new[] { BeatmapLanguage.Japanese }));

            Assert.Multiple(() =>
            {
                Assert.That(encoded, Does.Contain("Language:japanese"));
                Assert.That(encoded, Does.Not.Contain("Languages:"));
            });
        }

        [Test]
        public void ALegacySingleLanguageFileStillDecodes()
        {
            // A file written before backlog 472: one Language line, no list.
            string legacy = encode(map(Array.Empty<BeatmapLanguage>())).Replace("Tags:", "Language:russian\nTags:");

            Assert.That(decode(legacy).Metadata.Languages, Is.EqualTo(new[] { BeatmapLanguage.Russian }));
        }

        [Test]
        public void TheListWinsOverTheSingleLineWhicheverComesFirst()
        {
            string bare = encode(map(Array.Empty<BeatmapLanguage>()));

            string listFirst = bare.Replace("Tags:", "Languages:korean,english\nLanguage:french\nTags:");
            string listLast = bare.Replace("Tags:", "Language:french\nLanguages:korean,english\nTags:");

            Assert.Multiple(() =>
            {
                Assert.That(decode(listFirst).Metadata.Languages, Is.EqualTo(new[] { BeatmapLanguage.Korean, BeatmapLanguage.English }));
                Assert.That(decode(listLast).Metadata.Languages, Is.EqualTo(new[] { BeatmapLanguage.Korean, BeatmapLanguage.English }));
            });
        }

        [Test]
        public void AListOfOnlyUnknownLanguagesFallsBackToTheSingleLine()
        {
            // A newer client's languages this build has never heard of: the primary it also wrote still lands.
            string encoded = encode(map(Array.Empty<BeatmapLanguage>())).Replace("Tags:", "Language:german\nLanguages:klingon,elvish\nTags:");

            Assert.That(decode(encoded).Metadata.Languages, Is.EqualTo(new[] { BeatmapLanguage.German }));
        }

        [Test]
        public void TheLegacyEncoderWritesTheSameLines()
        {
            // The .osz export path. No hit objects: the legacy encoder cannot serialise TypeBeatHitObjects, and this is a
            // [Metadata] assertion.
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Languages = japanese_english;

            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                new typebeat.Game.Beatmaps.Formats.LegacyBeatmapEncoder(beatmap, null, null).Encode(sw);

            Assert.Multiple(() =>
            {
                Assert.That(sb.ToString(), Does.Contain("Language:japanese"));
                Assert.That(sb.ToString(), Does.Contain("Languages:japanese,english"));
                Assert.That(decode(sb.ToString()).Metadata.Languages, Is.EqualTo(japanese_english));
            });
        }

        // ---- realm ----

        [Test]
        public void Schema68CarriesEveryOldSingleLanguageOver()
        {
            using var context = new SynchronousRealmTestContext();
            var storage = new NativeStorage(Path.Combine(directory, "migration"));
            string path = storage.GetFullPath(realm_filename, true);

            var ids = new Guid[3];

            // Rows as an install at schema 67 holds them. The model no longer has the int column, so the rows are
            // written at 66 and the column is added back at 67 through the dynamic API.
            using (var realm = Realm.GetInstance(new RealmConfiguration(path) { SchemaVersion = 66 }))
            {
                realm.Write(() =>
                {
                    var ruleset = realm.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0));
                    var set = new BeatmapSetInfo();

                    for (int i = 0; i < ids.Length; i++)
                    {
                        var beatmap = new BeatmapInfo(ruleset, new BeatmapDifficulty(), new BeatmapMetadata { Title = $"row {i}" }) { BeatmapSet = set };
                        set.Beatmaps.Add(beatmap);
                        ids[i] = beatmap.ID;
                    }

                    realm.Add(set);
                });
            }

            var oldValues = new[] { BeatmapLanguage.Japanese, BeatmapLanguage.Unspecified, BeatmapLanguage.Swedish };

            LegacyRealmColumn.Add(path, 67, "BeatmapMetadata", Property.Primitive("Language", RealmValueType.Int), realm =>
            {
                foreach (var metadata in realm.DynamicApi.All("BeatmapMetadata"))
                {
                    int row = int.Parse(metadata.DynamicApi.Get<string>("Title")["row ".Length..]);
                    metadata.DynamicApi.Set("Language", (int)oldValues[row]);
                }
            });

            using (var access = new RealmAccess(storage, realm_filename))
            {
                BeatmapLanguage[] languagesOf(int row) => access.Run(r => r.Find<BeatmapInfo>(ids[row])!.Metadata.Languages.ToArray());

                Assert.Multiple(() =>
                {
                    Assert.That(languagesOf(0), Is.EqualTo(new[] { BeatmapLanguage.Japanese }));
                    Assert.That(languagesOf(1), Is.Empty, "an unspecified row stays unspecified");
                    Assert.That(languagesOf(2), Is.EqualTo(new[] { BeatmapLanguage.Swedish }));

                    Assert.That(access.Run(r => r.Schema.TryFindObjectSchema("BeatmapMetadata", out var metadata) && metadata.TryFindProperty("Language", out _)),
                        Is.False, "the old int column is gone");
                });
            }
        }

        [Test]
        public async Task ImportCopiesTheWholeListOntoTheRealmRow()
        {
            var stored = await importAndRead(encode(map(new[] { BeatmapLanguage.Korean, BeatmapLanguage.English }))).ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(stored.Languages, Is.EqualTo(new[] { BeatmapLanguage.Korean, BeatmapLanguage.English }));
                Assert.That(stored.Language, Is.EqualTo(BeatmapLanguage.Korean));
            });
        }

        [Test]
        public void TheStampTakesAListThatAddsALanguageUnderTheSamePrimary()
        {
            // The case a primary-only comparison would miss: the file adds English to a Japanese map.
            var stored = new BeatmapMetadata { Language = BeatmapLanguage.Japanese };
            var file = decode(encode(map(japanese_english))).Metadata;

            Assert.Multiple(() =>
            {
                Assert.That(StoredBeatmapFacts.StampFileMetadata(stored, file), Is.True);
                Assert.That(stored.Languages, Is.EqualTo(japanese_english));
                Assert.That(StoredBeatmapFacts.StampFileMetadata(stored, file), Is.False, "a second pass has nothing left to change");
            });
        }

        [Test]
        public void TheStampLeavesAStoredListAloneWhenTheFileStatesNone()
        {
            var stored = new BeatmapMetadata { Languages = japanese_english };
            var file = decode(encode(map(Array.Empty<BeatmapLanguage>()))).Metadata;

            Assert.Multiple(() =>
            {
                Assert.That(StoredBeatmapFacts.StampFileMetadata(stored, file), Is.False);
                Assert.That(stored.Languages, Is.EqualTo(japanese_english));
            });
        }

        // ---- song select ----

        [Test]
        public void ALanguageFilterMatchesAMapThatDeclaresItAmongOthers()
        {
            var bilingual = new BeatmapInfo { Metadata = { Languages = japanese_english } };
            var japanese = new BeatmapInfo { Metadata = { Language = BeatmapLanguage.Japanese } };
            var korean = new BeatmapInfo { Metadata = { Language = BeatmapLanguage.Korean } };

            Assert.Multiple(() =>
            {
                Assert.That(matches(bilingual, "lang:en"), Is.True, "English is one of its languages, though not the primary");
                Assert.That(matches(bilingual, "lang:jp"), Is.True);
                Assert.That(matches(bilingual, "lang:ko"), Is.False);

                // Two terms find the maps that declare both.
                Assert.That(matches(bilingual, "lang:en lang:jp"), Is.True);
                Assert.That(matches(japanese, "lang:en lang:jp"), Is.False);
                Assert.That(matches(korean, "lang:en lang:jp"), Is.False);
            });
        }

        [Test]
        public void GroupingListsAMapUnderEachOfItsLanguages()
        {
            var groups = BeatmapCarouselFilterGrouping.DefineGroupsByLanguages(japanese_english).ToArray();
            var none = BeatmapCarouselFilterGrouping.DefineGroupsByLanguages(Array.Empty<BeatmapLanguage>()).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(groups.Select(g => g.Order), Is.EqualTo(new[] { (int)BeatmapLanguage.Japanese, (int)BeatmapLanguage.English }));
                Assert.That(none.Single().Order, Is.EqualTo(int.MaxValue), "a map with no language goes under its own last heading");
            });
        }

        // ---- helpers ----

        private static bool matches(BeatmapInfo beatmap, string query)
        {
            var criteria = new FilterCriteria();
            FilterQueryParser.ApplyQueries(criteria, query);
            return BeatmapCarouselFilterMatching.CheckCriteriaMatch(beatmap, criteria);
        }

        private async Task<BeatmapMetadata> importAndRead(string osu)
        {
            using var context = new SynchronousRealmTestContext();
            var storage = new NativeStorage(Path.Combine(directory, "data"));

            string oszPath = Path.Combine(directory, "set.osz");

            using (var zip = ZipFile.Open(oszPath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("Synth Rider - Neon Nights (mapper) [Normal].osu").Open(), new UTF8Encoding(false)))
                    await writer.WriteAsync(osu).ConfigureAwait(false);

                using (var writer = new StreamWriter(zip.CreateEntry("audio.mp3").Open()))
                    await writer.WriteAsync("fake audio").ConfigureAwait(false);
            }

            using var realm = new RealmAccess(storage, realm_filename);

            realm.Write(r => r.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0)
            {
                Available = true,
            }));

            var importer = new BeatmapImporter(storage, realm);
            var imported = await importer.Import(new ImportTask(oszPath)).ConfigureAwait(false);

            Assert.That(imported, Is.Not.Null, "the import itself failed");

            return realm.Run(r => r.All<BeatmapInfo>().Single().Metadata.DeepClone());
        }

        private static Beatmap map(BeatmapLanguage[] languages)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.BeatmapInfo.DifficultyName = "Normal";
            beatmap.Metadata.Artist = "Synth Rider";
            beatmap.Metadata.Title = "Neon Nights";
            beatmap.Metadata.AudioFile = "audio.mp3";
            beatmap.Metadata.Languages = languages;

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

            return beatmap;
        }

        private static string encode(Beatmap source)
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(source, null, sw);

            return sb.ToString();
        }

        private static Beatmap decode(string text)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var reader = new typebeat.Game.IO.LineBufferedReader(stream);
            return (Beatmap)typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }
    }
}
