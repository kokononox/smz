using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
        var status = AutoCycleUiKit.Badge("بازهٔ فعال", AutoCycleUiKit.Success);
        Grid.SetColumn(title, 0);
        Grid.SetColumn(status, 1);
        head.Children.Add(title);
        head.Children.Add(status);
        section.Children.Add(head);

        section.Children.Add(StageCard(
            "۱ · After",
            "با پایان Game یا رسیدن Deadline چرخه اجرا می‌شود؛ سپس یکی از روش‌های Restart در تب After اجرا خواهد شد."));
        section.Children.Add(CycleRangeRow());
        section.Children.Add(StageCard(
            "۲ · Startup",
            "پس از Restart و بازگشت پایدار USB فقط یک‌بار اجرا می‌شود. سپس Desktop رد می‌شود و جریان از Login / DC ادامه پیدا می‌کند."));
        section.Children.Add(AutoCycleUiKit.Helper(
            "بازه از لحظهٔ Start انتخاب می‌شود و پیش‌فرض آن ۱۱۰ تا ۱۳۰ دقیقه است. Resume Essentials و زمان‌بندی‌های USB/PostLaunch منسوخ شده‌اند."));

        advanced.Children.Add(AutoCycleUiKit.Card(AutoCycleUiKit.ScheduleTag, section));
        AutoCycleUiKit.ReorderAdvanced(advanced);
        AutoCycleUiKit.Reorder(body);
    }

    private static Grid CycleRangeRow()
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        AddLabel("بازهٔ چرخه:", 0);
        AddBox(nameof(MainViewModel.RestartMinMinutes), 1);
        AddLabel("تا", 2);
        AddBox(nameof(MainViewModel.RestartMaxMinutes), 3);
        AddLabel("دقیقه", 4);
        return row;

        void AddLabel(string text, int column)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = AutoCycleUiKit.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
            };
            Grid.SetColumn(label, column);
            row.Children.Add(label);
        }

        void AddBox(string property, int column)
        {
            var box = new TextBox
            {
                MinHeight = 30,
                Margin = new Thickness(4, 0, 4, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            box.SetBinding(TextBox.TextProperty, new Binding(property)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
            });
            Grid.SetColumn(box, column);
            row.Children.Add(box);
        }
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
