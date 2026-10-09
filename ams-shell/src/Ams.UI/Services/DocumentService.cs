using System.IO;
using System.Text.Json;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>
/// .amsj document persistence (phase F5): a versioned JSON envelope around the
/// step tree. StepNode serializes its Props dictionary verbatim, so new step
/// fields survive open/save even before the UI knows about them.
/// </summary>
public static class DocumentService
{
    private sealed class Doc
    {
        public string app { get; set; } = "AMS";
        public string version { get; set; } = "0.8.2";
        public List<StepNode> steps { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void Save(string path, IEnumerable<StepNode> roots)
    {
        var doc = new Doc { steps = roots.ToList() };
        ShiftTextValidation.ValidateTree(doc.steps);
        File.WriteAllText(path, JsonSerializer.Serialize(doc, Opts));
    }

    public static List<StepNode> Load(string path)
    {
        var doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(path))
                  ?? throw new InvalidDataException("Not an AMS document.");
        if (doc.app != "AMS") throw new InvalidDataException("Not an AMS document.");
        foreach (var n in doc.steps) FixParents(n, null);
        return doc.steps;
    }

    /// <summary>
    /// v0.9.42 - a conditionless findImage / waitForSound / waitForLight can never own a
    /// branch. Older plans (and pre-v0.9.42 edits) nested steps under such a head, so those
    /// rows rendered one indent level too deep and pretended to be a Then branch. Lift every
    /// orphan child out so it becomes the next sibling of the step, order preserved.
    /// Else marker rows keep their children - they are the real not-found branch.
    /// Returns the number of lifted rows.
    /// </summary>
    public static int LiftOrphanChildren(IList<StepNode> list, StepNode? parent = null)
    {
        int lifted = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var n = list[i];
            lifted += LiftOrphanChildren(n.Children, n);
            if (n.Children.Count == 0 || StepDefinitions.AcceptsChildren(n) || IsBranchMarker(n)) continue;
            var orphans = n.Children.ToList();
            n.Children.Clear();
            int at = i + 1;
            foreach (var c in orphans)
            {
                c.Parent = parent;
                list.Insert(at++, c);
                lifted++;
            }
            i = at - 1;
        }
        return lifted;
    }

    /// <summary>Else / End If comment markers are structural: they legitimately hold children.</summary>
    private static bool IsBranchMarker(StepNode n)
        => n.Type == "comment"
           && (PropEx.GetString(n.Props, "text").StartsWith("Else", System.StringComparison.Ordinal)
               || PropEx.GetString(n.Props, "text").StartsWith("End If", System.StringComparison.Ordinal));

    internal static void FixParents(StepNode n, StepNode? parent)
    {
        n.Parent = parent;
        foreach (var c in n.Children) FixParents(c, n);
    }
}

/// <summary>
/// App options (Tools → Options → Serial/Board tab, §2.4). Persisted to
/// ams-settings.json next to the exe.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Default AUTO: BoardLink scans KNOWN_VIDS and finds the board on any COM (§14.3 / §15.5).</summary>
    public string DayWindowsUser { get; set; } = "";
    public string NightWindowsUser { get; set; } = "";
    public string Port { get; set; } = "AUTO";
    public string PythonDir { get; set; } = "";
    public string ToolkitDir { get; set; } = "";

    // Play Options (AMK bottom-panel parity, §9.1): app-side run settings — never in the .amsj
    public string PlayRepeatMode { get; set; } = "once";
    public int PlayRepeatTimes { get; set; } = 10;
    public int PlayRepeatValue { get; set; } = 1;
    public string PlayRepeatUnit { get; set; } = "minute";
    public bool ShutdownWhenFinished { get; set; }
    public bool NoActivateWhenStopped { get; set; }

    // v0.9.67 — portable automatic-cycle settings. These belong to app settings and are
    // intentionally not written into .amsj documents. The buzzer pin is hardware policy,
    // not a user option: every portable export uses GP6.
    public int RestartMinMinutes { get; set; } = 110;
    public int RestartMaxMinutes { get; set; } = 130;
    public bool AutoResumeEnabled { get; set; } = true;
    public int AutoResumeMinMinutes { get; set; } = 3;
    public int AutoResumeMaxMinutes { get; set; } = 5;
    public bool PostRestartLaunchEnabled { get; set; } = true;
    public int PostRestartTaskbarSlot { get; set; } = 1;
    public int PostRestartLaunchBeforeMinSeconds { get; set; } = 1;
    public int PostRestartLaunchBeforeMaxSeconds { get; set; } = 3;
    public int PostRestartLaunchAfterMinSeconds { get; set; } = 20;
    public int PostRestartLaunchAfterMaxSeconds { get; set; } = 40;
    public const string PortableBuzzerPin = "GP6";

    public int WordDelayMin { get; set; }
    public int WordDelayMax { get; set; }
    public int MouseMoveSpeedMin { get; set; } = 300;
    public int MouseMoveSpeedMax { get; set; } = 2000;
    public int TypeKeyMinMs { get; set; } = 80;
    public int TypeKeyMaxMs { get; set; } = 220;
    public string RunHotkey { get; set; } = "Shift+F1";
    public string StopHotkey { get; set; } = "Shift+F2";
    public string PauseHotkey { get; set; } = "Shift+F3";
    public string ResumeHotkey { get; set; } = "Shift+F4";
    public string RunStopHotkey { get; set; } = "";
    public string PauseResumeHotkey { get; set; } = "";
    public string KeyboardBoard { get; set; } = "pico";

    private static string Path_ => System.IO.Path.Combine(AppContext.BaseDirectory, "ams-settings.json");

    public static (int min, int max) NormalizeSpeedDefaults(int min, int max)
        => min == 0 && max == 2300 ? (300, 2000) : (min, max);

    /// <summary>Normalizes one positive minute range. Missing/corrupt non-positive values
    /// return to their factory defaults; reversed valid bounds are swapped.</summary>
    public static (int min, int max) NormalizePositiveRange(int min, int max, int defaultMin, int defaultMax)
    {
        if (min <= 0) min = defaultMin;
        if (max <= 0) max = defaultMax;
        if (max < min) (min, max) = (max, min);
        return (min, max);
    }

    public static (int min, int max) NormalizeMinuteRange(int min, int max, int defaultMin, int defaultMax)
        => NormalizePositiveRange(min, max, defaultMin, defaultMax);

    /// <summary>Applies the v0.9.67 contract and reports whether persistence changed.</summary>
    public bool NormalizeAutoCycleSettings()
    {
        var old = (RestartMinMinutes, RestartMaxMinutes, AutoResumeMinMinutes, AutoResumeMaxMinutes,
            PostRestartTaskbarSlot, PostRestartLaunchBeforeMinSeconds, PostRestartLaunchBeforeMaxSeconds,
            PostRestartLaunchAfterMinSeconds, PostRestartLaunchAfterMaxSeconds);
        (RestartMinMinutes, RestartMaxMinutes) = NormalizeMinuteRange(
            RestartMinMinutes, RestartMaxMinutes, 110, 130);
        (AutoResumeMinMinutes, AutoResumeMaxMinutes) = NormalizeMinuteRange(
            AutoResumeMinMinutes, AutoResumeMaxMinutes, 3, 5);
        PostRestartTaskbarSlot = Math.Clamp(PostRestartTaskbarSlot, 1, 9);
        (PostRestartLaunchBeforeMinSeconds, PostRestartLaunchBeforeMaxSeconds) = NormalizePositiveRange(
            PostRestartLaunchBeforeMinSeconds, PostRestartLaunchBeforeMaxSeconds, 1, 3);
        (PostRestartLaunchAfterMinSeconds, PostRestartLaunchAfterMaxSeconds) = NormalizePositiveRange(
            PostRestartLaunchAfterMinSeconds, PostRestartLaunchAfterMaxSeconds, 20, 40);
        return old != (RestartMinMinutes, RestartMaxMinutes, AutoResumeMinMinutes, AutoResumeMaxMinutes,
            PostRestartTaskbarSlot, PostRestartLaunchBeforeMinSeconds, PostRestartLaunchBeforeMaxSeconds,
            PostRestartLaunchAfterMinSeconds, PostRestartLaunchAfterMaxSeconds);
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path_))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path_)) ?? new();
                var (m0, m1) = NormalizeSpeedDefaults(s.MouseMoveSpeedMin, s.MouseMoveSpeedMax);
                if (m0 != s.MouseMoveSpeedMin || m1 != s.MouseMoveSpeedMax)
                {
                    s.MouseMoveSpeedMin = m0;
                    s.MouseMoveSpeedMax = m1;
                    s.Save();
                }
                if (string.IsNullOrWhiteSpace(s.RunStopHotkey))
                {
                    s.RunStopHotkey = s.RunHotkey;
                    s.PauseResumeHotkey = s.PauseHotkey;
                    s.Save();
                }
                if (!string.IsNullOrEmpty(s.PythonDir) && !System.IO.Directory.Exists(s.PythonDir))
                {
                    s.PythonDir = "";
                    s.Save();
                }
                if (!string.IsNullOrEmpty(s.ToolkitDir) && !System.IO.Directory.Exists(s.ToolkitDir))
                {
                    s.ToolkitDir = "";
                    s.Save();
                }
                if (s.Port != "AUTO" && !System.IO.Ports.SerialPort.GetPortNames().Contains(s.Port))
                {
                    s.Port = "AUTO";
                    s.Save();
                }
                if (s.NormalizeAutoCycleSettings()) s.Save();
                return s;
            }
        }
        catch { /* corrupt settings → defaults */ }
        return new();
    }

    public void Save() => File.WriteAllText(Path_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
