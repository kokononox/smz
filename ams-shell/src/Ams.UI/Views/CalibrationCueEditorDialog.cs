using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Ams.UI.Models;
using Ams.UI.ViewModels;

namespace Ams.UI.Views;

/// <summary>Professional preset/custom editor for one light-environment cue.</summary>
public sealed class CalibrationCueEditorDialog : Window
{
    private sealed record Choice(int Id, string DisplayName);

    private readonly MainViewModel _vm;
    private readonly LightStateProfile _source;
    private readonly ComboBox _preset = new() { MinWidth = 430, IsTextSearchEnabled = true };
    private readonly TextBox _search = new() { MinWidth = 250 };
    private readonly TextBox _pattern = new() { AcceptsReturn = true, MinHeight = 76, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _volume = new() { Width = 80 };
    private readonly TextBox _tempo = new() { Width = 80 };
    private readonly ComboBox _envelope = new() { Width = 140, ItemsSource = new[] { "sharp", "smooth", "fade-in", "fade-out" } };
    private readonly TextBlock _status = new() { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap };
    private List<Choice> _choices = [];

    public CalibrationCueEditorDialog(Window owner, MainViewModel vm, LightStateProfile source)
    {
        Owner = owner;
        _vm = vm;
        _source = source;
        Title = "ویرایش حرفه‌ای صدای بازر";
        Width = 700;
        Height = 590;
        MinWidth = 620;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(30, 34, 40));
        Foreground = Brushes.WhiteSmoke;

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var form = new StackPanel();
        form.Children.Add(Heading($"صدای محیط «{source.Name}»"));
        form.Children.Add(Note("۱۰۰ الگوی آماده شامل ۸ صدای کلاسیک قبلی در دسترس است. برای طراحی آزاد، «سفارشی» را انتخاب کن."));
        form.Children.Add(Label("جست‌وجوی الگو"));
        _search.Margin = new Thickness(0, 4, 0, 8);
        _search.TextChanged += (_, _) => RefreshChoices();
        form.Children.Add(_search);
        form.Children.Add(Label("الگوی آماده"));
        _preset.Margin = new Thickness(0, 4, 0, 8);
        _preset.DisplayMemberPath = nameof(Choice.DisplayName);
        _preset.SelectedValuePath = nameof(Choice.Id);
        _preset.SelectionChanged += (_, _) => ApplyPreset();
        form.Children.Add(_preset);
        form.Children.Add(Label("توالی نوت‌ها — فرکانس:مدت,مکث؛ …"));
        _pattern.Margin = new Thickness(0, 4, 0, 8);
        form.Children.Add(_pattern);

        var styleRow = new WrapPanel();
        styleRow.Children.Add(Label("حجم ۱–۱۰۰٪"));
        _volume.Margin = new Thickness(6, 0, 18, 0);
        styleRow.Children.Add(_volume);
        styleRow.Children.Add(Label("لبه صدا"));
        _envelope.Margin = new Thickness(6, 0, 18, 0);
        styleRow.Children.Add(_envelope);
        styleRow.Children.Add(Label("سرعت ۲۵–۴۰۰٪"));
        _tempo.Margin = new Thickness(6, 0, 0, 0);
        styleRow.Children.Add(_tempo);
        form.Children.Add(styleRow);
        form.Children.Add(Note("بازه مجاز هر نوت: ۳۰ تا ۲۰٬۰۰۰ هرتز. مکث اختیاری است؛ حداکثر ۸ نوت برای ذخیره داخل UF2."));

        var preview = new Button { Content = "🔊 پیش‌شنیدن روی Pico", Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 8) };
        preview.Click += async (_, _) => await Preview(preview);
        form.Children.Add(preview);
        form.Children.Add(_status);
        Grid.SetRow(form, 0);
        root.Children.Add(form);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "لغو", MinWidth = 90, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(6) };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = "اعمال", MinWidth = 100, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(6) };
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        RefreshChoices();
        _preset.SelectedValue = source.CalibrationCue;
        if (_preset.SelectedIndex < 0) _preset.SelectedValue = 0;
        _pattern.Text = source.CalibrationCue == 0
            ? source.CalibrationCuePattern
            : CalibrationCueCatalog.Get(source.CalibrationCue).Pattern;
        _volume.Text = source.CalibrationCueVolume.ToString();
        _tempo.Text = source.CalibrationCueTempo.ToString();
        _envelope.SelectedItem = source.CalibrationCueEnvelope;
        UpdateCustomState();
    }

    private void RefreshChoices()
    {
        var q = _search.Text.Trim();
        var selected = _preset.SelectedValue is int id ? id : _source.CalibrationCue;
        _choices =
        [
            new Choice(0, "000 · سفارشی · طراحی آزاد نوت‌ها"),
            .. CalibrationCueCatalog.All
                .Where(x => string.IsNullOrEmpty(q)
                    || x.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .Select(x => new Choice(x.Id, x.DisplayName))
        ];
        _preset.ItemsSource = _choices;
        _preset.SelectedValue = _choices.Any(x => x.Id == selected) ? selected : 0;
    }

    private void ApplyPreset()
    {
        if (_preset.SelectedValue is not int id) return;
        if (id > 0)
        {
            var cue = CalibrationCueCatalog.Get(id);
            _pattern.Text = cue.Pattern;
            _volume.Text = cue.Volume.ToString();
            _tempo.Text = cue.Tempo.ToString();
            _envelope.SelectedItem = cue.Envelope;
        }
        UpdateCustomState();
    }

    private void UpdateCustomState()
    {
        var custom = _preset.SelectedValue is 0;
        _pattern.IsReadOnly = !custom;
        _volume.IsReadOnly = !custom;
        _tempo.IsReadOnly = !custom;
        _envelope.IsEnabled = custom;
    }

    private LightStateProfile ReadCandidate()
    {
        if (_preset.SelectedValue is not int id) throw new FormatException("یک الگو انتخاب کنید.");
        if (!int.TryParse(_volume.Text, out var volume) || volume is < 1 or > 100)
            throw new FormatException("حجم صدا باید بین ۱ تا ۱۰۰ باشد.");
        if (!int.TryParse(_tempo.Text, out var tempo) || tempo is < 25 or > 400)
            throw new FormatException("سرعت نوت باید بین ۲۵ تا ۴۰۰ باشد.");
        var candidate = new LightStateProfile
        {
            Id = _source.Id, Name = _source.Name, Enabled = _source.Enabled,
            LuxCenter = _source.LuxCenter, LuxTolerance = _source.LuxTolerance,
            StableDurationMs = _source.StableDurationMs, HysteresisLux = _source.HysteresisLux,
            LightCooldownMs = _source.LightCooldownMs, CalibrationCue = id,
            CalibrationCuePattern = _pattern.Text.Trim(), CalibrationCueVolume = volume,
            CalibrationCueTempo = tempo, CalibrationCueEnvelope = _envelope.SelectedItem?.ToString() ?? "sharp",
        };
        // Reuse the production parser/range validator.
        _ = StepDefinitions.BuildBuzzerCommands(new Dictionary<string, object?>
        {
            ["preset"] = "custom", ["pattern"] = candidate.CalibrationCuePattern,
            ["volume"] = candidate.CalibrationCueVolume, ["tempo"] = candidate.CalibrationCueTempo,
            ["envelope"] = candidate.CalibrationCueEnvelope,
        });
        var noteCount = candidate.CalibrationCuePattern.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
        if (candidate.CalibrationCue == 0 && noteCount > 8)
            throw new FormatException("برای ذخیره روی Pico حداکثر ۸ نوت مجاز است.");
        return candidate;
    }

    private async Task Preview(Button button)
    {
        try
        {
            button.IsEnabled = false;
            _status.Text = "در حال پخش…";
            _status.Text = await _vm.PreviewLightCalibrationCueAsync(ReadCandidate());
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { button.IsEnabled = true; }
    }

    private void Save()
    {
        try
        {
            var value = ReadCandidate();
            _source.CalibrationCue = value.CalibrationCue;
            _source.CalibrationCuePattern = value.CalibrationCuePattern;
            _source.CalibrationCueVolume = value.CalibrationCueVolume;
            _source.CalibrationCueTempo = value.CalibrationCueTempo;
            _source.CalibrationCueEnvelope = value.CalibrationCueEnvelope;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "صدای نامعتبر", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gainsboro };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(170, 179, 194)), Margin = new Thickness(0, 4, 0, 8) };
}