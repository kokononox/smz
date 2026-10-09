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
        else if(line.Contains("state=switching|",StringComparison.Ordinal))
            _shiftStatusText="شیفت فعلی مناسب این ساعت نیست؛ مسیر سوییچ در حال اجراست.";
        else if(line.Contains("state=switch-confirmed|",StringComparison.Ordinal))
            _shiftStatusText="ویندوز مقصد تأیید شد؛ شمارنده از دور اول آغاز می‌شود.";
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

    public bool ShiftScheduleEnabled {
        get=>_pipelineWorkspace.ShiftSchedule.enabled;
        set { _pipelineWorkspace.ShiftSchedule.enabled=value;ShiftScheduleChanged(nameof(ShiftScheduleEnabled)); }
    }
    public int ShiftMaxAttempts {
        get=>_pipelineWorkspace.ShiftSchedule.maxAttempts;
        set { _pipelineWorkspace.ShiftSchedule.maxAttempts=value;ShiftScheduleChanged(nameof(ShiftMaxAttempts)); }
    }
    private static string ShiftTime(int value)=>value>=0&&value<1440?$"{value/60:00}:{value%60:00}":"نامعتبر";
    private static int ShiftMinute(string value) {
        var parts=value.Trim().Split(':');
        return parts.Length==2&&parts[0].Length==2&&parts[1].Length==2
            &&int.TryParse(parts[0],out int h)&&int.TryParse(parts[1],out int m)
            &&h>=0&&h<24&&m>=0&&m<60?h*60+m:-1;
    }
    public string ShiftDayStart { get=>ShiftTime(_pipelineWorkspace.ShiftSchedule.dayStart);
        set { _pipelineWorkspace.ShiftSchedule.dayStart=ShiftMinute(value);ShiftScheduleChanged(nameof(ShiftDayStart)); } }
    public string ShiftDayEnd { get=>ShiftTime(_pipelineWorkspace.ShiftSchedule.dayEnd);
        set { _pipelineWorkspace.ShiftSchedule.dayEnd=ShiftMinute(value);ShiftScheduleChanged(nameof(ShiftDayEnd)); } }
    public string ShiftNightStart { get=>ShiftTime(_pipelineWorkspace.ShiftSchedule.nightStart);
        set { _pipelineWorkspace.ShiftSchedule.nightStart=ShiftMinute(value);ShiftScheduleChanged(nameof(ShiftNightStart)); } }
    public string ShiftNightEnd { get=>ShiftTime(_pipelineWorkspace.ShiftSchedule.nightEnd);
        set { _pipelineWorkspace.ShiftSchedule.nightEnd=ShiftMinute(value);ShiftScheduleChanged(nameof(ShiftNightEnd)); } }
    public string ShiftScheduleSummary {
        get {
            try { _pipelineWorkspace.ShiftSchedule.Validate(); }
            catch(FormatException ex){return ex.Message;}
            return ShiftScheduleEnabled?"فعال — ساعت محلی ویندوز؛ فاصله بین شیفت‌ها نادیده گرفته می‌شود. تغییرات نیازمند Export Native UF2 است.":"خاموش — فقط تأیید هویت قبلی اجرا می‌شود.";
        }
    }
    private void ShiftScheduleChanged(string property) {
        _dirty=true;UpdateFileText();OnPropertyChanged(property);OnPropertyChanged(nameof(ShiftScheduleSummary));
    }
    private void RefreshShiftScheduleBindings() {
        foreach(var property in new[]{nameof(ShiftScheduleEnabled),nameof(ShiftMaxAttempts),nameof(ShiftDayStart),nameof(ShiftDayEnd),nameof(ShiftNightStart),nameof(ShiftNightEnd),nameof(ShiftScheduleSummary)})OnPropertyChanged(property);
    }
}