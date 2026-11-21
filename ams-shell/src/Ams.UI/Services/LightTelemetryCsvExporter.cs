using System.Globalization;
using System.Text;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>Exports labeled, raw dual-channel telemetry in a stable UTF-8 CSV shape.</summary>
public static class LightTelemetryCsvExporter
{
    private static readonly string[] Header =
    [
        "captured_at_utc", "environment", "sensor1_lux", "sensor1_status", "sensor1_age_ms",
        "sensor2_lux", "sensor2_status", "sensor2_age_ms",
    ];

    public static string ToCsv(IEnumerable<LightTelemetryCaptureRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var output = new StringBuilder();
        output.AppendLine(string.Join(',', Header));
        foreach (var row in records)
        {
            ArgumentNullException.ThrowIfNull(row);
            output.Append(Csv(row.CapturedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))).Append(',')
                .Append(Csv(SafeText(row.EnvironmentLabel))).Append(',')
                .Append(Number(row.Lux1)).Append(',')
                .Append(Csv(SafeText(row.Sensor1Status))).Append(',')
                .Append(Integer(row.Age1Ms)).Append(',')
                .Append(Number(row.Lux2)).Append(',')
                .Append(Csv(SafeText(row.Sensor2Status))).Append(',')
                .Append(Integer(row.Age2Ms)).AppendLine();
        }
        return output.ToString();
    }

    private static string Number(double? value) => value?.ToString("0.################", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Integer(uint? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string SafeText(string? value)
    {
        var text = value ?? string.Empty;
        return text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + text
            : text;
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
