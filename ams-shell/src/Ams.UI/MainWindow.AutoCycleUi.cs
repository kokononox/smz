using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ams.UI.ViewModels;

namespace Ams.UI;

/// <summary>Read-only description of the route-driven After/Startup cycle.</summary>
public partial class MainWindow
{
    private static readonly DependencyProperty AutoCycleUiInstalledProperty =
        DependencyProperty.RegisterAttached(
            "AutoCycleUiInstalled", typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(InstallAutoCycleUi), true);
    }

    private static void InstallAutoCycleUi(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || window.GetValue(AutoCycleUiInstalledProperty) is true) return;
        if (window.FindName("PlayOptBody") is not StackPanel body) return;
        window.SetValue(AutoCycleUiInstalledProperty, true);

        var advanced = AutoCycleUiKit.EnsureAdvancedPanel(body);
        var section = new StackPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = AutoCycleUiKit.Title("چرخهٔ After و Startup");
        var status = AutoCycleUiKit.Badge("خودکار · بدون تایمر اضافه", AutoCycleUiKit.Success);
        Grid.SetColumn(title, 0);
        Grid.SetColumn(status, 1);
        head.Children.Add(title);
        head.Children.Add(status);
        section.Children.Add(head);

        section.Children.Add(StageCard(
            "۱ · After",
            "بلافاصله پس از پایان Game اجرا می‌شود. مدت کار داخل خود استپ‌های Game تعریف می‌شود؛ سپس یکی از روش‌های Restart در تب After اجرا خواهد شد."));
        section.Children.Add(StageCard(
            "۲ · Startup",
            "پس از Restart و بازگشت پایدار USB فقط یک‌بار اجرا می‌شود. سپس Desktop رد می‌شود و جریان از Login / DC ادامه پیدا می‌کند."));
        section.Children.Add(AutoCycleUiKit.Helper(
            "Resume Essentials و زمان‌بندی‌های Restart/USB/PostLaunch منسوخ شده‌اند و دیگر در خروجی جدید نوشته نمی‌شوند."));

        advanced.Children.Add(AutoCycleUiKit.Card(AutoCycleUiKit.ScheduleTag, section));
        AutoCycleUiKit.ReorderAdvanced(advanced);
        AutoCycleUiKit.Reorder(body);
    }

    private static Border StageCard(string title, string description)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = AutoCycleUiKit.Text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = AutoCycleUiKit.Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });
        return new Border
        {
            Background = AutoCycleUiKit.Raised,
            BorderBrush = AutoCycleUiKit.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 7),
            Child = content,
        };
    }
}
