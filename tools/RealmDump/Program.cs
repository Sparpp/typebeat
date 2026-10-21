// Throwaway diagnostic: dump beatmap/ruleset rows from a copy of the osu dev realm.
using System;
using System.IO;
using Realms;

string src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "typebeat-development", "client_51.realm");
string tmp = Path.Combine(Path.GetTempPath(), $"realmdump_{Guid.NewGuid():N}.realm");
File.Copy(src, tmp, true);

var config = new RealmConfiguration(tmp)
{
    IsDynamic = true,
    IsReadOnly = false,
    SchemaVersion = 51,
};

using var realm = Realm.GetInstance(config);

Console.WriteLine("=== schema classes ===");
foreach (var objSchema in realm.Schema)
    Console.WriteLine($"  {objSchema.Name}");

Console.WriteLine("=== Ruleset rows ===");
foreach (dynamic r in realm.DynamicApi.All("Ruleset"))
    Console.WriteLine($"  ShortName='{r.ShortName}' Name='{r.Name}' OnlineID={r.OnlineID} Available={r.Available}");

Console.WriteLine("=== BeatmapSet rows ===");
foreach (dynamic s in realm.DynamicApi.All("BeatmapSet"))
{
    Console.WriteLine($"  Set OnlineID={s.OnlineID} DeletePending={s.DeletePending} Protected={s.Protected} Beatmaps={s.Beatmaps.Count}");
    // handled below via the Beatmap table directly
}

Console.WriteLine("=== Beatmap (difficulty) rows ===");
foreach (object bObj in realm.DynamicApi.All("Beatmap"))
{
    var b = (Realms.IRealmObjectBase)bObj;

    foreach (var prop in b.ObjectSchema)
    {
        object val;
        try { val = b.DynamicApi.Get<object>(prop.Name); } catch { val = "<?>"; }

        if (val is Realms.IRealmObjectBase linked)
        {
            if (linked.ObjectSchema.TryFindProperty("ShortName", out _))
                val = "-> " + linked.DynamicApi.Get<string>("ShortName");
            else if (linked.ObjectSchema.TryFindProperty("Title", out _))
                val = $"-> {linked.DynamicApi.Get<string>("Artist")} - {linked.DynamicApi.Get<string>("Title")}";
            else
                val = "-> <" + linked.ObjectSchema.Name + ">";
        }

        Console.WriteLine($"    {prop.Name} = {val ?? "NULL"}");
    }
}

File.Delete(tmp);
