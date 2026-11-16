using System.Globalization;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>Strict all-or-nothing physical calibration sync, tenths of Lux.</summary>
public static class BoardLightSync
{
    public static readonly string[] Ids = ["desktop", "login-or-dc", "character-dashboard", "entering-game-loading", "game", "targeted", "whisper", "whisper-repeat", "targeted-repeat"];
    public sealed record Range(uint Low, uint High, uint Center);
    public sealed record Snapshot(ushort Tolerance, uint Revision, IReadOnlyList<Range> Ranges);
    private static uint Number(string text) => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
        ? n : throw new FormatException("پاسخ کالیبراسیون نامعتبر است.");
    public static Snapshot Parse(string reply)
    {
        var p = reply.Trim().Split('|');
        if (p.Length != 5 || p[0] != "OK" || p[1] != "CALSYNC" || !p[2].StartsWith("tolerance=")
            || !p[3].StartsWith("revision=") || !p[4].StartsWith("profiles=")) throw new FormatException("پاسخ کالیبراسیون نامعتبر است: " + reply);
        var tolerance = Number(p[2][10..]); var revision = Number(p[3][9..]);
        var rows = p[4][9..].Split(',');
        if (tolerance is < 1 or > 10000 || rows.Length != 9) throw new FormatException("تعداد پروفایل یا تلورانس نامعتبر است.");
        var ranges = rows.Select(row => {
            var values = row.Split(':');
            if (values.Length != 3) throw new FormatException("بازهٔ نوری نامعتبر است.");
            var low = Number(values[0]); var high = Number(values[1]); var center = Number(values[2]);
            if (low > center || center > high || high > 1000000) throw new FormatException("مرکز یا مرز نوری نامعتبر است.");
            return new Range(low, high, center);
        }).ToArray();
        return new Snapshot((ushort)tolerance, revision, ranges);
    }
    public static void Apply(Snapshot snapshot, IList<LightStateProfile> profiles)
    {
        var targets = Ids.Select(id => profiles.Single(p => p.Id == id)).ToArray();
        if (snapshot.Ranges.Count != 9) throw new FormatException("تعداد پروفایل نامعتبر است.");
        for (var i = 0; i < 9; ++i) {
            var p = targets[i]; var r = snapshot.Ranges[i];
            p.LuxCenter = r.Center / 10.0;
            p.LuxTolerance = Math.Max(r.Center - r.Low, r.High - r.Center) / 10.0;
            p.LuxLowOverride = r.Low / 10.0; p.LuxHighOverride = r.High / 10.0;
        }
    }
    private static uint Tenths(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > 100000) throw new InvalidOperationException("مقدار نور خارج از محدوده است.");
        return checked((uint)Math.Round(value * 10, MidpointRounding.AwayFromZero));
    }
    public static string BuildCommand(IList<LightStateProfile> profiles, double tolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0.1 || tolerance > 1000
            || Math.Abs(tolerance * 10 - Math.Round(tolerance * 10)) > 0.000001)
            throw new InvalidOperationException("تلورانس پیش‌فرض باید از ۰٫۱ تا ۱۰۰۰ با گام ۰٫۱ لوکس باشد.");
        var rows = Ids.Select(id => {
            var p = profiles.Single(x => x.Id == id);
            if (!p.IsValid || !p.Enabled) throw new InvalidOperationException("تمام ۹ پروفایل باید فعال و معتبر باشند.");
            return $"{Tenths(p.LuxMin)}:{Tenths(p.LuxMax)}:{Tenths(p.LuxCenter)}";
        });
        return $"CALSYNC|SET|{Tenths(tolerance)}|{string.Join(",", rows)}";
    }
}
