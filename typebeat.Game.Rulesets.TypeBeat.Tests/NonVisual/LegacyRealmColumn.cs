// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Realms;
using Realms.Schema;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Builds a realm file at an OLD schema version that still carries a column the current model has dropped,
    /// so a migration test can hand <see cref="typebeat.Game.Database.RealmAccess"/> exactly what an old install
    /// holds. The caller writes the file with the current model one version below the target; this reopens it
    /// dynamically at the target version with the dropped column added back, where <c>write</c>
    /// fills the column in through the dynamic API.
    /// </summary>
    public static class LegacyRealmColumn
    {
        public static void Add(string path, ulong schemaVersion, string className, Property column, Action<Realm> write)
        {
            RealmSchema.Builder schema;

            using (var typed = Realm.GetInstance(new RealmConfiguration(path) { SchemaVersion = schemaVersion - 1 }))
                schema = typed.Schema.GetBuilder();

            var objectSchema = schema[className].GetBuilder();
            objectSchema.Add(column);

            schema.Remove(className);
            schema.Add(objectSchema);

            using var realm = Realm.GetInstance(new RealmConfiguration(path)
            {
                SchemaVersion = schemaVersion,
                IsDynamic = true,
                Schema = schema,
            });

            realm.Write(() => write(realm));
        }
    }
}
