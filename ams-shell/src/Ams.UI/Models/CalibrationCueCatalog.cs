namespace Ams.UI.Models;

/// <summary>
/// Copyright-free short UI motifs for the passive GP6 buzzer.  IDs 1..8 are
/// the original Classroom Studio sounds and are intentionally immutable.
/// IDs 9..100 are compact generated motifs based on equal-tempered note
/// frequencies and common notification contours (rise, fall, answer, pulse).
/// </summary>
public sealed record CalibrationCueDefinition(
    int Id, string Category, string Name, string Pattern,
    int Volume = 100, string Envelope = "sharp", int Tempo = 100)
{
    public string DisplayName => $"{Id:000} · {Category} · {Name}";
}

public static class CalibrationCueCatalog
{
    private static readonly int[] Notes =
    [
        262, 294, 330, 349, 392, 440, 494, 523,
        587, 659, 698, 784, 880, 988, 1047, 1175,
        1319, 1397, 1568, 1760, 1976, 2093
    ];

    public static IReadOnlyList<CalibrationCueDefinition> All { get; } = Build();

    public static CalibrationCueDefinition Get(int id)
        => All.FirstOrDefault(x => x.Id == id) ?? All[0];

    private static List<CalibrationCueDefinition> Build()
    {
        var result = new List<CalibrationCueDefinition>
        {
            // Legacy defaults — keep exact pitch/timing so old projects sound identical.
            new(1, "کلاسیک", "سه‌نت صعودی آرام", "262:100,45;392:100,45;523:180"),
            new(2, "کلاسیک", "سه‌نت نزولی", "659:110,40;523:110,40;392:220"),
            new(3, "کلاسیک", "چهارنُت روشن صعودی", "330:90,35;415:90,35;494:90,35;659:190"),
            new(4, "کلاسیک", "سه‌نت مکث‌دار", "294:160,70;440:100,50;294:230"),
            new(5, "کلاسیک", "چهارنُت ورود به بازی", "392:90,30;494:90,30;587:90,30;784:190"),
            new(6, "کلاسیک", "هشدار چهارنُت متناوب", "880:85,30;740:85,30;880:85,30;740:180"),
            new(7, "کلاسیک", "ویسپر جدید — درخشان", "1047:80,25;1319:80,25;1568:80,25;2093:180"),
            new(8, "کلاسیک", "ویسپر تکراری — پاسخ‌دهنده", "1175:90,30;988:90,30;1175:90,30;1568:190"),
            // Every existing runtime/physical-button cue, kept byte-for-byte.
            new(9, "سیستم", "دکمه فیزیکی شروع", "784:160;988:160;1175:200,80;1175:280"),
            new(10, "سیستم", "دکمه فیزیکی توقف", "392:180;330:160;262:260,60;196:260"),
            new(11, "سیستم", "دکمه فیزیکی مکث", "523:180,100;523:180,100;523:340"),
            new(12, "سیستم", "دکمه فیزیکی ادامه", "659:150;784:150;988:150;784:150;988:300"),
            new(13, "سیستم", "Timeout / هشدار", "700:180,90;700:180,90;700:300"),
            new(14, "سیستم", "Error / خطا", "700:180,90;700:180,90;700:300"),
            new(15, "سیستم", "ویسپر جدید واقعی", "1397:110,45;1760:190"),
            new(16, "سیستم", "ویسپر تکراری واقعی", "1175:100,35;988:100,35;1175:190"),
            // Existing calibration lifecycle cues from the firmware.
            new(17, "کالیبراسیون", "ورود به حالت کالیبراسیون", "523:100;659:120;784:180"),
            new(18, "کالیبراسیون", "خروج از کالیبراسیون", "784:100;659:120;523:220"),
            new(19, "کالیبراسیون", "خطای ذخیره", "220:140,80;220:260"),
            new(20, "کالیبراسیون", "ذخیره موفق", "880:160,60;1175:220,60;1568:360"),
            new(21, "کالیبراسیون", "تکمیل همه مراحل", "262:90,35;294:90,35;330:90,35;349:90,35;392:90,35;440:90"),
            new(22, "کالیبراسیون", "شروع ثبت نور", "660:65"),
            new(23, "کالیبراسیون", "شروع ثبت صدا", "523:90"),
            new(24, "کالیبراسیون", "رسیدن صدا به هدف", "988:120"),
            new(25, "گذار", "تغییر محیط ۱", "550:50,10;660:50,10;770:80"),
            new(26, "گذار", "تغییر محیط ۲", "660:50,10;770:50,10;880:80"),
            new(27, "گذار", "تغییر محیط ۳", "770:50,10;880:50,10;990:80"),
            new(28, "گذار", "تغییر محیط ۴", "880:50,10;990:50,10;1100:80"),
            new(29, "گذار", "تغییر محیط ۵", "990:50,10;1100:50,10;1210:80"),
            new(30, "گذار", "تغییر محیط ۶", "1100:50,10;1210:50,10;1320:80"),
            new(31, "واچ‌داگ", "آژیر آمبولانسی — یک چرخه", "620:180,20;1000:180,20;1380:180,20;1000:180"),
        };

        string[] categories = ["اعلان", "تأیید", "هشدار", "ورود", "خروج", "دیجیتال", "آرام", "فوری"];
        string[] shapes = ["صعودی", "نزولی", "پاسخ", "پالس", "قوس", "دوضرب", "سه‌ضرب", "درخشان"];
        for (var id = 32; id <= 100; id++)
        {
            var n = id - 32;
            var category = categories[n % categories.Length];
            var shape = n % shapes.Length;
            var root = (n * 3 + n / 8) % (Notes.Length - 7);
            var pattern = shape switch
            {
                0 => P((root, 90, 30), (root + 2, 90, 30), (root + 4, 170, 0)),
                1 => P((root + 5, 90, 30), (root + 3, 90, 30), (root, 180, 0)),
                2 => P((root, 95, 35), (root + 4, 120, 45), (root + 2, 190, 0)),
                3 => P((root + 2, 70, 45), (root + 2, 70, 45), (root + 2, 150, 0)),
                4 => P((root, 80, 25), (root + 3, 90, 25), (root + 6, 80, 25), (root + 3, 180, 0)),
                5 => P((root, 120, 55), (root + 5, 210, 0)),
                6 => P((root, 75, 25), (root + 2, 75, 25), (root + 5, 190, 0)),
                _ => P((root + 3, 65, 20), (root + 5, 65, 20), (root + 7, 150, 0)),
            };
            var envelope = category is "آرام" or "ورود" ? "smooth"
                : category == "خروج" ? "fade-out" : category == "دیجیتال" ? "sharp" : "fade-in";
            var volume = category == "آرام" ? 55 : category == "هشدار" || category == "فوری" ? 100 : 80;
            result.Add(new(id, category, $"{shapes[shape]} {n / 8 + 1}", pattern, volume, envelope));
        }
        return result;
    }

    private static string P(params (int Note, int Duration, int Gap)[] values)
        => string.Join(";", values.Select(value =>
            $"{Notes[Math.Clamp(value.Note, 0, Notes.Length - 1)]}:{value.Duration}"
            + (value.Gap > 0 ? $",{value.Gap}" : "")));
}