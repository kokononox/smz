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
    public ObservableCollection<BuzzerSystemCueProfile> BuzzerSystemCues { get; }
        = new(BuzzerSystemCueStore.Load());

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

    public async Task<bool> ImportLightStateProfilesFromBoardAsync()
    {
        if (_bridge is null || Connection != ConnectionState.Connected
                            || _bridge.State != BridgeState.Connected)
        {
            LightProfileSaveStatus = "وارد کردن انجام نشد: ابتدا برد را متصل کنید.";
            return false;
        }

        try
        {
            LightProfileSaveStatus = "در حال خواندن کالیبراسیون نوری از برد…";
            var reply = await _bridge.SendAsync("CALDUMP|LIGHT", 3);
            if (reply == "ERR|COMMAND|unknown=CALDUMP|LIGHT")
            {
                LightProfileSaveStatus =
                    "Firmware برد قدیمی است؛ با همین نسخه یک Native UF2 جدید بسازید و روی برد فلش کنید.";
                return false;
            }
            var dump = LightCalibrationDumpParser.Parse(reply);
            if (dump.Profiles.Count == 0)
            {
                LightProfileSaveStatus = "برد هنوز هیچ کالیبراسیون نوری ذخیره‌شده‌ای ندارد.";
                return false;
            }

            var imported = LightCalibrationDumpParser.Apply(dump, LightStateProfiles);
            if (!SaveLightStateProfiles()) return false;
            LightProfileSaveStatus =
                $"{imported} پروفایل از برد وارد و ذخیره شد · revision {dump.Revision}.";
            return true;
        }
        catch (Exception ex)
        {
            LightProfileSaveStatus = ex.Message.Contains("ERR|TIMEOUT|CALDUMP", StringComparison.Ordinal)
                ? "برد پاسخ CALDUMP نداد؛ Native UF2 همین نسخه را روی برد فلش و دوباره متصل کنید."
                : "وارد کردن از برد ناموفق بود: " + ex.Message;
            return false;
        }
    }

    public bool SaveBuzzerSystemCues()
    {
        try
        {
            BuzzerSystemCueStore.Save(BuzzerSystemCues);
            LightProfileSaveStatus = "همه صداهای سیستمی و دکمه‌های فیزیکی ذخیره شدند.";
            return true;
        }
        catch (Exception ex)
        {
            LightProfileSaveStatus = "ذخیره صداهای سیستمی ناموفق: " + ex.Message;
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

    public async Task<string> PreviewLightCalibrationCueAsync(LightStateProfile profile)
    {
        var definition = CalibrationCueCatalog.Get(Math.Max(1, profile.CalibrationCue));
        var values = new Dictionary<string, object?>
        {
            ["preset"] = "custom",
            ["pattern"] = profile.CalibrationCue == 0 ? profile.CalibrationCuePattern : definition.Pattern,
            // Preset chooses notes only. Style and playback rate belong to this
            // assignment and remain editable for both preset and custom motifs.
            ["volume"] = profile.CalibrationCueVolume,
            ["envelope"] = profile.CalibrationCueEnvelope,
            ["tempo"] = profile.CalibrationCueTempo,
        };
        return await PreviewBuzzerCommandsAsync(StepDefinitions.BuildBuzzerCommands(values));
    }

    public async Task<string> PreviewLightCalibrationCueAsync(int cue)
    {
        var definition = CalibrationCueCatalog.Get(cue);
        return await PreviewLightCalibrationCueAsync(new LightStateProfile
        {
            CalibrationCue = definition.Id,
            CalibrationCuePattern = definition.Pattern,
            CalibrationCueVolume = definition.Volume,
            CalibrationCueEnvelope = definition.Envelope,
            CalibrationCueTempo = definition.Tempo,
            CalibrationCueStyleVersion = 1,
        });
    }
}
