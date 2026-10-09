using System.IO;
using System.Windows;
using Ams.UI.Services;
using CommunityToolkit.Mvvm.Input;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task ExportNativeUf2()
    {
        var templateDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Native ABVM UF2 template (*.uf2)|*.uf2",
            Title = "UF2 پایهٔ هویت برد را انتخاب کن",
            CheckFileExists = true,
        };
        var bundledTemplates = Path.Combine(AppContext.BaseDirectory, "native-runtime");
        if (Directory.Exists(bundledTemplates)) templateDialog.InitialDirectory = bundledTemplates;
        if (templateDialog.ShowDialog() != true) return;

        var projectName = string.IsNullOrWhiteSpace(_currentFile)
            ? "ClassroomProject"
            : Path.GetFileNameWithoutExtension(_currentFile);
        var identity = Path.GetFileNameWithoutExtension(templateDialog.FileName);
        var outputDialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Raspberry Pi Pico firmware (*.uf2)|*.uf2",
            FileName = $"{projectName}-{identity}.uf2",
            Title = "ذخیرهٔ Native UF2 پروژهٔ فعلی",
            AddExtension = true,
            DefaultExt = ".uf2",
        };
        if (outputDialog.ShowDialog() != true) return;

        try
        {
            var workspace = CapturePipelineWorkspaceForExport();
            var result = await NativeUf2Exporter.ExportAsync(
                templateDialog.FileName, outputDialog.FileName, workspace, _settings);
            Log($"native UF2 export: {Path.GetFileName(outputDialog.FileName)}; "
                + $"program={result.ProgramBytes} bytes; sha256={result.ProgramSha256}");
            if (!string.IsNullOrWhiteSpace(result.CompilerSummary))
                Log("native ABP compiler: " + result.CompilerSummary);
            if (!string.IsNullOrWhiteSpace(result.PatcherSummary))
                Log("native UF2 patcher: " + result.PatcherSummary);
            MessageBox.Show(
                $"Native UF2 پروژهٔ فعلی ساخته شد.\n\n{outputDialog.FileName}\n\n"
                + $"ABP: {result.ProgramBytes:N0} bytes\nSHA-256: {result.ProgramSha256}",
                "Export Native UF2", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("native UF2 export failed: " + ex.Message);
            MessageBox.Show("ساخت Native UF2 ناموفق بود:\n" + ex.Message,
                "Export Native UF2", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
