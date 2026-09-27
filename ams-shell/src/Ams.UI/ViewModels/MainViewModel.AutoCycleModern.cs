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
            staging = Path.Combine(Path.GetTempPath(), "ClassroomStudio-modern-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var workspace = CapturePipelineWorkspaceForExport();
            var files = ModernAutoCycleFirmwareBundle.ExportCurrentProject(
                    Path.Combine(staging, "code.py"), workspace, _settings,
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
            MessageBox.Show(
                $"پروژهٔ باز فعلی همراه Bundle مدرن روی CIRCUITPY کپی شد ({files.Length} فایل).\n"
                + "تمام Routeها، plan.txt و autocycle.amsj از تب‌های همین پروژه ساخته شدند؛ code.py آخر کپی شد.",
                "Current project Pico export", MessageBoxButton.OK, MessageBoxImage.Information);
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
}using System.IO;
using System.Windows;
using Ams.UI.Services;
using CommunityToolkit.Mvvm.Input;
using Forms = System.Windows.Forms;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    /// <summary>Copies the split-memory modern bundle; Golden 100 remains a separate export.</summary>
    [RelayCommand]
    private void ExportAutoCycleModern()
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
            staging = Path.Combine(Path.GetTempPath(), "ClassroomStudio-modern-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var workspace = CapturePipelineWorkspaceForExport();
            var files = ModernAutoCycleFirmwareBundle.ExportCurrentProject(
                    Path.Combine(staging, "code.py"), workspace, _settings,
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
            MessageBox.Show(
                $"پروژهٔ باز فعلی همراه Bundle مدرن روی CIRCUITPY کپی شد ({files.Length} فایل).\n"
                + "تمام Routeها، plan.txt و autocycle.amsj از تب‌های همین پروژه ساخته شدند؛ code.py آخر کپی شد.",
                "Current project Pico export", MessageBoxButton.OK, MessageBoxImage.Information);
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
