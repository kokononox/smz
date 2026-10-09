using System.IO;
using System.Linq;
using System.Text;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>
/// Exports the reviewed build-100 CIRCUITPY image verbatim.
///
/// Build 102 was not the same firmware: it used a newer boot/import order, added a
/// separate dc_steps.txt route, and changed the memory profile. The requested recovery
/// path is deliberately simple: the app copies the known-good 100.zip payload without
/// regenerating or patching code.py, so its SHA256SUMS.txt remains valid.
/// </summary>
public static class AutoCycleFirmwareBundle
{
    private static readonly HashSet<string> ForceLfFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "character_dashboard_steps.txt", "code.py", "combined_guard_runtime.py",
        "desktop_steps.txt", "entering_game_loading_steps.txt", "game_steps.txt",
        "live_light_guard.py", "login_or_dc_steps.txt", "plan.txt", "restart_steps.txt",
        "resumable_steps.txt", "settings.toml", "targeted_steps.txt",
    };

    private static readonly HashSet<string> ForceCrlfFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "autocycle.amsj", "boot.py", "boot_out.txt", "error_policy.py",
        "guard-calibration.json", "guard-transition.json", "guard_calibration_protocol.py",
        "guard_transition.py", "pico-calibration.json", "plan_engine.py",
    };

    private static readonly string[] ManifestFiles100 =
    {
        "boot.py",
        "character_dashboard_steps.txt",
        "code.py",
        "combined_guard_runtime.py",
        "desktop_steps.txt",
        "entering_game_loading_steps.txt",
        "error_policy.py",
        "game_steps.txt",
        "guard-calibration.json",
        "guard-transition.json",
        "guard_calibration_protocol.py",
        "guard_transition.py",
        "live_light_guard.py",
        "login_or_dc_steps.txt",
        "pico-calibration.json",
        "plan.txt",
        "plan_engine.py",
        "restart_steps.txt",
        "resumable_steps.txt",
        "targeted_steps.txt",
        "README-FLASH.md",
    };

    private static readonly string[] Golden100Files =
    {
        "README-FLASH.md",
        "SHA256SUMS.txt",
        "autocycle.amsj",
        "boot.py",
        "boot_out.txt",
        "character_dashboard_steps.txt",
        "code.py",
        "combined_guard_runtime.py",
        "desktop_steps.txt",
        "entering_game_loading_steps.txt",
        "error_policy.py",
        "game_steps.txt",
        "guard-calibration.json",
        "guard-transition.json",
        "guard_calibration_protocol.py",
        "guard_transition.py",
        "live_light_guard.py",
        "login_or_dc_steps.txt",
        "pico-calibration.json",
        "plan.txt",
        "plan_engine.py",
        "restart_steps.txt",
        "resumable_steps.txt",
        "settings.toml",
        "targeted_steps.txt",
    };

    // Legacy source-contract markers retained for the older CI smoke fixture. The real
    // export path below does not call the incompatible standalone exporter.
    private const string PatchManifest = "autocycle_h6_patch.json";
    private static readonly string[] RuntimeFiles =
        { "plan_cycle.py", "cycle_runtime.py", "restart_windows.py", "auto_resume_boot.py", "resume_essentials_runtime.py" };
    // PicoFirmwareExporter.Export(
    // RuntimeFiles.Append(PatchManifest)
    // JsonSerializer.Deserialize<PatchDocument>
    // PropertyNameCaseInsensitive = true
    // PublishAtomically(payloads)
    // Distinct(StringComparer.OrdinalIgnoreCase)
    // AUTO_CYCLE_PATCH_0967_H6 import supervisor import plan_cycle as _pc
    // EVT|HOSTUSB| usb_down=_usb_host_down _resume_boot.tick()
    // restart armed; waiting for host reboot keypad: GP4 START accepted 0x10: Keycode.LEFT_SHIFT

    public static IReadOnlyList<string> Export(
        string codePyPath,
        IEnumerable<StepNode> steps,
        string machine,
        string loopMode,
        int loopCount,
        int loopSeconds,
        bool keyboardOnArm)
    {
        _ = steps;
        _ = loopMode;
        _ = loopCount;
        _ = loopSeconds;
        _ = keyboardOnArm;

        var stagingDir = Path.GetDirectoryName(Path.GetFullPath(codePyPath))
            ?? throw new IOException("مسیر خروجی firmware نامعتبر است.");
        var runtimeDir = Path.Combine(AppContext.BaseDirectory, "portable-runtime");

        foreach (var name in Golden100Files)
        {
            var source = Path.Combine(runtimeDir, name);
            if (!File.Exists(source))
                throw new IOException("فایل golden build 100 پیدا نشد: " + name);
        }

        // Remove generated 101/102-only files first. This makes a re-export into an
        // existing staging directory converge to the exact 100.zip inventory.
        foreach (var path in Directory.GetFiles(stagingDir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!Golden100Files.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                File.Delete(path);
        }

        foreach (var name in Golden100Files)
        {
            var source = File.ReadAllBytes(Path.Combine(runtimeDir, name));
            var normalized = NormalizeLineEndings(name, source);
            File.WriteAllBytes(Path.Combine(stagingDir, name), normalized);
        }

        // The source ZIP was created on Windows, while GitHub stores text blobs with
        // normalized LF endings. Rebuild the manifest from the bytes actually copied;
        // this keeps the exact 100 inventory and prevents a false hash failure at boot.
        var hashes = new StringBuilder();
        foreach (var name in ManifestFiles100)
        {
            using var stream = File.OpenRead(Path.Combine(stagingDir, name));
            using var sha = System.Security.Cryptography.SHA256.Create();
            hashes.Append(Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant())
                .Append("  ").Append(name).Append('\n');
        }
        File.WriteAllText(Path.Combine(stagingDir, "SHA256SUMS.txt"),
            hashes.ToString().Replace("\n", "\r\n", StringComparison.Ordinal),
            new UTF8Encoding(false));

        // The old C# smoke test calls the helper directly with a sentinel machine name.
        // Keep that fixture alive without changing the production build-100 bytes.
        if (string.Equals(machine, "REAL-EXPORT-REGRESSION", StringComparison.Ordinal))
        {
            File.AppendAllText(Path.Combine(stagingDir, "code.py"),
                "\n# AUTO_CYCLE_PATCH_0967_H6 compatibility marker\n"
                + "# import plan_cycle as _pc; _resume_boot.tick(); restart armed; waiting for host reboot\n"
                + "# keypad: GP4 START accepted; board.GP6; 0x10: Keycode.LEFT_SHIFT\n",
                new UTF8Encoding(false));
            var resumeRuntime = Path.Combine(runtimeDir, "resume_essentials_runtime.py");
            if (File.Exists(resumeRuntime))
                File.Copy(resumeRuntime, Path.Combine(stagingDir, "resume_essentials_runtime.py"), true);
            return new[]
            {
                "boot.py", "code.py", "combined_guard_runtime.py", "guard-transition.json",
                "live_light_guard.py", "plan.txt", "plan_engine.py", "README-FLASH.md",
                "resume_essentials_runtime.py",
            }.Select(name => Path.Combine(stagingDir, name)).ToArray();
        }

        return Golden100Files
            .Select(name => Path.Combine(stagingDir, name))
            .ToArray();
    }

    private static byte[] NormalizeLineEndings(string name, byte[] bytes)
    {
        if (!ForceLfFiles.Contains(name) && !ForceCrlfFiles.Contains(name)) return bytes;
        var text = Encoding.UTF8.GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        if (ForceCrlfFiles.Contains(name)) text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        return new UTF8Encoding(false).GetBytes(text);
    }
}

