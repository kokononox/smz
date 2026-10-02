using System.Text.Json;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>Versioned persistence for workflow tabs and game-wide sound profiles.</summary>
public static class PipelineWorkspaceSerializer
{
    private sealed class Envelope
    {
        public string app { get; set; } = "AMS";
        public int pipelineVersion { get; set; }
        public Dictionary<string, List<StepNode>> pipelines { get; set; } = new();
        public List<SoundWatchProfile> soundProfiles { get; set; } = new();
        public HumanMouseProfile humanMouseProfile { get; set; } = new();
        public DisplayProfile displayProfile { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Serialize(PipelineWorkspace workspace)
    {
        workspace.EnsureDcDefaults();
        var envelope = new Envelope
        {
            pipelineVersion = PipelineWorkspace.FormatVersion,
            soundProfiles = workspace.SoundProfiles.Select(CloneSoundProfile).ToList(),
            humanMouseProfile = workspace.HumanMouseProfile,
            displayProfile = workspace.DisplayProfile,
        };
        foreach (var tab in workspace.Tabs)
            envelope.pipelines[tab.Kind.ToString()] = tab.Steps.ToList();
        return JsonSerializer.Serialize(envelope, Options);
    }

    public static PipelineWorkspace Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("app", out var app) || app.GetString() != "AMS")
            throw new InvalidDataException("Not an AMS pipeline document.");
        if (!root.TryGetProperty("pipelines", out var pipelines) || pipelines.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Pipeline document has no pipelines.");

        var version = root.TryGetProperty("pipelineVersion", out var versionValue)
            && versionValue.TryGetInt32(out var parsed) ? parsed : 0;
        if (version is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10))
            throw new InvalidDataException("Unsupported AMS pipeline document.");

        var workspace = new PipelineWorkspace();
        if (version >= 8 && root.TryGetProperty("humanMouseProfile", out var humanProfile)
            && humanProfile.ValueKind == JsonValueKind.Object)
            workspace.HumanMouseProfile =
                humanProfile.Deserialize<HumanMouseProfile>() ?? new();
        if (version >= 9 && root.TryGetProperty("displayProfile", out var displayProfile)
            && displayProfile.ValueKind == JsonValueKind.Object)
        {
            var loaded = displayProfile.Deserialize<DisplayProfile>() ?? new();
            if (!loaded.IsValid)
                throw new InvalidDataException("Display profile dimensions or soft margin are invalid.");
            workspace.DisplayProfile = loaded;
        }
        foreach (var tab in workspace.Tabs) tab.Steps.Clear();
        var hasDc = false;
        foreach (var property in pipelines.EnumerateObject())
        {
            var target = Map(property.Name);
            if (target is null) continue;
            var roots = property.Value.Deserialize<List<StepNode>>() ?? new();
            var tab = workspace[target.Value];
            foreach (var rootNode in roots)
            {
                DocumentService.FixParents(rootNode, null);
                tab.Steps.Add(rootNode);
            }
            if (target == PipelineKind.Dc) hasDc = true;
        }
        if (version >= 4 && root.TryGetProperty("soundProfiles", out var soundProfiles)
            && soundProfiles.ValueKind == JsonValueKind.Array)
        {
            var loaded = soundProfiles.Deserialize<List<SoundWatchProfile>>() ?? new();
            foreach (var source in loaded)
            {
                var target = workspace.SoundProfiles.FirstOrDefault(x => x.Id == source.Id);
                if (target is null || source.ResponseTab is not
                    (PipelineKind.Whisper or PipelineKind.Splash or PipelineKind.WhisperRepeat))
                    continue;
                CopySoundProfile(source, target);
            }
        }
        MigrateExplicitCatchWait(workspace);
        // Build 100 predates the dedicated DC tab. Give it the safe ESC route on import.
        // An explicitly empty DC tab receives the same default so a blank tab never disables
        // popup dismissal by accident.
        if (!hasDc || workspace[PipelineKind.Dc].Steps.Count == 0)
            workspace.EnsureDcDefaults();
        return workspace;
    }

    /// <summary>
    /// Build 119 reverses the Build-95 UI abstraction. Splash is an explicit,
    /// scoped Wait For Sound at the cast site; only Whisper remains global.
    /// The retired Splash tab is copied into the wait node's visible children.
    /// </summary>
    private static void MigrateExplicitCatchWait(PipelineWorkspace workspace)
    {
        var legacyTab = workspace[PipelineKind.Splash];
        var responseSnapshot = StepTreeSerializer.Snapshot(legacyTab.Steps);
        var profile = workspace.SoundProfiles.FirstOrDefault(x => x.Id == 2)
            ?? new SoundWatchProfile
            {
                Id = 2, Name = "Splash", PeakMin = 90, PeakMax = 511,
                MinDurationMs = 60, TimeoutMinSec = 18, TimeoutMaxSec = 22,
                ResponseTab = PipelineKind.Splash,
            };
        var foundCatch = false;
        foreach (var tab in workspace.Tabs.Where(x => x.Kind != PipelineKind.Splash))
            Visit(tab.Steps);
        if (foundCatch) legacyTab.Steps.Clear();
        profile.Enabled = false; // Splash is never part of the global listener.

        void Visit(IEnumerable<StepNode> nodes)
        {
            foreach (var node in nodes)
            {
                var scoped = node.Type == "splashListener"
                    || (node.Type == "waitForSound"
                        && PropEx.GetString(node.Props, "responseRoute", "inline") == "splash");
                if (scoped)
                {
                    foundCatch = true;
                    var wasMarker = node.Type == "splashListener";
                    node.Type = "waitForSound";
                    if (wasMarker)
                    {
                        node.Props = new Dictionary<string, object?>();
                    }
                    SetDefault("title", "Catch / Splash");
                    SetDefault("calibrationId", 2);
                    SetDefault("threshold", Math.Max(1, profile.PeakMin));
                    SetDefault("peakMin", profile.PeakMin);
                    SetDefault("peakMax", profile.PeakMax);
                    SetDefault("soundPriority", profile.Priority);
                    SetDefault("minDurationMs", profile.MinDurationMs);
                    SetDefault("cooldownMs", profile.CooldownMs);
                    SetDefault("timeoutMs", Math.Clamp(
                        PropEx.GetInt(node.Props, "timeoutMaxSec", profile.TimeoutMaxSec), 1, 300) * 1000);
                    SetDefault("responseRoute", "splash");
                    SetDefault("timeoutMinSec", Math.Clamp(profile.TimeoutMinSec, 1, 300));
                    SetDefault("timeoutMaxSec", Math.Clamp(profile.TimeoutMaxSec, 1, 300));
                    SetDefault("onTimeout", "continue");
                    SetDefault("insertIfElse", false);
                    SetDefault("armed", false);
                    if (node.Children.Count == 0 && responseSnapshot.Length > 0)
                        foreach (var child in StepTreeSerializer.Restore(responseSnapshot))
                        {
                            DocumentService.FixParents(child, node);
                            node.Children.Add(child);
                        }

                    void SetDefault(string key, object? value)
                    {
                        if (!node.Props.ContainsKey(key)) node.Props[key] = value;
                    }
                }
                Visit(node.Children);
            }
        }
    }

    private static PipelineKind? Map(string name)
    {
        return name switch
        {
            "Desktop" => PipelineKind.Desktop,
            "Restart" or "After" => PipelineKind.Restart,
            "Startup" => PipelineKind.Startup,
            "LoginOrDc" or "Login" => PipelineKind.LoginOrDc,
            "Dc" or "DC" => PipelineKind.Dc,
            "CharacterDashboard" => PipelineKind.CharacterDashboard,
            "EnteringGameLoading" => PipelineKind.EnteringGameLoading,
            "Game" => PipelineKind.Game,
            "Targeted" => PipelineKind.Targeted,
            "Whisper" => PipelineKind.Whisper,
            "WhisperRepeat" => PipelineKind.WhisperRepeat,
            "Splash" => PipelineKind.Splash,
            // The Resumable tab was retired; its old data is intentionally ignored.
            "Resumable" => null,
            // v1 five-tab names
            "Launch" => PipelineKind.Restart,
            "Main" => PipelineKind.Desktop,
            "LaunchRecovery" or "MainRecovery" => PipelineKind.Dc,
            "ResumeEssentials" => null,
            _ => null,
        };
    }

    private static SoundWatchProfile CloneSoundProfile(SoundWatchProfile source)
    {
        var clone = new SoundWatchProfile();
        CopySoundProfile(source, clone);
        return clone;
    }

    private static void CopySoundProfile(SoundWatchProfile source, SoundWatchProfile target)
    {
        target.Id = source.Id;
        target.Name = source.Name;
        target.Enabled = source.Enabled;
        target.PeakMin = source.PeakMin;
        target.PeakMax = source.PeakMax;
        target.Priority = source.Priority;
        target.MinDurationMs = source.MinDurationMs;
        target.ListenWindowMs = source.ListenWindowMs;
        target.CooldownMs = source.CooldownMs;
        target.TimeoutMinSec = source.TimeoutMinSec;
        target.TimeoutMaxSec = source.TimeoutMaxSec;
        target.ResponseTab = source.ResponseTab;
    }
}
