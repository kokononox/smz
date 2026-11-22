using System.Globalization;

using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>Strict parser for single- and dual-sensor light telemetry contracts.</summary>
public static class LightTelemetryParser
{
    public static LightTelemetrySample Parse(string reply, DateTimeOffset? receivedAt = null)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var raw = reply.Trim();
        var at = receivedAt ?? DateTimeOffset.UtcNow;

        var errorStatus = raw switch
        {
            "ERR|NOSENSOR|LUX" => LightTelemetryStatus.NoSensor,
            "ERR|I2C|LUX" => LightTelemetryStatus.I2cError,
            "ERR|BUSY|LUX" => LightTelemetryStatus.Busy,
            "ERR|NOSENSOR|LUX2|sensor1" => LightTelemetryStatus.NoSensor,
            "ERR|I2C|LUX2" => LightTelemetryStatus.I2cError,
            "ERR|BUSY|LUX2" => LightTelemetryStatus.Busy,
            _ => (LightTelemetryStatus?)null,
        };
        if (errorStatus.HasValue)
            return new(errorStatus.Value, null, null, null, at, raw)
            {
                HasDualSensorData = raw.Contains("|LUX2", StringComparison.Ordinal),
            };

        var parts = raw.Split('|', StringSplitOptions.None);
        if (parts.Length >= 2 && parts[1] == "LUX2")
            return ParseDual(parts, raw, at);
        if (parts.Length is not (4 or 5 or 6) || parts[0] != "OK" || parts[1] != "LUX")
            throw new LightTelemetryProtocolException(raw, "Expected a supported OK|LUX or OK|LUX2 response shape.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < parts.Length; i++)
        {
            var separator = parts[i].IndexOf('=');
            if (separator <= 0 || separator == parts[i].Length - 1)
                throw new LightTelemetryProtocolException(raw, "Telemetry field is malformed.");
            if (!fields.TryAdd(parts[i][..separator], parts[i][(separator + 1)..]))
                throw new LightTelemetryProtocolException(raw, "Telemetry field is duplicated.");
        }

        if (!fields.TryGetValue("lux", out var luxText)
            || !double.TryParse(luxText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lux)
            || !double.IsFinite(lux) || lux < 0
            || !fields.TryGetValue("sensor", out var sensor) || sensor != "ok")
            throw new LightTelemetryProtocolException(raw, "Telemetry fields are invalid.");

        uint? sequence = null;
        string? mode = null;
        if (fields.Count == 4)
        {
            if (!fields.TryGetValue("seq", out var seqText)
                || !uint.TryParse(seqText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSequence)
                || !fields.TryGetValue("mode", out var parsedMode)
                || parsedMode is not ("hires" or "lowres"))
                throw new LightTelemetryProtocolException(raw, "Telemetry fields are invalid.");
            sequence = parsedSequence;
            mode = parsedMode;
        }
        else if (fields.Count == 3)
        {
            if (!fields.TryGetValue("age", out var ageText)
                || !uint.TryParse(ageText, NumberStyles.None,
                    CultureInfo.InvariantCulture, out _))
                throw new LightTelemetryProtocolException(raw, "Telemetry fields are invalid.");
        }
        else if (fields.Count != 2)
        {
            throw new LightTelemetryProtocolException(raw, "Telemetry fields are invalid.");
        }

        return new(LightTelemetryStatus.Ok, sequence, lux, mode, at, raw);
    }

    private static LightTelemetrySample ParseDual(
        string[] parts,
        string raw,
        DateTimeOffset at)
    {
        if (parts[0] != "OK" || parts.Length is not (6 or 8))
            throw new LightTelemetryProtocolException(raw, "Expected a supported OK|LUX2 response shape.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < parts.Length; i++)
        {
            var separator = parts[i].IndexOf('=');
            if (separator <= 0 || separator == parts[i].Length - 1)
                throw new LightTelemetryProtocolException(raw, "Telemetry field is malformed.");
            if (!fields.TryAdd(parts[i][..separator], parts[i][(separator + 1)..]))
                throw new LightTelemetryProtocolException(raw, "Telemetry field is duplicated.");
        }

        if (!fields.TryGetValue("lux1", out var lux1Text)
            || !double.TryParse(lux1Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lux1)
            || !double.IsFinite(lux1) || lux1 < 0
            || !fields.TryGetValue("sensor1", out var sensor1) || sensor1 != "ok"
            || !fields.TryGetValue("age1", out var age1Text)
            || !uint.TryParse(age1Text, NumberStyles.None, CultureInfo.InvariantCulture, out var age1)
            || !fields.TryGetValue("sensor2", out var sensor2)
            || sensor2 is not ("ok" or "warming" or "missing"))
            throw new LightTelemetryProtocolException(raw, "Dual telemetry fields are invalid.");

        double? lux2 = null;
        uint? age2 = null;
        if (sensor2 == "ok")
        {
            if (fields.Count != 6
                || !fields.TryGetValue("lux2", out var lux2Text)
                || !double.TryParse(lux2Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLux2)
                || !double.IsFinite(parsedLux2) || parsedLux2 < 0
                || !fields.TryGetValue("age2", out var age2Text)
                || !uint.TryParse(age2Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedAge2))
                throw new LightTelemetryProtocolException(raw, "Dual telemetry fields are invalid.");
            lux2 = parsedLux2;
            age2 = parsedAge2;
        }
        else if (fields.Count != 4 || fields.ContainsKey("lux2") || fields.ContainsKey("age2"))
        {
            throw new LightTelemetryProtocolException(raw, "Dual telemetry fields are invalid.");
        }

        return new(LightTelemetryStatus.Ok, null, lux1, null, at, raw)
        {
            HasDualSensorData = true,
            Lux2 = lux2,
            Age1Ms = age1,
            Age2Ms = age2,
            Sensor2Status = sensor2,
        };
    }
}

public sealed class LightTelemetryProtocolException : FormatException
{
    public LightTelemetryProtocolException(string rawReply, string message)
        : base($"{message} Reply: {rawReply}") => RawReply = rawReply;

    public string RawReply { get; }
}
