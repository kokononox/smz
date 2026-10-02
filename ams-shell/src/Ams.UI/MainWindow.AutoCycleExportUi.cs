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

        var nativeButton = AutoCycleUiKit.Action("ساخت Native UF2 پروژهٔ فعلی", true);
        nativeButton.Tag = "AutoCycle.NativeUf2";
        nativeButton.ToolTip =
            "پروژهٔ باز را محلی به ABP تبدیل می‌کند و بدون SDK، GCC یا GitHub داخل UF2 پایهٔ انتخاب‌شده قرار می‌دهد.";
        nativeButton.SetBinding(Button.CommandProperty,
            new Binding(nameof(MainViewModel.ExportNativeUf2Command)));
        panel.Children.Add(nativeButton);
        if (panel.Parent is StackPanel cardBody)
        {
            if (!cardBody.Children.OfType<FrameworkElement>()
                    .Any(x => Equals(x.Tag, "AutoCycle.DisplayProfile")))
                cardBody.Children.Insert(Math.Max(2, cardBody.Children.Count - 1),
                    BuildDisplayProfile(vm));
            if (!cardBody.Children.OfType<FrameworkElement>()
                    .Any(x => Equals(x.Tag, "AutoCycle.HumanMouseProfile")))
                cardBody.Children.Insert(Math.Max(2, cardBody.Children.Count - 1),
                    BuildHumanMouseProfile(vm));
            if (!cardBody.Children.OfType<FrameworkElement>()
                    .Any(x => Equals(x.Tag, "AutoCycle.SoundProfiles")))
                cardBody.Children.Insert(Math.Max(2, cardBody.Children.Count - 1),
                    BuildSoundProfiles(vm));
        }
        AutoCycleUiKit.ReorderExportSteps(panel);
        AutoCycleUiKit.Reorder(body);
    }

    private static FrameworkElement BuildDisplayProfile(MainViewModel vm)
    {
        var panel = new StackPanel
        {
            Tag = "AutoCycle.DisplayProfile",
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Thickness(0, 8, 0, 8),
        };
        panel.Children.Add(AutoCycleUiKit.Title("نمایشگر مقصد و مرز نرم موس"));
        panel.Children.Add(AutoCycleUiKit.Helper(
            "رزولوشن داخل AMSJ و UF2 ذخیره می‌شود؛ برای سیستم مقصد نیازی به "
            + "نصب Classroom Studio یا Bridge نیست. اگر رزولوشن در فهرست نبود، Custom را انتخاب کنید."));

        var preset = new System.Windows.Controls.ComboBox
        {
            ItemsSource = MainViewModel.DisplayResolutionPresets,
            MinWidth = 190, Margin = new Thickness(0, 5, 0, 5),
            FlowDirection = FlowDirection.LeftToRight,
        };
        preset.SetBinding(System.Windows.Controls.ComboBox.SelectedItemProperty,
            new Binding(nameof(MainViewModel.DisplayResolutionPreset))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });
        panel.Children.Add(preset);

        var custom = new Grid { Margin = new Thickness(0, 3, 0, 7) };
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddNumber(custom, 0, "عرض Custom", nameof(MainViewModel.DisplayCustomWidth));
        AddNumber(custom, 1, "ارتفاع Custom", nameof(MainViewModel.DisplayCustomHeight));
        custom.SetBinding(UIElement.VisibilityProperty,
            new Binding(nameof(MainViewModel.IsCustomDisplayResolution))
            {
                Converter = new System.Windows.Controls.BooleanToVisibilityConverter(),
            });
        panel.Children.Add(custom);

        var enabled = new CheckBox
        {
            Content = "Soft Boundary Steering فعال باشد",
            Foreground = AutoCycleUiKit.Text,
            Margin = new Thickness(0, 4, 0, 4),
        };
        enabled.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(MainViewModel.SoftBoundaryEnabled)) { Mode = BindingMode.TwoWay });
        panel.Children.Add(enabled);

        var margin = new Grid { Margin = new Thickness(0, 3, 0, 5) };
        margin.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddNumber(margin, 0, "حاشیهٔ نرم از هر طرف (۱ تا ۲۰٪)", nameof(MainViewModel.SoftBoundaryMarginPercent));
        panel.Children.Add(margin);

        var summary = AutoCycleUiKit.Helper("");
        summary.Foreground = AutoCycleUiKit.Success;
        summary.SetBinding(TextBlock.TextProperty,
            new Binding(nameof(MainViewModel.DisplayProfileSummary)));
        panel.Children.Add(summary);
        return panel;
    }

    private static FrameworkElement BuildHumanMouseProfile(MainViewModel vm)
    {
        var panel = new StackPanel
        {
            Tag = "AutoCycle.HumanMouseProfile",
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Thickness(0, 8, 0, 8),
        };
        panel.Children.Add(AutoCycleUiKit.Title("پروفایل سراسری حرکت دست"));
        panel.Children.Add(AutoCycleUiKit.Helper(
            "یک بار ۳۰ ثانیه حرکت طبیعی ضبط می‌شود. Native Export سرعت، ریتم و "
            + "امضای همان دست را روی تمام حرکات موس اعمال می‌کند؛ اجرای برد به "
            + "Registry، Bridge یا برنامهٔ پس‌زمینه نیاز ندارد."));
        var button = AutoCycleUiKit.Action("ساخت دوباره پروفایل دست — ۳۰ ثانیه", true);
        button.SetBinding(Button.CommandProperty,
            new Binding(nameof(MainViewModel.CaptureHumanMouseProfileCommand)));
        panel.Children.Add(button);
        var summary = AutoCycleUiKit.Helper("");
        summary.Foreground = AutoCycleUiKit.Success;
        summary.SetBinding(TextBlock.TextProperty,
            new Binding(nameof(MainViewModel.HumanMouseProfileSummary)));
        panel.Children.Add(summary);
        return panel;
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
            "Whisper New با ID 1 و Whisper Repeat با ID 3 شنونده‌های سراسری Game هستند و بعد از واکنش همان Cursor را ادامه می‌دهند. " +
            "صدای Catch داخل استپ صریح Wait For Sound تنظیم می‌شود."));

        var profiles = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        profiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        profiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var whisper = ProfileCard("Whisper New — ID 1", nameof(MainViewModel.WhisperSoundEnabled),
            nameof(MainViewModel.WhisperPeakMin), nameof(MainViewModel.WhisperPeakMax),
            nameof(MainViewModel.WhisperPriority), nameof(MainViewModel.WhisperCooldownMs));
        var repeat = ProfileCard("Whisper Repeat — ID 3", nameof(MainViewModel.WhisperRepeatSoundEnabled),
            nameof(MainViewModel.WhisperRepeatPeakMin), nameof(MainViewModel.WhisperRepeatPeakMax),
            nameof(MainViewModel.WhisperRepeatPriority), nameof(MainViewModel.WhisperRepeatCooldownMs));
        Grid.SetColumn(whisper, 0);
        Grid.SetColumn(repeat, 1);
        profiles.Children.Add(whisper);
        profiles.Children.Add(repeat);
        panel.Children.Add(profiles);
        var summary = AutoCycleUiKit.Helper("");
        summary.Foreground = AutoCycleUiKit.Success;
        summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainViewModel.SoundProfileSummary)));
        panel.Children.Add(summary);
        return panel;
    }

    private static Border ProfileCard(string title, string enabled, string min, string max,
        string priority, string cooldown, string? timeoutMin = null, string? timeoutMax = null)
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
        if (timeoutMin is not null && timeoutMax is not null)
        {
            var timeoutFields = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            timeoutFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            timeoutFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddNumber(timeoutFields, 0, "Timeout حداقل هر پرتاب (ثانیه)", timeoutMin);
            AddNumber(timeoutFields, 1, "Timeout حداکثر هر پرتاب (ثانیه)", timeoutMax);
            body.Children.Add(timeoutFields);
        }
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
