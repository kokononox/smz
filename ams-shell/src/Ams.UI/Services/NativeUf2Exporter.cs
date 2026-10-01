using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>
/// Compiles the current pipeline workspace to ABP1 and injects it into a fixed-slot
/// native ABVM UF2 template. The operation is entirely local; it does not require
/// Pico SDK, GCC, GitHub, or a network connection.
/// </summary>
public static class NativeUf2Exporter
{
    public sealed record ExportResult(long ProgramBytes, string ProgramSha256, string CompilerSummary,
        string PatcherSummary);

    public static async Task<ExportResult> ExportAsync(string templateUf2, string outputUf2,
        PipelineWorkspace workspace, AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(templateUf2))
            throw new FileNotFoundException("فایل UF2 پایه پیدا نشد.", templateUf2);
        if (!string.Equals(Path.GetExtension(outputUf2), ".uf2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("نام فایل خروجی باید پسوند .uf2 داشته باشد.");
        if (!workspace.HumanMouseProfile.IsValid)
            throw new InvalidDataException(
                "Native Export به پروفایل سراسری دست نیاز دارد. ابتدا دکمهٔ "
                + "«ساخت پروفایل دست — ۳۰ ثانیه» را اجرا و پروژه را ذخیره کن.");

        var compiler = FindTool("abvm.py");
        var patcher = FindTool("abvm_uf2.py");
        var staging = Path.Combine(Path.GetTempPath(), "ClassroomStudio-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var source = Path.Combine(staging, "project.amsj");
            var program = Path.Combine(staging, "program.abp");
            var patched = Path.Combine(staging, "project.uf2");
            var root = JsonNode.Parse(PipelineWorkspaceSerializer.Serialize(workspace))?.AsObject()
                ?? throw new InvalidDataException("ساختار پروژه برای Native Guard معتبر نیست.");
            root["nativeGuard"] = BuildNativeGuard();
            root["nativeCycle"] = BuildNativeCycle(settings);
            await File.WriteAllTextAsync(source, root.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false), cancellationToken);

            var compilerSummary = await RunPythonAsync(compiler,
                ["compile", source, program, "--routes",
                 "Desktop", "Restart", "Startup", "LoginOrDc", "Dc",
                 "CharacterDashboard", "EnteringGameLoading", "Game", "Targeted",
                 "Whisper", "WhisperRepeat"],
                cancellationToken);
            if (!File.Exists(program) || new FileInfo(program).Length == 0)
                throw new InvalidDataException("کامپایلر ABP فایل program.abp را نساخت.");

            var patcherSummary = await RunPythonAsync(patcher,
                [templateUf2, program, patched], cancellationToken);
            if (!File.Exists(patched) || new FileInfo(patched).Length == 0)
                throw new InvalidDataException("Patcher فایل UF2 نهایی را نساخت.");

            var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputUf2));
            if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);
            File.Copy(patched, outputUf2, true);

            var bytes = await File.ReadAllBytesAsync(program, cancellationToken);
            return new ExportResult(bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                compilerSummary, patcherSummary);
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { }
        }
    }

    private static JsonObject BuildNativeGuard()
    {
        var profiles = new JsonArray();
        foreach (var profile in LightStateProfileStore.Load())
            profiles.Add(new JsonObject
            {
                ["id"] = profile.Id,
                ["enabled"] = profile.Enabled,
                ["luxCenter"] = profile.LuxCenter,
                ["luxTolerance"] = profile.LuxTolerance,
                ["stableDurationMs"] = profile.StableDurationMs,
                ["hysteresisLux"] = profile.HysteresisLux,
                ["lightCooldownMs"] = profile.LightCooldownMs,
                ["calibrationCue"] = profile.CalibrationCue,
                ["calibrationCuePattern"] = profile.CalibrationCuePattern,
                ["calibrationCueVolume"] = profile.CalibrationCueVolume,
                ["calibrationCueEnvelope"] = profile.CalibrationCueEnvelope,
                ["calibrationCueTempo"] = profile.CalibrationCueTempo,
            });
        var buzzerCues = new JsonArray();
        foreach (var cue in BuzzerSystemCueStore.Load())
        {
            var preset = CalibrationCueCatalog.Get(Math.Max(1, cue.CalibrationCue));
            buzzerCues.Add(new JsonObject
            {
                ["id"] = cue.NumericId,
                ["pattern"] = cue.CalibrationCue == 0 ? cue.CalibrationCuePattern : preset.Pattern,
                ["volume"] = cue.CalibrationCue == 0 ? cue.CalibrationCueVolume : preset.Volume,
                ["envelope"] = cue.CalibrationCue == 0 ? cue.CalibrationCueEnvelope : preset.Envelope,
                ["tempo"] = cue.CalibrationCue == 0 ? cue.CalibrationCueTempo : preset.Tempo,
            });
        }
        return new JsonObject
        {
            ["enabled"] = true,
            ["sampleMode"] = "hires",
            ["sensorTimeoutMs"] = 1500,
            ["stageWatchdogMinutes"] = 2,
            ["profiles"] = profiles,
            ["buzzerCues"] = buzzerCues,
        };
    }

    private static JsonObject BuildNativeCycle(AppSettings settings)
    {
        settings.NormalizeAutoCycleSettings();
        var (restartMin, restartMax) = AppSettings.NormalizeMinuteRange(
            settings.RestartMinMinutes, settings.RestartMaxMinutes, 110, 130);
        return new JsonObject
        {
            ["enabled"] = true,
            ["autoResume"] = settings.AutoResumeEnabled,
            ["runMinSeconds"] = restartMin * 60,
            ["runMaxSeconds"] = restartMax * 60,
            ["maxRestarts"] = 5,
            ["usbStableMs"] = 2000,
        };
    }

    internal static string FindTool(string name)
    {
        var root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var candidates = new List<string>
        {
            Path.Combine(root, "native-tools", name),
            Path.Combine(root, "tools", name),
        };
        var dir = new DirectoryInfo(root);
        for (var i = 0; i < 6 && dir?.Parent is not null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.Parent.FullName, "tools", name));
        return PortablePaths.FirstExistingFile(candidates)
            ?? throw new FileNotFoundException($"ابزار Native UF2 پیدا نشد: {name}");
    }

    private static async Task<string> RunPythonAsync(string script, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PortablePaths.FindPython(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.ArgumentList.Add(script);
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("اجرای ابزار Native UF2 آغاز نشد.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? $"ابزار Native UF2 با کد {process.ExitCode} متوقف شد."
                : stderr);
        return stdout;
    }
}
