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

        var mean = ProposeAxis(valid.Select(x => x.Mean),
            LightStateProfile.MeanLuxToleranceUnitsPerLux);
        var difference = ProposeAxis(valid.Select(x => x.Difference),
            LightStateProfile.DifferenceLuxToleranceUnitsPerLux);
        var sensor1 = ProposeAxis(valid.Select(x => x.Lux1));
        var sensor2 = ProposeAxis(valid.Select(x => x.Lux2));
        return new DualLightCalibrationSuggestion(
            mean.Center, mean.Tolerance, mean.Minimum, mean.Maximum,
            mean.MinimumTolerance,
            difference.Center, difference.Tolerance, difference.Minimum, difference.Maximum,
            difference.MinimumTolerance,
            sensor1.Center, sensor1.Minimum, sensor1.Maximum,
            sensor2.Center, sensor2.Minimum, sensor2.Maximum,
            valid.Length);
    }

    /// <summary>
    /// Keeps the recommended tolerance when it is safe. If it overlaps another
    /// calibrated profile, finds the widest proportionate fit along the preferred-to-
    /// minimum path without going below robust sample-coverage floors. The candidate
    /// is modified only.
    /// </summary>
    public static bool TryFitAutomaticTolerances(
        LightStateProfile candidate,
        IEnumerable<LightStateProfile> existingProfiles,
        DualLightCalibrationSuggestion suggestion,
        out DualLightProfileValidation validation,
        out bool reduced)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(existingProfiles);
        ArgumentNullException.ThrowIfNull(suggestion);
        var existing = existingProfiles.ToArray();
        candidate.MeanLuxTolerance = suggestion.MeanToleranceLux;
        candidate.DifferenceLuxTolerance = suggestion.DifferenceToleranceLux;
        validation = ValidateCandidate(candidate, existing);
        reduced = false;
        if (validation.IsValid) return true;

        var preferredMean = suggestion.MeanToleranceLux;
        var preferredDifference = suggestion.DifferenceToleranceLux;
        candidate.MeanLuxTolerance = suggestion.MeanMinimumToleranceLux;
        candidate.DifferenceLuxTolerance = suggestion.DifferenceMinimumToleranceLux;
        validation = ValidateCandidate(candidate, existing);
        if (!validation.IsValid) return false;

        var safeFraction = 0.0;
        var unsafeFraction = 1.0;
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var fraction = (safeFraction + unsafeFraction) / 2.0;
            candidate.MeanLuxTolerance = QuantizeUp(
                suggestion.MeanMinimumToleranceLux
                + (preferredMean - suggestion.MeanMinimumToleranceLux) * fraction,
                LightStateProfile.MeanLuxToleranceUnitsPerLux);
            candidate.DifferenceLuxTolerance = QuantizeUp(
                suggestion.DifferenceMinimumToleranceLux
                + (preferredDifference - suggestion.DifferenceMinimumToleranceLux) * fraction,
                LightStateProfile.DifferenceLuxToleranceUnitsPerLux);
            var trial = ValidateCandidate(candidate, existing);
            if (trial.IsValid) safeFraction = fraction;
            else unsafeFraction = fraction;
        }

        candidate.MeanLuxTolerance = QuantizeUp(
            suggestion.MeanMinimumToleranceLux
            + (preferredMean - suggestion.MeanMinimumToleranceLux) * safeFraction,
            LightStateProfile.MeanLuxToleranceUnitsPerLux);
        candidate.DifferenceLuxTolerance = QuantizeUp(
            suggestion.DifferenceMinimumToleranceLux
            + (preferredDifference - suggestion.DifferenceMinimumToleranceLux) * safeFraction,
            LightStateProfile.DifferenceLuxToleranceUnitsPerLux);
        validation = ValidateCandidate(candidate, existing);
        if (!validation.IsValid)
        {
            candidate.MeanLuxTolerance = suggestion.MeanMinimumToleranceLux;
            candidate.DifferenceLuxTolerance = suggestion.DifferenceMinimumToleranceLux;
            validation = ValidateCandidate(candidate, existing);
            if (!validation.IsValid) return false;
        }
        reduced = candidate.MeanLuxTolerance + 1e-9 < preferredMean
                  || candidate.DifferenceLuxTolerance + 1e-9 < preferredDifference;
        return true;
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

    private static (double Center, double Tolerance, double Minimum, double Maximum,
        double MinimumTolerance) ProposeAxis(IEnumerable<double> source, int unitsPerLux)
    {
        var values = source.OrderBy(value => value).ToArray();
        var middle = values.Length / 2;
        var center = values.Length % 2 == 0
            ? values[middle - 1] / 2.0 + values[middle] / 2.0
            : values[middle];
        var minimum = values[0];
        var maximum = values[^1];
        var deviations = values.Select(value => Math.Abs(value - center))
            .OrderBy(value => value).ToArray();
        var middleDeviation = deviations.Length / 2;
        var mad = deviations.Length % 2 == 0
            ? deviations[middleDeviation - 1] / 2.0 + deviations[middleDeviation] / 2.0
            : deviations[middleDeviation];
        var robustSigma = mad * 1.4826;
        // The minimum floor covers about 95% of normal sample variation; the
        // preferred value leaves a wider ~3-sigma envelope. MAD limits outlier
        // influence, and fixed-point quantization matches the board descriptor.
        var minimumTolerance = QuantizeUp(
            Math.Max(MinimumAxisToleranceLux, 2.0 * robustSigma + 0.1), unitsPerLux);
        var tolerance = QuantizeUp(
            Math.Max(minimumTolerance, 3.0 * robustSigma + 0.1), unitsPerLux);
        if (!double.IsFinite(center) || !double.IsFinite(tolerance)
            || !double.IsFinite(minimumTolerance) || tolerance > 1_000_000)
            throw new InvalidOperationException("نمونه‌های نور از محدودهٔ عددی معتبر خارج‌اند.");
        return (center, tolerance, minimum, maximum, minimumTolerance);
    }

    private static (double Center, double Minimum, double Maximum) ProposeAxis(
        IEnumerable<double> source)
    {
        var values = source.OrderBy(value => value).ToArray();
        var middle = values.Length / 2;
        var center = values.Length % 2 == 0
            ? values[middle - 1] / 2.0 + values[middle] / 2.0
            : values[middle];
        return (center, values[0], values[^1]);
    }

    private static double QuantizeUp(double value, int unitsPerLux)
        => Math.Ceiling(value * unitsPerLux - 1e-9) / unitsPerLux;

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
    double MeanMinimumToleranceLux,
    double DifferenceCenterLux,
    double DifferenceToleranceLux,
    double DifferenceMinimumLux,
    double DifferenceMaximumLux,
    double DifferenceMinimumToleranceLux,
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