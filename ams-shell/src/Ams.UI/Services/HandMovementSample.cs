using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Ams.UI.Services;

/// <summary>Captures only cursor deltas/timing. No keyboard, button, window title, text or secret data is observed.</summary>
public static class HandMovementSample
{
    public const int CaptureDurationMs = 10_000;
    public const int ProfileCaptureDurationMs = 30_000;
    // 8 ms stays below the safe ~125-command/s UART replay budget while retaining
    // the small 1-5 px motion seen in the user's high-rate reference recordings.
    public const int CaptureIntervalMs = 8;
    // Ten seconds at the 8 ms capture interval is at most about 1250 samples.
    // Keep those samples instead of merging them into 48 large cursor jumps.
    // The Pico exporter stores the path as one compact HANDPATH command, so this
    // no longer creates hundreds of parsed route-command objects on RP2040.
    public const int ReplaySegmentLimit = 1280;
    public readonly record struct Segment(int DelayMs, int Dx, int Dy);
    public sealed record Sample(int DurationMs, Point Start, Point End, IReadOnlyList<Segment> Segments);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    // The v1 payload is intentionally limited to cursor coordinates, relative deltas, and timing.
    public static async Task<Sample?> CaptureAsync(CancellationToken ct = default)
        => await CaptureAsync(CaptureDurationMs, ct);

    public static async Task<Sample?> CaptureAsync(int durationMs,
        CancellationToken ct = default)
    {
        durationMs = Math.Clamp(durationMs, 1_000, 60_000);
        // Task.Delay(8) otherwise resolves to about 15.6 ms on many Windows
        // systems. Scope the 1 ms multimedia timer request to this ten-second
        // capture only; the finally block also covers cancellation/errors.
        uint timerResult = timeBeginPeriod(1);
        try
        {
            var start = System.Windows.Forms.Cursor.Position;
            var previous = start;
            var lastChangeMs = 0;
            var segments = new List<Segment>();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < durationMs)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(CaptureIntervalMs, ct);
                var now = System.Windows.Forms.Cursor.Position;
                int elapsed = (int)Math.Min(durationMs, sw.ElapsedMilliseconds);
                int dx = now.X - previous.X, dy = now.Y - previous.Y;
                if (dx != 0 || dy != 0)
                {
                    segments.Add(new Segment(Math.Max(1, elapsed - lastChangeMs), dx, dy));
                    previous = now;
                    lastChangeMs = elapsed;
                }
            }
            if (segments.Count == 0) return null;
            int trailing = Math.Max(1, durationMs - lastChangeMs);
            segments.Add(new Segment(trailing, 0, 0));
            return new Sample(durationMs, start, previous, segments.ToArray());
        }
        finally
        {
            if (timerResult == 0) timeEndPeriod(1);
        }
    }

    public static string Encode(Sample sample)
    {
        var parts = sample.Segments.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.DelayMs},{s.Dx},{s.Dy}"));
        return string.Create(CultureInfo.InvariantCulture,
            $"v1|{sample.DurationMs}|{sample.Start.X},{sample.Start.Y}|{sample.End.X},{sample.End.Y}|{string.Join(';', parts)}");
    }

    public static bool TryDecode(string? text, out Sample sample)
    {
        sample = new Sample(0, default, default, Array.Empty<Segment>());
        if (string.IsNullOrWhiteSpace(text)) return false;
        var fields = text.Split('|');
        if (fields.Length != 5 || fields[0] != "v1" || !int.TryParse(fields[1], out int duration)
            || duration is < 1 or > 60_000 || !TryPoint(fields[2], out var start) || !TryPoint(fields[3], out var end)) return false;
        var segments = new List<Segment>();
        foreach (var token in fields[4].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = token.Split(',');
            if (p.Length != 3 || !int.TryParse(p[0], out int dt) || !int.TryParse(p[1], out int dx) || !int.TryParse(p[2], out int dy)
                || dt is < 1 or > 60_000 || Math.Abs(dx) > 8192 || Math.Abs(dy) > 8192) return false;
            segments.Add(new Segment(dt, dx, dy));
        }
        if (segments.Count == 0 || segments.Count > 4096) return false;
        sample = new Sample(duration, start, end, segments);
        return true;
    }

    public static IReadOnlyList<Segment> Compact(IReadOnlyList<Segment> source, int maxSegments)
    {
        if (source.Count <= maxSegments) return source.ToArray();
        var result = new List<Segment>(maxSegments);
        for (int bucket = 0; bucket < maxSegments; bucket++)
        {
            int lo = bucket * source.Count / maxSegments;
            int hi = (bucket + 1) * source.Count / maxSegments;
            int dt = 0, dx = 0, dy = 0;
            for (int i = lo; i < hi; i++) { dt += source[i].DelayMs; dx += source[i].Dx; dy += source[i].Dy; }
            result.Add(new Segment(Math.Max(1, dt), dx, dy));
        }
        return result;
    }

    /// <summary>
    /// The recorded deltas are the authoritative payload. Start/End are capture metadata only;
    /// using their difference would let a malformed legacy payload describe a different move.
    /// </summary>
    public static Point Displacement(Sample sample)
    {
        int x = 0, y = 0;
        foreach (var segment in sample.Segments) { x += segment.Dx; y += segment.Dy; }
        return new Point(x, y);
    }

    /// <summary>
    /// Robust personal speed band for Random Mouse Position. The 20th/80th
    /// percentiles reject click pauses and isolated scheduling spikes while
    /// preserving the user's measured acceleration range.
    /// </summary>
    public static bool TryGetSpeedRange(Sample sample, out int minimum, out int maximum)
    {
        var speeds = new List<double>();
        foreach (var segment in sample.Segments)
        {
            if (segment.DelayMs < 4 || (segment.Dx == 0 && segment.Dy == 0)) continue;
            double distance = Math.Sqrt(segment.Dx * (double)segment.Dx + segment.Dy * (double)segment.Dy);
            speeds.Add(distance * 1000.0 / segment.DelayMs);
        }
        if (speeds.Count < 5) { minimum = maximum = 0; return false; }
        speeds.Sort();
        minimum = Math.Clamp((int)Math.Round(speeds[(speeds.Count - 1) * 20 / 100]), 150, 3000);
        maximum = Math.Clamp((int)Math.Round(speeds[(speeds.Count - 1) * 80 / 100]), minimum, 3000);
        return true;
    }

    private static bool TryPoint(string text, out Point point)
    {
        point = default; var p = text.Split(',');
        if (p.Length != 2 || !int.TryParse(p[0], out int x) || !int.TryParse(p[1], out int y)) return false;
        point = new Point(x, y); return true;
    }
}
