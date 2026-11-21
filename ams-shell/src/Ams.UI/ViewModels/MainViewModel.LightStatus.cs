using Ams.UI.Models;
using Ams.UI.Services;
using Microsoft.Win32;
using System.Text;
using CommunityToolkit.Mvvm.Input;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    private readonly Queue<(DateTimeOffset At, double Lux)> _lightHistory = new();
    private readonly Queue<double> _lightMedianWindow = new();
    private readonly Queue<double> _light2MedianWindow = new();
    private readonly List<LightTelemetryCaptureRecord> _lightCaptureRecords = new();
    private const int MaxLightCaptureRecords = 100_000;
    private string _lightCaptureLabel = string.Empty;
    private string _activeLightCaptureLabel = string.Empty;
    private bool _isLightCaptureRunning;
    private string _lightCaptureStatusText = "برای شروع، برچسب محیط را وارد کنید.";
    private LightWatchService? _lightWatch;
    private IBoardBridge? _lightWatchBridge;
    private System.Windows.Threading.DispatcherTimer? _lightFreshnessTimer;
    private DateTimeOffset? _lastSuccessfulLightAt;
    private int _activeLightIntervalMs = LightWatchService.DefaultIntervalMs;
    private int _selectedLightWatchIntervalMs = LightWatchService.DefaultIntervalMs;
    private bool _isLightWatchRunning;
    private double? _currentLux;
    private double? _currentLux2;
    private string _lightSensor1SampleAgeDisplay = "—";
    private string _lightSensor2StatusDisplay = "منتظر نمونهٔ زنده";
    private string _lightSensor2SampleAgeDisplay = "—";
    private string _lightSensorStatus = "بدون داده";
    private string _lightSensorMode = "—";
    private string _lastLightSampleText = "هنوز نمونه‌ای دریافت نشده";
    private string _lightFreshnessText = "بدون نمونه معتبر";
    private double? _lightMinimum;
    private double? _lightMaximum;
    private double? _lightAverage;
    private int _lightChartVersion;

    public IReadOnlyList<int> LightWatchIntervalsMs { get; } = new[] { 100, 250, 500, 1000 };

    public int SelectedLightWatchIntervalMs
    {
        get => _selectedLightWatchIntervalMs;
        set => SetProperty(ref _selectedLightWatchIntervalMs, value);
    }

    public bool IsLightWatchRunning
    {
        get => _isLightWatchRunning;
        private set
        {
            if (!SetProperty(ref _isLightWatchRunning, value)) return;
            OnPropertyChanged(nameof(LightWatchButtonText));
        }
    }

    public string LightWatchButtonText => IsLightWatchRunning ? "توقف پایش" : "شروع پایش";

    public string LightCaptureLabel
    {
        get => _lightCaptureLabel;
        set => SetProperty(ref _lightCaptureLabel, value);
    }
    public bool IsLightCaptureRunning
    {
        get => _isLightCaptureRunning;
        private set
        {
            if (!SetProperty(ref _isLightCaptureRunning, value)) return;
            OnPropertyChanged(nameof(LightCaptureButtonText));
            OnPropertyChanged(nameof(CanEditLightCaptureLabel));
        }
    }
    public bool CanEditLightCaptureLabel => !IsLightCaptureRunning;
    public string LightCaptureButtonText => IsLightCaptureRunning ? "پایان ثبت" : "شروع ثبت";
    public string LightCaptureCountDisplay => $"تعداد نمونه‌های ذخیره‌شده در حافظه: {_lightCaptureRecords.Count:N0}";
    public string LightCaptureStatusText { get => _lightCaptureStatusText; private set => SetProperty(ref _lightCaptureStatusText, value); }

    [RelayCommand]
    private async Task ToggleLightCapture()
    {
        if (IsLightCaptureRunning)
        {
            IsLightCaptureRunning = false;
            LightCaptureStatusText = $"ثبت «{_activeLightCaptureLabel}» متوقف شد؛ داده‌ها نگه داشته شدند.";
            return;
        }

        var label = LightCaptureLabel.Trim();
        if (label.Length == 0)
        {
            LightCaptureStatusText = "برای ثبت نمونه، ابتدا برچسب محیط را وارد کنید.";
            return;
        }
        if (!IsLightWatchRunning)
        {
            await StartLightWatchAsync();
            if (!IsLightWatchRunning)
            {
                LightCaptureStatusText = "ثبت آغاز نشد؛ اتصال برد و پایش را بررسی کنید.";
                return;
            }
        }
        _activeLightCaptureLabel = label;
        IsLightCaptureRunning = true;
        LightCaptureStatusText = $"در حال ثبت داده‌های خام برای «{_activeLightCaptureLabel}».";
    }

    [RelayCommand]
    private void ExportLightCapture()
    {
        if (_lightCaptureRecords.Count == 0)
        {
            LightCaptureStatusText = "هنوز نمونه‌ای برای صدور وجود ندارد.";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "صدور تله‌متری دو سنسور به CSV",
            Filter = "CSV (*.csv)|*.csv|همه فایل‌ها (*.*)|*.*",
            FileName = $"light-capture-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            AddExtension = true,
            DefaultExt = ".csv",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, LightTelemetryCsvExporter.ToCsv(_lightCaptureRecords), new UTF8Encoding(true));
            LightCaptureStatusText = $"{_lightCaptureRecords.Count:N0} نمونه در فایل CSV ذخیره شد.";
        }
        catch (Exception ex)
        {
            LightCaptureStatusText = "ذخیره CSV ناموفق بود: " + ex.Message;
        }
    }

    public double? CurrentLux
    {
        get => _currentLux;
        private set
        {
            if (!SetProperty(ref _currentLux, value)) return;
            OnPropertyChanged(nameof(CurrentLuxDisplay));
        }
    }
    public string CurrentLuxDisplay => CurrentLux is double value ? value.ToString("0.0") : "—";

    public double? CurrentLux2
    {
        get => _currentLux2;
        private set
        {
            if (!SetProperty(ref _currentLux2, value)) return;
            OnPropertyChanged(nameof(CurrentLux2Display));
        }
    }
    public string CurrentLux2Display => CurrentLux2 is double value ? value.ToString("0.0") : "—";
    public string LightSensor1SampleAgeDisplay
    {
        get => _lightSensor1SampleAgeDisplay;
        private set => SetProperty(ref _lightSensor1SampleAgeDisplay, value);
    }
    public string LightSensor2StatusDisplay
    {
        get => _lightSensor2StatusDisplay;
        private set => SetProperty(ref _lightSensor2StatusDisplay, value);
    }
    public string LightSensor2SampleAgeDisplay
    {
        get => _lightSensor2SampleAgeDisplay;
        private set => SetProperty(ref _lightSensor2SampleAgeDisplay, value);
    }

    public string LightSensorStatus { get => _lightSensorStatus; private set => SetProperty(ref _lightSensorStatus, value); }
    public string LightSensorMode { get => _lightSensorMode; private set => SetProperty(ref _lightSensorMode, value); }
    public string LastLightSampleText { get => _lastLightSampleText; private set => SetProperty(ref _lastLightSampleText, value); }
    public string LightFreshnessText { get => _lightFreshnessText; private set => SetProperty(ref _lightFreshnessText, value); }

    public double? LightMinimum
    {
        get => _lightMinimum;
        private set { if (SetProperty(ref _lightMinimum, value)) OnPropertyChanged(nameof(LightMinimumDisplay)); }
    }
    public double? LightMaximum
    {
        get => _lightMaximum;
        private set { if (SetProperty(ref _lightMaximum, value)) OnPropertyChanged(nameof(LightMaximumDisplay)); }
    }
    public double? LightAverage
    {
        get => _lightAverage;
        private set
        {
            if (!SetProperty(ref _lightAverage, value)) return;
            OnPropertyChanged(nameof(LightAverageDisplay));
            OnPropertyChanged(nameof(LightSpreadDisplay));
        }
    }
    public string LightMinimumDisplay => LightMinimum is double value ? value.ToString("0.0") : "—";
    public string LightMaximumDisplay => LightMaximum is double value ? value.ToString("0.0") : "—";
    public string LightAverageDisplay => LightAverage is double value ? value.ToString("0.0") : "—";
    public string LightSpreadDisplay => LightMinimum is double lo && LightMaximum is double hi ? (hi - lo).ToString("0.0") : "—";
    public int LightChartVersion { get => _lightChartVersion; private set => SetProperty(ref _lightChartVersion, value); }

    [RelayCommand]
    private async Task ToggleLightWatch()
    {
        if (IsLightWatchRunning) await StopLightWatchAsync();
        else await StartLightWatchAsync();
    }

    private async Task StartLightWatchAsync()
    {
        if (_bridge is null || Connection != ConnectionState.Connected || _bridge.State != BridgeState.Connected)
        {
            LightSensorStatus = "برد متصل نیست";
            Log("light watch: connect the Pico first");
            return;
        }

        await StopLightWatchAsync();
        _activeLightIntervalMs = SelectedLightWatchIntervalMs;
        var service = new LightWatchService(_bridge);
        service.SampleReceived += OnLightSampleReceived;
        service.WatchFaulted += OnLightWatchFaulted;
        _bridge.StateChanged += OnLightBridgeStateChanged;
        _lightWatch = service;
        _lightWatchBridge = _bridge;
        try
        {
            await service.StartAsync(TimeSpan.FromMilliseconds(_activeLightIntervalMs));
            IsLightWatchRunning = true;
            LightSensorStatus = "در انتظار اولین نمونه";
            EnsureLightFreshnessTimer();
            Log($"light watch started — interval {_activeLightIntervalMs} ms, read-only");
        }
        catch (Exception ex)
        {
            await StopLightWatchAsync();
            LightSensorStatus = "شروع پایش ناموفق";
            Log("light watch start failed: " + ex.Message);
        }
    }

    private async Task StopLightWatchAsync()
    {
        var service = _lightWatch;
        var bridge = _lightWatchBridge;
        _lightWatch = null;
        _lightWatchBridge = null;
        if (bridge is not null) bridge.StateChanged -= OnLightBridgeStateChanged;
        if (service is not null)
        {
            service.SampleReceived -= OnLightSampleReceived;
            service.WatchFaulted -= OnLightWatchFaulted;
            try { await service.DisposeAsync(); } catch (Exception ex) { Log("light watch stop: " + ex.Message); }
        }
        IsLightWatchRunning = false;
        if (IsLightCaptureRunning)
        {
            IsLightCaptureRunning = false;
            LightCaptureStatusText = "ثبت خودکار متوقف شد؛ پایش زنده متوقف است و داده‌های جمع‌شده حفظ شدند.";
        }
        UpdateLightFreshness();
    }

    private void OnLightBridgeStateChanged(object? sender, BridgeState state)
    {
        if (state == BridgeState.Connected) return;
        RunOnUi(async () =>
        {
            await StopLightWatchAsync();
            LightSensorStatus = "اتصال Pico قطع شد";
            Log("light watch stopped — board disconnected");
        });
    }

    private void OnLightWatchFaulted(Exception ex) => RunOnUi(() =>
    {
        LightSensorStatus = "خطای دریافت داده";
        Log("light watch: " + ex.Message);
    });

    private void OnLightSampleReceived(LightTelemetrySample sample) => RunOnUi(() => ApplyLightSample(sample));

    private void ApplyLightSample(LightTelemetrySample sample)
    {
        if (IsLightCaptureRunning)
        {
            var sensor1Status = sample.Status switch
            {
                LightTelemetryStatus.Ok => "ok",
                LightTelemetryStatus.NoSensor => "missing",
                LightTelemetryStatus.I2cError => "i2c_error",
                LightTelemetryStatus.Busy => "busy",
                _ => "unknown",
            };
            var sensor2Status = sample.HasDualSensorData ? sample.Sensor2Status ?? "unknown" : "unsupported";
            _lightCaptureRecords.Add(new LightTelemetryCaptureRecord(
                _activeLightCaptureLabel, sample.ReceivedAt, sample.Lux, sensor1Status, sample.Age1Ms,
                sample.HasDualSensorData ? sample.Lux2 : null, sensor2Status,
                sample.HasDualSensorData ? sample.Age2Ms : null));
            OnPropertyChanged(nameof(LightCaptureCountDisplay));
            if (_lightCaptureRecords.Count >= MaxLightCaptureRecords)
            {
                IsLightCaptureRunning = false;
                LightCaptureStatusText = "به سقف ۱۰۰٬۰۰۰ نمونه رسید؛ ثبت متوقف شد و داده‌ها حفظ شدند.";
            }
        }
        LastLightSampleText = sample.ReceivedAt.ToLocalTime().ToString("HH:mm:ss.fff");
        ApplyDualSensorSample(sample);
        switch (sample.Status)
        {
            case LightTelemetryStatus.Ok when sample.Lux is double lux:
                LightSensorStatus = "سنسور سالم";
                LightSensorMode = sample.Mode ?? "—";
                _lastSuccessfulLightAt = sample.ReceivedAt;
                _lightMedianWindow.Enqueue(lux);
                while (_lightMedianWindow.Count > 5) _lightMedianWindow.Dequeue();
                CurrentLux = Median(_lightMedianWindow);
                _lightHistory.Enqueue((sample.ReceivedAt, lux));
                TrimLightHistory(sample.ReceivedAt);
                RefreshLightStatistics();
                LightChartVersion++;
                break;
            case LightTelemetryStatus.NoSensor:
                LightSensorStatus = "سنسور BH1750 پیدا نشد";
                LightSensorMode = "—";
                break;
            case LightTelemetryStatus.I2cError:
                LightSensorStatus = "خطای I2C سنسور";
                break;
            case LightTelemetryStatus.Busy:
                LightSensorStatus = "برد مشغول اجرای فرمان است";
                break;
        }
        UpdateLightFreshness();
    }

    private void ApplyDualSensorSample(LightTelemetrySample sample)
    {
        if (!sample.HasDualSensorData)
        {
            _light2MedianWindow.Clear();
            CurrentLux2 = null;
            LightSensor1SampleAgeDisplay = "—";
            LightSensor2SampleAgeDisplay = "—";
            LightSensor2StatusDisplay = "فریمور تک‌سنسوره است؛ LUX2? پشتیبانی نمی‌شود";
            return;
        }

        if (sample.Status != LightTelemetryStatus.Ok)
        {
            _light2MedianWindow.Clear();
            CurrentLux2 = null;
            LightSensor1SampleAgeDisplay = "—";
            LightSensor2SampleAgeDisplay = "—";
            LightSensor2StatusDisplay = sample.Status == LightTelemetryStatus.NoSensor
                ? "سنسور اول پیدا نشد"
                : "خوانش جفت سنسورها ناموفق";
            return;
        }

        LightSensor1SampleAgeDisplay = sample.Age1Ms is uint age1 ? $"{age1} ms" : "—";
        LightSensor2SampleAgeDisplay = sample.Age2Ms is uint age2 ? $"{age2} ms" : "—";
        LightSensor2StatusDisplay = sample.Sensor2Status switch
        {
            "ok" => "سالم",
            "warming" => "در حال آماده‌شدن",
            "missing" => "پیدا نشد",
            _ => "وضعیت نامشخص",
        };

        if (sample.Sensor2Status == "ok" && sample.Lux2 is double lux2)
        {
            _light2MedianWindow.Enqueue(lux2);
            while (_light2MedianWindow.Count > 5) _light2MedianWindow.Dequeue();
            CurrentLux2 = Median(_light2MedianWindow);
        }
        else
        {
            _light2MedianWindow.Clear();
            CurrentLux2 = null;
        }
    }

    private void EnsureLightFreshnessTimer()
    {
        _lightFreshnessTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _lightFreshnessTimer.Tick -= OnLightFreshnessTick;
        _lightFreshnessTimer.Tick += OnLightFreshnessTick;
        _lightFreshnessTimer.Start();
    }

    private void OnLightFreshnessTick(object? sender, EventArgs e)
    {
        if (_lightWatch is { IsRunning: false } && IsLightWatchRunning)
        {
            IsLightWatchRunning = false;
            LightSensorStatus = Connection == ConnectionState.Connected ? "پایش متوقف شد" : "اتصال Pico قطع شد";
            if (IsLightCaptureRunning)
            {
                IsLightCaptureRunning = false;
                LightCaptureStatusText = "ثبت متوقف شد چون پایش زنده پایان یافت؛ نمونه‌های جمع‌شده حفظ شدند.";
            }
        }
        UpdateLightFreshness();
    }

    private void UpdateLightFreshness()
    {
        if (_lastSuccessfulLightAt is not DateTimeOffset last)
        {
            LightFreshnessText = "بدون نمونه معتبر";
            return;
        }
        var age = DateTimeOffset.UtcNow - last.ToUniversalTime();
        var threshold = LightWatchService.GetStaleThreshold(TimeSpan.FromMilliseconds(_activeLightIntervalMs));
        LightFreshnessText = age > threshold
            ? $"داده قدیمی · {age.TotalSeconds:0.0} ثانیه"
            : $"به‌روز · {age.TotalSeconds:0.0} ثانیه قبل";
    }

    private void TrimLightHistory(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromSeconds(60);
        while (_lightHistory.Count > 0 && (_lightHistory.Peek().At < cutoff || _lightHistory.Count > 600))
            _lightHistory.Dequeue();
    }

    private void RefreshLightStatistics()
    {
        if (_lightHistory.Count == 0)
        {
            LightMinimum = LightMaximum = LightAverage = null;
            return;
        }
        LightMinimum = _lightHistory.Min(x => x.Lux);
        LightMaximum = _lightHistory.Max(x => x.Lux);
        LightAverage = _lightHistory.Average(x => x.Lux);
        OnPropertyChanged(nameof(LightSpreadDisplay));
    }

    public IReadOnlyList<double> GetLightChartLuxSnapshot() => _lightHistory.Select(x => x.Lux).ToArray();

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(x => x).ToArray();
        if (ordered.Length == 0) return 0;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2 : ordered[middle];
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(action);
        else action();
    }

    private static void RunOnUi(Func<Task> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(action);
        else _ = action();
    }
}
