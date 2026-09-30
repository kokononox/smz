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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var valid = profiles.Where(p => p is not null && p.IsValid && seen.Add(p.Id)).ToList();
        if (valid.Count == 0) return LightStateDefaults.CreateInitialProfiles();
        // Schema migration: preserve every calibrated value and append only
        // newly introduced required profiles (for example light Whisper).
        foreach (var fallback in LightStateDefaults.CreateInitialProfiles())
            if (seen.Add(fallback.Id)) valid.Add(fallback);
        return valid;
    }
}
