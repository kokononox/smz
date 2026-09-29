using System.Text;
using System.Security.Cryptography;
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
        // Modern AutoCycle remains route-driven, but the root RUNFOR range is
        // still the authoritative bound for the complete active cycle.
        // Resume/PostLaunch stay retired; only the cycle window survives.
        File.WriteAllText(planPath, StripRetiredCycleHeaders(File.ReadAllText(planPath)),
            new UTF8Encoding(false));
        var directory = Path.GetDirectoryName(Path.GetFullPath(planPath))
            ?? throw new IOException("مسیر خروجی Pipeline نامعتبر است.");

        var catchWaits = FindCatchWaits(workspace[PipelineKind.Game].Steps).ToList();
        if (catchWaits.Count > 1)
            throw new PlanExporter.PlanBlockedException(new[]
            {
                "Game: در این نسخه فقط یک Wait For Sound از نوع Catch/Splash مجاز است."
            });
        var catchWait = catchWaits.SingleOrDefault();
        var payloads = new List<(string Name, byte[] Bytes)>();
        foreach (var tab in workspace.Tabs)
        {
            var sourceSteps = tab.Kind == PipelineKind.Splash && catchWait is not null
                ? catchWait.Children : tab.Steps;
            var normalized = NormalizeRecoveryCalls(sourceSteps);
            var text = normalized.Count == 0
                ? "PLAN|2\n"
                : PlanExporter.CompileOnce(normalized, settings, screenW, screenH,
                    sourceName + "#" + tab.Kind, machine).Text;
            text = ExpandRecoveryCalls(text);
            if (tab.Kind == PipelineKind.Game)
                text = AddGameSoundWatch(text, workspace.SoundProfiles, workspace, catchWait);
            else if (text.Split('\n').Any(x => x.StartsWith("WPROFILE|splash,", StringComparison.Ordinal)))
                throw new PlanExporter.PlanBlockedException(new[] { tab.Title + ": Wait For Sound نوع Catch فقط در تب Game مجاز است." });
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
        foreach (var profile in workspace.SoundProfiles.Where(x => x.Enabled))
        {
            var label = "Game sound profile / " + profile.Name;
            if (profile.Id is not (1 or 2))
                errors.Add(label + ": شناسهٔ کالیبراسیون صدا باید ۱ یا ۲ باشد.");
            else if (!owners.TryAdd(profile.Id, label))
                errors.Add(label + ": شناسهٔ کالیبراسیون صدای " + profile.Id + " تکراری است.");
        }
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

    private static string AddGameSoundWatch(string text, IEnumerable<SoundWatchProfile> profiles,
        PipelineWorkspace workspace, StepNode? catchWait)
    {
        var enabled = profiles.Where(x => x.Enabled && x.ResponseTab == PipelineKind.Whisper)
            .OrderBy(x => x.Id).ToList();
        var errors = new List<string>();
        foreach (var profile in enabled)
        {
            if (profile.Id is not (1 or 2)) errors.Add(profile.Name + ": Sound profile ID must be 1 or 2.");
            if (profile.PeakMin < 0 || profile.PeakMax is < 1 or > 511 || profile.PeakMin > profile.PeakMax)
                errors.Add(profile.Name + ": بازهٔ Peak باید 0 <= Min <= Max <= 511 باشد.");
            if (profile.Priority is < -100 or > 100) errors.Add(profile.Name + ": Priority باید بین -100 و 100 باشد.");
            if (profile.MinDurationMs is < 10 or > 5000) errors.Add(profile.Name + ": Min duration باید بین 10 و 5000 ms باشد.");
            if (profile.CooldownMs is < 0 or > 60000) errors.Add(profile.Name + ": Cooldown باید بین 0 و 60000 ms باشد.");
            var responseTab = workspace[profile.ResponseTab];
            if (responseTab.Steps.Count == 0)
                errors.Add(profile.Name + ": تب واکنش " + responseTab.Title + " خالی است.");
            ValidateSoundResponse(responseTab.Steps, responseTab.Title, errors);
        }
        if (catchWait is not null)
        {
            var p = catchWait.Props;
            var id = PropEx.GetInt(p, "calibrationId", 2);
            var peakMin = PropEx.GetInt(p, "peakMin", 0);
            var peakMax = PropEx.GetInt(p, "peakMax", 511);
            var minimum = PropEx.GetInt(p, "minDurationMs", 60);
            var priority = PropEx.GetInt(p, "soundPriority", 5);
            var cooldown = PropEx.GetInt(p, "cooldownMs", 900);
            var lo = PropEx.GetInt(p, "timeoutMinSec", 18);
            var hi = PropEx.GetInt(p, "timeoutMaxSec", 22);
            if (id != 2) errors.Add("Catch Wait For Sound: شناسهٔ صدای Catch باید ID 2 باشد.");
            if (peakMin < 0 || peakMax is < 1 or > 511 || peakMin > peakMax)
                errors.Add("Catch Wait For Sound: بازهٔ Peak نامعتبر است.");
            if (minimum is < 10 or > 5000) errors.Add("Catch Wait For Sound: Min duration نامعتبر است.");
            if (priority is < -100 or > 100) errors.Add("Catch Wait For Sound: Priority نامعتبر است.");
            if (cooldown is < 0 or > 60000) errors.Add("Catch Wait For Sound: Cooldown نامعتبر است.");
            if (lo < 1 || hi > 300 || lo > hi) errors.Add("Catch Wait For Sound: Timeout باید بازهٔ مرتب ۱ تا ۳۰۰ ثانیه باشد.");
            if (catchWait.Children.Count == 0)
                errors.Add("Catch Wait For Sound: پاسخ تشخیص صدا را به‌صورت Child داخل همین استپ قرار دهید.");
            ValidateSoundResponse(catchWait.Children, "Catch Wait For Sound", errors);
        }
        if (errors.Count > 0) throw new PlanExporter.PlanBlockedException(errors);
        if (enabled.Count == 0 && catchWait is null) return text;

        var payloads = enabled.Select(profile => string.Join(",",
            "whisper",
            profile.PeakMin, profile.PeakMax, profile.MinDurationMs, profile.Priority,
            profile.CooldownMs, workspace[profile.ResponseTab].FileName, "global")).ToList();
        if (catchWait is not null)
        {
            var p = catchWait.Props;
            payloads.Add(string.Join(",",
                "splash",
                PropEx.GetInt(p, "peakMin", 0),
                PropEx.GetInt(p, "peakMax", 511),
                PropEx.GetInt(p, "minDurationMs", 60),
                PropEx.GetInt(p, "soundPriority", 5),
                PropEx.GetInt(p, "cooldownMs", 900),
                "splash_steps.txt", "scoped"));
        }
        var payload = string.Join(";", payloads);
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var firstNewline = normalized.IndexOf('\n');
        if (!normalized.StartsWith("PLAN|2", StringComparison.Ordinal) || firstNewline < 0)
            throw new PlanExporter.PlanBlockedException(new[] { "game_steps.txt: هدر PLAN|2 نامعتبر است." });
        return normalized.Insert(firstNewline + 1, "SOUNDWATCH|" + payload + "\n");
    }

    private static IEnumerable<StepNode> FindCatchWaits(IEnumerable<StepNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsDisabled) continue;
            if (node.Type == "waitForSound"
                && PropEx.GetString(node.Props, "responseRoute", "inline") == "splash")
                yield return node;
            foreach (var child in FindCatchWaits(node.Children))
                yield return child;
        }
    }

    private static void ValidateSoundResponse(IEnumerable<StepNode> nodes, string tab, List<string> errors)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "delay", "keystroke", "typeText", "keyDown", "keyUp", "mouseScroll", "rawCommand",
            "buzzer", "comment", "forLoop", "randomPackage",
        };
        foreach (var node in nodes)
        {
            if (!node.IsDisabled && !allowed.Contains(node.Type) && !node.Type.StartsWith("__", StringComparison.Ordinal))
                errors.Add(tab + ": استپ «" + node.Type + "» هنوز برای واکنش کم‌حافظهٔ صدا پشتیبانی نمی‌شود.");
            ValidateSoundResponse(node.Children, tab, errors);
        }
    }

    private static bool ContainsCycleDirective(string text)
        => text.Contains("RUNFOR|", StringComparison.Ordinal)
        || text.Contains("AUTORESUME|", StringComparison.Ordinal)
        || text.Contains("POSTLAUNCH|", StringComparison.Ordinal)
        || text.Contains("LAUNCH|", StringComparison.Ordinal);

    private static string StripRetiredCycleHeaders(string text)
        => string.Join("\n", text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Where(line => !line.StartsWith("AUTORESUME|", StringComparison.Ordinal)
                        && !line.StartsWith("POSTLAUNCH|", StringComparison.Ordinal)
                        && !line.StartsWith("LAUNCH|", StringComparison.Ordinal)));

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
