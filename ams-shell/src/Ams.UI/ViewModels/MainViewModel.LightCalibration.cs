using Ams.UI.Models;
using Ams.UI.Services;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    private readonly List<(double Lux1, double Lux2)> _pairedLightCalibrationSamples = new();
    private int _lastLightCalibrationSampleChartVersion = -1;
    private DateTimeOffset? _lightCalibrationStartedAt;
    private LightStateProfile? _selectedLightCalibrationProfile;
    private int _selectedLightCalibrationDurationSeconds = 5;
    private bool _isLightCalibrationRunning;
    private string _lightCalibrationStatus = "یک پروفایل و مدت نمونه‌برداری را انتخاب کنید.";
    private DualLightCalibrationSuggestion? _pendingLightCalibrationSuggestion;

    public IReadOnlyList<int> LightCalibrationDurationsSeconds { get; } = new[] { 3, 5, 10, 20 };

    public LightStateProfile? SelectedLightCalibrationProfile
    {
        get => _selectedLightCalibrationProfile;
        set => SetProperty(ref _selectedLightCalibrationProfile, value);
    }

    public int SelectedLightCalibrationDurationSeconds
    {
        get => _selectedLightCalibrationDurationSeconds;
        set => SetProperty(ref _selectedLightCalibrationDurationSeconds, Math.Clamp(value, 3, 20));
    }

    public bool IsLightCalibrationRunning
    {
        get => _isLightCalibrationRunning;
        private set => SetProperty(ref _isLightCalibrationRunning, value);
    }

    public string LightCalibrationStatus
    {
        get => _lightCalibrationStatus;
        private set => SetProperty(ref _lightCalibrationStatus, value);
    }

    public string LightCalibrationSuggestionDisplay => _pendingLightCalibrationSuggestion is null
        ? "پیشنهادی آماده نیست."
        : $"حسگر ۱: میانه {_pendingLightCalibrationSuggestion.Sensor1CenterLux:0.0} "
          + $"[{_pendingLightCalibrationSuggestion.Sensor1MinimumLux:0.0} تا {_pendingLightCalibrationSuggestion.Sensor1MaximumLux:0.0}] Lux · "
          + $"حسگر ۲: میانه {_pendingLightCalibrationSuggestion.Sensor2CenterLux:0.0} "
          + $"[{_pendingLightCalibrationSuggestion.Sensor2MinimumLux:0.0} تا {_pendingLightCalibrationSuggestion.Sensor2MaximumLux:0.0}] Lux · "
          + $"{_pendingLightCalibrationSuggestion.SampleCount} جفت نمونه\n"
          + $"میانگین (L1+L2)/2: {_pendingLightCalibrationSuggestion.MeanCenterLux:0.0} ±{_pendingLightCalibrationSuggestion.MeanToleranceLux:0.0} Lux "
          + $"[{_pendingLightCalibrationSuggestion.MeanMinimumLux:0.0} تا {_pendingLightCalibrationSuggestion.MeanMaximumLux:0.0}] · "
          + $"اختلاف علامت‌دار L2−L1: {_pendingLightCalibrationSuggestion.DifferenceCenterLux:0.0} ±{_pendingLightCalibrationSuggestion.DifferenceToleranceLux:0.0} Lux "
          + $"[{_pendingLightCalibrationSuggestion.DifferenceMinimumLux:0.0} تا {_pendingLightCalibrationSuggestion.DifferenceMaximumLux:0.0}] · "
          + "محدوده‌های L1/L2 بالا فقط نمونه‌های همین برداشت‌اند؛ تشخیص همچنان با M/Δ انجام می‌شود.";

    public bool StartLightProfileCalibration()
    {
        if (!IsLightWatchRunning || CurrentLux is null || CurrentLux2 is null
            || !LightFreshnessText.StartsWith("به‌روز", StringComparison.Ordinal)
            || LightSensor2StatusDisplay != "سالم")
        {
            LightCalibrationStatus = "برای کالیبراسیون دوحسگری، پایش زندهٔ تازه و خوانش سالم هر دو سنسور لازم است.";
            return false;
        }
        if (SelectedLightCalibrationProfile is null)
        {
            LightCalibrationStatus = "ابتدا پروفایل مقصد را انتخاب کنید.";
            return false;
        }

        _pairedLightCalibrationSamples.Clear();
        _lastLightCalibrationSampleChartVersion = LightChartVersion;
        _pendingLightCalibrationSuggestion = null;
        _lightCalibrationStartedAt = DateTimeOffset.UtcNow;
        IsLightCalibrationRunning = true;
        OnPropertyChanged(nameof(LightCalibrationSuggestionDisplay));
        LightCalibrationStatus = $"نمونه‌برداری {SelectedLightCalibrationDurationSeconds} ثانیه‌ای برای «{SelectedLightCalibrationProfile.Name}» شروع شد.";
        return true;
    }

    public void CaptureLightCalibrationSample()
    {
        if (!IsLightCalibrationRunning || _lightCalibrationStartedAt is not DateTimeOffset started) return;
        var elapsed = DateTimeOffset.UtcNow - started;
        if (LightChartVersion != _lastLightCalibrationSampleChartVersion)
        {
            _lastLightCalibrationSampleChartVersion = LightChartVersion;
            if (CurrentLux is double lux1 && CurrentLux2 is double lux2
                && LightFreshnessText.StartsWith("به‌روز", StringComparison.Ordinal)
                && LightSensor2StatusDisplay == "سالم")
                _pairedLightCalibrationSamples.Add((lux1, lux2));
        }
        LightCalibrationStatus = $"در حال نمونه‌برداری جفت‌سنسور · {_pairedLightCalibrationSamples.Count} نمونه · {Math.Min(elapsed.TotalSeconds, SelectedLightCalibrationDurationSeconds):0.0}/{SelectedLightCalibrationDurationSeconds} ثانیه";
        if (elapsed < TimeSpan.FromSeconds(SelectedLightCalibrationDurationSeconds)) return;

        IsLightCalibrationRunning = false;
        _lightCalibrationStartedAt = null;
        if (_pairedLightCalibrationSamples.Count < DualLightCalibration.MinimumSampleCount)
        {
            _pendingLightCalibrationSuggestion = null;
            OnPropertyChanged(nameof(LightCalibrationSuggestionDisplay));
            LightCalibrationStatus =
                $"پیشنهاد ساخته نشد: حداقل {DualLightCalibration.MinimumSampleCount} نمونهٔ جفت و معتبر لازم است؛ هیچ مقداری تغییر نکرد.";
            return;
        }

        _pendingLightCalibrationSuggestion = DualLightCalibration.Suggest(_pairedLightCalibrationSamples);
        OnPropertyChanged(nameof(LightCalibrationSuggestionDisplay));
        LightCalibrationStatus = "پیشنهاد دوحسگری آماده است؛ هنوز هیچ پروفایلی تغییر نکرده است.";
    }

    public void CancelLightProfileCalibration()
    {
        IsLightCalibrationRunning = false;
        _lightCalibrationStartedAt = null;
        _pairedLightCalibrationSamples.Clear();
        LightCalibrationStatus = "نمونه‌برداری جفت‌سنسور لغو شد و هیچ مقداری تغییر نکرد.";
    }

    public bool ApplyPendingLightCalibrationSuggestion()
    {
        var selectedProfile = SelectedLightCalibrationProfile;
        if (_pendingLightCalibrationSuggestion is null || selectedProfile is null)
        {
            LightCalibrationStatus = "پیشنهاد معتبری برای اعمال وجود ندارد.";
            return false;
        }
        var profile = selectedProfile;
        var suggestion = _pendingLightCalibrationSuggestion;
        var candidate = profile.Copy();
        candidate.HasDualSensorCalibration = true;
        candidate.DualSensorCalibrationRequired = true;
        candidate.MeanLuxCenter = suggestion.MeanCenterLux;
        candidate.DifferenceLuxCenter = suggestion.DifferenceCenterLux;
        candidate.Sensor1LuxCenter = suggestion.Sensor1CenterLux;
        candidate.Sensor1LuxMinimum = suggestion.Sensor1MinimumLux;
        candidate.Sensor1LuxMaximum = suggestion.Sensor1MaximumLux;
        candidate.Sensor2LuxCenter = suggestion.Sensor2CenterLux;
        candidate.Sensor2LuxMinimum = suggestion.Sensor2MinimumLux;
        candidate.Sensor2LuxMaximum = suggestion.Sensor2MaximumLux;
        candidate.DualSensorSampleCount = suggestion.SampleCount;
        DualLightProfileValidation separation;
        var automaticToleranceReduced = false;
        if (profile.UseAutomaticDualTolerance)
        {
            if (!DualLightCalibration.TryFitAutomaticTolerances(candidate,
                    LightStateProfiles, suggestion, out separation,
                    out automaticToleranceReduced))
            {
                LightCalibrationStatus = DualLightCalibration.DescribeConflicts(separation)
                    + " تلورانس خودکار برای پوشش نویز نمونه‌ها کوچک‌تر نمی‌شود.";
                return false;
            }
        }
        else
        {
            candidate.MeanLuxTolerance = profile.HasDualSensorCalibration
                ? profile.MeanLuxTolerance : suggestion.MeanToleranceLux;
            candidate.DifferenceLuxTolerance = profile.HasDualSensorCalibration
                ? profile.DifferenceLuxTolerance : suggestion.DifferenceToleranceLux;
            separation = DualLightCalibration.ValidateCandidate(candidate, LightStateProfiles);
        }
        if (!candidate.IsValid)
        {
            LightCalibrationStatus = "پیشنهاد اعمال نشد: مقادیر دوحسگری خارج از محدودهٔ معتبرند؛ دوباره نمونه‌برداری کنید.";
            return false;
        }
        if (!separation.IsValid)
        {
            LightCalibrationStatus = DualLightCalibration.DescribeConflicts(separation);
            return false;
        }

        profile.HasDualSensorCalibration = true;
        profile.DualSensorCalibrationRequired = true;
        profile.MeanLuxCenter = candidate.MeanLuxCenter;
        profile.MeanLuxTolerance = candidate.MeanLuxTolerance;
        profile.DifferenceLuxCenter = candidate.DifferenceLuxCenter;
        profile.DifferenceLuxTolerance = candidate.DifferenceLuxTolerance;
        profile.Sensor1LuxCenter = candidate.Sensor1LuxCenter;
        profile.Sensor1LuxMinimum = candidate.Sensor1LuxMinimum;
        profile.Sensor1LuxMaximum = candidate.Sensor1LuxMaximum;
        profile.Sensor2LuxCenter = candidate.Sensor2LuxCenter;
        profile.Sensor2LuxMinimum = candidate.Sensor2LuxMinimum;
        profile.Sensor2LuxMaximum = candidate.Sensor2LuxMaximum;
        profile.DualSensorSampleCount = candidate.DualSensorSampleCount;
        RefreshLightProfileWarnings();
        // The paired tolerance editors appear only for paired-calibrated rows.
        // Rebuilding the profile table exposes them immediately after Apply.
        OnPropertyChanged(nameof(LightStateProfiles));
        LightCalibrationStatus = automaticToleranceReduced
            ? "پیشنهاد اعمال شد؛ تلورانس‌های هوشمند تا محدودهٔ امن و بدون هم‌پوشانی کاهش یافتند. برای ماندگاری «ذخیره پروفایل‌ها» را بزنید."
            : profile.UseAutomaticDualTolerance
                ? "پیشنهاد اعمال شد؛ تلورانس‌های هوشمند از پراکندگی مقاوم نمونه‌ها تنظیم شدند. برای ماندگاری «ذخیره پروفایل‌ها» را بزنید."
                : "پیشنهاد اعمال شد؛ تلورانس‌های دستی حفظ شدند. برای ماندگاری «ذخیره پروفایل‌ها» را بزنید.";
        return true;
    }

    public void ResetLightCalibrationProfileSelection(string? preferredProfileId)
    {
        IsLightCalibrationRunning = false;
        _lightCalibrationStartedAt = null;
        _pairedLightCalibrationSamples.Clear();
        _pendingLightCalibrationSuggestion = null;
        SelectedLightCalibrationProfile = LightStateProfiles.FirstOrDefault(x => x.Id == preferredProfileId)
                                          ?? LightStateProfiles.FirstOrDefault();
        OnPropertyChanged(nameof(LightCalibrationSuggestionDisplay));
        LightCalibrationStatus = "پروفایل‌ها بازنشانی شدند؛ پروفایل کالیبراسیون دوباره انتخاب شد.";
    }
}
