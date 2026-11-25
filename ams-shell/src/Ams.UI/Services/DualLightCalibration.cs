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
    public const double MinimumAxisToleranceLux = LightStateProfile.MinimumDualAxisToleranceLux;
    private const int SeparationDirectionSteps = 4096;

    public static DualLightCalibrationSuggestion Suggest(
        IEnumerable<(double Lux1, double Lux2)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var valid = samples
            .Where(sample => double.IsFinite(sample.Lux1) && sample.Lux1 >= 0
                             && double.IsFinite(sample.Lux2) && sample.Lux2 >= 0)
            .Select(sample => (Lux1: sample.Lux1,
                               Lux2: sample.Lux2,
                               Mean: (sample.Lux1 + sample.Lux2) / 2.0,
                               Difference: sample.Lux2 - sample.Lux1))
            .Where(sample => double.IsFinite(sample.Mean) && double.IsFinite(sample.Difference))
            .ToArray();
        if (valid.Length < MinimumSampleCount)
            throw new ArgumentException(
                $"At least {MinimumSampleCount} valid paired samples are required.", nameof(samples));

        var mean = ProposeAxis(valid.Select(x => x.Mean));
        var difference = ProposeAxis(valid.Select(x => x.Difference));
        var sensor1 = ProposeAxis(valid.Select(x => x.Lux1));
        var sensor2 = ProposeAxis(valid.Select(x => x.Lux2));
        return new DualLightCalibrationSuggestion(
            mean.Center, mean.Tolerance, mean.Minimum, mean.Maximum,
            difference.Center, difference.Tolerance, difference.Minimum, difference.Maximum,
            sensor1.Center, sensor1.Minimum, sensor1.Maximum,
            sensor2.Center, sensor2.Minimum, sensor2.Maximum,
            valid.Length);
    }

    /// <summary>
    /// Checks all enabled paired profiles. A sampled separating direction is accepted
    /// only when the projected gap between the tolerance ellipses exceeds the required
    /// Euclidean clearance. This is a safe lower bound on their actual distance; angular
    /// sampling can reject a borderline-separated pair, but cannot approve an unsafe one.
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

        var centerMeanDelta = second.MeanLuxCenter - first.MeanLuxCenter;
        var centerDifferenceDelta = second.DifferenceLuxCenter - first.DifferenceLuxCenter;
        if (!double.IsFinite(centerMeanDelta) || !double.IsFinite(centerDifferenceDelta))
            return false;

        for (var i = 0; i < SeparationDirectionSteps; i++)
        {
            var angle = 2.0 * Math.PI * i / SeparationDirectionSteps;
            var meanAxis = Math.Cos(angle);
            var differenceAxis = Math.Sin(angle);
            var centerProjection = centerMeanDelta * meanAxis + centerDifferenceDelta * differenceAxis;
            var firstSupport = EllipseSupport(first, meanAxis, differenceAxis);
            var secondSupport = EllipseSupport(second, meanAxis, differenceAxis);
            var projectedGap = centerProjection - firstSupport - secondSupport;
            if (double.IsFinite(projectedGap)
                && projectedGap > MinimumInterProfileGapLux + 1e-9)
                return true;
        }
        return false;
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

    private static double EllipseSupport(LightStateProfile profile, double meanAxis, double differenceAxis)
    {
        var meanExtent = profile.MeanLuxTolerance * meanAxis;
        var differenceExtent = profile.DifferenceLuxTolerance * differenceAxis;
        return Math.Sqrt(meanExtent * meanExtent + differenceExtent * differenceExtent);
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
    double Sensor1CenterLux,
    double Sensor1MinimumLux,
    double Sensor1MaximumLux,
    double Sensor2CenterLux,
    double Sensor2MinimumLux,
    double Sensor2MaximumLux,
    int SampleCount);

public sealed record DualLightProfileConflict(
    LightStateProfile First,
    LightStateProfile Second);

public sealed record DualLightProfileValidation(
    IReadOnlyList<DualLightProfileConflict> Conflicts)
{
    public bool IsValid => Conflicts.Count == 0;
}