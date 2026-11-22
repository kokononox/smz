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

/// <summary>Non-destructive profile calibration UI; proposals require explicit Apply and Save actions.</summary>
internal static class LightCalibrationUiBootstrap
{
    private static readonly DependencyProperty InstalledProperty = DependencyProperty.RegisterAttached(
        "LightCalibrationUiInstalled", typeof(bool), typeof(LightCalibrationUiBootstrap), new PropertyMetadata(false));

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
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static void Install(MainWindow window, MainViewModel vm)
    {
        var body = FindStatusBody(window);
        if (body is null) return;
        vm.SelectedLightCalibrationProfile ??= vm.LightStateProfiles.FirstOrDefault();

        var card = new Border
        {
            Background = Brush("#22262D"), BorderBrush = Brush("#343B46"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(4, 0, 4, 12),
        };
        var content = new StackPanel();
        content.Children.Add(Text("کالیبراسیون پیشنهادی", 15, "#F5F7FA", FontWeights.SemiBold));
        content.Children.Add(Text("کالیبره‌کردن با دو سنسورِ ثابت و مرتب‌شده انجام می‌شود: میانگین (L1+L2)/2 و اختلاف علامت‌دار L2−L1 هر دو در تلورانس نقش دارند.", 12, "#AAB3C2"));
        content.Children.Add(Text("محدودهٔ هر محیط باید در فضای دو‌بعدی با حاشیهٔ امن حداقل ۱ لوکس از محیط‌های فعال جدا باشد. جابه‌جایی هر سنسور یعنی کالیبره‌کردن دوبارهٔ همهٔ محیط‌ها؛ firmware تک‌سنسوره تا زمان پشتیبانی جداگانه، این پروفایل‌ها را نمی‌پذیرد.", 11, "#AAB3C2"));
        content.Children.Add(Text("نمونه‌برداری از دادهٔ زندهٔ تازهٔ هر دو سنسور انجام می‌شود؛ ابتدا «شروع پایش» را بزنید. اعمال پیشنهاد و ذخیره‌سازی همچنان دو اقدام جدا هستند.", 11, "#AAB3C2"));

        var controls = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
        controls.Children.Add(Text("پروفایل:", 12, "#D9DEE7"));
        var profile = new WpfComboBox { ItemsSource = vm.LightStateProfiles, DisplayMemberPath = "Name", Width = 210, MinHeight = 36, Margin = new Thickness(6, 0, 12, 6) };
        profile.SetBinding(WpfComboBox.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedLightCalibrationProfile)) { Mode = BindingMode.TwoWay });
        controls.Children.Add(profile);
        controls.Children.Add(Text("مدت:", 12, "#D9DEE7"));
        var duration = new WpfComboBox { ItemsSource = vm.LightCalibrationDurationsSeconds, Width = 80, MinHeight = 36, Margin = new Thickness(6, 0, 4, 6) };
        duration.SetBinding(WpfComboBox.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedLightCalibrationDurationSeconds)) { Mode = BindingMode.TwoWay });
        controls.Children.Add(duration);
        controls.Children.Add(Text("ثانیه", 12, "#AAB3C2"));

        var start = Button("شروع نمونه‌برداری", "#5E9FE8", "#10151C");
        start.Click += (_, _) => vm.StartLightProfileCalibration();
        controls.Children.Add(start);
        var cancel = Button("لغو", "#333740", "#F5F7FA");
        cancel.Click += (_, _) => vm.CancelLightProfileCalibration();
        controls.Children.Add(cancel);
        var apply = Button("اعمال پیشنهاد روی فرم", "#3D6B55", "#F5F7FA");
        apply.Click += (_, _) =>
        {
            if (!vm.ApplyPendingLightCalibrationSuggestion()) return;
            RefreshProfileEditor(window);
        };
        controls.Children.Add(apply);
        content.Children.Add(controls);

        var status = Bound(nameof(MainViewModel.LightCalibrationStatus), 12, "#D9DEE7");
        status.Margin = new Thickness(3, 2, 3, 4);
        content.Children.Add(status);
        var suggestion = Bound(nameof(MainViewModel.LightCalibrationSuggestionDisplay), 13, "#72BC8F", FontWeights.SemiBold);
        suggestion.Margin = new Thickness(3, 2, 3, 0);
        content.Children.Add(suggestion);
        card.Child = content;

        body.Children.Insert(Math.Max(0, body.Children.Count - 1), card);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.LightChartVersion)
                or nameof(MainViewModel.LightFreshnessText))
                window.Dispatcher.BeginInvoke(new Action(vm.CaptureLightCalibrationSample));
        };
    }

    private static Border SensorTile(string title, params UIElement[] details)
    {
        var body = new StackPanel();
        body.Children.Add(Text(title, 12, "#AAB3C2", FontWeights.SemiBold));
        foreach (var detail in details) body.Children.Add(detail);
        return new Border
        {
            Child = body,
            Background = Brush("#181B20"),
            BorderBrush = Brush("#343B46"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10),
            Margin = new Thickness(3),
        };
    }

    private static void RefreshProfileEditor(DependencyObject root)
    {
        if (root is TextBox box
            && box.GetBindingExpression(TextBox.TextProperty) is BindingExpression expression
            && expression.ResolvedSource is LightStateProfile)
            expression.UpdateTarget();

        if (root is Grid grid)
        {
            var center = grid.Children.OfType<TextBox>()
                .FirstOrDefault(x => Grid.GetColumn(x) == 2)?
                .GetBindingExpression(TextBox.TextProperty)?.ResolvedSource as LightStateProfile;
            var range = grid.Children.OfType<TextBlock>().FirstOrDefault(x => Grid.GetColumn(x) == 4);
            if (center is not null && range is not null)
                range.Text = center.HasDualSensorCalibration
                    ? $"دوحسگر · M {center.MeanLuxCenter:0.#} ±{center.MeanLuxTolerance:0.#}\nΔ {center.DifferenceLuxCenter:0.#} ±{center.DifferenceLuxTolerance:0.#}"
                    : $"L1 · {center.LuxMin:0.#} تا {center.LuxMax:0.#}";
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            RefreshProfileEditor(VisualTreeHelper.GetChild(root, i));
    }

    private static Button Button(string label, string background, string foreground) => new()
    {
        Content = label, Padding = new Thickness(12, 7, 12, 7), MinHeight = 36, Margin = new Thickness(6, 0, 0, 6),
        Background = Brush(background), Foreground = Brush(foreground), FontWeight = FontWeights.SemiBold,
    };

    private static TextBlock Bound(string path, double size, string color, FontWeight? weight = null)
    {
        var text = Text("", size, color, weight);
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        return text;
    }

    private static TextBlock Text(string value, double size, string color, FontWeight? weight = null) => new()
    {
        Text = value, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Foreground = Brush(color),
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3),
    };

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
