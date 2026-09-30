using System.Collections.ObjectModel;
using Ams.UI.Models;
using Ams.UI.Services;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    private LightStateClassifier? _lightStateClassifier;
    private int _lastClassifiedChartVersion = -1;
    private string _lightStateDisplay = "نامشخص";
    private string _lightStateConfidenceDisplay = "—";
    private string _lightStateWarning = "";
    private string _lightProfileSaveStatus = "اعداد فعلی فرضی و قابل تنظیم‌اند.";

    public ObservableCollection<LightStateProfile> LightStateProfiles { get; }
        = new(LightStateProfileStore.Load());

    public string LightStateDisplay
    {
        get => _lightStateDisplay;
        private set => SetProperty(ref _lightStateDisplay, value);
    }

    public string LightStateConfidenceDisplay
    {
        get => _lightStateConfidenceDisplay;
        private set => SetProperty(ref _lightStateConfidenceDisplay, value);
    }

    public string LightStateWarning
    {
        get => _lightStateWarning;
        private set => SetProperty(ref _lightStateWarning, value);
    }

    public string LightProfileSaveStatus
    {
        get => _lightProfileSaveStatus;
        private set => SetProperty(ref _lightProfileSaveStatus, value);
    }

    /// <summary>Called only after a new median sample has completed its UI update.</summary>
    public void ObserveCurrentLightState()
    {
        if (_lastClassifiedChartVersion == LightChartVersion) return;
        _lastClassifiedChartVersion = LightChartVersion;
        var fresh = LightFreshnessText.StartsWith("به‌روز", StringComparison.Ordinal);
        ApplyLightClassification((_lightStateClassifier ??= new LightStateClassifier(LightStateProfiles))
            .Update(CurrentLux, DateTimeOffset.UtcNow, fresh));
    }

    public void MarkCurrentLightStateStale()
    {
        _lightStateClassifier?.Reset();
        LightStateDisplay = "نامشخص — داده قدیمی";
        LightStateConfidenceDisplay = "—";
        MarkLightGateDiagnosticStale();
    }

    public bool SaveLightStateProfiles()
    {
        var normalized = LightStateProfileStore.Normalize(LightStateProfiles);
        if (normalized.Count != LightStateProfiles.Count)
        {
            LightProfileSaveStatus = "ذخیره نشد: نام، شناسه و مقادیر پروفایل‌ها را بررسی کنید.";
            return false;
        }

        try
        {
            LightStateProfileStore.Save(normalized);
            _lightStateClassifier = new LightStateClassifier(LightStateProfiles);
            _lastClassifiedChartVersion = -1;
            RefreshLightGateDiagnosticProfiles();
            LightStateWarning = DescribeProfileOverlaps(LightStateProfiles);
            LightProfileSaveStatus = "پروفایل‌ها ذخیره شدند؛ مقادیر تا کالیبراسیون سخت‌افزاری فرضی‌اند.";
            return true;
        }
        catch (Exception ex)
        {
            LightProfileSaveStatus = "ذخیره ناموفق: " + ex.Message;
            return false;
        }
    }

    public void ReportLightProfileEditorValidationError()
        => LightProfileSaveStatus = "ذخیره نشد: یک یا چند فیلد خالی یا دارای قالب نامعتبر است.";

    public void ResetLightStateProfiles()
    {
        var preferredCalibrationProfileId = SelectedLightCalibrationProfile?.Id;
        LightStateProfiles.Clear();
        foreach (var profile in LightStateDefaults.CreateInitialProfiles()) LightStateProfiles.Add(profile);
        ResetLightCalibrationProfileSelection(preferredCalibrationProfileId);
        SaveLightStateProfiles();
    }

    public void RefreshLightProfileWarnings()
        => LightStateWarning = DescribeProfileOverlaps(LightStateProfiles);

    private void ApplyLightClassification(LightStateClassification classification)
    {
        LightStateConfidenceDisplay = classification.Confidence is double confidence
            ? $"{confidence:P0}"
            : "—";
        LightStateDisplay = classification.Kind switch
        {
            LightStateClassificationKind.Stable => classification.Profile?.Name ?? "نامشخص",
            LightStateClassificationKind.Candidate => $"{classification.Profile?.Name ?? "نامشخص"} · در حال تثبیت",
            LightStateClassificationKind.Ambiguous => "نامشخص — هم‌پوشانی پروفایل‌ها",
            LightStateClassificationKind.Invalid => "داده نامعتبر",
            _ => "نامشخص",
        };
        LightStateWarning = classification.Kind == LightStateClassificationKind.Ambiguous
            ? "هم‌پوشانی فعال: " + string.Join(" / ", classification.Matches?.Select(x => x.Name) ?? Array.Empty<string>())
            : DescribeProfileOverlaps(LightStateProfiles);
        ObserveLightGateDiagnostic(classification);
    }

    public static string DescribeProfileOverlaps(IEnumerable<LightStateProfile> profiles)
    {
        var enabled = profiles.Where(x => x.Enabled && x.IsValid).ToArray();
        var pairs = new List<string>();
        for (var i = 0; i < enabled.Length; i++)
        for (var j = i + 1; j < enabled.Length; j++)
        {
            var lo = Math.Max(enabled[i].LuxMin, enabled[j].LuxMin);
            var hi = Math.Min(enabled[i].LuxMax, enabled[j].LuxMax);
            if (lo <= hi) pairs.Add($"{enabled[i].Name} / {enabled[j].Name}: {lo:0.#} تا {hi:0.#} Lux");
        }
        return pairs.Count == 0 ? "هم‌پوشانی فعالی وجود ندارد." : "هشدار هم‌پوشانی — " + string.Join("؛ ", pairs);
    }

    public async Task<string> PreviewLightCalibrationCueAsync(int cue)
    {
        var motifs = new Dictionary<int, (int Hz, int Duration, int Gap)[]>
        {
            [1] = [(262, 100, 45), (392, 100, 45), (523, 180, 0)],
            [2] = [(659, 110, 40), (523, 110, 40), (392, 220, 0)],
            [3] = [(330, 90, 35), (415, 90, 35), (494, 90, 35), (659, 190, 0)],
            [4] = [(294, 160, 70), (440, 100, 50), (294, 230, 0)],
            [5] = [(392, 90, 30), (494, 90, 30), (587, 90, 30), (784, 190, 0)],
            [6] = [(880, 85, 30), (740, 85, 30), (880, 85, 30), (740, 180, 0)],
            [7] = [(1047, 80, 25), (1319, 80, 25), (1568, 80, 25), (2093, 180, 0)],
            [8] = [(1175, 90, 30), (988, 90, 30), (1175, 90, 30), (1568, 190, 0)],
        };
        if (!motifs.TryGetValue(cue, out var motif))
            throw new ArgumentOutOfRangeException(nameof(cue));
        var commands = new List<string>();
        foreach (var (hz, duration, gap) in motif)
        {
            commands.Add($"BEEP|{hz},{duration},100,0");
            if (gap > 0) commands.Add($"DLY|{gap}");
        }
        return await PreviewBuzzerCommandsAsync(commands);
    }
}
