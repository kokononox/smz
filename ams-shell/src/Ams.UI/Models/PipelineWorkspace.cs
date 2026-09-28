using System.Collections.ObjectModel;
using Ams.UI.Services;

namespace Ams.UI.Models;

/// <summary>Fixed workflow tabs used by Classroom Studio and the portable Guard exporter.</summary>
public enum PipelineKind
{
    Desktop,
    Restart,
    Startup,
    LoginOrDc,
    Dc,
    CharacterDashboard,
    EnteringGameLoading,
    Game,
    Targeted,
    Whisper,
    Splash,
    // Resume is intentionally not a UI tab for now; keep the enum name only as a
    // source-compatibility alias for the old exporter.

    // Names kept as value-compatible aliases for the earlier five-tab workspace.
    Launch = Restart,
    Main = Desktop,
    LaunchRecovery = Dc,
    MainRecovery = Dc,
    Resumable = Restart,
    ResumeEssentials = Restart,
}

/// <summary>Game-wide acoustic interrupt configuration.</summary>
public sealed class SoundWatchProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Zero means use the calibrated threshold for the lower edge.</summary>
    public int PeakMin { get; set; }
    public int PeakMax { get; set; } = 511;
    public int Priority { get; set; }
    public int MinDurationMs { get; set; } = 60;
    public int ListenWindowMs { get; set; } = 1000;
    public int CooldownMs { get; set; } = 1500;
    /// <summary>
    /// Per-cast timeout range for the scoped Splash listener. These values belong to
    /// the Splash profile, not to the retired generic Wait For Sound editor.
    /// </summary>
    public int TimeoutMinSec { get; set; } = 18;
    public int TimeoutMaxSec { get; set; } = 22;
    public PipelineKind ResponseTab { get; set; }
}

public sealed class PipelineTabDocument
{
    public PipelineKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string FileName { get; init; } = "";
    public ObservableCollection<StepNode> Steps { get; } = new();
    public bool IsDirty { get; set; }
    public string? ValidationError { get; set; }
}

/// <summary>
/// Owns the workflow tabs. Login/DC remains one optical profile, while DC is a
/// separate executable route selected by GuardTransition whenever the same light is observed
/// after the ordered flow has already entered the game environment.
/// </summary>
public sealed class PipelineWorkspace
{
    // Compatibility contract for older exporters: launch_steps.txt, plan.txt,
    // launch_recovery.txt, main_recovery.txt and resume_essentials.txt remain emitted
    // resumable_steps.txt is still emitted as an empty firmware compatibility file
    // alongside the current desktop/restart/DC route files. PipelineKind.Main is the
    // value-compatible name for Desktop in those documents.
    public const int FormatVersion = 5;
    public ObservableCollection<PipelineTabDocument> Tabs { get; } = new()
    {
        new() { Kind = PipelineKind.Desktop, Title = "Desktop", FileName = "desktop_steps.txt" },
        new() { Kind = PipelineKind.Restart, Title = "After", FileName = "restart_steps.txt" },
        new() { Kind = PipelineKind.Startup, Title = "Startup", FileName = "startup_steps.txt" },
        new() { Kind = PipelineKind.LoginOrDc, Title = "Login / DC", FileName = "login_or_dc_steps.txt" },
        new() { Kind = PipelineKind.Dc, Title = "DC", FileName = "dc_steps.txt" },
        new() { Kind = PipelineKind.CharacterDashboard, Title = "Character Dashboard", FileName = "character_dashboard_steps.txt" },
        new() { Kind = PipelineKind.EnteringGameLoading, Title = "Entering Game / Loading", FileName = "entering_game_loading_steps.txt" },
        new() { Kind = PipelineKind.Game, Title = "Game", FileName = "game_steps.txt" },
        new() { Kind = PipelineKind.Targeted, Title = "Targeted", FileName = "targeted_steps.txt" },
        new() { Kind = PipelineKind.Whisper, Title = "Whisper", FileName = "whisper_steps.txt" },
        new() { Kind = PipelineKind.Splash, Title = "Splash", FileName = "splash_steps.txt" },
    };

    public List<SoundWatchProfile> SoundProfiles { get; } = new()
    {
        new() { Id = 1, Name = "Whisper", Enabled = false, PeakMin = 0, PeakMax = 511,
            Priority = 10, MinDurationMs = 60, ListenWindowMs = 1000, CooldownMs = 1800,
            ResponseTab = PipelineKind.Whisper },
        new() { Id = 2, Name = "Splash", Enabled = false, PeakMin = 0, PeakMax = 511,
            Priority = 5, MinDurationMs = 60, ListenWindowMs = 1000, CooldownMs = 900,
            ResponseTab = PipelineKind.Splash },
    };

    public PipelineTabDocument this[PipelineKind kind] => Tabs.Single(x => x.Kind == kind);

    /// <summary>Install the safe default DC popup dismissal when the tab is empty.</summary>
    public void EnsureDcDefaults()
    {
        var dc = this[PipelineKind.Dc];
        if (dc.Steps.Count != 0) return;
        dc.Steps.Add(new StepNode
        {
            Type = "delay",
            Name = "تأخیر تصادفی قبل از بستن Popup DC",
            Props = new Dictionary<string, object?> { ["minMs"] = 88, ["maxMs"] = 188 },
        });
        dc.Steps.Add(new StepNode
        {
            Type = "keyDown",
            Name = "ESC — رد Popup قطع اتصال",
            Props = new Dictionary<string, object?> { ["key"] = "ESC", ["keyboardBoard"] = "default" },
        });
        dc.Steps.Add(new StepNode
        {
            Type = "keyUp",
            Name = "آزادکردن ESC",
            Props = new Dictionary<string, object?> { ["key"] = "ESC", ["keyboardBoard"] = "default" },
        });
        foreach (var node in dc.Steps) node.Parent = null;
    }

    /// <summary>Legacy .amsj files migrate into the requested active tab without losing roots.</summary>
    public static PipelineWorkspace FromLegacy(IEnumerable<StepNode> roots, PipelineKind target = PipelineKind.Desktop)
    {
        var workspace = new PipelineWorkspace();
        foreach (var root in roots)
        {
            DocumentService.FixParents(root, null);
            workspace[target].Steps.Add(root);
        }
        workspace.EnsureDcDefaults();
        return workspace;
    }
}
