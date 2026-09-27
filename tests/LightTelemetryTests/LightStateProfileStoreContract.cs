using System.Runtime.CompilerServices;
using Ams.UI.Models;
using Ams.UI.Services;

internal static class LightStateProfileStoreContract
{
    [ModuleInitializer]
    internal static void Execute()
    {
        var passed = 0;
        var failed = 0;
        void Check(bool condition, string name)
        {
            if (condition) { passed++; Console.WriteLine("PASS: " + name); }
            else { failed++; Console.WriteLine("FAIL: " + name); }
        }

        var directory = Path.Combine(Path.GetTempPath(), "ams-light-profile-contract-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "profiles.json");
        try
        {
            var edited = LightStateDefaults.CreateInitialProfiles();
            edited.Single(x => x.Id == "targeted").LuxTolerance = 3.5;
            edited.Single(x => x.Id == "game").LuxCenter = 27.25;
            LightStateProfileStore.Save(edited, path);
            var loaded = LightStateProfileStore.Load(path);
            Check(loaded.Single(x => x.Id == "targeted").LuxTolerance == 3.5
                  && loaded.Single(x => x.Id == "game").LuxCenter == 27.25,
                "profile store round-trips user-edited center and tolerance");

            File.WriteAllText(path, "{not-json");
            var recovered = LightStateProfileStore.Load(path);
            Check(recovered.Count == 6 && recovered.Single(x => x.Id == "desktop").LuxCenter == 0,
                "corrupt profile storage falls back to the six safe defaults");

            var duplicate = LightStateDefaults.CreateInitialProfiles();
            duplicate.Add(new LightStateProfile { Id = "game", Name = "duplicate", LuxCenter = 999 });
            duplicate.Add(new LightStateProfile { Id = "bad", Name = "bad", LuxCenter = -1 });
            var normalized = LightStateProfileStore.Normalize(duplicate);
            Check(normalized.Count == 6 && normalized.Count(x => x.Id == "game") == 1,
                "normalization removes invalid and duplicate profile IDs");
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }

        Console.WriteLine($"=== Light profile store results: {passed} passed, {failed} failed ===");
        if (failed > 0) throw new InvalidOperationException($"Light profile store contract failed: {failed}");
    }
}
