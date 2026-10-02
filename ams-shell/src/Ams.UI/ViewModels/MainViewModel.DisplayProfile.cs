using Ams.UI.Models;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    public static IReadOnlyList<string> DisplayResolutionPresets { get; } =
    [
        "800x600", "1024x768", "1152x864", "1280x720", "1280x768",
        "1280x800", "1280x960", "1280x1024", "1360x768", "1366x768",
        "1400x1050", "1440x900", "1600x900", "1600x1024", "1600x1200",
        "1680x1050", "1768x992", "1776x1000", "1920x1080", "Custom",
    ];

    public string DisplayResolutionPreset
    {
        get => _pipelineWorkspace.DisplayProfile.Preset;
        set
        {
            var selected = string.IsNullOrWhiteSpace(value) ? "1920x1080" : value;
            if (_pipelineWorkspace.DisplayProfile.Preset == selected) return;
            _pipelineWorkspace.DisplayProfile.Preset = selected;
            if (selected != "Custom" && TryParseResolution(selected, out var width, out var height))
            {
                _pipelineWorkspace.DisplayProfile.Width = width;
                _pipelineWorkspace.DisplayProfile.Height = height;
                OnPropertyChanged(nameof(DisplayCustomWidth));
                OnPropertyChanged(nameof(DisplayCustomHeight));
            }
            MarkDirty();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomDisplayResolution));
            OnPropertyChanged(nameof(DisplayProfileSummary));
        }
    }

    public bool IsCustomDisplayResolution
        => DisplayResolutionPreset == "Custom";

    public int DisplayCustomWidth
    {
        get => _pipelineWorkspace.DisplayProfile.Width;
        set
        {
            var normalized = Math.Clamp(value, 640, 7680);
            if (_pipelineWorkspace.DisplayProfile.Width == normalized) return;
            _pipelineWorkspace.DisplayProfile.Width = normalized;
            MarkDirty();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayProfileSummary));
        }
    }

    public int DisplayCustomHeight
    {
        get => _pipelineWorkspace.DisplayProfile.Height;
        set
        {
            var normalized = Math.Clamp(value, 480, 4320);
            if (_pipelineWorkspace.DisplayProfile.Height == normalized) return;
            _pipelineWorkspace.DisplayProfile.Height = normalized;
            MarkDirty();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayProfileSummary));
        }
    }

    public bool SoftBoundaryEnabled
    {
        get => _pipelineWorkspace.DisplayProfile.SoftBoundaryEnabled;
        set
        {
            if (_pipelineWorkspace.DisplayProfile.SoftBoundaryEnabled == value) return;
            _pipelineWorkspace.DisplayProfile.SoftBoundaryEnabled = value;
            MarkDirty();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayProfileSummary));
        }
    }

    public int SoftBoundaryMarginPercent
    {
        get => _pipelineWorkspace.DisplayProfile.SoftMarginPercent;
        set
        {
            var normalized = Math.Clamp(value, 1, 20);
            if (_pipelineWorkspace.DisplayProfile.SoftMarginPercent == normalized) return;
            _pipelineWorkspace.DisplayProfile.SoftMarginPercent = normalized;
            MarkDirty();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayProfileSummary));
        }
    }

    public string DisplayProfileSummary
        => $"{DisplayCustomWidth}×{DisplayCustomHeight} · "
           + (SoftBoundaryEnabled
               ? $"Soft Boundary با حاشیهٔ {SoftBoundaryMarginPercent}٪"
               : "Soft Boundary خاموش");

    private static bool TryParseResolution(string text, out int width, out int height)
    {
        width = height = 0;
        var parts = text.Split('x', 2);
        return parts.Length == 2
            && int.TryParse(parts[0], out width)
            && int.TryParse(parts[1], out height);
    }

    private void NotifyDisplayProfileChanged()
    {
        OnPropertyChanged(nameof(DisplayResolutionPreset));
        OnPropertyChanged(nameof(IsCustomDisplayResolution));
        OnPropertyChanged(nameof(DisplayCustomWidth));
        OnPropertyChanged(nameof(DisplayCustomHeight));
        OnPropertyChanged(nameof(SoftBoundaryEnabled));
        OnPropertyChanged(nameof(SoftBoundaryMarginPercent));
        OnPropertyChanged(nameof(DisplayProfileSummary));
    }
}