using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ams.UI.Models;
using Ams.UI.Services;

namespace Ams.UI.Views;

/// <summary>
/// Generic modal step dialog. Builds its form from the step type's FieldDefs
/// (design doc §5.5.x dialog maps), validates int fields on OK, and returns the
/// edited values in <see cref="Values"/>.
/// Optional injected tools: a sound Calibrate button (SCAL on the board, §16.6.5)
/// and a full-screen Region Picker button for x/y/w/h field sets (§5.5.12).
/// </summary>
public partial class StepDialog : Window
{
    private readonly IReadOnlyList<FieldDef> _fields;
    private readonly string? _stepType;   // v0.9.24 — resolved from the shared Fields reference

    private readonly Dictionary<string, FrameworkElement> _controls = new();
    private readonly List<(FieldDef Def, System.Windows.Controls.TextBlock? Label, FrameworkElement Control)> _rows = new();
    private readonly Func<Task<int?>>? _calibrate;
    private readonly string? _calibrateTargetKey;
    private readonly Func<Task<(int x, int y, int w, int h)?>>? _pickRegion;
    private readonly Func<Task<(int x, int y)?>>? _pickPoint;
    private readonly Func<Task<string?>>? _sampleMouse;
    private readonly Func<IReadOnlyDictionary<string, object?>, Task<string>>? _previewBuzzer;
    private System.Windows.Controls.TextBlock? _sampleStatus;
    private Wpf.Ui.Controls.Button? _pointPickerButton;

    /// <summary>Edited values when the dialog closes with OK; otherwise null.</summary>
    public Dictionary<string, object?>? Values { get; private set; }

    public StepDialog(string title, IReadOnlyList<FieldDef> fields, IReadOnlyDictionary<string, object?>? current,
                      Func<Task<int?>>? calibrate = null, string? calibrateTargetKey = null,
                      Func<Task<(int x, int y, int w, int h)?>>? pickRegion = null, string? stepType = null,
                      Func<Task<string?>>? sampleMouse = null,
                      Func<Task<(int x, int y)?>>? pickPoint = null,
                      Func<IReadOnlyDictionary<string, object?>, Task<string>>? previewBuzzer = null)
    {
        InitializeComponent();
        // v0.9.24 — Persian dialog chrome: translate the New:/Edit: prefix; action names stay English
        Title = title.StartsWith("New: ") ? "جدید: " + title[5..]
              : title.StartsWith("Edit: ") ? "ویرایش: " + title[6..]
              : title;
        _fields = fields;
        _stepType = stepType ?? StepDefinitions.FindTypeByFields(fields);   // v0.9.29 — explicit type key: the concatenated fields array never reference-matched a definition, so every per-step override (FaByStep) silently fell back
        _calibrate = calibrate;
        _calibrateTargetKey = calibrateTargetKey;
        _pickRegion = pickRegion;
        _pickPoint = pickPoint;
        _sampleMouse = sampleMouse;
        _previewBuzzer = previewBuzzer;
        BuildForm(current);
        WireConditionalVisibility();   // v0.9.14 — hide fields ruled out by the current mode/toggle
    }

    private void BuildForm(IReadOnlyDictionary<string, object?>? current)
    {
        foreach (var f in _fields)
        {
            object? cur = current is not null && current.TryGetValue(f.Key, out var v) ? v : f.Default;

            // The recorded path is an opaque compact payload. Keep it in the dialog model,
            // never expose it as an editable textbox; the sampler/status controls own it.
            if ((_stepType is "mouseMove" or "randomMousePosition") && f.Key == "handSample")
            {
                var hidden = new Wpf.Ui.Controls.TextBox { Text = PropAsString(cur), Visibility = Visibility.Collapsed };
                _controls[f.Key] = hidden;
                _rows.Add((f, null, hidden));
                continue;
            }

            System.Windows.Controls.TextBlock? label = null;
            if (f.Kind != FieldKind.Check)
            {
                label = new System.Windows.Controls.TextBlock
                {
                    Text = StepTextsFa.Get(_stepType, f.Key, f.Label),   // v0.9.24 — Persian description
                    TextAlignment = TextAlignment.Right,                    // v0.9.24 — right-aligned RTL
                    Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextSecondaryBrush"],
                    Margin = new Thickness(0, 8, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                };
                FormPanel.Children.Add(label);
            }

            FrameworkElement c = f.Kind switch
            {
                FieldKind.Multiline => new Wpf.Ui.Controls.TextBox
                {
                    AcceptsReturn = true, Height = 90,
                    TextWrapping = TextWrapping.Wrap, Text = PropAsString(cur),
                },
                FieldKind.Int => new Wpf.Ui.Controls.TextBox
                {
                    Text = PropAsString(cur),
                    FontFamily = new System.Windows.Media.FontFamily("Cascadia Code"),
                },
                FieldKind.Float => new Wpf.Ui.Controls.TextBox
                {
                    Text = PropAsString(cur),
                    FontFamily = new System.Windows.Media.FontFamily("Cascadia Code"),
                },
                FieldKind.Combo => MakeCombo(f, cur),
                FieldKind.EditableCombo => MakeCombo(f, cur, editable: true),
                FieldKind.AudioDevice => MakeAudioDeviceCombo(cur),   // v0.9.43 — real device names, not an index
                FieldKind.Check => new System.Windows.Controls.CheckBox
                {
                    Content = StepTextsFa.Get(_stepType, f.Key, f.Label),   // v0.9.24 — Persian checkbox text
                    IsChecked = PropAsBool(cur),
                    Margin = new Thickness(0, 8, 0, 0),
                },
                _ => new Wpf.Ui.Controls.TextBox { Text = PropAsString(cur) },
            };

            _controls[f.Key] = c;
            _rows.Add((f, label, c));   // v0.9.14 — tracked for conditional visibility

            // Calibrate button next to the threshold field (§16.6.5)
            if (_calibrate is not null && f.Key == _calibrateTargetKey)
            {
                var btn = new Wpf.Ui.Controls.Button
                {
                    Content = "کالیبره…",
                    Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                };
                btn.Click += OnCalibrate;
                FormPanel.Children.Add(c);
                FormPanel.Children.Add(btn);
                continue;
            }

            FormPanel.Children.Add(c);

            if (((_stepType == "buzzer" && f.Key == "pattern")
                 || (_stepType == "waitForSound" && f.Key == "armCuePattern"))
                && _previewBuzzer is not null)
            {
                var preview = new Wpf.Ui.Controls.Button
                {
                    Content = "شنیدن صدای انتخاب‌شده روی بازر…",
                    Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                preview.Click += OnPreviewBuzzer;
                FormPanel.Children.Add(preview);
            }

            if (_stepType == "mouseMove" && f.Key == "moveMode" && _sampleMouse is not null)
                AddMouseSampleControls(current);

            if (_stepType == "randomMousePosition" && f.Key == "h" && _sampleMouse is not null)
                AddMouseSampleControls(current);

            // A fixed Move to Position needs a point picker, not the rectangular
            // picker used by Random Mouse Position and Find Image.
            if (_pickPoint is not null && f.Key == "y"
                && _controls.ContainsKey("x") && _controls.ContainsKey("y"))
            {
                _pointPickerButton = new Wpf.Ui.Controls.Button
                {
                    Content = "انتخاب مختصات روی صفحه…  (کلیک = تأیید · Esc = لغو)",
                    Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                _pointPickerButton.Click += OnPickPoint;
                FormPanel.Children.Add(_pointPickerButton);
            }

            // Browse button for path-type fields (playAudio / runExe / openFile) (§5.5.x)
            // Uses a plain WPF Button so it stays visible on the dark panel background.
            if (!string.IsNullOrWhiteSpace(f.BrowseFilter))
            {
                var btn = new System.Windows.Controls.Button
                {
                    Content = "مرور…",
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Padding = new Thickness(8, 2, 8, 2),
                    FontSize = 12,
                    Tag = f.Key,   // store field key for handler
                    Background = (System.Windows.Media.Brush)
                        System.Windows.Application.Current.Resources["AccentBrush"],
                    Foreground = (System.Windows.Media.Brush)
                        System.Windows.Application.Current.Resources["TextPrimaryBrush"],
                };
                btn.Click += OnBrowseClick;
                FormPanel.Children.Add(btn);
            }

            // Region Picker button after the h field of an x/y/w/h set (§5.5.12)
            if (_pickRegion is not null && f.Key == "h"
                && _controls.ContainsKey("x") && _controls.ContainsKey("y") && _controls.ContainsKey("w"))
            {
                var btn = new Wpf.Ui.Controls.Button
                {
                    Content = "انتخاب ناحیه روی صفحه…  (بکش · دابل‌کلیک تأیید می‌کند · Esc لغو)",
                    Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                    Margin = new Thickness(0, 4, 0, 0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                };
                btn.Click += OnPickRegion;
                FormPanel.Children.Add(btn);
            }
        }
    }

    private void AddMouseSampleControls(IReadOnlyDictionary<string, object?>? current)
    {
        var sampleButton = new Wpf.Ui.Controls.Button
        {
            Content = "نمونه‌گیری از حرکت دست — ۱۰ ثانیه…",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        sampleButton.Click += OnSampleMouse;
        FormPanel.Children.Add(sampleButton);
        _sampleStatus = new System.Windows.Controls.TextBlock
        {
            Text = current is not null && current.TryGetValue("handSample", out var stored)
                   && HandMovementSample.TryDecode(PropAsString(stored), out var ready)
                ? SampleStatus(ready)
                : "هنوز نمونه‌ای ثبت نشده است.",
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextSecondaryBrush"],
            Margin = new Thickness(0, 4, 0, 2), TextWrapping = TextWrapping.Wrap,
        };
        FormPanel.Children.Add(_sampleStatus);
    }


    private async void OnSampleMouse(object sender, RoutedEventArgs e)
    {
        if (_sampleMouse is null || sender is not Wpf.Ui.Controls.Button btn) return;
        btn.IsEnabled = false; btn.Content = "در حال ضبط حرکت دست — ۱۰ ثانیه…";
        if (_sampleStatus is not null) _sampleStatus.Text = "فقط حرکت نشانگر ثبت می‌شود؛ متن، کلید و کلیک ثبت نمی‌شوند.";
        try
        {
            var encoded = await _sampleMouse();
            if (encoded is null || !HandMovementSample.TryDecode(encoded, out var sample))
            {
                if (_sampleStatus is not null) _sampleStatus.Text = "حرکتی ثبت نشد؛ دوباره تلاش کنید.";
                return;
            }
            if (_controls.TryGetValue("handSample", out var payload) && payload is Wpf.Ui.Controls.TextBox hidden) hidden.Text = encoded;
            if (_stepType == "mouseMove"
                && _controls.TryGetValue("moveMode", out var mode)
                && mode is System.Windows.Controls.ComboBox cb) cb.SelectedItem = "handSample";
            if (_sampleStatus is not null) _sampleStatus.Text = SampleStatus(sample);
        }
        finally { btn.IsEnabled = true; btn.Content = "نمونه‌گیری از حرکت دست — ۱۰ ثانیه…"; }
    }
    private async void OnCalibrate(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button btn || _calibrate is null || _calibrateTargetKey is null) return;
        btn.IsEnabled = false;
        btn.Content = "در حال نمونه‌برداری از سنسور صدا — سکون…";
        try
        {
            var v = await _calibrate();
            if (v is not null && _controls[_calibrateTargetKey] is Wpf.Ui.Controls.TextBox tb)
                tb.Text = v.Value.ToString();
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = "کالیبره…";
        }
    }

    private async void OnPickRegion(object sender, RoutedEventArgs e)
    {
        if (_pickRegion is null) return;
        // Note: we do NOT call Hide()/Show() here. RegionPickerWindow is Topmost and
        // fully opaque, so the StepDialog remains safely behind it. Calling Hide()
        // inside a ShowDialog()-owned modal loop puts WPF into an invalid state and
        // crashes the app on Show() resume.
        var region = await _pickRegion();
        if (region is not null)
        {
            SetIntText("x", region.Value.x);
            SetIntText("y", region.Value.y);
            SetIntText("w", region.Value.w);
            SetIntText("h", region.Value.h);
        }
    }

    private async void OnPickPoint(object sender, RoutedEventArgs e)
    {
        if (_pickPoint is null) return;
        var point = await _pickPoint();
        if (point is null) return;
        SetIntText("x", point.Value.x);
        SetIntText("y", point.Value.y);
    }

    private void SetIntText(string key, int value)
    {
        if (_controls.TryGetValue(key, out var c) && c is Wpf.Ui.Controls.TextBox tb)
            tb.Text = value.ToString();
    }

    // v0.9.14 — conditional visibility: fields with HideWhenKey/HideWhenValue collapse while the
    // sibling combo/checkbox currently holds that value (e.g. typeText tuning hides in clipboard
    // mode; Find Image human-move fields hide when the humanMove box is off).
    private void WireConditionalVisibility()
    {
        if (_controls.TryGetValue("moveMode", out var moveMode) && moveMode is System.Windows.Controls.ComboBox modeCombo)
            modeCombo.SelectionChanged += (_, _) => ApplyConditionalVisibility();
        foreach (var dep in _rows.Where(r => _rows.Any(x => x.Def.HideWhenKey == r.Def.Key)))
        {
            if (dep.Control is System.Windows.Controls.ComboBox combo)
                combo.SelectionChanged += (_, _) => ApplyConditionalVisibility();
            else if (dep.Control is System.Windows.Controls.CheckBox chk)
            {
                chk.Checked += (_, _) => ApplyConditionalVisibility();
                chk.Unchecked += (_, _) => ApplyConditionalVisibility();
            }
        }
        ApplyConditionalVisibility();
    }

    private void ApplyConditionalVisibility()
    {
        foreach (var (def, label, control) in _rows)
        {
            if (def.HideWhenKey is null) continue;
            var dep = _rows.FirstOrDefault(r => r.Def.Key == def.HideWhenKey);
            string? depValue = dep.Control switch
            {
                System.Windows.Controls.ComboBox cb => cb.SelectedItem as string,
                System.Windows.Controls.CheckBox chk => (chk.IsChecked == true).ToString(),
                _ => null,
            };
            // v0.9.29 — HideUnlessValue joins HideWhenValue: forLoop.mode has three laws and a field can be hidden by two
            var vis = StepFieldVisibility.FieldVisible(depValue, def.HideWhenValue, def.HideUnlessValue) ? Visibility.Visible : Visibility.Collapsed;
            if (_stepType == "mouseMove" && IsHandSampleMode() && HandSampleTuningKeys.Contains(def.Key)) vis = Visibility.Collapsed;
            control.Visibility = vis;
            if (label is not null) label.Visibility = vis;
        }
        if (_stepType == "mouseMove")
        {
            foreach (var row in _rows.Where(r => HandSampleTuningKeys.Contains(r.Def.Key)))
            {
                var vis = IsHandSampleMode() ? Visibility.Collapsed : Visibility.Visible;
                row.Control.Visibility = vis; if (row.Label is not null) row.Label.Visibility = vis;
            }
            if (_pointPickerButton is not null)
                _pointPickerButton.Visibility = IsHandSampleMode() ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private bool IsHandSampleMode()
        => _controls.TryGetValue("moveMode", out var mode) && mode is System.Windows.Controls.ComboBox cb
           && (cb.SelectedItem as string) == "handSample";

    private string SampleStatus(HandMovementSample.Sample sample)
    {
        var delta = HandMovementSample.Displacement(sample);
        if (_stepType == "randomMousePosition"
            && HandMovementSample.TryGetSpeedRange(sample, out var speedMin, out var speedMax))
            return $"نمونه آماده: {sample.Segments.Count} بخش · سرعت شخصی {speedMin} تا {speedMax} پیکسل/ثانیه؛ مقصد و هندسه همچنان تصادفی‌اند.";
        return $"نمونه آماده: {sample.Segments.Count} بخش · جابه‌جایی نسبی Δ({delta.X},{delta.Y}) از موقعیت فعلی";
    }

    private static readonly HashSet<string> HandSampleTuningKeys = new(StringComparer.Ordinal)
    {
        "x", "y", "human", "pauseBeforeMin", "pauseBeforeMax", "pauseAfterMin", "pauseAfterMax",
        "midPauseChance", "midPauseMin", "midPauseMax", "overshootChance", "curveMinPct",
        "curveMaxPct", "moveTimeMin", "moveTimeMax"
    };

    // v0.9.43 — the audio output device is picked by NAME from the devices actually present
    // (user request: a dropdown instead of the numeric "شاخص خروجی صدا"). Tag = the int index.
    private static System.Windows.Controls.ComboBox MakeAudioDeviceCombo(object? cur)
    {
        var cb = new System.Windows.Controls.ComboBox();
        int current = int.TryParse(PropAsString(cur), out var v) ? v : -1;
        cb.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "خودکار (پیش‌فرض سیستم)", Tag = -1 });
        try
        {
            int n = NAudio.Wave.WaveOut.DeviceCount;
            for (int i = 0; i < n; i++)
                cb.Items.Add(new System.Windows.Controls.ComboBoxItem
                {
                    Content = $"#{i} — {NAudio.Wave.WaveOut.GetCapabilities(i).ProductName}",
                    Tag = i,
                });
        }
        catch { /* no audio device / NAudio failure — the auto entry still works */ }
        foreach (System.Windows.Controls.ComboBoxItem it in cb.Items)
            if ((int)it.Tag! == current) { cb.SelectedItem = it; break; }
        if (cb.SelectedIndex < 0) cb.SelectedIndex = 0;
        return cb;
    }

    private static System.Windows.Controls.ComboBox MakeCombo(FieldDef f, object? cur, bool editable = false)
    {
        var cb = new System.Windows.Controls.ComboBox { IsEditable = editable };
        foreach (var o in f.Options ?? Array.Empty<string>()) cb.Items.Add(o);
        if (editable)
        {
            // keep the current value visible even when it is not among the offered options
            var text = PropAsString(cur);
            if (text.Length > 0 && !cb.Items.Contains(text)) cb.Items.Add(text);
            cb.Text = text;
        }
        else
        {
            cb.SelectedItem = PropAsString(cur);
            if (cb.SelectedIndex < 0 && cb.Items.Count > 0) cb.SelectedIndex = 0;
        }
        return cb;
    }

    private async void OnPreviewBuzzer(object sender, RoutedEventArgs e)
    {
        if (_previewBuzzer is null || sender is not Wpf.Ui.Controls.Button button) return;
        button.IsEnabled = false;
        string original = button.Content?.ToString() ?? "شنیدن";
        button.Content = "در حال پخش…";
        try
        {
            var values = ReadValues();
            _ = _stepType == "waitForSound"
                ? StepDefinitions.BuildArmBuzzerCommands(values)
                : StepDefinitions.BuildBuzzerCommands(values); // validate before touching the board
            button.Content = await _previewBuzzer(values);
            await Task.Delay(900);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "پیش‌شنیدن Buzzer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            button.Content = original;
            button.IsEnabled = true;
        }
    }

    private Dictionary<string, object?> ReadValues()
    {
        var vals = new Dictionary<string, object?>();
        foreach (var f in _fields)
        {
            vals[f.Key] = f.Kind switch
            {
                FieldKind.Int => int.Parse(((Wpf.Ui.Controls.TextBox)_controls[f.Key]).Text),
                FieldKind.Check => ((System.Windows.Controls.CheckBox)_controls[f.Key]).IsChecked == true,
                FieldKind.Combo => (string?)((System.Windows.Controls.ComboBox)_controls[f.Key]).SelectedItem ?? "",
                FieldKind.EditableCombo => ((System.Windows.Controls.ComboBox)_controls[f.Key]).Text.Trim(),
                FieldKind.AudioDevice => (int)((System.Windows.Controls.ComboBoxItem)((System.Windows.Controls.ComboBox)_controls[f.Key]).SelectedItem).Tag!,
                FieldKind.Float => ParseFloat(((Wpf.Ui.Controls.TextBox)_controls[f.Key]).Text),
                _ => ((Wpf.Ui.Controls.TextBox)_controls[f.Key]).Text,
            };
        }
        return vals;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _fields)
        {
            if (f.Kind == FieldKind.Int &&
                !int.TryParse(((Wpf.Ui.Controls.TextBox)_controls[f.Key]).Text, out _))
            {
                System.Windows.MessageBox.Show(this, $"'{StepTextsFa.Get(_stepType, f.Key, f.Label)}' باید عدد باشد.",
                    "مقدار نامعتبر", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (f.Kind == FieldKind.Float &&
                !TryParseFloat(((Wpf.Ui.Controls.TextBox)_controls[f.Key]).Text, out _))
            {
                System.Windows.MessageBox.Show(this, $"'{StepTextsFa.Get(_stepType, f.Key, f.Label)}' باید عدد (می‌تواند اعشاری باشد) باشد.",
                    "مقدار نامعتبر", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var vals = ReadValues();
        if (_stepType == "buzzer")
        {
            try { _ = StepDefinitions.BuildBuzzerCommands(vals); }
            catch (FormatException ex)
            {
                System.Windows.MessageBox.Show(this, ex.Message, "مقدار نامعتبر Buzzer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        Values = vals;
        DialogResult = true;
    }

    // v0.9.60 — fractional fields accept both "0.5" (invariant) and the local decimal
    // separator, regardless of the Windows display language.
    private static bool TryParseFloat(string s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
           || double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v);

    private static double ParseFloat(string s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
           : double.Parse(s, NumberStyles.Float, CultureInfo.CurrentCulture);

    private static string PropAsString(object? v) => v switch
    {
        null => "",
        string s => s,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je => je.GetString() ?? "",
        System.Text.Json.JsonElement je => je.ToString(),
        _ => v.ToString() ?? "",
    };

    private static bool PropAsBool(object? v) => v switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var b2) && b2,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True } => true,
        _ => false,
    };

    /// <summary>Opens a file dialog and writes the selected path into the field whose key is on Button.Tag.</summary>
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string key) return;
        if (!_controls.TryGetValue(key, out var ctrl) || ctrl is not Wpf.Ui.Controls.TextBox tb) return;

        var browseDef = _fields.FirstOrDefault(f => f.Key == key);
        var filter = browseDef?.BrowseFilter ?? "All files|*.*";

        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        if (dlg.ShowDialog(this) != true) return;
        tb.Text = dlg.FileName;
    }
}
