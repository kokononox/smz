using Ams.UI.Models;

namespace Ams.UI.Services;

public static class GameBuffValidation
{
    // Keep UI choices aligned with the native Pico HID actor and ABVM compiler.
    public static bool IsSupportedFollowKey(string key) => KeyMap.VK.TryGetValue(key, out int code)
        && ((code >= 48 && code <= 57) || (code >= 65 && code <= 90)
            || (code >= 96 && code <= 105) || (code >= 112 && code <= 123)
            || code is 8 or 9 or 13 or 27 or 32 or 37 or 38 or 39 or 40);

    public static void Validate(StepNode buff)
    {
        if (buff.Type != "keystroke") throw new FormatException("باف باید از نوع Keystroke باشد.");
        if (buff.IsDisabled) return;
        var p = buff.Props;
        var key = PropEx.GetString(p, "key");
        if (!KeyMap.VK.ContainsKey(key)) throw new FormatException("کلید باف معتبر نیست.");
        if (PropEx.GetBool(p, "followEnabled") && !IsSupportedFollowKey(PropEx.GetString(p, "followKey")))
            throw new FormatException("کلید دوم باف معتبر نیست.");
        var modifiers = new[] { "modCtrl", "modShift", "modAlt", "modWin" }.Count(k => PropEx.GetBool(p, k));
        if (modifiers > 3) throw new FormatException("حداکثر سه Modifier همراه کلید مجاز است.");
        double lo = PropEx.GetDouble(p, "renewMinMinutes", 54), hi = PropEx.GetDouble(p, "renewMaxMinutes", 56);
        if (!double.IsFinite(lo) || !double.IsFinite(hi) || lo * 60000 < 1 ||
            lo > hi || hi * 60000 > int.MaxValue)
            throw new FormatException("بازهٔ تجدید باید مثبت، مرتب و حداکثر حدود ۲۴ روز باشد.");
        Range(PropEx.GetInt(p, "beforeMinMs"), PropEx.GetInt(p, "beforeMaxMs"), false);
        Range(PropEx.GetInt(p, "holdMin", 90), PropEx.GetInt(p, "holdMax", 200), true);
        Range(buff.Delay, buff.DelayMax == 0 ? buff.Delay : buff.DelayMax, false);
        if (buff.Children.Count != 0) throw new FormatException("باف نباید استپ فرزند داشته باشد.");
    }

    private static void Range(int lo, int hi, bool positive)
    {
        if (lo < 0 || lo > hi || (positive && lo == 0))
            throw new FormatException("بازهٔ مکث/نگه‌داشتن کلید نامعتبر است.");
    }
}