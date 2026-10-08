using Ams.UI.Models;
using ClassroomShift;

namespace Ams.UI.Services;

public static class ShiftTextValidation
{
    public static void ValidateText(IReadOnlyDictionary<string, object?> values)
    {
        var scope = PropEx.GetString(values, "textScope", "global");
        if (scope is not ("global" or "shift")) throw new FormatException("حالت متن نامعتبر است.");
        if (scope == "shift" && (string.IsNullOrWhiteSpace(PropEx.GetString(values, "textDay"))
            || string.IsNullOrWhiteSpace(PropEx.GetString(values, "textNight"))))
            throw new FormatException("برای متن شیفتی، هر دو متن روز و شب باید پر باشند.");
    }

    public static void ValidateTree(IEnumerable<StepNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsDisabled) continue;
            if (node.Type == "typeText") ValidateText(node.Props);
            ValidateTree(node.Children);
        }
    }

    public static (string day, string night) UserHashes(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DayWindowsUser) || string.IsNullOrWhiteSpace(settings.NightWindowsUser))
            throw new FormatException("نام کاربر ویندوز روز و شب را در تنظیمات شیفت وارد کنید.");
        string day = ShiftUserIdentity.Hash(settings.DayWindowsUser), night = ShiftUserIdentity.Hash(settings.NightWindowsUser);
        if (day == night) throw new FormatException("نام کاربران روز و شب باید متفاوت باشند.");
        return (day, night);
    }
}