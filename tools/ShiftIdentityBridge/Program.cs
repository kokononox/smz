using System.Diagnostics;
using System.IO.Ports;
using System.Reflection;
using System.Text.Json;
using ClassroomShift;
using Microsoft.Win32;

namespace ShiftIdentityBridge;

internal static class Program
{
    // No autorun, service, keyboard hook, stored shift, password or username override.
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(true, "Local\\ClassroomStudioShiftIdentityBridge", out bool owns);
        if (!owns) return 2;
        try
        {
            if(args.Length==1&&args[0]=="--self-test") {
                var ids=KnownUsbIdentities();
                return ShiftUserIdentity.Hash(" dayuser ")==ShiftUserIdentity.Hash("DAYUSER")
                    &&ShiftUserIdentity.Hash("DAYUSER")!=ShiftUserIdentity.Hash("NIGHTUSER")
                    &&ClockStamp(new DateTime(2026,10,10,7,5,0))=="TIME!|07:05"
                    &&ClockStamp(new DateTime(2026,10,10,23,59,0))=="TIME!|23:59"
                    &&ids.Contains("VID_6BA8&PID_C5DE")&&ids.Count>=31 ? 0 : 9;
            }
            string? requested = args.Length == 2 && args[0] == "--port" ? args[1] : null;
            if (args.Length != 0 && requested is null) return 3;
            var known = KnownPorts();
            if (requested is not null)
                known = known.Where(p => p.Equals(requested, StringComparison.OrdinalIgnoreCase)).ToList();
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(30))
            {
                var candidates = new List<(SerialPort port, string nonce, bool clock)>();
                try
                {
                    foreach (var name in known)
                    {
                        SerialPort? port = null;
                        try
                        {
                            port = new SerialPort(name, 115200)
                            {
                                DtrEnable = true, RtsEnable = false, Handshake = Handshake.None,
                                ReadTimeout = 350, WriteTimeout = 1000, NewLine = "\n",
                            };
                            port.Open();
                            // Power-loss bootstrap: a board that lost power keeps the
                            // schedule it was flashed with but owns no wall clock, so
                            // it can never arm a window or wake the machine by itself.
                            // The stamp goes only to a board that says it has no clock:
                            // re-arming a healthy board would move a real deadline, and
                            // a stamp taken inside the lead time falls back to "one
                            // minute from now".
                            if (BoardNeedsClock(port)) port.WriteLine(ClockStamp(DateTime.Now));
                            port.WriteLine("SHIFT?");
                            var probe = Stopwatch.StartNew();
                            while (probe.ElapsedMilliseconds < 600)
                            {
                                string line;
                                try { line = port.ReadLine().Trim(); }
                                catch (TimeoutException) { continue; }
                                if (!line.StartsWith("OK|SHIFT-CHALLENGE|", StringComparison.Ordinal)) continue;
                                var challenge=line["OK|SHIFT-CHALLENGE|".Length..].Split('|');
                                string nonce=challenge[0];
                                bool needsClock=challenge.Length==2&&challenge[1]=="clock=1";
                                if ((challenge.Length==1||needsClock) && nonce.Length == 8 && uint.TryParse(nonce,
                                    System.Globalization.NumberStyles.HexNumber, null, out _))
                                {
                                    candidates.Add((port, nonce, needsClock)); port = null; break;
                                }
                            }
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        finally { Close(port); }
                    }
                    // Never guess between two simultaneously waiting boards.
                    if (candidates.Count > 1) return 4;
                    if (candidates.Count == 1)
                    {
                        var (port, nonce, needsClock) = candidates[0];
                        var localNow=DateTime.Now;
                        port.WriteLine(needsClock
                            ? $"SHIFT2!|{nonce}|{ShiftUserIdentity.Hash(Environment.UserName)}|{localNow.Hour*60+localNow.Minute}"
                            : $"SHIFT!|{nonce}|{ShiftUserIdentity.Hash(Environment.UserName)}");
                        var acknowledgement = Stopwatch.StartNew();
                        while (acknowledgement.ElapsedMilliseconds < 3000)
                        {
                            string line;
                            try { line = port.ReadLine().Trim(); }
                            catch (TimeoutException) { continue; }
                            if (line == $"OK|SHIFT-ACCEPTED|{nonce}") return 0;
                            if (line.StartsWith("ERR|SHIFT|", StringComparison.Ordinal)) return 5;
                        }
                        return 6;
                    }
                }
                finally
                {
                    // Runs before Main returns. DTR falls and COM is released;
                    // Pico independently waits for a stable DTR-off interval.
                    foreach (var (port, _, _) in candidates) Close(port);
                }
                Thread.Sleep(200);
                known = KnownPorts();
                if (requested is not null)
                    known = known.Where(p => p.Equals(requested, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            return 7;
        }
        catch { return 8; }
        finally { mutex.ReleaseMutex(); }
    }

    private static void Close(SerialPort? port)
    {
        if (port is null) return;
        try { if (port.IsOpen) { port.DtrEnable = false; port.Close(); } }
        catch (IOException) { }
        finally { port.Dispose(); }
    }

    // "TIME!|HH:MM" is the board's one-shot clock stamp.  Formatted with the
    // invariant culture: the board parses ASCII digits only, and a culture with
    // different numerals would send a stamp it has to reject.
    private static string ClockStamp(DateTime now) =>
        "TIME!|" + now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    // A board that lost power owns no wall clock and can never arm a window; a
    // board that still has one must be left exactly as it is.  WAKE? is the only
    // question that tells the two apart, and only the new firmware answers it:
    // older firmware replies ERR|COMMAND|unknown=WAKE? and gets no stamp.  The
    // answer is read off the same open port before the shift probe, so a board
    // that cannot answer costs this attempt nothing.
    private static bool BoardNeedsClock(SerialPort port)
    {
        port.WriteLine("WAKE?");
        var probe = Stopwatch.StartNew();
        while (probe.ElapsedMilliseconds < 400)
        {
            string line;
            try { line = port.ReadLine().Trim(); }
            catch (TimeoutException) { continue; }
            if (!line.StartsWith("OK|WAKE|", StringComparison.Ordinal)) continue;
            return line.Contains("|synced=0|") || line.EndsWith("|synced=0", StringComparison.Ordinal);
        }
        return false;
    }

    private static HashSet<string> KnownUsbIdentities()
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VID_2E8A&PID_000A" };
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var profiles = root.TryGetProperty("profiles", out var items)
                ? items.EnumerateArray().ToArray() : new[] { root };
            foreach (var profile in profiles)
            {
                string vid = profile.GetProperty("usb_vid").GetString()![2..];
                string pid = profile.GetProperty("usb_pid").GetString()![2..];
                identities.Add($"VID_{vid}&PID_{pid}");
            }
        }
        return identities;
    }
    private static List<string> KnownPorts()
    {
        var identities=KnownUsbIdentities();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usb is null) return result.ToList();
        foreach (var device in usb.GetSubKeyNames())
        {
            if (!identities.Any(id => device.StartsWith(id, StringComparison.OrdinalIgnoreCase))) continue;
            using var key = usb.OpenSubKey(device);
            if (key is null) continue;
            foreach (var instance in key.GetSubKeyNames())
            {
                using var parameters = key.OpenSubKey(instance + @"\Device Parameters");
                if (parameters?.GetValue("PortName") is string name && name.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    result.Add(name);
            }
        }
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}