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
    /// <summary>Hardware-derived seed values shipped with every Classroom package.</summary>
    public static List<LightStateProfile> CreateInitialProfiles() => new()
    {
        Profile("desktop", "دسکتاپ", 45, 5),
        Profile("login-or-dc", "صفحه لاگین یا DC", 2.5, 1),
        Profile("character-dashboard", "داشبورد انتخاب کرکترها", 13.3, 3),
        Profile("entering-game-loading", "صفحه لود ورود به بازی", 38.3, 1),
        Profile("game", "محیط بازی", 25, 2),
        Profile("targeted", "تارگت شدن توسط افراد", 20, 1),
    };

    private static LightStateProfile Profile(
        string id, string name, double center, double tolerance) => new()
    {
        Id = id,
        Name = name,
        LuxCenter = center,
        LuxTolerance = tolerance,
        StableDurationMs = 750,
        HysteresisLux = 1,
    };
}
