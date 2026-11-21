namespace Ams.UI.Models;

/// <summary>Typed result of one read-only LUX? request.</summary>
public sealed record LightTelemetrySample(
    LightTelemetryStatus Status,
    uint? Sequence,
    double? Lux,
    string? Mode,
    DateTimeOffset ReceivedAt,
    string RawReply)
{
    public bool IsSuccess => Status == LightTelemetryStatus.Ok;

    /// <summary>True when the firmware answered with the dual-sensor LUX2? contract.</summary>
    public bool HasDualSensorData { get; init; }

    public double? Lux2 { get; init; }
    public uint? Age1Ms { get; init; }
    public uint? Age2Ms { get; init; }
    public string? Sensor2Status { get; init; }
}

public enum LightTelemetryStatus
{
    Ok,
    NoSensor,
    I2cError,
    Busy,
}
