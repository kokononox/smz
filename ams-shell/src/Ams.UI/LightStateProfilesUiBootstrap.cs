using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Ams.UI.Models;
using Ams.UI.ViewModels;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace Ams.UI;

/// <summary>Adds phase-four state/profile diagnostics to the existing read-only Status surface.</summary>
internal static class LightStateProfilesUiBootstrap
{
    private static readonly DependencyProperty InstalledProperty = DependencyProperty.RegisterAttached(
        "LightStateProfilesUiInstalled", typeof(bool), typeof(LightStateProfilesUiBootstrap), new PropertyMetadata(false));

    [ModuleInitializer]
    internal static void Initialize()
        => EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded), true);

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || window.GetValue(InstalledProperty) is true
            || window.DataContext is not MainViewModel vm) return;
        window.SetValue(InstalledProperty, true);
        window.Dispatcher.BeginInvoke(new Action(() => Install(window, vm)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void Install(MainWindow window, MainViewModel vm)
    {
        var body = FindStatusBody(window);
        if (body is null) return;
        vm.RefreshLightProfileWarnings();

        var section = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        section.Children.Add(new TextBlock
        {
            Text = "تشخیص وضعیت نور",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F5F7FA"),
            Margin = new Thickness(0, 0, 0, 8),
        });

        var stateGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        for (var i = 0; i < 3; i++) stateGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var state = Card("وضعیت فعلی", Bound(nameof(MainViewModel.LightStateDisplay), 19, FontWeights.SemiBold));
        var confidence = Card("اطمینان نمایشی", Bound(nameof(MainViewModel.LightStateConfidenceDisplay), 19, FontWeights.SemiBold));
        var readiness = Card("آمادگی Launch / Auto Resume", Text("فقط تشخیصی · گیت اجرایی خاموش است", 13, "#DE9255"));
        Grid.SetColumn(state, 0); Grid.SetColumn(confidence, 1); Grid.SetColumn(readiness, 2);
        stateGrid.Children.Add(state); stateGrid.Children.Add(confidence); stateGrid.Children.Add(readiness);
        section.Children.Add(stateGrid);

        var warning = new Border
        {
            Background = Brush("#3A2B20"), BorderBrush = Brush("#8B5A32"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(4, 0, 4, 10),
            Child = Bound(nameof(MainViewModel.LightStateWarning), 13),
        };
        section.Children.Add(warning);

        var editor = new Border
        {
            Background = Brush("#22262D"), BorderBrush = Brush("#343B46"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(4, 0, 4, 12),
        };
        var editorBody = new StackPanel();
        editorBody.Children.Add(Text("پروفایل‌های قابل تنظیم", 15, "#F5F7FA", FontWeights.SemiBold));
        editorBody.Children.Add(Text("مرکز و تلورانس فعلی فرضی‌اند. ذخیره‌سازی هیچ مقداری را روی کالیبراسیون یا ماکرو overwrite نمی‌کند.", 12, "#AAB3C2"));
        var rows = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
        editorBody.Children.Add(rows);

        void RebuildRows()
        {
            rows.Children.Clear();
            rows.Children.Add(ProfileHeader());
            foreach (var profile in vm.LightStateProfiles) rows.Children.Add(ProfileRow(profile));
        }
        RebuildRows();

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = ActionButton("ذخیره پروفایل‌ها", "#5E9FE8", "#10151C");
        save.Click += (_, _) => { if (vm.SaveLightStateProfiles()) RebuildRows(); };
        var reset = ActionButton("بازگردانی مقادیر اولیه", "#333740", "#F5F7FA");
        reset.Margin = new Thickness(8, 0, 0, 0);
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show(window, "پروفایل‌های نور به مقادیر فرضی اولیه برگردند؟", "بازگردانی پروفایل‌ها",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            vm.ResetLightStateProfiles();
            RebuildRows();
        };
        actions.Children.Add(save); actions.Children.Add(reset);
        editorBody.Children.Add(actions);
        var saveStatus = Bound(nameof(MainViewModel.LightProfileSaveStatus), 12);
        saveStatus.Margin = new Thickness(0, 8, 0, 0);
        editorBody.Children.Add(saveStatus);
        editor.Child = editorBody;
        section.Children.Add(editor);
        section.Children.Add(CalibrationCueMenu(window, vm));

        var insertAt = Math.Max(0, body.Children.Count - 1);
        body.Children.Insert(insertAt, section);

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.LightChartVersion))
                window.Dispatcher.BeginInvoke(new Action(vm.ObserveCurrentLightState));
            else if (args.PropertyName == nameof(MainViewModel.LightFreshnessText)
                     && vm.LightFreshnessText.StartsWith("داده قدیمی", StringComparison.Ordinal))
                vm.MarkCurrentLightStateStale();
        };
    }

    private static Grid ProfileHeader()
    {
        var row = ProfileGrid();
        Add(row, Text("فعال", 11, "#AAB3C2"), 0);
        Add(row, Text("نام وضعیت", 11, "#AAB3C2"), 1);
        Add(row, Text("مرکز", 11, "#AAB3C2"), 2);
        Add(row, Text("تلورانس ±", 11, "#AAB3C2"), 3);
        Add(row, Text("بازه مؤثر", 11, "#AAB3C2"), 4);
        Add(row, Text("پایداری ms", 11, "#AAB3C2"), 5);
        Add(row, Text("Hysteresis", 11, "#AAB3C2"), 6);
        Add(row, Text("Cooldown نوری ms", 11, "#AAB3C2"), 7);
        return row;
    }

    private static Grid ProfileRow(LightStateProfile profile)
    {
        var row = ProfileGrid();
        row.Margin = new Thickness(0, 3, 0, 3);
        var enabled = new CheckBox { IsChecked = profile.Enabled, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        enabled.Checked += (_, _) => profile.Enabled = true;
        enabled.Unchecked += (_, _) => profile.Enabled = false;
        Add(row, enabled, 0);
        Add(row, Editor(profile, nameof(profile.Name)), 1);
        Add(row, Editor(profile, nameof(profile.LuxCenter)), 2);
        Add(row, Editor(profile, nameof(profile.LuxTolerance)), 3);
        Add(row, Text($"{profile.LuxMin:0.#} تا {profile.LuxMax:0.#}", 12, "#D9DEE7"), 4);
        Add(row, Editor(profile, nameof(profile.StableDurationMs)), 5);
        Add(row, Editor(profile, nameof(profile.HysteresisLux)), 6);
        Add(row, profile.Id is "whisper" or "whisper-repeat"
            ? Editor(profile, nameof(profile.LightCooldownMs))
            : Text("—", 12, "#777F8C"), 7);
        return row;
    }

    private static Grid ProfileGrid()
    {
        var grid = new Grid { MinWidth = 900 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
        for (var i = 0; i < 6; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private sealed record CueOption(int Id, string Name);
    private static readonly CueOption[] CueOptions =
    [
        new(1, "۱ · سه‌نت صعودی آرام"),
        new(2, "۲ · سه‌نت نزولی"),
        new(3, "۳ · چهارنُت روشن صعودی"),
        new(4, "۴ · سه‌نت مکث‌دار"),
        new(5, "۵ · چهارنُت ورود به بازی"),
        new(6, "۶ · هشدار چهارنُت متناوب"),
        new(7, "۷ · ویسپر جدید — درخشان"),
        new(8, "۸ · ویسپر تکراری — پاسخ‌دهنده"),
    ];

    private static Border CalibrationCueMenu(MainWindow window, MainViewModel vm)
    {
        var body = new StackPanel();
        body.Children.Add(Text("تخصیص صدای راهنمای کالیبراسیون", 15, "#F5F7FA", FontWeights.SemiBold));
        body.Children.Add(Text(
            "برای هر محیط یک ملودی ۳ تا ۵ نتی انتخاب کن. این ملودی روی خود Pico ذخیره و هنگام انتخاب همان بخش در کالیبراسیون پخش می‌شود.",
            12, "#AAB3C2"));
        foreach (var profile in vm.LightStateProfiles)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Add(row, Text(profile.Name, 12, "#D9DEE7"), 0);
            var selector = new WpfComboBox
            {
                ItemsSource = CueOptions, DisplayMemberPath = nameof(CueOption.Name),
                SelectedValuePath = nameof(CueOption.Id), MinWidth = 250, Margin = new Thickness(4),
            };
            selector.SetBinding(WpfComboBox.SelectedValueProperty, new Binding(nameof(profile.CalibrationCue))
            {
                Source = profile, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
            Add(row, selector, 1);
            var preview = ActionButton("پیش‌شنیدن", "#333740", "#F5F7FA");
            preview.Margin = new Thickness(6, 3, 0, 3);
            preview.Click += async (_, _) =>
            {
                try
                {
                    preview.IsEnabled = false;
                    preview.Content = await vm.PreviewLightCalibrationCueAsync(profile.CalibrationCue);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(window, ex.Message, "پیش‌شنیدن صدای کالیبراسیون",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    preview.IsEnabled = true;
                    preview.Content = "پیش‌شنیدن";
                }
            };
            Add(row, preview, 2);
            body.Children.Add(row);
        }
        var save = ActionButton("ذخیره تخصیص صداها", "#5E9FE8", "#10151C");
        save.HorizontalAlignment = HorizontalAlignment.Right;
        save.Margin = new Thickness(0, 8, 0, 0);
        save.Click += (_, _) => vm.SaveLightStateProfiles();
        body.Children.Add(save);
        return new Border
        {
            Child = body, Background = Brush("#22262D"), BorderBrush = Brush("#343B46"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14), Margin = new Thickness(4, 0, 4, 12),
        };
    }

    private static TextBox Editor(LightStateProfile profile, string path)
    {
        var box = new TextBox
        {
            Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(3), MinWidth = 70,
            Background = Brush("#181B20"), Foreground = Brush("#F5F7FA"), BorderBrush = Brush("#444A55"),
        };
        box.SetBinding(TextBox.TextProperty, new Binding(path)
        {
            Source = profile, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
            ValidatesOnExceptions = true,
        });
        return box;
    }

    private static Button ActionButton(string title, string background, string foreground) => new()
    {
        Content = title, Padding = new Thickness(14, 7, 14, 7), MinHeight = 36,
        Background = Brush(background), Foreground = Brush(foreground), FontWeight = FontWeights.SemiBold,
    };

    private static Border Card(string title, params UIElement[] content)
    {
        var body = new StackPanel();
        body.Children.Add(Text(title, 12, "#AAB3C2"));
        foreach (var item in content) body.Children.Add(item);
        return new Border
        {
            Child = body, Background = Brush("#22262D"), BorderBrush = Brush("#343B46"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(4), MinHeight = 84,
        };
    }

    private static TextBlock Bound(string path, double size, FontWeight? weight = null)
    {
        var text = Text("", size, "#F5F7FA", weight);
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        return text;
    }

    private static TextBlock Text(string value, double size, string color, FontWeight? weight = null) => new()
    {
        Text = value, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Foreground = Brush(color),
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3),
    };

    private static void Add(Grid grid, UIElement child, int column) { Grid.SetColumn(child, column); grid.Children.Add(child); }

    private static StackPanel? FindStatusBody(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Text: "وضعیت زنده‌ی سنسور نور" } title
                && VisualTreeHelper.GetParent(title) is StackPanel body) return body;
            var found = FindStatusBody(child);
            if (found is not null) return found;
        }
        return null;
    }

    private static SolidColorBrush Brush(string color) => new((WpfColor)WpfColorConverter.ConvertFromString(color));
}
