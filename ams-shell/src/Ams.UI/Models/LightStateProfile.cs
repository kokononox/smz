using System.Text.Json.Serialization;

namespace Ams.UI.Models;

/// <summary>Editable app-level light signature. Bounds are derived and never allow physical negative Lux.</summary>
public class LightStateProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double LuxCenter { get; set; }
    public double LuxTolerance { get; set; } = 2;
    public int StableDurationMs { get; set; } = 750;
    public double HysteresisLux { get; set; } = 1;
    /// <summary>
    /// Board-only re-arm delay for transient light overlays. Only repeat-person
    /// Targeted/Whisper profiles use it; new-person and durable scenes keep zero.
    /// </summary>
    public int LightCooldownMs { get; set; }
    /// <summary>
    /// One of the 100 built-in calibration motifs; zero selects Custom.
    /// Targeted New/Repeat also use their distinct motif as the runtime notice.
    /// </summary>
    // -1 is the deserialization sentinel for profile files created before this
    // property existed. Normalize migrates it to that environment's legacy cue.
    public int CalibrationCue { get; set; } = -1;
    /// <summary>Custom freq:duration,gap sequence. Used only when CalibrationCue is zero.</summary>
    public string CalibrationCuePattern { get; set; } = "900:120,45;1200:200";
    public int CalibrationCueVolume { get; set; } = 100;
    public string CalibrationCueEnvelope { get; set; } = "sharp";
    public int CalibrationCueTempo { get; set; } = 100;
    public int CalibrationCueStyleVersion { get; set; }

    [JsonIgnore] public double LuxMin => Math.Max(0, LuxCenter - LuxTolerance);
    [JsonIgnore] public double LuxMax => LuxCenter + LuxTolerance;
    [JsonIgnore] public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrWhiteSpace(Name)
        && double.IsFinite(LuxCenter) && LuxCenter >= 0
        && double.IsFinite(LuxTolerance) && LuxTolerance >= 0
        && StableDurationMs >= 0
        && double.IsFinite(HysteresisLux) && HysteresisLux >= 0
        && LightCooldownMs is >= 0 and <= 3600000
        && CalibrationCue is >= -1 and <= 100
        && CalibrationCueVolume is >= 1 and <= 100
        && CalibrationCueTempo is >= 25 and <= 400
        && CalibrationCueEnvelope is "sharp" or "smooth" or "fade-in" or "fade-out";
}

/// <summary>A formerly hard-coded firmware cue exposed through Classroom Studio.</summary>
public sealed class BuzzerSystemCueProfile : LightStateProfile
{
    public int NumericId { get; set; }
}

public static class BuzzerSystemCueDefaults
{
    public static List<BuzzerSystemCueProfile> Create() =>
    [
        Cue(1, "start", "دکمه فیزیکی شروع", 9),
        Cue(2, "stop", "دکمه فیزیکی توقف", 10),
        Cue(3, "pause", "دکمه فیزیکی مکث", 11),
        Cue(4, "resume", "دکمه فیزیکی ادامه", 12),
        Cue(5, "timeout", "Timeout / هشدار", 13),
        Cue(6, "error", "Error / خطا", 14),
        Cue(7, "whisper", "ویسپر جدید", 15),
        Cue(8, "whisper-repeat", "ویسپر تکراری", 16),
        Cue(9, "calibration-enter", "ورود به کالیبراسیون", 17),
        Cue(10, "calibration-exit", "خروج از کالیبراسیون", 18),
        Cue(11, "calibration-error", "خطای کالیبراسیون", 19),
        Cue(12, "calibration-success", "ذخیره موفق کالیبراسیون", 20),
        Cue(13, "calibration-complete", "تکمیل همه مراحل کالیبراسیون", 21),
        Cue(14, "calibration-record-light", "شروع ثبت نور", 22),
        Cue(15, "calibration-record-sound", "شروع ثبت صدا", 23),
        Cue(16, "calibration-sound-target", "رسیدن صدا به هدف", 24),
        Cue(17, "transition-1", "گذار محیط ۱", 25),
        Cue(18, "transition-2", "گذار محیط ۲", 26),
        Cue(19, "transition-3", "گذار محیط ۳", 27),
        Cue(20, "transition-4", "گذار محیط ۴", 28),
        Cue(21, "transition-5", "گذار محیط ۵", 29),
        Cue(22, "transition-6", "گذار محیط ۶", 30),
        Cue(23, "watchdog", "آژیر Watchdog", 31),
    ];

    private static BuzzerSystemCueProfile Cue(int numericId, string id, string name, int catalogId)
    {
        var preset = CalibrationCueCatalog.Get(catalogId);
        return new BuzzerSystemCueProfile
        {
            NumericId = numericId, Id = id, Name = name, CalibrationCue = catalogId,
            CalibrationCuePattern = preset.Pattern, CalibrationCueVolume = preset.Volume,
            CalibrationCueEnvelope = preset.Envelope, CalibrationCueTempo = preset.Tempo,
            CalibrationCueStyleVersion = 1,
        };
    }
}

public static class LightStateDefaults
{
    /// <summary>Phase-four emergency fallback values; the packaged JSON is the hardware source.</summary>
    public static List<LightStateProfile> CreateInitialProfiles() => new()
    {
        Profile("desktop", "دسکتاپ", 59, 1, tolerance: 1),
        Profile("login-or-dc", "صفحه لاگین یا DC", 74, 2, tolerance: 13.5),
        Profile("character-dashboard", "داشبورد انتخاب کرکترها", 43, 3, tolerance: 1),
        Profile("entering-game-loading", "صفحه لود ورود به بازی", 3, 4, tolerance: 1),
        Profile("game", "محیط بازی", 33, 5, tolerance: 0.7),
        Profile("targeted", "تارگت شدن — فرد جدید", 36.7, 6, tolerance: 0.5),
        Profile("targeted-repeat", "تارگت شدن — فرد تکراری", 39, 9, 60000, tolerance: 0.5),
        Profile("whisper", "ویسپر افراد جدید", 100, 7, tolerance: 1),
        Profile("whisper-repeat", "ویسپر افراد تکراری", 23, 8, 1000000, tolerance: 2.5),
    };

    private static LightStateProfile Profile(string id, string name, double center, int cue,
        int lightCooldownMs = 0, double tolerance = 2)
    {
        var preset = CalibrationCueCatalog.Get(cue);
        return new LightStateProfile
        {
            Id = id,
            Name = name,
            LuxCenter = center,
            LuxTolerance = tolerance,
            StableDurationMs = 750,
            HysteresisLux = 1,
            LightCooldownMs = lightCooldownMs,
            CalibrationCue = cue,
            CalibrationCuePattern = preset.Pattern,
            CalibrationCueVolume = preset.Volume,
            CalibrationCueEnvelope = preset.Envelope,
            CalibrationCueTempo = preset.Tempo,
            CalibrationCueStyleVersion = 1,
        };
    }
}
