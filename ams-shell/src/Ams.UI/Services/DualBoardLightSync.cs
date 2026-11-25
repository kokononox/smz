using System.Globalization;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>
/// Versioned round-trip for the board's paired M/D calibration. Mean values use
/// half-tenths of Lux; signed differences use tenths of Lux.
/// </summary>
public sealed record DualBoardLightProfile(
    byte ProfileId,
    uint MeanCenterHalfTenths,
    uint MeanToleranceHalfTenths,
    int DifferenceCenterTenths,
    uint DifferenceToleranceTenths);

public sealed record DualBoardLightSnapshot(
    uint Revision,
    IReadOnlyList<DualBoardLightProfile> Profiles);

public static class DualBoardLightSync
{
    private const uint MaximumMeanValueHalfTenths = 20_000_000;
    private const int MaximumDifferenceTenths = 10_000_000;
    private static readonly string[] ProfileIds = BoardLightSync.Ids;

    public static DualBoardLightSnapshot Parse(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var parts = reply.Trim().Split('|', StringSplitOptions.None);
        if (parts.Length != 5 || parts[0] != "OK" || parts[1] != "CALSYNC"
            || parts[2] != "DUAL")
            throw new FormatException("پاسخ همگام‌سازی دوحسگری نامعتبر است.");

        var revisionText = Field(parts[3], "revision");
        if (!uint.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture,
                out var revision))
            throw new FormatException("شمارهٔ بازبینی کالیبراسیون نامعتبر است.");

        var encodedProfiles = Field(parts[4], "profiles");
        var profiles = new List<DualBoardLightProfile>(ProfileIds.Length);
        foreach (var encoded in encodedProfiles.Split(',', StringSplitOptions.None))
        {
            var values = encoded.Split(':', StringSplitOptions.None);
            if (values.Length != 5
                || !byte.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture,
                    out var id)
                || id is < 1 or > 9
                || !uint.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture,
                    out var mean)
                || !uint.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture,
                    out var meanTolerance)
                || !int.TryParse(values[3], NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var difference)
                || !uint.TryParse(values[4], NumberStyles.None, CultureInfo.InvariantCulture,
                    out var differenceTolerance)
                || mean > MaximumMeanValueHalfTenths
                || meanTolerance is 0 or > MaximumMeanValueHalfTenths
                || difference is < -MaximumDifferenceTenths or > MaximumDifferenceTenths
                || differenceTolerance is 0 or > MaximumDifferenceTenths
                || profiles.Any(profile => profile.ProfileId == id))
                throw new FormatException("یکی از رکوردهای کالیبراسیون دوحسگری نامعتبر است.");
            profiles.Add(new DualBoardLightProfile(id, mean, meanTolerance,
                difference, differenceTolerance));
        }

        if (profiles.Count != ProfileIds.Length
            || !profiles.Select(profile => profile.ProfileId).Order()
                .SequenceEqual(Enumerable.Range(1, ProfileIds.Length).Select(id => (byte)id)))
            throw new FormatException("همگام‌سازی دوحسگری باید دقیقاً ۹ پروفایل کامل داشته باشد.");

        return new DualBoardLightSnapshot(revision,
            profiles.OrderBy(profile => profile.ProfileId).ToArray());
    }

    /// <summary>Builds a center sync while requiring the board's UF2 tolerances to match.</summary>
    public static string BuildCommand(IList<LightStateProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var ordered = ProfileIds.Select(id =>
        {
            var profile = profiles.SingleOrDefault(candidate => candidate.Id == id)
                ?? throw new InvalidOperationException($"پروفایل «{id}» پیدا نشد.");
            if (!profile.Enabled || !profile.HasDualSensorCalibration || !profile.IsValid)
                throw new InvalidOperationException(
                    "برای همگام‌سازی Native دوحسگری، هر ۹ پروفایل باید فعال و جفت‌کالیبره باشند.");
            return (Profile: profile,
                Mean: ToUnits(profile.MeanLuxCenter, 20, MaximumMeanValueHalfTenths,
                    "مرکز میانگین"),
                MeanTolerance: ToUnits(profile.MeanLuxTolerance, 20,
                    MaximumMeanValueHalfTenths, "تلورانس میانگین", positive: true),
                Difference: ToSignedUnits(profile.DifferenceLuxCenter, 10,
                    MaximumDifferenceTenths, "مرکز اختلاف"),
                DifferenceTolerance: ToUnits(profile.DifferenceLuxTolerance, 10,
                    MaximumDifferenceTenths, "تلورانس اختلاف", positive: true));
        }).ToArray();

        var separation = DualLightCalibration.Validate(ordered.Select(item => item.Profile));
        if (!separation.IsValid)
            throw new InvalidOperationException(DualLightCalibration.DescribeConflicts(separation));

        var rows = ordered.Select((item, index) =>
            $"{index + 1}:{item.Mean}:{item.MeanTolerance}:{item.Difference}:{item.DifferenceTolerance}");
        var command = "CALSYNC|DUAL|SET|profiles=" + string.Join(",", rows);
        if (command.Length >= 512)
            throw new InvalidOperationException("دادهٔ همگام‌سازی از ظرفیت فرمان برد بزرگ‌تر است.");
        return command;
    }

    /// <summary>Applies a complete board snapshot atomically, without inventing L1/L2 samples.</summary>
    public static int Apply(DualBoardLightSnapshot snapshot, IList<LightStateProfile> targets)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(targets);
        if (snapshot.Profiles.Count != ProfileIds.Length
            || snapshot.Profiles.Any(profile => profile.ProfileId is < 1 or > 9
                || profile.MeanCenterHalfTenths > MaximumMeanValueHalfTenths
                || profile.MeanToleranceHalfTenths is 0 or > MaximumMeanValueHalfTenths
                || profile.DifferenceCenterTenths is < -MaximumDifferenceTenths
                    or > MaximumDifferenceTenths
                || profile.DifferenceToleranceTenths is 0 or > MaximumDifferenceTenths)
            || snapshot.Profiles.Select(profile => profile.ProfileId).Distinct().Count()
                != ProfileIds.Length)
            throw new FormatException("دادهٔ برد برای هر ۹ پروفایل کامل نیست.");

        var targetById = ProfileIds.ToDictionary(id => id, id =>
            targets.SingleOrDefault(profile => profile.Id == id)
            ?? throw new InvalidOperationException($"پروفایل مقصد «{id}» در برنامه پیدا نشد."),
            StringComparer.Ordinal);
        var staged = new Dictionary<string, LightStateProfile>(StringComparer.Ordinal);
        foreach (var item in snapshot.Profiles.OrderBy(profile => profile.ProfileId))
        {
            var profileId = ProfileIds[item.ProfileId - 1];
            var candidate = targetById[profileId].Copy();
            candidate.HasDualSensorCalibration = true;
            candidate.DualSensorCalibrationRequired = true;
            candidate.MeanLuxCenter = item.MeanCenterHalfTenths / 20.0;
            candidate.MeanLuxTolerance = item.MeanToleranceHalfTenths / 20.0;
            candidate.DifferenceLuxCenter = item.DifferenceCenterTenths / 10.0;
            candidate.DifferenceLuxTolerance = item.DifferenceToleranceTenths / 10.0;
            // Raw L1/L2 min/max values are not stored on the board. Clear any old
            // Studio-only capture summary rather than presenting it as board data.
            candidate.Sensor1LuxCenter = null;
            candidate.Sensor1LuxMinimum = null;
            candidate.Sensor1LuxMaximum = null;
            candidate.Sensor2LuxCenter = null;
            candidate.Sensor2LuxMinimum = null;
            candidate.Sensor2LuxMaximum = null;
            candidate.DualSensorSampleCount = null;
            if (!candidate.IsValid)
                throw new FormatException($"پروفایل «{profileId}» روی برد معتبر نیست.");
            staged.Add(profileId, candidate);
        }

        var separation = DualLightCalibration.Validate(staged.Values);
        if (!separation.IsValid)
            throw new InvalidOperationException(
                "پروفایل‌های دوحسگری روی برد حاشیهٔ جدایی امن ندارند: "
                + DualLightCalibration.DescribeConflicts(separation));

        foreach (var (id, candidate) in staged)
        {
            var target = targetById[id];
            target.HasDualSensorCalibration = candidate.HasDualSensorCalibration;
            target.DualSensorCalibrationRequired = candidate.DualSensorCalibrationRequired;
            target.MeanLuxCenter = candidate.MeanLuxCenter;
            target.MeanLuxTolerance = candidate.MeanLuxTolerance;
            target.DifferenceLuxCenter = candidate.DifferenceLuxCenter;
            target.DifferenceLuxTolerance = candidate.DifferenceLuxTolerance;
            target.Sensor1LuxCenter = null;
            target.Sensor1LuxMinimum = null;
            target.Sensor1LuxMaximum = null;
            target.Sensor2LuxCenter = null;
            target.Sensor2LuxMinimum = null;
            target.Sensor2LuxMaximum = null;
            target.DualSensorSampleCount = null;
        }
        return staged.Count;
    }

    public static bool Matches(DualBoardLightSnapshot snapshot,
        IList<LightStateProfile> profiles)
    {
        try
        {
            var command = BuildCommand(profiles);
            var expected = command["CALSYNC|DUAL|SET|profiles=".Length..]
                .Split(',', StringSplitOptions.None);
            var actual = snapshot.Profiles.Select(profile =>
                $"{profile.ProfileId}:{profile.MeanCenterHalfTenths}:"
                + $"{profile.MeanToleranceHalfTenths}:{profile.DifferenceCenterTenths}:"
                + $"{profile.DifferenceToleranceTenths}");
            return expected.SequenceEqual(actual, StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string Field(string part, string name)
    {
        var prefix = name + "=";
        if (!part.StartsWith(prefix, StringComparison.Ordinal)
            || part.Length == prefix.Length)
            throw new FormatException($"فیلد {name} در پاسخ برد وجود ندارد.");
        return part[prefix.Length..];
    }

    private static uint ToUnits(double value, int scale, uint maximum, string label,
        bool positive = false)
    {
        if (!double.IsFinite(value) || value < 0 || (positive && value <= 0))
            throw new InvalidOperationException($"{label} خارج از محدوده است.");
        var scaled = value * scale;
        var rounded = Math.Round(scaled, MidpointRounding.AwayFromZero);
        if (Math.Abs(scaled - rounded) > 1e-6 || rounded > maximum
            || (positive && rounded < 1))
            throw new InvalidOperationException($"{label} با دقت پروتکل برد قابل نمایش نیست.");
        return checked((uint)rounded);
    }

    private static int ToSignedUnits(double value, int scale, int maximum, string label)
    {
        if (!double.IsFinite(value))
            throw new InvalidOperationException($"{label} خارج از محدوده است.");
        var scaled = value * scale;
        var rounded = Math.Round(scaled, MidpointRounding.AwayFromZero);
        if (Math.Abs(scaled - rounded) > 1e-6 || rounded < -maximum || rounded > maximum)
            throw new InvalidOperationException($"{label} با دقت پروتکل برد قابل نمایش نیست.");
        return checked((int)rounded);
    }
}