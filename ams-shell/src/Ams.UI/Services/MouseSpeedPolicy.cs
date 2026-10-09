using Ams.UI.Models;
namespace Ams.UI.Services;

/// <summary>Opt-in speed policy. Missing keys keep the legacy behavior.</summary>
public static class MouseSpeedPolicy
{
    public static Dictionary<string, object?> ResolveProfile(IReadOnlyDictionary<string, object?> props, string global)
    {
        var p = props.ToDictionary(k => k.Key, k => k.Value);
        string source = PropEx.GetString(p, "handProfileSource", "legacy");
        if (source == "global") p["handSample"] = global;
        else if (source != "legacy" && source != "local") throw new FormatException("منبع پروفایل نامعتبر است.");
        if (source != "legacy" && (!HandMovementSample.TryDecode(PropEx.GetString(p, "handSample"), out var sample) || sample.Segments.Count < 20))
            throw new FormatException("پروفایل انتخاب‌شده معتبر نیست؛ نمونهٔ دست را ضبط یا جایگزین کنید.");
        return p;
    }
    public static int Validate(IReadOnlyDictionary<string, object?> p)
    {
        string mode = PropEx.GetString(p, "speedMode", "legacy");
        if (!new[] { "legacy", "profile", "slow", "normal", "fast", "mixed", "custom" }.Contains(mode))
            throw new FormatException("حالت سرعت نامعتبر است.");
        if (mode == "legacy") return 0;
        if (PropEx.GetString(p, "handProfileSource", "legacy") is not ("global" or "local"))
            throw new FormatException("برای حالت سرعت جدید، منبع global یا local را صریح انتخاب کنید.");
        if (!HandMovementSample.TryDecode(PropEx.GetString(p, "handSample"), out var sample)
            || !HandMovementSample.TryGetSpeedRange(sample, out _, out var maximum))
            throw new FormatException("برای کنترل سرعت، پروفایل دست معتبر انتخاب کنید.");
        int cap = PropEx.GetInt(p, "speedCapPxPerSec", 0);
        if (cap == 0) cap = maximum;
        if (cap < 150 || cap > maximum) throw new FormatException($"سقف سرعت باید بین ۱۵۰ و {maximum} باشد؛ صفر یعنی سقف پروفایل.");
        int slow = PropEx.GetInt(p, "speedSlowWeight", 20), normal = PropEx.GetInt(p, "speedNormalWeight", 50), fast = PropEx.GetInt(p, "speedFastWeight", 30);
        if (slow < 0 || normal < 0 || fast < 0 || slow > 100 || normal > 100 || fast > 100 || slow + normal + fast == 0)
            throw new FormatException("وزن‌ها باید ۰ تا ۱۰۰ باشند و مجموع آن‌ها مثبت باشد.");
        int lo = PropEx.GetInt(p, "speedCustomMin", 300), hi = PropEx.GetInt(p, "speedCustomMax", 530);
        if (mode == "custom" && (lo < 150 || lo > hi || hi > cap)) throw new FormatException("بازهٔ سفارشی باید مرتب و زیر سقف دست باشد.");
        return cap;
    }
    public static (int low, int high, string selected) Select(string mode, int low, int cap, int slow, int normal, int fast, int customLow, int customHigh, Random rng)
    {
        if (mode == "mixed")
        {
            int pick = rng.Next(slow + normal + fast);
            mode = pick < slow ? "slow" : pick < slow + normal ? "normal" : "fast";
        }
        low = Math.Clamp(low, 150, cap);
        return mode switch
        {
            "slow" => (150, Math.Max(150, (low + cap) / 2), mode),
            "normal" => (low, Math.Max(low, low + (cap - low) * 2 / 3), mode),
            "fast" => (Math.Max(low, cap * 85 / 100), cap, mode),
            "custom" => (customLow, customHigh, mode),
            _ => (low, cap, mode),
        };
    }
}
