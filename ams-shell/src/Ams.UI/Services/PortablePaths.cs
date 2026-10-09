using System.Collections.Generic;
using System.IO;

namespace Ams.UI.Services;

/// <summary>v0.9.57 — portable path resolution relative to the exe.
/// First existing directory/file wins; empty result = use settings or fail loudly.</summary>
public static class PortablePaths
{
    /// <summary>First existing directory from an ordered list of candidates.</summary>
    public static string? FirstExistingDir(IEnumerable<string> candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c)) continue;
            if (Directory.Exists(c)) return c;
        }
        return null;
    }

    /// <summary>First existing file from an ordered list of candidates.</summary>
    public static string? FirstExistingFile(IEnumerable<string> candidates)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c)) continue;
            if (File.Exists(c)) return c;
        }
        return null;
    }

    /// <summary>Find a Python interpreter: bundled exe → py launcher → PATH python.</summary>
    public static string FindPython()
    {
        var exeDir = AppContext.BaseDirectory;

        // 1) Bundled python.exe next to the exe (optional embeddable)
        var bundled = Path.Combine(exeDir, "python", "python.exe");
        if (File.Exists(bundled)) return bundled;

        // 2) py launcher (py -3)
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("py", "-3 -c import+sys,+print(sys.executable)")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                proc.WaitForExit(3000);
                if (proc.ExitCode == 0)
                {
                    var outp = proc.StandardOutput.ReadToEnd().Trim();
                    if (!string.IsNullOrEmpty(outp) && File.Exists(outp)) return outp;
                }
            }
        }
        catch { /* py launcher not available */ }

        // 3) python on PATH
        try
        {
            var psi2 = new System.Diagnostics.ProcessStartInfo("python", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc2 = System.Diagnostics.Process.Start(psi2);
            if (proc2 is not null)
            {
                proc2.WaitForExit(3000);
                if (proc2.ExitCode == 0) return "python";
            }
        }
        catch { /* python not on PATH */ }

        throw new System.InvalidOperationException(
            "پایتون روی این سیستم پیدا نشد — Python 3 را از python.org نصب کنید (تیک Add to PATH را بزنید).");
    }
}
