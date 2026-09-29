using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
        PipelineWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(templateUf2))
            throw new FileNotFoundException("فایل UF2 پایه پیدا نشد.", templateUf2);
        if (!string.Equals(Path.GetExtension(outputUf2), ".uf2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("نام فایل خروجی باید پسوند .uf2 داشته باشد.");

        var compiler = FindTool("abvm.py");
        var patcher = FindTool("abvm_uf2.py");
        var staging = Path.Combine(Path.GetTempPath(), "ClassroomStudio-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var source = Path.Combine(staging, "project.amsj");
            var program = Path.Combine(staging, "program.abp");
            var patched = Path.Combine(staging, "project.uf2");
            await File.WriteAllTextAsync(source, PipelineWorkspaceSerializer.Serialize(workspace),
                new UTF8Encoding(false), cancellationToken);

            var compilerSummary = await RunPythonAsync(compiler,
                ["compile", source, program, "--routes", "Game", "Whisper"], cancellationToken);
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
