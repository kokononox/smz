using System.IO;
using System.Text.Json;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>Persists editable light profiles outside .amsj plans. Missing or corrupt data falls back safely.</summary>
public static class LightStateProfileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "light-state-profiles.json");

    public static List<LightStateProfile> Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return LightStateDefaults.CreateInitialProfiles();
            var profiles = JsonSerializer.Deserialize<List<LightStateProfile>>(File.ReadAllText(path), Options);
            return Normalize(profiles);
        }
        catch
        {
            return LightStateDefaults.CreateInitialProfiles();
        }
    }

    public static void Save(IEnumerable<LightStateProfile> profiles, string? path = null)
    {
        path ??= DefaultPath;
        var normalized = Normalize(profiles);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, Options));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Rejects malformed and duplicate IDs without rewriting valid user calibration values.</summary>
    public static List<LightStateProfile> Normalize(IEnumerable<LightStateProfile>? profiles)
    {
        if (profiles is null) return LightStateDefaults.CreateInitialProfiles();
        foreach (var profile in profiles.Where(x => x is not null))
            MigrateCueStyle(profile);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var valid = profiles.Where(p => p is not null && p.IsValid && seen.Add(p.Id)).ToList();
        if (valid.Count == 0) return LightStateDefaults.CreateInitialProfiles();
        // Schema migration: preserve every calibrated value and append only
        // newly introduced required profiles (for example light Whisper).
        foreach (var fallback in LightStateDefaults.CreateInitialProfiles())
            if (seen.Add(fallback.Id)) valid.Add(fallback);
        var defaults = LightStateDefaults.CreateInitialProfiles()
            .ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var profile in valid)
        {
            if (!defaults.TryGetValue(profile.Id, out var fallback)) continue;
            if (profile.CalibrationCue is < 0 or > 100)
                profile.CalibrationCue = fallback.CalibrationCue;
            MigrateCueStyle(profile);
            if (string.IsNullOrWhiteSpace(profile.CalibrationCuePattern))
                profile.CalibrationCuePattern = fallback.CalibrationCuePattern;
            if (profile.CalibrationCueVolume is < 1 or > 100)
                profile.CalibrationCueVolume = 100;
            if (profile.CalibrationCueTempo is < 25 or > 400)
                profile.CalibrationCueTempo = 100;
            if (profile.CalibrationCueEnvelope is not ("sharp" or "smooth" or "fade-in" or "fade-out"))
                profile.CalibrationCueEnvelope = "sharp";
            // Existing profile files predate optical cooldown. A zero value on
            // either transient Whisper profile is migrated to the safe default.
            if (profile.LightCooldownMs == 0 && fallback.LightCooldownMs > 0)
                profile.LightCooldownMs = fallback.LightCooldownMs;
        }
        return valid;
    }

    internal static void MigrateCueStyle(LightStateProfile profile)
    {
        if (profile.CalibrationCueStyleVersion > 0) return;
        if (profile.CalibrationCue < 0) return;
        if (profile.CalibrationCue > 0)
        {
            var preset = CalibrationCueCatalog.Get(profile.CalibrationCue);
            profile.CalibrationCuePattern = preset.Pattern;
            profile.CalibrationCueVolume = preset.Volume;
            profile.CalibrationCueEnvelope = preset.Envelope;
            profile.CalibrationCueTempo = preset.Tempo;
        }
        profile.CalibrationCueStyleVersion = 1;
    }
}
