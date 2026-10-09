using System.IO;
using System.Text.Json;
using Ams.UI.Models;

namespace Ams.UI.Services;

public static class BuzzerSystemCueStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "buzzer-system-cues.json");

    public static List<BuzzerSystemCueProfile> Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return BuzzerSystemCueDefaults.Create();
            var loaded = JsonSerializer.Deserialize<List<BuzzerSystemCueProfile>>(File.ReadAllText(path), Options);
            if (loaded is null) return BuzzerSystemCueDefaults.Create();
            foreach (var cue in loaded) LightStateProfileStore.MigrateCueStyle(cue);
            var defaults = BuzzerSystemCueDefaults.Create();
            var byId = loaded.Where(x => x.NumericId is >= 1 and <= 23 && x.IsValid)
                .GroupBy(x => x.NumericId).ToDictionary(x => x.Key, x => x.First());
            return defaults.Select(fallback => byId.TryGetValue(fallback.NumericId, out var value)
                ? value : fallback).ToList();
        }
        catch { return BuzzerSystemCueDefaults.Create(); }
    }

    public static void Save(IEnumerable<BuzzerSystemCueProfile> profiles, string? path = null)
    {
        path ??= DefaultPath;
        var normalized = profiles.OrderBy(x => x.NumericId).ToList();
        if (normalized.Count != 23 || normalized.Select(x => x.NumericId).Distinct().Count() != 23
            || normalized.Any(x => !x.IsValid))
            throw new InvalidDataException("تنظیم صداهای سیستمی ناقص یا نامعتبر است.");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, Options));
        File.Move(temporary, path, true);
    }
}