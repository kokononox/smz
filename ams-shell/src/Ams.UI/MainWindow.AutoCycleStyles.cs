using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace Ams.UI;

/// <summary>Compact dark-dashboard primitives for AutoCycle inside Play Options.</summary>
internal static class AutoCycleUiKit
{
    internal const string ExportTag = "AutoCycle.Export";
    internal const string AdvancedTag = "AutoCycle.Advanced";
    internal const string EssentialsTag = "AutoCycle.Essentials";
    internal const string ScheduleTag = "AutoCycle.Schedule";
    internal const string ExportActionsTag = "AutoCycle.ExportActions";
    internal const string PlanStepTag = "AutoCycle.PlanStep";
    internal const string FirmwareStepTag = "AutoCycle.FirmwareStep";

    internal static readonly Brush Surface = MakeBrush("#292C33");
    internal static readonly Brush Raised = MakeBrush("#333740");
    internal static readonly Brush BorderBrush = MakeBrush("#444A55");
    internal static readonly Brush Primary = MakeBrush("#5E9FE8");
    internal static readonly Brush OnPrimary = MakeBrush("#10151C");
    internal static readonly Brush PrimarySoft = MakeBrush("#263A52");
    internal static readonly Brush Text = MakeBrush("#F5F7FA");
    internal static readonly Brush Muted = MakeBrush("#B5BBC5");
    internal static readonly Brush Success = MakeBrush("#72BC8F");

    private static SolidColorBrush MakeBrush(string value)
        => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));

    internal static Border Card(string tag, UIElement content)
        => new()
        {
            Tag = tag,
            Background = Surface,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content,
        };

    internal static TextBlock Title(string text) => new()
    {
        Text = text,
        Foreground = Text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
    };

    internal static TextBlock Helper(string text) => new()
    {
        Text = text,
        Foreground = Muted,
        FontSize = 11,
        LineHeight = 16,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 6),
    };

    internal static Button Action(string text, bool primary = false) => new()
    {
        Content = text,
        MinWidth = 188,
        MinHeight = 34,
        Padding = new Thickness(12, 4, 12, 4),
        Margin = new Thickness(8, 0, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Right,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = primary ? OnPrimary : Text,
        Background = primary ? Primary : Raised,
        BorderBrush = primary ? Primary : BorderBrush,
        BorderThickness = new Thickness(1),
    };

    internal static Border Badge(string text, Brush? foreground = null, Brush? background = null)
        => new()
        {
            Background = background ?? PrimarySoft,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                Foreground = foreground ?? Primary,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
            },
        };

    internal static WrapPanel EnsureExportCard(StackPanel body)
    {
        var existing = FindCard(body, ExportTag);
        if (existing?.Child is StackPanel oldPanel)
        {
            var found = oldPanel.Children.OfType<WrapPanel>()
                .FirstOrDefault(x => Equals(x.Tag, ExportActionsTag));
            if (found is not null) return found;
        }

        var panel = new StackPanel { FlowDirection = FlowDirection.RightToLeft };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Title("خروجی AutoCycle برای Pico");
        var badge = Badge("۲ مرحله");
        Grid.SetColumn(title, 0);
        Grid.SetColumn(badge, 1);
        head.Children.Add(title);
        head.Children.Add(badge);
        panel.Children.Add(head);
        panel.Children.Add(Helper("هر دو خروجی را در یک پوشه بساز؛ جای Plan و Firmware عادی را می‌گیرند."));
        var actions = new WrapPanel
        {
            Tag = ExportActionsTag,
            FlowDirection = FlowDirection.RightToLeft,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        panel.Children.Add(actions);
        body.Children.Add(Card(ExportTag, panel));
        Reorder(body);
        return actions;
    }

    internal static StackPanel EnsureAdvancedPanel(StackPanel body)
    {
        var existing = FindCard(body, AdvancedTag);
        if (existing?.Child is Expander oldExpander && oldExpander.Content is StackPanel found)
            return found;

        var panel = new StackPanel { FlowDirection = FlowDirection.RightToLeft };
        var expander = new Expander
        {
            Header = "چرخهٔ AutoCycle — After و Startup",
            IsExpanded = false,
            Foreground = Text,
            FontWeight = FontWeights.SemiBold,
            Content = panel,
        };
        body.Children.Add(Card(AdvancedTag, expander));
        Reorder(body);
        return panel;
    }

    internal static Border? FindCard(StackPanel body, string tag)
        => body.Children.OfType<Border>().FirstOrDefault(x => Equals(x.Tag, tag));

    internal static void ReorderExportSteps(WrapPanel panel)
    {
        var steps = new[] { PlanStepTag, FirmwareStepTag }
            .Select(tag => panel.Children.OfType<Button>().FirstOrDefault(x => Equals(x.Tag, tag)))
            .Where(x => x is not null).Cast<Button>().ToList();
        foreach (var step in steps) panel.Children.Remove(step);
        foreach (var step in steps) panel.Children.Add(step);
    }

    internal static void ReorderAdvanced(StackPanel panel)
    {
        var sections = new[] { ScheduleTag }
            .Select(tag => FindCard(panel, tag)).Where(x => x is not null).Cast<Border>().ToList();
        foreach (var section in sections) panel.Children.Remove(section);
        foreach (var section in sections) panel.Children.Add(section);
    }

    internal static void Reorder(StackPanel body)
    {
        var cards = new[] { ExportTag, AdvancedTag }
            .Select(tag => FindCard(body, tag)).Where(x => x is not null).Cast<Border>().ToList();
        foreach (var card in cards) body.Children.Remove(card);
        foreach (var card in cards) body.Children.Add(card);
    }
}
