using System.Security.Cryptography;
using System.Text;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>
/// Exports the current split-memory Pico Guard bundle verbatim.
/// This is deliberately separate from AutoCycleFirmwareBundle, which remains
/// the Golden-100 legacy exporter and is covered by the golden contract.
/// </summary>
public static class ModernAutoCycleFirmwareBundle
{
    private static readonly string[] Files =
    {
        "README-FLASH.md", "SHA256SUMS.txt", "autocycle.amsj", "boot.py",
        "boot_out.txt", "character_dashboard_steps.txt", "code.py",
        "combined_guard_runtime.py", "desktop_steps.txt",
        "entering_game_loading_steps.txt", "error_policy.py", "game_steps.txt",
        "guard-calibration.json", "guard-transition.json",
        "guard_calibration_protocol.py", "guard_transition.py",
        "live_light_guard.py", "login_or_dc_steps.txt", "pico-calibration.json",
        "plan.txt", "plan_engine.py", "plan_engine_exec.py",
        "plan_engine_human.py", "plan_engine_login.py", "plan_engine_parallel.py",
        "plan_engine_parse.py", "restart_steps.txt",
        "resumable_steps.txt", "settings.toml", "targeted_steps.txt",
    };

    public static IReadOnlyList<string> Export(string codePyPath)
    {
        var stagingDir = Path.GetDirectoryName(Path.GetFullPath(codePyPath))
            ?? throw new IOException("مسیر خروجی firmware نامعتبر است.");
        var runtimeDir = Path.Combine(AppContext.BaseDirectory, "portable-modern-runtime");

        var sourceManifest = Path.Combine(runtimeDir, "SHA256SUMS.txt");
        if (!File.Exists(sourceManifest))
            throw new IOException("Manifest Bundle مدرن پیدا نشد.");
        var manifestNames = ReadManifestNames(sourceManifest);
        if (manifestNames.Length != 26)
            throw new IOException("تعداد فایل‌های Manifest Bundle مدرن نامعتبر است.");
        foreach (var name in Files.Concat(manifestNames).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(runtimeDir, name)))
                throw new IOException("فایل Bundle مدرن پیدا نشد: " + name);
        }

        // Remove only known legacy/experimental runtime leftovers. Do not touch
        // unrelated user files on CIRCUITPY.
        var stale = new[]
        {
            "guard_main.py", "guard_validate.py", ".guard_verified_v3",
            "plan_cycle.py", "cycle_runtime.py", "restart_windows.py",
            "auto_resume_boot.py", "resume_essentials_runtime.py",
            "recovery_runtime.py",
        };
        foreach (var name in stale)
        {
            var path = Path.Combine(stagingDir, name);
            if (File.Exists(path)) File.Delete(path);
        }
        var pycache = Path.Combine(stagingDir, "__pycache__");
        if (Directory.Exists(pycache)) Directory.Delete(pycache, true);

        foreach (var name in Files)
            File.Copy(Path.Combine(runtimeDir, name), Path.Combine(stagingDir, name), true);

        RebuildManifest(stagingDir, manifestNames);
        return Files.Select(name => Path.Combine(stagingDir, name)).ToArray();
    }

    /// <summary>
    /// Builds the executable modern runtime, then replaces every authorable plan/route and
    /// the embedded project snapshot with the workspace currently open in Classroom Studio.
    /// Runtime templates are never allowed to leak their sample project into a user export.
    /// </summary>
    public static IReadOnlyList<string> ExportCurrentProject(
        string codePyPath, PipelineWorkspace workspace, AppSettings settings,
        int screenW, int screenH, string sourceName, string machine)
    {
        var files = Export(codePyPath);
        var stagingDir = Path.GetDirectoryName(Path.GetFullPath(codePyPath))
            ?? throw new IOException("مسیر خروجی firmware نامعتبر است.");

        PipelinePlanBundle.Export(Path.Combine(stagingDir, "plan.txt"), workspace, settings,
            screenW, screenH, sourceName, machine);

        // PipelinePlanBundle intentionally uses the legacy PlanExporter, which also writes
        // its generated monolithic plan_engine.py next to the plans. That file must never
        // replace the modern split-memory facade copied by Export(): doing so imports a
        // ~30 KB source module on Pico and defeats the deferred parse/human/exec modules.
        // Restore the small facade after all authorable routes have been generated.
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "portable-modern-runtime", "plan_engine.py"),
            Path.Combine(stagingDir, "plan_engine.py"),
            true);

        File.WriteAllText(Path.Combine(stagingDir, "autocycle.amsj"),
            PipelineWorkspaceSerializer.Serialize(workspace), new UTF8Encoding(false));

        var manifestNames = ReadManifestNames(
            Path.Combine(AppContext.BaseDirectory, "portable-modern-runtime", "SHA256SUMS.txt"));
        RebuildManifest(stagingDir, manifestNames);
        return files;
    }

    private static string[] ReadManifestNames(string sourceManifest)
        => File.ReadAllLines(sourceManifest)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split(new[] { "  " }, StringSplitOptions.None))
            .Where(parts => parts.Length == 2)
            .Select(parts => parts[1].Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void RebuildManifest(string stagingDir, IReadOnlyList<string> manifestNames)
    {
        // Rebuild from the final bytes after the current project has replaced template routes.
        // Windows checkout may also normalize README/text line endings.
        var rebuiltManifest = new List<string>(manifestNames.Count);
        foreach (var name in manifestNames)
        {
            var path = Path.Combine(stagingDir, name);
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            rebuiltManifest.Add(actual + "  " + name);
        }
        File.WriteAllText(Path.Combine(stagingDir, "SHA256SUMS.txt"),
            string.Join(Environment.NewLine, rebuiltManifest) + Environment.NewLine);
    }
}
