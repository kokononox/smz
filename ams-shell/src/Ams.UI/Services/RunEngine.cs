using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ams.UI.Models;
using NAudio.Wave;

namespace Ams.UI.Services;

/// <summary>
/// Walks the step tree and sends board commands through the bridge
/// (runner rules — design doc §10.3: SETRES first, per-step DLY after,
/// DIS steps skipped, guaranteed HALT by the caller's finally block).
/// Sound steps use command-proportional timeouts (§15.4 rule 4) and their
/// timeout is a warning, not a run abort. Sensitive typeText steps are masked
/// in the log (§17.6). playScript decodes the target .amk recursively with a
/// cycle guard and depth cap (bug #7 lessons, §17.5).
/// </summary>
public sealed class RunEngine
{
    /// <summary>findImage with onTimeout=stopSilent ends the run through this (§3.3.1 group E).</summary>
    public sealed class SilentStop : Exception { }

    /// <summary>Stops the current run after the configured timeout policy has been applied.
    /// This control-flow exception is never alarmed twice by the outer run handler.</summary>
    public sealed class PolicyStop : Exception
    {
        public PolicyStop(bool alarmed) => Alarmed = alarmed;
        public bool Alarmed { get; }
    }

    private readonly IBoardBridge _bridge;
    private readonly Action<string> _log;
    private readonly int _screenW, _screenH;
    private readonly string? _toolkitDir;
    private readonly Func<bool> _picoPresent;   // v0.9.46 — transport-aware per-step keyboard routing
    private static readonly Random Rng = new();
    /// <summary>v0.9.15 — guards the shared RNG and pause planner while a Parallel Group runs
    /// branches on threadpool threads (Random is not thread-safe).</summary>
    private readonly object _rngLock = new();
    /// <summary>v0.9.15 — >0 while inside a Parallel Group: mouse paths switch to app-paced
    /// per-point streaming and keyboard chunks shrink, so branches interleave on one board.</summary>
    private int _parallelDepth;
    /// <summary>v0.9.20 — app-side cursor anchor: the single source of truth for where the
    /// cursor logically IS. Parallel branches used to re-read the OS cursor while sibling
    /// commands were still queued on the board, so the next path was planned from a stale
    /// point and the cursor visibly jumped back to where the previous move began. Updated on
    /// every mouse-impacting command we issue; re-synced from the real cursor at run start.</summary>
    private System.Drawing.Point? _mouseAnchor;
    /// <summary>v0.9.23 — shared typing-activity signal between Parallel Group branches.
    /// The keyboard branch notes every keystroke; the mouse branch reads it to slow down
    /// and take irregular micro-rests while a human would be typing two-handed.</summary>
    private long _parallelKeySeq;
    private long _lastParallelKeyTick;
    private int _mouseRestAtSeq = -1;
    private Func<CancellationToken, Task>? _pauseCheck;
    /// <summary>v0.9.0 — run-scoped human pause manager (every-N-moves long breaks span steps and repeats).</summary>
    private HumanMouse.PausePlanner? _mousePauses;
    private HumanMouse.PausePlanner MousePauses => _mousePauses ??= new HumanMouse.PausePlanner(Rng);
    /// <summary>v0.9.0 — live playAudio players, stopped+closed when the run ends (they used to leak).</summary>
    private readonly List<StepNode> _steps = new();
    /// <summary>v0.9.33b — one live playback: WaveOutEvent + its reader + loop/stop flags, so
    /// loop replay works again and StopAllAudio never resurrects a loop (NAudio raises
    /// PlaybackStopped on Stop() too).</summary>
    private sealed class LoopableOutput
    {
        public WaveOutEvent Out = null!;
        public AudioFileReader Reader = null!;
        public bool Loop;
        public bool StopRequested;
    }

    private readonly List<LoopableOutput> _audio = new();

    /// <summary>Master-Script guard (§9.5/§17.5): normalized paths currently on the call stack.</summary>
    private readonly HashSet<string> _scriptStack = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxScriptDepth = 4;

    public RunEngine(IBoardBridge bridge, Action<string> log, int screenW, int screenH, string? toolkitDir = null,
                     Func<bool>? picoPresent = null)
    {
        _bridge = bridge;
        _log = log;
        _screenW = screenW;
        _screenH = screenH;
        _toolkitDir = toolkitDir;
        _picoPresent = picoPresent ?? (() => false);
    }

    /// <summary>Optional pause-check delegate injected by MainViewModel.</summary>
    public void SetPauseCheck(Func<CancellationToken, Task> pauseCheck) => _pauseCheck = pauseCheck;
    /// <summary>Human-like mouse speed range (ms) injected by MainViewModel. 0 = disabled.</summary>
    public void SetMouseSpeedRange(int min, int max) { _mouseSpeedMin = min; _mouseSpeedMax = max; }
    private int _mouseSpeedMin, _mouseSpeedMax;

    public async Task RunAsync(IEnumerable<StepNode> roots, CancellationToken ct)
    {
        _mouseAnchor = System.Windows.Forms.Cursor.Position;   // v0.9.20 — anchor syncs at run start
        _parallelKeySeq = 0; _lastParallelKeyTick = 0; _mouseRestAtSeq = -1;   // v0.9.23 — reset typing signal
        await Send($"SETRES|{_screenW},{_screenH}", ct);
        // v0.9.28 — prime the vision hot path (tier-1 JIT + pooled allocators) BEFORE the first
        // findImage: a cold first entireScreen poll round took ~15 s once, so playAudio fired ~15 s
        // after the image was already visible. One small synthetic match here makes round 1 fast.
        VisionService.Warmup();
        try
        {
            await RunStepsAsync(roots, ct);
        }
        catch (GotoSignal g)   // v0.8.3 — the jump found no label at ANY level: stop cleanly with a tip
        {
            _log($"⚠ go to label \"{g.Label}\" — label not found; script stopped");
        }
    }

    /// <summary>v0.8.3 — cross-level "go to label" signal: unwinds RunStepsAsync levels
    /// until a level whose list contains the label catches it and continues there.</summary>
    public sealed class GotoSignal : Exception
    {
        public string Label { get; }
        public GotoSignal(string label) => Label = label;
    }

    /// <summary>v0.8.3 — row index of the first "label" step with this name in the list
    /// (case-insensitive). Labels are markers: a DISABLED label is still a valid target —
    /// execution lands on it, the disabled-check passes through, and the run continues.</summary>
    public static int FindLabelRow(IReadOnlyList<StepNode> list, string label)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s.Type == "label"
                && string.Equals(PropEx.GetString(s.Props, "label"), label, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    public async Task RunStepsAsync(IEnumerable<StepNode> steps, CancellationToken ct)
    {
        var list = steps as IReadOnlyList<StepNode> ?? steps.ToList();   // v0.7.9 — indexed (Else-branch lookup needs the sibling position)
        int si = 0;   // v0.8.3 — manual index so "gotoLabel" can jump within this sibling list
        while (si < list.Count)
        {
            var s = list[si];
            ct.ThrowIfCancellationRequested();
            // Pause/resume gate — injected by MainViewModel; waits until Unpause is called
            if (_pauseCheck is not null) await _pauseCheck(ct);
            if (s.IsDisabled) { _log($"skip (disabled): {s.Summary}"); si++; continue; }
            _log("▶ " + s.Summary);

            try   // v0.8.3 — gotoLabel unwinds via GotoSignal to the level that owns the label
            {
            switch (s.Type)
            {
                case "label":
                    // v0.8.3 — inert marker: landing here just passes through to the next step
                    break;

                case "gotoLabel":
                {
                    // v0.8.3 — jump: unwind via GotoSignal; the level whose list holds the
                    // label catches it and continues there (forward AND backward jumps).
                    var target = PropEx.GetString(s.Props, "label").Trim();
                    if (target.Length == 0) { _log("go to label: no label set — skipped"); break; }
                    throw new GotoSignal(target);
                }

                case "delay":
                    await DelayRandom(PropEx.GetInt(s.Props, "minMs"), PropEx.GetInt(s.Props, "maxMs", 333), ct);
                    break;

                case "forLoop":
                    await RunLoopAsync(s, ct);
                    break;

                case "randomPackage":
                    await RunRandomPackageAsync(s, ct);
                    break;

                case "parallelGroup":
                    await RunParallelGroupAsync(s, ct);
                    break;

                case "comment":
                    _log("# " + PropEx.GetString(s.Props, "text"));
                    break;

                case "raiseError":
                {
                    var message = PropEx.GetString(s.Props, "message", "A deliberate error stopped the macro.").Trim();
                    if (message.Length == 0) message = "A deliberate error stopped the macro.";
                    var error = new InvalidOperationException(message);
                    ErrorPolicyBootstrap.Handle(error, "step", s.Name,
                        () => Send("BEEP|880,180", ct, quiet: true), forceAlarm: true);
                    _log("⛔ error step: " + message + " — stopping immediately with alarm");
                    throw new PolicyStop(alarmed: true);
                }

                case "findImage":
                {
                    // v0.7.9 + v0.9.30 — If/Else for Find Image (§3.3.1 completed): found → children
                    // (Then). Not found → the Else branch. The FLAT "Else…"…"End If" comment markers
                    // are now structural: flat steps between them are the Else body (skipped when
                    // found). The Else comment's own nested children still run too (v0.7.9 style).
                    // An Else without a closing End If stays legacy (non-structural).
                    bool found = await RunFindImageAsync(s, ct);
                    if (!PropEx.GetBool(s.Props, "insertIfElse")) break;
                    var (elseIdx, endIfIdx) = LocateElseBlock(list, si);
                    if (found)
                    {
                        await RunStepsAsync(s.Children, ct);   // Then branch
                        if (elseIdx > si && endIfIdx > elseIdx) si = endIfIdx;   // skip Else body + markers
                    }
                    else if (elseIdx > si && endIfIdx > elseIdx)
                    {
                        _log("find image: Else branch (picture not found)");
                        if (list[elseIdx].Children.Count > 0)
                            await RunStepsAsync(list[elseIdx].Children, ct);   // v0.7.9 nested style
                        var flatBody = new List<StepNode>();   // v0.9.30 — flat style: siblings between the markers
                        for (int k = elseIdx + 1; k < endIfIdx; k++) flatBody.Add(list[k]);
                        if (flatBody.Count > 0) await RunStepsAsync(flatBody, ct);
                        si = endIfIdx;
                    }
                    else
                    {
                        var els = FindElseBranch(list, si);   // unterminated Else → legacy nested-only
                        if (els is not null)
                        {
                            _log("find image: Else branch (picture not found)");
                            await RunStepsAsync(els.Children, ct);
                        }
                    }
                    break;
                }

                case "waitForLight":
                {
                    // v0.9.39 - BH1750 light-range gate (Pico I2C). Same structural If/Else as
                    // waitForSound: stable range -> children (Then); nothing within the window ->
                    // the flat Else block between the markers.
                    bool lit = await RunWaitForLightAsync(s, ct);
                    if (!PropEx.GetBool(s.Props, "insertIfElse")) break;
                    var (lightElseIdx, lightEndIfIdx) = LocateElseBlock(list, si);
                    if (lit)
                    {
                        await RunStepsAsync(s.Children, ct);   // Then branch - the range matched
                        if (lightElseIdx > si && lightEndIfIdx > lightElseIdx) si = lightEndIfIdx;
                    }
                    else if (lightElseIdx > si && lightEndIfIdx > lightElseIdx)
                    {
                        _log("wait for light: Else branch (range not stable in time)");
                        if (list[lightElseIdx].Children.Count > 0)
                            await RunStepsAsync(list[lightElseIdx].Children, ct);
                        var lightBody = new List<StepNode>();
                        for (int k = lightElseIdx + 1; k < lightEndIfIdx; k++) lightBody.Add(list[k]);
                        if (lightBody.Count > 0) await RunStepsAsync(lightBody, ct);
                        si = lightEndIfIdx;
                    }
                    break;
                }

                case "waitForSound":
                {
                    // v0.9.31 — If/Else for sound (same structural machinery as findImage, v0.9.30):
                    // heard → children (Then); not heard → the Else block. No legacy fallback needed
                    // (waitForSound never had If/Else before).
                    bool heard = await RunWaitForSoundAsync(s, ct);
                    if (!PropEx.GetBool(s.Props, "insertIfElse")) break;
                    var (elseIdx, endIfIdx) = LocateElseBlock(list, si);
                    if (heard)
                    {
                        await RunStepsAsync(s.Children, ct);   // Then branch — the sound fired
                        if (elseIdx > si && endIfIdx > elseIdx) si = endIfIdx;   // skip the Else block
                    }
                    else if (elseIdx > si && endIfIdx > elseIdx)
                    {
                        _log("wait for sound: Else branch (not heard in time)");
                        if (list[elseIdx].Children.Count > 0)
                            await RunStepsAsync(list[elseIdx].Children, ct);
                        var flatBody = new List<StepNode>();
                        for (int k = elseIdx + 1; k < endIfIdx; k++) flatBody.Add(list[k]);
                        if (flatBody.Count > 0) await RunStepsAsync(flatBody, ct);
                        si = endIfIdx;
                    }
                    break;
                }

                case "splashListener":
                    _log("Splash Listener is executed only by the portable Pico runtime; use Export to Pico.");
                    break;

                case "openFile":
                {
                    var p = PropEx.GetString(s.Props, "path");
                    if (string.IsNullOrWhiteSpace(p)) { _log("openFile: empty path — skipped"); break; }
                    if (!File.Exists(p)) { _log($"openFile: not found: {p} — skipped"); break; }
                    // v0.7.9 — restored openFile Warden-hardening (docs/openfile-hardening.md):
                    // path stays out of the log (md5-tagged name only) + human-like launch delay
                    string tag = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(p)))[..6];
                    _log($"openFile: {Path.GetFileName(p)} [{tag}]");
                    var psi = new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true };
                    var args = PropEx.GetString(s.Props, "args");
                    if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
                    psi.WindowStyle = PropEx.GetString(s.Props, "windowState", "normal") switch
                    {
                        "maximized" => System.Diagnostics.ProcessWindowStyle.Maximized,
                        "minimized" => System.Diagnostics.ProcessWindowStyle.Minimized,
                        _ => System.Diagnostics.ProcessWindowStyle.Normal,
                    };
                    await Task.Delay(Rng.Next(200, 800), ct);   // human-like launch delay (restored v0.7.9)
                    System.Diagnostics.Process.Start(psi);
                    break;
                }

                case "playAudio":
                {
                    var audioPath = PropEx.GetString(s.Props, "path");
                    if (string.IsNullOrWhiteSpace(audioPath)) { _log("playAudio: empty path — skipped"); break; }
                    if (!File.Exists(audioPath)) { _log($"playAudio: not found: {audioPath} — skipped"); break; }
                    bool loop = PropEx.GetBool(s.Props, "loop");
                    // v0.9.33 — outputDevice index (0 = default, -1 = use default). NAudio gives per-device control.
                    int deviceIndex = PropEx.GetInt(s.Props, "outputDevice", -1);
                    string deviceName = deviceIndex >= 0
                        ? $"[device #{deviceIndex}]"
                        : "";
                    _log($"playAudio: {Path.GetFileName(audioPath)}{deviceName}{(loop ? " (loop)" : "")}");
                    try
                    {
                        // v0.9.0 bug fixes: the old finally closed NON-loop players immediately
                        // (the sound never actually played) and loop players were never stopped
                        // (they kept looping after Stop). Players are now tracked on the engine
                        // and stopped by StopAllAudio() when the run ends; non-loop players
                        // close themselves on MediaEnded.
                        // v0.9.27 — MediaPlayer has thread affinity to a PUMPED Dispatcher thread.
                        // Sequential steps resume on the UI thread (await captures the WPF sync
                        // context), but Parallel Group branches run on Task.Run thread-pool threads
                        // with no pump: Open/Play stalled for seconds (user-recorded 5–10 s startup
                        // delay) and MediaEnded never fired (loop replay broken; non-loop players
                        // never self-closed). All player ops now run on the UI dispatcher.
                        // v0.9.33 — NAudio WaveOutEvent with per-step outputDevice index.
                        // v0.9.33b — clamp the device index (out-of-range must not kill the step)
                        // All playback ops must run on the UI dispatcher (parallel branches have no pump).
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            int dev = Math.Max(0, deviceIndex);
                            if (dev >= WaveOut.DeviceCount)
                            {
                                _log($"playAudio: device #{dev} not present ({WaveOut.DeviceCount} device(s)) — using default");
                                dev = 0;
                            }
                            var item = new LoopableOutput
                            {
                                Out = new WaveOutEvent { DeviceNumber = dev },
                                Reader = new AudioFileReader(audioPath),
                                Loop = loop,
                            };
                            // v0.9.33b — handler for BOTH modes: loop rewinds + replays (the NAudio
                            // port had dropped this → loop=true played only once); never after StopAllAudio.
                            item.Out.PlaybackStopped += (_, _) =>
                            {
                                lock (_audio)
                                {
                                    try
                                    {
                                        if (item.Loop && !item.StopRequested) { item.Reader.Position = 0; item.Out.Play(); return; }
                                        item.Out.Dispose(); item.Reader.Dispose();
                                    }
                                    catch { }
                                    _audio.Remove(item);
                                }
                            };
                            lock (_audio)
                            {
                                _audio.Add(item);
                                item.Out.Init(item.Reader);
                            }
                            item.Out.Play();
                        });
                    }
                    catch (Exception ex) { _log($"playAudio failed: {ex.Message}"); }
                    break;
                }

                case "runExe":
                {
                    // Same as openFile but with pink colorKey (StepPackageBrush) and a cleaner
                    // implementation (no MD5 tagging needed — runExe is the new explicit API).
                    var p = PropEx.GetString(s.Props, "path");
                    if (string.IsNullOrWhiteSpace(p)) { _log("runExe: empty path — skipped"); break; }
                    if (!File.Exists(p)) { _log($"runExe: not found: {p} — skipped"); break; }
                    _log($"runExe: {Path.GetFileName(p)}");
                    var psi = new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true };
                    var argsEx = PropEx.GetString(s.Props, "args");
                    if (!string.IsNullOrWhiteSpace(argsEx)) psi.Arguments = argsEx;
                    psi.WindowStyle = PropEx.GetString(s.Props, "windowState", "normal") switch
                    {
                        "maximized" => System.Diagnostics.ProcessWindowStyle.Maximized,
                        "minimized" => System.Diagnostics.ProcessWindowStyle.Minimized,
                        _ => System.Diagnostics.ProcessWindowStyle.Normal,
                    };
                    await Task.Delay(Rng.Next(200, 800), ct);   // human-like launch delay
                    System.Diagnostics.Process.Start(psi);
                    break;
                }

                case "playScript":
                {
                    var p = PropEx.GetString(s.Props, "path");
                    if (string.IsNullOrWhiteSpace(p)) { _log("playScript: empty path — skipped"); break; }
                    var norm = NormPath(p);
                    if (_scriptStack.Contains(norm))
                    {
                        _log($"⟲ call cycle: {p} already running — skipped (§17.5)");
                        break;
                    }
                    if (_scriptStack.Count >= MaxScriptDepth)
                    {
                        _log($"⏹ max call depth ({MaxScriptDepth}) — {p} not opened");
                        break;
                    }
                    // v0.9.32 — native .amsj scripts run without toolkit; .amk still needs it
                    if (p.EndsWith(".amsj", StringComparison.OrdinalIgnoreCase))
                    {
                        _log("play script (amsj): " + p);
                        try
                        {
                            _scriptStack.Add(norm);
                            var roots = DocumentService.Load(p);
                            await RunStepsAsync(roots, ct);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (GotoSignal) { throw; }
                        catch (Exception ex) { _log("playScript failed: " + ex.Message); }
                        finally { _scriptStack.Remove(norm); }
                        break;
                    }
                    if (_toolkitDir is null)
                    {
                        _log("playScript: no AMK toolkit folder — set it in Tools → Options");
                        break;
                    }
                    _log("play script: " + p);
                    try
                    {
                        _scriptStack.Add(norm);
                        var res = await Task.Run(() => AmkImporter.Import(p, _toolkitDir), ct);
                        foreach (var w in res.Warnings) _log("import note: " + w);
                        await RunStepsAsync(res.Roots, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (GotoSignal) { throw; }   // v0.8.3 — a goto must escape the called script and resolve at an outer level
                    catch (Exception ex) { _log("playScript failed: " + ex.Message); }
                    finally { _scriptStack.Remove(norm); }
                    break;
                }

                case "randomMousePosition":
                {
                    // v0.9.0 — full humanization replaces the two-segment curve (v0.8.5):
                    //  • path starts from the REAL cursor position (the old curve was computed
                    //    from the region's top-left corner — a wrong-origin bug)
                    //  • WindMouse trail (gravity + wind, ease-in-out timing, overshoot & correct)
                    //  • complete pause management: reaction / hesitation / settle pauses and a
                    //    long 0–5000 ms "distraction" break every N moves — all step fields
                    var cfg = HumanMouse.Config.FromProps(s.Props, _mouseSpeedMin, _mouseSpeedMax);
                    var intent = PropEx.GetString(s.Props, "motionIntent", "targetRegion");
                    var (x, y, w, h) = (PropEx.GetInt(s.Props, "x"), PropEx.GetInt(s.Props, "y"),
                                        Math.Max(1, PropEx.GetInt(s.Props, "w", 100)), Math.Max(1, PropEx.GetInt(s.Props, "h", 100)));
                    int destX, destY;
                    if (intent is "microTwitch" or "mediumTwitch")
                    {
                        var current = System.Windows.Forms.Cursor.Position;
                        var defaultMin = intent == "microTwitch" ? 2 : 20;
                        var defaultMax = intent == "microTwitch" ? 12 : 80;
                        var r0 = Math.Max(1, PropEx.GetInt(s.Props, "twitchMinPx", defaultMin));
                        var r1 = Math.Max(r0, PropEx.GetInt(s.Props, "twitchMaxPx", defaultMax));
                        lock (_rngLock)
                        {
                            var radius = Rng.Next(r0, r1 + 1);
                            var angle = Rng.NextDouble() * Math.PI * 2;
                            destX = current.X + (int)Math.Round(Math.Cos(angle) * radius);
                            destY = current.Y + (int)Math.Round(Math.Sin(angle) * radius);
                        }
                    }
                    else lock (_rngLock)
                    {
                        destX = x + Rng.Next(w); destY = y + Rng.Next(h);
                    }   // v0.9.15 — parallel-safe
                    await HumanMoveToAsync(destX, destY, cfg, ct);
                    break;
                }

                case "mouseMove":
                {
                    // handSample is a relative gesture from the cursor's current position.
                    // There is deliberately no absolute destination: a bare HID device cannot
                    // know the host cursor origin without a Windows-side bridge.
                    if (PropEx.GetString(s.Props, "moveMode", "fixed") == "handSample"
                        && HandMovementSample.TryDecode(PropEx.GetString(s.Props, "handSample"), out var handSample))
                    {
                        await ReplayHandMovementAsync(handSample, ct);
                    }
                    else if (PropEx.GetBool(s.Props, "human", true))
                    {
                        // v0.9.14 — tunable per step (dialog fields, Gentle defaults) instead of
                        // the fixed preset; every cursor move in the app is now configurable.
                        await HumanMoveToAsync(PropEx.GetInt(s.Props, "x", 600), PropEx.GetInt(s.Props, "y", 497),
                                               HumanMouse.Config.FromProps(s.Props, _mouseSpeedMin, _mouseSpeedMax, gentleDefaults: true), ct);
                    }
                    else
                    {
                        string rawCmd = StepDefinitions.GetCommands(s)[0];
                        await Send(rawCmd, ct);
                        // v0.9.20 — keep the cursor anchor in sync (format: MMOVE|x,y,abs,h)
                        var parts = rawCmd.Split('|', ',');
                        if (parts.Length >= 3 && int.TryParse(parts[1], out int ax) && int.TryParse(parts[2], out int ay))
                            _mouseAnchor = new System.Drawing.Point(ax, ay);
                    }
                    break;
                }

                default:
                {
                    bool secret = s.Type == "typeText" && PropEx.GetBool(s.Props, "secret");
                    foreach (var cmd in StepDefinitions.GetCommands(s))
                    {
                        // v0.9.46 — route only board-bound keyboard commands. DLY/CLIPBOARD remain PC-local.
                        string wireCmd = StepDefinitions.RouteKeyboardCommand(s, cmd);
                        if (cmd.StartsWith("CLIPBOARD:"))
                        {
                            var text = cmd["CLIPBOARD:".Length..];
                            _log("→ CLIPBOARD ← " + (secret ? Mask(text) : Truncate(text)));
                            await System.Windows.Application.Current.Dispatcher
                                .InvokeAsync(() => System.Windows.Clipboard.SetText(text));
                            await Send(StepDefinitions.RouteKeyboardCommand(s, "KCOMBO|162+86"), ct);   // Ctrl+V on this step's board
                        }
                        else if (cmd.StartsWith("DLY|"))
                        {
                            int ms = int.Parse(cmd["DLY|".Length..]);
                            await DelayRandom(ms, ms, ct);
                        }
                        else if (_parallelDepth > 0 && cmd.StartsWith("KTEXT|"))
                        {
                            // v0.9.21 — INSTANT ops (KTEXT|0,0 per char) + ALL pacing app-side.
                            // v0.9.20's 1-char ops kept the original hmin,hmax — but the firmware
                            // applies the per-key delay board-side even to single-char payloads
                            // (the recorded run shows a 408 ms median key cadence ≈ board ~190 ms
                            // + app ~190 ms: typing ran at half speed), and every op monopolized
                            // the single serial channel for that delay, micro-freezing the mouse.
                            // Now each board op takes ~10 ms and the app owns the cadence, so
                            // mouse waypoints flow between keystrokes. (Recorded human baseline:
                            // median 18 ms mouse cadence, ≤1 s mouse rest while typing.)
                            int p1 = cmd.IndexOf(','); int p2 = cmd.IndexOf(',', p1 + 1);
                            int hMin = int.Parse(cmd[6..p1]), hMax = int.Parse(cmd[(p1 + 1)..p2]);
                            foreach (var piece in ChunkKtextForParallel(cmd))
                            {
                                await Send(StepDefinitions.RouteKeyboardCommand(s, piece), ct,
                                           logAs: secret ? MaskKtext(piece) : null);
                                // v0.9.23 — publish the keystroke so the mouse branch can feel it
                                Interlocked.Increment(ref _parallelKeySeq);
                                Interlocked.Exchange(ref _lastParallelKeyTick, Environment.TickCount64);
                                await PausableDelay(NextRandom(hMin, hMax), ct);
                            }
                        }
                        else
                        {
                            // v0.9.61 — sync the board's tracked cursor to the REAL cursor before
                            // button/wheel actions: HID-Project's AbsoluteMouse resends its stored
                            // axes on every button/wheel report, and those axes boot to (0,0) =
                            // screen centre, so a click/scroll without a prior board-driven move
                            // landed in the middle of the monitor. The board cannot read the cursor
                            // back; only the app knows it. Instant abs move = invisible (the cursor
                            // is already there). Arm fw 1.8 carries the same fix board-side.
                            if (cmd.StartsWith("MCLICK|") || cmd.StartsWith("MWHEEL|")
                                || cmd.StartsWith("MDOWN|") || cmd.StartsWith("MUP|"))
                            {
                                var cur = System.Windows.Forms.Cursor.Position;
                                await Send($"MMOVE|{cur.X},{cur.Y},abs,0", ct, quiet: true);
                                _mouseAnchor = cur;
                            }
                            await Send(wireCmd, ct,
                                logAs: secret && cmd.StartsWith("KTEXT|") ? MaskKtext(cmd) : null);
                        }
                    }
                    break;
                }
            }
            }
            catch (GotoSignal g)
            {
                // v0.8.3 — jump-target search: THIS sibling list first; if the label is not
                // here, the signal propagates to the outer level (loop body → loop's list → …).
                int targetRow = FindLabelRow(list, g.Label);
                if (targetRow < 0) throw;
                _log($"→ go to label \"{g.Label}\"");
                si = targetRow;   // land ON the label row (inert) — the loop then advances
                continue;
            }
            catch (OperationCanceledException) { throw; }
            catch (SilentStop) { throw; }   // stopSilent stays silent (§3.3.1 group E)
            catch (Exception ex)   // v0.9.30 — a failing step names itself in the log before the run stops
            {
                _log($"❌ step failed [{s.Type}]: {ex.Message}");
                throw;
            }

            await DelayAfterStepAsync(s, ct);
            si++;
        }
    }

    // ─────────────────────────── find image (PC-side, §3.3) ───────────────────────────

    private async Task<bool> RunFindImageAsync(StepNode s, CancellationToken ct)   // v0.7.9 — returns found (If/Else in caller)
    {
        var paths = PropEx.GetString(s.Props, "pictures")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int similarity = PropEx.GetInt(s.Props, "similarity", 75);
        string scope = PropEx.GetString(s.Props, "searchScope", "entireScreen");
        System.Drawing.Rectangle? region = scope == "region"
            ? new System.Drawing.Rectangle(PropEx.GetInt(s.Props, "x"), PropEx.GetInt(s.Props, "y"),
                                           PropEx.GetInt(s.Props, "w", 400), PropEx.GetInt(s.Props, "h", 300))
            : null;
        bool neverTimeout = PropEx.GetBool(s.Props, "neverTimeout");
        int timeoutMs = (int)(PropEx.GetInt(s.Props, "timeoutValue", 3)
            * UnitMs(PropEx.GetString(s.Props, "timeoutUnit", "second")));

        _log($"find image: {paths.Length} picture(s) · {similarity}% · scope {scope}");
        var hit = await Task.Run(
            () => VisionService.FindOnScreen(paths, similarity, scope, region, timeoutMs, neverTimeout, _log, ct), ct);

        if (hit is not null)
        {
            _log($"find image: found at {hit.Value.X},{hit.Value.Y}");
            // v0.9.14 — the approach move is tunable per step (same human engine as Random Mouse
            // Position, Gentle defaults so old files behave as before); humanMove off = instant jump.
            bool humanApproach = PropEx.GetBool(s.Props, "humanMove", true);
            var mouseCfg = humanApproach
                ? HumanMouse.Config.FromProps(s.Props, _mouseSpeedMin, _mouseSpeedMax, gentleDefaults: true)
                : null;
            Task ApproachAsync(int px, int py) => mouseCfg is not null
                ? HumanMoveToAsync(px, py, mouseCfg, ct)
                : SendMmoveAbsAsync(px, py, ct);
            switch (PropEx.GetString(s.Props, "onFound", "moveAndClick"))
            {
                case "moveAndClick":
                    await ApproachAsync(hit.Value.X, hit.Value.Y);
                    await Send("MCLICK|left,1", ct);
                    break;
                case "moveOnly":
                    await ApproachAsync(hit.Value.X, hit.Value.Y);
                    break;
                case "clickRestoreCursor":   // AMK's "click but do not move" → option B (§3.3.1)
                {
                    var prev = _mouseAnchor ?? System.Windows.Forms.Cursor.Position;   // v0.9.20 — logical cursor, not a racy read
                    await ApproachAsync(hit.Value.X, hit.Value.Y);
                    await Send("MCLICK|left,1", ct);
                    await ApproachAsync(prev.X, prev.Y);
                    break;
                }
                // "none" → just detection
            }

            // v0.9.0 — the dialog's "Park cursor" box (AMK ATM) was saved but never executed.
            // After the action, park the cursor at the search region's corner so it does not
            // sit on the found element (hover popups / pressed highlight).
            if (PropEx.GetBool(s.Props, "parkCursor") && PropEx.GetString(s.Props, "onFound", "moveAndClick") != "none")
            {
                int parkX = region?.X ?? 8, parkY = region?.Y ?? 8;
                _log($"find image: parking cursor at {parkX},{parkY}");
                await ApproachAsync(parkX, parkY);
            }

            return true;
        }
        else
        {
            _log("find image: not found");
            switch (PropEx.GetString(s.Props, "onTimeout", "continue"))
            {
                case "stopWithTip":
                    throw new InvalidOperationException("Find image timed out — the script was stopped (stop with tip).");
                case "stopSilent":
                    throw new SilentStop();
                // "continue" → fall through
            }
        }
        return false;
    }

    /// <summary>v0.7.9 — the Else branch of a Find Image If/Else: the next "Else…" comment
    /// sibling (its children = the not-found steps; AMK structure §9.2 — also what the
    /// importer produces). The scan stops at "End If" or the first non-comment sibling.</summary>
    public static StepNode? FindElseBranch(IReadOnlyList<StepNode> siblings, int ifIndex)
    {
        for (int j = ifIndex + 1; j < siblings.Count; j++)
        {
            var c = siblings[j];
            if (c.Type != "comment") return null;
            var t = PropEx.GetString(c.Props, "text");
            if (t.StartsWith("Else", StringComparison.Ordinal)) return c;
            if (t == "End If") return null;
        }
        return null;
    }

    /// <summary>v0.9.30 — locates the flat "Else…"…"End If" comment block after a Find Image step
    /// (insertIfElse). The Else marker must be comment-adjacent to the If (v0.7.9 contract); the
    /// block is structural only when an "End If" comment closes it — an unterminated Else stays
    /// legacy. Returns (-1,-1) when there is no structural block. Extracted for TestRunner.</summary>
    public static (int elseIdx, int endIfIdx) LocateElseBlock(IReadOnlyList<StepNode> siblings, int ifIndex)
    {
        int elseIdx = -1;
        for (int j = ifIndex + 1; j < siblings.Count; j++)
        {
            var c = siblings[j];
            if (c.Type != "comment") break;   // the Else marker must follow the If through comments only
            var t = PropEx.GetString(c.Props, "text");
            if (t.StartsWith("Else", StringComparison.Ordinal)) { elseIdx = j; break; }
            if (t == "End If") return (-1, -1);
        }
        if (elseIdx < 0) return (-1, -1);
        for (int j = elseIdx + 1; j < siblings.Count; j++)
        {
            var c = siblings[j];
            if (c.Type == "comment" && PropEx.GetString(c.Props, "text") == "End If")
                return (elseIdx, j);
        }
        return (-1, -1);
    }

    // ─────────────────────────── wait for sound (§14/§16/§17) ───────────────────────────

    /// <summary>v0.9.39 - WLUX/TRGLUX round trip: the board answers once the lux range held stable
    /// for the configured seconds; ERR|TIMEOUT means the window expired (Else branch).</summary>
    private static string? StepTimeoutPolicy(StepNode s)
    {
        var policy = PropEx.GetString(s.Props, "onTimeout", "global");
        return policy is "stopWithAlarm" or "stopQuiet" or "continue" ? policy : null;
    }

    private async Task<bool> RunWaitForLightAsync(StepNode s, CancellationToken ct)
    {
        var cmd = StepDefinitions.GetCommands(s)[0];   // WLUX|lo,hi,stableMs,timeout,mode or TRGLUX|...
        int timeoutMs = PropEx.GetInt(s.Props, "timeoutMs", 20000);
        var reply = await Send(cmd, ct, allowTimeout: true, timeoutSeconds: timeoutMs / 1000.0 + 10, timeoutPolicy: StepTimeoutPolicy(s));
        if (reply.StartsWith("EVT|TRGLUX", StringComparison.Ordinal))
            _log("light trigger fired: " + reply);   // armed board-side keypress happened
        bool matched = !reply.StartsWith("ERR|TIMEOUT", StringComparison.Ordinal);
        if (PropEx.GetBool(s.Props, "insertIfElse"))
            _log(matched ? "wait for light: range matched -> Then branch" : "wait for light: range not matched within the window");
        return matched;
    }

    private async Task<bool> RunWaitForSoundAsync(StepNode s, CancellationToken ct)
    {
        var cmd = StepDefinitions.GetCommands(s)[0];   // WSND|… or TRGSND|… (numeric act, §15.5)
        int timeoutMs = PropEx.GetInt(s.Props, "timeoutMs", 20000);

        // command-proportional timeout: window + board overhead (§15.4 rule 4)
        var reply = await Send(cmd, ct, allowTimeout: true, timeoutSeconds: timeoutMs / 1000.0 + 10, timeoutPolicy: StepTimeoutPolicy(s));
        if (reply.StartsWith("EVT|TRG", StringComparison.Ordinal))
            _log("sound trigger fired: " + reply);   // armed board-side click happened (§14.4)
        // v0.9.31 — the If/Else structure needs the outcome: with allowTimeout an ERR|TIMEOUT
        // reply means the window expired with nothing heard; OK / EVT|TRG mean the sound fired.
        bool heard = !reply.StartsWith("ERR|TIMEOUT", StringComparison.Ordinal);
        if (PropEx.GetBool(s.Props, "insertIfElse"))
            _log(heard ? "wait for sound: heard → Then branch" : "wait for sound: not heard within the window");
        return heard;
    }

    // ─────────────────────────── loops ───────────────────────────

    private async Task RunLoopAsync(StepNode s, CancellationToken ct)
    {
        switch (PropEx.GetString(s.Props, "mode", "count"))
        {
            case "count":
            {
                int count = Math.Max(0, PropEx.GetInt(s.Props, "count", 10));
                for (int i = 1; i <= count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    _log($"loop iteration {i}/{count}");
                    await RunStepsAsync(s.Children, ct);
                }
                break;
            }
            case "time":
            {
                long ms = PropEx.GetInt(s.Props, "timeValue", 10) * UnitMs(PropEx.GetString(s.Props, "timeUnit", "minute"));
                var deadline = Environment.TickCount64 + ms;
                int iter = 0;
                while (Environment.TickCount64 < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    _log($"loop iteration {++iter} (time mode, {(deadline - Environment.TickCount64) / 1000}s left)");
                    await RunStepsAsync(s.Children, ct);
                }
                break;
            }
            default: // infinite — runs until Stop (Shift+F2)
            {
                int iter = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    _log($"loop iteration {++iter} (infinite)");
                    await RunStepsAsync(s.Children, ct);
                }
            }
        }
    }

    // ─────────────────────────── random package (v0.7.8) ───────────────────────────

    /// <summary>
    /// Random Package (user feature request): children are the "buttons". Every pass draws a
    /// FRESH pattern — shuffleAll: all enabled children fire exactly once in a random order;
    /// randomSubset: a random count in [minCount, maxCount] of random children, shuffled.
    /// Disabled children stay out of the pool (§9.5). Each child's own DLY/DelayMax range
    /// still applies between picks (see DelayAfterStepAsync).
    /// </summary>
    private async Task RunRandomPackageAsync(StepNode s, CancellationToken ct)
    {
        List<StepNode> pool;
        lock (_rngLock) pool = PickPackageSteps(s, Rng);   // v0.9.15 — parallel-safe
        if (pool.Count == 0) { _log("random package: no enabled steps — skipped"); return; }
        _log("🎲 random package: " + string.Join(" → ",
            pool.Select(p => string.IsNullOrWhiteSpace(p.Name) ? p.Summary : p.Name)));
        await RunStepsAsync(pool, ct);
    }

    /// <summary>v0.9.15 — Parallel Group: the children run CONCURRENTLY on the same board and the
    /// group completes when the LONGEST branch completes (the next step after the group runs after
    /// the join — the user's requested semantics). One serial channel is shared: the bridge
    /// serializes each individual command, so inside the group the mouse switches to app-paced
    /// per-point streaming and keyboard chunks shrink to 8 chars — typing and mouse movement
    /// visibly interleave instead of blocking each other. Pause/Stop propagate to every branch;
    /// the first fault cancels the rest. Avoid gotoLabel jumps across the group boundary.</summary>
    private async Task RunParallelGroupAsync(StepNode s, CancellationToken ct)
    {
        var children = s.Children.Where(c => !c.IsDisabled).ToList();
        if (children.Count == 0) { _log("parallel group: empty — skipped"); return; }
        if (children.Count == 1) { await RunStepsAsync(children, ct); return; }
        _log($"⚡ parallel group: {children.Count} branches start together (join on the longest)");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _parallelDepth++;
        try
        {
            var tasks = children.Select(c => Task.Run(async () =>
            {
                try { await RunStepsAsync(new[] { c }, linked.Token); }
                catch { linked.Cancel(); throw; }   // first fault stops the sibling branches
            })).ToList();
            await Task.WhenAll(tasks);
            _log("⚡ parallel group: all branches finished (joined on the longest)");
        }
        finally { _parallelDepth--; }
    }

    /// <summary>v0.9.15 — thread-safe inclusive random (Parallel Group branches share Rng).</summary>
    private int NextRandom(int min, int max) { lock (_rngLock) return Rng.Next(min, max + 1); }

    /// <summary>v0.9.21 — split a KTEXT op into one INSTANT op per character ("KTEXT|0,0,&lt;c&gt;",
    /// no board-side per-key delay), so a Parallel Group's keyboard branch holds the single
    /// serial channel for milliseconds instead of a full keystroke delay. The caller re-adds
    /// the human cadence app-side between ops. Extracted for TestRunner.
    /// Fallback if a future firmware floors the 0 delay: per-char KDOWN|vk/KUP|vk pairs
    /// (both are verified firmware-1.6 ops) with the same app-side pacing.</summary>
    public static List<string> ChunkKtextForParallel(string cmd)
    {
        int p1 = cmd.IndexOf(','); int p2 = cmd.IndexOf(',', p1 + 1);
        string payload = cmd[(p2 + 1)..];
        var ops = new List<string>(payload.Length);
        foreach (char c in payload) ops.Add("KTEXT|0,0," + c);
        return ops;
    }

    /// <summary>v0.9.20 — raw absolute MMOVE that also updates the cursor anchor.</summary>
    private async Task<string> SendMmoveAbsAsync(int x, int y, CancellationToken ct)
    {
        var reply = await Send($"MMOVE|{x},{y},abs,0", ct);
        _mouseAnchor = new System.Drawing.Point(x, y);
        return reply;
    }

    /// <summary>v0.9.23 — true while a parallel sibling typed within the last 900 ms.
    /// Extracted for TestRunner.</summary>
    public static bool IsTypingActive(long lastKeyTick, long nowTick)
        => lastKeyTick > 0 && nowTick - lastKeyTick <= 900;

    /// <summary>v0.9.23 — while typing, mouse micro-step delays double (half speed).
    /// v0.9.26 — the doubling is now CAPPED: move-time-stretched tail delays (moveTime up to
    /// 7000 ms in the plan) doubled into multi-second cursor parks on sub-pixel tail waypoints
    /// (recorded: 12.4 s and 11.0 s mid-move freezes while keystrokes flowed normally). The
    /// half-speed feel stays for real micro-steps; no single waypoint delay exceeds the cap.</summary>
    public static int SuppressedMouseDelayMs(int baseDelayMs) => Math.Min(Math.Max(1, baseDelayMs * 2), SuppressedMouseDelayCapMs);

    /// <summary>v0.9.26 — ceiling for a suppressed waypoint delay (ms). Dense hand record:
    /// in-typing mouse gaps p90 = 6 ms, max = 351 ms — a human glide never parks for seconds.</summary>
    public const int SuppressedMouseDelayCapMs = 140;

    private bool TypingActiveNow()
        => _parallelDepth > 0
           && IsTypingActive(Interlocked.Read(ref _lastParallelKeyTick), Environment.TickCount64);

    /// <summary>v0.9.26 — while typing, the mouse rests briefly and OCCASIONALLY: every 10–24
    /// keystrokes, 90–300 ms, ~10% stretch to 350–700 ms. (v0.9.23 rested every 2–6 keystrokes
    /// up to 1200 ms; during a continuous typing burst the 900 ms IsTypingActive window never
    /// lapses, so rests fired back-to-back — the recorded start-stop stutter the user reported.)
    /// App-side and pause-aware; never queues or reorders board commands.</summary>
    private async Task MaybeTypingRestAsync(CancellationToken ct)
    {
        long seq = Interlocked.Read(ref _parallelKeySeq);
        if (_mouseRestAtSeq < 0)
        {
            lock (_rngLock) _mouseRestAtSeq = (int)seq + NextTypingRestEveryKeys(Rng);
        }
        if (seq < _mouseRestAtSeq) return;
        int rest;
        lock (_rngLock)
        {
            rest = TypingRestMs(Rng);
            _mouseRestAtSeq = (int)seq + NextTypingRestEveryKeys(Rng);
        }
        await PausableDelay(rest, ct);
    }

    /// <summary>v0.9.26 — keystrokes between typing-time mouse rests (pure, for TestRunner).</summary>
    public static int NextTypingRestEveryKeys(Random rng) => rng.Next(10, 25);

    /// <summary>v0.9.26 — typing-time mouse rest length (pure, for TestRunner):
    /// 90–300 ms, ~10% stretch to 350–700 ms.</summary>
    public static int TypingRestMs(Random rng) => rng.Next(0, 100) < 10 ? rng.Next(350, 701) : rng.Next(90, 301);

    /// <summary>The pure pick logic, separated for tests: Fisher–Yates shuffle of the
    /// enabled children, then (randomSubset) a random-count prefix in [minCount, maxCount].</summary>
    public static List<StepNode> PickPackageSteps(StepNode s, Random rng)
    {
        var pool = s.Children.Where(c => !c.IsDisabled).ToList();
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        if (PropEx.GetString(s.Props, "mode", "shuffleAll") != "randomSubset") return pool;

        int minC = Math.Max(1, PropEx.GetInt(s.Props, "minCount", 1));
        int maxC = PropEx.GetInt(s.Props, "maxCount", pool.Count);
        if (maxC < minC) (minC, maxC) = (maxC, minC);
        maxC = Math.Min(maxC, pool.Count);
        minC = Math.Min(minC, maxC);
        return pool.Take(rng.Next(minC, maxC + 1)).ToList();
    }

    /// <summary>After-step DLY (§10.3). v0.7.8: when DelayMax &gt; Delay, the wait is a random
    /// value in [Delay, DelayMax] — AMK's RAND+WAIT pattern, per-step humanizing.</summary>
    private async Task DelayAfterStepAsync(StepNode s, CancellationToken ct)
    {
        int min = Math.Max(0, s.Delay), max = Math.Max(min, s.DelayMax);
        if (max <= 0) return;
        int ms = max <= min ? min : Rng.Next(min, max + 1);
        if (max > min) _log($"delay {ms} ms (random {min}–{max})");
        await PausableDelay(ms, ct);   // v0.9.0 — pause-aware (was Task.Delay)
    }

    // ─────────────────────────── humanized mouse movement (v0.9.0) ───────────────────────────

    /// <summary>Plans and executes one human-like move to (tx,ty): reaction pause → WindMouse
    /// waypoints (pause-aware + cancellation-aware per waypoint) → settle pause → the long
    /// every-N-moves break when the planner says one is due.</summary>
    private async Task HumanMoveToAsync(int tx, int ty, HumanMouse.Config cfg, CancellationToken ct)
    {
        // v0.9.20 — plan from the app-side cursor anchor, not the racy OS read: with parallel
        // branches the OS position can lag the commands we have already issued, which made the
        // next path start from a stale point and the cursor visibly jump back mid-run.
        var start = _mouseAnchor ?? System.Windows.Forms.Cursor.Position;   // v0.8.5 — anchor, never the region corner
        HumanMouse.Plan plan;
        lock (_rngLock)   // v0.9.15 — shared RNG + pause planner are not thread-safe (Parallel Group)
            plan = HumanMouse.PlanMove(start.X, start.Y, tx, ty, cfg, MousePauses, Rng, _screenW, _screenH);

        if (plan.BeforeMs > 0) await PausableDelay(plan.BeforeMs, ct);
        int pathMs = 0;
        foreach (var w in plan.Waypoints) pathMs += w.DelayMs;
        _log($"mouse: human move → ({tx},{ty}) · {plan.Waypoints.Count} micro-steps · ~{pathMs} ms" +
             (plan.Arced ? $" · dynamic circular arc {plan.ArcHeightPx}px" : plan.Overshot ? " · overshoot+correct" : "") +
             (plan.CurveMinPct == plan.CurveMaxPct
                 ? $" · curve fixed {plan.CurveMinPct}%"
                 : $" · curve continuously {plan.CurveMinPct}–{plan.CurveMaxPct}%") +
             (plan.TargetMoveMs > 0 ? $" · move-time {plan.TargetMoveMs}ms target" : ""));
        if (_parallelDepth > 0)
        {
            // v0.9.15 — inside a Parallel Group the monolithic send_path would monopolize the bridge
            // worker for the whole path (concurrent typing would freeze). Pace each micro-step
            // app-side; the bridge's one-in-flight lock interleaves these with the other branch's
            // commands — the human-visible effect of typing + mouse wandering at the same time.
            foreach (var w in plan.Waypoints)
            {
                ct.ThrowIfCancellationRequested();
                await Send($"MMOVE|{w.X},{w.Y},abs,0", ct, quiet: true);
                _mouseAnchor = new System.Drawing.Point(w.X, w.Y);   // v0.9.20 — anchor tracks every issued point
                int dly = w.DelayMs;
                // v0.9.23 — two-handed human pattern: while a sibling branch types, the mouse
                // slows to half speed and takes irregular micro-rests (v0.9.21 kept it 100%
                // active; the recorded human mostly rests the mouse during fast typing).
                // v0.9.26 — the doubling is capped (SuppressedMouseDelayCapMs) and rests are rare
                // + brief (every 10–24 keys, at most 700 ms): the dense hand record shows the glide
                // never stalls mid-path; v0.9.23's uncapped doubling + frequent rests caused it.
                if (TypingActiveNow())
                {
                    dly = SuppressedMouseDelayMs(dly);
                    await MaybeTypingRestAsync(ct);
                }
                if (dly > 0) await PausableDelay(dly, ct);
            }
        }
        else
        {
        try
        {
            // v0.9.2 — stream the dense human trail in ONE bridge op (send_path): no per-point
            // serial round-trip, so the cursor glides at true hand cadence (median ~5 ms / ~3 px,
            // measured from the user's own recording) instead of stepping.
            await _bridge.SendPathAsync(
                plan.Waypoints.Select(w => (w.X, w.Y, w.DelayMs)).ToList(), ct);
            _mouseAnchor = new System.Drawing.Point(tx, ty);   // v0.9.20 — path completed to target
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("unknown op"))
        {
            // bridge.py older than v0.9.2 — fall back to firmware-smoothstepped control points
            _log("bridge.py is old (no send_path) — control-point fallback; update bridge/bridge.py next to the exe");
            // v0.9.6 — never replay from the move's ORIGINAL start: that made the cursor visibly
            // jump back to where the move began (user report: "بعد از بعضی حرکت‌ها موس ریست می‌شود
            // به جایی که شروع کرده"). Re-plan the fallback from the cursor's ACTUAL position.
            _mouseAnchor = System.Windows.Forms.Cursor.Position;   // v0.9.20 — path failed: resync from the real cursor
            var now = _mouseAnchor.Value;
            var replanned = HumanMouse.PlanMove(now.X, now.Y, tx, ty, cfg, MousePauses, Rng, _screenW, _screenH);
            foreach (var cp in replanned.ControlPoints)
            {
                ct.ThrowIfCancellationRequested();
                if (_pauseCheck is not null) await _pauseCheck(ct);
                await Send($"MMOVE|{cp.X},{cp.Y},abs,1", ct, quiet: true);
                _mouseAnchor = new System.Drawing.Point(cp.X, cp.Y);   // v0.9.20
                if (cp.DelayMs > 0) await Task.Delay(cp.DelayMs, ct);
            }
        }
        }
        if (plan.AfterMs > 0) await PausableDelay(plan.AfterMs, ct);
        if (plan.LongPauseMs > 0)
        {
            _log($"mouse: idle break {plan.LongPauseMs} ms (human every-N-moves pause)");
            await PausableDelay(plan.LongPauseMs, ct);
        }
    }

    private async Task ReplayHandMovementAsync(HandMovementSample.Sample sample, CancellationToken ct)
    {
        var path = HandMovementSample.Compact(sample.Segments, HandMovementSample.ReplaySegmentLimit);
        var delta = HandMovementSample.Displacement(sample);
        _log($"mouse: relative 10s hand gesture Δ({delta.X},{delta.Y}) · {path.Count} segments");
        foreach (var segment in path)
        {
            ct.ThrowIfCancellationRequested();
            if (_pauseCheck is not null) await _pauseCheck(ct);
            // DelayMs was measured from the previous captured position to this
            // one, so it belongs before the corresponding relative report.
            if (segment.DelayMs > 0) await PausableDelay(segment.DelayMs, ct);
            if (segment.Dx != 0 || segment.Dy != 0)
                await Send($"MMOVE|{segment.Dx},{segment.Dy},rel,2", ct, quiet: true);
        }
        // Windows can report the real post-HID position. Do not invent it by adding raw HID
        // deltas: pointer acceleration means a report delta is not guaranteed to equal pixels.
        _mouseAnchor = System.Windows.Forms.Cursor.Position;
    }

    /// <summary>v0.9.0 — a delay that respects Pause: time spent paused does NOT count down the
    /// remaining delay (previously Pause only applied between steps, never during a wait).</summary>
    private async Task PausableDelay(int ms, CancellationToken ct)
    {
        int remaining = ms;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (_pauseCheck is not null) await _pauseCheck(ct);   // blocks here while paused
            int slice = Math.Min(120, remaining);
            await Task.Delay(slice, ct);
            remaining -= slice;
        }
    }

    /// <summary>v0.9.0 — stop + close every live playAudio player (run end / abort / app exit).</summary>
    public void StopAllAudio()
    {
        // v0.9.27 — players are owned by the UI dispatcher thread; marshal Stop/Close there.
        // Guard: during app shutdown the dispatcher may already be stopping — a synchronous
        // Invoke would hang or throw, so fall back to a best-effort direct sweep.
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.HasShutdownStarted || d.HasShutdownFinished)
        {
            lock (_audio)
            {
                foreach (var p in _audio) { p.StopRequested = true; try { p.Out.Stop(); p.Out.Dispose(); p.Reader.Dispose(); } catch { } }
                _audio.Clear();
            }
            return;
        }
        d.Invoke(() =>
        {
            lock (_audio)
            {
                foreach (var p in _audio) { p.StopRequested = true; try { p.Out.Stop(); p.Out.Dispose(); p.Reader.Dispose(); } catch { } }
                _audio.Clear();
            }
        });
    }

    // ─────────────────────────── helpers ───────────────────────────

    /// <summary>v0.9.46 — explicit per-step keyboard route on the actual transport.
    /// A connected Pico understands the envelope. On a direct Pro Micro connection, KBDARM is
    /// safely unwrapped; asking that direct arm to execute on an absent Pico fails explicitly.</summary>
    public static string ResolveKeyboardRouteForTransport(string command, bool picoPresent)
    {
        const string arm = "KBDARM|", pico = "KBDPICO|";
        if (command.StartsWith(arm, StringComparison.Ordinal))
            return picoPresent ? command : command[arm.Length..];
        if (command.StartsWith(pico, StringComparison.Ordinal))
        {
            if (!picoPresent)
                throw new InvalidOperationException("This keyboard step targets Pico, but the current connection is a direct Pro Micro.");
            return command;
        }
        return command;
    }

    private async Task<string> Send(string cmd, CancellationToken ct, string? logAs = null,
                            bool allowTimeout = false, double? timeoutSeconds = null, bool quiet = false, string? timeoutPolicy = null)
    {
        cmd = ResolveKeyboardRouteForTransport(cmd, _picoPresent());
        if (!quiet) _log("→ " + (logAs ?? cmd));
        var reply = await _bridge.SendAsync(cmd, timeoutSeconds, ct);
        if (!quiet) _log("← " + reply);
        if (reply.StartsWith("ERR|", StringComparison.Ordinal))
        {
            // a sound window expiring is a normal outcome, not a fatal error (§3.3.1 group E)
            if (allowTimeout && reply.StartsWith("ERR|TIMEOUT", StringComparison.Ordinal))
            {
                var policy = timeoutPolicy ?? ErrorPolicyBootstrap.Settings.TimeoutPolicy;
                if (policy == "continue")
                {
                    _log("wait window expired (timeout) — continuing by policy");
                    return reply;
                }
                if (policy == "stopQuiet")
                {
                    _log("wait window expired (timeout) — stopping quietly by policy");
                    throw new PolicyStop(alarmed: false);
                }
                var timeout = new TimeoutException($"Board wait timed out: {cmd}");
                ErrorPolicyBootstrap.Handle(timeout, "timeout");
                _log("wait window expired (timeout) — stopping with alarm by policy");
                throw new PolicyStop(alarmed: true);
            }
            throw new InvalidOperationException($"Board replied {reply} to {cmd}");
        }
        return reply;
    }

    private async Task DelayRandom(int minMs, int maxMs, CancellationToken ct)
    {
        if (maxMs < minMs) (minMs, maxMs) = (maxMs, minMs);
        int ms = maxMs <= minMs ? minMs : NextRandom(minMs, maxMs);   // v0.9.15 — parallel-safe
        _log($"delay {ms} ms");
        if (ms > 0) await PausableDelay(ms, ct);   // v0.9.0 — pause-aware (was Task.Delay)
    }

    private static long UnitMs(string unit) => unit switch
    {
        "second" => 1_000L,
        "minute" => 60_000L,
        "hour" => 3_600_000L,
        _ => 1L,   // "ms"
    };

    private static string NormPath(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    /// <summary>§17.6 mask format: 6**********9 (12 chars — hidden).</summary>
    private static string Mask(string text)
    {
        if (text.Length <= 2) return "** (hidden)";
        return $"{text[0]}{new string('*', text.Length - 2)}{text[^1]} ({text.Length} chars — hidden)";
    }

    private static string MaskKtext(string cmd)
    {
        // KTEXT|hmin,hmax,TEXT → KTEXT|hmin,hmax,<masked>
        int secondPipe = cmd.IndexOf('|', cmd.IndexOf('|') + 1);
        if (secondPipe < 0) return "KTEXT (hidden)";
        return cmd[..(secondPipe + 1)] + Mask(cmd[(secondPipe + 1)..]);
    }

    private static string Truncate(string text)
        => text.Length <= 60 ? text : text[..60] + "…";
}
