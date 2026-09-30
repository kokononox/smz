using System.Collections.ObjectModel;

using System.IO;

using System.Text.Json;

using System.Text.RegularExpressions;

using System.Windows;

using System.Windows.Input;

using Ams.UI.Models;

using Ams.UI.Services;

using Ams.UI.Views;

using CommunityToolkit.Mvvm.ComponentModel;

using CommunityToolkit.Mvvm.Input;



namespace Ams.UI.ViewModels;



public enum ConnectionState { Disconnected, Handshaking, Connected }



public partial class MainViewModel : ObservableObject

{

    public ObservableCollection<StepNode> Steps { get; } = new();

    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>v0.9.43 — serial-port choices for the manual-connect picker (AUTO + real ports).</summary>
    public ObservableCollection<string> PortChoices { get; } = new() { "AUTO" };

    [ObservableProperty] private string _portText = "AUTO";



    /// <summary>Flat pre-order projection of Steps for the AMK-style list (v0.7).

    /// Rebuilt by Renumber() on every structural change — the list always shows every node,

    /// which also fixes the imported/opened tree rendering flat (user report v0.7).</summary>

    public ObservableCollection<FlatStepRow> FlatSteps { get; } = new();



    /// <summary>Current multi-selection (Extended list), set by the view on SelectionChanged.

    /// Empty = fall back to SelectedNode only.</summary>

    public List<StepNode> SelectedNodes { get; set; } = new();



    [ObservableProperty] private StepNode? _selectedNode;

    /// <summary>v0.9.40 - single source of truth for the scope vein overlay: the block whose

    /// accordion toggle was pressed, otherwise the current selection. Cleared as soon as the

    /// selection moves, so a vein never lingers on a stale block.</summary>

    private StepNode? _veinFocusNode;

    public StepNode? ScopeVeinNode => _veinFocusNode ?? SelectedNode;

    public void FocusScopeVein(StepNode? node)

    {

        _veinFocusNode = node;

        OnPropertyChanged(nameof(ScopeVeinNode));

    }

    partial void OnSelectedNodeChanged(StepNode? value)

    {

        if (!ReferenceEquals(_veinFocusNode, value)) _veinFocusNode = null;

        OnPropertyChanged(nameof(ScopeVeinNode));

    }

    [ObservableProperty] private ConnectionState _connection = ConnectionState.Disconnected;

    [ObservableProperty] private string _connectButtonText = "Connect";

    [ObservableProperty] private string _fileText = "untitled";

    [ObservableProperty] private bool _isRunning;

    [ObservableProperty] private bool _isPaused;

    [ObservableProperty] private bool _picoPresent;   // v0.9.44 — the Pico brain answered (status-bar LED)

    [ObservableProperty] private bool _armPresent;    // v0.9.44 — the Pro Micro arm answered (status-bar LED)

    public bool IsNotRunning => !IsRunning;

    public bool IsPausedAndRunning => IsRunning && IsPaused;

    public bool IsNotPaused => !IsPaused;



    private readonly AppSettings _settings;

    private IBoardBridge? _bridge;

    private string? _currentFile;

    private bool _dirty;

    private CancellationTokenSource? _runCts;

    private bool _runActive;   // v0.9.0 — true until the run loop has fully unwound (Stop frees the Run BUTTON instantly, but a second engine must not overlap the first)

    private string? _stepClipboardJson;   // step clipboard for Cut/Copy/Paste (AMK parity)



    // ── v0.7.2 visual structure (user request): scope ranges for the AMK red line ──

    private readonly Dictionary<StepNode, (int Start, int End)> _scopeRanges = new();
    // v0.9.38 — flat row index of each scope head's own Else marker (bend on the bracket).
    private readonly Dictionary<StepNode, int> _scopeElseRows = new();



    /// <summary>v0.8.0 — accordion: collapsed container nodes (session-only; survives Renumber rebuilds).</summary>

    private readonly HashSet<StepNode> _collapsed = new();



    // v0.9.37 — step rows are WHITE. Scope depth is conveyed by the indent and by the ONE red

    // scope bracket overlay, so the pastel scope tints (and their brush helpers) were removed.

    private static readonly System.Windows.Media.Brush Transparent_ = System.Windows.Media.Brushes.Transparent;



    public string StatusText => Connection switch

    {

        ConnectionState.Connected => $"{_bridge?.Port ?? _settings.Port} · FW {_bridge?.FirmwareVersion ?? "?"} · Encrypted",

        ConnectionState.Handshaking => "Handshaking…",

        _ => "Board disconnected",

    };



    public MainViewModel()

    {

        _settings = AppSettings.Load();

        _portText = string.IsNullOrWhiteSpace(_settings.Port) ? "AUTO" : _settings.Port;   // v0.9.43 — the status-bar port pick starts from the saved default

        // v0.9.23 — session typing fallbacks (calibrated via Options → "Measure from my hand")

        StepDefinitions.TypingFallbackMinMs = _settings.TypeKeyMinMs;

        StepDefinitions.TypingFallbackMaxMs = _settings.TypeKeyMaxMs;

        Log("Classroom Studio v0.9.67 — step-by-step board wizard with per-device default board specs filled in for you, an editable board-spec form in step 2, and a port scan that lists only the boards actually attached");

        Log("Insert a step from the Insert menu, the left rail, or the right-click menu — then Connect and Run.");

        StartPortWatcher();   // v0.9.52 - plug the board in later and it still lights up

    }



    partial void OnConnectionChanged(ConnectionState value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsRunningChanged(bool value)

    {

        OnPropertyChanged(nameof(IsNotRunning));

        if (!value) IsPaused = false;   // reset pause on run end

    }

    partial void OnIsPausedChanged(bool value)

    {

        OnPropertyChanged(nameof(IsPausedAndRunning));

        OnPropertyChanged(nameof(IsNotPaused));   // v0.9.0 — was never notified: the Pause button stayed enabled after pausing

    }



    // ═══════════════ logging ═══════════════



    private void Log(string message)

    {

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

        var d = System.Windows.Application.Current?.Dispatcher;

        if (d is not null && !d.CheckAccess()) d.Invoke(() => LogLines.Add(line));
        else LogLines.Add(line);

    }



    [RelayCommand]

    private void ClearLog()

    {

        LogLines.Clear();   // v0.9.43 — the serial log is user-clearable (user request)

        Log("log cleared");

    }



    // v0.9.43 — paired playback hotkeys (user request: 4 keys → 2): one key toggles

    // run/stop, the other toggles pause/resume.

    [RelayCommand]

    private void RunOrStop()

    {

        if (IsRunning) Stop();

        else _ = Run();

    }



    [RelayCommand]

    private void PauseOrResume()

    {

        if (!IsRunning) return;

        if (IsPaused) Resume(); else Pause();

    }



    /// <summary>v0.9.43 — the status-bar port picker wins over the saved default. A picked item may

    /// carry a label after the device ("COM5 — USB Serial") — only the bare device goes to the bridge.

    /// Empty/AUTO means auto-detect. Public + static for TestRunner.</summary>

    public static string PortDeviceFromText(string? text)

    {

        var t = (text ?? "").Trim();

        if (t.Length == 0 || t.Equals("AUTO", StringComparison.OrdinalIgnoreCase)) return "AUTO";

        int dash = t.IndexOf(" — ", StringComparison.Ordinal);

        if (dash > 0) t = t[..dash].Trim();

        return t.Length == 0 ? "AUTO" : t;

    }



    /// <summary>v0.9.44 — which boards answered after connect: the Pico brain advertises

    /// "role=brain" (pico-light firmware); a plain firmware-1.6 board is the Pro Micro arm alone.

    /// Pure + public static for TestRunner.</summary>

    public static (bool Pico, bool Arm) ParseBoardPresence(string? pong)

    {

        var s = pong ?? "";

        bool pico = s.Contains("pico", StringComparison.OrdinalIgnoreCase) || s.Contains("role=brain", StringComparison.OrdinalIgnoreCase);

        bool arm = s.Contains("arm=promicro", StringComparison.OrdinalIgnoreCase) || s.Contains("arm=ok", StringComparison.OrdinalIgnoreCase) || !pico;

        return (pico, arm);

    }



    private string EffectivePort()

    {

        var manual = PortDeviceFromText(PortText);

        if (!manual.Equals("AUTO", StringComparison.OrdinalIgnoreCase)) return manual;

        return string.IsNullOrWhiteSpace(_settings.Port) ? "AUTO" : _settings.Port;

    }



    // ── v0.9.52: hot-plug watcher (pure logic in Services/HotPlugWatcher.cs) ──

    private System.Windows.Threading.DispatcherTimer? _portWatcher;

    private System.Collections.Generic.List<string> _watchedPorts = new();

    private bool _watcherBusy;

    /// <summary>Starts the port watcher so a board plugged in after launch is noticed.</summary>

    private void StartPortWatcher()

    {

        _watchedPorts = HotPlugWatcher.CurrentPorts();

        _portWatcher = new System.Windows.Threading.DispatcherTimer

        {

            Interval = TimeSpan.FromMilliseconds(HotPlugWatcher.PollMs),

        };

        _portWatcher.Tick += OnPortWatcherTick;

        _portWatcher.Start();

    }

    private void OnPortWatcherTick(object? sender, EventArgs e)

    {

        if (_watcherBusy) return;

        _watcherBusy = true;

        try

        {

            var now = HotPlugWatcher.CurrentPorts();

            var arrived = HotPlugWatcher.Added(_watchedPorts, now);

            var left = HotPlugWatcher.Added(now, _watchedPorts);

            _watchedPorts = now;

            if (arrived.Count == 0 && left.Count == 0) return;

            Log(HotPlugWatcher.DescribeChange(arrived, left));

            SyncPortChoices(now);

            var idle = Connection == ConnectionState.Disconnected;

            if (HotPlugWatcher.ShouldAutoConnect(idle, IsRunning, arrived.Count, true))

            {

                Log("hot-plug: board detected on " + string.Join(", ", arrived) + " — connecting automatically");

                if (ConnectCommand.CanExecute(null)) ConnectCommand.Execute(null);

            }

        }

        catch (Exception ex) { Log("port watcher: " + ex.Message); }

        finally { _watcherBusy = false; }

    }

    /// <summary>Refreshes the status-bar port picker without losing a manual pick.</summary>

    private void SyncPortChoices(System.Collections.Generic.List<string> ports)

    {

        var keep = PortText;

        PortChoices.Clear();

        PortChoices.Add("AUTO");

        foreach (var p in ports) PortChoices.Add(p);

        PortText = PortChoices.Contains(keep) ? keep : "AUTO";

    }

    [RelayCommand]

    private async Task RefreshPorts()

    {

        try

        {

            _bridge ??= CreateBridge();

            var ports = await _bridge.ListPortsAsync();

            PortChoices.Clear();

            PortChoices.Add("AUTO");

            foreach (var p in ports) PortChoices.Add(p);

            Log(ports.Count == 0 ? "no serial ports found" : "serial ports: " + string.Join(", ", ports));

        }

        catch (Exception ex) { Log("port scan failed: " + ex.Message); }

    }



    // ═══════════════ Play Options (AMK bottom-panel parity, §9.1) ═══════════════

    // App-side run settings — persisted in ams-settings.json, NEVER in the .amsj file.



    public bool PlayModeOnce { get => _settings.PlayRepeatMode == "once"; set { if (!value) return; _settings.PlayRepeatMode = "once"; SavePlayOpts(); } }

    public bool PlayModeTimes { get => _settings.PlayRepeatMode == "times"; set { if (!value) return; _settings.PlayRepeatMode = "times"; SavePlayOpts(); } }

    public bool PlayModeTimed { get => _settings.PlayRepeatMode == "timed"; set { if (!value) return; _settings.PlayRepeatMode = "timed"; SavePlayOpts(); } }



    public int PlayRepeatTimes { get => _settings.PlayRepeatTimes; set { _settings.PlayRepeatTimes = Math.Max(1, value); _settings.Save(); OnPropertyChanged(); } }

    public int PlayRepeatValue { get => _settings.PlayRepeatValue; set { _settings.PlayRepeatValue = Math.Max(1, value); _settings.Save(); OnPropertyChanged(); } }

    public int PlayRepeatUnitIndex

    {

        get => _settings.PlayRepeatUnit == "second" ? 0 : _settings.PlayRepeatUnit == "hour" ? 2 : 1;

        set { _settings.PlayRepeatUnit = value switch { 0 => "second", 2 => "hour", _ => "minute" }; _settings.Save(); OnPropertyChanged(); }

    }

    public bool ShutdownWhenFinished { get => _settings.ShutdownWhenFinished; set { _settings.ShutdownWhenFinished = value; _settings.Save(); OnPropertyChanged(); } }

    public bool NoActivateWhenStopped { get => _settings.NoActivateWhenStopped; set { _settings.NoActivateWhenStopped = value; _settings.Save(); OnPropertyChanged(); } }






    private void SavePlayOpts()

    {

        _settings.Save();

        OnPropertyChanged(nameof(PlayModeOnce));

        OnPropertyChanged(nameof(PlayModeTimes));

        OnPropertyChanged(nameof(PlayModeTimed));

    }



    /// <summary>"View Execution Log" — writes the serial log to a timestamped file (AMK parity).</summary>

    [RelayCommand]

    private void SaveLog()

    {

        var p = Path.Combine(AppContext.BaseDirectory, $"ams-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        File.WriteAllLines(p, LogLines);

        Log("execution log saved: " + p);

    }



    // ═══════════════ steps: add / edit / delete / move / toggle ═══════════════



    [RelayCommand]

    private void AddStep(string type)

    {

        if (type == RecoveryCallStepDefinitions.CallLaunch && !IsLaunchPipeline)

        {

            Log("add blocked: Run/Call Launch DC Recovery is only valid in the Launch tab");

            return;

        }

        var def = StepDefinitions.Get(type);

        // v0.9.32 — playScript: if current .amsj file exists, prefill path so user just presses OK

        if (type == "playScript" && !string.IsNullOrWhiteSpace(_currentFile))

        {

            var dlgVals = ShowStepDialog("New: " + def.Label, type, null);

            if (dlgVals is null) return;

            var newNode = new StepNode

            {

                Type = type,

                Name = PropEx.GetString(dlgVals, "__name"),

                Delay = PropEx.GetInt(dlgVals, "__delay", def.DefaultDelay),

                DelayMax = PropEx.GetInt(dlgVals, "__delayMax"),

                Props = Strip(dlgVals),

            };

            Snapshot();

            InsertNode(newNode);

            if (type is "findImage" or "waitForSound" or "waitForLight" && PropEx.GetBool(newNode.Props, "insertIfElse")) EnsureElseMarkers(newNode);

            if (OwnsNextMarker(type)) EnsureLoopMarker(newNode);   // v0.9.45/48 — every condition-less child-capable container gets its structural # Next terminator

            MarkDirty();

            Log("added: " + def.Label);

            return;

        }

        var vals = ShowStepDialog("New: " + def.Label, type, null);

        if (vals is null) return;



        var node = new StepNode

        {

            Type = type,

            Name = PropEx.GetString(vals, "__name"),

            Delay = PropEx.GetInt(vals, "__delay", def.DefaultDelay),

            DelayMax = PropEx.GetInt(vals, "__delayMax"),

            Props = Strip(vals),

        };

        // v0.8.4 — seed word-level humanize defaults from app settings on new typeText steps

        if (type == "typeText" && node.Props.ContainsKey("wmin") && _settings.WordDelayMax > _settings.WordDelayMin)

        {

            node.Props["wmin"] = _settings.WordDelayMin;

            node.Props["wmax"] = _settings.WordDelayMax;

        }

        Snapshot();   // v0.9.30 — undoable

        InsertNode(node);

        if (type is "findImage" or "waitForSound" or "waitForLight" && PropEx.GetBool(node.Props, "insertIfElse")) EnsureElseMarkers(node);   // v0.7.9/v0.9.31

        if (OwnsNextMarker(type)) EnsureLoopMarker(node);   // v0.9.45/48

        MarkDirty();

        Log("added: " + def.Label);

    }



    [RelayCommand]

    private void EditSelected()

    {

        if (SelectedNode is null || EditingText()) return;

        var node = SelectedNode;

        if (IsStructuralMarker(node))

        {

            Log("edit blocked: Else/End If is part of its If/Else block");

            return;

        }

        var vals = ShowStepDialog("Edit: " + StepDefinitions.Get(node.Type).Label, node.Type, node);

        if (vals is null) return;



        Snapshot();   // v0.9.30 — undoable (dialog returned OK)

        node.Name = PropEx.GetString(vals, "__name");

        node.Delay = PropEx.GetInt(vals, "__delay", node.Delay);

        node.DelayMax = PropEx.GetInt(vals, "__delayMax", node.DelayMax);

        node.Props = Strip(vals);

        node.RefreshSummary();

        if (node.Type is "findImage" or "waitForSound" or "waitForLight" && PropEx.GetBool(node.Props, "insertIfElse")) EnsureElseMarkers(node);   // v0.7.9/v0.9.31

        if (OwnsNextMarker(node)) EnsureLoopMarker(node);   // v0.9.45/48

        DocumentService.LiftOrphanChildren(Steps);   // v0.9.42 — unchecking Insert If-Else releases the former branch

        Renumber();

        MarkDirty();

    }



    /// <summary>Working set for destructive commands (v0.7): the multi-selection when present

    /// (filtered to topmost nodes — a node whose ancestor is also selected is dropped so

    /// nested content is never processed twice), else just SelectedNode.</summary>

    private List<StepNode> EffectiveSelection()

    {

        var sel = SelectedNodes.Count > 0

            ? SelectedNodes

            : (SelectedNode is null ? new List<StepNode>() : new List<StepNode> { SelectedNode });

        return sel.Where(n => !sel.Any(o => !ReferenceEquals(o, n) && IsAncestorOf(o, n))).ToList();



        static bool IsAncestorOf(StepNode maybeAncestor, StepNode n)

        {

            for (var p = n.Parent; p is not null; p = p.Parent)

                if (ReferenceEquals(p, maybeAncestor)) return true;

            return false;

        }

    }



    /// <summary>v0.9.36/45 — an active If head + Else/End If, or a For Loop + Next,

    /// form one destructive clipboard unit. A marker selected by itself remains locked.</summary>

    private (List<StepNode> Nodes, HashSet<StepNode> OwnedMarkers) ExpandConditionalSelection(List<StepNode> requested)

    {

        var nodes = new List<StepNode>();

        var seen = new HashSet<StepNode>();

        var owned = new HashSet<StepNode>();

        foreach (var item in requested)

        {

            if (seen.Add(item)) nodes.Add(item);

            var list = item.Parent?.Children ?? Steps;

            int i = list.IndexOf(item);

            if (i < 0) continue;

            // v0.9.45/48 — a condition-less container and its immediate # Next marker are one atomic clipboard/

            // destructive unit, exactly like an If head and its Else/End If markers.

            if (OwnsNextMarker(item))   // v0.9.48 — loop, parallel group and random package alike

            {

                if (i + 1 < list.Count && IsNextMarker(list[i + 1]))

                {

                    var next = list[i + 1];

                    owned.Add(next);

                    if (seen.Add(next)) nodes.Add(next);

                }

                continue;

            }

            if (!IsIfElseHead(item)) continue;

            var (elseIndex, endIfIndex) = LocateElseMarkersForUi(list, i);

            if (elseIndex < 0 || endIfIndex <= elseIndex) continue;

            foreach (var marker in new[] { list[elseIndex], list[endIfIndex] })

            {

                owned.Add(marker);

                if (seen.Add(marker)) nodes.Add(marker);   // immediately after its head: stable for multi-block clips

            }

        }

        return (nodes, owned);

    }



    private static bool IsIfElseHead(StepNode n)

        => n.Type is "findImage" or "waitForSound" or "waitForLight" && PropEx.GetBool(n.Props, "insertIfElse");



    [RelayCommand]

    private void DeleteSelected()

    {

        var requested = EffectiveSelection();

        if (requested.Count == 0 || EditingText()) return;

        var (sel, ownedMarkers) = ExpandConditionalSelection(requested);

        // A marker selected alone is locked; selecting its owning If head removes the whole block atomically.

        var deletable = sel.Where(n => !IsStructuralMarker(n) || ownedMarkers.Contains(n)).ToList();

        if (deletable.Count < sel.Count)

        {

            var locked = sel.Count - deletable.Count;

            Log($"delete blocked: {locked} structural marker(s) cannot be removed by themselves");

            if (deletable.Count == 0) return;

        }

        Snapshot();   // v0.9.30 — undoable

        foreach (var n in deletable)

            (n.Parent?.Children ?? Steps).Remove(n);

        SelectedNodes = new();

        SelectedNode = null;

        Renumber();

        MarkDirty();

        Log($"deleted {deletable.Count} step(s)");

    }



    [RelayCommand] private void MoveUp() => MoveSelected(-1);

    [RelayCommand] private void MoveDown() => MoveSelected(1);



    private void MoveSelected(int delta)

    {

        if (SelectedNode is null || EditingText()) return;

        if (IsStructuralMarker(SelectedNode) || IsIfElseHead(SelectedNode))

        {

            Log("move blocked: an If/Else block is atomic — use Cut/Paste to relocate the whole block");

            return;

        }

        var list = SelectedNode.Parent?.Children ?? Steps;

        int i = list.IndexOf(SelectedNode);

        int j = i + delta;

        if (i < 0 || j < 0 || j >= list.Count) return;

        Snapshot();   // v0.9.30 — undoable

        list.Move(i, j);

        Renumber();

        MarkDirty();

    }



    [RelayCommand]

    private void ToggleDisabled()

    {

        var sel = EffectiveSelection();

        if (sel.Count == 0 || EditingText()) return;

        Snapshot();   // v0.9.30 — undoable

        foreach (var n in sel) n.IsDisabled = !n.IsDisabled;   // rows update in place (forwarded)

        Log($"toggled disabled on {sel.Count} step(s)");

        MarkDirty();

    }



    [RelayCommand]

    private void BatchDelays()

    {

        var fields = new FieldDef[]

        {

            new("mode", "Mode", FieldKind.Combo, "multiply", new[] { "set", "multiply" }),

            new("value", "Value (ms for 'set' · percent for 'multiply')", FieldKind.Int, "100"),

        };

        var dlg = new StepDialog("ویرایش گروهی تاخیرها — روی کل درخت اعمال می‌شود", fields, null)

        {

            Owner = System.Windows.Application.Current.MainWindow,

        };

        if (dlg.ShowDialog() != true || dlg.Values is null) return;



        var mode = PropEx.GetString(dlg.Values, "mode");

        var value = PropEx.GetInt(dlg.Values, "value", 100);



        Snapshot();   // v0.9.36 — batch edit is one undoable mutation

        foreach (var n in Steps) Apply(n);

        void Apply(StepNode n)

        {

            n.Delay = mode == "set" ? value : (int)Math.Round(n.Delay * value / 100.0);

            if (n.DelayMax > 0) n.DelayMax = mode == "set" ? value : (int)Math.Round(n.DelayMax * value / 100.0);   // v0.7.8 — keep ranges in sync

            foreach (var c in n.Children) Apply(c);

        }



        MarkDirty();

        Log($"batch delays applied ({mode} {value})");

    }



    // ═══════════════ step clipboard + bulk enable (AMK context-menu parity) ═══════════════



    [RelayCommand]

    private void Cut()

    {

        var requested = EffectiveSelection();

        if (requested.Count == 0 || EditingText()) return;

        var (sel, ownedMarkers) = ExpandConditionalSelection(requested);

        var cuttable = sel.Where(n => !IsStructuralMarker(n) || ownedMarkers.Contains(n)).ToList();

        if (cuttable.Count < sel.Count)

            Log($"cut blocked: {sel.Count - cuttable.Count} structural marker(s) cannot be cut by themselves");

        if (cuttable.Count == 0) return;

        Snapshot();   // v0.9.30 — undoable

        _stepClipboardJson = JsonSerializer.Serialize(cuttable);

        foreach (var n in cuttable)

            (n.Parent?.Children ?? Steps).Remove(n);

        SelectedNodes = new();

        SelectedNode = null;

        Renumber();

        MarkDirty();

        Log($"cut {cuttable.Count} step(s)");

    }



    [RelayCommand]

    private void Copy()

    {

        var requested = EffectiveSelection();

        if (requested.Count == 0 || EditingText()) return;

        var (sel, ownedMarkers) = ExpandConditionalSelection(requested);

        var copyable = sel.Where(n => !IsStructuralMarker(n) || ownedMarkers.Contains(n)).ToList();

        if (copyable.Count < sel.Count)

            Log($"copy blocked: {sel.Count - copyable.Count} structural marker(s) cannot be copied by themselves");

        if (copyable.Count == 0) return;

        _stepClipboardJson = JsonSerializer.Serialize(copyable);

        Log($"copied {copyable.Count} step(s)");

    }



    [RelayCommand]

    private void Paste()

    {

        if (_stepClipboardJson is null || EditingText()) return;

        // Clipboard holds a JSON array; the pre-0.7 single-node object is still accepted.

        var json = _stepClipboardJson.TrimStart();

        var nodes = json.StartsWith("[")

            ? JsonSerializer.Deserialize<List<StepNode>>(json)

            : new List<StepNode> { JsonSerializer.Deserialize<StepNode>(json)! };

        if (nodes is null || nodes.Count == 0) return;

        Snapshot();   // v0.9.30 — undoable



        // Resolve the insertion point ONCE. Calling InsertNode repeatedly with the same selection

        // reversed sibling order and could nest the second item inside the first container.

        var selected = SelectedNode;

        IList<StepNode> targetList;

        StepNode? parent;

        int insertAt;

        if (selected is not null && (StepDefinitions.AcceptsChildren(selected) || IsElseMarker(selected)))   // v0.9.42

        {

            targetList = selected.Children;

            parent = selected;

            insertAt = targetList.Count;

        }

        else if (selected is not null)

        {

            targetList = selected.Parent?.Children ?? Steps;

            parent = selected.Parent;

            insertAt = targetList.IndexOf(selected) + 1;

        }

        else

        {

            targetList = Steps;

            parent = null;

            insertAt = targetList.Count;

        }



        foreach (var n in nodes)

        {

            DocumentService.FixParents(n, parent);

            targetList.Insert(insertAt++, n);   // monotonically increasing: clipboard order is preserved

        }

        foreach (var head in nodes.Where(IsIfElseHead))

            EnsureElseMarkers(head);   // old/single-node clipboards still receive one owned pair

        foreach (var loop in nodes.Where(n => OwnsNextMarker(n)))

            EnsureLoopMarker(loop);   // v0.9.45 — old/single-node clipboards receive # Next too

        Renumber();

        SelectedNode = nodes.FirstOrDefault(IsIfElseHead) ?? nodes[^1];

        MarkDirty();

        Log($"pasted {nodes.Count} step(s)");

    }



    // ═══════════════ v0.9.30 — undo/redo (full-tree JSON snapshots) ═══════════════

    // Trees are small (KBs) — snapshotting the whole tree per mutation is simple and total.

    private readonly List<string> _undo = new();   // stack top = last element

    private readonly List<string> _redo = new();

    private const int UndoCap = 100;



    /// <summary>Push the current tree onto the undo stack and clear redo. Called at the start

    /// of every tree-mutating command (add / edit / delete / cut / paste / move / drag / import / new / open).</summary>

    private void Snapshot()

    {

        _undo.Add(StepTreeSerializer.Snapshot(Steps));

        if (_undo.Count > UndoCap) _undo.RemoveRange(0, _undo.Count - UndoCap);

        _redo.Clear();

    }



    [RelayCommand]

    private void Undo()

    {

        if (_undo.Count == 0 || EditingText()) return;

        _redo.Add(StepTreeSerializer.Snapshot(Steps));

        var json = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);

        RestoreSnapshot(json);

        Log("undo");

    }



    [RelayCommand]

    private void Redo()

    {

        if (_redo.Count == 0 || EditingText()) return;

        _undo.Add(StepTreeSerializer.Snapshot(Steps));

        var json = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);

        RestoreSnapshot(json);

        Log("redo");

    }



    private void RestoreSnapshot(string json)

    {

        Steps.Clear();

        foreach (var n in StepTreeSerializer.Restore(json)) Steps.Add(n);

        SelectedNodes = new();

        SelectedNode = null;

        Renumber();

        MarkDirty();

    }



    [RelayCommand]

    private void DisableAllButThis()

    {

        if (SelectedNode is null) return;

        Snapshot();   // v0.9.36 — bulk enable/disable must be undoable

        SetDisabledAllBut(SelectedNode, othersDisabled: true);

        Log("disabled all but: " + SelectedNode.Summary);

    }



    [RelayCommand]

    private void EnableAllButThis()

    {

        if (SelectedNode is null) return;

        Snapshot();   // v0.9.36 — bulk enable/disable must be undoable

        SetDisabledAllBut(SelectedNode, othersDisabled: false);

        Log("enabled all but: " + SelectedNode.Summary);

    }



    /// <summary>Sets IsDisabled on every step; the selected node, its ancestors and its

    /// descendants get the inverse (so the kept branch can actually run).</summary>

    private void SetDisabledAllBut(StepNode keep, bool othersDisabled)

    {

        var keepSet = new HashSet<StepNode>();

        for (var p = keep; p is not null; p = p.Parent) keepSet.Add(p);

        void Collect(StepNode n) { keepSet.Add(n); foreach (var c in n.Children) Collect(c); }

        Collect(keep);



        void Apply(StepNode n)

        {

            n.IsDisabled = keepSet.Contains(n) ? !othersDisabled : othersDisabled;

            foreach (var c in n.Children) Apply(c);

        }

        foreach (var n in Steps) Apply(n);

        MarkDirty();

    }



    /// <summary>Drag &amp; drop move: target null → root end; mode before/after/into.

    /// "into" only when the target is a container (For Loop / Find Image).</summary>

    public void MoveNode(StepNode node, StepNode? target, string mode)
    {
        if (IsStructuralMarker(node) || IsIfElseHead(node))
        {
            Log("drag blocked: an If/Else block is atomic — use Cut/Paste to relocate the whole block");
            return;
        }
        if (target is not null)
        {
            if (ReferenceEquals(node, target)) return;
            for (var p = target; p is not null; p = p.Parent)
                if (ReferenceEquals(p, node)) return;   // cannot drop into own descendant
        }

        // v0.9.48 — a condition-less scope container and its # Next closing row are ONE draggable
        // unit: the marker can neither be left behind nor separated from its head (user report).
        var moveSet = new List<StepNode> { node };
        if (OwnsNextMarker(node))
        {
            var homeList = node.Parent?.Children ?? Steps;
            int ni = homeList.IndexOf(node);
            if (ni >= 0 && ni + 1 < homeList.Count && IsNextMarker(homeList[ni + 1])) moveSet.Add(homeList[ni + 1]);
        }
        if (target is not null && moveSet.Contains(target)) return;   // dropping onto its own unit = no-op

        // v0.9.48 — every drop band on every row kind resolves to a place that can never detach a
        // marker from its head: before/onto a # Next or # End If row = the end of the owning block's
        // last branch · just above an # Else row = the end of the Then branch · just under it = the
        // start of the Else branch · just under a scope head = its FIRST child · after a closing
        // marker = after the whole block (already outside).
        StepNode? adoptParent = null;
        StepNode? prependParent = null;
        if (target is not null)
        {
            if (IsNextMarker(target) || IsEndIfMarker(target))
            {
                if (mode != "after")
                {
                    var owner = FindClosingMarkerOwner(target, out var elseBranch);
                    if (owner is not null)
                    {
                        if (elseBranch is not null && moveSet.Contains(elseBranch)) return;
                        for (var p = owner; p is not null; p = p.Parent)
                            if (moveSet.Contains(p)) return;   // the owning block is (part of) the moved unit
                        adoptParent = elseBranch ?? owner;
                    }
                }
            }
            else if (IsElseMarker(target))
            {
                var owner = FindElseMarkerOwner(target);
                if (owner is not null)
                {
                    for (var p = owner; p is not null; p = p.Parent)
                        if (moveSet.Contains(p)) return;
                    if (mode == "before") adoptParent = owner;          // just above the Else row = end of the Then branch
                    else if (mode == "after") prependParent = target;   // just under it = start of the Else branch
                    // "into" keeps the v0.9.42 behaviour: appended into the Else branch
                }
            }
            else if (mode == "after" && StepDefinitions.AcceptsChildren(target))
            {
                for (var p = target; p is not null; p = p.Parent)
                    if (moveSet.Contains(p)) return;
                prependParent = target;   // v0.9.48 — just under a scope head = its first child;
                                          // a sibling insert here would split the head from its closing row
            }
        }

        // v0.9.49 — remember the drag direction before removal shifts indices: a middle-band drop
        // on a plain row lands on the side the drag came from (user report: the row directly
        // below its target would not move).
        bool sameSiblings = target is not null
            && ReferenceEquals(node.Parent?.Children ?? Steps, target.Parent?.Children ?? Steps);
        int nodeIdxBefore = (node.Parent?.Children ?? Steps).IndexOf(node);
        int targetIdxBefore = sameSiblings && target is not null ? (target.Parent?.Children ?? Steps).IndexOf(target) : -1;

        Snapshot();   // v0.9.30 — undoable

        foreach (var m in moveSet)
        {
            if (!(m.Parent?.Children ?? Steps).Remove(m)) return;
            m.Parent = null;
        }

        if (adoptParent is not null)
        {
            foreach (var m in moveSet) { m.Parent = adoptParent; adoptParent.Children.Add(m); }
        }
        else if (prependParent is not null)
        {
            for (int k = moveSet.Count - 1; k >= 0; k--) { moveSet[k].Parent = prependParent; prependParent.Children.Insert(0, moveSet[k]); }
        }
        else if (target is null)
        {
            foreach (var m in moveSet) Steps.Add(m);
        }
        else if (mode == "into" && (StepDefinitions.AcceptsChildren(target) || IsElseMarker(target)))   // v0.7.9 / v0.9.42
        {
            foreach (var m in moveSet) { m.Parent = target; target.Children.Add(m); }
        }
        else
        {
            var list = target.Parent?.Children ?? Steps;
            int i = list.IndexOf(target);
            if (i < 0) { foreach (var m in moveSet) Steps.Add(m); }
            else
            {
                int at = DropBeforeForPlainRow(mode, sameSiblings, nodeIdxBefore, targetIdxBefore) ? i : i + 1;   // v0.9.49 — direction-aware: a middle-band drop on a plain row can never be a no-op
                foreach (var m in moveSet) { m.Parent = target.Parent; list.Insert(at++, m); }
            }
        }

        if (OwnsNextMarker(node)) EnsureLoopMarker(node);   // v0.9.48 — a moved block always keeps its # Next row
        Renumber();
        MarkDirty();
        Log(moveSet.Count > 1
            ? "moved: " + node.Summary + "  (+ its # Next closing row — the block stays one unit)"
            : "moved: " + node.Summary);
    }



    // ═══════════════ file ═══════════════



    [RelayCommand]

    private void NewDocument()

    {

        if (!ConfirmDiscard()) return;

        Snapshot();   // v0.9.30 — undoable

        Steps.Clear();

        SelectedNode = null;

        _currentFile = null;

        _dirty = false;

        UpdateFileText();

        Log("new script");

    }



    [RelayCommand]

    private void OpenDocument()

    {

        if (!ConfirmDiscard()) return;

        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "AMS script (*.amsj)|*.amsj" };

        if (dlg.ShowDialog() != true) return;

        try

        {

            Snapshot();   // v0.9.30 — undoable

            Steps.Clear();

            foreach (var n in DocumentService.Load(dlg.FileName)) Steps.Add(n);

            Renumber();

            int sanitized = SanitizeAllElseMarkers();   // v0.9.36 — a repaired file must remain dirty until saved

            int healed = HealAllElseMarkers();   // v0.9.37 — an If without Else/End If is invalid; repair on open

            int loopHealed = HealAllLoopMarkers();   // v0.9.45 — every For Loop gets its visible/protected # Next closing row

            int lifted = DocumentService.LiftOrphanChildren(Steps);   // v0.9.42 — release steps nested under conditionless heads

            if (lifted > 0) { Renumber(); Log($"released {lifted} step(s) that were nested under a conditionless step (v0.9.42)"); }

            _currentFile = dlg.FileName;

            _dirty = sanitized > 0 || healed > 0 || loopHealed > 0 || lifted > 0;

            UpdateFileText();

            Log($"opened: {dlg.FileName} — {Steps.Count} top-level / {CountAll(Steps)} total steps");

        }

        catch (Exception ex)

        {

            System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow, "بازکردن ناموفق: " + ex.Message,

                "Open", MessageBoxButton.OK, MessageBoxImage.Error);

        }

    }



    [RelayCommand] private void Save() => SaveCore(saveAs: false);

    [RelayCommand] private void SaveAs() => SaveCore(saveAs: true);



    private void SaveCore(bool saveAs)

    {

        var path = _currentFile;

        if (saveAs || path is null)

        {

            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "AMS script (*.amsj)|*.amsj", FileName = "script.amsj" };

            if (dlg.ShowDialog() != true) return;

            path = dlg.FileName;

        }

        try

        {

            DocumentService.Save(path, Steps);

            _currentFile = path;

            _dirty = false;

            UpdateFileText();

            Log("saved: " + path);

        }

        catch (Exception ex)

        {

            System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow, "ذخیره ناموفق: " + ex.Message,

                "Save", MessageBoxButton.OK, MessageBoxImage.Error);

        }

    }



    /// <summary>Import .amk via the amk_toolkit decoder (§9.4): decode → JSON → step tree.</summary>

    [RelayCommand]

    private async Task ImportAmk()

    {

        if (!ConfirmDiscard()) return;

        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "AMK script (*.amk)|*.amk" };

        if (dlg.ShowDialog() != true) return;

        Log("decoding: " + dlg.FileName + " …");

        try

        {

            var res = await Task.Run(() => AmkImporter.Import(dlg.FileName, _settings.ToolkitDir));

            Snapshot();   // v0.9.36 — capture the PREVIOUS document before clearing it

            Steps.Clear();

            SelectedNode = null;

            foreach (var n in res.Roots) Steps.Add(n);

            Renumber();

            SanitizeAllElseMarkers();   // v0.9.35 — clean v0.9.33-era duplicate pairs on import

            HealAllElseMarkers();   // v0.9.37 — imports must land with complete If/Else structure

            HealAllLoopMarkers();   // v0.9.45 — imported loops get # Next

            if (DocumentService.LiftOrphanChildren(Steps) > 0) Renumber();   // v0.9.42

            _currentFile = null;

            _dirty = true;

            UpdateFileText();

            Log($"imported {res.Roots.Count} top-level / {CountAll(res.Roots)} total steps from {Path.GetFileName(dlg.FileName)}");

            foreach (var w in res.Warnings) Log("import note: " + w);

        }

        catch (Exception ex)

        {

            Log("import failed: " + ex.Message);

            System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow, "ایمپورت ناموفق: " + ex.Message,

                "Import .amk", MessageBoxButton.OK, MessageBoxImage.Error);

        }

    }



    [RelayCommand]

    private void Generate()

    {

        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PowerShell script (*.ps1)|*.ps1", FileName = "macro.ps1" };

        if (dlg.ShowDialog() != true) return;

        try

        {

            ScriptGenerator.Generate(dlg.FileName, Steps, _settings.Port, _settings.PythonDir,

                                     _settings.MouseMoveSpeedMin, _settings.MouseMoveSpeedMax,

                                     (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);

            Log("generated: " + dlg.FileName + "  (copy bridge\\bridge.py next to it)");

        }

        catch (Exception ex)

        {

            System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow, "تولید ناموفق: " + ex.Message,

                "Generate Script", MessageBoxButton.OK, MessageBoxImage.Error);

        }

    }



    [RelayCommand]

    private void Exit() => System.Windows.Application.Current.MainWindow?.Close();



    // ═══════════════ options ═══════════════



        [RelayCommand]
    private void BoardPrep()
    {
        // v0.9.50 — Tools → Board Preparation: the AMS USB Studio flow inside Classroom Studio.
        var win = new Ams.UI.Views.BoardPrepWindow { Owner = System.Windows.Application.Current.MainWindow };
        win.ShowDialog();
    }

[RelayCommand]

    private void Options()

    {

        var dlg = new OptionsDialog(_settings.Port, _settings.PythonDir, _settings.ToolkitDir,

                                      _settings.WordDelayMin, _settings.WordDelayMax,

                                      _settings.MouseMoveSpeedMin, _settings.MouseMoveSpeedMax,

                                      _settings.RunStopHotkey, _settings.PauseResumeHotkey,   // v0.9.43 — paired hotkeys (4 keys → 2)

                                      _settings.KeyboardBoard,   // v0.9.44 — which board executes the keyboard

                                      _settings.TypeKeyMinMs, _settings.TypeKeyMaxMs,

                                      _settings.NoActivateWhenStopped)

        {

            Owner = System.Windows.Application.Current.MainWindow,

        };

        void Apply()

        {

            _settings.Port = dlg.Port;

            _settings.PythonDir = dlg.PythonDir;

            _settings.ToolkitDir = dlg.ToolkitDir;

            _settings.WordDelayMin = dlg.WordDelayMin;

            _settings.WordDelayMax = dlg.WordDelayMax;

            _settings.MouseMoveSpeedMin = Math.Max(0, dlg.MouseMoveSpeedMin);

            _settings.MouseMoveSpeedMax = Math.Max(_settings.MouseMoveSpeedMin, dlg.MouseMoveSpeedMax);

            _settings.RunStopHotkey = dlg.RunStopHotkey;         // v0.9.43 — paired (4 keys → 2)

            _settings.PauseResumeHotkey = dlg.PauseResumeHotkey;

            _settings.KeyboardBoard = dlg.KeyboardBoard;           // v0.9.44

            _settings.TypeKeyMinMs = dlg.TypeKeyMinMs;                              // v0.9.23

            _settings.TypeKeyMaxMs = Math.Max(dlg.TypeKeyMinMs, dlg.TypeKeyMaxMs);

            _settings.NoActivateWhenStopped = dlg.NoActivateWhenStopped;

            StepDefinitions.TypingFallbackMinMs = _settings.TypeKeyMinMs;           // live update

            StepDefinitions.TypingFallbackMaxMs = _settings.TypeKeyMaxMs;

            _settings.Save();

            ReloadKeyBindings();

            OnPropertyChanged(nameof(RunLabel));

            OnPropertyChanged(nameof(StopLabel));

            Log($"options saved — port {_settings.Port}");

        }

        // v0.9.55 - the settings tab is no longer a detachable window: its whole content

        // (all four sections) is hosted in a docked panel inside the main window.

        if (System.Windows.Application.Current.MainWindow is Ams.UI.MainWindow mw)

        {

            dlg.Completed += (_, ok) => { mw.HideSettingsPanel(); if (ok) Apply(); };

            mw.ShowSettingsPanel(dlg.EmbedContent());

        }

        else if (dlg.ShowDialog() == true) Apply();

    }



    // ═══════════════ board: connect / run / stop ═══════════════



    [RelayCommand]

    private async Task Connect()

    {

        if (Connection == ConnectionState.Connected)

        {

            // bridge sends HALT + BYE (§11.3) → next run re-handshakes cleanly (FW 1.3+)

            try

            {

                if (_bridge is not null) await _bridge.DisconnectAsync();

                Log("disconnected (HALT + BYE sent)");

            }

            catch (Exception ex) { Log("disconnect error: " + ex.Message); }

            Connection = ConnectionState.Disconnected;

            PicoPresent = ArmPresent = false;   // v0.9.44 — both presence lights go out
            StopCursorSync();

            ConnectButtonText = "Connect";

            return;

        }



        _bridge ??= CreateBridge();

        _bridge.LineReceived -= OnBridgeLine;
        _bridge.LineReceived += OnBridgeLine;
        _bridge.StateChanged -= OnBridgeStateChanged;
        _bridge.StateChanged += OnBridgeStateChanged;



        try

        {

            Connection = ConnectionState.Handshaking;

            var v43port = EffectivePort();   // v0.9.43 — the manual status-bar pick overrides the saved default

            Log($"HELLO → {v43port} …");

            await _bridge.ConnectAsync(v43port);

            Connection = ConnectionState.Connected;

            ConnectButtonText = "Disconnect";

            Log($"connected to board on {_bridge.Port} (FW {_bridge.FirmwareVersion}, encrypted channel)");



            // v0.9.44 — identify which board(s) answered: light the Pico / Pro Micro indicators

            try

            {

                var pong = await _bridge.SendAsync("PING");

                (PicoPresent, ArmPresent) = ParseBoardPresence(pong);

                Log($"board identity: {pong} → pico={(PicoPresent ? "on" : "off")}, arm={(ArmPresent ? "on" : "off")}");
                StartCursorSync();

            }

            catch (Exception ex)

            {

                PicoPresent = false; ArmPresent = true;   // a plain firmware-1.6 board is the Pro Micro arm alone

                StopCursorSync();
                Log("board identity probe failed: " + ex.Message);

            }

        }

        catch (Exception ex)

        {

            StopCursorSync();
            Connection = ConnectionState.Disconnected;

            ConnectButtonText = "Connect";

            Log("connect failed: " + ex.Message);

        }

    }



    /// <summary>v0.9.5 — شناسایی خودکار برد هنگام اجرای برنامه: یک‌بار از MainWindow_Loaded

    /// صدا زده می‌شود؛ اگر برد وصل نیست، فقط در لاگ ثبت می‌شود و دکمه Connect دستی می‌ماند.</summary>

    public void AutoConnectOnStartup()

    {

        if (Connection != ConnectionState.Disconnected) return;

        Log("auto-detecting board…");

        ConnectCommand.Execute(null);

    }



    [RelayCommand]

    private async Task Run()

    {

        if (_runActive) { Log("run: the previous run is still stopping — one moment…"); return; }   // v0.9.0

        if (IsRunning) return;

        if (Connection != ConnectionState.Connected || _bridge is null)

        {

            Log("Run: board not connected — press Connect first");

            return;

        }

        if (Steps.Count == 0)

        {

            Log("Run: no steps — insert some steps first");

            return;

        }



        IsRunning = true;

        _runActive = true;   // v0.9.0 — released in finally once the engine has fully unwound

        _runCts = new CancellationTokenSource();

        Log("run started");

        // Play Options (AMK parity): repeat once / N times / for a duration (§9.1).

        var mode = _settings.PlayRepeatMode;

        int? timesLimit = mode == "times" ? Math.Max(1, _settings.PlayRepeatTimes) : null;

        TimeSpan? timeLimit = mode == "timed"

            ? _settings.PlayRepeatUnit switch

            {

                "second" => TimeSpan.FromSeconds(Math.Max(1, _settings.PlayRepeatValue)),

                "hour" => TimeSpan.FromHours(Math.Max(1, _settings.PlayRepeatValue)),

                _ => TimeSpan.FromMinutes(Math.Max(1, _settings.PlayRepeatValue)),

            }

            : null;

        RunEngine? engine = null;

        try

        {

            engine = new RunEngine(_bridge, Log,

                (int)SystemParameters.PrimaryScreenWidth,

                (int)SystemParameters.PrimaryScreenHeight,

                _settings.ToolkitDir,

                () => PicoPresent);   // v0.9.46 — explicit per-step Pico/arm keyboard routing

            // v0.9.0 — simple cancellable poll; the old ManualResetEvent + Task.Run loop burned

            // a thread-pool thread every 300 ms for the whole pause.

            engine.SetPauseCheck(async ct =>

            {

                while (IsPaused) await Task.Delay(100, ct);

            });

            engine.SetMouseSpeedRange(_settings.MouseMoveSpeedMin, _settings.MouseMoveSpeedMax);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int iter = 1; ; iter++)

            {

                _runCts.Token.ThrowIfCancellationRequested();

                if (iter > 1) Log($"—— repeat {iter} ——");

                await engine.RunAsync(Steps, _runCts.Token);

                if (timesLimit is not null && iter >= timesLimit) break;

                if (timeLimit is not null && sw.Elapsed >= timeLimit) break;

                if (timesLimit is null && timeLimit is null) break;   // once

            }

            Log("run finished");


        }

        catch (RunEngine.SilentStop) { Log("run stopped (silent — find image timeout)"); }

        catch (OperationCanceledException) { Log("run aborted by user"); }

        catch (Exception ex) { ErrorPolicyBootstrap.Handle(ex, "run"); Log("run error: " + ex.Message); }

        finally

        {

            engine?.StopAllAudio();   // v0.9.0 — looped playAudio used to keep playing after Stop

            // guaranteed key/button release (§10.3 rule 8) — 5s cap so the Run button always re-enables

            try { await _bridge.SendAsync("HALT", 5); } catch { }

            _runActive = false;

            IsRunning = false;

            // v0.9.0 — the "Do not activate the main window when script stopped" checkbox existed

            // in Play Options but was never honored (dead setting). AMK activates by default.

            if (!_settings.NoActivateWhenStopped)

                try { System.Windows.Application.Current?.MainWindow?.Activate(); } catch { }

        }

    }



    [RelayCommand]

    private void Stop()

    {
        // Manual Stop is not fatal, but it must acknowledge a repeating alarm.
        ErrorPolicyBootstrap.Acknowledge();

        if (IsRunning)

        {

            _runCts?.Cancel();

            Log("stop requested — aborting the in-flight board command (instant)");

        }

        // Disable Run button immediately so the user cannot double-click while HALT is in-flight.

        IsRunning = false;

        if (Connection == ConnectionState.Connected && _bridge is not null)

        {

            var b = _bridge;

            _ = Task.Run(async () =>

            {

                // v0.6.2 — instant abort first (breaks a WSND/TRGSND listen immediately),

                // then a settle HALT as backup once the pipeline clears.

                try { await b.SendAbortAsync(); } catch { }

                await Task.Delay(800);

                try { await b.SendAsync("HALT", 5); } catch { }

            });

        }

        else if (!IsRunning)

        {

            Log("Stop pressed — board not connected");

        }

    }



    [RelayCommand]

    private void Pause()

    {

        if (!IsRunning) return;

        IsPaused = true;

        Log("⏸ playback paused");

    }



    [RelayCommand]

    private void Resume()

    {

        if (!IsRunning || !IsPaused) return;

        IsPaused = false;   // the poll loop in the pause-check notices within ~100ms (v0.9.0)

        Log("▶ playback resumed");

    }



    // ═══════════════ dialog tools: sound calibrate + region picker ═══════════════



    /// <summary>

    /// Calibrate button for waitForSound (§16.6.5): samples the sensor on A0 for 1s

    /// in silence (SCAL) and suggests floor_max × 1.5 — the rule of §16.1/§17.7.

    /// Final threshold should be the middle of floor and real-sound peak.

    /// </summary>

    private async Task<int?> CalibrateSoundThreshold()

    {

        if (Connection != ConnectionState.Connected || _bridge is null
            || _bridge.State != BridgeState.Connected)

        {

            System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow,

                "ارتباط برد فعال نیست. Connect را بزنید و سپس دوباره کالیبراسیون صدا را اجرا کنید.",

                "Calibrate", MessageBoxButton.OK, MessageBoxImage.Information);

            return null;

        }

        int? suggested = null;

        try

        {

            var reply = await _bridge.SendAsync("SCAL|1000", 4.0);   // ARM protocol maximum is 1000 ms

            var m = Regex.Match(reply, @"max=(\d+)");

            if (m.Success)

            {

                int max = int.Parse(m.Groups[1].Value);

                if (max > 70)

                    Log($"calibrate: floor max={max} is high — something was playing? measure in real silence (§17.7 floor classes)");

                suggested = (int)Math.Ceiling(max * 1.5);   // never below floor × 1.5 (§16.1)

                Log($"calibrate: floor max={max} → suggested threshold {suggested}");

            }

            else if (reply.StartsWith("ERR|UNKNOWN|SCAL", StringComparison.Ordinal))
            {
                Log("calibrate: Pico bundle does not proxy SCAL to the Pro Micro");
                System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow,
                    "این Bundle پیکو فرمان تست صدا را پشتیبانی نمی‌کند. ابتدا با همین نسخهٔ Classroom پروژه را دوباره روی CIRCUITPY خروجی بگیرید.",
                    "کالیبره", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            else

                Log("calibrate: SCAL reply unexpected (" + reply + ") — falling back to WSND probing");

        }

        catch (Exception ex) { Log("calibrate: SCAL failed (" + ex.Message + ") — falling back to WSND probing"); }



        if (suggested is null)

        {

            // v0.9.32 — fallback: binary-search the silence floor with WSND probes. Works on any

            // firmware that answers WSND: fired → the floor is below the probe; ERR/timeout → above.

            int lo = 1, hi = 200, probes = 0, probeErrors = 0;

            while (hi - lo > 4)

            {

                int mid = (lo + hi) / 2;

                probes++;

                string probe;

                try { probe = await _bridge.SendAsync($"WSND|{mid},40,900", 4.0); }

                catch { probeErrors++; probe = ""; }

                if (probe.StartsWith("ERR|UNKNOWN|WSND", StringComparison.Ordinal)
                    || probe.StartsWith("ERR|EXEC|WSND", StringComparison.Ordinal))
                {
                    Log("calibrate probe failed: " + probe);
                    System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow,
                        "فرمان تست صدا به Pro Micro نرسید. Bundle جدید را روی CIRCUITPY خروجی بگیرید و اتصال UART بردها را بررسی کنید.",
                        "کالیبره", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }
                bool fired = probe.StartsWith("OK|", StringComparison.Ordinal) || probe.StartsWith("EVT|", StringComparison.Ordinal);
                bool quiet = probe.StartsWith("ERR|TIMEOUT|WSND", StringComparison.Ordinal);
                if (!fired && !quiet)
                {
                    probeErrors++;
                    Log("calibrate probe invalid reply: " + probe);
                    continue;
                }

                Log($"calibrate probe: WSND ≥{mid} → {(fired ? "fired" : "quiet")}");

                if (fired) hi = mid; else lo = mid;

            }

            if (probeErrors >= probes)

            {

                System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow,

                    "کالیبراسیون صدا ناموفق — برد به WSND پاسخ نداد; جزئیات در لاگ سریال.",

                    "کالیبره", MessageBoxButton.OK, MessageBoxImage.Warning);

                return null;

            }

            suggested = Math.Max(10, (int)Math.Ceiling(hi * 1.5));

            Log($"calibrate (WSND fallback): silence floor ≈ {hi} → suggested threshold {suggested}");

        }

        return suggested;

    }



    /// <summary>Region Picker overlay (§5.5.12) for x/y/w/h field sets.</summary>

    private async Task<string?> SampleHandMovementAsync()
    {
        var sample = await HandMovementSample.CaptureAsync();
        return sample is null ? null : HandMovementSample.Encode(sample);
    }

    private Task<(int x, int y, int w, int h)?> PickRegionOnScreen()

    {

        var picker = new RegionPickerWindow { Owner = System.Windows.Application.Current.MainWindow };

        if (picker.ShowDialog() == true)

        {

            var r = (picker.RegionX, picker.RegionY, picker.RegionW, picker.RegionH);

            Log($"region picked: [{r.RegionX},{r.RegionY} {r.RegionW}x{r.RegionH}]");

            return Task.FromResult<(int, int, int, int)?>(r);

        }

        Log("region pick cancelled");

        return Task.FromResult<(int, int, int, int)?>(null);

    }

    private Task<(int x, int y)?> PickPointOnScreen()
    {
        var picker = new PointPickerWindow { Owner = System.Windows.Application.Current.MainWindow };
        if (picker.ShowDialog() == true)
        {
            var point = (picker.ScreenX, picker.ScreenY);
            Log($"point picked: ({point.ScreenX},{point.ScreenY})");
            return Task.FromResult<(int, int)?>(point);
        }
        Log("point pick cancelled");
        return Task.FromResult<(int, int)?>(null);
    }



    // ═══════════════ lifecycle ═══════════════



    public bool ConfirmDiscard()

    {

        if (!_dirty) return true;

        var r = System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow,

            "Save changes to the current script?", "AMS",

            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (r == MessageBoxResult.Cancel) return false;

        if (r == MessageBoxResult.Yes)

        {

            SaveCore(saveAs: false);

            return !_dirty;   // user may have cancelled the Save-As dialog

        }

        return true;

    }



    public async Task ShutdownAsync()

    {

        _runCts?.Cancel();
        StopCursorSync();

        if (_bridge is not null)

        {

            try { await _bridge.DisconnectAsync(); } catch { }

        }

    }



    // ═══════════════ internals ═══════════════



    private void OnBridgeLine(object? sender, string line)
    {
        // Build 72 — cursor origin sync runs four times per second. Successful ACKs are
        // transport housekeeping, not operator diagnostics; keep errors and all other lines.
        if (line.Contains("OK|CURSOR", StringComparison.Ordinal)) return;
        // PythonBoardBridge raises LineReceived on the serial-reader thread before it parses
        // and completes the pending reply. Never synchronously wait for WPF from this callback:
        // a modal tool awaiting that reply would otherwise create a reader/UI deadlock.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke((Action)(() => Log("bridge: " + line)));
        else Log("bridge: " + line);
    }

    // Build 71 — transport state is authoritative. A sidecar/COM failure must not leave
    // the status bar green while every later command throws "Bridge is not connected."
    private void OnBridgeStateChanged(object? sender, BridgeState state)
    {
        if (state != BridgeState.Disconnected) return;
        void ApplyDisconnected()
        {
            Connection = ConnectionState.Disconnected;
            ConnectButtonText = "Connect";
            PicoPresent = false;
            ArmPresent = false;
            StopCursorSync();
            Log("bridge disconnected — reconnect before calibration or playback");
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) ApplyDisconnected();
        else dispatcher.BeginInvoke((Action)ApplyDisconnected);
    }



    /// <summary>Hotkey display strings bound from settings (for the Options dialog preview and bottom-bar buttons).</summary>

    public string RunHotkeyText => string.IsNullOrWhiteSpace(_settings.RunStopHotkey) ? "" : $"    {_settings.RunStopHotkey}";   // v0.9.43 — paired

    public string StopHotkeyText => string.IsNullOrWhiteSpace(_settings.RunStopHotkey) ? "" : $"    {_settings.RunStopHotkey}";   // v0.9.43 — same key as run

    public string PauseHotkeyText => _settings.PauseResumeHotkey;   // v0.9.43 — paired

    public string ResumeHotkeyText => _settings.PauseResumeHotkey;   // v0.9.43 — paired

    /// <summary>Run/Stop button label — "Run" or "Run + key", updated on hotkey change.</summary>

    public string RunLabel =>

        string.IsNullOrWhiteSpace(_settings.RunStopHotkey) ? "Run" : $"Run    {_settings.RunStopHotkey}";   // v0.9.43 — the same key stops

    public string StopLabel =>

        string.IsNullOrWhiteSpace(_settings.RunStopHotkey) ? "Stop" : $"Stop    {_settings.RunStopHotkey}";   // v0.9.43 — same key as Run



    /// <summary>Applies the saved hotkey strings from _settings to MainWindow.InputBindings

    /// AND registers them as OS-level global hotkeys (works even when the window has no focus).</summary>

    public void ReloadKeyBindings()

    {

        var mw = System.Windows.Application.Current.MainWindow;

        if (mw is null) return;

        var bindings = mw.InputBindings;

        // v0.9.0 — replace only OUR four playback bindings. The old bindings.Clear() also wiped

        // the XAML editing shortcuts (Ctrl+N/O/S, F2, Del, Ctrl+D, Alt+Up/Down), silently killing

        // them from app start (MainWindow_Loaded calls this).

        for (int i = bindings.Count - 1; i >= 0; i--)

        {

            if (bindings[i] is KeyBinding kb &&

                (ReferenceEquals(kb.Command, RunCommand) || ReferenceEquals(kb.Command, StopCommand)

                 || ReferenceEquals(kb.Command, PauseCommand) || ReferenceEquals(kb.Command, ResumeCommand)

                 || ReferenceEquals(kb.Command, RunOrStopCommand) || ReferenceEquals(kb.Command, PauseOrResumeCommand)))   // v0.9.43 — also replace the paired bindings

                bindings.RemoveAt(i);

        }

        // v0.9.3 — register OS-level GLOBAL hotkeys first (crash-safe HwndSource hook; the

        // v0.9.2 build died at startup because of the SetWindowLongPtr subclass). A combo that

        // registered globally must NOT keep its window-local KeyBinding, or a focused keypress

        // would fire the command twice. Combos the OS refused keep the local binding as fallback.

        var global = GlobalHotkeyService.Register(_settings, name => name switch

        {

            "runstop"     => RunOrStopCommand,      // v0.9.43 — paired hotkeys (4 keys → 2)

            "pauseresume" => PauseOrResumeCommand,

            _             => null,

        });

        if (global.Count > 0)

            Log("global hotkeys active: " + string.Join(", ", global.OrderBy(x => x)));

        foreach (var error in GlobalHotkeyService.LastErrors)

            Log("⚠ global hotkey: " + error + " — local-only fallback installed");

        if (!global.Contains("runstop")) AddBinding(bindings, _settings.RunStopHotkey, RunOrStopCommand);   // v0.9.43

        if (!global.Contains("pauseresume")) AddBinding(bindings, _settings.PauseResumeHotkey, PauseOrResumeCommand);

    }



    private static void AddBinding(InputBindingCollection bindings, string hk, ICommand cmd)

    {

        try { bindings.Add(new KeyBinding(cmd, ParseHk(hk))); } catch { }

    }



    private static KeyGesture ParseHk(string hk)

    {

        if (string.IsNullOrWhiteSpace(hk)) return new KeyGesture(Key.None);

        var parts = hk.Split('+');

        var mods = ModifierKeys.None;

        for (int i = 0; i < parts.Length - 1; i++)

        {

            var p = parts[i].Trim();

            if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= ModifierKeys.Shift;

            else if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) mods |= ModifierKeys.Control;

            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= ModifierKeys.Alt;

        }

        var key = Enum.TryParse<Key>(parts[parts.Length - 1].Trim(), ignoreCase: true, out var k) ? k : Key.None;

        return new KeyGesture(key, mods);

    }



    private IBoardBridge CreateBridge()

    {

        // v0.9.20 — search several layouts instead of assuming the build-output one: a

        // hand-assembled release zip had the exe sitting in src\Ams.UI with no bridge\

        // subfolder next to it, so Connect could never reach the board.

        var candidates = PythonBoardBridge.BridgeScriptCandidates(AppContext.BaseDirectory);

        string? found = null;

        foreach (var c in candidates) { if (File.Exists(c)) { found = c; break; } }

        if (found is null)

        {

            Log("⚠ bridge.py not found — the board cannot connect. Searched:");

            foreach (var c in candidates) Log("   " + c);

            found = candidates[0];   // let python's own error surface in the log too

        }

        else if (found != candidates[0])

        {

            Log("bridge.py: using fallback location — " + found);

        }

        // v0.9.57 — portable path resolution: bundled bridge\ dir first, settings only if exists
        var pydir = PortablePaths.FirstExistingDir(new[]
        {
            Path.Combine(AppContext.BaseDirectory, "bridge"),
            _settings.PythonDir,
        });
        if (pydir is null && _settings.PythonDir.Length > 0)
            Log("\u0645\u0633\u06cc\u0631 \u062a\u0646\u0638\u06cc\u0645\u200c\u0634\u062f\u0647 \u0631\u0648\u06cc \u0627\u06cc\u0646 \u0633\u06cc\u0633\u062a\u0645 \u0646\u06cc\u0633\u062a — \u0627\u0632 \u0646\u0633\u062e\u0647\u0027\u06cc \u062f\u0627\u062e\u0644 \u067e\u0648\u0634\u0647 \u0627\u0633\u062a\u0641\u0627\u062f\u0647 \u0645\u06cc\u0027\u0634\u0648\u062f: " + _settings.PythonDir);
        var resolvedPydir = pydir ?? _settings.PythonDir;
        return new PythonBoardBridge(resolvedPydir, found);

    }



    /// <summary>v0.9.45/48 — AMK parity: every condition-less child-capable container (loop / parallel / package) owns one structural sibling marker

    /// named "Next". Runtime iteration still uses loop.Children; this marker is the visible,

    /// protected closing row and the red vein's bottom cap.</summary>

    private void EnsureLoopMarker(StepNode loopNode)

    {

        var list = loopNode.Parent?.Children ?? Steps;

        int i = list.IndexOf(loopNode);

        if (i < 0 || (i + 1 < list.Count && IsNextMarker(list[i + 1]))) return;

        list.Insert(i + 1, new StepNode

        {

            Type = "comment", Parent = loopNode.Parent,

            Props = new() { ["text"] = "Next" },

        });

        Renumber();

        Log("loop marker added — # Next closes the For Loop and anchors the red vein (v0.9.45)");

    }



    /// <summary>v0.9.45/48 — recursively heals old/opened/imported plans: every condition-less child-capable container

    /// without its immediate # Next sibling. Returns inserted marker rows.</summary>

    public static int HealMissingLoopMarkers(IList<StepNode> list)

    {

        int inserted = 0;

        for (int i = 0; i < list.Count; i++)

        {

            var n = list[i];

            if (n.Children.Count > 0) inserted += HealMissingLoopMarkers(n.Children);

            if (!OwnsNextMarker(n) || (i + 1 < list.Count && IsNextMarker(list[i + 1]))) continue;

            list.Insert(i + 1, new StepNode

            {

                Type = "comment", Parent = n.Parent,

                Props = new() { ["text"] = "Next" },

            });

            inserted++;

            i++;

        }

        return inserted;

    }



    private int HealAllLoopMarkers()

    {

        int inserted = HealMissingLoopMarkers(Steps);

        if (inserted > 0)

        {

            Renumber();

            Log($"structure repaired — added {inserted} missing # Next loop terminator(s) (v0.9.45)");

        }

        return inserted;

    }



    /// <summary>v0.7.9 — AMK parity: a Find Image with "Insert If Else" owns two marker

    /// siblings right after it: "Else" (holds the not-found steps) and "End If".

    /// Created once (existing markers are kept); removing them is the user's call.</summary>

    private void EnsureElseMarkers(StepNode ifNode)

    {

        var list = ifNode.Parent?.Children ?? Steps;

        int i = list.IndexOf(ifNode);

        if (i < 0) return;

        // v0.9.35 — UI-side existence scan instead of the runtime LocateElseBlock (which breaks at

        // non-comments ON PURPOSE for legacy-mode execution — wrong tool here). HasElseMarkersForUi

        // steps over flat rows but never borrows a LATER If's markers and never crosses an orphan End If.

        if (!HasElseMarkersForUi(list, i))

        {

            list.Insert(i + 1, new StepNode { Type = "comment", Props = new() { ["text"] = "End If" } });

            list.Insert(i + 1, new StepNode { Type = "comment", Props = new() { ["text"] = "Else" } });

            Renumber();

            Log("if/else markers added — the # Else row holds the condition-false branch (select it and Insert, or drag onto it); flat rows between Else and End If work too (v0.9.30+)");

        }

        if (SanitizeElseMarkers(list, i, Log) > 0) { Renumber(); MarkDirty(); }   // v0.9.35 — drop orphan duplicate pairs in this If's region

    }



    /// <summary>v0.9.36 — UI ownership locator. It deliberately differs from the runtime

    /// LocateElseBlock: flat sibling rows may precede Else in old plans, but a later active If is a

    /// hard boundary. All UI operations (existence + delete guard) use this one locator so they cannot drift.</summary>

    public static (int ElseIndex, int EndIfIndex) LocateElseMarkersForUi(IList<StepNode> siblings, int ifIndex)

    {

        for (int j = ifIndex + 1; j < siblings.Count; j++)

        {

            var c = siblings[j];

            if (c.Type is "findImage" or "waitForSound" or "waitForLight" && PropEx.GetBool(c.Props, "insertIfElse"))

                return (-1, -1);   // next condition owns everything after this boundary

            if (c.Type != "comment") continue;

            var t = PropEx.GetString(c.Props, "text");

            if (t == "End If") return (-1, -1);   // orphan boundary before Else

            if (!t.StartsWith("Else", StringComparison.Ordinal)) continue;

            for (int k = j + 1; k < siblings.Count; k++)

            {

                var d = siblings[k];

                if (d.Type is "findImage" or "waitForSound" or "waitForLight" && PropEx.GetBool(d.Props, "insertIfElse"))

                    return (-1, -1);

                if (d.Type != "comment") continue;

                var u = PropEx.GetString(d.Props, "text");

                if (u.StartsWith("Else", StringComparison.Ordinal)) return (-1, -1);

                if (u == "End If") return (j, k);

            }

            return (-1, -1);

        }

        return (-1, -1);

    }



    public static bool HasElseMarkersForUi(IList<StepNode> siblings, int ifIndex)

    {

        var (elseIndex, endIfIndex) = LocateElseMarkersForUi(siblings, ifIndex);

        return elseIndex >= 0 && endIfIndex > elseIndex;

    }



    /// <summary>v0.9.35 — within this If's region (up to the next insertIfElse If), keep the FIRST complete

    /// Else…End If pair; remove any later duplicate pairs (v0.9.33-bug leftovers). Only the two comment rows

    /// are removed — flat rows between them stay. Returns the number of removed pairs.</summary>

    public static int SanitizeElseMarkers(IList<StepNode> list, int ifIndex, Action<string>? log = null)

    {

        int end = list.Count;

        for (int j = ifIndex + 1; j < list.Count; j++)

        {

            var c = list[j];

            if (c.Type is "findImage" or "waitForSound" or "waitForLight" && PropEx.GetBool(c.Props, "insertIfElse")) { end = j; break; }

        }

        int pairs = 0, removed = 0;

        int j2 = ifIndex + 1;

        while (j2 < end)

        {

            var c = list[j2];

            bool isElse = c.Type == "comment" && PropEx.GetString(c.Props, "text").StartsWith("Else", StringComparison.Ordinal);

            if (!isElse) { j2++; continue; }

            int k = j2 + 1;

            while (k < end)

            {

                var d = list[k];

                if (d.Type == "comment")

                {

                    var u = PropEx.GetString(d.Props, "text");

                    if (u.StartsWith("Else", StringComparison.Ordinal)) break;   // unterminated Else — leave it alone

                    if (u == "End If") break;

                }

                k++;

            }

            if (k >= end || !(list[k].Type == "comment" && PropEx.GetString(list[k].Props, "text") == "End If")) break;

            pairs++;

            if (pairs == 1) { j2 = k + 1; continue; }   // keep the first complete pair

            list.RemoveAt(k);   // duplicate pair → remove End If first (higher index), then Else

            list.RemoveAt(j2);

            end -= 2;

            removed++;

        }

        if (removed > 0) log?.Invoke($"removed {removed} orphan duplicate Else/End If pair(s) (v0.9.35)");

        return removed;

    }



    /// <summary>v0.9.35 — after open/import: clean orphan duplicate marker pairs for every insertIfElse If

    /// in every sibling list (the v0.9.33 bug left them in old plans).</summary>

    private int SanitizeAllElseMarkers()

    {

        int total = 0;

        void Walk(IList<StepNode> list)

        {

            int j = 0;

            while (j < list.Count)

            {

                var n = list[j];

                if (n.Children.Count > 0) Walk(n.Children);

                if (n.Type is "findImage" or "waitForSound" or "waitForLight" && PropEx.GetBool(n.Props, "insertIfElse"))

                    total += SanitizeElseMarkers(list, j);

                j++;

            }

        }

        Walk(Steps);

        if (total > 0) { Renumber(); Log($"sanitized {total} orphan duplicate Else/End If pair(s) from old plans (v0.9.36)"); }

        return total;

    }



    /// <summary>v0.9.37 — MANDATORY STRUCTURE: no active If may exist without its OWN Else/End If

    /// pair. In old plans a nested If could consume the outer If’s markers, leaving the outer block

    /// structureless (daroon1.amsj showed exactly this). Walks every list, children first, and inserts

    /// the missing pair right after each unpaired If. Returns the number of inserted marker rows.</summary>

    public static int HealMissingElseMarkers(IList<StepNode> list)

    {

        int inserted = 0;

        int i = 0;

        while (i < list.Count)

        {

            var n = list[i];

            if (n.Children.Count > 0) inserted += HealMissingElseMarkers(n.Children);

            if (n.Type is "findImage" or "waitForSound" or "waitForLight" && PropEx.GetBool(n.Props, "insertIfElse")

                && !HasElseMarkersForUi(list, i))

            {

                list.Insert(i + 1, new StepNode { Type = "comment", Props = new() { ["text"] = "End If" } });

                list.Insert(i + 1, new StepNode { Type = "comment", Props = new() { ["text"] = "Else" } });

                inserted += 2;

                i += 2;

            }

            i++;

        }

        return inserted;

    }



    /// <summary>v0.9.37 — heals the whole document, then refreshes numbering/rows.</summary>

    private int HealAllElseMarkers()

    {

        int inserted = HealMissingElseMarkers(Steps);

        if (inserted > 0)

        {

            Renumber();

            Log($"structure repaired — added {inserted / 2} missing Else/End If pair(s); every If now carries its own structure (v0.9.37)");

        }

        return inserted;

    }



    /// <summary>Else marker rows accept children (the not-found branch) — v0.7.9.</summary>

    private static bool IsElseMarker(StepNode n)

        => n.Type == "comment" && PropEx.GetString(n.Props, "text").StartsWith("Else");



    private static bool IsNextMarker(StepNode n)   // v0.9.45/48 — closing row of every condition-less child-capable block (loop · parallel · package)

        => n.Type == "comment" && PropEx.GetString(n.Props, "text") == "Next";
    /// <summary>v0.9.48 — every condition-less container that can hold children closes with a
    /// structural sibling row named # Next: For Loop, Parallel Group and Random Package alike.
    /// (If-family blocks keep their own Else/End If markers.)</summary>
    private static bool OwnsNextMarker(string? type)
        => type is "forLoop" or "parallelGroup" or "randomPackage";

    private static bool OwnsNextMarker(StepNode n) => OwnsNextMarker(n.Type);

    private static bool IsEndIfMarker(StepNode n)
        => n.Type == "comment" && PropEx.GetString(n.Props, "text") == "End If";

    /// <summary>v0.9.48 — resolves which container owns a closing marker row in the given sibling
    /// list: a # Next row belongs to the immediately-preceding loop/parallel/package head; an
    /// # End If row belongs to the nearest If head whose located End If is this marker.
    /// elseBranch receives that head's Else row when present (drops land in the LAST branch).</summary>
    public static StepNode? FindClosingMarkerOwnerIn(IList<StepNode> list, StepNode marker, out StepNode? elseBranch)
    {
        elseBranch = null;
        int mi = list.IndexOf(marker);
        if (mi < 0) return null;
        if (IsNextMarker(marker))
            return mi > 0 && OwnsNextMarker(list[mi - 1]) ? list[mi - 1] : null;
        if (IsEndIfMarker(marker))
        {
            for (int i = mi - 1; i >= 0; i--)
            {
                if (!IsIfElseHead(list[i])) continue;
                var (elseIdx, endIfIdx) = LocateElseMarkersForUi(list, i);
                if (endIfIdx != mi) continue;
                if (elseIdx >= 0) elseBranch = list[elseIdx];
                return list[i];
            }
        }
        return null;
    }

    private StepNode? FindClosingMarkerOwner(StepNode marker, out StepNode? elseBranch)
        => FindClosingMarkerOwnerIn(marker.Parent?.Children ?? Steps, marker, out elseBranch);

    /// <summary>v0.9.48 — the If head whose located Else row is this marker.</summary>
    public static StepNode? FindElseMarkerOwnerIn(IList<StepNode> list, StepNode marker)
    {
        int mi = list.IndexOf(marker);
        if (mi < 0 || !IsElseMarker(marker)) return null;
        for (int i = mi - 1; i >= 0; i--)
        {
            if (!IsIfElseHead(list[i])) continue;
            var (elseIdx, _) = LocateElseMarkersForUi(list, i);
            if (elseIdx == mi) return list[i];
        }
        return null;
    }

    private StepNode? FindElseMarkerOwner(StepNode marker)
        => FindElseMarkerOwnerIn(marker.Parent?.Children ?? Steps, marker);

    /// <summary>v0.9.49 — where a middle-band drop on a plain (non-container) row lands:
    /// "before"/"after" bands are explicit; "into" a plain row has no meaning, so it resolves by
    /// drag direction — dragging up inserts before the target, dragging down inserts after it.
    /// (Earlier versions always used i+1, which made dropping the next row onto its predecessor
    /// a silent no-op.)</summary>
    public static bool DropBeforeForPlainRow(string mode, bool sameSiblings, int nodeIdxBefore, int targetIdxBefore)
        => mode == "before" || (mode == "into" && sameSiblings && nodeIdxBefore > targetIdxBefore);

    /// <summary>v0.9.49 — public read for the drop-band logic in the view.</summary>
    public static bool IsElseMarkerRow(StepNode n) => IsElseMarker(n);




    /// <summary>v0.9.33/35 — structural markers (Else/End If belonging to an active If/Else block)

    /// cannot be deleted; they are part of the step's conditional structure, not regular steps.</summary>

    private bool IsStructuralMarker(StepNode n)

    {

        // End If and Next rows are structural too (deleting one silently degrades the visual block)

        if (!IsElseMarker(n) && !IsNextMarker(n)

            && !(n.Type == "comment" && PropEx.GetString(n.Props, "text") == "End If")) return false;

        var list = n.Parent is null ? Steps.ToList() : n.Parent.Children.ToList();

        return IsStructuralMarkerIn(list, n);

    }



    /// <summary>v0.9.35 — static core (testable). Checks EVERY insertIfElse If in the sibling list —

    /// v0.9.33/34 broke after the first If, so in multi-block lists only the FIRST block's markers were

    /// protected and later blocks' markers were deletable.</summary>

    public static bool IsStructuralMarkerIn(IList<StepNode> list, StepNode n)

    {

        for (int i = 0; i < list.Count; i++)

        {

            if (list[i] == n) return false;   // reached n before any owning If — orphan (stays deletable)

            if (OwnsNextMarker(list[i]) && i + 1 < list.Count && list[i + 1] == n && IsNextMarker(n))

                return true;   // v0.9.45/48 — this loop/parallel/package head owns the immediate # Next row

            if (list[i].Type is "findImage" or "waitForSound" or "waitForLight" && PropEx.GetBool(list[i].Props, "insertIfElse"))

            {

                var (elseIdx, endIfIdx) = LocateElseMarkersForUi(list, i);

                if (elseIdx >= 0 && endIfIdx > elseIdx && (list[elseIdx] == n || list[endIfIdx] == n))

                    return true;

                // no break — a later If in the same list may own this marker

            }

        }

        return false;

    }



    private void InsertNode(StepNode node)

    {

        var sel = SelectedNode;

        if (sel is not null && (StepDefinitions.AcceptsChildren(sel) || IsElseMarker(sel)))   // v0.7.9 / v0.9.42 — only a real container (If head, loop, package) adopts the new step

        {

            // adding while a container (For Loop / Find Image+IfElse) is selected → child step

            node.Parent = sel;

            sel.Children.Add(node);

            _collapsed.Remove(sel);   // v0.8.0 — inserting into a container expands it so the new child is visible

        }

        else if (sel is not null)

        {

            var list = sel.Parent?.Children ?? Steps;

            node.Parent = sel.Parent;

            list.Insert(list.IndexOf(sel) + 1, node);

        }

        else

        {

            Steps.Add(node);

        }

        Renumber();

    }



    /// <summary>Recursive node count — logged after import/open so a flattened tree

    /// is immediately visible in the serial log (v0.7.1).</summary>

    private static int CountAll(IEnumerable<StepNode> roots)

    {

        int n = 0;

        foreach (var r in roots) { n++; n += CountAll(r.Children); }

        return n;

    }



    /// <summary>v0.8.0 — AMK accordion: collapse/expand a row's subtree (▾/▸ toggle on rows

    /// with children; the marker siblings of a collapsed scope hide with it).</summary>

    public void ToggleCollapse(StepNode n)

    {

        FocusScopeVein(n);   // v0.9.40 - pressing the accordion toggle reveals this block's vein

        if (!_collapsed.Remove(n)) _collapsed.Add(n);

        Renumber();

    }



    private void Renumber()

    {

        // rebuild the flat view projection; rows are reused by node reference so the

        // list selection survives renames/retoggles. v0.7.2: each row also gets its

        // scope tint; every container records its visible range for the single

        // ListBox-level selected-scope overlay (v0.9.36).

        var old = new Dictionary<StepNode, FlatStepRow>();

        foreach (var r in FlatSteps) old[r.Node] = r;

        FlatSteps.Clear();

        _scopeRanges.Clear();
        _scopeElseRows.Clear();



        WalkList(Steps, "", 0, new List<StepNode>());



        void WalkList(IList<StepNode> list, string prefix, int depth, List<StepNode> scopes)

        {

            for (int i = 0; i < list.Count; i++)

            {

                var n = list[i];

                var num = prefix + (i + 1).ToString();

                n.Number = num;



                bool opensLoop = n.Type == "forLoop";

                bool opensIf = n.Type is "findImage" or "waitForSound" or "waitForLight" && PropEx.GetBool(n.Props, "insertIfElse");

                bool opensPackage = n.Type == "randomPackage";   // v0.7.8

                bool opensParallel = n.Type == "parallelGroup";   // v0.9.44 — a Parallel Group with children registers its scope too, so its red vein shows (user report)



                bool isCollapsed = _collapsed.Contains(n);   // v0.8.0 — accordion



                if (opensLoop || opensIf || opensPackage || opensParallel)   // v0.9.44 — parallelGroup added: its children must show the red vein

                {

                    var inner = new List<StepNode>(scopes) { n };

                    int start = FlatSteps.Count;

                    AddRow(n, num, depth, inner);

                    if (!isCollapsed && n.Children.Count > 0) WalkList(n.Children, num + ".", depth + 1, inner);



                    // consume the scope's closing marker siblings (AMK structure:

                    // Next closes a For; Else…/End If close an If) so they share the tint;

                    // a collapsed scope hides them too (v0.8.0)

                    while (i + 1 < list.Count && IsScopeMarker(list[i + 1], n))

                    {

                        i++;

                        if (isCollapsed) continue;

                        var m = list[i];

                        m.Number = prefix + (i + 1).ToString();

                        int markerStart = FlatSteps.Count;   // v0.9.41
                        AddRow(m, m.Number, depth, inner);
                        if (m.Type == "comment" && !_scopeElseRows.ContainsKey(n)
                            && PropEx.GetString(m.Props, "text").StartsWith("Else", StringComparison.Ordinal))
                            _scopeElseRows[n] = FlatSteps.Count - 1;   // v0.9.38

                        if (m.Children.Count > 0 && !_collapsed.Contains(m)) WalkList(m.Children, m.Number + ".", depth + 1, inner);

                        // v0.9.41 - the rule the user stated: a toggle means children, and children mean
                        // a vein. A marker row (# Else) that owns sub-steps is therefore a scope of its
                        // own and registers its own range, so pressing its toggle reveals its own vein.
                        if (m.Children.Count > 0) _scopeRanges[m] = (markerStart, FlatSteps.Count - 1);

                    }

                    _scopeRanges[n] = (start, FlatSteps.Count - 1);

                }

                else

                {

                    AddRow(n, num, depth, scopes);

                    if (!isCollapsed && n.Children.Count > 0) WalkList(n.Children, num + ".", depth + 1, scopes);

                }

            }

        }



        void AddRow(StepNode n, string num, int depth, List<StepNode> scopes)

        {

            var key = scopes.Count > 0 ? scopes[^1] : null;

            if (old.TryGetValue(n, out var row) && row.Number == num && row.Depth == depth

                && ReferenceEquals(row.ScopeKey, key))

            {

                row.RefreshStructure(_collapsed.Contains(n));   // v0.8.0 — toggle state on reused rows

                FlatSteps.Add(row);

            }

            else

            {

                var r = new FlatStepRow(n, num, depth, TintFor(scopes), key);

                r.RefreshStructure(_collapsed.Contains(n));     // v0.8.0

                FlatSteps.Add(r);

            }

        }



        static bool IsScopeMarker(StepNode candidate, StepNode container)

        {

            if (candidate.Type != "comment") return false;

            var t = PropEx.GetString(candidate.Props, "text");

            if (container.Type == "forLoop") return t == "Next";   // v0.9.45
            if (OwnsNextMarker(container)) return t == "Next";   // v0.9.58 — parallelGroup + randomPackage

            

            return t == "End If" || t.StartsWith("Else");   // Else (with its children) then End If

        }



        // v0.9.37 — white rows: hover/selection triggers own the row colour, not the scope depth.

        static System.Windows.Media.Brush TintFor(List<StepNode> scopes) => Transparent_;



    }



    /// <summary>v0.9.36 — exposes the visible range of a scope head to MainWindow's ONE

    /// overlay bracket. A plain step or Else/End If marker is not a key in _scopeRanges, so selecting

    /// it displays no bracket. This keeps visual geometry out of the view model.</summary>

    public bool TryGetScopeRange(StepNode node, out int start, out int end)

    {

        if (_scopeRanges.TryGetValue(node, out var range))

        {

            start = range.Start;

            end = range.End;

            return start >= 0 && end >= start;

        }

        start = end = -1;

        return false;

    }



    /// <summary>v0.9.38 — flat row index of the Else marker owned by this scope head, so the
    /// overlay can draw the same small bend there. Loops/packages have no Else and return false.</summary>
    public bool TryGetScopeElseRow(StepNode node, out int elseRow)
    {
        if (_scopeElseRows.TryGetValue(node, out elseRow)) return elseRow >= 0;
        elseRow = -1;
        return false;
    }

    private Dictionary<string, object?>? ShowStepDialog(string title, string type, StepNode? existing)

    {

        // v0.7.9 fix: route findImage to the AMK-style Search Picture dialog — it exists since

        // v0.7.7 but was never wired, so the generic dialog opened instead (user report + screenshots).

        if (type == "findImage")

        {

            Dictionary<string, object?>? cur = null;

            if (existing is not null)

                cur = new Dictionary<string, object?>(existing.Props)

                {

                    ["__name"] = existing.Name,

                    ["__delay"] = existing.Delay,

                    ["__delayMax"] = existing.DelayMax,

                };

            var picDlg = new SearchPictureDialog(title, cur)

            {

                Owner = System.Windows.Application.Current.MainWindow,

            };

            return picDlg.ShowDialog() == true ? picDlg.Values : null;

        }



        var def = StepDefinitions.Get(type);

        // v0.8.3 — gotoLabel: offer the script's current labels as the combo options

        // (editable, so a not-yet-created name can still be typed in)

        FieldDef[] defFields = def.Fields.ToArray();

        if (type == "gotoLabel")

        {

            var names = new List<string>();

            void CollectLabels(IEnumerable<StepNode> nodes)

            {

                foreach (var n in nodes)

                {

                    if (n.Type == "label")

                    {

                        var nm = PropEx.GetString(n.Props, "label");

                        if (nm.Length > 0 && !names.Contains(nm)) names.Add(nm);

                    }

                    CollectLabels(n.Children);

                }

            }

            CollectLabels(Steps);

            defFields = def.Fields.Select(f => f.Key == "label" ? f with { Options = names.ToArray() } : f).ToArray();

        }

        var fields = defFields.Concat(new[]

        {

            new FieldDef("__name", "Step Name", FieldKind.Text, existing?.Name ?? ""),

            new FieldDef("__delay", "Delay after step (ms)", FieldKind.Int, (existing?.Delay ?? def.DefaultDelay).ToString()),

            new FieldDef("__delayMax", "Delay max (ms) — 0 = fixed; when > delay, a random in-between value is used every run (v0.7.8)", FieldKind.Int, (existing?.DelayMax ?? 0).ToString()),

        }).ToArray();



        Dictionary<string, object?>? current = null;

        if (existing is not null)

        {

            current = new Dictionary<string, object?>(existing.Props)

            {

                ["__name"] = existing.Name,

                ["__delay"] = existing.Delay,

                ["__delayMax"] = existing.DelayMax,

            };

        }



        // injected dialog tools per step type (§16.6.5 calibrate · §5.5.12 region picker)

        Func<Task<int?>>? calibrate = null;

        string? calibrateKey = null;

        Func<Task<(int x, int y, int w, int h)?>>? pickRegion = null;
        Func<Task<(int x, int y)?>>? pickPoint = null;
        Func<Task<string?>>? sampleMouse = null;
        Func<IReadOnlyDictionary<string, object?>, Task<string>>? previewBuzzer = null;

        if (type == "waitForSound") { calibrate = CalibrateSoundThreshold; calibrateKey = "threshold"; }
        if (type == "waitForLight") { calibrate = CalibrateLightRange; calibrateKey = "luxCenter"; }   // v0.9.39 — BH1750 range centre

        if (type is "randomMousePosition" or "findImage") pickRegion = PickRegionOnScreen;
        if (type == "mouseMove") pickPoint = PickPointOnScreen;
        if (type is "mouseMove" or "randomMousePosition") sampleMouse = SampleHandMovementAsync;
        if (type == "buzzer") previewBuzzer = PreviewBuzzerAsync;
        if (type == "waitForSound") previewBuzzer = PreviewArmBuzzerAsync;



        var dlg = new StepDialog(title, fields, current, calibrate, calibrateKey, pickRegion, type, sampleMouse, pickPoint, previewBuzzer)

        {

            Owner = System.Windows.Application.Current.MainWindow,

        };

        return dlg.ShowDialog() == true ? dlg.Values : null;

    }

    private async Task<string> PreviewBuzzerAsync(IReadOnlyDictionary<string, object?> values)
        => await PreviewBuzzerCommandsAsync(StepDefinitions.BuildBuzzerCommands(values));

    private async Task<string> PreviewArmBuzzerAsync(IReadOnlyDictionary<string, object?> values)
    {
        var commands = StepDefinitions.BuildArmBuzzerCommands(values);
        if (commands.Count == 0)
            throw new InvalidOperationException("ابتدا صدای مسلح‌شدن را از حالت off خارج کنید.");
        return await PreviewBuzzerCommandsAsync(commands);
    }

    private async Task<string> PreviewBuzzerCommandsAsync(IReadOnlyList<string> commands)
    {
        if (Connection != ConnectionState.Connected || _bridge is null
            || _bridge.State != BridgeState.Connected)
            throw new InvalidOperationException("ابتدا برد را Connect کنید.");

        foreach (string command in commands)
        {
            if (command.StartsWith("DLY|", StringComparison.Ordinal))
            {
                if (int.TryParse(command[4..], out int delay) && delay > 0)
                    await Task.Delay(delay);
                continue;
            }
            int duration = 0;
            var parts = command.Split('|', 2)[1].Split(',');
            _ = int.TryParse(parts.ElementAtOrDefault(1), out duration);
            string reply = await _bridge.SendAsync(command, Math.Max(3.0, duration / 1000.0 + 2.0));
            if (!reply.StartsWith("OK|BEEP", StringComparison.Ordinal))
                throw new InvalidOperationException("برد پیش‌شنیدن را نپذیرفت: " + reply);
        }
        Log("buzzer preview played");
        return "پخش شد ✓";
    }



    private static Dictionary<string, object?> Strip(Dictionary<string, object?> vals)

        => vals.Where(kv => kv.Key != "__name" && kv.Key != "__delay" && kv.Key != "__delayMax")

               .ToDictionary(kv => kv.Key, kv => kv.Value);



    /// <summary>True while the focus is inside a text field — blocks Delete/F2 hotkeys.</summary>

    private static bool EditingText()

        => Keyboard.FocusedElement is System.Windows.Controls.TextBox;



    public void MarkDirty()

    {

        _dirty = true;

        UpdateFileText();

    }



    /// <summary>
    /// v0.9.39 — Calibrate for "Wait For Light": samples the BH1750 (GY-302/GY-30) on the Pico
    /// for 3 s with LCAL and returns the averaged lux as the range centre. The dialog writes it into
    /// luxCenter; the step then matches luxCenter +/- luxTolerance. Keep 50+ lux of dead zone between
    /// two screen states and always calibrate with the dark hood mounted (docs §16.7).
    /// </summary>
    private async Task<int?> CalibrateLightRange()
    {
        if (Connection != ConnectionState.Connected || _bridge is null)
        {
            System.Windows.MessageBox.Show("ابتدا برد را وصل کن — کالیبراسیون نور مقدار لوکس را از سنسور BH1750 روی I2C می‌خواند.",
                "Calibrate light", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        try
        {
            var reply = await _bridge.SendAsync("LCAL|3000", 8.0);   // 3s window on the shown screen state
            var mAvg = Regex.Match(reply, @"avg=(\d+)");
            var mMin = Regex.Match(reply, @"min=(\d+)");
            var mMax = Regex.Match(reply, @"max=(\d+)");
            if (mAvg.Success)
            {
                int avg = int.Parse(mAvg.Groups[1].Value);
                if (mMin.Success && mMax.Success)
                {
                    int lo = int.Parse(mMin.Groups[1].Value), hi = int.Parse(mMax.Groups[1].Value);
                    int spread = Math.Max(0, hi - lo);
                    Log($"calibrate light: min={lo} max={hi} avg={avg} (spread {spread} lux)");
                    if (spread > 200)
                        Log("calibrate light: the reading is unstable — fix the sensor on the screen with the dark hood and repeat");
                }
                else Log($"calibrate light: avg={avg}");
                Log($"calibrate light: centre {avg} — keep tolerance >= 50 lux and 50+ lux dead zone from other states");
                return avg;
            }
            Log("calibrate light: unexpected LCAL reply (" + reply + ")");
            System.Windows.MessageBox.Show("پاسخ LCAL قابل خواندن نبود. فرم‌ور؛ پیکو را با منوی File → Export Pico Firmware تازه بریز.",
                "Calibrate light", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        catch (Exception ex)
        {
            Log("calibrate light: LCAL failed (" + ex.Message + ")");
            System.Windows.MessageBox.Show("کالیبراسیون نور ناموفق بود: " + ex.Message,
                "Calibrate light", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    /// <summary>
    /// v0.9.39 — per-system Pico output: writes a CircuitPython bundle (code.py + boot.py +
    /// pico-calibration.json + README-FLASH.md) built from the "Wait For Light" steps of the open
    /// plan. Run this once on every PC (the lux ranges are machine specific) and copy the files
    /// onto the CIRCUITPY drive of the Raspberry Pi Pico.
    /// </summary>
    [RelayCommand]
    private void ExportPicoFirmware()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CircuitPython firmware (code.py)|code.py",
            FileName = "code.py",
            Title = "Export Pico firmware — choose the CIRCUITPY drive of this system",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // v0.9.44 — the exported firmware must respect the Play Options and the keyboard-board choice

            var v44mode = _settings.PlayRepeatMode;

            var v44secs = v44mode == "timed"

                ? (int)(_settings.PlayRepeatUnit switch

                {

                    "second" => TimeSpan.FromSeconds(Math.Max(1, _settings.PlayRepeatValue)),

                    "hour" => TimeSpan.FromHours(Math.Max(1, _settings.PlayRepeatValue)),

                    _ => TimeSpan.FromMinutes(Math.Max(1, _settings.PlayRepeatValue)),

                }).TotalSeconds

                : 0;

            var written = PicoFirmwareExporter.Export(dlg.FileName, Steps, Environment.MachineName,

                v44mode, Math.Max(1, _settings.PlayRepeatTimes), v44secs, _settings.KeyboardBoard == "promicro");
            foreach (var f in written) Log("pico export: " + Path.GetFileName(f));
            Log($"pico export: {written.Count} file(s) for system '{Environment.MachineName}' — copy them onto CIRCUITPY and put adafruit_hid into /lib");

            Log($"pico export: play options baked — loop={v44mode}, times={Math.Max(1, _settings.PlayRepeatTimes)}, seconds={v44secs}, keyboard={_settings.KeyboardBoard}");   // v0.9.44
            System.Windows.MessageBox.Show($"خروجی پیکو ساخته شد ({written.Count} فایل).\nبعد از کپی روی درایو CIRCUITPY، پوشه‌ی adafruit_hid را در /lib بگذار.\nدستور کامل در README-FLASH.md است.",
                "Export Pico firmware", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("pico export failed: " + ex.Message);
            System.Windows.MessageBox.Show("خروجی پیکو ناموفق بود: " + ex.Message,
                "Export Pico firmware", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// v0.9.65 — File → Export Pico Plan… (phase 2 of the portable line): compiles the open step
    /// tree into a portable plan.txt (PLAN|2, engine 0.9.66) plus
    /// plan_engine.py and README-PLAN.md. Unsupported steps BLOCK the export with a per-step
    /// message in the serial log — nothing is silently skipped (project rule).
    /// </summary>
    [RelayCommand]
    private void ExportPicoPlan()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Pico portable plan (plan.txt)|plan.txt",
            FileName = "plan.txt",
            Title = "Export Pico plan — choose where plan.txt is written (then copy the files onto CIRCUITPY)",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var written = PlanExporter.Export(dlg.FileName, Steps, _settings,
                (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight,
                _currentFile ?? "untitled", Environment.MachineName);
            foreach (var f in written) Log("pico plan: " + Path.GetFileName(f));
            Log($"pico plan: {written.Count} file(s) written — copy every generated *.txt plan + plan_engine.py onto CIRCUITPY (code.py comes from Export Pico Firmware)");
            System.Windows.MessageBox.Show($"پلن پیکو ساخته شد ({written.Count} فایل).\nهمه‌ی فایل‌های *.txt و plan_engine.py را در ریشه‌ی CIRCUITPY کپی کن (code.py از «Export Pico Firmware» می‌آید).\nراهنما در README-PLAN.md است.",
                "Export Pico plan", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (PlanExporter.PlanBlockedException bx)
        {
            Log($"pico plan export blocked ({bx.Errors.Count} problem(s)) — nothing written:");
            foreach (var e in bx.Errors) Log("  x " + e);
            System.Windows.MessageBox.Show($"اکسپورت پلن متوقف شد — {bx.Errors.Count} مورد روی PLAN|2 پشتیبانی نمی‌شود.\nجزئیات هر مورد در لاگ سریال هست (هیچ استپی بی‌صدا حذف نشد).",
                "Export Pico plan", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log("pico plan export failed: " + ex.Message);
            System.Windows.MessageBox.Show("خروجی پلن پیکو ناموفق بود: " + ex.Message,
                "Export Pico plan", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateFileText()

        => FileText = (_currentFile ?? "untitled") + (_dirty ? " *" : "");

}

