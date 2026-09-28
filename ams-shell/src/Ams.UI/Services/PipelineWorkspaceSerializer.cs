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
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Serialize(PipelineWorkspace workspace)
    {
        workspace.EnsureDcDefaults();
        var envelope = new Envelope
        {
            pipelineVersion = PipelineWorkspace.FormatVersion,
            soundProfiles = workspace.SoundProfiles.Select(CloneSoundProfile).ToList(),
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
        if (version is not (1 or 2 or 3 or 4))
            throw new InvalidDataException("Unsupported AMS pipeline document.");

        var workspace = new PipelineWorkspace();
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
                if (target is null || source.ResponseTab is not (PipelineKind.Whisper or PipelineKind.Splash))
                    continue;
                CopySoundProfile(source, target);
            }
        }
        // Build 100 predates the dedicated DC tab. Give it the safe ESC route on import.
        // An explicitly empty DC tab receives the same default so a blank tab never disables
        // popup dismissal by accident.
        if (!hasDc || workspace[PipelineKind.Dc].Steps.Count == 0)
            workspace.EnsureDcDefaults();
        return workspace;
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
        target.ResponseTab = source.ResponseTab;
    }
}
