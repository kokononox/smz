using Ams.UI.Models;

namespace Ams.UI.Services;

public enum LightStateClassificationKind
{
    Unknown,
    Invalid,
    Ambiguous,
    Candidate,
    Stable,
}

public sealed record LightStateClassification(
    LightStateClassificationKind Kind,
    double? Lux,
    LightStateProfile? Profile = null,
    double? Confidence = null,
    IReadOnlyList<LightStateProfile>? Matches = null,
    string? Diagnostic = null)
{
    public static LightStateClassification Unknown(double? lux = null, string? diagnostic = null)
        => new(LightStateClassificationKind.Unknown, lux, Diagnostic: diagnostic);
}

/// <summary>
/// Pure deterministic state machine for light profiles. Overlap is always explicit: profile order
/// never chooses a winner. StableDuration suppresses transients; Hysteresis only holds the already
/// stable state just outside its normal range.
/// </summary>
public sealed class LightStateClassifier
{
    private readonly IReadOnlyList<LightStateProfile> _profiles;
    private string? _candidateId;
    private DateTimeOffset? _candidateSince;
    private string? _stableId;

    public LightStateClassifier(IEnumerable<LightStateProfile> profiles)
        => _profiles = profiles?.ToArray() ?? throw new ArgumentNullException(nameof(profiles));

    public LightStateClassification Update(double? lux, DateTimeOffset at, bool sampleIsFresh = true)
    {
        if (_profiles.Any(p => p.Enabled && p.IsValid && p.HasDualSensorCalibration))
        {
            Reset();
            return LightStateClassification.Unknown(lux, "برای پروفایل دوحسگری، هر دو سنسور لازم‌اند.");
        }
        if (!sampleIsFresh)
        {
            Reset();
            return LightStateClassification.Unknown(lux);
        }
        if (lux is not double value || !double.IsFinite(value) || value < 0)
        {
            Reset();
            return new LightStateClassification(LightStateClassificationKind.Invalid, lux);
        }

        var enabled = _profiles.Where(p => p.Enabled && p.IsValid).ToArray();
        var matches = enabled.Where(p => value >= p.LuxMin && value <= p.LuxMax).ToArray();
        if (matches.Length > 1)
        {
            Reset();
            return new LightStateClassification(
                LightStateClassificationKind.Ambiguous, value, Matches: matches);
        }

        if (matches.Length == 1)
        {
            var match = matches[0];
            if (_stableId == match.Id)
                return Stable(value, match);

            if (_candidateId != match.Id || _candidateSince is null || at < _candidateSince)
            {
                _candidateId = match.Id;
                _candidateSince = at;
                return Candidate(value, match);
            }

            if ((at - _candidateSince.Value).TotalMilliseconds < match.StableDurationMs)
                return Candidate(value, match);

            _stableId = match.Id;
            _candidateId = null;
            _candidateSince = null;
            return Stable(value, match);
        }

        var stable = enabled.FirstOrDefault(p => p.Id == _stableId);
        if (stable is not null
            && value >= Math.Max(0, stable.LuxMin - stable.HysteresisLux)
            && value <= stable.LuxMax + stable.HysteresisLux)
        {
            _candidateId = null;
            _candidateSince = null;
            return Stable(value, stable);
        }

        Reset();
        return LightStateClassification.Unknown(value);
    }

    /// <summary>
    /// Classifies a paired observation as (mean, signed contrast) while retaining the
    /// existing single-sensor behavior for entirely legacy profile sets.
    /// </summary>
    public LightStateClassification Update(
        double? lux1, double? lux2, DateTimeOffset at, bool sampleIsFresh = true)
    {
        if (!sampleIsFresh)
        {
            Reset();
            return LightStateClassification.Unknown(lux1);
        }

        var enabled = _profiles.Where(p => p.Enabled && p.IsValid).ToArray();
        var paired = enabled.Where(p => p.HasDualSensorCalibration).ToArray();
        if (paired.Length == 0)
            return Update(lux1, at, sampleIsFresh: true);

        if (paired.Length != enabled.Length)
        {
            Reset();
            return LightStateClassification.Unknown(
                lux1, "کالیبراسیون دوحسگری ناقص است؛ همهٔ پروفایل‌های فعال باید با دو سنسور کالیبره شوند.");
        }

        if (!IsValidLux(lux1) || !IsValidLux(lux2))
        {
            Reset();
            return lux1 is null || lux2 is null
                ? LightStateClassification.Unknown(lux1, "خوانش معتبر هر دو سنسور لازم است.")
                : new LightStateClassification(LightStateClassificationKind.Invalid, lux1);
        }

        var firstLux = lux1!.Value;
        var secondLux = lux2!.Value;
        var mean = firstLux / 2.0 + secondLux / 2.0;
        var difference = secondLux - firstLux;
        if (!double.IsFinite(mean) || !double.IsFinite(difference))
        {
            Reset();
            return new LightStateClassification(LightStateClassificationKind.Invalid, lux1);
        }

        var matches = paired
            .Where(profile => DualLightCalibration.ObservationScore(profile, mean, difference) <= 1.0)
            .ToArray();
        if (matches.Length > 1)
        {
            Reset();
            return new LightStateClassification(
                LightStateClassificationKind.Ambiguous, mean, Matches: matches);
        }

        if (matches.Length == 1)
        {
            var match = matches[0];
            var confidence = DualConfidence(mean, difference, match);
            if (_stableId == match.Id)
                return new LightStateClassification(LightStateClassificationKind.Stable, mean, match, confidence);

            if (_candidateId != match.Id || _candidateSince is null || at < _candidateSince)
            {
                _candidateId = match.Id;
                _candidateSince = at;
                return new LightStateClassification(LightStateClassificationKind.Candidate, mean, match, confidence);
            }

            if ((at - _candidateSince.Value).TotalMilliseconds < match.StableDurationMs)
                return new LightStateClassification(LightStateClassificationKind.Candidate, mean, match, confidence);

            _stableId = match.Id;
            _candidateId = null;
            _candidateSince = null;
            return new LightStateClassification(LightStateClassificationKind.Stable, mean, match, confidence);
        }

        var stable = paired.FirstOrDefault(p => p.Id == _stableId);
        if (stable is not null
            && DualLightCalibration.ObservationScoreWithHysteresis(stable, mean, difference) <= 1.0)
        {
            _candidateId = null;
            _candidateSince = null;
            return new LightStateClassification(
                LightStateClassificationKind.Stable, mean, stable, DualConfidence(mean, difference, stable));
        }

        Reset();
        return LightStateClassification.Unknown(mean);
    }

    public void Reset()
    {
        _candidateId = null;
        _candidateSince = null;
        _stableId = null;
    }

    private static LightStateClassification Candidate(double lux, LightStateProfile profile)
        => new(LightStateClassificationKind.Candidate, lux, profile, Confidence(lux, profile));

    private static LightStateClassification Stable(double lux, LightStateProfile profile)
        => new(LightStateClassificationKind.Stable, lux, profile, Confidence(lux, profile));

    private static double Confidence(double lux, LightStateProfile profile)
    {
        if (profile.LuxTolerance == 0) return lux == profile.LuxCenter ? 1 : 0;
        return Math.Clamp(1 - Math.Abs(lux - profile.LuxCenter) / profile.LuxTolerance, 0, 1);
    }

    private static double DualConfidence(double meanLux, double differenceLux, LightStateProfile profile)
    {
        var score = DualLightCalibration.ObservationScore(profile, meanLux, differenceLux);
        return double.IsFinite(score) ? Math.Clamp(1 - Math.Sqrt(Math.Max(0, score)), 0, 1) : 0;
    }

    private static bool IsValidLux(double? value)
        => value is double lux && double.IsFinite(lux) && lux >= 0;
}
