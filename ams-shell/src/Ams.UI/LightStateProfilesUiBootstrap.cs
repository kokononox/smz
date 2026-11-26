using System.Runtime.CompilerServices;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Ams.UI.Models;
using Ams.UI.ViewModels;
using Ams.UI.Views;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace Ams.UI;

/// <summary>Adds phase-four state/profile diagnostics to the existing read-only Status surface.</summary>
internal static class LightStateProfilesUiBootstrap
{
    private static readonly DependencyProperty InstalledProperty = DependencyProperty.RegisterAttached(
        "LightStateProfilesUiInstalled", typeof(bool), typeof(LightStateProfilesUiBootstrap), new PropertyMetadata(false));
    private static readonly DependencyProperty AudioCueMenuInstalledProperty = DependencyProperty.RegisterAttached(
        "LightStateProfilesAudioCueMenuInstalled", typeof(bool), typeof(LightStateProfilesUiBootstrap), new PropertyMetadata(false));
    private static readonly DependencyProperty AudioCueMenuRetryInstalledProperty = DependencyProperty.RegisterAttached(
        "LightStateProfilesAudioCueMenuRetryInstalled", typeof(bool), typeof(LightStateProfilesUiBootstrap), new PropertyMetadata(false));

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
        editorBody.Children.Add(Text("چیدمان و کالیبراسیون دوحسگری در فایل پروژهٔ ‎.amsj ذخیره می‌شوند؛ بعد از ذخیرهٔ پروفایل‌ها، پروژه را هم ذخیره کنید تا همراه ماکرو بمانند.", 12, "#AAB3C2"));
        editorBody.Children.Add(Text("در هر پروفایل دوحسگری، میانه و بازهٔ مشاهده‌شدهٔ خام L1 و L2 جداگانه ذخیره و نمایش داده می‌شود؛ تشخیص همچنان با M=(L1+L2)/2 و Δ=L2−L1 انجام می‌شود. این بازهٔ خام فقط مربوط به آخرین برداشت کالیبراسیون است، نه تغییرات کل روز.", 12, "#AAB3C2"));
        editorBody.Children.Add(Text("تلورانس‌های M و Δ به‌طور پیش‌فرض هوشمندند: از پراکندگی مقاوم نمونه‌ها پیشنهاد می‌شوند و فقط تا جایی کم می‌شوند که پروفایل‌ها با حاشیهٔ امن ۱ لوکس جدا بمانند. اگر حتی کف پوشش نویز هم هم‌پوشانی داشته باشد، اعمال رد می‌شود. با انتخاب «دستی» می‌توانید مقدارها را تنظیم کنید؛ این مقدارها در کالیبراسیون‌های بعدی حفظ می‌شوند. کف فعلی هر محور ۳ لوکس است؛ دقت برد برای M برابر ۰٫۰۵ و برای Δ برابر ۰٫۱ لوکس است.", 12, "#AAB3C2"));
        editorBody.Children.Add(Text("پروفایل‌های دوحسگری با جابه‌جایی سنسورها معتبر نمی‌مانند. پس از هر جابه‌جایی، «چیدمان دوحسگری جدید» را بزنید و همهٔ محیط‌های فعال را دوباره کالیبره کنید؛ هر وضعیت را می‌توان جداگانه یا همراه با بقیهٔ وضعیت‌های کالیبره‌شده به برد فرستاد. همگام‌سازی، مرکزها و تلورانس‌های M/Δ هر وضعیت را مستقیماً روی برد ذخیره می‌کند؛ پس از نصب این firmware، برای تغییر تلورانس نیازی به ساخت یا فلش دوبارهٔ UF2 نیست. خلاصهٔ خام L1/L2 فقط در Studio نگه‌داری می‌شود.", 12, "#DE9255"));
        editorBody.Children.Add(Text("تلورانس پیش‌فرض کالیبراسیون فیزیکی تک‌سنسوره (L1، لوکس، مرکز ± عدد):", 12, "#D9DEE7"));
        var defaultTolerance = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6), Margin = new Thickness(3), Background = Brush("#181B20"),
            Foreground = Brush("#F5F7FA"), BorderBrush = Brush("#444A55") };
        defaultTolerance.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.PhysicalCalibrationToleranceLux))
        { Source = vm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true });
        editorBody.Children.Add(defaultTolerance);
        editorBody.Children.Add(Text("برای برد تک‌سنسوره، پروفایل‌ها و تلورانس پیش‌فرض ارسال می‌شوند؛ در حالت دوحسگری مرکزها و تلورانس‌های M/Δ ارسال، روی برد ذخیره و بازخوانی و تأیید می‌شوند.", 12, "#AAB3C2"));
        var rows = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
        editorBody.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = rows,
        });

        void RebuildRows()
        {
            rows.Children.Clear();
            rows.Children.Add(ProfileTable(window, vm, vm.LightStateProfiles, rows, RebuildRows));
        }
        RebuildRows();

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var importFromBoard = ActionButton("وارد کردن از برد", "#2F7D5A", "#F5F7FA");
        importFromBoard.Click += async (_, _) =>
        {
            if (MessageBox.Show(window,
                    "کالیبراسیون موجود از برد وارد شود؟ در حالت دوحسگری، مرکزها و تلورانس‌های M/Δ وارد می‌شوند؛ خلاصهٔ خام L1/L2 از برد قابل بازیابی نیست. سایر تنظیمات پروفایل‌ها تغییر نمی‌کنند.",
                    "وارد کردن کالیبراسیون از برد", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            importFromBoard.IsEnabled = false;
            try
            {
                if (await vm.ImportLightStateProfilesFromBoardAsync()) RebuildRows();
            }
            finally
            {
                importFromBoard.IsEnabled = true;
            }
        };
        var sendToBoard = ActionButton("ارسال همه به برد", "#8D67C8", "#F5F7FA");
        sendToBoard.Margin = new Thickness(8, 0, 0, 0);
        sendToBoard.Click += async (_, _) =>
        {
            if (MessageBox.Show(window,
                    "در حالت دوحسگری، همهٔ وضعیت‌های فعال و جفت‌کالیبره‌شده همراه مرکزها و تلورانس‌های M/Δ ارسال می‌شوند؛ وضعیت‌های کالیبره‌نشده روی برد بدون تغییر می‌مانند و لازم نیست هر ۹ وضعیت آماده باشد. در حالت تک‌سنسوره، هر ۹ پروفایل ارسال می‌شوند. اجرای برد و کالیبراسیون باید متوقف باشند.",
                    "ارسال به برد", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (Validation.GetHasError(defaultTolerance)) { vm.ReportLightProfileEditorValidationError(); return; }
            if (HasAnyProfileValidationError(rows))
            {
                vm.ReportLightProfileEditorValidationError();
                return;
            }
            sendToBoard.IsEnabled = false;
            try { if (await vm.SendLightStateProfilesToBoardAsync()) RebuildRows(); }
            finally { sendToBoard.IsEnabled = true; }
        };
        var save = ActionButton("ذخیره پروفایل‌ها", "#5E9FE8", "#10151C");
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += (_, _) =>
        {
            if (Validation.GetHasError(defaultTolerance) || HasAnyProfileValidationError(rows))
            {
                vm.ReportLightProfileEditorValidationError();
                return;
            }
            if (vm.SaveLightStateProfiles()) RebuildRows();
        };
        var newDualLayout = ActionButton("چیدمان دوحسگری جدید", "#B36A3F", "#F5F7FA");
        newDualLayout.Margin = new Thickness(8, 0, 0, 0);
        newDualLayout.Click += (_, _) =>
        {
            if (MessageBox.Show(window,
                    "ابتدا دو سنسور را در جایگاه نهایی و ثابت قرار دهید. این اقدام همهٔ کالیبراسیون‌های جفتی قبلی را پاک می‌کند، مقادیر قدیمی L1 را فقط برای سازگاری نگه می‌دارد و تشخیص را تا کالیبره‌شدن همهٔ محیط‌های فعال متوقف می‌کند. ادامه می‌دهید؟",
                    "شروع چیدمان دوحسگری جدید", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            vm.ResetDualSensorCalibrationSet();
            RebuildRows();
        };
        var reset = ActionButton("بازگردانی مقادیر اولیه", "#333740", "#F5F7FA");
        reset.Margin = new Thickness(8, 0, 0, 0);
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show(window, "پروفایل‌های نور به مقادیر فرضی اولیهٔ تک‌سنسوره برگردند و نیاز به چیدمان دوحسگری پاک شود؟", "بازگردانی پروفایل‌ها",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            vm.ResetLightStateProfiles();
            RebuildRows();
        };
        actions.Children.Add(importFromBoard); actions.Children.Add(sendToBoard); actions.Children.Add(save);
        actions.Children.Add(newDualLayout); actions.Children.Add(reset);
        editorBody.Children.Add(actions);
        var saveStatus = Bound(nameof(MainViewModel.LightProfileSaveStatus), 12);
        saveStatus.Margin = new Thickness(0, 8, 0, 0);
        editorBody.Children.Add(saveStatus);
        editor.Child = editorBody;
        section.Children.Add(editor);

        EnsureAudioCueMenu(window, vm);
        var insertAt = Math.Max(0, body.Children.Count - 1);
        body.Children.Insert(insertAt, section);

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.LightStateProfiles))
                window.Dispatcher.BeginInvoke(new Action(RebuildRows));
            else if (args.PropertyName == nameof(MainViewModel.LightChartVersion))
                window.Dispatcher.BeginInvoke(new Action(vm.ObserveCurrentLightState));
            else if (args.PropertyName == nameof(MainViewModel.LightFreshnessText)
                     && vm.LightFreshnessText.StartsWith("داده قدیمی", StringComparison.Ordinal))
                vm.MarkCurrentLightStateStale();
        };
    }

    private enum ProfileDisplayKind { Sensor1, Sensor2, Signature }
    private sealed record ProfileDisplayCell(LightStateProfile Profile, ProfileDisplayKind Kind);

    private static Grid ProfileTable(MainWindow window, MainViewModel vm,
        IList<LightStateProfile> profiles, DependencyObject editorRoot, Action rebuildRows)
    {
        const int labelWidth = 100;
        const int profileWidth = 108;
        const int rowCount = 15;
        var table = new Grid
        {
            FlowDirection = FlowDirection.RightToLeft,
            Width = labelWidth + profileWidth * profiles.Count,
            Margin = new Thickness(0, 6, 0, 4),
        };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        for (var i = 0; i < profiles.Count; i++)
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(profileWidth) });
        for (var i = 0; i < rowCount; i++)
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddTableCell(table, Text("ویژگی", 10, "#AAB3C2", FontWeights.SemiBold), 0, 0, "#292E36");
        AddTableCell(table, Text("☑", 10, "#AAB3C2"), 1, 0, "#292E36", "فعال‌بودن موقعیت نوری");
        AddTableCell(table, Text("نام", 10, "#AAB3C2"), 2, 0, "#292E36", "نام موقعیت نوری");
        AddTableCell(table, Text("↑ / ↓", 10, "#AAB3C2"), 3, 0, "#292E36", "ارسال به برد / وارد کردن از برد");
        AddTableCell(table, Text("L1 μ", 10, "#AAB3C2"), 4, 0, "#292E36", "مرکز تک‌حسگر L1");
        AddTableCell(table, Text("L1 ±", 10, "#AAB3C2"), 5, 0, "#292E36", "تلورانس تک‌حسگر L1");
        AddTableCell(table, Text("① خام", 10, "#AAB3C2"), 6, 0, "#292E36", "خلاصهٔ آخرین نمونهٔ خام حسگر ۱");
        AddTableCell(table, Text("② خام", 10, "#AAB3C2"), 7, 0, "#292E36", "خلاصهٔ آخرین نمونهٔ خام حسگر ۲");
        AddTableCell(table, Text("M · Δ", 10, "#AAB3C2"), 8, 0, "#292E36", "امضای طبقه‌بندی میانگین M و اختلاف Δ");
        AddTableCell(table, Text("ثبات ms", 10, "#AAB3C2"), 9, 0, "#292E36", "مدت پایداری");
        AddTableCell(table, Text("Hys. ±", 10, "#AAB3C2"), 10, 0, "#292E36", "هیسترزیس");
        AddTableCell(table, Text("CD ms", 10, "#AAB3C2"), 11, 0, "#292E36", "فاصلهٔ ایمنی نوری");
        AddTableCell(table, Text("M ±", 10, "#AAB3C2"), 12, 0, "#292E36", "تلورانس میانگین M · حداقل ۳ Lux");
        AddTableCell(table, Text("Δ ±", 10, "#AAB3C2"), 13, 0, "#292E36", "تلورانس اختلاف Δ · حداقل ۳ Lux");
        AddTableCell(table, Text("روش تلورانس", 10, "#AAB3C2"), 14, 0, "#292E36",
            "هوشمند از پراکندگی نمونه و فاصلهٔ امن استفاده می‌کند؛ دستی مقدارهای فعلی را نگه می‌دارد.");

        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            var column = index + 1;
            var heading = Text(profile.Name, 10, "#F5F7FA", FontWeights.SemiBold);
            heading.TextAlignment = TextAlignment.Center;
            heading.ToolTip = profile.Name;
            AddTableCell(table, heading, 0, column, "#2F3540");

            var enabled = new CheckBox
            {
                IsChecked = profile.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = "فعال‌بودن این موقعیت",
            };
            enabled.Checked += (_, _) => profile.Enabled = true;
            enabled.Unchecked += (_, _) => profile.Enabled = false;
            AddTableCell(table, enabled, 1, column);

            var name = Editor(profile, nameof(profile.Name));
            name.Width = 96;
            name.ToolTip = profile.Name;
            name.TextChanged += (_, _) =>
            {
                heading.Text = name.Text;
                heading.ToolTip = name.Text;
            };
            AddTableCell(table, name, 2, column);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var send = CompactActionButton("↑", "#5D477F", "ارسال این موقعیت به برد");
            var import = CompactActionButton("↓", "#2F7D5A", "وارد کردن این موقعیت از برد");
            send.Click += async (_, _) =>
            {
                if (ProfileHasValidationError(editorRoot, profile))
                {
                    vm.ReportLightProfileEditorValidationError();
                    return;
                }
                if (!profile.Enabled || !profile.HasDualSensorCalibration)
                {
                    await vm.SendLightStateProfileToBoardAsync(profile.Id);
                    return;
                }
                if (MessageBox.Show(window,
                        $"فقط کالیبراسیون دوحسگری «{profile.Name}» به برد فرستاده شود؟ اجرای برد و کالیبراسیون باید متوقف باشند؛ وضعیت‌های دیگر تغییر نمی‌کنند.",
                        "ارسال یک وضعیت به برد", MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                send.IsEnabled = false;
                try { await vm.SendLightStateProfileToBoardAsync(profile.Id); }
                finally { send.IsEnabled = true; }
            };
            import.Click += async (_, _) =>
            {
                if (MessageBox.Show(window,
                        $"فقط کالیبراسیون دوحسگری «{profile.Name}» از برد وارد شود؟ هشت وضعیت دیگر بدون تغییر می‌مانند.",
                        "وارد کردن یک وضعیت از برد", MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                import.IsEnabled = false;
                send.IsEnabled = false;
                try
                {
                    if (await vm.ImportLightStateProfileFromBoardAsync(profile.Id))
                        rebuildRows();
                }
                finally
                {
                    import.IsEnabled = true;
                    send.IsEnabled = true;
                }
            };
            actions.Children.Add(send);
            actions.Children.Add(import);
            AddTableCell(table, actions, 3, column);

            var paired = profile.HasDualSensorCalibration || profile.DualSensorCalibrationRequired;
            AddTableCell(table, paired ? Text("—", 10, "#777F8C") : CompactEditor(profile, nameof(profile.LuxCenter)), 4, column);
            AddTableCell(table, paired ? Text("—", 10, "#777F8C") : CompactEditor(profile, nameof(profile.LuxTolerance)), 5, column);
            AddTableCell(table, ProfileDisplayText(profile, ProfileDisplayKind.Sensor1), 6, column);
            AddTableCell(table, ProfileDisplayText(profile, ProfileDisplayKind.Sensor2), 7, column);
            AddTableCell(table, ProfileDisplayText(profile, ProfileDisplayKind.Signature), 8, column);
            AddTableCell(table, CompactEditor(profile, nameof(profile.StableDurationMs)), 9, column);
            AddTableCell(table, CompactEditor(profile, nameof(profile.HysteresisLux)), 10, column);
            AddTableCell(table, profile.Id is "whisper-repeat" or "targeted-repeat"
                ? CompactEditor(profile, nameof(profile.LightCooldownMs))
                : Text("—", 10, "#777F8C"), 11, column);
            UIElement meanTolerance = profile.HasDualSensorCalibration
                ? CompactEditor(profile, nameof(profile.MeanLuxTolerance))
                : Text(profile.DualSensorCalibrationRequired ? "◌" : "—", 10, "#777F8C");
            UIElement differenceTolerance = profile.HasDualSensorCalibration
                ? CompactEditor(profile, nameof(profile.DifferenceLuxTolerance))
                : Text(profile.DualSensorCalibrationRequired ? "◌" : "—", 10, "#777F8C");
            if (meanTolerance is TextBox meanBox) meanBox.IsReadOnly = profile.UseAutomaticDualTolerance;
            if (differenceTolerance is TextBox differenceBox)
                differenceBox.IsReadOnly = profile.UseAutomaticDualTolerance;
            AddTableCell(table, meanTolerance, 12, column,
                tooltip: profile.DualSensorCalibrationRequired && !profile.HasDualSensorCalibration
                    ? "پس از کالیبراسیون جفتی قابل تنظیم است"
                    : profile.UseAutomaticDualTolerance
                        ? "خودکار: از پراکندگی نمونه تنظیم می‌شود؛ برای ویرایش، حالت دستی را انتخاب کنید."
                        : "دستی: مقدار انتخاب‌شده در کالیبراسیون بعدی حفظ می‌شود.");
            AddTableCell(table, differenceTolerance, 13, column,
                tooltip: profile.DualSensorCalibrationRequired && !profile.HasDualSensorCalibration
                    ? "پس از کالیبراسیون جفتی قابل تنظیم است"
                    : profile.UseAutomaticDualTolerance
                        ? "خودکار: از پراکندگی نمونه تنظیم می‌شود؛ برای ویرایش، حالت دستی را انتخاب کنید."
                        : "دستی: مقدار انتخاب‌شده در کالیبراسیون بعدی حفظ می‌شود.");

            var automaticTolerance = new CheckBox
            {
                Content = profile.UseAutomaticDualTolerance ? "هوشمند" : "دستی",
                IsChecked = profile.UseAutomaticDualTolerance,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brush("#D9DEE7"),
                ToolTip = "هوشمند: تنظیم از پراکندگی مقاوم نمونه و بیشترین تلورانس امن. دستی: حفظ مقدارهای ویرایش‌شده.",
            };
            automaticTolerance.Checked += (_, _) =>
            {
                if (profile.UseAutomaticDualTolerance) return;
                profile.UseAutomaticDualTolerance = true;
                rebuildRows();
            };
            automaticTolerance.Unchecked += (_, _) =>
            {
                if (!profile.UseAutomaticDualTolerance) return;
                profile.UseAutomaticDualTolerance = false;
                rebuildRows();
            };
            AddTableCell(table, automaticTolerance, 14, column);
        }

        return table;
    }

    internal static void RefreshProfileDisplayCell(DependencyObject element)
    {
        if (element is not TextBlock text || text.Tag is not ProfileDisplayCell cell) return;
        text.Text = DescribeProfileDisplay(cell.Profile, cell.Kind, out var tooltip);
        text.ToolTip = tooltip;
    }

    private static TextBlock ProfileDisplayText(LightStateProfile profile, ProfileDisplayKind kind)
    {
        var text = Text("", 9, "#D9DEE7");
        text.TextAlignment = TextAlignment.Center;
        text.Tag = new ProfileDisplayCell(profile, kind);
        RefreshProfileDisplayCell(text);
        return text;
    }

    private static string DescribeProfileDisplay(LightStateProfile profile,
        ProfileDisplayKind kind, out string tooltip)
    {
        if (kind is ProfileDisplayKind.Sensor1 or ProfileDisplayKind.Sensor2)
        {
            var sensor = kind == ProfileDisplayKind.Sensor1 ? 1 : 2;
            var center = sensor == 1 ? profile.Sensor1LuxCenter : profile.Sensor2LuxCenter;
            var minimum = sensor == 1 ? profile.Sensor1LuxMinimum : profile.Sensor2LuxMinimum;
            var maximum = sensor == 1 ? profile.Sensor1LuxMaximum : profile.Sensor2LuxMaximum;
            if (profile.DualSensorCalibrationRequired && !profile.HasDualSensorCalibration)
            {
                tooltip = $"حسگر {sensor}: منتظر کالیبراسیون جفتی";
                return "◌ جفتی";
            }
            if (!profile.HasDualSensorCalibration)
            {
                tooltip = $"حسگر {sensor}: فقط در حالت دوحسگری";
                return "—";
            }
            if (center is not double median || minimum is not double low || maximum is not double high)
            {
                tooltip = $"حسگر {sensor}: نمونهٔ خام در برد ذخیره نشده است";
                return "∅";
            }
            tooltip = $"حسگر {sensor} · میانه {median:0.0} Lux · بازهٔ {low:0.0} تا {high:0.0} Lux · {profile.DualSensorSampleCount ?? 0} جفت";
            return $"{median:0.0}\n{low:0.0}–{high:0.0}";
        }

        if (profile.HasDualSensorCalibration)
        {
            tooltip = $"مرکز M برابر {profile.MeanLuxCenter:0.#} Lux · مرکز Δ برابر {profile.DifferenceLuxCenter:0.#} Lux";
            return $"M {profile.MeanLuxCenter:0.#}\nΔ {profile.DifferenceLuxCenter:0.#}";
        }
        if (profile.DualSensorCalibrationRequired)
        {
            tooltip = "این موقعیت منتظر کالیبراسیون جفتی است";
            return "◌ جفتی";
        }
        tooltip = $"L1 · {profile.LuxMin:0.#} تا {profile.LuxMax:0.#} Lux";
        return $"L1 {profile.LuxMin:0.#}–{profile.LuxMax:0.#}";
    }

    private static void AddTableCell(Grid table, UIElement child, int row, int column,
        string background = "#22262D", string? tooltip = null)
    {
        var cell = new Border
        {
            Background = Brush(background),
            BorderBrush = Brush("#343B46"),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(2),
            Child = child,
            ToolTip = tooltip,
        };
        Grid.SetRow(cell, row);
        Grid.SetColumn(cell, column);
        table.Children.Add(cell);
    }

    private static Button CompactActionButton(string glyph, string color, string tooltip) => new()
    {
        Content = glyph,
        Width = 31,
        Height = 26,
        Padding = new Thickness(0),
        Margin = new Thickness(1),
        Background = Brush(color),
        Foreground = Brush("#F5F7FA"),
        FontWeight = FontWeights.Bold,
        FontSize = 14,
        ToolTip = tooltip,
    };

    private static TextBox CompactEditor(LightStateProfile profile, string path)
    {
        var box = Editor(profile, path);
        box.Width = 68;
        box.Padding = new Thickness(2);
        box.FontSize = 10;
        box.HorizontalContentAlignment = HorizontalAlignment.Center;
        return box;
    }

    private static bool HasAnyProfileValidationError(DependencyObject root)
    {
        if (root is TextBox box && Validation.GetHasError(box)) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (HasAnyProfileValidationError(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }

    private static bool ProfileHasValidationError(DependencyObject root, LightStateProfile profile)
    {
        if (root is TextBox box && Validation.GetHasError(box)
            && box.GetBindingExpression(TextBox.TextProperty)?.ResolvedSource == profile)
            return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (ProfileHasValidationError(VisualTreeHelper.GetChild(root, i), profile)) return true;
        return false;
    }

    private static void EnsureAudioCueMenu(MainWindow window, MainViewModel vm)
    {
        var audioBody = FindAudioBody(window);
        if (audioBody is not null)
        {
            if (audioBody.GetValue(AudioCueMenuInstalledProperty) is not true)
            {
                audioBody.SetValue(AudioCueMenuInstalledProperty, true);
                audioBody.Children.Add(CalibrationCueMenu(window, vm));
            }
            return;
        }

        if (window.GetValue(AudioCueMenuRetryInstalledProperty) is true) return;
        window.SetValue(AudioCueMenuRetryInstalledProperty, true);
        EventHandler retry = null!;
        retry = (_, _) =>
        {
            var found = FindAudioBody(window);
            if (found is null) return;
            window.LayoutUpdated -= retry;
            window.SetValue(AudioCueMenuRetryInstalledProperty, false);
            if (found.GetValue(AudioCueMenuInstalledProperty) is true) return;
            found.SetValue(AudioCueMenuInstalledProperty, true);
            found.Children.Add(CalibrationCueMenu(window, vm));
        };
        window.LayoutUpdated += retry;
    }

    private sealed record CueOption(int Id, string Name);
    private static readonly CueOption[] CueOptions =
    [
        new(0, "۰۰۰ · سفارشی"),
        .. CalibrationCueCatalog.All.Select(x => new CueOption(x.Id, x.DisplayName)),
    ];

    private static Border CalibrationCueMenu(MainWindow window, MainViewModel vm)
    {
        var body = new StackPanel();
        body.Children.Add(Text("صداهای راهنمای کالیبراسیون و سیستم", 14, "#F5F7FA", FontWeights.SemiBold));
        body.Children.Add(Text("هر گروه را فقط هنگام نیاز باز کنید تا صفحه کوتاه و خوانا بماند.", 12, "#AAB3C2"));

        var profileBody = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        profileBody.Children.Add(Text("برای هر محیط از ملودی آماده انتخاب کنید یا در ویرایش حرفه‌ای صدای سفارشی بسازید.", 12, "#AAB3C2"));
        var profileRows = new StackPanel();
        void RebuildProfileCueRows()
        {
            profileRows.Children.Clear();
            foreach (var profile in vm.LightStateProfiles) profileRows.Children.Add(CueRow(window, vm, profile));
        }
        RebuildProfileCueRows();
        profileBody.Children.Add(profileRows);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.LightStateProfiles))
                window.Dispatcher.BeginInvoke(new Action(RebuildProfileCueRows));
        };
        var save = ActionButton("ذخیره صدای محیط‌های نوری", "#5E9FE8", "#10151C");
        save.HorizontalAlignment = HorizontalAlignment.Right;
        save.Margin = new Thickness(0, 8, 0, 0);
        save.Click += (_, _) => vm.SaveLightStateProfiles();
        profileBody.Children.Add(save);
        body.Children.Add(new Expander { Header = "صداهای محیط‌های نوری", IsExpanded = false, Content = profileBody, Foreground = Brush("#F5F7FA"), Margin = new Thickness(0, 6, 0, 2) });

        var systemBody = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        systemBody.Children.Add(Text("صداهای دکمه‌ها، خطاها، Whisper، گذارها، Watchdog و مراحل کالیبراسیون.", 12, "#AAB3C2"));
        foreach (var cue in vm.BuzzerSystemCues) systemBody.Children.Add(CueRow(window, vm, cue));
        var saveSystem = ActionButton("ذخیره صداهای سیستمی", "#5E9FE8", "#10151C");
        saveSystem.HorizontalAlignment = HorizontalAlignment.Right;
        saveSystem.Margin = new Thickness(0, 8, 0, 0);
        saveSystem.Click += (_, _) => vm.SaveBuzzerSystemCues();
        systemBody.Children.Add(saveSystem);
        body.Children.Add(new Expander { Header = "صداهای سیستمی", IsExpanded = false, Content = systemBody, Foreground = Brush("#F5F7FA"), Margin = new Thickness(0, 2, 0, 2) });

        return new Border
        {
            Child = body, Background = Brush("#22262D"), BorderBrush = Brush("#343B46"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14), Margin = new Thickness(4, 0, 4, 12),
        };
    }

    private static Grid CueRow(MainWindow window, MainViewModel vm, LightStateProfile profile)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Add(row, Text(profile.Name, 12, "#D9DEE7"), 0);
        var selector = new WpfComboBox
        {
            ItemsSource = CueOptions, DisplayMemberPath = nameof(CueOption.Name),
            SelectedValuePath = nameof(CueOption.Id), MinWidth = 330, Margin = new Thickness(4),
            IsTextSearchEnabled = true,
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
                preview.Content = await vm.PreviewLightCalibrationCueAsync(profile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(window, ex.Message, "پیش‌شنیدن صدای بازر",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                preview.IsEnabled = true;
                preview.Content = "پیش‌شنیدن";
            }
        };
        Add(row, preview, 2);
        var edit = ActionButton("ویرایش حرفه‌ای…", "#3D6B55", "#F5F7FA");
        edit.Margin = new Thickness(6, 3, 0, 3);
        edit.Click += (_, _) =>
        {
            var dialog = new CalibrationCueEditorDialog(window, vm, profile);
            if (dialog.ShowDialog() != true) return;
            selector.SelectedValue = profile.CalibrationCue;
        };
        Add(row, edit, 3);
        return row;
    }

    private static TextBox Editor(LightStateProfile profile, string path)
    {
        var isName = path == nameof(LightStateProfile.Name);
        var box = new TextBox
        {
            Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(1), MinWidth = 0,
            FontSize = 11,
            FlowDirection = isName ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            TextAlignment = isName ? TextAlignment.Right : TextAlignment.Center,
            Background = Brush("#181B20"), Foreground = Brush("#F5F7FA"), BorderBrush = Brush("#444A55"),
        };
        var binding = new Binding(path)
        {
            Source = profile, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
            ValidatesOnExceptions = true,
        };
        if (path is nameof(LightStateProfile.MeanLuxTolerance)
            or nameof(LightStateProfile.DifferenceLuxTolerance))
        {
            var meanAxis = path == nameof(LightStateProfile.MeanLuxTolerance);
            var unitsPerLux = meanAxis
                ? LightStateProfile.MeanLuxToleranceUnitsPerLux
                : LightStateProfile.DifferenceLuxToleranceUnitsPerLux;
            binding.ValidationRules.Add(new DualAxisToleranceValidationRule(unitsPerLux, meanAxis));
            box.ToolTip = meanAxis
                ? "حداقل ۳ لوکس؛ گام قابل‌ارسال به برد ۰٫۰۵ لوکس"
                : "حداقل ۳ لوکس؛ گام قابل‌ارسال به برد ۰٫۱ لوکس";
            box.LostFocus += (_, _) => profile.UseAutomaticDualTolerance = false;
        }
        box.SetBinding(TextBox.TextProperty, binding);
        return box;
    }

    private sealed class DualAxisToleranceValidationRule : ValidationRule
    {
        private readonly int _unitsPerLux;
        private readonly bool _meanAxis;

        public DualAxisToleranceValidationRule(int unitsPerLux, bool meanAxis)
        {
            _unitsPerLux = unitsPerLux;
            _meanAxis = meanAxis;
            ValidatesOnTargetUpdated = true;
        }

        public override ValidationResult Validate(object value, CultureInfo cultureInfo)
        {
            var text = Convert.ToString(value, cultureInfo) ?? "";
            if (!double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                    cultureInfo, out var tolerance) || !double.IsFinite(tolerance))
                return new ValidationResult(false, "تلورانس باید عدد معتبر باشد.");
            if (tolerance < LightStateProfile.MinimumDualAxisToleranceLux)
                return new ValidationResult(false, "تلورانس هر محور باید دست‌کم ۳ لوکس باشد.");
            if (!LightStateProfile.IsValidDualAxisTolerance(tolerance, _unitsPerLux))
                return new ValidationResult(false, _meanAxis
                    ? "تلورانس M باید با گام ۰٫۰۵ لوکس و حداکثر ۱٬۰۰۰٬۰۰۰ لوکس وارد شود."
                    : "تلورانس Δ باید با گام ۰٫۱ لوکس و حداکثر ۱٬۰۰۰٬۰۰۰ لوکس وارد شود.");
            return ValidationResult.ValidResult;
        }
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

    private static StackPanel? FindAudioBody(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Text: "تنظیمات صوتی" } title
                && VisualTreeHelper.GetParent(title) is StackPanel body) return body;
            var found = FindAudioBody(child);
            if (found is not null) return found;
        }
        return null;
    }

    private static SolidColorBrush Brush(string color) => new((WpfColor)WpfColorConverter.ConvertFromString(color));
}
