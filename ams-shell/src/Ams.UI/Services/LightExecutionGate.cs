using Ams.UI.Models;

namespace Ams.UI.Services;

public enum LightExecutionIntent
{
    Desktop,
    Login,
    Disconnect,
    CharacterDashboard,
    EnteringGameLoading,
    Game,
    Targeted,
}

public enum LightGateDecisionKind
{
    Eligible,
    Denied,
}

public enum LightGateReasonCode
{
    EligibleStableMatch,
    Cancelled,
    Disconnected,
    WatchSessionChanged,
    InvalidTime,
    StaleSample,
    ProfileChanged,
    UnknownState,
    InvalidState,
    AmbiguousState,
    CandidateOnly,
    NotStableLongEnough,
    IntentMismatch,
    UnsupportedState,
}

/// <summary>
/// Immutable, monotonic-time snapshot for a diagnostic gate evaluation.
/// TimeSpan values are elapsed values from the same caller-owned monotonic clock.
/// </summary>
public sealed record LightGateInput(
    LightExecutionIntent Intent,
    LightStateClassification Classification,
    TimeSpan ObservedAt,
    TimeSpan StableSince,
    TimeSpan Now,
    string WatchSessionId,
    string ExpectedWatchSessionId,
    string ProfileRevision,
    string ExpectedProfileRevision,
    bool ConnectionHealthy,
    bool CancellationRequested,
    TimeSpan MaxSampleAge,
    TimeSpan RequiredStableDuration);

/// <summary>
/// Diagnostic data only. Intent is evidence of what the pure evaluator checked; it is not a
/// command or authorization. This result intentionally contains no callback, transport or runtime.
/// </summary>
public sealed record LightGateResult(
    LightGateDecisionKind Decision,
    LightGateReasonCode ReasonCode,
    TimeSpan EvaluatedAt,
    string WatchSessionId,
    string ProfileRevision,
    TimeSpan? SampleAge = null,
    TimeSpan? StableDuration = null,
    LightExecutionIntent? Intent = null)
{
    public bool IsEligible => Decision == LightGateDecisionKind.Eligible;
}

/// <summary>
/// Pure fail-closed evaluator. It has no state and no dependency on transport, HID, runtime,
/// launch, recovery, buzzer or firmware services.
/// </summary>
public static class LightExecutionGate
{
    public static LightGateResult Evaluate(LightGateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Classification);

        LightGateResult Deny(LightGateReasonCode reason, TimeSpan? age = null, TimeSpan? stable = null)
            => new(LightGateDecisionKind.Denied, reason, input.Now,
                input.WatchSessionId ?? string.Empty, input.ProfileRevision ?? string.Empty,
                age, stable, input.Intent);

        if (input.CancellationRequested)
            return Deny(LightGateReasonCode.Cancelled);
        if (!input.ConnectionHealthy)
            return Deny(LightGateReasonCode.Disconnected);
        if (string.IsNullOrWhiteSpace(input.WatchSessionId)
            || string.IsNullOrWhiteSpace(input.ExpectedWatchSessionId)
            || !string.Equals(input.WatchSessionId, input.ExpectedWatchSessionId, StringComparison.Ordinal))
            return Deny(LightGateReasonCode.WatchSessionChanged);

        if (input.ObservedAt < TimeSpan.Zero
            || input.StableSince < TimeSpan.Zero
            || input.Now < TimeSpan.Zero
            || input.MaxSampleAge < TimeSpan.Zero
            || input.RequiredStableDuration < TimeSpan.Zero
            || input.StableSince > input.ObservedAt
            || input.ObservedAt > input.Now)
            return Deny(LightGateReasonCode.InvalidTime);

        var sampleAge = input.Now - input.ObservedAt;
        var stableDuration = input.Now - input.StableSince;
        if (sampleAge > input.MaxSampleAge)
            return Deny(LightGateReasonCode.StaleSample, sampleAge, stableDuration);

        if (string.IsNullOrWhiteSpace(input.ProfileRevision)
            || string.IsNullOrWhiteSpace(input.ExpectedProfileRevision)
            || !string.Equals(input.ProfileRevision, input.ExpectedProfileRevision, StringComparison.Ordinal))
            return Deny(LightGateReasonCode.ProfileChanged, sampleAge, stableDuration);

        switch (input.Classification.Kind)
        {
            case LightStateClassificationKind.Unknown:
                return Deny(LightGateReasonCode.UnknownState, sampleAge, stableDuration);
            case LightStateClassificationKind.Invalid:
                return Deny(LightGateReasonCode.InvalidState, sampleAge, stableDuration);
            case LightStateClassificationKind.Ambiguous:
                return Deny(LightGateReasonCode.AmbiguousState, sampleAge, stableDuration);
            case LightStateClassificationKind.Candidate:
                return Deny(LightGateReasonCode.CandidateOnly, sampleAge, stableDuration);
            case LightStateClassificationKind.Stable:
                break;
            default:
                return Deny(LightGateReasonCode.UnsupportedState, sampleAge, stableDuration);
        }

        if (stableDuration < input.RequiredStableDuration)
            return Deny(LightGateReasonCode.NotStableLongEnough, sampleAge, stableDuration);

        var profileId = input.Classification.Profile?.Id;
        if (!MatchesIntent(input.Intent, profileId))
            return Deny(LightGateReasonCode.IntentMismatch, sampleAge, stableDuration);

        return new LightGateResult(
            LightGateDecisionKind.Eligible,
            LightGateReasonCode.EligibleStableMatch,
            input.Now,
            input.WatchSessionId,
            input.ProfileRevision,
            sampleAge,
            stableDuration,
            input.Intent);
    }

    private static bool MatchesIntent(LightExecutionIntent intent, string? profileId)
        => intent switch
        {
            LightExecutionIntent.Desktop => profileId == "desktop",
            LightExecutionIntent.Login or LightExecutionIntent.Disconnect => profileId == "login-or-dc",
            LightExecutionIntent.CharacterDashboard => profileId == "character-dashboard",
            LightExecutionIntent.EnteringGameLoading => profileId == "entering-game-loading",
            LightExecutionIntent.Game => profileId == "game",
            LightExecutionIntent.Targeted => profileId is "targeted" or "targeted-repeat",
            _ => false,
        };
}
