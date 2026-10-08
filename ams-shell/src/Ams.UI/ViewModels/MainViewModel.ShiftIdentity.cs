using System.Windows;
using Ams.UI.Models;
using Ams.UI.Services;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    public string DayWindowsUser
    {
        get => _settings.DayWindowsUser;
        set { _settings.DayWindowsUser = value.Trim(); _settings.Save(); OnPropertyChanged(nameof(DayWindowsUser)); }
    }
    public string NightWindowsUser
    {
        get => _settings.NightWindowsUser;
        set { _settings.NightWindowsUser = value.Trim(); _settings.Save(); OnPropertyChanged(nameof(NightWindowsUser)); }
    }
    private string _shiftStatusText = "هنوز شیفتی توسط Pico تأیید نشده است.";
    public string ShiftStatusText => _shiftStatusText;
    internal void UpdateShiftTelemetry(string line)
    {
        if (!line.Contains("|SHIFT|", StringComparison.Ordinal)) return;
        if (line.Contains("state=verified|shift=day", StringComparison.Ordinal))
            _shiftStatusText = "روز تأیید شد؛ اتصال بریج بسته شده است.";
        else if (line.Contains("state=verified|shift=night", StringComparison.Ordinal))
            _shiftStatusText = "شب تأیید شد؛ اتصال بریج بسته شده است.";
        else if (line.Contains("state=checking", StringComparison.Ordinal))
            _shiftStatusText = "در انتظار بریج موقت؛ ادامهٔ ماکرو متوقف است.";
        else if (line.Contains("ERR|SHIFT|", StringComparison.Ordinal))
            _shiftStatusText = "تأیید ناموفق؛ Pico باید Pause شود. Resume بررسی را از نو انجام می‌دهد.";
        OnPropertyChanged(nameof(ShiftStatusText));
    }
    internal void AddShiftCheck()
    {
        if (ActivePipelineTab?.Kind is not (PipelineKind.Desktop or PipelineKind.Startup))
        {
            MessageBox.Show("ابتدا تب Desktop یا Startup را انتخاب کنید."); return;
        }
        AddStepCommand.Execute("shiftCheck");
    }
}