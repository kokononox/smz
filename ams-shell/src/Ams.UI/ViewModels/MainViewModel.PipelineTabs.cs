using System.Collections.ObjectModel;
using System.IO;
using Ams.UI.Models;
using Ams.UI.Services;
using CommunityToolkit.Mvvm.Input;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    private PipelineWorkspace _pipelineWorkspace = new();
    private PipelineTabDocument? _activePipelineTab;
    private bool _pipelineInitialized;
    private bool _pipelineLoadInProgress;
    private bool _pipelineSyncAttached;

    // Splash is a legacy storage route only. Catch actions now live visibly
    // under the explicit Wait For Sound step in Game.
    public IReadOnlyList<PipelineTabDocument> PipelineTabs
        => _pipelineWorkspace.Tabs.Where(x => x.Kind != PipelineKind.Splash).ToList();
    public PipelineTabDocument? ActivePipelineTab => _activePipelineTab;
    public string ActivePipelineTitle => _activePipelineTab?.Title ?? "Desktop";
    public bool IsLaunchPipeline => _activePipelineTab?.Kind == PipelineKind.Launch;
    public bool IsMainPipeline => _activePipelineTab?.Kind == PipelineKind.Main;

    public void InitializePipelineTabs()
    {
        if (_pipelineInitialized) return;
        _pipelineInitialized = true;
        var main = _pipelineWorkspace[PipelineKind.Main];
        CopyTree(Steps, main.Steps);
        _activePipelineTab = main;
        AttachPipelineStepSync();
        OnPropertyChanged(nameof(PipelineTabs));
        OnPropertyChanged(nameof(ActivePipelineTab));
        OnPropertyChanged(nameof(ActivePipelineTitle));
        OnPropertyChanged(nameof(IsLaunchPipeline));
        OnPropertyChanged(nameof(IsMainPipeline));
        Log("pipeline tabs initialized: " + PipelineCounts());
    }

    [RelayCommand]
    private void SwitchPipeline(PipelineTabDocument? tab)
    {
        if (tab is null || ReferenceEquals(tab, _activePipelineTab) || IsRunning) return;
        InitializePipelineTabs();
        CaptureActivePipeline();
        _activePipelineTab = tab;
        LoadActivePipeline();
        OnPropertyChanged(nameof(ActivePipelineTab));
        OnPropertyChanged(nameof(ActivePipelineTitle));
        OnPropertyChanged(nameof(IsLaunchPipeline));
        OnPropertyChanged(nameof(IsMainPipeline));
        Log("pipeline tab: " + tab.Title + " — visible roots=" + Steps.Count + ", total=" + CountAll(Steps));
    }

    internal PipelineWorkspace CapturePipelineWorkspaceForExport()
    {
        InitializePipelineTabs();
        CaptureActivePipeline();
        return _pipelineWorkspace;
    }

    private void CaptureActivePipeline()
    {
        if (_activePipelineTab is null) return;
        CopyTree(Steps, _activePipelineTab.Steps);
        _activePipelineTab.IsDirty |= _dirty;
    }

    private void AttachPipelineStepSync()
    {
        if (_pipelineSyncAttached) return;
        _pipelineSyncAttached = true;
        Steps.CollectionChanged += PipelineSteps_CollectionChanged;
    }

    private void PipelineSteps_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_pipelineInitialized || _pipelineLoadInProgress || _activePipelineTab is null) return;
        // Keep the tab document current as soon as a root-level edit happens. Save still
        // performs a full capture so nested edits and property changes are included too.
        CopyTree(Steps, _activePipelineTab.Steps);
        _activePipelineTab.IsDirty = true;
    }

    private void LoadActivePipeline()
    {
        _pipelineLoadInProgress = true;
        try
        {
            Steps.Clear();
            if (_activePipelineTab is not null) CopyTree(_activePipelineTab.Steps, Steps);
            SelectedNodes = new();
            SelectedNode = null;
            _collapsed.Clear();
            _undo.Clear();
            _redo.Clear();
            Renumber();
        }
        finally
        {
            _pipelineLoadInProgress = false;
        }
    }

    private static void CopyTree(IEnumerable<StepNode> source, ICollection<StepNode> destination)
    {
        destination.Clear();
        foreach (var node in StepTreeSerializer.Restore(StepTreeSerializer.Snapshot(source)))
            destination.Add(node);
    }

    private string PipelineCounts()
        => string.Join(", ", _pipelineWorkspace.Tabs.Select(tab =>
            $"{tab.Kind}={{roots:{tab.Steps.Count},total:{CountAll(tab.Steps)}}}"));

    [RelayCommand]
    private void NewPipelineWorkspace()
    {
        if (!ConfirmDiscard()) return;
        InitializePipelineTabs();
        _pipelineWorkspace = new PipelineWorkspace();
        _pipelineWorkspace.EnsureDcDefaults();
        _activePipelineTab = _pipelineWorkspace[PipelineKind.Main];
        _currentFile = null;
        _dirty = false;
        LoadActivePipeline();
        OnPropertyChanged(nameof(PipelineTabs));
        OnPropertyChanged(nameof(ActivePipelineTab));
        OnPropertyChanged(nameof(ActivePipelineTitle));
        OnPropertyChanged(nameof(IsLaunchPipeline));
        OnPropertyChanged(nameof(IsMainPipeline));
        NotifySoundProfilesChanged();
        OnPropertyChanged(nameof(HumanMouseProfileSummary));
        UpdateFileText();
        Log("new pipeline workspace: " + PipelineCounts());
    }

    [RelayCommand]
    private void OpenPipelineWorkspace()
    {
        if (!ConfirmDiscard()) return;
        InitializePipelineTabs();
        var targetKind = _activePipelineTab?.Kind ?? PipelineKind.Main;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "AMS pipeline (*.amsj)|*.amsj" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var json = File.ReadAllText(dialog.FileName);
            try { _pipelineWorkspace = PipelineWorkspaceSerializer.Deserialize(json); }
            catch (InvalidDataException)
            {
                _pipelineWorkspace = PipelineWorkspace.FromLegacy(DocumentService.Load(dialog.FileName), targetKind);
            }
            _activePipelineTab = _pipelineWorkspace[targetKind];
            _currentFile = dialog.FileName;
            _dirty = false;
            LoadActivePipeline();
            foreach (var tab in _pipelineWorkspace.Tabs) tab.IsDirty = false;
            OnPropertyChanged(nameof(PipelineTabs));
            OnPropertyChanged(nameof(ActivePipelineTab));
            OnPropertyChanged(nameof(ActivePipelineTitle));
            OnPropertyChanged(nameof(IsLaunchPipeline));
        OnPropertyChanged(nameof(IsMainPipeline));
            NotifySoundProfilesChanged();
            OnPropertyChanged(nameof(HumanMouseProfileSummary));
            UpdateFileText();
            Log("pipeline workspace opened in " + targetKind + ": " + dialog.FileName + " — " + PipelineCounts());
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("بازکردن Pipeline ناموفق بود: " + ex.Message,
                "Open Pipeline", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void SavePipelineWorkspace() => SavePipelineWorkspaceCore(false);

    [RelayCommand]
    private void SavePipelineWorkspaceAs() => SavePipelineWorkspaceCore(true);

    private void SavePipelineWorkspaceCore(bool saveAs)
    {
        InitializePipelineTabs();
        CaptureActivePipeline();
        Log("pipeline save snapshot: " + PipelineCounts());
        var path = _currentFile;
        if (saveAs || string.IsNullOrWhiteSpace(path))
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "AMS pipeline (*.amsj)|*.amsj",
                FileName = "autocycle.amsj",
            };
            if (dialog.ShowDialog() != true) return;
            path = dialog.FileName;
        }
        try
        {
            File.WriteAllText(path!, PipelineWorkspaceSerializer.Serialize(_pipelineWorkspace));
            _currentFile = path;
            _dirty = false;
            foreach (var tab in _pipelineWorkspace.Tabs) tab.IsDirty = false;
            UpdateFileText();
            Log("pipeline workspace saved: " + path + " — " + PipelineCounts());
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("ذخیره‌ی Pipeline ناموفق بود: " + ex.Message,
                "Save Pipeline", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
