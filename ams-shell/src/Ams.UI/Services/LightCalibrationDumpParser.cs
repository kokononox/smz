using System.Globalization;

using Ams.UI.Models;

namespace Ams.UI.Services;

public sealed record LightCalibrationRange(byte ProfileId, uint LowTenths, uint HighTenths)
{
    public double CenterLux => (LowTenths + (double)HighTenths) / 20.0;
    public double ToleranceLux => (HighTenths - (double)LowTenths) / 20.0;
}

public sealed record LightCalibrationDump(
    uint Revision, byte Mask, IReadOnlyList<LightCalibrationRange> Profiles, string RawReply);

/// <summary>Strict parser and atomic importer for the native CALDUMP|LIGHT contract.</summary>
public static class LightCalibrationDumpParser
{
    private static readonly string[] ProfileIds =
    [
        "desktop", "login-or-dc", "character-dashboard", "entering-game-loading",
        "game", "targeted", "whisper", "whisper-repeat",
    ];

    public static LightCalibrationDump Parse(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var raw = reply.Trim();
        var parts = raw.Split('|', StringSplitOptions.None);
        if (parts.Length != 6 || parts[0] != "OK" || parts[1] != "CALDUMP" || parts[2] != "LIGHT")
            throw new LightCalibrationDumpProtocolException(raw, "Expected OK|CALDUMP|LIGHT response.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 3; i < parts.Length; i++)
        {
            var separator = parts[i].IndexOf('=');
            if (separator <= 0 || separator == parts[i].Length - 1
                || !fields.TryAdd(parts[i][..separator], parts[i][(separator + 1)..]))
                throw new LightCalibrationDumpProtocolException(raw, "Calibration dump field is malformed.");
        }

        if (fields.Count != 3
            || !fields.TryGetValue("revision", out var revisionText)
            || !uint.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || !fields.TryGetValue("mask", out var maskText)
            || maskText.Length != 2
            || !byte.TryParse(maskText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var mask)
            || !fields.TryGetValue("profiles", out var profilesText))
            throw new LightCalibrationDumpProtocolException(raw, "Calibration dump metadata is invalid.");

        var ranges = new List<LightCalibrationRange>();
        if (profilesText != "none")
        {
            foreach (var encoded in profilesText.Split(',', StringSplitOptions.None))
            {
                var values = encoded.Split(':', StringSplitOptions.None);
                if (values.Length != 3
                    || !byte.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || id is < 1 or > 8
                    || !uint.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out var low)
                    || !uint.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out var high)
                    || low > high || high > 1_000_000u
                    || ranges.Any(x => x.ProfileId == id))
                    throw new LightCalibrationDumpProtocolException(raw, "Calibration profile range is invalid.");
                ranges.Add(new(id, low, high));
            }
        }

        var parsedMask = ranges.Aggregate(0, (value, range) => value | 1 << (range.ProfileId - 1));
        if (parsedMask != mask || (mask == 0 && profilesText != "none")
            || (mask != 0 && profilesText == "none"))
            throw new LightCalibrationDumpProtocolException(raw, "Calibration profile mask does not match the payload.");

        return new(revision, mask, ranges.OrderBy(x => x.ProfileId).ToArray(), raw);
    }

    /// <summary>Validates every target first, then changes only center/tolerance fields.</summary>
    public static int Apply(LightCalibrationDump dump, IList<LightStateProfile> targets)
    {
        ArgumentNullException.ThrowIfNull(dump);
        ArgumentNullException.ThrowIfNull(targets);
        var changes = dump.Profiles.Select(range =>
        {
            var expectedId = ProfileIds[range.ProfileId - 1];
            var target = targets.SingleOrDefault(x => x.Id == expectedId)
                ?? throw new InvalidOperationException($"پروفایل مقصد «{expectedId}» در برنامه پیدا نشد.");
            return (target, range);
        }).ToArray();

        foreach (var (target, range) in changes)
        {
            target.LuxCenter = range.CenterLux;
            target.LuxTolerance = range.ToleranceLux;
        }
        return changes.Length;
    }
}

public sealed class LightCalibrationDumpProtocolException : FormatException
{
    public LightCalibrationDumpProtocolException(string rawReply, string message)
        : base($"{message} Reply: {rawReply}") => RawReply = rawReply;

    public string RawReply { get; }
}