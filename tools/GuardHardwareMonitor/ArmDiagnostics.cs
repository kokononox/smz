namespace GuardHardwareMonitor;

// Interprets only evidence already emitted by the Pico. Never sends a command.
internal sealed class ArmDiagnostics
{
    public string? Ready { get; private set; }
    public string? HostUsb { get; private set; }
    public string? Version { get; private set; }
    public string? Fault { get; private set; }
    private string? _lastReport;

    // Extract a device record from a monitor or Classroom text log; never opens a port.
    public string? ObserveLogLine(string text)
    {
        foreach (string marker in new[] { "ERR|GUARD|ARM|", "ERR|ARM|", "OK|PONG|" })
        {
            int start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            string record = text[start..];
            int end = record.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '"' });
            if (end >= 0) record = record[..end];
            return Observe(record);
        }
        return null;
    }

    public string? Observe(string line)
    {
        if (line.StartsWith("OK|PONG|", StringComparison.Ordinal))
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in line.Split('|'))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2) fields[pair[0]] = pair[1];
            }
            // A legacy/non-native PONG may lack these fields. Unknown is not a failure.
            Ready = fields.GetValueOrDefault("arm-ready");
            HostUsb = fields.GetValueOrDefault("arm-usb");
            Version = fields.GetValueOrDefault("arm-ver");
            if (Ready == "1") Fault = null;
        }
        else if (line.StartsWith("ERR|ARM|", StringComparison.OrdinalIgnoreCase) ||
                 line.StartsWith("ERR|GUARD|ARM|", StringComparison.OrdinalIgnoreCase))
        {
            int start = line.IndexOf("detail=", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            string value = line[(start + 7)..];
            int end = value.IndexOf("|version=", StringComparison.OrdinalIgnoreCase);
            Fault = (end >= 0 ? value[..end] : value).Trim();
        }
        else return null;

        string report = Render();
        if (report == _lastReport) return null;
        _lastReport = report;
        return report;
    }

    public string Render()
    {
        var usb = HostUsb switch
        {
            "3" => "UP was reported earlier (cached; not proof of a currently healthy link)",
            "2" => "SUSPEND was reported earlier",
            "1" => "DOWN was reported earlier",
            "0" => "no USB state reported yet",
            null => "not available on this firmware",
            _ => $"unknown code {HostUsb}"
        };
        var status = $"Pico ARM ready={Ready ?? "unknown"}; version={Version ?? "unknown"}; USB={usb}.";
        if (!string.IsNullOrEmpty(Fault)) status += $" Last fault={Fault}.";
        if (Fault?.Contains("INCOMPATIBLE|HVER", StringComparison.OrdinalIgnoreCase) == true)
            return status + " Pico received an HVER reply missing required REL/ASND capabilities. Check the installed Pro Micro firmware and version.";
        if (Fault?.Contains("ERR|UNKNOWN", StringComparison.OrdinalIgnoreCase) == true)
            return status + " Pico recorded an unrecognized-command reply. Possible causes: wrong/old firmware or damaged/incomplete command; check installed firmware and Pico GP16 TX -> level shifter -> Pro Micro D0 RX. A received reply alone does not prove which wire failed.";
        if (Fault?.Contains("CKSUM", StringComparison.OrdinalIgnoreCase) == true || Fault?.Contains("NOFRAME", StringComparison.OrdinalIgnoreCase) == true)
            return status + " Frame/checksum error reported. Check wiring, common ground, level shifter and 57600 baud; power off before touching wiring.";
        if (Fault?.Contains("ack-timeout", StringComparison.OrdinalIgnoreCase) == true)
            return status + " No acknowledgement in time. Check power, both UART directions, common ground, level shifter and firmware; timeout cannot identify the failed part.";
        if (!string.IsNullOrEmpty(Fault)) return status + $" Pico reported fault: {Fault}. Check the raw log; cause is not determined.";
        if (Ready == "1") return status + " Pico has accepted the ARM handshake; this does not verify sensors or all wiring.";
        if (Ready == "0") return status + " Not ready. PING does not expose the fault reason; watch for ERR|ARM or ERR|GUARD|ARM in the log. Do not start Guard just to obtain an error.";
        return status + " ARM diagnostic fields are unavailable or incomplete; no wiring conclusion is possible.";
    }
}
