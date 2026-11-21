namespace Ams.UI.Models;

/// <summary>One un-smoothed paired light sample captured for a labeled environment.</summary>
public sealed record LightTelemetryCaptureRecord(
    string EnvironmentLabel,
    DateTimeOffset CapturedAt,
    double? Lux1,
    string Sensor1Status,
    uint? Age1Ms,
    double? Lux2,
    string Sensor2Status,
    uint? Age2Ms);
