using System.Windows;
using Ams.UI.Models;
using Ams.UI.Services;
using CommunityToolkit.Mvvm.Input;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    public string HumanMouseProfileSummary
    {
        get
        {
            var profile = _pipelineWorkspace.HumanMouseProfile;
            if (!profile.IsValid)
                return "پروفایل دست ساخته نشده — برای Native Export الزامی است.";
            if (!HandMovementSample.TryDecode(profile.EncodedSample, out var sample))
                return "پروفایل دست نامعتبر است؛ دوباره ضبط کنید.";
            HandMovementSample.TryGetSpeedRange(sample, out var minimum, out var maximum);
            return $"پروفایل آماده · {profile.DurationMs / 1000} ثانیه · "
                + $"{sample.Segments.Count:N0} قطعه · سرعت شخصی {minimum:N0}–{maximum:N0} px/s";
        }
    }

    [RelayCommand]
    private async Task CaptureHumanMouseProfile()
    {
        var answer = MessageBox.Show(
            "پروفایل دست در ۳۰ ثانیه ساخته می‌شود.\n\n"
            + "در این مدت چند حرکت کوتاه دقیق، چند حرکت متوسط، دو یا سه حرکت بلند "
            + "و چند توقف و اصلاح طبیعی انجام بده. کلیک و صفحه‌کلید ثبت نمی‌شوند.\n\n"
            + "شروع شود؟",
            "ساخت پروفایل دست", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;

        Log("human mouse profile: recording 30 seconds...");
        var sample = await HandMovementSample.CaptureAsync(
            HandMovementSample.ProfileCaptureDurationMs);
        if (sample is null || sample.Segments.Count < 20)
        {
            MessageBox.Show("حرکت کافی ثبت نشد؛ دوباره تلاش کن.",
                "پروفایل دست", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _pipelineWorkspace.HumanMouseProfile = new HumanMouseProfile
        {
            Version = 1,
            DurationMs = sample.DurationMs,
            EncodedSample = HandMovementSample.Encode(sample),
            CapturedAtUtc = DateTime.UtcNow.ToString("O"),
        };
        MarkDirty();
        OnPropertyChanged(nameof(HumanMouseProfileSummary));
        Log($"human mouse profile ready: {sample.DurationMs / 1000}s, "
            + $"{sample.Segments.Count} segments");
        MessageBox.Show(HumanMouseProfileSummary,
            "پروفایل دست آماده است", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}