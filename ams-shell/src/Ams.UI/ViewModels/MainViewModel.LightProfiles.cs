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
            LightProfileSaveStatus = normalized.Any(profile =>
                    profile.HasDualSensorCalibration || profile.DualSensorCalibrationRequired)
                ? "پروفایل‌ها ذخیره شدند. " + DescribeProfileOverlaps(normalized)
                : "پروفایل‌ها ذخیره شدند؛ مقادیر تک‌سنسوره تا کالیبراسیون سخت‌افزاری فرضی‌اند.";
            if (_pipelineInitialized)
            {
                CaptureProjectLightProfiles();
                MarkDirty();
                LightProfileSaveStatus += " برای ثبت داخل فایل .amsj، پروژه را هم ذخیره کنید.";
            }
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
            var dualReply = await _bridge.SendAsync("CALSYNC|DUAL|GET", 3);
            if (dualReply.StartsWith("OK|CALSYNC|DUAL|", StringComparison.Ordinal))
            {
                var dualSnapshot = DualBoardLightSync.Parse(dualReply);
                var importedDual = DualBoardLightSync.Apply(dualSnapshot, LightStateProfiles);
                if (!SaveLightStateProfiles()) return false;
                LightProfileSaveStatus =
                    $"{importedDual} پروفایل جفتی از برد وارد و ذخیره شد · revision {dualSnapshot.Revision}. "
                    + "خلاصهٔ خام L1/L2 در Studio ذخیره می‌شود و از برد قابل بازیابی نیست.";
                return true;
            }

            var supportsDualSnapshot = dualReply == "ERR|COMMAND|unknown=CALSYNC|DUAL|GET"
                                       || dualReply == "ERR|STATE|CALSYNC|DUAL";
            if (!supportsDualSnapshot)
                throw new InvalidOperationException(dualReply);

            if (LightStateProfiles.Any(profile =>
                    profile.HasDualSensorCalibration || profile.DualSensorCalibrationRequired))
            {
                LightProfileSaveStatus =
                    "وارد کردن انجام نشد: برد از همگام‌سازی دوحسگری پشتیبانی نمی‌کند یا با UF2 جفتی راه‌اندازی نشده است؛ پروفایل‌های فعلی حفظ شدند.";
                return false;
            }

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

    public async Task<bool> ImportLightStateProfileFromBoardAsync(string profileId)
    {
        var profile = LightStateProfiles.SingleOrDefault(candidate => candidate.Id == profileId);
        if (profile is null)
        {
            LightProfileSaveStatus = "وارد کردن انجام نشد: وضعیت انتخاب‌شده در برنامه پیدا نشد.";
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
            LightProfileSaveStatus = $"در حال خواندن «{profile.Name}» از برد…";
            var reply = await _bridge.SendAsync("CALSYNC|DUAL|GET", 3);
            if (!reply.StartsWith("OK|CALSYNC|DUAL|", StringComparison.Ordinal))
            {
                if (reply == "ERR|COMMAND|unknown=CALSYNC|DUAL|GET"
                    || reply == "ERR|STATE|CALSYNC|DUAL")
                {
                    LightProfileSaveStatus =
                        "وارد کردن تکی به همگام‌سازی دوحسگری نیاز دارد؛ Native UF2 دوحسگری همین نسخه را روی برد فلش کنید.";
                    return false;
                }
                throw new InvalidOperationException(reply);
            }

            var snapshot = DualBoardLightSync.Parse(reply);
            if (!DualBoardLightSync.ApplyProfile(snapshot, LightStateProfiles, profileId))
            {
                LightProfileSaveStatus =
                    $"برد برای «{profile.Name}» کالیبراسیون جفتی ندارد؛ پروفایل‌های دیگر تغییر نکردند.";
                return false;
            }
            if (!SaveLightStateProfiles()) return false;

            LightProfileSaveStatus =
                $"کالیبراسیون «{profile.Name}» از برد وارد و ذخیره شد · revision {snapshot.Revision}. "
                + "هشت وضعیت دیگر تغییر نکردند؛ خلاصهٔ خام L1/L2 از برد قابل بازیابی نیست.";
            return true;
        }
        catch (Exception ex)
        {
            LightProfileSaveStatus = "وارد کردن تکی از برد ناموفق بود: " + ex.Message;
            return false;
        }
    }

    public async Task<bool> SendLightStateProfilesToBoardAsync()
    {
        if (_bridge is null || Connection != ConnectionState.Connected || _bridge.State != BridgeState.Connected)
        {
            LightProfileSaveStatus = "ارسال انجام نشد: ابتدا برد را متصل کنید.";
            return false;
        }
        try
        {
            var useDualSync = LightStateProfiles.Any(profile =>
                profile.HasDualSensorCalibration || profile.DualSensorCalibrationRequired);
            if (useDualSync)
            {
                var calibratedProfileIds = LightStateProfiles
                    .Where(profile => profile.Enabled && profile.HasDualSensorCalibration)
                    .Select(profile => profile.Id)
                    .ToArray();
                if (calibratedProfileIds.Length == 0)
                {
                    LightProfileSaveStatus =
                        "ارسال انجام نشد: ابتدا کالیبراسیون جفتی یک وضعیت را کامل و اعمال کنید.";
                    return false;
                }
                return await SendDualLightProfilesToBoardAsync(calibratedProfileIds);
            }

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

    public async Task<bool> SendLightStateProfileToBoardAsync(string profileId)
    {
        var profile = LightStateProfiles.SingleOrDefault(candidate => candidate.Id == profileId);
        if (profile is null || !profile.Enabled || !profile.HasDualSensorCalibration)
        {
            LightProfileSaveStatus =
                "ارسال انجام نشد: این وضعیت باید فعال و جفت‌کالیبره باشد.";
            return false;
        }
        return await SendDualLightProfilesToBoardAsync(new[] { profileId });
    }

    private async Task<bool> SendDualLightProfilesToBoardAsync(
        IReadOnlyCollection<string> profileIds)
    {
        if (_bridge is null || Connection != ConnectionState.Connected
                            || _bridge.State != BridgeState.Connected)
        {
            LightProfileSaveStatus = "ارسال انجام نشد: ابتدا برد را متصل کنید.";
            return false;
        }

        try
        {
            var command = DualBoardLightSync.BuildCommand(LightStateProfiles, profileIds);
            if (!SaveLightStateProfiles()) return false;
            LightProfileSaveStatus =
                $"در حال ارسال {profileIds.Count} وضعیت جفت‌کالیبره‌شده به برد…";
            var reply = await _bridge.SendAsync(command, 5);
            if (!reply.StartsWith("OK|CALSYNC|DUAL|SET|revision=", StringComparison.Ordinal))
            {
                LightProfileSaveStatus = reply switch
                {
                    _ when reply.StartsWith("ERR|COMMAND|unknown=", StringComparison.Ordinal)
                        => "فریمور برد همگام‌سازی دوحسگری را نمی‌شناسد؛ Native UF2 دوحسگری همین نسخه را فلش کنید.",
                    _ when reply.StartsWith("ERR|ARG|CALSYNC|DUAL", StringComparison.Ordinal)
                        => "فریمور فعلی فقط ارسال هر ۹ وضعیت را می‌پذیرد؛ برای ارسال تکی یا ناقص، Native UF2 به‌روز را فلش کنید.",
                    _ when reply.StartsWith("ERR|CONFIG|CALSYNC|DUAL|", StringComparison.Ordinal)
                        => "تلورانس‌های برد با پروفایل‌ها یکی نیستند؛ UF2 را با تلورانس‌های فعلی بسازید و فلش کنید، سپس دوباره ارسال کنید.",
                    _ when reply.StartsWith("ERR|FIT|CALSYNC|DUAL", StringComparison.Ordinal)
                        => "برد این تغییر را به‌دلیل هم‌پوشانی یا فاصلهٔ ایمنی ناکافی با وضعیت‌های دیگر نپذیرفت.",
                    _ => "ارسال دوحسگری ناموفق بود؛ اجرا و کالیبراسیون را متوقف و مقادیر را بررسی کنید: " + reply,
                };
                return false;
            }

            var snapshot = DualBoardLightSync.Parse(
                await _bridge.SendAsync("CALSYNC|DUAL|GET", 3));
            if (!DualBoardLightSync.Matches(snapshot, LightStateProfiles, profileIds))
            {
                LightProfileSaveStatus =
                    "تأیید برد با مقادیر ارسالی یکسان نبود؛ پیش از تلاش دوباره، از برد وارد کنید.";
                return false;
            }

            var unsentCount = LightStateProfiles.Count(profile =>
                profile.Enabled && profile.DualSensorCalibrationRequired
                && !profile.HasDualSensorCalibration);
            var unsentNote = unsentCount > 0
                ? $" {unsentCount} وضعیتِ هنوز کالیبره‌نشده ارسال نشد."
                : "";
            LightProfileSaveStatus =
                $"{profileIds.Count} وضعیت جفتی روی برد ذخیره و تأیید شد · revision {snapshot.Revision}. "
                + $"وضعیت‌های دیگر تغییر نکردند.{unsentNote} "
                + "تلورانس‌ها در UF2 فعال می‌مانند؛ برای تغییرشان UF2 را دوباره بسازید و فلش کنید.";
            return true;
        }
        catch (Exception ex)
        {
            LightProfileSaveStatus = "ارسال یا خواندن تأیید برد ناموفق بود: " + ex.Message
                                     + "؛ پیش از تلاش دوباره، از برد وارد کنید.";
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
        => LightProfileSaveStatus =
            "ذخیره نشد: فیلدها را بررسی کنید؛ تلورانس M و Δ در پروفایل دوحسگری باید حداقل ۳ لوکس و با گام مجاز برد باشد.";

    public void ResetLightStateProfiles()
    {
        var preferredCalibrationProfileId = SelectedLightCalibrationProfile?.Id;
        LightStateProfiles.Clear();
        foreach (var profile in LightStateDefaults.CreateInitialProfiles()) LightStateProfiles.Add(profile);
        ResetLightCalibrationProfileSelection(preferredCalibrationProfileId);
        SaveLightStateProfiles();
    }

    /// <summary>
    /// Starts a new fixed sensor-layout calibration set. Legacy L1 values are retained
    /// for compatibility but cannot classify or export while the paired set is incomplete.
    /// </summary>
    public bool ResetDualSensorCalibrationSet()
    {
        CancelLightProfileCalibration();
        _pendingLightCalibrationSuggestion = null;
        OnPropertyChanged(nameof(LightCalibrationSuggestionDisplay));
        var previous = LightStateProfiles.ToDictionary(profile => profile.Id, profile => profile.Copy(),
            StringComparer.Ordinal);
        foreach (var profile in LightStateProfiles)
        {
            profile.HasDualSensorCalibration = false;
            profile.DualSensorCalibrationRequired = true;
            profile.MeanLuxCenter = 0;
            profile.MeanLuxTolerance = 0;
            profile.DifferenceLuxCenter = 0;
            profile.DifferenceLuxTolerance = 0;
            profile.Sensor1LuxCenter = null;
            profile.Sensor1LuxMinimum = null;
            profile.Sensor1LuxMaximum = null;
            profile.Sensor2LuxCenter = null;
            profile.Sensor2LuxMinimum = null;
            profile.Sensor2LuxMaximum = null;
            profile.DualSensorSampleCount = null;
        }

        if (!SaveLightStateProfiles())
        {
            foreach (var profile in LightStateProfiles)
            {
                if (!previous.TryGetValue(profile.Id, out var saved)) continue;
                profile.HasDualSensorCalibration = saved.HasDualSensorCalibration;
                profile.DualSensorCalibrationRequired = saved.DualSensorCalibrationRequired;
                profile.MeanLuxCenter = saved.MeanLuxCenter;
                profile.MeanLuxTolerance = saved.MeanLuxTolerance;
                profile.DifferenceLuxCenter = saved.DifferenceLuxCenter;
                profile.DifferenceLuxTolerance = saved.DifferenceLuxTolerance;
                profile.Sensor1LuxCenter = saved.Sensor1LuxCenter;
                profile.Sensor1LuxMinimum = saved.Sensor1LuxMinimum;
                profile.Sensor1LuxMaximum = saved.Sensor1LuxMaximum;
                profile.Sensor2LuxCenter = saved.Sensor2LuxCenter;
                profile.Sensor2LuxMinimum = saved.Sensor2LuxMinimum;
                profile.Sensor2LuxMaximum = saved.Sensor2LuxMaximum;
                profile.DualSensorSampleCount = saved.DualSensorSampleCount;
            }
            _lightStateClassifier = new LightStateClassifier(LightStateProfiles);
            _lastClassifiedChartVersion = -1;
            RefreshLightProfileWarnings();
            return false;
        }

        LightProfileSaveStatus =
            "چیدمان دوحسگری جدید ثبت شد؛ کالیبراسیون جفتی همهٔ محیط‌های فعال لازم است و تشخیص L1 به‌تنهایی غیرفعال ماند.";
        LightStateWarning = DescribeProfileOverlaps(LightStateProfiles);
        return true;
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
        var dualRequired = enabled.Any(profile =>
            profile.HasDualSensorCalibration || profile.DualSensorCalibrationRequired);
        if (dualRequired && paired.Length != enabled.Length)
            return "کالیبراسیون دوحسگری ناقص است: همهٔ پروفایل‌های فعال باید در همین چیدمان و با همان ترتیب سنسورها کالیبره شوند؛ تشخیص تک‌سنسوری غیرفعال است.";
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
