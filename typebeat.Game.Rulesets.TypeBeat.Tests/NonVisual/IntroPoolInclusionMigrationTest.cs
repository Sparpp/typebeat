// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using osu.Framework.Platform;
using Realms;
using Realms.Schema;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Rulesets;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers realm schema 58, which moved "Use on game intro" from each difficulty
    /// (<c>BeatmapUserSettings.IntroPoolInclusion</c>) to the set (<see cref="BeatmapSetInfo.IntroPoolInclusion"/>),
    /// and schema 65, which dropped the per-difficulty column. A realm file is written at schema 57 with
    /// per-difficulty overrides only (the column added back through <see cref="LegacyRealmColumn"/>, since the
    /// model no longer has it), then opened through <see cref="RealmAccess"/>, which runs the real migration.
    /// </summary>
    [TestFixture]
    public class IntroPoolInclusionMigrationTest
    {
        private const string filename = "client.realm";

        private string directory = null!;

        [SetUp]
        public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "typebeat-intro-migration-" + Guid.NewGuid().ToString("N"));

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
        public void SetInclusionIsDerivedFromDifficultyOverrides()
        {
            using var context = new SynchronousRealmTestContext();
            var storage = new NativeStorage(directory);

            string path = storage.GetFullPath(filename, true);
            var inclusions = new Dictionary<Guid, bool?>();

            Guid trueWins = Guid.Empty, allFalse = Guid.Empty, mixed = Guid.Empty, untouched = Guid.Empty;

            using (var realm = Realm.GetInstance(new RealmConfiguration(path) { SchemaVersion = 56 }))
            {
                realm.Write(() =>
                {
                    var ruleset = realm.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0));

                    trueWins = add(realm, ruleset, inclusions, false, true, null);
                    allFalse = add(realm, ruleset, inclusions, false, false);
                    mixed = add(realm, ruleset, inclusions, false, null);
                    untouched = add(realm, ruleset, inclusions, null, null);
                });
            }

            LegacyRealmColumn.Add(path, 57, nameof(BeatmapUserSettings), Property.Primitive("IntroPoolInclusion", RealmValueType.Bool, isNullable: true), realm =>
            {
                foreach (var (beatmapId, inclusion) in inclusions)
                {
                    var settings = realm.DynamicApi.Find("Beatmap", beatmapId)!.DynamicApi.Get<IEmbeddedObject>(nameof(BeatmapInfo.UserSettings));
                    settings.DynamicApi.Set("IntroPoolInclusion", inclusion);
                }
            });

            using (var access = new RealmAccess(storage, filename))
            {
                bool? inclusionOf(Guid id) => access.Run(r => r.Find<BeatmapSetInfo>(id)!.IntroPoolInclusion);

                Assert.Multiple(() =>
                {
                    Assert.That(inclusionOf(trueWins), Is.True, "any difficulty opted in opts the set in");
                    Assert.That(inclusionOf(allFalse), Is.False, "every difficulty kept out keeps the set out");
                    Assert.That(inclusionOf(mixed), Is.Null, "a mix of kept-out and no override follows the beatdrops");
                    Assert.That(inclusionOf(untouched), Is.Null);

                    // schema 65 dropped the old per-difficulty field.
                    Assert.That(access.Run(r => r.Schema.TryFindObjectSchema(nameof(BeatmapUserSettings), out var settings) && settings.TryFindProperty("IntroPoolInclusion", out _)),
                        Is.False);
                });
            }
        }

        private static Guid add(Realm realm, RulesetInfo ruleset, Dictionary<Guid, bool?> inclusions, params bool?[] difficultyInclusions)
        {
            var set = new BeatmapSetInfo();

            foreach (bool? inclusion in difficultyInclusions)
            {
                var beatmap = new BeatmapInfo(ruleset, new BeatmapDifficulty(), new BeatmapMetadata()) { BeatmapSet = set };
                inclusions[beatmap.ID] = inclusion;
                set.Beatmaps.Add(beatmap);
            }

            realm.Add(set);
            return set.ID;
        }
    }
}
