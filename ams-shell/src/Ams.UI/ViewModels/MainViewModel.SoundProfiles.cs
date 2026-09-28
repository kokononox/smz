using Ams.UI.Models;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    private SoundWatchProfile SoundProfile(int id)
        => _pipelineWorkspace.SoundProfiles.Single(x => x.Id == id);

    private void UpdateSoundProfile(int id, Action<SoundWatchProfile> update, params string[] properties)
    {
        update(SoundProfile(id));
        _dirty = true;
        foreach (var property in properties) OnPropertyChanged(property);
        OnPropertyChanged(nameof(SoundProfileSummary));
    }

    public bool WhisperSoundEnabled
    {
        get => SoundProfile(1).Enabled;
        set => UpdateSoundProfile(1, x => x.Enabled = value, nameof(WhisperSoundEnabled));
    }
    public int WhisperPeakMin
    {
        get => SoundProfile(1).PeakMin;
        set => UpdateSoundProfile(1, x => x.PeakMin = Math.Clamp(value, 0, 511), nameof(WhisperPeakMin));
    }
    public int WhisperPeakMax
    {
        get => SoundProfile(1).PeakMax;
        set => UpdateSoundProfile(1, x => x.PeakMax = Math.Clamp(value, 1, 511), nameof(WhisperPeakMax));
    }
    public int WhisperPriority
    {
        get => SoundProfile(1).Priority;
        set => UpdateSoundProfile(1, x => x.Priority = Math.Clamp(value, -100, 100), nameof(WhisperPriority));
    }
    public int WhisperCooldownMs
    {
        get => SoundProfile(1).CooldownMs;
        set => UpdateSoundProfile(1, x => x.CooldownMs = Math.Clamp(value, 0, 60000), nameof(WhisperCooldownMs));
    }

    public bool SplashSoundEnabled
    {
        get => SoundProfile(2).Enabled;
        set => UpdateSoundProfile(2, x => x.Enabled = value, nameof(SplashSoundEnabled));
    }
    public int SplashPeakMin
    {
        get => SoundProfile(2).PeakMin;
        set => UpdateSoundProfile(2, x => x.PeakMin = Math.Clamp(value, 0, 511), nameof(SplashPeakMin));
    }
    public int SplashPeakMax
    {
        get => SoundProfile(2).PeakMax;
        set => UpdateSoundProfile(2, x => x.PeakMax = Math.Clamp(value, 1, 511), nameof(SplashPeakMax));
    }
    public int SplashPriority
    {
        get => SoundProfile(2).Priority;
        set => UpdateSoundProfile(2, x => x.Priority = Math.Clamp(value, -100, 100), nameof(SplashPriority));
    }
    public int SplashCooldownMs
    {
        get => SoundProfile(2).CooldownMs;
        set => UpdateSoundProfile(2, x => x.CooldownMs = Math.Clamp(value, 0, 60000), nameof(SplashCooldownMs));
    }
    public int SplashTimeoutMinSec
    {
        get => SoundProfile(2).TimeoutMinSec;
        set => UpdateSoundProfile(2, x => x.TimeoutMinSec = Math.Clamp(value, 1, 300), nameof(SplashTimeoutMinSec));
    }
    public int SplashTimeoutMaxSec
    {
        get => SoundProfile(2).TimeoutMaxSec;
        set => UpdateSoundProfile(2, x => x.TimeoutMaxSec = Math.Clamp(value, 1, 300), nameof(SplashTimeoutMaxSec));
    }

    public string SoundProfileSummary
    {
        get
        {
            var enabled = _pipelineWorkspace.SoundProfiles.Where(x => x.Enabled).ToList();
            return enabled.Count == 0
                ? "شنوندهٔ سراسری محیط بازی خاموش است."
                : string.Join(" · ", enabled.Select(x => x.ResponseTab == PipelineKind.Splash
                    ? $"{x.Name}: {x.PeakMin}–{x.PeakMax} / P{x.Priority} / Timeout {x.TimeoutMinSec}–{x.TimeoutMaxSec}s"
                    : $"{x.Name}: {x.PeakMin}–{x.PeakMax} / P{x.Priority}"));
        }
    }

    private void NotifySoundProfilesChanged()
    {
        foreach (var name in new[]
        {
            nameof(WhisperSoundEnabled), nameof(WhisperPeakMin), nameof(WhisperPeakMax),
            nameof(WhisperPriority), nameof(WhisperCooldownMs), nameof(SplashSoundEnabled),
            nameof(SplashPeakMin), nameof(SplashPeakMax), nameof(SplashPriority),
            nameof(SplashCooldownMs), nameof(SplashTimeoutMinSec),
            nameof(SplashTimeoutMaxSec), nameof(SoundProfileSummary),
        }) OnPropertyChanged(name);
    }
}
