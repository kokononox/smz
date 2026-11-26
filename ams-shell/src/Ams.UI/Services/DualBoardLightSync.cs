using System.Globalization;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>
/// Round-trip for the board's paired M/D centers and per-profile tolerances.
/// Mean values use half-tenths of Lux; signed differences use tenths of Lux.
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
        var rows = encodedProfiles == "none"
            ? Array.Empty<string>()
            : encodedProfiles.Split(',', StringSplitOptions.None);
        foreach (var encoded in rows)
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

        return new DualBoardLightSnapshot(revision,
            profiles.OrderBy(profile => profile.ProfileId).ToArray());
    }

    /// <summary>Builds a paired center-and-tolerance sync for the selected profiles.</summary>
    public static string BuildCommand(IList<LightStateProfile> profiles)
        => BuildCommand(profiles, ProfileIds);

    /// <summary>
    /// Builds a sparse center-and-tolerance sync for selected profiles. Unselected
    /// board profiles are left unchanged by the firmware.
    /// </summary>
    public static string BuildCommand(IList<LightStateProfile> profiles,
        IEnumerable<string> profileIds)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(profileIds);
        var requestedIds = profileIds.ToArray();
        if (requestedIds.Length == 0
            || requestedIds.Distinct(StringComparer.Ordinal).Count() != requestedIds.Length
            || requestedIds.Any(id => !ProfileIds.Contains(id, StringComparer.Ordinal)))
            throw new InvalidOperationException("یک یا چند شناسهٔ پروفایل برای همگام‌سازی نامعتبر است.");

        var requested = requestedIds.ToHashSet(StringComparer.Ordinal);
        var ordered = ProfileIds.Where(requested.Contains).Select(id =>
        {
            var profile = profiles.SingleOrDefault(candidate => candidate.Id == id)
                ?? throw new InvalidOperationException($"پروفایل «{id}» پیدا نشد.");
            if (!profile.Enabled || !profile.HasDualSensorCalibration || !profile.IsValid)
                throw new InvalidOperationException(
                    $"پروفایل «{profile.Name}» باید فعال و جفت‌کالیبره باشد.");
            if (!profile.HasValidDualAxisTolerances)
                throw new InvalidOperationException(
                    $"تلورانس‌های M و Δ در «{profile.Name}» باید دست‌کم "
                    + $"{LightStateProfile.MinimumDualAxisToleranceLux:0.#} لوکس باشند "
                    + "و با گام قابل پشتیبانی برد تنظیم شوند.");
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

        var separation = DualLightCalibration.Validate(profiles);
        if (!separation.IsValid)
            throw new InvalidOperationException(DualLightCalibration.DescribeConflicts(separation));

        var rows = ordered.Select(item =>
            $"{Array.IndexOf(ProfileIds, item.Profile.Id) + 1}:{item.Mean}:{item.MeanTolerance}:{item.Difference}:{item.DifferenceTolerance}");
        var command = "CALSYNC|DUAL|SET|profiles=" + string.Join(",", rows);
        if (command.Length >= 512)
            throw new InvalidOperationException("دادهٔ همگام‌سازی از ظرفیت فرمان برد بزرگ‌تر است.");
        return command;
    }

    /// <summary>
    /// Applies the board's calibrated rows atomically. Missing rows remain marked
    /// as requiring physical calibration; raw L1/L2 samples are never invented.
    /// </summary>
    public static int Apply(DualBoardLightSnapshot snapshot, IList<LightStateProfile> targets)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(targets);
        if (snapshot.Profiles.Count > ProfileIds.Length
            || snapshot.Profiles.Any(profile => profile.ProfileId is < 1 or > 9
                || profile.MeanCenterHalfTenths > MaximumMeanValueHalfTenths
                || profile.MeanToleranceHalfTenths is 0 or > MaximumMeanValueHalfTenths
                || profile.DifferenceCenterTenths is < -MaximumDifferenceTenths
                    or > MaximumDifferenceTenths
                || profile.DifferenceToleranceTenths is 0 or > MaximumDifferenceTenths)
            || snapshot.Profiles.Select(profile => profile.ProfileId).Distinct().Count()
                != snapshot.Profiles.Count)
            throw new FormatException("دادهٔ پروفایل‌های کالیبره‌شدهٔ برد نامعتبر است.");

        var targetById = ProfileIds.ToDictionary(id => id, id =>
            targets.SingleOrDefault(profile => profile.Id == id)
            ?? throw new InvalidOperationException($"پروفایل مقصد «{id}» در برنامه پیدا نشد."),
            StringComparer.Ordinal);
        var boardById = snapshot.Profiles.ToDictionary(
            profile => ProfileIds[profile.ProfileId - 1], StringComparer.Ordinal);
        var staged = new Dictionary<string, LightStateProfile>(StringComparer.Ordinal);
        foreach (var profileId in ProfileIds)
        {
            var candidate = targetById[profileId].Copy();
            candidate.HasDualSensorCalibration = boardById.TryGetValue(profileId, out var item);
            candidate.DualSensorCalibrationRequired = true;
            if (item is not null)
            {
                candidate.MeanLuxCenter = item.MeanCenterHalfTenths / 20.0;
                candidate.MeanLuxTolerance = item.MeanToleranceHalfTenths / 20.0;
                candidate.DifferenceLuxCenter = item.DifferenceCenterTenths / 10.0;
                candidate.DifferenceLuxTolerance = item.DifferenceToleranceTenths / 10.0;
            }
            else
            {
                candidate.MeanLuxCenter = 0;
                candidate.MeanLuxTolerance = 0;
                candidate.DifferenceLuxCenter = 0;
                candidate.DifferenceLuxTolerance = 0;
            }
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
        return snapshot.Profiles.Count;
    }

    /// <summary>
    /// Imports only one calibrated board profile. All other Studio profiles,
    /// including uncalibrated rows, remain untouched.
    /// </summary>
    public static bool ApplyProfile(DualBoardLightSnapshot snapshot,
        IList<LightStateProfile> targets, string profileId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var index = Array.IndexOf(ProfileIds, profileId);
        if (index < 0)
            throw new InvalidOperationException($"شناسهٔ پروفایل «{profileId}» برای وارد کردن نامعتبر است.");
        if (snapshot.Profiles.Any(profile => profile.ProfileId is < 1 or > 9
                || profile.MeanCenterHalfTenths > MaximumMeanValueHalfTenths
                || profile.MeanToleranceHalfTenths is 0 or > MaximumMeanValueHalfTenths
                || profile.DifferenceCenterTenths is < -MaximumDifferenceTenths
                    or > MaximumDifferenceTenths
                || profile.DifferenceToleranceTenths is 0 or > MaximumDifferenceTenths)
            || snapshot.Profiles.Select(profile => profile.ProfileId).Distinct().Count()
                != snapshot.Profiles.Count)
            throw new FormatException("دادهٔ پروفایل‌های دوحسگری برد نامعتبر است.");

        var boardProfile = snapshot.Profiles.SingleOrDefault(profile => profile.ProfileId == index + 1);
        if (boardProfile is null) return false;

        var target = targets.SingleOrDefault(profile => profile.Id == profileId)
            ?? throw new InvalidOperationException($"پروفایل مقصد «{profileId}» در برنامه پیدا نشد.");
        var candidate = target.Copy();
        candidate.HasDualSensorCalibration = true;
        candidate.DualSensorCalibrationRequired = true;
        candidate.MeanLuxCenter = boardProfile.MeanCenterHalfTenths / 20.0;
        candidate.MeanLuxTolerance = boardProfile.MeanToleranceHalfTenths / 20.0;
        candidate.DifferenceLuxCenter = boardProfile.DifferenceCenterTenths / 10.0;
        candidate.DifferenceLuxTolerance = boardProfile.DifferenceToleranceTenths / 10.0;
        candidate.Sensor1LuxCenter = null;
        candidate.Sensor1LuxMinimum = null;
        candidate.Sensor1LuxMaximum = null;
        candidate.Sensor2LuxCenter = null;
        candidate.Sensor2LuxMinimum = null;
        candidate.Sensor2LuxMaximum = null;
        candidate.DualSensorSampleCount = null;
        if (!candidate.IsValid)
            throw new FormatException($"پروفایل «{profileId}» روی برد معتبر نیست.");

        var staged = targets.Select(profile =>
            profile.Id == profileId ? candidate : profile.Copy()).ToArray();
        var separation = DualLightCalibration.Validate(
            staged.Where(profile => profile.HasDualSensorCalibration));
        if (!separation.IsValid)
            throw new InvalidOperationException(
                "پروفایل واردشده با کالیبراسیون‌های جفتی موجود جدایی امن ندارد: "
                + DualLightCalibration.DescribeConflicts(separation));

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
        return true;
    }

    public static bool Matches(DualBoardLightSnapshot snapshot,
        IList<LightStateProfile> profiles)
        => Matches(snapshot, profiles, ProfileIds);

    public static bool Matches(DualBoardLightSnapshot snapshot,
        IList<LightStateProfile> profiles, IEnumerable<string> profileIds)
    {
        try
        {
            var requestedIds = profileIds.ToArray();
            var command = BuildCommand(profiles, requestedIds);
            var expected = command["CALSYNC|DUAL|SET|profiles=".Length..]
                .Split(',', StringSplitOptions.None);
            var actual = snapshot.Profiles.ToDictionary(profile => profile.ProfileId, profile =>
                $"{profile.ProfileId}:{profile.MeanCenterHalfTenths}:"
                + $"{profile.MeanToleranceHalfTenths}:{profile.DifferenceCenterTenths}:"
                + $"{profile.DifferenceToleranceTenths}");
            return expected.All(row =>
            {
                var separator = row.IndexOf(':');
                return separator > 0
                       && byte.TryParse(row[..separator], NumberStyles.None,
                           CultureInfo.InvariantCulture, out var id)
                       && actual.TryGetValue(id, out var actualRow)
                       && string.Equals(row, actualRow, StringComparison.Ordinal);
            });
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