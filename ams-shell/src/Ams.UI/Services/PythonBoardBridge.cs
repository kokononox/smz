using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

using Ams.UI.Services;

namespace Ams.UI.Services;

/// <summary>
/// Option B (design doc §13.10): runs the proven Python stack
/// (ams_serial.py / ams_crypto.py) as a sidecar via bridge.py and exchanges
/// line-delimited JSON over stdio.
///
/// Wire protocol v1 (implemented by bridge/bridge.py):
///   → {"op":"connect","port":"AUTO"}                    ← {"event":"connected","port":"COM8","fw":"1.6"}
///   → {"op":"send","cmd":"MCLICK|left,1","timeout":25}  ← {"event":"reply","reply":"OK|MCLICK"}
///   → {"op":"disconnect"}                                ← {"event":"disconnected"}  (HALT + BYE, §11.3)
///   ← {"event":"evt","line":"EVT|..."}                  (unsolicited board events, e.g. EVT|TRG)
///   ← {"event":"error","op":"...","message":"..."}
///
/// "timeout" is in SECONDS (ams_serial's command() counts seconds) and must be
/// proportional to the command window for WSND/TRGSND/SCAL (§15.4 rule 4).
/// </summary>
public sealed class PythonBoardBridge : IBoardBridge
{
    private readonly string _pythonDir;
    private readonly string _bridgeScript;

    private Process? _proc;
    private CancellationTokenSource? _readerCts;

    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private TaskCompletionSource _connectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string>? _replyTcs;
    private TaskCompletionSource<List<string>>? _portsTcs;   // v0.9.43 — pending list_ports answer

    // v0.6.2 — abort writes must never queue behind an in-flight command
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public event EventHandler<string>? LineReceived;
    public event EventHandler<BridgeState>? StateChanged;

    public BridgeState State { get; private set; } = BridgeState.Disconnected;
    public string? FirmwareVersion { get; private set; }
    public string? Port { get; private set; }

    /// <param name="pythonDir">Folder that contains ams_serial.py and ams_crypto.py (the AMS pc folder).</param>
    /// <param name="bridgeScript">Path to bridge.py (ships in the repo under bridge/, copied next to the exe).</param>
    public PythonBoardBridge(string pythonDir, string bridgeScript)
    {
        _pythonDir = pythonDir;
        _bridgeScript = bridgeScript;
    }

    /// <summary>v0.9.20 — candidate bridge.py locations, best first. The build output has
    /// bridge\bridge.py next to the exe (csproj link copy); a flat publish has bridge.py loose;
    /// and an exe dropped anywhere inside the repo tree (e.g. src\Ams.UI in a hand-made release
    /// zip) finds it by walking up the folders. Extracted for TestRunner.</summary>
    public static List<string> BridgeScriptCandidates(string baseDir)
    {
        string trimmed = baseDir.TrimEnd(Path.DirectorySeparatorChar);
        var list = new List<string>
        {
            Path.Combine(trimmed, "bridge", "bridge.py"),   // build output (csproj link copy)
            Path.Combine(trimmed, "bridge.py"),             // flat publish folder
        };
        var dir = new DirectoryInfo(trimmed);
        for (int i = 0; i < 4 && dir?.Parent is not null; i++, dir = dir.Parent)
            list.Add(Path.Combine(dir.Parent.FullName, "bridge", "bridge.py"));   // exe inside the repo tree
        return list;
    }

    private void SetState(BridgeState s)
    {
        State = s;
        StateChanged?.Invoke(this, s);
    }

    /// <summary>v0.9.43 — starts the python sidecar if it is not already running. Extracted from
    /// ConnectAsync so the manual port scan (ListPortsAsync) reuses the same process the Connect will use.</summary>
    private void EnsureProcess()
    {
        if (_proc is { HasExited: false }) return;

        var psi = new ProcessStartInfo
        {
            FileName = PortablePaths.FindPython(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // v0.9.60 — read the bridge as UTF-8 (Persian log/error lines arrive intact;
            // pairs with bridge.py's stdout reconfigure + ensure_ascii fallback)
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";   // v0.9.60 — python's own stderr prints too
        psi.ArgumentList.Add(_bridgeScript);
        psi.ArgumentList.Add("--pydir");
        psi.ArgumentList.Add(_pythonDir);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start python bridge.");
        // v0.9.20 — if python dies before the handshake (bridge.py missing, bad --pydir),
        // fail the pending connect immediately instead of hanging in "Handshaking" forever.
        var proc = _proc;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            if (State != BridgeState.Connected)
            {
                _connectTcs.TrySetException(new InvalidOperationException(
                    $"python bridge exited before connecting (code {proc.ExitCode}). Is bridge.py present? See the searched paths in the log."));
                _portsTcs?.TrySetException(new InvalidOperationException("python bridge exited"));   // v0.9.43
            }
        };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) LineReceived?.Invoke(this, "[py] " + e.Data);
        };
        _proc.BeginErrorReadLine();

        _readerCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoop(_readerCts.Token));
    }

    /// <summary>v0.9.43 — manual board connect (the user has no Pico yet): asks bridge.py for the
    /// actual serial ports. Items are "COMx — description" labels; the view model parses the device.</summary>
    public async Task<IReadOnlyList<string>> ListPortsAsync(CancellationToken ct = default)
    {
        EnsureProcess();
        _portsTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await WriteAsync(new { op = "list_ports" });
        try { return await _portsTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
        finally { _portsTcs = null; }
    }

    public async Task ConnectAsync(string portName, CancellationToken ct = default)
    {
        if (State != BridgeState.Disconnected) return;
        SetState(BridgeState.Connecting);

        EnsureProcess();   // v0.9.43 — extracted so the manual port scan reuses the same sidecar

        _connectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await WriteAsync(new { op = "connect", port = portName });
            await _connectTcs.Task.WaitAsync(System.TimeSpan.FromSeconds(15), ct);   // v0.9.57 — 15s timeout on HELLO
        }
        catch
        {
            // v0.9.20 — reset so a failed attempt (missing bridge.py, no board, bad port) does
            // not poison the bridge: the next Connect click must be able to start over.
            try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
            _proc = null;
            SetState(BridgeState.Disconnected);
            throw;
        }
    }

    public async Task<string> SendAsync(string command, double? timeoutSeconds = null, CancellationToken ct = default)
    {
        if (State != BridgeState.Connected)
            throw new InvalidOperationException("Bridge is not connected.");

        await _ioLock.WaitAsync(ct);            // one command in flight — board frames are strictly ordered
        try
        {
            _replyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (timeoutSeconds.HasValue)
                await WriteAsync(new { op = "send", cmd = command, timeout = timeoutSeconds.Value });
            else
                await WriteAsync(new { op = "send", cmd = command });

            // reply wait = command window + 10s bridge overhead (§15.4 rule 4)
            var wait = timeoutSeconds.HasValue
                ? TimeSpan.FromSeconds(timeoutSeconds.Value + 10)
                : Timeout.InfiniteTimeSpan;
            return await _replyTcs.Task.WaitAsync(wait, ct);
        }
        finally
        {
            _replyTcs = null;
            _ioLock.Release();
        }
    }

    /// <summary>
    /// v0.9.2 — packs the dense path into one {"op":"send_path"} line (compact "x,y;…" and
    /// "d;d;…" strings). Single reply wait, same one-in-flight ordering as SendAsync.
    /// </summary>
    public async Task<string> SendPathAsync(IReadOnlyList<(int X, int Y, int DelayMs)> points, CancellationToken ct = default)
    {
        if (State != BridgeState.Connected)
            throw new InvalidOperationException("Bridge is not connected.");
        if (points.Count == 0) return "OK|PATH,0";

        await _ioLock.WaitAsync(ct);            // one operation in flight — board frames are strictly ordered
        try
        {
            _replyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pts = new StringBuilder(points.Count * 9);
            var dlys = new StringBuilder(points.Count * 5);
            long totalMs = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0) { pts.Append(';'); dlys.Append(';'); }
                pts.Append(points[i].X).Append(',').Append(points[i].Y);
                dlys.Append(points[i].DelayMs);
                totalMs += points[i].DelayMs;
            }
            await WriteAsync(new { op = "send_path", pts = pts.ToString(), dlys = dlys.ToString() });
            // reply wait = path duration + serial TX time (~2ms/point) + bridge overhead
            var wait = TimeSpan.FromMilliseconds(totalMs + points.Count * 10 + 20000);
            return await _replyTcs.Task.WaitAsync(wait, ct);
        }
        finally
        {
            _replyTcs = null;
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Instant abort (v0.6.2): fire-and-forget {"op":"abort"} — bridge.py v2 writes HALT
    /// to the board immediately (write-only, no reply wait, no command lock), so a
    /// long-window listen (WSND/TRGSND) breaks right away instead of after its window.
    /// </summary>
    public async Task SendAbortAsync()
    {
        if (_proc is null) return;
        try { await WriteAsync(new { op = "abort" }); } catch { /* best effort */ }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_proc is null) return;
        try
        {
            if (State == BridgeState.Connected)
                await WriteAsync(new { op = "disconnect" });   // sidecar sends HALT + BYE (§11.3)
            _proc.StandardInput.Close();                        // EOF ends bridge.py's stdin loop
            await _proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
        }
        catch { /* best effort */ }
        finally
        {
            try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
            _proc = null;
            _readerCts?.Cancel();
            FirmwareVersion = null;
            Port = null;
            SetState(BridgeState.Disconnected);
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private async Task WriteAsync(object msg)
    {
        if (_proc is null) throw new InvalidOperationException("Bridge process is not running.");
        await _writeLock.WaitAsync();
        try
        {
            await _proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(msg));
            await _proc.StandardInput.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _proc is not null)
            {
                var line = await _proc.StandardOutput.ReadLineAsync(ct);
                if (line is null) break;         // sidecar exited
                HandleLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LineReceived?.Invoke(this, "[bridge] read error: " + ex.Message);
        }

        if (State != BridgeState.Disconnected)
        {
            FirmwareVersion = null;
            SetState(BridgeState.Disconnected);
            LineReceived?.Invoke(this, "[bridge] sidecar exited — connection closed");
        }
        // v0.9.0 — never leave a pending SendAsync/Connect waiting when the sidecar dies
        _replyTcs?.TrySetException(new InvalidOperationException("bridge process closed"));
        _connectTcs.TrySetException(new InvalidOperationException("bridge process closed"));
        _portsTcs?.TrySetException(new InvalidOperationException("bridge process closed"));   // v0.9.43
    }

    private void HandleLine(string line)
    {
        LineReceived?.Invoke(this, line);        // raw protocol visibility for the log panel

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }        // non-JSON noise from python — already logged

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var evtProp)) return;

            switch (evtProp.GetString())
            {
                case "connected":
                    Port = root.GetProperty("port").GetString();
                    FirmwareVersion = root.GetProperty("fw").GetString();
                    SetState(BridgeState.Connected);
                    _connectTcs.TrySetResult();
                    break;

                case "reply":
                    _replyTcs?.TrySetResult(root.GetProperty("reply").GetString() ?? string.Empty);
                    break;

                case "disconnected":
                    SetState(BridgeState.Disconnected);
                    break;

                case "evt":
                    // unsolicited board events (EVT|TRG|react=… — fired by TRGSND, §14.4)
                    // surfaced via LineReceived; a dedicated event lands when the UI needs it
                    break;

                case "abort_sent":
                    // bridge acknowledged the out-of-band HALT — informational only, nothing to pair
                    break;

                case "aborted":
                    // v0.9.0 — REGRESSION FIX: bridge.py v2 ends the in-flight command with an
                    // "aborted" event (not "reply") after an abort. Without this case SendAsync
                    // hung until its timeout and the Run button stayed grey (the aborted-event bug
                    // from the memory index had crept back in).
                    _replyTcs?.TrySetResult(root.TryGetProperty("reply", out var ar)
                        ? ar.GetString() ?? "ERR|aborted" : "ERR|aborted");
                    break;

                case "ports":   // v0.9.43 — answer to list_ports
                    var plist = new List<string>();
                    if (root.TryGetProperty("ports", out var parr))
                        foreach (var p in parr.EnumerateArray())
                        {
                            var dev = p.TryGetProperty("device", out var dv) ? dv.GetString() ?? "" : "";
                            var lbl = p.TryGetProperty("label", out var lb) ? lb.GetString() ?? "" : "";
                            if (dev.Length > 0) plist.Add(lbl.Length > 0 ? $"{dev} — {lbl}" : dev);
                        }
                    _portsTcs?.TrySetResult(plist);
                    break;

                case "error":
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown error" : "unknown error";
                    var op = root.TryGetProperty("op", out var operation) ? operation.GetString() ?? "" : "";
                    _connectTcs.TrySetException(new InvalidOperationException(msg));
                    _replyTcs?.TrySetException(new InvalidOperationException(msg));
                    if (State == BridgeState.Connecting || (State == BridgeState.Connected
                            && op is ("send" or "send_path")))
                        SetState(BridgeState.Disconnected);
                    break;
            }
        }
    }
}using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

using Ams.UI.Services;

namespace Ams.UI.Services;

/// <summary>
/// Option B (design doc §13.10): runs the proven Python stack
/// (ams_serial.py / ams_crypto.py) as a sidecar via bridge.py and exchanges
/// line-delimited JSON over stdio.
///
/// Wire protocol v1 (implemented by bridge/bridge.py):
///   → {"op":"connect","port":"AUTO"}                    ← {"event":"connected","port":"COM8","fw":"1.6"}
///   → {"op":"send","cmd":"MCLICK|left,1","timeout":25}  ← {"event":"reply","reply":"OK|MCLICK"}
///   → {"op":"disconnect"}                                ← {"event":"disconnected"}  (HALT + BYE, §11.3)
///   ← {"event":"evt","line":"EVT|..."}                  (unsolicited board events, e.g. EVT|TRG)
///   ← {"event":"error","op":"...","message":"..."}
///
/// "timeout" is in SECONDS (ams_serial's command() counts seconds) and must be
/// proportional to the command window for WSND/TRGSND/SCAL (§15.4 rule 4).
/// </summary>
public sealed class PythonBoardBridge : IBoardBridge
{
    private readonly string _pythonDir;
    private readonly string _bridgeScript;

    private Process? _proc;
    private CancellationTokenSource? _readerCts;

    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private TaskCompletionSource _connectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<string>? _replyTcs;
    private TaskCompletionSource<List<string>>? _portsTcs;   // v0.9.43 — pending list_ports answer

    // v0.6.2 — abort writes must never queue behind an in-flight command
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public event EventHandler<string>? LineReceived;
    public event EventHandler<BridgeState>? StateChanged;

    public BridgeState State { get; private set; } = BridgeState.Disconnected;
    public string? FirmwareVersion { get; private set; }
    public string? Port { get; private set; }

    /// <param name="pythonDir">Folder that contains ams_serial.py and ams_crypto.py (the AMS pc folder).</param>
    /// <param name="bridgeScript">Path to bridge.py (ships in the repo under bridge/, copied next to the exe).</param>
    public PythonBoardBridge(string pythonDir, string bridgeScript)
    {
        _pythonDir = pythonDir;
        _bridgeScript = bridgeScript;
    }

    /// <summary>v0.9.20 — candidate bridge.py locations, best first. The build output has
    /// bridge\bridge.py next to the exe (csproj link copy); a flat publish has bridge.py loose;
    /// and an exe dropped anywhere inside the repo tree (e.g. src\Ams.UI in a hand-made release
    /// zip) finds it by walking up the folders. Extracted for TestRunner.</summary>
    public static List<string> BridgeScriptCandidates(string baseDir)
    {
        string trimmed = baseDir.TrimEnd(Path.DirectorySeparatorChar);
        var list = new List<string>
        {
            Path.Combine(trimmed, "bridge", "bridge.py"),   // build output (csproj link copy)
            Path.Combine(trimmed, "bridge.py"),             // flat publish folder
        };
        var dir = new DirectoryInfo(trimmed);
        for (int i = 0; i < 4 && dir?.Parent is not null; i++, dir = dir.Parent)
            list.Add(Path.Combine(dir.Parent.FullName, "bridge", "bridge.py"));   // exe inside the repo tree
        return list;
    }

    private void SetState(BridgeState s)
    {
        State = s;
        StateChanged?.Invoke(this, s);
    }

    /// <summary>v0.9.43 — starts the python sidecar if it is not already running. Extracted from
    /// ConnectAsync so the manual port scan (ListPortsAsync) reuses the same process the Connect will use.</summary>
    private void EnsureProcess()
    {
        if (_proc is { HasExited: false }) return;

        var psi = new ProcessStartInfo
        {
            FileName = PortablePaths.FindPython(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // v0.9.60 — read the bridge as UTF-8 (Persian log/error lines arrive intact;
            // pairs with bridge.py's stdout reconfigure + ensure_ascii fallback)
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";   // v0.9.60 — python's own stderr prints too
        psi.ArgumentList.Add(_bridgeScript);
        psi.ArgumentList.Add("--pydir");
        psi.ArgumentList.Add(_pythonDir);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start python bridge.");
        // v0.9.20 — if python dies before the handshake (bridge.py missing, bad --pydir),
        // fail the pending connect immediately instead of hanging in "Handshaking" forever.
        var proc = _proc;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            if (State != BridgeState.Connected)
            {
                _connectTcs.TrySetException(new InvalidOperationException(
                    $"python bridge exited before connecting (code {proc.ExitCode}). Is bridge.py present? See the searched paths in the log."));
                _portsTcs?.TrySetException(new InvalidOperationException("python bridge exited"));   // v0.9.43
            }
        };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) LineReceived?.Invoke(this, "[py] " + e.Data);
        };
        _proc.BeginErrorReadLine();

        _readerCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoop(_readerCts.Token));
    }

    /// <summary>v0.9.43 — manual board connect (the user has no Pico yet): asks bridge.py for the
    /// actual serial ports. Items are "COMx — description" labels; the view model parses the device.</summary>
    public async Task<IReadOnlyList<string>> ListPortsAsync(CancellationToken ct = default)
    {
        EnsureProcess();
        _portsTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await WriteAsync(new { op = "list_ports" });
        try { return await _portsTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); }
        finally { _portsTcs = null; }
    }

    public async Task ConnectAsync(string portName, CancellationToken ct = default)
    {
        if (State != BridgeState.Disconnected) return;
        SetState(BridgeState.Connecting);

        EnsureProcess();   // v0.9.43 — extracted so the manual port scan reuses the same sidecar

        _connectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await WriteAsync(new { op = "connect", port = portName });
            await _connectTcs.Task.WaitAsync(System.TimeSpan.FromSeconds(15), ct);   // v0.9.57 — 15s timeout on HELLO
        }
        catch
        {
            // v0.9.20 — reset so a failed attempt (missing bridge.py, no board, bad port) does
            // not poison the bridge: the next Connect click must be able to start over.
            try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
            _proc = null;
            SetState(BridgeState.Disconnected);
            throw;
        }
    }

    public async Task<string> SendAsync(string command, double? timeoutSeconds = null, CancellationToken ct = default)
    {
        if (State != BridgeState.Connected)
            throw new InvalidOperationException("Bridge is not connected.");

        await _ioLock.WaitAsync(ct);            // one command in flight — board frames are strictly ordered
        try
        {
            _replyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (timeoutSeconds.HasValue)
                await WriteAsync(new { op = "send", cmd = command, timeout = timeoutSeconds.Value });
            else
                await WriteAsync(new { op = "send", cmd = command });

            // reply wait = command window + 10s bridge overhead (§15.4 rule 4)
            var wait = timeoutSeconds.HasValue
                ? TimeSpan.FromSeconds(timeoutSeconds.Value + 10)
                : Timeout.InfiniteTimeSpan;
            return await _replyTcs.Task.WaitAsync(wait, ct);
        }
        finally
        {
            _replyTcs = null;
            _ioLock.Release();
        }
    }

    /// <summary>
    /// v0.9.2 — packs the dense path into one {"op":"send_path"} line (compact "x,y;…" and
    /// "d;d;…" strings). Single reply wait, same one-in-flight ordering as SendAsync.
    /// </summary>
    public async Task<string> SendPathAsync(IReadOnlyList<(int X, int Y, int DelayMs)> points, CancellationToken ct = default)
    {
        if (State != BridgeState.Connected)
            throw new InvalidOperationException("Bridge is not connected.");
        if (points.Count == 0) return "OK|PATH,0";

        await _ioLock.WaitAsync(ct);            // one operation in flight — board frames are strictly ordered
        try
        {
            _replyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pts = new StringBuilder(points.Count * 9);
            var dlys = new StringBuilder(points.Count * 5);
            long totalMs = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0) { pts.Append(';'); dlys.Append(';'); }
                pts.Append(points[i].X).Append(',').Append(points[i].Y);
                dlys.Append(points[i].DelayMs);
                totalMs += points[i].DelayMs;
            }
            await WriteAsync(new { op = "send_path", pts = pts.ToString(), dlys = dlys.ToString() });
            // reply wait = path duration + serial TX time (~2ms/point) + bridge overhead
            var wait = TimeSpan.FromMilliseconds(totalMs + points.Count * 10 + 20000);
            return await _replyTcs.Task.WaitAsync(wait, ct);
        }
        finally
        {
            _replyTcs = null;
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Instant abort (v0.6.2): fire-and-forget {"op":"abort"} — bridge.py v2 writes HALT
    /// to the board immediately (write-only, no reply wait, no command lock), so a
    /// long-window listen (WSND/TRGSND) breaks right away instead of after its window.
    /// </summary>
    public async Task SendAbortAsync()
    {
        if (_proc is null) return;
        try { await WriteAsync(new { op = "abort" }); } catch { /* best effort */ }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_proc is null) return;
        try
        {
            if (State == BridgeState.Connected)
                await WriteAsync(new { op = "disconnect" });   // sidecar sends HALT + BYE (§11.3)
            _proc.StandardInput.Close();                        // EOF ends bridge.py's stdin loop
            await _proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
        }
        catch { /* best effort */ }
        finally
        {
            try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
            _proc = null;
            _readerCts?.Cancel();
            FirmwareVersion = null;
            Port = null;
            SetState(BridgeState.Disconnected);
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private async Task WriteAsync(object msg)
    {
        if (_proc is null) throw new InvalidOperationException("Bridge process is not running.");
        await _writeLock.WaitAsync();
        try
        {
            await _proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(msg));
            await _proc.StandardInput.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _proc is not null)
            {
                var line = await _proc.StandardOutput.ReadLineAsync(ct);
                if (line is null) break;         // sidecar exited
                HandleLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LineReceived?.Invoke(this, "[bridge] read error: " + ex.Message);
        }

        if (State != BridgeState.Disconnected)
        {
            FirmwareVersion = null;
            SetState(BridgeState.Disconnected);
            LineReceived?.Invoke(this, "[bridge] sidecar exited — connection closed");
        }
        // v0.9.0 — never leave a pending SendAsync/Connect waiting when the sidecar dies
        _replyTcs?.TrySetException(new InvalidOperationException("bridge process closed"));
        _connectTcs.TrySetException(new InvalidOperationException("bridge process closed"));
        _portsTcs?.TrySetException(new InvalidOperationException("bridge process closed"));   // v0.9.43
    }

    private void HandleLine(string line)
    {
        LineReceived?.Invoke(this, line);        // raw protocol visibility for the log panel

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }        // non-JSON noise from python — already logged

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var evtProp)) return;

            switch (evtProp.GetString())
            {
                case "connected":
                    Port = root.GetProperty("port").GetString();
                    FirmwareVersion = root.GetProperty("fw").GetString();
                    SetState(BridgeState.Connected);
                    _connectTcs.TrySetResult();
                    break;

                case "reply":
                    _replyTcs?.TrySetResult(root.GetProperty("reply").GetString() ?? string.Empty);
                    break;

                case "disconnected":
                    SetState(BridgeState.Disconnected);
                    break;

                case "evt":
                    // unsolicited board events (EVT|TRG|react=… — fired by TRGSND, §14.4)
                    // surfaced via LineReceived; a dedicated event lands when the UI needs it
                    break;

                case "abort_sent":
                    // bridge acknowledged the out-of-band HALT — informational only, nothing to pair
                    break;

                case "aborted":
                    // v0.9.0 — REGRESSION FIX: bridge.py v2 ends the in-flight command with an
                    // "aborted" event (not "reply") after an abort. Without this case SendAsync
                    // hung until its timeout and the Run button stayed grey (the aborted-event bug
                    // from the memory index had crept back in).
                    _replyTcs?.TrySetResult(root.TryGetProperty("reply", out var ar)
                        ? ar.GetString() ?? "ERR|aborted" : "ERR|aborted");
                    break;

                case "ports":   // v0.9.43 — answer to list_ports
                    var plist = new List<string>();
                    if (root.TryGetProperty("ports", out var parr))
                        foreach (var p in parr.EnumerateArray())
                        {
                            var dev = p.TryGetProperty("device", out var dv) ? dv.GetString() ?? "" : "";
                            var lbl = p.TryGetProperty("label", out var lb) ? lb.GetString() ?? "" : "";
                            if (dev.Length > 0) plist.Add(lbl.Length > 0 ? $"{dev} — {lbl}" : dev);
                        }
                    _portsTcs?.TrySetResult(plist);
                    break;

                case "error":
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown error" : "unknown error";
                    _connectTcs.TrySetException(new InvalidOperationException(msg));
                    _replyTcs?.TrySetException(new InvalidOperationException(msg));
                    if (State == BridgeState.Connecting) SetState(BridgeState.Disconnected);
                    break;
            }
        }
    }
}
