using System.Text.Json.Serialization;

namespace Ams.UI.Models;

/// <summary>Editable app-level light signature. Bounds are derived and never allow physical negative Lux.</summary>
public sealed class LightStateProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double LuxCenter { get; set; }
    public double LuxTolerance { get; set; } = 2;
    public int StableDurationMs { get; set; } = 750;
    public double HysteresisLux { get; set; } = 1;

    [JsonIgnore] public double LuxMin => Math.Max(0, LuxCenter - LuxTolerance);
    [JsonIgnore] public double LuxMax => LuxCenter + LuxTolerance;
    [JsonIgnore] public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrWhiteSpace(Name)
        && double.IsFinite(LuxCenter) && LuxCenter >= 0
        && double.IsFinite(LuxTolerance) && LuxTolerance >= 0
        && StableDurationMs >= 0
        && double.IsFinite(HysteresisLux) && HysteresisLux >= 0;
}

public static class LightStateDefaults
{
    /// <summary>Phase-four emergency fallback values; the packaged JSON is the hardware source.</summary>
    public static List<LightStateProfile> CreateInitialProfiles() => new()
    {
        Profile("desktop", "دسکتاپ", 0),
        Profile("login-or-dc", "صفحه لاگین یا DC", 25),
        Profile("character-dashboard", "داشبورد انتخاب کرکترها", 31),
        Profile("entering-game-loading", "صفحه لود ورود به بازی", 5),
        Profile("game", "محیط بازی", 26),
        Profile("targeted", "تارگت شدن توسط افراد", 20),
        Profile("whisper", "ویسپر", 55),
    };

    private static LightStateProfile Profile(string id, string name, double center) => new()
    {
        Id = id,
        Name = name,
        LuxCenter = center,
        LuxTolerance = 2,
        StableDurationMs = 750,
        HysteresisLux = 1,
    };
}
