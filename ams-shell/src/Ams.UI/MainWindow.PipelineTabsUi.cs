using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Ams.UI.Models;
using Ams.UI.ViewModels;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfCursors = System.Windows.Input.Cursors;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfPanel = System.Windows.Controls.Panel;
using WpfPoint = System.Windows.Point;

namespace Ams.UI;

/// <summary>Fixed, non-closable browser-style pipeline tabs plus the read-only Status surface.</summary>
internal static class PipelineTabsUiBootstrap
{
    private static readonly DependencyProperty InstalledProperty = DependencyProperty.RegisterAttached(
        "PipelineTabsUiInstalled", typeof(bool), typeof(PipelineTabsUiBootstrap), new PropertyMetadata(false));

    [ModuleInitializer]
    internal static void Initialize()
        => EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded), true);

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MainWindow window || window.GetValue(InstalledProperty) is true) return;
        if (window.FindName("StepsList") is not WpfListBox steps || steps.Parent is not Grid host) return;
        if (window.DataContext is not MainViewModel vm) return;
        window.SetValue(InstalledProperty, true);
        vm.InitializePipelineTabs();

        var existing = host.Children.Cast<UIElement>().ToList();
        host.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
        foreach (var child in existing) Grid.SetRow(child, Grid.GetRow(child) + 1);

        var strip = new DockPanel
        {
            LastChildFill = true,
            Background = new SolidColorBrush(WpfColor.FromRgb(31, 35, 40)),
            Margin = new Thickness(0, 0, 0, 4),
        };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        tools.Children.Add(ToolButton("جدید", vm.NewPipelineWorkspaceCommand));
        tools.Children.Add(ToolButton("بازکردن", vm.OpenPipelineWorkspaceCommand));
        tools.Children.Add(ToolButton("ذخیره", vm.SavePipelineWorkspaceCommand));
        DockPanel.SetDock(tools, Dock.Right);
        strip.Children.Add(tools);

        var statusPanel = BuildStatusPanel(vm);
        statusPanel.Visibility = Visibility.Collapsed;
        Grid.SetRow(statusPanel, 1);
        WpfPanel.SetZIndex(statusPanel, 50);
        host.Children.Add(statusPanel);

        var tabs = new WrapPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight };
        var buttons = new List<Button>();
        Button? statusButton = null;
        foreach (var tab in vm.PipelineTabs)
        {
            var button = new Button
            {
                Content = tab.Title,
                Tag = tab.Kind,
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 2, 3, 0),
                BorderThickness = new Thickness(1),
                Cursor = WpfCursors.Hand,
                ToolTip = tab.FileName,
            };
            button.Click += (_, _) =>
            {
                statusPanel.Visibility = Visibility.Collapsed;
                if (button.Tag is PipelineKind kind)
                {
                    var current = vm.PipelineTabs.Single(x => x.Kind == kind);
                    if (vm.SwitchPipelineCommand.CanExecute(current)) vm.SwitchPipelineCommand.Execute(current);
                }
                Paint(buttons, vm.ActivePipelineTab);
                PaintStatus(statusButton!, false);
            };
            buttons.Add(button);
            tabs.Children.Add(button);
        }

        statusButton = new Button
        {
            Content = "وضعیت",
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(6, 2, 3, 0),
            BorderThickness = new Thickness(1),
            Cursor = WpfCursors.Hand,
            ToolTip = "Live read-only BH1750 telemetry",
        };
        statusButton.Click += (_, _) =>
        {
            statusPanel.Visibility = Visibility.Visible;
            Paint(buttons, null);
            PaintStatus(statusButton, true);
        };
        tabs.Children.Add(statusButton);
        strip.Children.Add(tabs);
        Grid.SetRow(strip, 0);
        host.Children.Add(strip);

        void RefreshTabUi()
        {
            statusPanel.Visibility = Visibility.Collapsed;
            UpdateTabKeyBindings(window, vm);
            Paint(buttons, vm.ActivePipelineTab);
            PaintStatus(statusButton, false);
        }

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.ActivePipelineTab)
                or nameof(MainViewModel.PipelineTabs)) RefreshTabUi();
        };
        RefreshTabUi();
    }

    private static FrameworkElement BuildStatusPanel(MainViewModel vm)
    {
        var root = new Grid
        {
            Background = Brush("#171A1F"),
            FlowDirection = FlowDirection.RightToLeft,
        };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = new StackPanel { Margin = new Thickness(20), MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
        scroll.Content = body;
        root.Children.Add(scroll);

        var identity = new StackPanel { Margin=new Thickness(0,0,0,18) };
        identity.Children.Add(new TextBlock { Text="تنظیمات کلی — کاربران ویندوز شیفت‌ها", FontSize=20, Foreground=Brush("#F5F7FA") });
        identity.Children.Add(new TextBlock { Text="نام Username ویندوز را وارد کنید، نه Display name. تغییر نام‌ها با Export Native UF2 روی برد اعمال می‌شود. ClassroomStudio هنگام اجرای بریج موقت باید Disconnect باشد.", TextWrapping=TextWrapping.Wrap, Foreground=Brush("#AAB3C2"), Margin=new Thickness(0,6,0,8) });
        foreach(var entry in new[] { ("کاربر روز",nameof(MainViewModel.DayWindowsUser)),("کاربر شب",nameof(MainViewModel.NightWindowsUser)) }) {
            var row=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,4) };
            row.Children.Add(new TextBlock { Text=entry.Item1,Width=90,Foreground=Brush("#F5F7FA") });
            var input=new TextBox { Width=260,FlowDirection=FlowDirection.LeftToRight };
            input.SetBinding(TextBox.TextProperty,new Binding(entry.Item2) { Source=vm,Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.LostFocus });
            row.Children.Add(input);identity.Children.Add(row);
        }
        var addCheck=new Button { Content="افزودن استپ تأیید شیفت به Desktop / Startup",Margin=new Thickness(0,8,0,8),Padding=new Thickness(10,6,10,6) };
        addCheck.Click+=(_,_)=>vm.AddShiftCheck();identity.Children.Add(addCheck);
        identity.Children.Add(Bound(nameof(MainViewModel.ShiftStatusText),13));body.Children.Add(identity);
        var buffBox = new StackPanel { Margin = new Thickness(0,0,0,18) };
        buffBox.Children.Add(new TextBlock { Text="باف‌های محیط بازی — Pico", FontSize=20, Foreground=Brush("#F5F7FA") });
        buffBox.Children.Add(new TextBlock {
            Text="ترتیب تصادفی • مصرف اولیه قبل از ماهیگیری • تجدید پس از Catch/Timeout • Pause ساعت باف را متوقف نمی‌کند. تغییرات پس از Export Native UF2 اعمال می‌شوند.",
            TextWrapping=TextWrapping.Wrap, Foreground=Brush("#AAB3C2"), Margin=new Thickness(0,6,0,8) });
        var buffList = new WpfListBox { Height=150, DisplayMemberPath="Name", Background=Brush("#22262D"), Foreground=Brush("#F5F7FA") };
        buffList.SetBinding(ItemsControl.ItemsSourceProperty,new Binding(nameof(MainViewModel.GameBuffs)) { Source=vm });
        buffBox.Children.Add(buffList);
        var actions = new StackPanel { Orientation=Orientation.Horizontal };
        var addBuff = new Button { Content="افزودن باف", Margin=new Thickness(3), Padding=new Thickness(10,6,10,6) };
        var editBuff = new Button { Content="ویرایش باف", Margin=new Thickness(3), Padding=new Thickness(10,6,10,6) };
        var removeBuff = new Button { Content="حذف باف", Margin=new Thickness(3), Padding=new Thickness(10,6,10,6) };
        addBuff.Click+=(_,_)=>vm.EditGameBuff(null);
        editBuff.Click+=(_,_)=> { if(buffList.SelectedItem is StepNode buff)vm.EditGameBuff(buff); };
        removeBuff.Click+=(_,_)=>vm.RemoveGameBuff(buffList.SelectedItem as StepNode);
        actions.Children.Add(addBuff);actions.Children.Add(editBuff);actions.Children.Add(removeBuff);
        buffBox.Children.Add(actions);
        var fatigueRow=new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(0,8,0,8) };
        fatigueRow.Children.Add(new TextBlock { Text="مدت افت تدریجی سرعت موس (دقیقهٔ ماهیگیری فعال): ", Foreground=Brush("#AAB3C2") });
        var fatigueInput=new TextBox { Width=90 };
        fatigueInput.SetBinding(TextBox.TextProperty,new Binding(nameof(MainViewModel.GameMouseFatigueMinutes)) { Source=vm, Mode=BindingMode.TwoWay, UpdateSourceTrigger=UpdateSourceTrigger.LostFocus, ValidatesOnExceptions=true });
        fatigueRow.Children.Add(fatigueInput);buffBox.Children.Add(fatigueRow);
        buffBox.Children.Add(Bound(nameof(MainViewModel.GameBuffStatusText),13));
        body.Children.Add(buffBox);
        body.Children.Add(new TextBlock
        {
            Text = "وضعیت زنده‌ی سنسور نور",
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F5F7FA"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        body.Children.Add(new TextBlock
        {
            Text = "پایش فقط خواندنی است؛ تغییر نور هیچ ماکرو یا واکنش Armed را اجرا نمی‌کند.",
            FontSize = 13,
            Foreground = Brush("#AAB3C2"),
            Margin = new Thickness(0, 0, 0, 16),
        });

        var summary = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        var connection = Card("اتصال", Bound(nameof(MainViewModel.StatusText), 14), Bound(nameof(MainViewModel.LightSensorStatus), 17, FontWeights.SemiBold));
        var freshness = Card("آخرین نمونه", Bound(nameof(MainViewModel.LastLightSampleText), 17, FontWeights.SemiBold), Bound(nameof(MainViewModel.LightFreshnessText), 13));
        var lux = Card("نور فعلی (Lux)", Bound(nameof(MainViewModel.CurrentLuxDisplay), 38, FontWeights.Bold), Bound(nameof(MainViewModel.LightSensorMode), 13));
        Grid.SetColumn(connection, 0); Grid.SetColumn(freshness, 1); Grid.SetColumn(lux, 2);
        summary.Children.Add(connection); summary.Children.Add(freshness); summary.Children.Add(lux);
        body.Children.Add(summary);

        var controls = new Border
        {
            Background = Brush("#22262D"), BorderBrush = Brush("#343B46"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12),
        };
        var controlRow = new StackPanel { Orientation = Orientation.Horizontal };
        var toggle = new Button
        {
            Command = vm.ToggleLightWatchCommand,
            Padding = new Thickness(18, 8, 18, 8), MinWidth = 125,
            Background = Brush("#5E9FE8"), Foreground = Brush("#10151C"), FontWeight = FontWeights.SemiBold,
        };
        toggle.SetBinding(ContentControl.ContentProperty, new Binding(nameof(MainViewModel.LightWatchButtonText)));
        controlRow.Children.Add(toggle);
        controlRow.Children.Add(new TextBlock { Text = "فاصله نمونه‌برداری:", Foreground = Brush("#D9DEE7"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0) });
        var interval = new WpfComboBox { ItemsSource = vm.LightWatchIntervalsMs, Width = 100, Padding = new Thickness(6), VerticalContentAlignment = VerticalAlignment.Center };
        interval.SetBinding(WpfComboBox.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedLightWatchIntervalMs)) { Mode = BindingMode.TwoWay });
        controlRow.Children.Add(interval);
        controlRow.Children.Add(new TextBlock { Text = "میلی‌ثانیه", Foreground = Brush("#AAB3C2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        controls.Child = controlRow;
        body.Children.Add(controls);

        var stats = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        for (var i = 0; i < 4; i++) stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var statItems = new[]
        {
            Card("کمینه ۶۰ ثانیه", Bound(nameof(MainViewModel.LightMinimumDisplay), 22, FontWeights.SemiBold)),
            Card("بیشینه ۶۰ ثانیه", Bound(nameof(MainViewModel.LightMaximumDisplay), 22, FontWeights.SemiBold)),
            Card("میانگین ۶۰ ثانیه", Bound(nameof(MainViewModel.LightAverageDisplay), 22, FontWeights.SemiBold)),
            Card("دامنه تغییر", Bound(nameof(MainViewModel.LightSpreadDisplay), 22, FontWeights.SemiBold)),
        };
        for (var i = 0; i < statItems.Length; i++) { Grid.SetColumn(statItems[i], i); stats.Children.Add(statItems[i]); }
        body.Children.Add(stats);

        var chartCard = new Border
        {
            Background = Brush("#22262D"), BorderBrush = Brush("#343B46"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14),
        };
        var chartBody = new StackPanel();
        chartBody.Children.Add(new TextBlock { Text = "نمودار نور · پنجره ۶۰ ثانیه", Foreground = Brush("#AAB3C2"), FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        var chart = new Canvas { Height = 180, Background = Brush("#181B20"), ClipToBounds = true };
        var line = new Polyline { Stroke = Brush("#66D9A6"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        chart.Children.Add(line);
        chartBody.Children.Add(chart);
        chartCard.Child = chartBody;
        body.Children.Add(chartCard);

        void DrawChart()
        {
            var values = vm.GetLightChartLuxSnapshot();
            line.Points.Clear();
            if (values.Count == 0 || chart.ActualWidth <= 2 || chart.ActualHeight <= 2) return;
            var lo = values.Min();
            var hi = values.Max();
            var range = Math.Max(1, hi - lo);
            for (var i = 0; i < values.Count; i++)
            {
                var x = values.Count == 1 ? chart.ActualWidth / 2 : i * chart.ActualWidth / (values.Count - 1);
                var y = chart.ActualHeight - 10 - ((values[i] - lo) / range) * (chart.ActualHeight - 20);
                line.Points.Add(new WpfPoint(x, y));
            }
        }
        chart.SizeChanged += (_, _) => DrawChart();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.LightChartVersion)) DrawChart();
        };
        return root;
    }

    private static Border Card(string title, params UIElement[] content)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, Foreground = Brush("#AAB3C2"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var item in content) body.Children.Add(item);
        return new Border
        {
            Child = body, Background = Brush("#22262D"), BorderBrush = Brush("#343B46"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Margin = new Thickness(4), MinHeight = 92,
        };
    }

    private static TextBlock Bound(string path, double size, FontWeight? weight = null)
    {
        var text = new TextBlock
        {
            FontSize = size, FontWeight = weight ?? FontWeights.Normal, Foreground = Brush("#F5F7FA"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1),
        };
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        return text;
    }

    private static SolidColorBrush Brush(string color) => new((WpfColor)WpfColorConverter.ConvertFromString(color));

    private static void UpdateTabKeyBindings(MainWindow window, MainViewModel vm)
    {
        for (int i = window.InputBindings.Count - 1; i >= 0; i--)
        {
            if (window.InputBindings[i] is KeyBinding kb && ReferenceEquals(kb.Command, vm.SwitchPipelineCommand))
                window.InputBindings.RemoveAt(i);
        }
        for (var i = 0; i < vm.PipelineTabs.Count && i < 9; i++)
        {
            window.InputBindings.Add(new KeyBinding(vm.SwitchPipelineCommand,
                new KeyGesture((Key)((int)Key.D1 + i), ModifierKeys.Control))
                { CommandParameter = vm.PipelineTabs[i] });
        }
    }

    private static Button ToolButton(string title, ICommand command) => new()
    {
        Content = title,
        Command = command,
        Padding = new Thickness(9, 5, 9, 5),
        Margin = new Thickness(3, 3, 0, 3),
        MinWidth = 64,
    };

    private static void Paint(IEnumerable<Button> buttons, PipelineTabDocument? active)
    {
        foreach (var button in buttons)
        {
            var selected = button.Tag is PipelineKind kind && active?.Kind == kind;
            PaintButton(button, selected);
        }
    }

    private static void PaintStatus(Button button, bool selected) => PaintButton(button, selected);

    private static void PaintButton(Button button, bool selected)
    {
        button.Background = Brush(selected ? "#5E9FE8" : "#333740");
        button.Foreground = Brush(selected ? "#10151C" : "#F5F7FA");
        button.BorderBrush = Brush(selected ? "#5E9FE8" : "#444A55");
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
