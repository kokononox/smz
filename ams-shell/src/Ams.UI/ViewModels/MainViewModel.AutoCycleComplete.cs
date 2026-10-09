using System.IO;
using System.Windows;
using Ams.UI.Services;
using CommunityToolkit.Mvvm.Input;
using Forms = System.Windows.Forms;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    // چرخهی خودکار / چرخه‌ی خودکار
    /// <summary>
    /// Publishes the reviewed Golden-100 Pico bundle to CIRCUITPY in a safe order.
    /// The bundle already contains plan.txt and all required route/runtime files;
    /// Modern AutoCycle runtime files remain outside this legacy debug path.
    /// </summary>
    [RelayCommand]
    private void ExportAutoCycleComplete()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "ریشهٔ درایو CIRCUITPY پیکو را انتخاب کن",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

        var targetRoot = dialog.SelectedPath;
        string? staging = null;
        try
        {
            staging = Path.Combine(Path.GetTempPath(), "ClassroomStudio-pico-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            // Golden-100 is the current physical-debug baseline. It owns plan.txt,
            // route files and the exact code.py; do not invoke PipelinePlanBundle here.
            var mode = _settings.PlayRepeatMode;
            var seconds = mode == "timed"
                ? (int)(_settings.PlayRepeatUnit switch
                {
                    "second" => TimeSpan.FromSeconds(Math.Max(1, _settings.PlayRepeatValue)),
                    "hour" => TimeSpan.FromHours(Math.Max(1, _settings.PlayRepeatValue)),
                    _ => TimeSpan.FromMinutes(Math.Max(1, _settings.PlayRepeatValue)),
                }).TotalSeconds
                : 0;
            AutoCycleFirmwareBundle.Export(Path.Combine(staging, "code.py"), Steps,
                Environment.MachineName, mode, Math.Max(1, _settings.PlayRepeatTimes), seconds, false);

            var files = Directory.GetFiles(staging, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path).Equals("code.py", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!files.Any(path => Path.GetFileName(path).Equals("plan.txt", StringComparison.OrdinalIgnoreCase))
                || !files.Any(path => Path.GetFileName(path).Equals("code.py", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("خروجی کامل شامل plan.txt و code.py نیست.");

            Directory.CreateDirectory(targetRoot);
            foreach (var source in files)
            {
                var destination = Path.Combine(targetRoot, Path.GetFileName(source));
                File.Copy(source, destination, true);
                Log("pico complete export: " + Path.GetFileName(source));
            }
            MessageBox.Show(
                $"بستهٔ کامل Golden 100 ساخته و روی CIRCUITPY کپی شد ({files.Length} فایل).\n"
                + "code.py عمداً آخرین فایل کپی شد تا برد قبل از کامل‌شدن Bundle اجرا نشود.",
                "Complete Pico export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("pico complete export failed: " + ex.Message);
            MessageBox.Show("خروجی کامل Pico ناموفق بود: " + ex.Message,
                "Complete Pico export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (staging is not null)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch { /* best-effort cleanup of temporary staging */ }
            }
        }
    }
}
