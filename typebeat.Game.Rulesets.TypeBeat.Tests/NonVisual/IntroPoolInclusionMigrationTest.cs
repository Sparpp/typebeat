// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using NUnit.Framework;
using osu.Framework.Platform;
using Realms;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Rulesets;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers realm schema 58, which moved "Use on game intro" from each difficulty
    /// (<see cref="BeatmapUserSettings.IntroPoolInclusion"/>) to the set (<see cref="BeatmapSetInfo.IntroPoolInclusion"/>).
    /// A realm file is written at schema 57 with per-difficulty overrides only, then opened through
    /// <see cref="RealmAccess"/>, which runs the real migration.
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

            Guid trueWins = Guid.Empty, allFalse = Guid.Empty, mixed = Guid.Empty, untouched = Guid.Empty;

            using (var realm = Realm.GetInstance(new RealmConfiguration(storage.GetFullPath(filename, true)) { SchemaVersion = 57 }))
            {
                realm.Write(() =>
                {
                    var ruleset = realm.Add(new RulesetInfo("typebeat", "type!beat", "typebeat.Game.Rulesets.TypeBeat.TypeBeatRuleset, typebeat.Game.Rulesets.TypeBeat", 0));

                    trueWins = add(realm, ruleset, false, true, null);
                    allFalse = add(realm, ruleset, false, false);
                    mixed = add(realm, ruleset, false, null);
                    untouched = add(realm, ruleset, null, null);
                });
            }

            using (var access = new RealmAccess(storage, filename))
            {
                bool? inclusionOf(Guid id) => access.Run(r => r.Find<BeatmapSetInfo>(id)!.IntroPoolInclusion);

                Assert.Multiple(() =>
                {
                    Assert.That(inclusionOf(trueWins), Is.True, "any difficulty opted in opts the set in");
                    Assert.That(inclusionOf(allFalse), Is.False, "every difficulty kept out keeps the set out");
                    Assert.That(inclusionOf(mixed), Is.Null, "a mix of kept-out and no override follows the beatdrops");
                    Assert.That(inclusionOf(untouched), Is.Null);

                    // the old per-difficulty field is left in place, unread.
                    Assert.That(access.Run(r => r.Find<BeatmapSetInfo>(allFalse)!.Beatmaps[0].UserSettings.IntroPoolInclusion), Is.False);
                });
            }
        }

        private static Guid add(Realm realm, RulesetInfo ruleset, params bool?[] difficultyInclusions)
        {
            var set = new BeatmapSetInfo();

            foreach (bool? inclusion in difficultyInclusions)
            {
                var beatmap = new BeatmapInfo(ruleset, new BeatmapDifficulty(), new BeatmapMetadata()) { BeatmapSet = set };
                beatmap.UserSettings.IntroPoolInclusion = inclusion;
                set.Beatmaps.Add(beatmap);
            }

            realm.Add(set);
            return set.ID;
        }
    }
}
