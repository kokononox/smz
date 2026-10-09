using Ams.UI.Models;

namespace Ams.UI.Services;

public static class GameBuffValidation
{
    public static void Validate(StepNode buff)
    {
        if (buff.Type != "keystroke") throw new FormatException("باف باید از نوع Keystroke باشد.");
        if (buff.IsDisabled) return;
        var p = buff.Props;
        var key = PropEx.GetString(p, "key");
        if (!KeyMap.VK.ContainsKey(key)) throw new FormatException("کلید باف معتبر نیست.");
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