using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
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
    private static string PhysicalTolerancePath => Path.Combine(AppContext.BaseDirectory, "light-physical-tolerance.json");
    private static double LoadPhysicalTolerance()
    {
        try { var n = JsonSerializer.Deserialize<double>(File.ReadAllText(PhysicalTolerancePath)); return n is >= 0.1 and <= 1000 && double.IsFinite(n) ? n : 1.0; }
        catch { return 1.0; }
    }
    private double _physicalCalibrationToleranceLux = LoadPhysicalTolerance();
    public double PhysicalCalibrationToleranceLux
    {
        get => _physicalCalibrationToleranceLux;
        set => SetProperty(ref _physicalCalibrationToleranceLux, value);
    }
    private void SavePhysicalTolerance()
    {
        var n = PhysicalCalibrationToleranceLux;
        if (!double.IsFinite(n) || n < 0.1 || n > 1000 || Math.Abs(n * 10 - Math.Round(n * 10)) > 0.000001)
            throw new InvalidOperationException("تلورانس پیش‌فرض باید از ۰٫۱ تا ۱۰۰۰ با گام ۰٫۱ لوکس باشد.");
        var tmp = PhysicalTolerancePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(n));
        File.Move(tmp, PhysicalTolerancePath, overwrite: true);
    }


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
            .Update(CurrentLux, CurrentLux2, DateTimeOffset.UtcNow, fresh));
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
        if (LightStateProfiles.Any(profile => profile.HasDualSensorCalibration && !profile.IsValid))
        {
            LightProfileSaveStatus = "ذخیره نشد: مقادیر کالیبراسیون دوحسگری نامعتبرند؛ دوباره کالیبره کنید.";
            return false;
        }
        var normalized = LightStateProfileStore.Normalize(LightStateProfiles);
        if (normalized.Count != LightStateProfiles.Count)
        {
            LightProfileSaveStatus = "ذخیره نشد: نام، شناسه و مقادیر پروفایل‌ها را بررسی کنید.";
            return false;
        }
        var separation = DualLightCalibration.Validate(normalized);
        if (!separation.IsValid)
        {
            LightProfileSaveStatus = DualLightCalibration.DescribeConflicts(separation);
            return false;
        }

        try
        {
            SavePhysicalTolerance();
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
        if (LightStateProfiles.Any(profile => profile.HasDualSensorCalibration))
        {
            LightProfileSaveStatus =
                "وارد کردن از برد انجام نشد: این firmware فقط کالیبراسیون تک‌سنسوره را می‌خواند و می‌تواند پروفایل دوحسگری را ناقص کند.";
            return false;
        }
        if (_bridge is null || Connection != ConnectionState.Connected
                            || _bridge.State != BridgeState.Connected)
        {
            LightProfileSaveStatus = "وارد کردن انجام نشد: ابتدا برد را متصل کنید.";
            return false;
        }

        try
        {
            LightProfileSaveStatus = "در حال خواندن کالیبراسیون نوری از برد…";
            var syncReply = await _bridge.SendAsync("CALSYNC|GET", 3);
            if (syncReply.StartsWith("OK|CALSYNC|", StringComparison.Ordinal))
            {
                var snapshot = BoardLightSync.Parse(syncReply);
                BoardLightSync.Apply(snapshot, LightStateProfiles);
                PhysicalCalibrationToleranceLux = snapshot.Tolerance / 10.0;
                if (!SaveLightStateProfiles()) return false;
                LightProfileSaveStatus = $"۹ پروفایل و تلورانس پیش‌فرض از برد وارد و ذخیره شد · revision {snapshot.Revision}.";
                return true;
            }
            if (syncReply != "ERR|COMMAND|unknown=CALSYNC|GET")
                throw new InvalidOperationException(syncReply);

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
                $"{imported} پروفایل از برد قدیمی وارد شد · تلورانس پیش‌فرض قابل خواندن نبود · revision {dump.Revision}.";
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

    public async Task<bool> SendLightStateProfilesToBoardAsync()
    {
        if (LightStateProfiles.Any(profile => profile.HasDualSensorCalibration))
        {
            LightProfileSaveStatus =
                "ارسال انجام نشد: firmware فعلی فقط L1 را می‌پذیرد و دادهٔ دوحسگری را نادیده می‌گیرد. تا پشتیبانی firmware، پروفایل دوحسگری روی برد صادر نمی‌شود.";
            return false;
        }
        if (_bridge is null || Connection != ConnectionState.Connected || _bridge.State != BridgeState.Connected)
        {
            LightProfileSaveStatus = "ارسال انجام نشد: ابتدا برد را متصل کنید.";
            return false;
        }
        try
        {
            var command = BoardLightSync.BuildCommand(LightStateProfiles, PhysicalCalibrationToleranceLux);
            if (!SaveLightStateProfiles()) return false;
            LightProfileSaveStatus = "در حال ارسال پروفایل‌ها و تلورانس به برد…";
            var reply = await _bridge.SendAsync(command, 5);
            if (reply != "OK|CALSYNC|SET")
            {
                LightProfileSaveStatus = reply.StartsWith("ERR|COMMAND|unknown=", StringComparison.Ordinal)
                    ? "فریمور قدیمی است؛ ابتدا Native UF2 همین نسخه را روی برد فلش کنید."
                    : "ارسال به برد انجام نشد؛ اجرای برد و کالیبراسیون را متوقف و مقادیر را بررسی کنید: " + reply;
                return false;
            }
            // Read the fitted bounds back: the board can trim facing edges.
            var snapshot = BoardLightSync.Parse(await _bridge.SendAsync("CALSYNC|GET", 3));
            BoardLightSync.Apply(snapshot, LightStateProfiles);
            PhysicalCalibrationToleranceLux = snapshot.Tolerance / 10.0;
            if (!SaveLightStateProfiles()) return false;
            LightProfileSaveStatus = $"پروفایل‌ها و تلورانس روی برد ذخیره و بازه‌های نهایی وارد شدند · revision {snapshot.Revision}.";
            return true;
        }
        catch (Exception ex)
        {
            LightProfileSaveStatus = "ارسال یا خواندن تأیید برد ناموفق بود: " + ex.Message + "؛ پیش از تلاش دوباره، از برد وارد کنید.";
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
            : classification.Diagnostic ?? DescribeProfileOverlaps(LightStateProfiles);
        ObserveLightGateDiagnostic(classification);
    }

    public static string DescribeProfileOverlaps(IEnumerable<LightStateProfile> profiles)
    {
        var enabled = profiles.Where(x => x.Enabled && x.IsValid).ToArray();
        var paired = enabled.Where(profile => profile.HasDualSensorCalibration).ToArray();
        if (paired.Length > 0 && paired.Length != enabled.Length)
            return "کالیبراسیون دوحسگری ناقص است: همهٔ پروفایل‌های فعال باید با همان دو سنسور و همان ترتیب کالیبره شوند.";
        if (paired.Length > 0)
        {
            var validation = DualLightCalibration.Validate(paired);
            return validation.IsValid
                ? "پروفایل‌های فعال در فضای دو‌بعدی جدا هستند؛ حاشیهٔ امن اضافه بین محدوده‌ها حداقل ۱ لوکس است."
                : DualLightCalibration.DescribeConflicts(validation);
        }

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
