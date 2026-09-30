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
    /// <summary>
    /// Board-only re-arm delay for transient light overlays. It is currently
    /// consumed by Whisper New and Whisper Repeat; durable scenes keep zero.
    /// </summary>
    public int LightCooldownMs { get; set; }
    /// <summary>One of the eight built-in 3–5 note calibration motifs.</summary>
    public int CalibrationCue { get; set; }

    [JsonIgnore] public double LuxMin => Math.Max(0, LuxCenter - LuxTolerance);
    [JsonIgnore] public double LuxMax => LuxCenter + LuxTolerance;
    [JsonIgnore] public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrWhiteSpace(Name)
        && double.IsFinite(LuxCenter) && LuxCenter >= 0
        && double.IsFinite(LuxTolerance) && LuxTolerance >= 0
        && StableDurationMs >= 0
        && double.IsFinite(HysteresisLux) && HysteresisLux >= 0
        && LightCooldownMs is >= 0 and <= 600000
        && CalibrationCue is >= 0 and <= 8;
}

public static class LightStateDefaults
{
    /// <summary>Phase-four emergency fallback values; the packaged JSON is the hardware source.</summary>
    public static List<LightStateProfile> CreateInitialProfiles() => new()
    {
        Profile("desktop", "دسکتاپ", 0, 1),
        Profile("login-or-dc", "صفحه لاگین یا DC", 25, 2),
        Profile("character-dashboard", "داشبورد انتخاب کرکترها", 31, 3),
        Profile("entering-game-loading", "صفحه لود ورود به بازی", 5, 4),
        Profile("game", "محیط بازی", 26, 5),
        Profile("targeted", "تارگت شدن توسط افراد", 20, 6),
        Profile("whisper", "ویسپر افراد جدید", 55, 7, 5000),
        Profile("whisper-repeat", "ویسپر افراد تکراری", 60, 8, 10000),
    };

    private static LightStateProfile Profile(string id, string name, double center, int cue,
        int lightCooldownMs = 0) => new()
    {
        Id = id,
        Name = name,
        LuxCenter = center,
        LuxTolerance = 2,
        StableDurationMs = 750,
        HysteresisLux = 1,
        LightCooldownMs = lightCooldownMs,
        CalibrationCue = cue,
    };
}
