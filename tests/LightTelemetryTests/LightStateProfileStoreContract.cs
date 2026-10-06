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
            edited.Single(x => x.Id == "game").CalibrationCue = 2;
            edited.Single(x => x.Id == "targeted").CalibrationCue = 0;
            edited.Single(x => x.Id == "targeted").CalibrationCuePattern = "440:100,30;880:180";
            edited.Single(x => x.Id == "targeted").CalibrationCueVolume = 72;
            edited.Single(x => x.Id == "targeted").CalibrationCueEnvelope = "smooth";
            edited.Single(x => x.Id == "whisper").LightCooldownMs = 12500;
            edited.Single(x => x.Id == "targeted-repeat").LightCooldownMs = 42000;
            edited.Single(x => x.Id == "whisper-repeat").LightCooldownMs = 3600000;
            LightStateProfileStore.Save(edited, path);
            var loaded = LightStateProfileStore.Load(path);
            Check(loaded.Single(x => x.Id == "targeted").LuxTolerance == 3.5
                  && loaded.Single(x => x.Id == "game").LuxCenter == 27.25
                  && loaded.Single(x => x.Id == "game").CalibrationCue == 2
                  && loaded.Single(x => x.Id == "targeted").CalibrationCue == 0
                  && loaded.Single(x => x.Id == "targeted").CalibrationCuePattern == "440:100,30;880:180"
                  && loaded.Single(x => x.Id == "targeted").CalibrationCueVolume == 72
                  && loaded.Single(x => x.Id == "targeted").CalibrationCueEnvelope == "smooth"
                  && loaded.Single(x => x.Id == "whisper").LightCooldownMs == 0
                  && loaded.Single(x => x.Id == "targeted-repeat").LightCooldownMs == 42000
                  && loaded.Single(x => x.Id == "whisper-repeat").LightCooldownMs == 3600000,
                "profile store round-trips cues and repeat-only optical cooldowns");

            File.WriteAllText(path, "{not-json");
            var recovered = LightStateProfileStore.Load(path);
            Check(recovered.Count == 9
                  && recovered.Single(x => x.Id == "desktop").LuxCenter == 59
                  && recovered.Single(x => x.Id == "targeted-repeat").LuxCenter == 39,
                "corrupt profile storage falls back to the nine safe defaults");

            var duplicate = LightStateDefaults.CreateInitialProfiles();
            duplicate.Add(new LightStateProfile { Id = "game", Name = "duplicate", LuxCenter = 999 });
            duplicate.Add(new LightStateProfile { Id = "bad", Name = "bad", LuxCenter = -1 });
            var normalized = LightStateProfileStore.Normalize(duplicate);
            Check(normalized.Count == 9 && normalized.Count(x => x.Id == "game") == 1,
                "normalization removes invalid and duplicate profile IDs");

            var legacySix = LightStateDefaults.CreateInitialProfiles()
                .Where(x => x.Id != "targeted-repeat"
                            && x.Id != "whisper"
                            && x.Id != "whisper-repeat").ToList();
            legacySix.Single(x => x.Id == "game").LuxCenter = 88.5;
            var migrated = LightStateProfileStore.Normalize(legacySix);
            Check(migrated.Count == 9
                  && migrated.Single(x => x.Id == "game").LuxCenter == 88.5
                  && migrated.Single(x => x.Id == "targeted-repeat").LuxCenter == 39
                  && migrated.Single(x => x.Id == "whisper").LuxCenter == 100
                  && migrated.Single(x => x.Id == "whisper-repeat").LuxCenter == 23
                  && migrated.Single(x => x.Id == "whisper").LightCooldownMs == 0
                  && migrated.Single(x => x.Id == "targeted-repeat").LightCooldownMs == 60000
                  && migrated.Single(x => x.Id == "whisper-repeat").LightCooldownMs == 1000000
                  && migrated.All(x => x.CalibrationCue is >= 1 and <= 9),
                "legacy storage gains split repeat profiles and distinct calibration motifs");

            var build517Profile = new LightStateProfile
            {
                Id = "game", Name = "محیط بازی", LuxCenter = 26,
                CalibrationCue = 5,
                CalibrationCuePattern = "900:120,45;1200:200",
                CalibrationCueVolume = 100,
                CalibrationCueEnvelope = "sharp",
                CalibrationCueTempo = 100,
                CalibrationCueStyleVersion = 0,
            };
            var migratedStyle = LightStateProfileStore.Normalize([build517Profile])
                .Single(x => x.Id == "game");
            var oldDefault = CalibrationCueCatalog.Get(5);
            Check(migratedStyle.CalibrationCuePattern == oldDefault.Pattern
                  && migratedStyle.CalibrationCueVolume == oldDefault.Volume
                  && migratedStyle.CalibrationCueEnvelope == oldDefault.Envelope
                  && migratedStyle.CalibrationCueTempo == oldDefault.Tempo
                  && migratedStyle.CalibrationCueStyleVersion == 1,
                "Build 517 profiles materialize the selected legacy preset as their editable defaults");
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }

        Console.WriteLine($"=== Light profile store results: {passed} passed, {failed} failed ===");
        if (failed > 0) throw new InvalidOperationException($"Light profile store contract failed: {failed}");
    }
}
