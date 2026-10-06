using System.Diagnostics;
using Ams.UI.Models;
using Ams.UI.Services;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    private readonly ReadOnlyLightGateCoordinator _lightGateCoordinator = new();
    private string _lightGateDiagnosticDisplay = "رد تشخیصی · پایش خاموش است";
    private string _lightGateDiagnosticReasonDisplay = "Disconnected";
    private string _lightGateObservationRevision = string.Empty;

    public string LightGateDiagnosticDisplay
    {
        get => _lightGateDiagnosticDisplay;
        private set => SetProperty(ref _lightGateDiagnosticDisplay, value);
    }

    public string LightGateDiagnosticReasonDisplay
    {
        get => _lightGateDiagnosticReasonDisplay;
        private set => SetProperty(ref _lightGateDiagnosticReasonDisplay, value);
    }

    public void StartLightGateDiagnosticSession()
    {
        var now = MonotonicNow();
        _lightGateCoordinator.StartSession(LightStateProfiles, now);
        _lightGateObservationRevision = _lightGateCoordinator.ProfileRevision;
        PublishLightGateResult(_lightGateCoordinator.LastResult, null);
    }

    public void StopLightGateDiagnosticSession()
    {
        if (_lightGateCoordinator.IsSessionActive)
            PublishLightGateResult(_lightGateCoordinator.Stop(MonotonicNow()), null);
        else
        {
            LightGateDiagnosticDisplay = "رد تشخیصی · پایش خاموش است";
            LightGateDiagnosticReasonDisplay = LightGateReasonCode.Disconnected.ToString();
        }
    }

    private void RefreshLightGateDiagnosticProfiles()
    {
        if (!_lightGateCoordinator.IsSessionActive) return;
        _lightGateObservationRevision = _lightGateCoordinator.RefreshProfiles(
            LightStateProfiles, MonotonicNow());
        PublishLightGateResult(_lightGateCoordinator.LastResult, null);
    }

    private void ObserveLightGateDiagnostic(LightStateClassification classification)
    {
        if (!IsLightWatchRunning || !_lightGateCoordinator.IsSessionActive) return;
        var now = MonotonicNow();
        var intent = IntentFor(classification.Profile?.Id);
        var result = _lightGateCoordinator.Observe(
            intent,
            classification,
            now,
            now,
            _lightGateCoordinator.ActiveSessionId ?? string.Empty,
            _lightGateObservationRevision,
            connectionHealthy: Connection == ConnectionState.Connected,
            cancellationRequested: false);
        PublishLightGateResult(result, classification.Profile?.Name);
    }

    private void MarkLightGateDiagnosticStale()
    {
        if (!_lightGateCoordinator.IsSessionActive) return;
        var now = MonotonicNow();
        var staleAt = now - TimeSpan.FromMilliseconds(2001);
        if (staleAt < TimeSpan.Zero) staleAt = TimeSpan.Zero;
        var result = _lightGateCoordinator.Observe(
            LightExecutionIntent.Desktop,
            LightStateClassification.Unknown(CurrentLux),
            staleAt,
            now,
            _lightGateCoordinator.ActiveSessionId ?? string.Empty,
            _lightGateObservationRevision,
            connectionHealthy: IsLightWatchRunning && Connection == ConnectionState.Connected,
            cancellationRequested: false);
        PublishLightGateResult(result, null);
    }

    private void PublishLightGateResult(LightGateResult result, string? profileName)
    {
        LightGateDiagnosticReasonDisplay = result.ReasonCode.ToString();
        LightGateDiagnosticDisplay = result.IsEligible
            ? $"واجد شرایط تشخیصی · {profileName ?? "وضعیت پایدار"}"
            : "رد تشخیصی · " + DescribeLightGateReason(result.ReasonCode);
    }

    private static LightExecutionIntent IntentFor(string? profileId)
        => profileId switch
        {
            "login-or-dc" => LightExecutionIntent.Login,
            "character-dashboard" => LightExecutionIntent.CharacterDashboard,
            "entering-game-loading" => LightExecutionIntent.EnteringGameLoading,
            "game" => LightExecutionIntent.Game,
            "targeted" or "targeted-repeat" => LightExecutionIntent.Targeted,
            _ => LightExecutionIntent.Desktop,
        };

    private static string DescribeLightGateReason(LightGateReasonCode reason)
        => reason switch
        {
            LightGateReasonCode.Cancelled => "لغوشده",
            LightGateReasonCode.Disconnected => "پایش یا اتصال متوقف است",
            LightGateReasonCode.WatchSessionChanged => "نشست پایش تغییر کرده است",
            LightGateReasonCode.InvalidTime => "زمان نمونه نامعتبر است",
            LightGateReasonCode.StaleSample => "داده قدیمی است",
            LightGateReasonCode.ProfileChanged => "پروفایل تغییر کرده است",
            LightGateReasonCode.UnknownState => "وضعیت نامشخص است",
            LightGateReasonCode.InvalidState => "داده نامعتبر است",
            LightGateReasonCode.AmbiguousState => "وضعیت هم‌پوشان است",
            LightGateReasonCode.CandidateOnly => "در حال تثبیت",
            LightGateReasonCode.NotStableLongEnough => "پایداری کافی نیست",
            LightGateReasonCode.IntentMismatch => "وضعیت با هدف منطبق نیست",
            LightGateReasonCode.UnsupportedState => "وضعیت پشتیبانی نمی‌شود",
            _ => reason.ToString(),
        };

    private static TimeSpan MonotonicNow()
        => TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);
}
