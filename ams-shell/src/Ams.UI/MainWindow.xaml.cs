using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ams.UI.Models;
using Ams.UI.ViewModels;
namespace Ams.UI;

/// <summary>
/// Plain Window on purpose: standard Windows chrome gives us the
/// minimize / maximize / close buttons (user feedback after v0.3).
/// v0.7: the steps TreeView is now an AMK-style flat numbered ListBox —
/// every node always renders (fixes imported/opened scripts showing flat),
/// Windows-native multi-select (Extended: Ctrl toggles, Shift ranges),
/// right-click context menu, drag &amp; drop move.
/// </summary>
public partial class MainWindow : Window
{
    private bool _forceClose;
    private bool _closeInProgress;
    private System.Windows.Point _dragStartPos;
    private bool _dragInProgress;
    private bool _scopeVeinEventsAttached;
    private bool _scopeVeinUpdateQueued;
    private StepNode? _scopeVeinAnchor;
    private double _scopeVeinLeft = double.NaN;
    private const double StepRowHeight = 28.0;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureRailMenuTimers();   // v0.9.45 — exactly one owner for rail popup open/close
    }

    /// <summary>v0.9.55 - hosts the settings sections inside this window instead of a
    /// separate floating dialog (user request). The caller hands over the detached content.</summary>
    public void ShowSettingsPanel(System.Windows.UIElement content)
    {
        SettingsHost.Content = content;
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    public void HideSettingsPanel()
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        SettingsHost.Content = null;
    }

    /// <summary>Raised when the panel's ✕ button is used (treated as Cancel).</summary>
    public event System.EventHandler? SettingsCloseRequested;

    private void BtnCloseSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsCloseRequested?.Invoke(this, System.EventArgs.Empty);
        HideSettingsPanel();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is Ams.UI.ViewModels.MainViewModel vm)
        {
            vm.ReloadKeyBindings();
            vm.AutoConnectOnStartup();   // v0.9.5 — شناسایی و اتصال خودکار برد
            AttachScopeVeinEvents(vm);
            QueueScopeVeinUpdate();
        }
    }

    // v0.9.3 — install the SAFE global-hotkey hook (HwndSource.AddHook). The old
    // SetWindowLongPtr(GWL_WNDPROC) subclass used a delegate with an extra ref-bool
    // parameter (the HwndSourceHook convention, not a native WndProc) → stack corruption
    // on the first window message → the process died before the window ever appeared.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Ams.UI.Services.GlobalHotkeyService.Install(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        Ams.UI.Services.GlobalHotkeyService.Uninstall();
        base.OnClosed(e);
    }

    // ── selection → view model (Extended mode gives Ctrl-toggle / Shift-range natively) ──
    private void CopySelectedLog_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (SerialLogList is null || SerialLogList.SelectedItems.Count == 0) return;
        var text = string.Join(Environment.NewLine, SerialLogList.SelectedItems.Cast<string>());
        if (text.Length > 0) System.Windows.Clipboard.SetText(text);
    }

    private void CopyAllLog_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var text = string.Join(Environment.NewLine, vm.LogLines);
        if (text.Length > 0) System.Windows.Clipboard.SetText(text);
    }

    private void StepsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.SelectedNodes = StepsList.SelectedItems.Cast<FlatStepRow>().Select(r => r.Node).ToList();
        vm.SelectedNode = (StepsList.SelectedItem as FlatStepRow)?.Node;
        QueueScopeVeinUpdate();
    }

    private void StepsList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.EditSelectedCommand.CanExecute(null))
            vm.EditSelectedCommand.Execute(null);
    }

    private void InspectorDelay_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.MarkDirty();
    }

    // ── v0.8.0 — accordion: clicking a row's ▾/▸ collapses/expands its subtree ──
    private void ScopeToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindRow(sender as DependencyObject) is { } item && item.DataContext is FlatStepRow row
            && DataContext is MainViewModel vm)
        {
            vm.FocusScopeVein(row.Node);   // v0.9.40 - reveal the vein for the toggled block
            vm.ToggleCollapse(row.Node);
            QueueScopeVeinUpdate();
            e.Handled = true;   // the click must not start a selection or a drag
        }
    }

    // ── v0.9.45 — stable rail-menu state machine ──────────────────────────────
    // Never open a ContextMenu directly inside MouseEnter: opening its popup changes mouse
    // capture and can immediately generate another Enter/Leave pair (the v0.9.43/44 flicker
    // and eventual crash). One dwell timer opens once; one watcher owns all close decisions.
    private System.Windows.Controls.ContextMenu? _openRailMenu;
    private System.Windows.Controls.Button? _openRailHost;
    private System.Windows.Controls.Button? _pendingRailHost;
    private readonly DispatcherTimer _railDwellTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _railWatchTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private int _railOutsideTicks;

    private void ConfigureRailMenuTimers()
    {
        _railDwellTimer.Tick += RailDwellTimer_Tick;
        _railWatchTimer.Tick += RailWatchTimer_Tick;
    }

    private void RailButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button host || host.ContextMenu is null) return;
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
        if (ReferenceEquals(_openRailHost, host) && _openRailMenu?.IsOpen == true) return;
        _pendingRailHost = host;
        _railDwellTimer.Stop();
        _railDwellTimer.Start();
    }

    private void RailDwellTimer_Tick(object? sender, EventArgs e)
    {
        _railDwellTimer.Stop();
        var host = _pendingRailHost;
        _pendingRailHost = null;
        if (host?.IsMouseOver != true || host.ContextMenu is null) return;
        OpenRailMenu(host);
    }

    private void OpenRailMenu(System.Windows.Controls.Button host)
    {
        if (ReferenceEquals(_openRailHost, host) && _openRailMenu?.IsOpen == true) return;
        CloseRailMenu();
        var menu = host.ContextMenu;
        if (menu is null) return;
        _openRailHost = host;
        _openRailMenu = menu;
        // v0.9.55 - suppress this icon's hover hint while its submenu is on screen
        System.Windows.Controls.ToolTipService.SetIsEnabled(host, false);
        _railOutsideTicks = 0;
        menu.PlacementTarget = host;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
        menu.Closed -= RailMenu_Closed;
        menu.Closed += RailMenu_Closed;
        menu.IsOpen = true;
        _railWatchTimer.Start();
    }

    private void RailWatchTimer_Tick(object? sender, EventArgs e)
    {
        var menu = _openRailMenu;
        var host = _openRailHost;
        if (menu?.IsOpen != true) { CloseRailMenu(); return; }
        // v0.9.46 — ContextMenu is a separate Popup/HWND; WPF IsMouseOver can remain stale
        // until the next click. Use the real cursor and each visual's screen rectangle instead.
        if (IsCursorInside(host) || IsCursorInside(menu))
        {
            _railOutsideTicks = 0;
            return;
        }
        if (++_railOutsideTicks >= 3) CloseRailMenu();   // ≈360 ms outside BOTH host and popup
    }

    private static bool IsCursorInside(FrameworkElement? element)
    {
        if (element is null || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;
        try
        {
            var topLeft = element.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = element.PointToScreen(new System.Windows.Point(element.ActualWidth, element.ActualHeight));
            var cursor = System.Windows.Forms.Control.MousePosition;
            return cursor.X >= Math.Min(topLeft.X, bottomRight.X)
                && cursor.X <= Math.Max(topLeft.X, bottomRight.X)
                && cursor.Y >= Math.Min(topLeft.Y, bottomRight.Y)
                && cursor.Y <= Math.Max(topLeft.Y, bottomRight.Y);
        }
        catch (InvalidOperationException) { return false; }   // visual disconnected while popup closes
    }

    private void RailBorder_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _pendingRailHost = null;
        _railDwellTimer.Stop();
        if (_openRailMenu?.IsOpen == true && !_railWatchTimer.IsEnabled) _railWatchTimer.Start();
    }

    private void RailMenu_Closed(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _openRailMenu)) ClearRailMenuState();
    }

    private void CloseRailMenu()
    {
        var menu = _openRailMenu;
        ClearRailMenuState();
        if (menu?.IsOpen == true) menu.IsOpen = false;
    }

    private void ClearRailMenuState()
    {
        // v0.9.55 - the hover hint comes back as soon as the submenu is gone
        if (_openRailHost is not null) System.Windows.Controls.ToolTipService.SetIsEnabled(_openRailHost, true);
        _railWatchTimer.Stop();
        _railOutsideTicks = 0;
        _openRailMenu = null;
        _openRailHost = null;
    }

    private void RailSubmenu_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? d = sender as DependencyObject;
        while (d is not null and not System.Windows.Controls.Button) d = VisualTreeHelper.GetParent(d);
        if (d is System.Windows.Controls.Button host && host.ContextMenu is not null)
        {
            _pendingRailHost = null;
            _railDwellTimer.Stop();
            OpenRailMenu(host);
            e.Handled = true;   // the caret must not also add the section's default step
        }
    }

    // ── right-click: Windows behavior — if the row is not in the current
    // selection, the selection collapses to it; otherwise the selection is kept ──
    private void StepsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindRow(e.OriginalSource as DependencyObject) is not { } item) return;
        if (!item.IsSelected)
            StepsList.SelectedItem = item.DataContext;
        item.Focus();
    }

    // ── drag & drop move ──
    // Drag a row onto another row: top quarter = insert before, bottom quarter =
    // insert after, middle = drop INTO when the target is a container.
    // Dropping on empty space moves the step to the root end.
    // Multi-drag is deferred: the pressed node moves; drag is disabled while
    // Ctrl/Shift are held so selection gestures never start a drag.

    private void StepsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragStartPos = e.GetPosition(null);

    private void StepsList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragInProgress || e.LeftButton != MouseButtonState.Pressed) return;
        if (System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftCtrl)
            || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightCtrl)
            || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftShift)
            || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightShift)) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPos.X) < 6 && Math.Abs(pos.Y - _dragStartPos.Y) < 6) return;
        var item = FindRow(e.OriginalSource as DependencyObject);
        if (item is null || !item.IsSelected || item.DataContext is not FlatStepRow row) return;
        _dragInProgress = true;
        try { DragDrop.DoDragDrop(StepsList, row.Node, System.Windows.DragDropEffects.Move); }
        catch { /* drag cancelled */ }
        finally { _dragInProgress = false; }
    }

    private void StepsList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(StepNode)) ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        e.Handled = true;

        // v0.9.48 — auto-scroll while dragging near the edges: without it, a bottom row could never
        // be dragged to the top of a long plan (user report: the move simply did not happen).
        if (e.Effects != System.Windows.DragDropEffects.Move) return;
        if (FindDescendant<ScrollViewer>(StepsList) is not { } sv) return;
        double y = e.GetPosition(StepsList).Y;
        if (y < 28) sv.ScrollToVerticalOffset(sv.VerticalOffset - 14);
        else if (y > StepsList.ActualHeight - 28) sv.ScrollToVerticalOffset(sv.VerticalOffset + 14);
    }

    /// <summary>v0.9.48 — first visual-tree descendant of the given type (reaches the list's ScrollViewer).</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } inner) return inner;
        }
        return null;
    }

    private void StepsList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(StepNode)) is not StepNode node) return;
        if (DataContext is not MainViewModel vm) return;

        var item = FindRow(e.OriginalSource as DependencyObject);
        var target = (item?.DataContext as FlatStepRow)?.Node;
        string mode = "after";
        if (item is not null && target is not null)
        {
            var p = e.GetPosition(item);
            // v0.9.49 — only containers and Else rows have a meaningful middle "into" band; plain
            // rows split 50/50, so a middle drop resolves by direction and can never be a no-op.
            bool canInto = StepDefinitions.AcceptsChildren(target) || MainViewModel.IsElseMarkerRow(target);
            double edge = canInto ? 0.25 : 0.5;
            if (p.Y < item.ActualHeight * edge) mode = "before";
            else if (p.Y > item.ActualHeight * (1.0 - edge)) mode = "after";
            else mode = "into";
        }
        vm.MoveNode(node, target, mode);
        QueueScopeVeinUpdate();
    }

    /// <summary>v0.9.36 — events that can change the overlay geometry without changing selection.</summary>
    private void AttachScopeVeinEvents(MainViewModel vm)
    {
        if (_scopeVeinEventsAttached) return;
        _scopeVeinEventsAttached = true;
        vm.FlatSteps.CollectionChanged += FlatSteps_CollectionChanged;
        StepsList.SizeChanged += (_, _) => QueueScopeVeinUpdate();
        StepsList.ItemContainerGenerator.StatusChanged += (_, _) => QueueScopeVeinUpdate();
        StepsList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => QueueScopeVeinUpdate()));
    }

    private void FlatSteps_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => QueueScopeVeinUpdate();

    private void QueueScopeVeinUpdate()
    {
        if (_scopeVeinUpdateQueued) return;
        _scopeVeinUpdateQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _scopeVeinUpdateQueued = false;
            UpdateScopeVeinOverlay();
        }));
    }

    /// <summary>One physical Border spans the selected scope. Its X comes from the ACTUAL rendered
    /// SummaryText of the scope head (responsive to window/column width); its Y/height come from the
    /// visible row range. This cannot split or stair-step across child depths.</summary>
    private void UpdateScopeVeinOverlay()
    {
        ScopeVeinBracket.Visibility = Visibility.Collapsed;
        ScopeVeinElseTick.Visibility = Visibility.Collapsed;   // v0.9.38
        // v0.9.40 - ScopeVeinNode is the toggled block when the accordion toggle was pressed,
        // otherwise the selection, so both gestures reveal the same vein.
        if (DataContext is not MainViewModel vm || vm.ScopeVeinNode is null
            || !vm.TryGetScopeRange(vm.ScopeVeinNode, out int start, out int end)
            || start < 0 || end < start || end >= vm.FlatSteps.Count)
        {
            _scopeVeinAnchor = null;
            _scopeVeinLeft = double.NaN;
            return;
        }

        if (!ReferenceEquals(_scopeVeinAnchor, vm.ScopeVeinNode))
        {
            _scopeVeinAnchor = vm.ScopeVeinNode;
            _scopeVeinLeft = double.NaN;
        }

        // v0.9.37 — anchor at the LEFTMOST realized row of the scope. The head and its Else/End If
        // markers share the shallowest indent, so the vein always spans the Else row as well.
        double left = double.NaN;
        for (int i = start; i <= end; i++)
        {
            if (StepsList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem row) continue;
            // v0.9.38 — measure the whole text cell (toggle + description), so the vein never overlaps the toggle.
            var summary = FindNamedDescendant<FrameworkElement>(row, "RowTextAnchor")
                          ?? FindNamedDescendant<FrameworkElement>(row, "SummaryText");
            if (summary is null) continue;
            var p = summary.TranslatePoint(new System.Windows.Point(0, 0), ScopeVeinCanvas);
            double candidate = Math.Max(2, p.X - 14);   // 10px cap + 4px breathing room before text
            if (double.IsNaN(left) || candidate < left) left = candidate;
        }
        if (!double.IsNaN(left)) _scopeVeinLeft = left;
        if (double.IsNaN(_scopeVeinLeft)) return;

        // Find any realized row in the range. Fixed 28-DIP rows let us infer the full range even when
        // its head or tail is outside the viewport; the Canvas clips the off-screen portion cleanly.
        ListBoxItem? reference = null;
        int referenceIndex = -1;
        for (int i = start; i <= end; i++)
        {
            if (StepsList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
            {
                reference = item;
                referenceIndex = i;
                break;
            }
        }
        if (reference is null) return;

        double rowHeight = reference.ActualHeight > 0 ? reference.ActualHeight : StepRowHeight;
        double referenceTop = reference.TranslatePoint(new System.Windows.Point(0, 0), ScopeVeinCanvas).Y;
        // Bracket caps meet the vertical center of the head/tail rows, like the reference app.
        double top = referenceTop - (referenceIndex - start) * rowHeight + rowHeight / 2;
        double bottom = top + (end - start) * rowHeight;

        // Prefer the realized tail's exact center (guards against DPI/template height changes).
        if (StepsList.ItemContainerGenerator.ContainerFromIndex(end) is ListBoxItem tail)
            bottom = tail.TranslatePoint(new System.Windows.Point(0, tail.ActualHeight / 2), ScopeVeinCanvas).Y;

        if (bottom <= 0 || top >= ScopeVeinCanvas.ActualHeight) return;
        Canvas.SetLeft(ScopeVeinBracket, _scopeVeinLeft);
        Canvas.SetTop(ScopeVeinBracket, top);
        ScopeVeinBracket.Height = Math.Max(4, bottom - top);
        ScopeVeinBracket.Visibility = Visibility.Visible;

        // v0.9.38 — mark the Else row with the same small bend used by the head/tail caps. The bracket
        // already spans If → End If (Else and all of its sub-steps included); this makes the split visible.
        if (vm.TryGetScopeElseRow(vm.ScopeVeinNode, out int elseRow) && elseRow > start && elseRow < end)
        {
            double elseY = StepsList.ItemContainerGenerator.ContainerFromIndex(elseRow) is ListBoxItem elseItem
                ? elseItem.TranslatePoint(new System.Windows.Point(0, elseItem.ActualHeight / 2), ScopeVeinCanvas).Y
                : top + (elseRow - start) * rowHeight;
            if (elseY > top && elseY < bottom)
            {
                Canvas.SetLeft(ScopeVeinElseTick, _scopeVeinLeft);
                Canvas.SetTop(ScopeVeinElseTick, elseY - 1);
                ScopeVeinElseTick.Visibility = Visibility.Visible;
            }
        }
    }

    private static T? FindNamedDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && found.Name == name) return found;
            if (FindNamedDescendant<T>(child, name) is { } nested) return nested;
        }
        return null;
    }

    private static ListBoxItem? FindRow(DependencyObject? d)
    {
        while (d is not null and not ListBoxItem)
            d = VisualTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }

    // ── Play Options panel collapse/expand (AMK bottom panel, v0.7.5) ──
    private void PlayOptToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = PlayOptBody.Visibility != Visibility.Visible;
        PlayOptBody.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PlayOptToggleText.Text = show ? "▾ Play Options" : "▸ Play Options";
    }

    // Keep Play Options useful without letting its generated AutoCycle cards hide the
    // pipeline tabs and step list on a short monitor. Its viewport gets at most 45% of
    // the live workspace (with sensible absolute limits); the remainder belongs to steps.
    private void StepsWorkspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double minExpandedHeight = 150;
        const double maxExpandedHeight = 420;
        var responsiveHeight = Math.Clamp(e.NewSize.Height * 0.45, minExpandedHeight, maxExpandedHeight);
        PlayOptionsPanel.MaxHeight = responsiveHeight;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_forceClose) return;
        e.Cancel = true;
        if (_closeInProgress) return;

        var vm = DataContext as MainViewModel;
        if (vm is not null && !vm.ConfirmDiscard()) return;

        // The first click owns shutdown. Hide immediately so Windows cannot send
        // another close request (and its blocked-window beep) while HALT/BYE waits.
        _closeInProgress = true;
        Hide();
        try
        {
            if (vm is not null)
                await vm.ShutdownAsync();   // HALT + BYE before exit (§11.3)
        }
        finally
        {
            _forceClose = true;
            Close();
        }
    }
}
