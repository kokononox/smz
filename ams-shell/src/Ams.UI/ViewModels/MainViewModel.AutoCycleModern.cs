using System.IO;
using System.Windows;
using Ams.UI.Services;
using CommunityToolkit.Mvvm.Input;
using Forms = System.Windows.Forms;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    /// <summary>Copies the split-memory modern bundle; Golden 100 remains a separate export.</summary>
    [RelayCommand]
    private async Task ExportAutoCycleModern()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "ریشهٔ درایو CIRCUITPY پیکو را برای Bundle مدرن انتخاب کن",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

        var targetRoot = dialog.SelectedPath;
        string? staging = null;
        try
        {
            if (IsLightWatchRunning)
            {
                await StopLightWatchAsync();
                Log("light watch stopped — Pico export restarts the USB serial connection");
            }
            if (_bridge is not null && _bridge.State == BridgeState.Connected)
            {
                try
                {
                    await _bridge.SendAsync("HALT|SILENT", 3);
                    Log("Pico runtime quiesced before CIRCUITPY export");
                }
                catch (Exception ex)
                {
                    Log("Pico quiesce warning: " + ex.Message);
                }
                await _bridge.DisconnectAsync();
            }
            staging = Path.Combine(Path.GetTempPath(), "ClassroomStudio-modern-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var workspace = CapturePipelineWorkspaceForExport();
            var files = ModernAutoCycleFirmwareBundle.ExportCurrentProject(
                    Path.Combine(staging, "code.py"), workspace, _settings, LightStateProfiles,
                    (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight,
                    _currentFile ?? "untitled", Environment.MachineName)
                .OrderBy(path => Path.GetFileName(path).Equals("code.py", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Directory.CreateDirectory(targetRoot);
            foreach (var source in files)
            {
                var destination = Path.Combine(targetRoot, Path.GetFileName(source));
                File.Copy(source, destination, true);
                Log("pico current-project export: " + Path.GetFileName(source));
            }

            Exception? verifyError = null;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                await Task.Delay(attempt == 1 ? 750 : 500);
                try
                {
                    ModernAutoCycleFirmwareBundle.VerifyExportedTarget(targetRoot);
                    verifyError = null;
                    Log("pico current-project verification: 37/37 hashes and Guard revisions OK");
                    break;
                }
                catch (Exception ex)
                {
                    verifyError = ex;
                    Log($"pico verification attempt {attempt}/5 failed: {ex.Message}");
                }
            }
            if (verifyError is not null)
                throw new IOException(
                    "خروجی CIRCUITPY پس از کپی معتبر نیست. Pico را Reset و فایل‌سیستم را بررسی کنید.",
                    verifyError);

            MessageBox.Show(
                $"پروژهٔ باز فعلی همراه Bundle مدرن روی CIRCUITPY کپی شد ({files.Length} فایل).\n"
                + "تمام Routeها، پروفایل‌ها و ۳۴ Hash مستقیماً از CIRCUITPY بازخوانی و تأیید شدند.",
                "Current project Pico export", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (PlanExporter.PlanBlockedException bx)
        {
            Log($"pico current-project export blocked ({bx.Errors.Count} problem(s)):");
            foreach (var error in bx.Errors) Log("  x " + error);
            var details = string.Join("\n", bx.Errors.Take(6).Select(error => "• " + error));
            MessageBox.Show(
                $"خروجی پروژهٔ فعلی به‌علت {bx.Errors.Count} خطای قابل‌اصلاح متوقف شد:\n\n{details}",
                "Current project Pico export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log("pico current-project export failed: " + ex.Message);
            MessageBox.Show("خروجی پروژهٔ فعلی ناموفق بود: " + ex.Message,
                "Current project Pico export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (staging is not null)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch { }
            }
        }
    }
}
