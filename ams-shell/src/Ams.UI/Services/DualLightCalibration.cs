using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>Two-sensor calibration proposal and conservative profile-separation rules.</summary>
public static class DualLightCalibration
{
    /// <summary>
    /// Required extra clearance between calibrated tolerance regions, in transformed Lux
    /// coordinates. The clearance is applied on both axes, after both profiles' tolerances.
    /// </summary>
    public const double MinimumInterProfileGapLux = 1.0;
    public const int MinimumSampleCount = 5;
    public const double MinimumAxisToleranceLux = 0.5;

    public static DualLightCalibrationSuggestion Suggest(
        IEnumerable<(double Lux1, double Lux2)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var valid = samples
            .Where(sample => double.IsFinite(sample.Lux1) && sample.Lux1 >= 0
                             && double.IsFinite(sample.Lux2) && sample.Lux2 >= 0)
            .Select(sample => (Mean: (sample.Lux1 + sample.Lux2) / 2.0,
                               Difference: sample.Lux2 - sample.Lux1))
            .Where(sample => double.IsFinite(sample.Mean) && double.IsFinite(sample.Difference))
            .ToArray();
        if (valid.Length < MinimumSampleCount)
            throw new ArgumentException(
                $"At least {MinimumSampleCount} valid paired samples are required.", nameof(samples));

        var mean = ProposeAxis(valid.Select(x => x.Mean));
        var difference = ProposeAxis(valid.Select(x => x.Difference));
        return new DualLightCalibrationSuggestion(
            mean.Center, mean.Tolerance, mean.Minimum, mean.Maximum,
            difference.Center, difference.Tolerance, difference.Minimum, difference.Maximum,
            valid.Length);
    }

    /// <summary>
    /// Checks all enabled paired profiles. The enlarged ellipse is a conservative
    /// superset of both tolerance ellipses plus the required Euclidean clearance, so
    /// passing this test guarantees the requested gap; borderline cases fail closed.
    /// </summary>
    public static DualLightProfileValidation Validate(IEnumerable<LightStateProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var enabled = profiles.Where(p => p is not null && p.Enabled && p.HasDualSensorCalibration)
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToArray();
        var conflicts = new List<DualLightProfileConflict>();
        for (var i = 0; i < enabled.Length; i++)
        for (var j = i + 1; j < enabled.Length; j++)
        {
            if (!HasRequiredClearance(enabled[i], enabled[j]))
                conflicts.Add(new DualLightProfileConflict(enabled[i], enabled[j]));
        }
        return new DualLightProfileValidation(conflicts);
    }

    /// <summary>Checks a pending profile as active against all other enabled paired profiles.</summary>
    public static DualLightProfileValidation ValidateCandidate(
        LightStateProfile candidate, IEnumerable<LightStateProfile> existingProfiles)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(existingProfiles);
        var activeCandidate = candidate.Copy();
        activeCandidate.Enabled = true;
        var profiles = existingProfiles
            .Where(profile => !string.Equals(profile.Id, candidate.Id, StringComparison.Ordinal))
            .Append(activeCandidate);
        return Validate(profiles);
    }

    /// <summary>Squared normalized distance for one paired observation; values ≤ 1 are in-profile.</summary>
    public static double ObservationScore(
        LightStateProfile profile, double meanLux, double differenceLux)
    {
        var meanScale = meanLux - profile.MeanLuxCenter;
        var differenceScale = differenceLux - profile.DifferenceLuxCenter;
        var meanRatio = meanScale / profile.MeanLuxTolerance;
        var differenceRatio = differenceScale / profile.DifferenceLuxTolerance;
        return meanRatio * meanRatio + differenceRatio * differenceRatio;
    }

    public static double ObservationScoreWithHysteresis(
        LightStateProfile profile, double meanLux, double differenceLux)
    {
        var meanTolerance = profile.MeanLuxTolerance + profile.HysteresisLux;
        var differenceTolerance = profile.DifferenceLuxTolerance + profile.HysteresisLux;
        var meanRatio = (meanLux - profile.MeanLuxCenter) / meanTolerance;
        var differenceRatio = (differenceLux - profile.DifferenceLuxCenter) / differenceTolerance;
        return meanRatio * meanRatio + differenceRatio * differenceRatio;
    }

    public static bool HasRequiredClearance(LightStateProfile first, LightStateProfile second)
    {
        if (!first.IsValid || !second.IsValid
            || !first.HasDualSensorCalibration || !second.HasDualSensorCalibration)
            return false;

        var meanRadius = first.MeanLuxTolerance + second.MeanLuxTolerance + MinimumInterProfileGapLux;
        var differenceRadius = first.DifferenceLuxTolerance + second.DifferenceLuxTolerance + MinimumInterProfileGapLux;
        if (!double.IsFinite(meanRadius) || !double.IsFinite(differenceRadius)
            || meanRadius <= 0 || differenceRadius <= 0)
            return false;

        var meanRatio = (first.MeanLuxCenter - second.MeanLuxCenter) / meanRadius;
        var differenceRatio = (first.DifferenceLuxCenter - second.DifferenceLuxCenter) / differenceRadius;
        var normalizedDistance = Hypot(meanRatio, differenceRatio);
        return double.IsFinite(normalizedDistance) && normalizedDistance > 1.0 + 1e-12;
    }

    public static string DescribeConflicts(DualLightProfileValidation validation)
    {
        if (validation.IsValid) return "کالیبراسیون‌های دوحسگری با حاشیهٔ امن جدا هستند.";
        var pairs = validation.Conflicts
            .Select(conflict => $"«{conflict.First.Name}» و «{conflict.Second.Name}»")
            .Distinct(StringComparer.Ordinal);
        return "ذخیره نشد: محدودهٔ تلورانس این محیط‌ها با حاشیهٔ امن ۱ لوکس در فضای دو‌بعدی جدا نیست: "
               + string.Join("؛ ", pairs)
               + ". سنسورها را جابه‌جا کنید یا نور محیط را تغییر دهید، سپس دوباره کالیبره کنید.";
    }

    private static (double Center, double Tolerance, double Minimum, double Maximum) ProposeAxis(
        IEnumerable<double> source)
    {
        var values = source.OrderBy(value => value).ToArray();
        var middle = values.Length / 2;
        var center = values.Length % 2 == 0
            ? values[middle - 1] / 2.0 + values[middle] / 2.0
            : values[middle];
        var minimum = values[0];
        var maximum = values[^1];
        var tolerance = Math.Max(MinimumAxisToleranceLux,
            Math.Ceiling((((maximum - minimum) / 2.0) + MinimumAxisToleranceLux) * 10.0) / 10.0);
        if (!double.IsFinite(center) || !double.IsFinite(tolerance))
            throw new InvalidOperationException("نمونه‌های نور از محدودهٔ عددی معتبر خارج‌اند.");
        return (center, tolerance, minimum, maximum);
    }

    private static double Hypot(double x, double y)
    {
        x = Math.Abs(x);
        y = Math.Abs(y);
        var max = Math.Max(x, y);
        if (max == 0) return 0;
        var min = Math.Min(x, y) / max;
        return max * Math.Sqrt(1 + min * min);
    }
}

public sealed record DualLightCalibrationSuggestion(
    double MeanCenterLux,
    double MeanToleranceLux,
    double MeanMinimumLux,
    double MeanMaximumLux,
    double DifferenceCenterLux,
    double DifferenceToleranceLux,
    double DifferenceMinimumLux,
    double DifferenceMaximumLux,
    int SampleCount);

public sealed record DualLightProfileConflict(
    LightStateProfile First,
    LightStateProfile Second);

public sealed record DualLightProfileValidation(
    IReadOnlyList<DualLightProfileConflict> Conflicts)
{
    public bool IsValid => Conflicts.Count == 0;
}