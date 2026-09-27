using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        "plan.txt", "plan_engine.py", "plan_engine_exec.py", "plan_engine_game.py",
        "plan_engine_human.py", "plan_engine_login.py", "plan_engine_parallel.py",
        "plan_engine_parse.py", "restart_steps.txt",
        "resumable_steps.txt", "settings.toml", "sound_step_calibration.py",
        "targeted_steps.txt",
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
        if (manifestNames.Length != 28)
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
        IEnumerable<LightStateProfile> lightProfiles,
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

        // Build 73: the Status/Calibration profiles are the operator's source of truth.
        // Replace both fixed template contracts before hashing the final bundle.
        WriteLightProfiles(stagingDir, lightProfiles);

        var manifestNames = ReadManifestNames(
            Path.Combine(AppContext.BaseDirectory, "portable-modern-runtime", "SHA256SUMS.txt"));
        RebuildManifest(stagingDir, manifestNames);
        return files;
    }

    /// <summary>
    /// Reads the bytes back from the selected CIRCUITPY volume. A successful
    /// staging export is not sufficient: concurrent device-side FAT writes can
    /// acknowledge host copies while cross-linking unrelated files.
    /// </summary>
    public static void VerifyExportedTarget(string targetRoot)
    {
        var manifestPath = Path.Combine(targetRoot, "SHA256SUMS.txt");
        if (!File.Exists(manifestPath))
            throw new IOException("SHA256SUMS.txt روی CIRCUITPY پیدا نشد.");

        var entries = File.ReadAllLines(manifestPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split(new[] { "  " }, StringSplitOptions.None))
            .ToArray();
        if (entries.Length != 28 || entries.Any(parts => parts.Length != 2))
            throw new IOException("Manifest خوانده‌شده از CIRCUITPY نامعتبر است.");

        foreach (var parts in entries)
        {
            var expected = parts[0].Trim().ToLowerInvariant();
            var name = parts[1].Trim();
            if (name != Path.GetFileName(name))
                throw new IOException("نام فایل نامعتبر در Manifest: " + name);
            var path = Path.Combine(targetRoot, name);
            if (!File.Exists(path))
                throw new IOException("فایل خروجی روی CIRCUITPY پیدا نشد: " + name);
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected)))
                throw new IOException("Hash فایل خروجی روی CIRCUITPY ناهماهنگ است: " + name);
        }

        var calibration = JsonNode.Parse(
            File.ReadAllText(Path.Combine(targetRoot, "guard-calibration.json")))?.AsObject()
            ?? throw new IOException("guard-calibration.json روی CIRCUITPY نامعتبر است.");
        var transition = JsonNode.Parse(
            File.ReadAllText(Path.Combine(targetRoot, "guard-transition.json")))?.AsObject()
            ?? throw new IOException("guard-transition.json روی CIRCUITPY نامعتبر است.");
        var calibrationRevision = calibration["revision"]?.GetValue<string>();
        var transitionRevision = transition["calibrationRevision"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(calibrationRevision)
            || !string.Equals(calibrationRevision, transitionRevision, StringComparison.Ordinal))
            throw new IOException("Revision پروفایل‌های Guard روی CIRCUITPY ناهماهنگ است.");
        if (calibration["profiles"]?.AsObject().Count != RequiredLightProfileIds.Length
            || transition["profiles"]?.AsArray().Count != RequiredLightProfileIds.Length)
            throw new IOException("تعداد پروفایل‌های Guard روی CIRCUITPY نامعتبر است.");
    }


    private static readonly string[] RequiredLightProfileIds =
    {
        "desktop", "login-or-dc", "character-dashboard",
        "entering-game-loading", "game", "targeted",
    };

    private static void WriteLightProfiles(string stagingDir, IEnumerable<LightStateProfile> source)
    {
        var profiles = LightStateProfileStore.Normalize(source).Where(p => p.Enabled).ToArray();
        var byId = profiles.ToDictionary(p => p.Id, StringComparer.Ordinal);
        if (profiles.Length != RequiredLightProfileIds.Length
            || RequiredLightProfileIds.Any(id => !byId.ContainsKey(id)))
            throw new IOException("خروجی Pico به هر شش پروفایل نور فعال و معتبر نیاز دارد.");

        var revisionHash = ReadOnlyLightGateCoordinator.ComputeProfileRevision(profiles).ToLowerInvariant();
        var revision = "guard-" + revisionHash[..16];
        var calibrationProfiles = new JsonObject();
        foreach (var id in RequiredLightProfileIds)
        {
            var profile = byId[id];
            calibrationProfiles[id] = new JsonObject
            {
                ["center"] = profile.LuxCenter,
                ["tolerance"] = profile.LuxTolerance,
                ["stable_ms"] = profile.StableDurationMs,
            };
        }
        var calibration = new JsonObject
        {
            ["format"] = 1,
            ["revision"] = revision,
            ["profiles"] = calibrationProfiles,
        };

        var transitionPath = Path.Combine(stagingDir, "guard-transition.json");
        var transition = JsonNode.Parse(File.ReadAllText(transitionPath))?.AsObject()
            ?? throw new IOException("guard-transition.json نامعتبر است.");
        transition["calibrationRevision"] = revision;
        var transitionProfiles = transition["profiles"]?.AsArray()
            ?? throw new IOException("پروفایل‌های guard-transition.json نامعتبرند.");
        foreach (var node in transitionProfiles)
        {
            var item = node?.AsObject() ?? throw new IOException("پروفایل Guard نامعتبر است.");
            var id = item["id"]?.GetValue<string>() ?? "";
            if (!byId.TryGetValue(id, out var profile))
                throw new IOException("پروفایل Guard ناشناخته است: " + id);
            item["center"] = profile.LuxCenter;
            item["tolerance"] = profile.LuxTolerance;
            item["stableMs"] = profile.StableDurationMs;
        }

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(stagingDir, "guard-calibration.json"),
            calibration.ToJsonString(jsonOptions) + Environment.NewLine, new UTF8Encoding(false));
        File.WriteAllText(transitionPath,
            transition.ToJsonString(jsonOptions) + Environment.NewLine, new UTF8Encoding(false));
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
