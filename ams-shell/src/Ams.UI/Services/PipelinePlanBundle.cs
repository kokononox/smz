using System.Text;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>
/// Exports the Classroom Studio workflow tabs and keeps the root automatic-cycle contract.
/// The dedicated DC route is emitted as dc_steps.txt and is selected by GuardTransition when
/// the Login/DC light is observed after stage 2.
/// </summary>
public static class PipelinePlanBundle
{
    private const string LegacyLaunchFile = "launch_steps.txt";
    private static readonly PipelineKind LegacyLaunchKind = PipelineKind.Launch;
    private static readonly PipelineKind LegacyMainKind = PipelineKind.Main;
    // Legacy validation wording: «فقط در تب Main مجاز است» و «فقط در تب Launch مجاز است».
    // Legacy compatibility names intentionally remain visible to the exporter contract:
    // PipelineKind.Launch maps to Restart and launch_steps.txt is still emitted by the
    // root AutoCyclePlanBundle alongside the new nine route files.
    private const string MainSentinel = "__PIPELINE_CALL_MAIN_DC_RECOVERY__";
    private const string LaunchSentinel = "__PIPELINE_CALL_LAUNCH_DC_RECOVERY__";

    public static IReadOnlyList<string> Export(string planPath, PipelineWorkspace workspace,
        AppSettings settings, int screenW, int screenH, string sourceName, string machine)
    {
        ValidateRecoveryCalls(workspace);
        ValidateSoundCalibrationIds(workspace);
        workspace.EnsureDcDefaults();
        var desktop = NormalizeRecoveryCalls(workspace[PipelineKind.Desktop].Steps);
        var written = AutoCyclePlanBundle.Export(planPath, desktop, settings, screenW, screenH,
            sourceName + "#desktop", machine).ToList();
        var directory = Path.GetDirectoryName(Path.GetFullPath(planPath))
            ?? throw new IOException("مسیر خروجی Pipeline نامعتبر است.");

        var payloads = new List<(string Name, byte[] Bytes)>();
        foreach (var tab in workspace.Tabs)
        {
            var normalized = NormalizeRecoveryCalls(tab.Steps);
            var text = normalized.Count == 0
                ? "PLAN|2\n"
                : PlanExporter.CompileOnce(normalized, settings, screenW, screenH,
                    sourceName + "#" + tab.Kind, machine).Text;
            text = ExpandRecoveryCalls(text);
            if (tab.Kind != PipelineKind.Desktop && ContainsCycleDirective(text))
                throw new PlanExporter.PlanBlockedException(new[] { tab.FileName + ": directive چرخه فقط در plan.txt مجاز است." });
            payloads.Add((tab.FileName, new UTF8Encoding(false).GetBytes(text)));
        }

        // Preserve the two recovery filenames consumed by older portable bundles. They now
        // mirror the dedicated DC route and remain harmless compatibility aliases.
        // Keep the retired route file as an empty compatibility payload; it is no longer a tab.
        payloads.Add(("resumable_steps.txt", new UTF8Encoding(false).GetBytes("PLAN|2\n")));

        var dc = payloads.Single(x => x.Name == "dc_steps.txt").Bytes;
        payloads.Add(("launch_recovery.txt", dc));
        payloads.Add(("main_recovery.txt", dc));
        foreach (var payload in payloads)
        {
            var path = Path.Combine(directory, payload.Name);
            AtomicWrite(path, payload.Bytes);
            written.Add(path);
        }

        var recoverySource = Path.Combine(AppContext.BaseDirectory, "portable-runtime", "recovery_runtime.py");
        if (File.Exists(recoverySource))
        {
            var recoveryTarget = Path.Combine(directory, "recovery_runtime.py");
            AtomicWrite(recoveryTarget, File.ReadAllBytes(recoverySource));
            written.Add(recoveryTarget);
        }
        return written.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ValidateSoundCalibrationIds(PipelineWorkspace workspace)
    {
        var owners = new Dictionary<int, string>();
        var errors = new List<string>();
        foreach (var tab in workspace.Tabs)
            Visit(tab.Steps, tab.Title, owners, errors);
        if (errors.Count > 0) throw new PlanExporter.PlanBlockedException(errors);

        static void Visit(IEnumerable<StepNode> nodes, string tab,
            Dictionary<int, string> owners, List<string> errors)
        {
            foreach (var node in nodes)
            {
                if (!node.IsDisabled && node.Type == "waitForSound"
                    && !PropEx.GetBool(node.Props, "armed")
                    && !PropEx.GetBool(node.Props, "insertIfElse"))
                {
                    var id = PropEx.GetInt(node.Props, "calibrationId", 1);
                    var label = tab + " / " + (string.IsNullOrWhiteSpace(node.Name)
                        ? "Wait For Sound" : node.Name.Trim());
                    if (id is not (1 or 2))
                        errors.Add(label + ": شناسهٔ کالیبراسیون صدا باید ۱ یا ۲ باشد.");
                    else if (owners.TryGetValue(id, out var owner))
                        errors.Add(label + ": شناسهٔ کالیبراسیون صدای " + id
                            + " قبلاً به «" + owner + "» اختصاص یافته است.");
                    else
                        owners[id] = label;
                }
                Visit(node.Children, tab, owners, errors);
            }
        }
    }

    private static bool ContainsCycleDirective(string text)
        => text.Contains("RUNFOR|", StringComparison.Ordinal)
        || text.Contains("AUTORESUME|", StringComparison.Ordinal)
        || text.Contains("POSTLAUNCH|", StringComparison.Ordinal)
        || text.Contains("LAUNCH|", StringComparison.Ordinal);

    private static void ValidateRecoveryCalls(PipelineWorkspace workspace)
    {
        var errors = new List<string>();
        foreach (var tab in workspace.Tabs) Visit(tab.Steps, tab.Kind, tab.Title, errors);
        if (errors.Count > 0) throw new PlanExporter.PlanBlockedException(errors);

        static void Visit(IEnumerable<StepNode> nodes, PipelineKind kind, string title, List<string> errors)
        {
            foreach (var node in nodes)
            {
                if (node.Type == RecoveryCallStepDefinitions.CallMain && kind != PipelineKind.Desktop)
                    errors.Add(title + ": استپ Run/Call Main DC Recovery فقط در تب Main مجاز است (معادل Desktop).");
                if (node.Type == RecoveryCallStepDefinitions.CallLaunch && kind != PipelineKind.Restart)
                    errors.Add(title + ": استپ Run/Call Launch DC Recovery فقط در تب Launch مجاز است (معادل Restart).");
                Visit(node.Children, kind, title, errors);
            }
        }
    }

    private static List<StepNode> NormalizeRecoveryCalls(IEnumerable<StepNode> nodes)
    {
        var result = new List<StepNode>();
        foreach (var source in nodes)
        {
            var mapped = source.Type switch
            {
                RecoveryCallStepDefinitions.CallMain => MainSentinel,
                RecoveryCallStepDefinitions.CallLaunch => LaunchSentinel,
                _ => null,
            };
            var clone = new StepNode
            {
                Type = mapped is null ? source.Type : "comment",
                Name = source.Name,
                Delay = source.Delay,
                DelayMax = source.DelayMax,
                IsDisabled = source.IsDisabled,
                Props = mapped is null
                    ? new Dictionary<string, object?>(source.Props)
                    : new Dictionary<string, object?> { ["text"] = mapped },
            };
            foreach (var child in NormalizeRecoveryCalls(source.Children))
            {
                child.Parent = clone;
                clone.Children.Add(child);
            }
            result.Add(clone);
        }
        return result;
    }

    private static string ExpandRecoveryCalls(string text)
        => text.Replace("# " + MainSentinel, "INCLUDE|file=main_recovery.txt", StringComparison.Ordinal)
               .Replace("# " + LaunchSentinel, "INCLUDE|file=launch_recovery.txt", StringComparison.Ordinal);

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
