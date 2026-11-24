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
            var bundledClientUf2 = Path.Combine(AppContext.BaseDirectory,
                "client-firmware", "SMZ-HID-Client.uf2");
            if (!File.Exists(bundledClientUf2))
                throw new FileNotFoundException(
                    "فریمور همراه SMZ-HID-Client.uf2 در بستهٔ Classroom Studio پیدا نشد.",
                    bundledClientUf2);

            var outputPath = Path.GetFullPath(outputDialog.FileName);
            var outputDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidDataException("مسیر خروجی UF2 نامعتبر است.");
            var clientOutputPath = Path.Combine(outputDirectory, "SMZ-HID-Client.uf2");
            if (string.Equals(outputPath, clientOutputPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "نام UF2 پروژه با فایل همراه SMZ-HID-Client.uf2 تداخل دارد.");

            var workspace = CapturePipelineWorkspaceForExport();
            var result = await NativeUf2Exporter.ExportAsync(
                templateDialog.FileName, outputPath, workspace, _settings);
            File.Copy(bundledClientUf2, clientOutputPath, true);
            Log($"native UF2 export: {Path.GetFileName(outputPath)}; "
                + $"program={result.ProgramBytes} bytes; sha256={result.ProgramSha256}");
            Log("client firmware companion: " + clientOutputPath);
            if (!string.IsNullOrWhiteSpace(result.CompilerSummary))
                Log("native ABP compiler: " + result.CompilerSummary);
            if (!string.IsNullOrWhiteSpace(result.PatcherSummary))
                Log("native UF2 patcher: " + result.PatcherSummary);
            MessageBox.Show(
                $"UF2 پروژه و فایل همراه کلاینت ساخته شدند.\n\nپروژه: {outputPath}\nکلاینت: {clientOutputPath}\n\n"
                + $"ABP: {result.ProgramBytes:N0} bytes\nSHA-256: {result.ProgramSha256}",
                "Export Native UF2", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("native UF2 export failed: " + ex.Message);
            MessageBox.Show("ساخت UF2های پروژه و کلاینت ناموفق بود:\n" + ex.Message,
                "Export Native UF2", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
