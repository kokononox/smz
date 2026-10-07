using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using Ams.UI.Models;
using Ams.UI.Services;
using Ams.UI.Views;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<StepNode> GameBuffs => _pipelineWorkspace.GameBuffs;
    public int GameMouseFatigueMinutes
    {
        get => _pipelineWorkspace.GameMouseFatigueMinutes;
        set
        {
            if (value < 1 || value > 1440) return;
            _pipelineWorkspace.GameMouseFatigueMinutes = value;
            _dirty = true; UpdateFileText();
            OnPropertyChanged(nameof(GameMouseFatigueMinutes));
        }
    }
    private readonly Dictionary<int, string> _buffTelemetry = new();
    public string GameBuffStatusText => _buffTelemetry.Count == 0
        ? "هنوز گزارشی از Pico دریافت نشده؛ تایمر باف روی برد اجرا می‌شود."
        : string.Join("\n", _buffTelemetry.OrderBy(x => x.Key).Select(x => x.Value));

    internal void UpdateGameBuffTelemetry(string line)
    {
        int position = line.IndexOf("EVT|BUFF|", StringComparison.Ordinal);
        if (position < 0) return;
        var values = line[(position + 9)..].Split('|')
            .Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0], x => x[1]);
        if (values.TryGetValue("state", out var state) && state == "new-game")
            _buffTelemetry.Clear();
        if (values.TryGetValue("index", out var indexText) && int.TryParse(indexText, out int index))
        {
            var active = GameBuffs.Where(x => !x.IsDisabled).ToList();
            var name = index < active.Count ? active[index].Name : $"باف {index + 1}";
            if (values.TryGetValue("remaining-ms", out var remaining) && uint.TryParse(remaining, out uint ms))
            {
                string status = values.GetValueOrDefault("consumed") == "0"
                    ? "منتظر مصرف اولیه" : ms == 0 ? "موعد رسیده؛ منتظر فرصت امن"
                    : $"تجدید بعدی: {ms / 60000} دقیقه و {(ms / 1000) % 60} ثانیه";
                _buffTelemetry[index] = name + " — " + status;
            }
            else if (state == "command-completed") _buffTelemetry[index] = name + " — فرمان مصرف و مکث کامل شد";
            else if (state == "key-accepted") _buffTelemetry[index] = name + " — در حال مصرف";
        }
        OnPropertyChanged(nameof(GameBuffStatusText));
    }

    internal void EditGameBuff(StepNode? existing)
    {
        if (IsRunning) return;
        if (existing is null && GameBuffs.Count >= 16)
        {
            MessageBox.Show("حداکثر ۱۶ باف مجاز است."); return;
        }
        var fields = StepDefinitions.Get("keystroke").Fields
            .Where(f => f.Key != "keyboardBoard")
            .Select(f => f.Key == "holdMin" ? f with { Default = "90" }
                : f.Key == "holdMax" ? f with { Default = "200" } : f)
            .Concat(new FieldDef[] {
                new("__name", "نام باف", FieldKind.Text, existing?.Name ?? "باف جدید"),
                new("__enabled", "فعال", FieldKind.Check, "true"),
                new("renewMinMinutes", "حداقل فاصلهٔ تجدید (دقیقه)", FieldKind.Float, "54"),
                new("renewMaxMinutes", "حداکثر فاصلهٔ تجدید (دقیقه)", FieldKind.Float, "56"),
                new("beforeMinMs", "حداقل مکث قبل از مصرف (ms)", FieldKind.Int, "33"),
                new("beforeMaxMs", "حداکثر مکث قبل از مصرف (ms)", FieldKind.Int, "888"),
                new("__delay", "حداقل مکث پس از مصرف (ms) — مبدأ تایمر پس از این مکث", FieldKind.Int, "333"),
                new("__delayMax", "حداکثر مکث پس از مصرف (ms)", FieldKind.Int, "888"),
            }).ToArray();
        var current = existing is null ? null : new Dictionary<string, object?>(existing.Props)
        {
            ["__name"] = existing.Name, ["__enabled"] = !existing.IsDisabled,
            ["__delay"] = existing.Delay, ["__delayMax"] = existing.DelayMax == 0 ? existing.Delay : existing.DelayMax,
        };
        var dialog = new StepDialog("باف محیط بازی — اجرای مستقل روی Pico", fields, current)
        { Owner = System.Windows.Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Values is null) return;
        var values = dialog.Values;
        var buff = new StepNode
        {
            Type = "keystroke", Name = PropEx.GetString(values, "__name"),
            IsDisabled = !PropEx.GetBool(values, "__enabled", true),
            Delay = PropEx.GetInt(values, "__delay"),
            DelayMax = PropEx.GetInt(values, "__delayMax"), Props = values,
        };
        foreach (var key in values.Keys.Where(k => k.StartsWith("__", StringComparison.Ordinal)).ToArray()) values.Remove(key);
        buff.Props["keyboardBoard"] = "pico";
        try { GameBuffValidation.Validate(buff); }
        catch (FormatException ex) { MessageBox.Show(ex.Message, "باف نامعتبر"); return; }
        if (existing is null) GameBuffs.Add(buff);
        else { int index = GameBuffs.IndexOf(existing); GameBuffs[index] = buff; }
        _dirty = true; UpdateFileText();
    }

    internal void RemoveGameBuff(StepNode? buff)
    {
        if (buff is null || IsRunning) return;
        if (MessageBox.Show("این باف حذف شود؟", "حذف باف", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        GameBuffs.Remove(buff); _dirty = true; UpdateFileText();
    }
}