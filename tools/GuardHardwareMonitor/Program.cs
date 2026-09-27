using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GuardHardwareMonitor;

internal static partial class Program
{
    private const string Version = "2.0.0";
    private static readonly object ConsoleLock = new();
    private static readonly object LogLock = new();
    private static readonly CancellationTokenSource Stop = new();
    private static readonly SessionStats Stats = new();
    private static readonly ConcurrentQueue<string> RecentRaw = new();
    private static StreamWriter? _rawLog;
    private static StreamWriter? _jsonLog;
    private static bool _useColor = true;
    private static DateTimeOffset? _startWithoutStateAt;
    private static ProfileSet? _profiles;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Console.Title = $"GuardHardwareMonitor {Version}";

        var options = Options.Parse(args);
        _useColor = !options.NoColor && !Console.IsOutputRedirected;
        Directory.CreateDirectory(options.DiagnosticsDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var rawPath = Path.Combine(options.DiagnosticsDirectory, $"guard-hardware-{stamp}.log");
        var jsonPath = Path.Combine(options.DiagnosticsDirectory, $"guard-events-{stamp}.jsonl");
        var summaryPath = Path.Combine(options.DiagnosticsDirectory, $"guard-summary-{stamp}.txt");

        using var rawLog = new StreamWriter(rawPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        using var jsonLog = new StreamWriter(jsonPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        _rawLog = rawLog;
        _jsonLog = jsonLog;

        PrintBanner(rawPath, jsonPath);
        _profiles = ProfileSet.TryLoad(options.BundlePath);
        if (_profiles is not null)
            PrintProfiles(_profiles);
        else
            Info("PROFILE", "No Guard profile file supplied. Use --bundle <ZIP|folder|guard-calibration.json> for nearest-profile diagnostics.");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Stop.Cancel();
        };

        try
        {
            while (!Stop.IsCancellationRequested)
            {
                SerialPort? port = null;
                try
                {
                    port = await FindBrainAsync(options, Stop.Token);
                    if (port is null)
                    {
                        Warn("SCAN", $"Pico Brain not found. Retrying in {options.RescanSeconds}s.");
                        await Task.Delay(TimeSpan.FromSeconds(options.RescanSeconds), Stop.Token);
                        continue;
                    }

                    await MonitorAsync(port, options, Stop.Token);
                }
                catch (OperationCanceledException) when (Stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Error("LINK", $"{ex.GetType().Name}: {ex.Message}");
                    await DelayUnlessStopped(TimeSpan.FromSeconds(options.RescanSeconds));
                }
                finally
                {
                    if (port is not null)
                    {
                        try { port.Close(); } catch { }
                        port.Dispose();
                    }
                }
            }
        }
        finally
        {
            var summary = Stats.Render(_profiles);
            File.WriteAllText(summaryPath, summary, new UTF8Encoding(false));
            lock (ConsoleLock)
            {
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine(summary);
                Console.WriteLine($"SUMMARY|{Path.GetFullPath(summaryPath)}");
            }
        }

        return Stats.Failures > 0 ? 2 : 0;
    }

    private static void PrintBanner(string rawPath, string jsonPath)
    {
        lock (ConsoleLock)
        {
            SetColor(ConsoleColor.Cyan);
            Console.WriteLine($"GuardHardwareMonitor {Version} — Safe Portable Diagnostics");
            Console.ResetColor();
            Console.WriteLine($"RAW LOG : {Path.GetFullPath(rawPath)}");
            Console.WriteLine($"JSONL   : {Path.GetFullPath(jsonPath)}");
            Console.WriteLine("Safety  : only PING is sent automatically. No Guard/HID command is sent.");
            Console.WriteLine("Keys    : [S] summary  [P] ping  [R] reconnect  [Q] quit");
            Console.WriteLine();
        }
    }

    private static async Task<SerialPort?> FindBrainAsync(Options options, CancellationToken ct)
    {
        var ports = string.IsNullOrWhiteSpace(options.Port)
            ? SerialPort.GetPortNames().OrderBy(PortNumber).ToArray()
            : new[] { options.Port! };

        Raw($"PORTS|{string.Join(',', ports)}");
        if (ports.Length == 0)
            return null;

        foreach (var name in ports)
        {
            ct.ThrowIfCancellationRequested();
            SerialPort? candidate = null;
            try
            {
                candidate = CreatePort(name, options.Baud);
                candidate.Open();
                candidate.DiscardInBuffer();
                candidate.DiscardOutBuffer();
                await Task.Delay(120, ct);
                candidate.WriteLine("PING");
                Raw($"TX|{name}|PING");

                var deadline = DateTime.UtcNow.AddMilliseconds(options.ProbeTimeoutMs);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    try
                    {
                        var line = candidate.ReadLine().Trim();
                        if (line.Length == 0) continue;
                        Raw($"PROBE|{name}|{line}");
                        if (line.Contains("OK|PONG|", StringComparison.Ordinal) &&
                            line.Contains("role=brain", StringComparison.OrdinalIgnoreCase))
                        {
                            Success("PICO", $"connected {name} · {line}");
                            Stats.Port = name;
                            Stats.Firmware = line;
                            return candidate;
                        }
                    }
                    catch (TimeoutException) { }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                Warn("SCAN", $"{name}: {ex.Message}");
            }

            if (candidate is not null)
            {
                try { candidate.Close(); } catch { }
                candidate.Dispose();
            }
        }
        return null;
    }

    private static SerialPort CreatePort(string name, int baud) => new(name, baud)
    {
        NewLine = "\n",
        ReadTimeout = 180,
        WriteTimeout = 1000,
        DtrEnable = true,
        RtsEnable = false,
        Encoding = Encoding.UTF8
    };

    private static async Task MonitorAsync(SerialPort port, Options options, CancellationToken ct)
    {
        var nextPing = DateTime.UtcNow;
        var nextHeartbeat = DateTime.UtcNow.AddSeconds(5);
        var forceReconnect = false;

        while (!ct.IsCancellationRequested && port.IsOpen && !forceReconnect)
        {
            if (DateTime.UtcNow >= nextPing)
            {
                try
                {
                    port.WriteLine("PING");
                    Raw($"TX|PICO|PING");
                }
                catch (Exception ex)
                {
                    throw new IOException($"PING write failed on {port.PortName}", ex);
                }
                nextPing = DateTime.UtcNow.AddSeconds(options.PingSeconds);
            }

            try
            {
                var line = port.ReadLine().TrimEnd('\r', '\n');
                if (line.Length > 0)
                    HandleLine(line);
            }
            catch (TimeoutException) { }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                throw new IOException($"Read failed on {port.PortName}", ex);
            }

            if (_startWithoutStateAt is { } started &&
                DateTimeOffset.Now - started > TimeSpan.FromSeconds(4))
            {
                Warn("DIAG", "Start was accepted, but no stable STATE/ROUTE followed. The current lux likely matches no profile. Run with --bundle to see profile ranges, then recalibrate the active stage.");
                Stats.StartWithoutStateWarnings++;
                _startWithoutStateAt = null;
            }

            if (DateTime.UtcNow >= nextHeartbeat)
            {
                Info("HEARTBEAT", $"PICO open={port.IsOpen} verified={Stats.Verified} events={Stats.Events} state={Stats.LastState ?? "-"}");
                nextHeartbeat = DateTime.UtcNow.AddSeconds(5);
            }

            if (!Console.IsInputRedirected && Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true).Key;
                switch (key)
                {
                    case ConsoleKey.Q:
                        Stop.Cancel();
                        break;
                    case ConsoleKey.S:
                        lock (ConsoleLock) Console.WriteLine(Stats.Render(_profiles));
                        break;
                    case ConsoleKey.P:
                        port.WriteLine("PING");
                        Raw("TX|PICO|PING");
                        nextPing = DateTime.UtcNow.AddSeconds(options.PingSeconds);
                        break;
                    case ConsoleKey.R:
                        Warn("LINK", "Manual reconnect requested.");
                        forceReconnect = true;
                        break;
                }
            }

            await Task.Yield();
        }
    }

    private static void HandleLine(string line)
    {
        Stats.Verified |= line.Contains("OK|PONG|", StringComparison.Ordinal);
        Stats.Events++;
        Raw($"RX|PICO|{line}");
        EnqueueRecent(line);

        var payload = line;
        long? deviceTick = null;
        if (line.StartsWith("EVT|DEBUG|", StringComparison.Ordinal))
        {
            payload = line["EVT|DEBUG|".Length..];
            var slash = payload.IndexOf('/');
            if (slash > 0 && long.TryParse(payload[..slash], out var tick))
            {
                deviceTick = tick;
                payload = payload[(slash + 1)..];
            }
        }
        else if (line.StartsWith("EVT|", StringComparison.Ordinal))
        {
            payload = line["EVT|".Length..];
        }

        var category = Classify(payload);
        UpdateStats(category, payload);
        WriteJson(category, payload, line, deviceTick);

        switch (category)
        {
            case "FAIL":
                Error(category, payload);
                break;
            case "WARN":
            case "DENIED":
            case "UNKNOWN":
                Warn(category, EnrichState(payload));
                break;
            case "STATE":
            case "ROUTE":
            case "ARM":
                Success(category, EnrichState(payload));
                break;
            case "MEM":
                Info(category, payload);
                break;
            case "BUTTON":
            case "CAL":
            case "SOUND":
                Highlight(category, payload);
                break;
            default:
                Info(category, payload);
                break;
        }
    }

    private static string Classify(string payload)
    {
        if (payload.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("MemoryError", StringComparison.OrdinalIgnoreCase) ||
            payload.StartsWith("ERR|", StringComparison.OrdinalIgnoreCase))
            return "FAIL";
        if (payload.Contains("denied", StringComparison.OrdinalIgnoreCase))
            return "DENIED";
        if (payload.StartsWith("STATE/unknown", StringComparison.OrdinalIgnoreCase))
            return "UNKNOWN";
        if (payload.StartsWith("STATE/", StringComparison.OrdinalIgnoreCase))
            return "STATE";
        if (payload.Contains("ROUTE/", StringComparison.OrdinalIgnoreCase))
            return "ROUTE";
        if (payload.Contains("stage=", StringComparison.OrdinalIgnoreCase) &&
            payload.Contains("free=", StringComparison.OrdinalIgnoreCase))
            return "MEM";
        if (payload.Contains("HVER", StringComparison.OrdinalIgnoreCase) ||
            payload.StartsWith("ARM", StringComparison.OrdinalIgnoreCase))
            return "ARM";
        if (payload.StartsWith("CAL", StringComparison.OrdinalIgnoreCase))
            return "CAL";
        if (payload.Contains("ASND", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("SOUND", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("WSND", StringComparison.OrdinalIgnoreCase))
            return "SOUND";
        if (payload.Contains("GP3/", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("GP4/", StringComparison.OrdinalIgnoreCase))
            return "BUTTON";
        if (payload.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("BUSY", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("CKSUM", StringComparison.OrdinalIgnoreCase))
            return "WARN";
        return "EVENT";
    }

    private static void UpdateStats(string category, string payload)
    {
        if (category == "FAIL") Stats.Failures++;
        if (category == "DENIED")
        {
            Stats.Denied++;
            var reason = ReasonRegex().Match(payload);
            if (reason.Success) Stats.Increment(Stats.DeniedReasons, reason.Groups[1].Value);
        }
        if (category == "ARM" && payload.Contains("HVER", StringComparison.OrdinalIgnoreCase))
            Stats.ArmVersion = payload;
        if (category == "STATE" || category == "UNKNOWN")
        {
            var stateMatch = StateRegex().Match(payload);
            if (stateMatch.Success)
            {
                var state = stateMatch.Groups[1].Value;
                Stats.LastState = state;
                Stats.Increment(Stats.States, state);
                if (!state.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                    _startWithoutStateAt = null;
            }
            var lux = LuxRegex().Match(payload);
            if (lux.Success && double.TryParse(lux.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                Stats.AddLux(value);
        }
        if (payload.Contains("GP4/short-start", StringComparison.OrdinalIgnoreCase))
            _startWithoutStateAt = DateTimeOffset.Now;
        if (payload.Contains("ROUTE/start", StringComparison.OrdinalIgnoreCase))
        {
            Stats.RouteStarts++;
            Stats.ActiveRouteStartedAt = DateTimeOffset.Now;
            _startWithoutStateAt = null;
        }
        if (payload.Contains("ROUTE/complete", StringComparison.OrdinalIgnoreCase))
        {
            Stats.RouteCompletes++;
            if (Stats.ActiveRouteStartedAt is { } started)
            {
                Stats.RouteDurations.Add(DateTimeOffset.Now - started);
                Stats.ActiveRouteStartedAt = null;
            }
        }
        if (payload.Contains("ROUTE/aborted", StringComparison.OrdinalIgnoreCase))
            Stats.RouteAborts++;
        var free = FreeRegex().Match(payload);
        if (free.Success && int.TryParse(free.Groups[1].Value, out var bytes))
            Stats.AddFree(bytes);
    }

    private static string EnrichState(string payload)
    {
        if (_profiles is null) return payload;
        var lux = LuxRegex().Match(payload);
        if (!lux.Success ||
            !double.TryParse(lux.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return payload;
        var nearest = _profiles.Nearest(value);
        return nearest is null
            ? payload
            : $"{payload} · nearest={nearest.Id} range={nearest.Min:0.0}..{nearest.Max:0.0} Δ={nearest.Distance(value):0.0}";
    }

    private static void WriteJson(string category, string payload, string raw, long? deviceTick)
    {
        if (_jsonLog is null) return;
        var record = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            category,
            payload,
            raw,
            deviceTick,
            state = Stats.LastState
        });
        lock (LogLock) _jsonLog.WriteLine(record);
    }

    private static void EnqueueRecent(string line)
    {
        RecentRaw.Enqueue(line);
        while (RecentRaw.Count > 100 && RecentRaw.TryDequeue(out _)) { }
    }

    private static void Raw(string message)
    {
        lock (LogLock) _rawLog?.WriteLine(message);
    }

    private static void Info(string category, string message) => Print(category, message, ConsoleColor.Gray);
    private static void Highlight(string category, string message) => Print(category, message, ConsoleColor.Cyan);
    private static void Success(string category, string message) => Print(category, message, ConsoleColor.Green);
    private static void Warn(string category, string message) => Print(category, message, ConsoleColor.Yellow);
    private static void Error(string category, string message) => Print(category, message, ConsoleColor.Red);

    private static void Print(string category, string message, ConsoleColor color)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{category,-9}] {message}";
        lock (ConsoleLock)
        {
            SetColor(color);
            Console.WriteLine(line);
            Console.ResetColor();
        }
        Raw(line);
    }

    private static void SetColor(ConsoleColor color)
    {
        if (_useColor) Console.ForegroundColor = color;
    }

    private static async Task DelayUnlessStopped(TimeSpan delay)
    {
        try { await Task.Delay(delay, Stop.Token); }
        catch (OperationCanceledException) { }
    }

    private static int PortNumber(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }

    private static void PrintProfiles(ProfileSet profiles)
    {
        Highlight("PROFILE", $"Loaded {profiles.Profiles.Count} profiles from {profiles.Source}");
        foreach (var p in profiles.Profiles)
            Info("PROFILE", $"{p.Id,-22} {p.Min,6:0.0} .. {p.Max,6:0.0} lux  stable={p.StableMs}ms");
    }

    [GeneratedRegex(@"STATE/([a-z0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex StateRegex();
    [GeneratedRegex(@"lux=(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex LuxRegex();
    [GeneratedRegex(@"free=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FreeRegex();
    [GeneratedRegex(@"reason=([^\s|]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReasonRegex();
}

internal sealed class Options
{
    public string? Port { get; private set; }
    public int Baud { get; private set; } = 115200;
    public int PingSeconds { get; private set; } = 10;
    public int RescanSeconds { get; private set; } = 3;
    public int ProbeTimeoutMs { get; private set; } = 1500;
    public bool NoColor { get; private set; }
    public string? BundlePath { get; private set; }
    public string DiagnosticsDirectory { get; private set; } = "diagnostics";

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next()
            {
                if (++i >= args.Length) throw new ArgumentException($"Missing value after {args[i - 1]}");
                return args[i];
            }

            switch (args[i])
            {
                case "--port": o.Port = Next(); break;
                case "--baud": o.Baud = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--ping-seconds": o.PingSeconds = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 2, 300); break;
                case "--rescan-seconds": o.RescanSeconds = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 1, 60); break;
                case "--probe-timeout-ms": o.ProbeTimeoutMs = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 300, 10000); break;
                case "--bundle": o.BundlePath = Next(); break;
                case "--diagnostics": o.DiagnosticsDirectory = Next(); break;
                case "--no-color": o.NoColor = true; break;
                case "--help":
                case "-h":
                    Console.WriteLine("""
GuardHardwareMonitor 2.0
  --port COM31              Pin Pico port; default scans all COM ports safely
  --bundle <path>           ZIP, folder, or guard-calibration.json for lux diagnosis
  --ping-seconds <2..300>   Safe PING interval; default 10
  --rescan-seconds <1..60>  Reconnect delay; default 3
  --diagnostics <folder>    Log directory; default diagnostics
  --no-color                Disable console colors
""");
                    Environment.Exit(0);
                    break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        return o;
    }
}

internal sealed class SessionStats
{
    public string? Port;
    public string? Firmware;
    public string? ArmVersion;
    public string? LastState;
    public bool Verified;
    public long Events;
    public int Failures;
    public int Denied;
    public int RouteStarts;
    public int RouteCompletes;
    public int RouteAborts;
    public int StartWithoutStateWarnings;
    public int? MinFree;
    public int? MaxFree;
    public double? MinLux;
    public double? MaxLux;
    public DateTimeOffset? ActiveRouteStartedAt;
    public readonly List<TimeSpan> RouteDurations = [];
    public readonly Dictionary<string, int> States = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, int> DeniedReasons = new(StringComparer.OrdinalIgnoreCase);

    public void Increment(Dictionary<string, int> map, string key) =>
        map[key] = map.TryGetValue(key, out var value) ? value + 1 : 1;

    public void AddFree(int value)
    {
        MinFree = MinFree is null ? value : Math.Min(MinFree.Value, value);
        MaxFree = MaxFree is null ? value : Math.Max(MaxFree.Value, value);
    }

    public void AddLux(double value)
    {
        MinLux = MinLux is null ? value : Math.Min(MinLux.Value, value);
        MaxLux = MaxLux is null ? value : Math.Max(MaxLux.Value, value);
    }

    public string Render(ProfileSet? profiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== GuardHardwareMonitor session summary ===");
        sb.AppendLine($"Port: {Port ?? "-"}");
        sb.AppendLine($"Pico: {Firmware ?? "-"}");
        sb.AppendLine($"ARM : {ArmVersion ?? "not observed in Pico events"}");
        sb.AppendLine($"Events: {Events} · failures: {Failures} · denied: {Denied}");
        sb.AppendLine($"Routes: start={RouteStarts} complete={RouteCompletes} aborted={RouteAborts}");
        if (RouteDurations.Count > 0)
            sb.AppendLine($"Route duration: min={RouteDurations.Min():g} max={RouteDurations.Max():g}");
        if (MinFree is not null)
            sb.AppendLine($"Free heap: {MinFree:N0} .. {MaxFree:N0} bytes");
        if (MinLux is not null)
            sb.AppendLine($"Observed lux: {MinLux:0.0} .. {MaxLux:0.0}");
        if (States.Count > 0)
            sb.AppendLine("States: " + string.Join(", ", States.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")));
        if (DeniedReasons.Count > 0)
            sb.AppendLine("Denied reasons: " + string.Join(", ", DeniedReasons.OrderByDescending(x => x.Value).Select(x => $"{x.Key}={x.Value}")));
        if (StartWithoutStateWarnings > 0)
            sb.AppendLine($"WARN: {StartWithoutStateWarnings} start(s) produced no stable state/route.");
        if (profiles is not null)
            sb.AppendLine($"Profiles: {profiles.Source}");
        return sb.ToString();
    }
}

internal sealed record GuardProfile(string Id, double Center, double Tolerance, int StableMs)
{
    public double Min => Center - Tolerance;
    public double Max => Center + Tolerance;
    public double Distance(double lux) => lux < Min ? Min - lux : lux > Max ? lux - Max : 0;
}

internal sealed class ProfileSet
{
    public required string Source { get; init; }
    public required List<GuardProfile> Profiles { get; init; }

    public GuardProfile? Nearest(double lux) =>
        Profiles.OrderBy(p => p.Distance(lux)).ThenBy(p => Math.Abs(p.Center - lux)).FirstOrDefault();

    public static ProfileSet? TryLoad(string? input)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(input))
                input = FindCircuitPyCalibration();
            if (string.IsNullOrWhiteSpace(input))
                return null;

            string json;
            var full = Path.GetFullPath(input);
            if (Directory.Exists(full))
            {
                var file = Path.Combine(full, "guard-calibration.json");
                json = File.ReadAllText(file);
                full = file;
            }
            else if (full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(full);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("guard-calibration.json", StringComparison.OrdinalIgnoreCase));
                if (entry is null) return null;
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                json = reader.ReadToEnd();
            }
            else
            {
                json = File.ReadAllText(full);
            }

            using var doc = JsonDocument.Parse(json);
            var profiles = new List<GuardProfile>();
            foreach (var property in doc.RootElement.GetProperty("profiles").EnumerateObject())
            {
                var value = property.Value;
                profiles.Add(new GuardProfile(
                    property.Name,
                    value.GetProperty("center").GetDouble(),
                    value.GetProperty("tolerance").GetDouble(),
                    value.TryGetProperty("stable_ms", out var stable) ? stable.GetInt32() : 750));
            }
            return new ProfileSet { Source = full, Profiles = profiles };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PROFILE|load failed: {ex.Message}");
            return null;
        }
    }

    private static string? FindCircuitPyCalibration()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady &&
                    drive.VolumeLabel.Equals("CIRCUITPY", StringComparison.OrdinalIgnoreCase))
                {
                    var path = Path.Combine(drive.RootDirectory.FullName, "guard-calibration.json");
                    if (File.Exists(path)) return path;
                }
            }
            catch { }
        }
        return null;
    }
}
