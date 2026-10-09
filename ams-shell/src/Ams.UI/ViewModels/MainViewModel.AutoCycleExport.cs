using System.IO;
using System.Windows;
using Ams.UI.Services;
using CommunityToolkit.Mvvm.Input;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void ExportAutoCyclePicoPlan()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Pico automatic-cycle plan (plan.txt)|plan.txt",
            FileName = "plan.txt",
            Title = "خروجی چرخه‌ی خودکار پیکو — پوشه‌ی CIRCUITPY را انتخاب کن",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var workspace = CapturePipelineWorkspaceForExport();
            if(workspace.GameBuffs.Any(b=>!b.IsDisabled))
                throw new InvalidOperationException("باف مستقل به خروجی Native UF2 نیاز دارد؛ این خروجی پشتیبانی نمی‌کند.");
            var written = PipelinePlanBundle.Export(dlg.FileName, workspace, _settings,
                (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight,
                _currentFile ?? "untitled", Environment.MachineName);
            foreach (var file in written) Log("auto-cycle plan: " + Path.GetFileName(file));
            Log($"auto-cycle pipeline: {written.Count} file(s) written; Main owns cycle directives");
            MessageBox.Show(
                $"بسته‌ی Pipeline ساخته شد ({written.Count} فایل).\n"
                + "Launch، Main، دو Recovery و Resume Essentials به فایل‌های مستقل PLAN|2 تبدیل شدند.\n"
                + "RUNFOR و AUTORESUME فقط در plan.txt اصلی نوشته شدند.\n"
                + "همه‌ی فایل‌ها را در ریشه‌ی CIRCUITPY نگه دار.",
                "Export AutoCycle Pipeline", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (PlanExporter.PlanBlockedException bx)
        {
            Log($"auto-cycle export blocked ({bx.Errors.Count} problem(s)):");
            foreach (var error in bx.Errors) Log("  x " + error);
            MessageBox.Show($"اکسپورت Pipeline متوقف شد — {bx.Errors.Count} خطا. جزئیات در لاگ است.",
                "Export AutoCycle Pipeline", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log("auto-cycle export failed: " + ex.Message);
            MessageBox.Show("خروجی Pipeline ناموفق بود: " + ex.Message,
                "Export AutoCycle Pipeline", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
