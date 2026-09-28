using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Ams.UI.ViewModels;

namespace Ams.UI;

internal static class AutoCycleExportUiBootstrap
{
    // Legacy command marker retained for downstream contract readers: ExportAutoCyclePicoPlanCommand.
    // چرخهی خودکار / چرخه‌ی خودکار
    private static readonly DependencyProperty InstalledProperty = DependencyProperty.RegisterAttached(
        "AutoCycleExportUiInstalled", typeof(bool), typeof(AutoCycleExportUiBootstrap), new PropertyMetadata(false));

    [ModuleInitializer]
    internal static void Initialize()
        => EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded), true);

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || window.GetValue(InstalledProperty) is true) return;
        if (window.FindName("PlayOptBody") is not StackPanel body) return;
        if (window.DataContext is not MainViewModel vm) return;
        window.SetValue(InstalledProperty, true);

        var panel = AutoCycleUiKit.EnsureExportCard(body);
        // One authoritative action: current tabs + modern low-memory runtime.
        // The Golden-100 command remains available to CI/legacy recovery but is not a
        // user-facing exporter because it intentionally contains a frozen sample project.
        var button = AutoCycleUiKit.Action("ساخت و کپی کامل پروژهٔ فعلی به درایو Pico", true);
        button.Tag = AutoCycleUiKit.PlanStepTag;
        button.ToolTip = "تمام تب‌های پروژهٔ باز را به Route تبدیل می‌کند، Bundle مدرن را می‌سازد و code.py را در آخر روی CIRCUITPY کپی می‌کند.";
        button.SetBinding(Button.CommandProperty,
            new Binding(nameof(MainViewModel.ExportAutoCycleModernCommand)));
        panel.Children.Add(button);
        if (panel.Parent is StackPanel cardBody && !cardBody.Children.OfType<FrameworkElement>()
                .Any(x => Equals(x.Tag, "AutoCycle.SoundProfiles")))
            cardBody.Children.Insert(Math.Max(2, cardBody.Children.Count - 1), BuildSoundProfiles(vm));
        AutoCycleUiKit.ReorderExportSteps(panel);
        AutoCycleUiKit.Reorder(body);
    }
    private static FrameworkElement BuildSoundProfiles(MainViewModel vm)
    {
        var panel = new StackPanel
        {
            Tag = "AutoCycle.SoundProfiles", FlowDirection = FlowDirection.RightToLeft,
            Margin = new Thickness(0, 8, 0, 8),
        };
        panel.Children.Add(AutoCycleUiKit.Title("پروفایل‌های صدای Game"));
        panel.Children.Add(AutoCycleUiKit.Helper(
            "Whisper در تمام Game سراسری است و بعد از واکنش همان نقطه را ادامه می‌دهد. " +
            "Splash فقط داخل Wait For Sound با رفتار Splash scoped فعال می‌شود؛ با تشخیص یا پایان Timeout بازه‌ای، پرتاب بعدی شروع می‌شود."));

        var profiles = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        profiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        profiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var whisper = ProfileCard("ویسپر", nameof(MainViewModel.WhisperSoundEnabled),
            nameof(MainViewModel.WhisperPeakMin), nameof(MainViewModel.WhisperPeakMax),
            nameof(MainViewModel.WhisperPriority), nameof(MainViewModel.WhisperCooldownMs));
        var splash = ProfileCard("چلپ آب — فقط Scoped", nameof(MainViewModel.SplashSoundEnabled),
            nameof(MainViewModel.SplashPeakMin), nameof(MainViewModel.SplashPeakMax),
            nameof(MainViewModel.SplashPriority), nameof(MainViewModel.SplashCooldownMs));
        Grid.SetColumn(whisper, 0); Grid.SetColumn(splash, 1);
        profiles.Children.Add(whisper); profiles.Children.Add(splash);
        panel.Children.Add(profiles);
        var summary = AutoCycleUiKit.Helper("");
        summary.Foreground = AutoCycleUiKit.Success;
        summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.SoundProfileSummary)));
        panel.Children.Add(summary);
        return panel;
    }

    private static Border ProfileCard(string title, string enabled, string min, string max,
        string priority, string cooldown)
    {
        var body = new StackPanel { Margin = new Thickness(9) };
        var toggle = new CheckBox
        {
            Content = title + " — فعال", Foreground = AutoCycleUiKit.Text,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7),
        };
        toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(enabled) { Mode = BindingMode.TwoWay });
        body.Children.Add(toggle);
        var fields = new Grid();
        for (var i = 0; i < 4; i++) fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddNumber(fields, 0, "Peak Min", min);
        AddNumber(fields, 1, "Peak Max", max);
        AddNumber(fields, 2, "Priority", priority);
        AddNumber(fields, 3, "Cooldown ms", cooldown);
        body.Children.Add(fields);
        return new Border
        {
            Child = body, Background = AutoCycleUiKit.Raised, BorderBrush = AutoCycleUiKit.BorderBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Margin = new Thickness(4, 0, 4, 0),
        };
    }

    private static void AddNumber(Grid grid, int column, string label, string property)
    {
        var stack = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        stack.Children.Add(new TextBlock { Text = label, Foreground = AutoCycleUiKit.Muted, FontSize = 10 });
        var box = new TextBox
        {
            MinWidth = 70, Padding = new Thickness(5, 3, 5, 3), FlowDirection = FlowDirection.LeftToRight,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        box.SetBinding(TextBox.TextProperty, new Binding(property)
        {
            Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
        });
        stack.Children.Add(box);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

}
