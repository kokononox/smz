using System.Text.Json;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>Versioned persistence for the nine workflow tabs, including build-100 import.</summary>
public static class PipelineWorkspaceSerializer
{
    private sealed class Envelope
    {
        public string app { get; set; } = "AMS";
        public int pipelineVersion { get; set; }
        public Dictionary<string, List<StepNode>> pipelines { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Serialize(PipelineWorkspace workspace)
    {
        workspace.EnsureDcDefaults();
        var envelope = new Envelope { pipelineVersion = PipelineWorkspace.FormatVersion };
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
        if (version is not (1 or 2 or 3))
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
}
