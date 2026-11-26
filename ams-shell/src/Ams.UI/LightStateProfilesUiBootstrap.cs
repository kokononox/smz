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
    // Keep these shared with the calibration-form refresh after the send column was added.
    internal const int ProfileNameColumn = 1;
    internal const int ProfileSensor1Column = 5;
    internal const int ProfileSensor2Column = 6;
    internal const int ProfileSignatureColumn = 7;
    internal const int ProfileMeanToleranceColumn = 11;
    internal const int ProfileDifferenceToleranceColumn = 12;

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
        var audioBody = FindAudioBody(window);
        if (body is null || audioBody is null) return;
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
        editorBody.Children.Add(Text("تلورانس‌های M و Δ برای هر وضعیت در دو ستون جداگانه قابل تنظیم‌اند؛ حداقل هرکدام ۳ لوکس است. هم‌پوشانی در یک محور اشکالی ندارد، اما پروفایل‌ها باید در فضای دوبعدی با حاشیهٔ امن ۱ لوکس جدا بمانند. دقت قابل‌ارسال به برد برای M برابر ۰٫۰۵ و برای Δ برابر ۰٫۱ لوکس است.", 12, "#AAB3C2"));
        editorBody.Children.Add(Text("پروفایل‌های دوحسگری با جابه‌جایی سنسورها معتبر نمی‌مانند. پس از هر جابه‌جایی، «چیدمان دوحسگری جدید» را بزنید و همهٔ محیط‌های فعال را دوباره کالیبره کنید؛ هر وضعیت را می‌توان جداگانه یا همراه با بقیهٔ وضعیت‌های کالیبره‌شده به برد فرستاد. همگام‌سازی با برد فقط مراکز M/Δ را می‌فرستد؛ برای اعمال تلورانس‌های جدید، پروفایل‌ها را ذخیره کنید، Native UF2 را دوباره بسازید و آن را فلش کنید. خلاصهٔ خام L1/L2 فقط در Studio نگه‌داری می‌شود.", 12, "#DE9255"));
        editorBody.Children.Add(Text("تلورانس پیش‌فرض کالیبراسیون فیزیکی تک‌سنسوره (L1، لوکس، مرکز ± عدد):", 12, "#D9DEE7"));
        var defaultTolerance = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6), Margin = new Thickness(3), Background = Brush("#181B20"),
            Foreground = Brush("#F5F7FA"), BorderBrush = Brush("#444A55") };
        defaultTolerance.SetBinding(TextBox.TextProperty, new Binding(nameof(MainViewModel.PhysicalCalibrationToleranceLux))
        { Source = vm, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true });
        editorBody.Children.Add(defaultTolerance);
        editorBody.Children.Add(Text("برای برد تک‌سنسوره، پروفایل‌ها و تلورانس پیش‌فرض ارسال می‌شوند؛ در حالت دوحسگری مراکز جفتی ارسال و با برد بازخوانی و تأیید می‌شوند، اما تلورانس‌ها باید با UF2 فعال یکی باشند.", 12, "#AAB3C2"));
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
            rows.Children.Add(ProfileHeader());
            foreach (var profile in vm.LightStateProfiles)
                rows.Children.Add(ProfileRow(window, vm, profile, RebuildRows));
        }
        RebuildRows();

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var importFromBoard = ActionButton("وارد کردن از برد", "#2F7D5A", "#F5F7FA");
        importFromBoard.Click += async (_, _) =>
        {
            if (MessageBox.Show(window,
                    "کالیبراسیون موجود از برد وارد شود؟ در حالت دوحسگری، مراکز M/Δ وارد می‌شوند؛ خلاصهٔ خام L1/L2 از برد قابل بازیابی نیست. سایر تنظیمات پروفایل‌ها تغییر نمی‌کنند.",
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
                    "در حالت دوحسگری، همهٔ وضعیت‌های فعال و جفت‌کالیبره‌شده ارسال می‌شوند؛ وضعیت‌های کالیبره‌نشده روی برد بدون تغییر می‌مانند و لازم نیست هر ۹ وضعیت آماده باشد. در حالت تک‌سنسوره، هر ۹ پروفایل ارسال می‌شوند. تلورانس‌های دوحسگری باید با UF2 فعال یکی باشند. اجرای برد و کالیبراسیون باید متوقف باشند.",
                    "ارسال به برد", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (Validation.GetHasError(defaultTolerance)) { vm.ReportLightProfileEditorValidationError(); return; }
            if (rows.Children.OfType<Grid>()
                .SelectMany(grid => grid.Children.OfType<TextBox>())
                .Any(box => Validation.GetHasError(box)))
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
        save.Click += (_, _) => { if (Validation.GetHasError(defaultTolerance)) { vm.ReportLightProfileEditorValidationError(); return; } if (vm.SaveLightStateProfiles()) RebuildRows(); };
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

        audioBody.Children.Add(CalibrationCueMenu(window, vm));
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

    private static Grid ProfileHeader()
    {
        var row = ProfileGrid();
        Add(row, Text("فعال", 11, "#AAB3C2"), 0);
        Add(row, Text("نام وضعیت", 11, "#AAB3C2"), ProfileNameColumn);
        Add(row, Text("ارسال / وارد کردن تکی", 11, "#AAB3C2"), 2);
        Add(row, Text("مرکز تک‌سنسور L1", 11, "#AAB3C2"), 3);
        Add(row, Text("تلورانس L1 ±", 11, "#AAB3C2"), 4);
        Add(row, Text("حسگر ۱ · میانه / بازهٔ آخرین برداشت", 11, "#AAB3C2"), ProfileSensor1Column);
        Add(row, Text("حسگر ۲ · میانه / بازهٔ آخرین برداشت", 11, "#AAB3C2"), ProfileSensor2Column);
        Add(row, Text("امضای طبقه‌بندی M / Δ", 11, "#AAB3C2"), ProfileSignatureColumn);
        Add(row, Text("پایداری ms", 11, "#AAB3C2"), 8);
        Add(row, Text("Hysteresis", 11, "#AAB3C2"), 9);
        Add(row, Text("Cooldown نوری ms · حداکثر ۶۰ دقیقه", 11, "#AAB3C2"), 10);
        Add(row, Text("تلورانس M ± · حداقل ۳ Lux", 11, "#AAB3C2"), ProfileMeanToleranceColumn);
        Add(row, Text("تلورانس Δ ± · حداقل ۳ Lux", 11, "#AAB3C2"), ProfileDifferenceToleranceColumn);
        return row;
    }

    private static Grid ProfileRow(MainWindow window, MainViewModel vm,
        LightStateProfile profile, Action rebuildRows)
    {
        var row = ProfileGrid();
        row.Margin = new Thickness(0, 3, 0, 3);
        var enabled = new CheckBox { IsChecked = profile.Enabled, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        enabled.Checked += (_, _) => profile.Enabled = true;
        enabled.Unchecked += (_, _) => profile.Enabled = false;
        Add(row, enabled, 0);
        Add(row, Editor(profile, nameof(profile.Name)), ProfileNameColumn);
        var syncActions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var send = ActionButton("ارسال به برد", "#5D477F", "#F5F7FA");
        send.Margin = new Thickness(4, 2, 0, 2);
        send.Click += async (_, _) =>
        {
            if (row.Children.OfType<TextBox>().Any(box => Validation.GetHasError(box)))
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
        var import = ActionButton("وارد از برد", "#2F7D5A", "#F5F7FA");
        import.Margin = new Thickness(4, 2, 0, 2);
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
        syncActions.Children.Add(send);
        syncActions.Children.Add(import);
        Add(row, syncActions, 2);
        var isPaired = profile.HasDualSensorCalibration || profile.DualSensorCalibrationRequired;
        if (isPaired)
        {
            Add(row, Text("—", 11, "#777F8C"), 3);
            Add(row, Text("—", 11, "#777F8C"), 4);
        }
        else
        {
            Add(row, Editor(profile, nameof(profile.LuxCenter)), 3);
            Add(row, Editor(profile, nameof(profile.LuxTolerance)), 4);
        }
        Add(row, Text(DescribePairedSamples(profile, sensor1: true), 10, "#D9DEE7"), ProfileSensor1Column);
        Add(row, Text(DescribePairedSamples(profile, sensor1: false), 10, "#D9DEE7"), ProfileSensor2Column);
        Add(row, Text(profile.HasDualSensorCalibration
                ? $"M {profile.MeanLuxCenter:0.#} ±{profile.MeanLuxTolerance:0.#}\nΔ {profile.DifferenceLuxCenter:0.#} ±{profile.DifferenceLuxTolerance:0.#}"
                : profile.DualSensorCalibrationRequired
                    ? "منتظر کالیبراسیون جفتی"
                    : $"L1 · {profile.LuxMin:0.#} تا {profile.LuxMax:0.#}",
            11, "#D9DEE7"), ProfileSignatureColumn);
        Add(row, Editor(profile, nameof(profile.StableDurationMs)), 8);
        Add(row, Editor(profile, nameof(profile.HysteresisLux)), 9);
        Add(row, profile.Id is "whisper-repeat" or "targeted-repeat"
            ? Editor(profile, nameof(profile.LightCooldownMs))
            : Text("—", 12, "#777F8C"), 10);
        if (profile.HasDualSensorCalibration)
        {
            Add(row, Editor(profile, nameof(profile.MeanLuxTolerance)), ProfileMeanToleranceColumn);
            Add(row, Editor(profile, nameof(profile.DifferenceLuxTolerance)),
                ProfileDifferenceToleranceColumn);
        }
        else if (profile.DualSensorCalibrationRequired)
        {
            Add(row, Text("بعد از کالیبراسیون", 10, "#777F8C"), ProfileMeanToleranceColumn);
            Add(row, Text("بعد از کالیبراسیون", 10, "#777F8C"),
                ProfileDifferenceToleranceColumn);
        }
        else
        {
            Add(row, Text("—", 12, "#777F8C"), ProfileMeanToleranceColumn);
            Add(row, Text("—", 12, "#777F8C"), ProfileDifferenceToleranceColumn);
        }
        return row;
    }

    private static string DescribePairedSamples(LightStateProfile profile, bool sensor1)
    {
        if (profile.DualSensorCalibrationRequired && !profile.HasDualSensorCalibration)
            return "منتظر کالیبراسیون";
        if (!profile.HasDualSensorCalibration) return "—";

        var center = sensor1 ? profile.Sensor1LuxCenter : profile.Sensor2LuxCenter;
        var minimum = sensor1 ? profile.Sensor1LuxMinimum : profile.Sensor2LuxMinimum;
        var maximum = sensor1 ? profile.Sensor1LuxMaximum : profile.Sensor2LuxMaximum;
        if (center is not double median || minimum is not double low || maximum is not double high)
            return "نمونهٔ خام در کالیبراسیون قبلی ذخیره نشده";
        return $"میانه {median:0.0} Lux\n{low:0.0}–{high:0.0} · {profile.DualSensorSampleCount ?? 0} جفت";
    }

    private static Grid ProfileGrid()
    {
        var grid = new Grid { MinWidth = 1680 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        for (var i = 0; i < 10; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
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
        var box = new TextBox
        {
            Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(3), MinWidth = 70,
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
