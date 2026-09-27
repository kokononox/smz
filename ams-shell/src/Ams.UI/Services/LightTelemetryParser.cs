using System.Globalization;

using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>Strict parser for the phase-one LUX? response contract.</summary>
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
            _ => (LightTelemetryStatus?)null,
        };
        if (errorStatus.HasValue)
            return new(errorStatus.Value, null, null, null, at, raw);

        var parts = raw.Split('|', StringSplitOptions.None);
        if (parts.Length is not (4 or 6) || parts[0] != "OK" || parts[1] != "LUX")
            throw new LightTelemetryProtocolException(raw, "Expected a supported OK|LUX response shape.");

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
                || !fields.TryGetValue("mode", out var parsedMode) || parsedMode is not ("hires" or "lowres"))
                throw new LightTelemetryProtocolException(raw, "Telemetry fields are invalid.");
            sequence = parsedSequence;
            mode = parsedMode;
        }
        else if (fields.Count != 2)
        {
            throw new LightTelemetryProtocolException(raw, "Telemetry fields are invalid.");
        }

        return new(LightTelemetryStatus.Ok, sequence, lux, mode, at, raw);
    }
}

public sealed class LightTelemetryProtocolException : FormatException
{
    public LightTelemetryProtocolException(string rawReply, string message)
        : base($"{message} Reply: {rawReply}") => RawReply = rawReply;

    public string RawReply { get; }
}using System.Globalization;

using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>Strict parser for the phase-one LUX? response contract.</summary>
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
            _ => (LightTelemetryStatus?)null,
        };
        if (errorStatus.HasValue)
            return new(errorStatus.Value, null, null, null, at, raw);

        var parts = raw.Split('|', StringSplitOptions.None);
        if (parts.Length != 6 || parts[0] != "OK" || parts[1] != "LUX")
            throw new LightTelemetryProtocolException(raw, "Expected the exact OK|LUX response shape.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < parts.Length; i++)
        {
            var separator = parts[i].IndexOf('=');
            if (separator <= 0 || separator == parts[i].Length - 1)
                throw new LightTelemetryProtocolException(raw, "Telemetry field is malformed.");
            if (!fields.TryAdd(parts[i][..separator], parts[i][(separator + 1)..]))
                throw new LightTelemetryProtocolException(raw, "Telemetry field is duplicated.");
        }

        if (fields.Count != 4 || !fields.TryGetValue("seq", out var seqText)
            || !uint.TryParse(seqText, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || !fields.TryGetValue("lux", out var luxText)
            || !double.TryParse(luxText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lux)
            || !double.IsFinite(lux) || lux < 0
            || !fields.TryGetValue("mode", out var mode) || mode is not ("hires" or "lowres")
            || !fields.TryGetValue("sensor", out var sensor) || sensor != "ok")
            throw new LightTelemetryProtocolException(raw, "Telemetry fields are invalid.");

        return new(LightTelemetryStatus.Ok, sequence, lux, mode, at, raw);
    }
}

public sealed class LightTelemetryProtocolException : FormatException
{
    public LightTelemetryProtocolException(string rawReply, string message)
        : base($"{message} Reply: {rawReply}") => RawReply = rawReply;

    public string RawReply { get; }
}
