using System.Drawing;

namespace Ams.UI.Services;

/// <summary>
/// Formats the host cursor-origin hint used by the modern Pico Guard path.
/// The Pico cannot read the Windows cursor itself; it receives the current
/// absolute origin and applies it at an idle route boundary.
/// </summary>
public static class CursorOriginSync
{
    public static string Command(Point point) => $"CURSOR|{point.X},{point.Y}";

    public static bool IsAcknowledged(string? reply) =>
        string.Equals(reply, "OK|CURSOR", StringComparison.Ordinal)
        || (reply?.StartsWith("OK|CURSOR|", StringComparison.Ordinal) ?? false);
}