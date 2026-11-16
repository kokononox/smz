using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>
/// Lifecycle coordinator for read-only gate diagnostics. It owns only session/revision and
/// candidate/stable timing metadata. It never starts Watch, writes to a bridge, or invokes runtime.
/// </summary>
public sealed class ReadOnlyLightGateCoordinator
{
    private readonly Func<string> _sessionIdFactory;
    private readonly TimeSpan _maxSampleAge;
    private string? _activeSessionId;
    private string _profileRevision = string.Empty;
    private string? _candidateProfileId;
    private TimeSpan? _candidateSince;
    private string? _stableProfileId;
    private TimeSpan? _stableSince;

    public ReadOnlyLightGateCoordinator(
        TimeSpan? maxSampleAge = null,
        Func<string>? sessionIdFactory = null)
    {
        _maxSampleAge = maxSampleAge ?? TimeSpan.FromSeconds(2);
        if (_maxSampleAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxSampleAge));
        _sessionIdFactory = sessionIdFactory ?? (() => Guid.NewGuid().ToString("N"));
        LastResult = Denied(LightGateReasonCode.WatchSessionChanged, TimeSpan.Zero, string.Empty, string.Empty);
    }

    public string? ActiveSessionId => _activeSessionId;
    public string ProfileRevision => _profileRevision;
    public bool IsSessionActive => !string.IsNullOrWhiteSpace(_activeSessionId);
    public LightGateResult LastResult { get; private set; }

    public string StartSession(IEnumerable<LightStateProfile> profiles, TimeSpan now)
    {
        ValidateNow(now);
        var sessionId = _sessionIdFactory();
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException("Session ID factory returned an empty value.");

        _activeSessionId = sessionId;
        _profileRevision = ComputeProfileRevision(profiles);
        ResetTracking();
        LastResult = Denied(LightGateReasonCode.UnknownState, now, sessionId, _profileRevision);
        return sessionId;
    }

    public string RefreshProfiles(IEnumerable<LightStateProfile> profiles, TimeSpan now)
    {
        ValidateNow(now);
        _profileRevision = ComputeProfileRevision(profiles);
        ResetTracking();
        LastResult = Denied(
            LightGateReasonCode.ProfileChanged,
            now,
            _activeSessionId ?? string.Empty,
            _profileRevision);
        return _profileRevision;
    }

    public LightGateResult Stop(TimeSpan now)
    {
        ValidateNow(now);
        var previousSession = _activeSessionId ?? string.Empty;
        _activeSessionId = null;
        ResetTracking();
        LastResult = Denied(LightGateReasonCode.Disconnected, now, previousSession, _profileRevision);
        return LastResult;
    }

    public LightGateResult Observe(
        LightExecutionIntent intent,
        LightStateClassification classification,
        TimeSpan observedAt,
        TimeSpan now,
        string watchSessionId,
        string observationProfileRevision,
        bool connectionHealthy = true,
        bool cancellationRequested = false)
    {
        ArgumentNullException.ThrowIfNull(classification);
        var expectedSession = _activeSessionId ?? string.Empty;

        var canTrack = !string.IsNullOrWhiteSpace(expectedSession)
            && string.Equals(watchSessionId, expectedSession, StringComparison.Ordinal)
            && string.Equals(observationProfileRevision, _profileRevision, StringComparison.Ordinal)
            && observedAt >= TimeSpan.Zero
            && now >= observedAt;

        if (canTrack)
            TrackClassification(classification, observedAt);

        var stableSince = _stableSince ?? _candidateSince ?? observedAt;
        var requiredStable = TimeSpan.FromMilliseconds(
            Math.Max(0, classification.Profile?.StableDurationMs ?? 0));

        LastResult = LightExecutionGate.Evaluate(new LightGateInput(
            intent,
            classification,
            observedAt,
            stableSince,
            now,
            watchSessionId,
            expectedSession,
            observationProfileRevision,
            _profileRevision,
            connectionHealthy && IsSessionActive,
            cancellationRequested,
            _maxSampleAge,
            requiredStable));
        return LastResult;
    }

    private void TrackClassification(LightStateClassification classification, TimeSpan observedAt)
    {
        var profileId = classification.Profile?.Id;
        switch (classification.Kind)
        {
            case LightStateClassificationKind.Candidate when !string.IsNullOrWhiteSpace(profileId):
                if (!string.Equals(_candidateProfileId, profileId, StringComparison.Ordinal))
                {
                    _candidateProfileId = profileId;
                    _candidateSince = observedAt;
                }
                _stableProfileId = null;
                _stableSince = null;
                break;

            case LightStateClassificationKind.Stable when !string.IsNullOrWhiteSpace(profileId):
                if (!string.Equals(_stableProfileId, profileId, StringComparison.Ordinal))
                {
                    _stableProfileId = profileId;
                    _stableSince = string.Equals(_candidateProfileId, profileId, StringComparison.Ordinal)
                        ? _candidateSince ?? observedAt
                        : observedAt;
                }
                _candidateProfileId = null;
                _candidateSince = null;
                break;

            default:
                ResetTracking();
                break;
        }
    }

    private void ResetTracking()
    {
        _candidateProfileId = null;
        _candidateSince = null;
        _stableProfileId = null;
        _stableSince = null;
    }

    public static string ComputeProfileRevision(IEnumerable<LightStateProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var canonical = profiles
            .Select(profile => profile ?? throw new ArgumentException("Profiles cannot contain null.", nameof(profiles)))
            .OrderBy(profile => profile.Id, StringComparer.Ordinal)
            .Select(profile => new CanonicalProfile(
                profile.Id,
                profile.Name,
                profile.Enabled,
                profile.LuxCenter.ToString("R", CultureInfo.InvariantCulture),
                profile.LuxTolerance.ToString("R", CultureInfo.InvariantCulture),
                profile.LuxMin.ToString("R", CultureInfo.InvariantCulture),
                profile.LuxMax.ToString("R", CultureInfo.InvariantCulture),
                profile.StableDurationMs,
                profile.HysteresisLux.ToString("R", CultureInfo.InvariantCulture)))
            .ToArray();
        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static void ValidateNow(TimeSpan now)
    {
        if (now < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(now));
    }

    private static LightGateResult Denied(
        LightGateReasonCode reason,
        TimeSpan now,
        string sessionId,
        string revision)
        => new(LightGateDecisionKind.Denied, reason, now, sessionId, revision);

    private sealed record CanonicalProfile(
        string Id,
        string Name,
        bool Enabled,
        string LuxCenter,
        string LuxTolerance,
        string LuxMin,
        string LuxMax,
        int StableDurationMs,
        string HysteresisLux);
}
