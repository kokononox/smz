using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Ams.UI.Services;

namespace Ams.UI.Models;

public enum FieldKind { Text, Multiline, Int, Combo, EditableCombo, Check, AudioDevice, Float }   // v0.9.59 — Float: decimal number (e.g. stableSec)

/// <summary>One editable field of a step dialog (design doc §5.5.x dialog maps).</summary>
/// <summary>v0.9.14 — HideWhenKey/HideWhenValue: the step dialog collapses this field while the
/// sibling field HideWhenKey currently equals HideWhenValue (e.g. typeText typing-tuning fields
/// hide in clipboard mode; Find Image human-move fields hide when the humanMove box is off).</summary>
public sealed record FieldDef(string Key, string Label, FieldKind Kind, string Default = "", string[]? Options = null,
                              string? BrowseFilter = null, string? HideWhenKey = null, string? HideWhenValue = null, string? HideUnlessValue = null);

/// <summary>
/// Registry entry for a step type: label, category color, dialog fields,
/// summary renderer and board commands. Command syntax verified against
/// ams_board.ino (firmware 1.6) — see design doc §5 and §15.5 (pipe separator).
/// </summary>
public sealed class StepDefinition
{
    public required string Label { get; init; }
    public required string ColorResourceKey { get; init; }
    public int DefaultDelay { get; init; }

    /// <summary>Containers accept child steps (adding while selected inserts into them).
    /// For Loop children = body · Find Image children = Then branch (§3.3.1).</summary>
    public bool IsContainer { get; init; }

    /// <summary>Scope containers produce a visible range band + red scope line in the flat list (v0.7.2):
    /// For Loop → soft-blue tinted body · Find Image (If/Else) → mint-tinted body.</summary>
    public bool IsScopeContainer { get; init; }

    public required IReadOnlyList<FieldDef> Fields { get; init; }
    public required Func<StepNode, string> Summarize { get; init; }

    /// <summary>Board commands for board-executed steps; null for PC-side/structural steps.</summary>
    public Func<StepNode, IReadOnlyList<string>>? Commands { get; init; }
}

public static class StepDefinitions
{
    private static Random Rng = new();   // NOT readonly: swappable under _rngSync for seeded tests (v0.9.16)
    private static readonly object _rngSync = new();   // v0.9.15 — Parallel Group branches share this RNG
    private static int NextInt(int maxExclusive) { lock (_rngSync) return Rng.Next(maxExclusive); }
    /// <summary>v0.9.39 - BH1750 range helpers: centre +/- tolerance (dead zone rule: keep 50+ lux between states).</summary>
    private static int LuxLow(StepNode s) => Math.Max(0, PropEx.GetInt(s.Props, "luxCenter", 1250) - Math.Max(1, PropEx.GetInt(s.Props, "luxTolerance", 50)));
    private static int LuxHigh(StepNode s) => PropEx.GetInt(s.Props, "luxCenter", 1250) + Math.Max(1, PropEx.GetInt(s.Props, "luxTolerance", 50));
    private static int LuxVk(StepNode s) => KeyMap.VK.TryGetValue(PropEx.GetString(s.Props, "key", "E"), out int vk) ? vk : 69;
    /// <summary>v0.9.60 — stableSec is fractional (FieldKind.Float): the summary must show
    /// "0.5s", not a rounded-down integer. Invariant "0.#" keeps the dot in every locale.</summary>
    private static string StableSecText(StepNode s)
        => PropEx.GetDouble(s.Props, "stableSec", 2).ToString("0.#", CultureInfo.InvariantCulture);

    private static string TimeoutPolicyText(StepNode s)
        => PropEx.GetString(s.Props, "onTimeout", "global") switch
        {
            "stopWithAlarm" => "timeout → alarm + stop",
            "stopQuiet" => "timeout → quiet stop",
            "continue" => "timeout → continue",
            _ => "timeout → global policy",
        };

    private static readonly Dictionary<string, StepDefinition> Defs = new()
    {
        ["mouseClick"] = new StepDefinition
        {
            Label = "Mouse Click", ColorResourceKey = "StepMouseBrush", DefaultDelay = 1000,
            Fields = new FieldDef[]
            {
                new("button", "Mouse button", FieldKind.Combo, "left", new[] { "left", "middle", "right" }),
                new("action", "Action", FieldKind.Combo, "single", new[] { "single", "double" }),
                new("holdMin", "Hold min (ms) — random press-hold range; 0 = firmware default (firmware 1.7+)", FieldKind.Int, "0"),
                new("holdMax", "Hold max (ms) — 0 = firmware default (firmware 1.7+)", FieldKind.Int, "0"),
            },
            Summarize = s => $"{Title(PropEx.GetString(s.Props, "button", "left"))} {(PropEx.GetString(s.Props, "action") == "double" ? "Double " : "")}Click" + HoldText(s.Props),
            Commands = s => new[] { $"MCLICK|{PropEx.GetString(s.Props, "button", "left")},{(PropEx.GetString(s.Props, "action") == "double" ? 2 : 1)}{HoldSuffix(s.Props)}" },
        },
        ["mouseMove"] = new StepDefinition
        {
            Label = "Mouse Movement", ColorResourceKey = "StepMouseBrush", DefaultDelay = 1000,
            Fields = new FieldDef[]
            {
                new("x", "X", FieldKind.Int, "600"),
                new("y", "Y", FieldKind.Int, "497"),
                new("moveMode", "Movement source", FieldKind.Combo, "fixed", new[] { "fixed", "handSample" }),
                new("handSample", "Recorded hand movement", FieldKind.Text, ""),
                new("human", "Humanized movement (app-side WindMouse path + pauses — off = instant firmware move)", FieldKind.Check, "true"),
                new("pauseBeforeMin", "Pause BEFORE move — min (ms)", FieldKind.Int, "60", HideWhenKey: "human", HideWhenValue: "false"),
                new("pauseBeforeMax", "Pause BEFORE move — max (ms)", FieldKind.Int, "220", HideWhenKey: "human", HideWhenValue: "false"),
                new("pauseAfterMin", "Settle pause AFTER arrival — min (ms)", FieldKind.Int, "80", HideWhenKey: "human", HideWhenValue: "false"),
                new("pauseAfterMax", "Settle pause AFTER arrival — max (ms)", FieldKind.Int, "280", HideWhenKey: "human", HideWhenValue: "false"),
                new("midPauseChance", "Mid-path hesitation chance % (0 = off)", FieldKind.Int, "6", HideWhenKey: "human", HideWhenValue: "false"),
                new("midPauseMin", "Hesitation pause — min (ms)", FieldKind.Int, "80", HideWhenKey: "human", HideWhenValue: "false"),
                new("midPauseMax", "Hesitation pause — max (ms)", FieldKind.Int, "250", HideWhenKey: "human", HideWhenValue: "false"),
                new("overshootChance", "Overshoot & correct chance %", FieldKind.Int, "12", HideWhenKey: "human", HideWhenValue: "false"),
                new("curveMinPct", "Path curvature MIN % · 0–100 bow · 101–200 true arc", FieldKind.Int, "20", HideWhenKey: "human", HideWhenValue: "false"),
                new("curveMaxPct", "Path curvature MAX % · modulated smoothly during the move", FieldKind.Int, "40", HideWhenKey: "human", HideWhenValue: "false"),
                new("moveTimeMin", "Move duration — min (ms) · 0/0 = speed-based (Options)", FieldKind.Int, "0", HideWhenKey: "human", HideWhenValue: "false"),
                new("moveTimeMax", "Move duration — max (ms)", FieldKind.Int, "0", HideWhenKey: "human", HideWhenValue: "false"),
            },
            Summarize = s =>
            {
                if (PropEx.GetString(s.Props, "moveMode", "fixed") == "handSample")
                {
                    if (HandMovementSample.TryDecode(PropEx.GetString(s.Props, "handSample"), out var sample))
                    {
                        var delta = HandMovementSample.Displacement(sample);
                        return $"Replay Relative Hand Gesture Δ({delta.X}, {delta.Y})";
                    }
                    return "Replay Relative Hand Gesture · sample required";
                }
                return $"Move to Position ({PropEx.GetInt(s.Props, "x")}, {PropEx.GetInt(s.Props, "y")})";
            },
            Commands = s => new[] { $"MMOVE|{PropEx.GetInt(s.Props, "x")},{PropEx.GetInt(s.Props, "y")},abs,{(PropEx.GetBool(s.Props, "human", true) ? 1 : 0)}" },
        },
        ["mouseScroll"] = new StepDefinition
        {
            Label = "Mouse Scroll", ColorResourceKey = "StepMouseBrush", DefaultDelay = 1000,
            Fields = new FieldDef[] { new("delta", "Wheel delta (negative = down)", FieldKind.Int, "-1") },
            Summarize = s => $"Mouse Scroll {PropEx.GetInt(s.Props, "delta")}",
            Commands = s => new[] { $"MWHEEL|{PropEx.GetInt(s.Props, "delta")}" },
        },
        ["randomMousePosition"] = new StepDefinition
        {
            Label = "Random Mouse Position", ColorResourceKey = "StepMouseBrush", DefaultDelay = 55,
            Fields = new FieldDef[]
            {
                new("x", "Region X", FieldKind.Int, "1301"),
                new("y", "Region Y", FieldKind.Int, "0"),
                new("w", "Region width", FieldKind.Int, "378"),
                new("h", "Region height", FieldKind.Int, "1049"),
                // Captured style source for the sampled-RMOUSE profile. The
                // dialog owns this opaque payload; it is never hand-edited.
                new("handSample", "Recorded hand movement", FieldKind.Text, ""),
                new("pauseBeforeMin", "Reaction pause BEFORE the move — min (ms)", FieldKind.Int, "120"),
                new("pauseBeforeMax", "Reaction pause BEFORE the move — max (ms)", FieldKind.Int, "450"),
                new("pauseAfterMin", "Settle pause AFTER arrival — min (ms)", FieldKind.Int, "150"),
                new("pauseAfterMax", "Settle pause AFTER arrival — max (ms)", FieldKind.Int, "600"),
                new("midPauseChance", "Mid-path hesitation chance % (0 = off)", FieldKind.Int, "12"),
                new("midPauseMin", "Hesitation pause length — min (ms)", FieldKind.Int, "100"),
                new("midPauseMax", "Hesitation pause length — max (ms)", FieldKind.Int, "500"),   // v0.9.24 — recorded hesitations ran 297–485 ms
                new("idleEveryMin", "Long break every N moves — min N", FieldKind.Int, "5"),
                new("idleEveryMax", "Long break every N moves — max N", FieldKind.Int, "12"),
                new("idlePauseMin", "Long break length — min (ms)", FieldKind.Int, "800"),
                new("idlePauseMax", "Long break length — max (ms) · 0 disables long breaks (800–3000 = human-calibrated)", FieldKind.Int, "3000"),
                new("overshootChance", "Overshoot & correct chance % (aim past the point, then correct back)", FieldKind.Int, "25"),   // v0.9.24 — user's own hand overshot 2/4 recorded moves
                new("curveMinPct", "Path curvature MIN % · 0–100 slight bow · 101–200 true arc", FieldKind.Int, "15"),
                new("curveMaxPct", "Path curvature MAX % · changes smoothly during every move (e.g. 120–190)", FieldKind.Int, "45"),
                new("moveTimeMin", "Movement duration — min (ms) · 0/0 = use global speed range (Options)", FieldKind.Int, "0"),
                new("moveTimeMax", "Movement duration — max (ms) · fresh random target per move; floor ≈ 1ms per micro-step", FieldKind.Int, "0"),
            },
            Summarize = s => $"Random Mouse Position in region [{PropEx.GetInt(s.Props, "x")},{PropEx.GetInt(s.Props, "y")} {PropEx.GetInt(s.Props, "w", 100)}x{PropEx.GetInt(s.Props, "h", 100)}]" +
                (PropEx.GetInt(s.Props, "idlePauseMax", 3000) > 0
                    ? $" · break {PropEx.GetInt(s.Props, "idlePauseMin", 800)}–{PropEx.GetInt(s.Props, "idlePauseMax", 3000)}ms / {PropEx.GetInt(s.Props, "idleEveryMin", 5)}–{PropEx.GetInt(s.Props, "idleEveryMax", 12)} moves"
                    : ""),
            // v0.9.0 — WindMouse path + full pause manager: see HumanMouse / RunEngine / ScriptGenerator
        },
        ["keystroke"] = new StepDefinition
        {
            Label = "Keystroke", ColorResourceKey = "StepKeyboardBrush", DefaultDelay = 1000,
            Fields = new FieldDef[]
            {
                new("modCtrl", "Ctrl", FieldKind.Check, "false"),
                new("modShift", "Shift", FieldKind.Check, "false"),
                new("modAlt", "Alt", FieldKind.Check, "false"),
                new("modWin", "Win", FieldKind.Check, "false"),
                new("key", "Key", FieldKind.Combo, "F4", KeyMap.KeyNames.ToArray()),
                new("holdMin", "Hold min (ms) — random press-hold range; 0 = firmware default (firmware 1.7+)", FieldKind.Int, "0"),
                new("holdMax", "Hold max (ms) — 0 = firmware default (firmware 1.7+)", FieldKind.Int, "0"),
                new("keyboardBoard", "Keyboard executor — default uses Options; Pico executes locally; Pro Micro uses the UART arm", FieldKind.Combo, "default", new[] { "default", "pico", "promicro" }),
            },
            Summarize = s => "Keystroke " + ComboText(s.Props) + HoldText(s.Props) + KeyboardBoardHint(s.Props),
            Commands = s => new[] { "KCOMBO|" + ComboVk(s.Props) + HoldSuffix(s.Props) },
        },
        ["typeText"] = new StepDefinition
        {
            Label = "Type Text", ColorResourceKey = "StepKeyboardBrush", DefaultDelay = 1000,
            Fields = new FieldDef[]
            {
                new("text", "Text", FieldKind.Multiline, ""),
                new("mode", "Mode", FieldKind.Combo, "keystrokes", new[] { "keystrokes", "clipboard" }),
                new("keyboardBoard", "Keyboard executor — default uses Options; Pico executes locally; Pro Micro uses the UART arm", FieldKind.Combo, "default", new[] { "default", "pico", "promicro" }),
                new("secret", "Sensitive (password) — masked in logs, pasted via clipboard (§17.6)", FieldKind.Check, "false"),
                new("hmin", "Humanize min (ms between keys — human-calibrated default 80)", FieldKind.Int, "80", HideWhenKey: "mode", HideWhenValue: "clipboard"),
                new("hmax", "Humanize max (ms between keys — human-calibrated default 220)", FieldKind.Int, "220", HideWhenKey: "mode", HideWhenValue: "clipboard"),
                new("wmin", "Word humanize min (ms pause between words — 0 = disabled)", FieldKind.Int, "0", HideWhenKey: "mode", HideWhenValue: "clipboard"),
                new("wmax", "Word humanize max (ms pause between words — 0 = disabled)", FieldKind.Int, "0", HideWhenKey: "mode", HideWhenValue: "clipboard"),
                new("wordPauseChance", "Word pause chance % · 100 = after EVERY word (metronome) · 40–70 looks human", FieldKind.Int, "60", HideWhenKey: "mode", HideWhenValue: "clipboard"),
                new("pmin", "Punctuation pause — min (ms after . , ! ? ; : · 0 = off)", FieldKind.Int, "0", HideWhenKey: "mode", HideWhenValue: "clipboard"),
                new("pmax", "Punctuation pause — max (ms)", FieldKind.Int, "0", HideWhenKey: "mode", HideWhenValue: "clipboard"),
                new("thinkChance", "Thinking pause chance % per word (0 = off · humans pause to think)", FieldKind.Int, "0", HideWhenKey: "mode", HideWhenValue: "clipboard"),
                new("thinkMin", "Thinking pause — min (ms)", FieldKind.Int, "800", HideWhenKey: "mode", HideWhenValue: "clipboard"),
                new("thinkMax", "Thinking pause — max (ms)", FieldKind.Int, "2200", HideWhenKey: "mode", HideWhenValue: "clipboard"),
                // Keep the persisted property names for old .amsj files, but their user-facing
                // meaning is the eligible-character interval between corrected slips.
                new("typoEveryMin", "Typing-error interval — minimum eligible characters · 0/0 = off", FieldKind.Int, "0", HideWhenKey: "mode", HideWhenValue: "clipboard"),
                new("typoEveryMax", "Typing-error interval — maximum eligible characters · re-rolled after each correction", FieldKind.Int, "0", HideWhenKey: "mode", HideWhenValue: "clipboard"),
            },
            Summarize = s =>
            {
                if (PropEx.GetBool(s.Props, "secret"))
                    return "Type text · SECRET (masked) · clipboard" + KeyboardBoardHint(s.Props);
                var t = PropEx.GetString(s.Props, "text").Replace('\n', ' ');
                if (t.Length > 30) t = t[..30] + "…";
                string hint = PropEx.GetString(s.Props, "mode", "keystrokes");
                int wmin = PropEx.GetInt(s.Props, "wmin"), wmax = PropEx.GetInt(s.Props, "wmax");
                int wpc = PropEx.GetInt(s.Props, "wordPauseChance", 100);
                if (wmax > 0) hint += $" · words {wmin}–{wmax}ms" + (wpc < 100 ? $" @{wpc}%" : "");
                if (PropEx.GetInt(s.Props, "pmax") > 0) hint += " · punct";
                if (PropEx.GetInt(s.Props, "thinkChance") > 0) hint += $" · think {PropEx.GetInt(s.Props, "thinkChance")}%";
                int tyMax = PropEx.GetInt(s.Props, "typoEveryMax");
                if (tyMax > 0) hint += $" · typo every {PropEx.GetInt(s.Props, "typoEveryMin")}–{tyMax} chars";
                else if (PropEx.GetInt(s.Props, "typoChance") > 0) hint += $" · typos {PropEx.GetInt(s.Props, "typoChance")}%";
                return $"Type text · {hint} · \"{t}\"" + KeyboardBoardHint(s.Props);
            },
            Commands = s => TypeTextCommands(s.Props),
        },
        ["keyDown"] = new StepDefinition
        {
            Label = "Key Down", ColorResourceKey = "StepKeyboardBrush", DefaultDelay = 50,
            Fields = new FieldDef[]
            {
                new("key", "Key", FieldKind.Combo, "SHIFT", KeyMap.KeyNames.ToArray()),
                new("keyboardBoard", "Keyboard executor — default uses Options; Pico executes locally; Pro Micro uses the UART arm", FieldKind.Combo, "default", new[] { "default", "pico", "promicro" }),
            },
            Summarize = s => "Key Down " + PropEx.GetString(s.Props, "key", "SHIFT") + KeyboardBoardHint(s.Props),
            Commands = s => new[] { "KDOWN|" + KeyVk(s.Props) },
        },
        ["keyUp"] = new StepDefinition
        {
            Label = "Key Up", ColorResourceKey = "StepKeyboardBrush", DefaultDelay = 50,
            Fields = new FieldDef[]
            {
                new("key", "Key", FieldKind.Combo, "SHIFT", KeyMap.KeyNames.ToArray()),
                new("keyboardBoard", "Keyboard executor — default uses Options; Pico executes locally; Pro Micro uses the UART arm", FieldKind.Combo, "default", new[] { "default", "pico", "promicro" }),
            },
            Summarize = s => "Key Up " + PropEx.GetString(s.Props, "key", "SHIFT") + KeyboardBoardHint(s.Props),
            Commands = s => new[] { "KUP|" + KeyVk(s.Props) },
        },
        ["delay"] = new StepDefinition
        {
            Label = "Delay", ColorResourceKey = "StepDelayBrush", DefaultDelay = 0,
            Fields = new FieldDef[]
            {
                new("minMs", "Min (ms)", FieldKind.Int, "0"),
                new("maxMs", "Max (ms)", FieldKind.Int, "333"),
            },
            Summarize = s => $"Delay {PropEx.GetInt(s.Props, "minMs")} to {PropEx.GetInt(s.Props, "maxMs", 333)} ms",
            // PC-side — no board command
        },
        ["raiseError"] = new StepDefinition
        {
            Label = "Raise Error / Stop Macro", ColorResourceKey = "StepErrorBrush", DefaultDelay = 0,
            Fields = new FieldDef[]
            {
                new("message", "Error message", FieldKind.Multiline, "A deliberate error stopped the macro."),
            },
            Summarize = s =>
            {
                var m = PropEx.GetString(s.Props, "message", "A deliberate error stopped the macro.").Replace('\n', ' ').Trim();
                if (m.Length > 56) m = m[..56] + "…";
                return "⛔ Raise Error · " + m;
            },
            // PC-side safety step: RunEngine records it, starts the buzzer alarm, and stops immediately.
        },
        ["forLoop"] = new StepDefinition
        {
            Label = "For Loop", ColorResourceKey = "StepFlowBrush", DefaultDelay = 0, IsContainer = true, IsScopeContainer = true,
            Fields = new FieldDef[]
            {
                new("title", "Group title (blank = default name)", FieldKind.Text, ""),
                new("mode", "Loop mode", FieldKind.Combo, "count", new[] { "count", "time", "infinite" }),
                new("count", "Loop count (count mode)", FieldKind.Int, "10", HideWhenKey: "mode", HideUnlessValue: "count"),
                new("timeValue", "Time value (time mode)", FieldKind.Int, "10", HideWhenKey: "mode", HideUnlessValue: "time"),
                new("timeUnit", "Time unit (time mode)", FieldKind.Combo, "minute", new[] { "second", "minute", "hour" }, HideWhenKey: "mode", HideUnlessValue: "time"),
            },
            Summarize = s => PropEx.GetString(s.Props, "mode", "count") switch
            {
                "time" => $"For {PropEx.GetInt(s.Props, "timeValue", 10)} {PropEx.GetString(s.Props, "timeUnit", "minute")}",
                "infinite" => "For infinite",
                _ => $"For {PropEx.GetInt(s.Props, "count", 10)} times",
            },
            // structural — the runner iterates Children
        },
        ["randomPackage"] = new StepDefinition
        {
            Label = "Random Package", ColorResourceKey = "StepPackageBrush", DefaultDelay = 0, IsContainer = true, IsScopeContainer = true,
            Fields = new FieldDef[]
            {
                new("title", "Group title (blank = default name)", FieldKind.Text, ""),
                new("mode", "Mode — shuffleAll: every child fires once in a fresh random order · randomSubset: a random count of random children", FieldKind.Combo, "shuffleAll", new[] { "shuffleAll", "randomSubset" }),
                new("minCount", "Min steps per pass (randomSubset)", FieldKind.Int, "1"),
                new("maxCount", "Max steps per pass (randomSubset)", FieldKind.Int, "10"),
            },
            Summarize = s => PropEx.GetString(s.Props, "mode", "shuffleAll") == "randomSubset"
                ? $"Random Package · {PropEx.GetInt(s.Props, "minCount", 1)}–{PropEx.GetInt(s.Props, "maxCount", 10)} of {s.Children.Count} step(s)"
                : $"Random Package · all {s.Children.Count} step(s), shuffled",
            // container — the runner shuffles/picks children per pass (v0.7.8)
        },
        ["splashListener"] = new StepDefinition
        {
            // Load-only compatibility for Build 95-118 documents. The serializer
            // migrates this marker to an explicit Wait For Sound node.
            Label = "Splash Listener (Legacy)", ColorResourceKey = "StepFindImageBrush", DefaultDelay = 0,
            Fields = Array.Empty<FieldDef>(),
            Summarize = _ => "Splash Listener · Timeout از پروفایل Splash · تشخیص/Timeout → پرتاب بعدی",
            // Portable-only marker. PipelinePlanBundle supplies the profile timeout values.
            Commands = _ => new[] { "WPROFILE|splash,18000,22000" },
        },
        ["waitForSound"] = new StepDefinition
        {
            Label = "Wait For Sound", ColorResourceKey = "StepFindImageBrush", DefaultDelay = 0, IsContainer = true, IsScopeContainer = true,
            Fields = new FieldDef[]
            {
                new("title", "Group title (blank = default name)", FieldKind.Text, ""),
                new("calibrationId", "Portable sound-step calibration ID", FieldKind.Combo, "1", new[] { "1", "2" }),
                new("threshold", "Threshold (sensor units — use Calibrate; 0 = saved physical profile)", FieldKind.Int, "90"),
                new("peakMin", "Peak range minimum (0 = calibrated threshold)", FieldKind.Int, "0"),
                new("peakMax", "Peak range maximum", FieldKind.Int, "511"),
                new("soundPriority", "Priority when ranges overlap", FieldKind.Int, "0"),
                new("minDurationMs", "Min duration (ms) — splash is a 1.5–2.2s event, 60–100 is safe (§16.2)", FieldKind.Int, "60"),
                new("cooldownMs", "Cooldown after detection (ms)", FieldKind.Int, "900"),
                new("timeoutMs", "Legacy timeout (ms)", FieldKind.Int, "20000"),
                new("responseRoute", "Detection behavior", FieldKind.Combo, "inline", new[] { "inline", "splash" }),
                new("timeoutMinSec", "Splash timeout minimum (seconds)", FieldKind.Int, "18"),
                new("timeoutMaxSec", "Splash timeout maximum (seconds)", FieldKind.Int, "22"),
                new("onTimeout", "On timeout", FieldKind.Combo, "global", new[] { "global", "stopWithAlarm", "stopQuiet", "continue" }),
                new("armCuePreset", "Buzzer feedback when the sound watch arms", FieldKind.Combo, "off", new[]
                    { "off", "short", "double", "notification", "warning", "success", "error", "rising", "falling", "custom" }),
                new("armCueVolume", "Arm cue volume (1–100%)", FieldKind.Int, "60",
                    HideWhenKey: "armCuePreset", HideWhenValue: "off"),
                new("armCueEnvelope", "Arm cue tone edge", FieldKind.Combo, "smooth",
                    new[] { "sharp", "smooth", "fade-in", "fade-out" },
                    HideWhenKey: "armCuePreset", HideWhenValue: "off"),
                new("armCueTempo", "Arm cue note speed (25–400%; 100 = normal)", FieldKind.Int, "100",
                    HideWhenKey: "armCuePreset", HideWhenValue: "off"),
                new("armCuePattern", "Custom arm cue — freq:duration,pause;...", FieldKind.Text,
                    "880:100,40;1175:150", HideWhenKey: "armCuePreset", HideUnlessValue: "custom"),
                new("insertIfElse", "Insert If-Else (children = Then — heard · Else — not heard; §3.3.1)", FieldKind.Check, "false"),   // v0.9.31
                new("armed", "Armed reaction: board clicks by itself on detection (TRGSND)", FieldKind.Check, "false", HideWhenKey: "insertIfElse", HideWhenValue: "true"),
                new("act", "Armed click button", FieldKind.Combo, "left", new[] { "left", "right", "middle" }, HideWhenKey: "insertIfElse", HideWhenValue: "true"),
                new("reactMin", "React min (ms)", FieldKind.Int, "80", HideWhenKey: "insertIfElse", HideWhenValue: "true"),
                new("reactMax", "React max (ms)", FieldKind.Int, "180", HideWhenKey: "insertIfElse", HideWhenValue: "true"),
                new("holdMin", "Hold min (ms)", FieldKind.Int, "30", HideWhenKey: "insertIfElse", HideWhenValue: "true"),
                new("holdMax", "Hold max (ms)", FieldKind.Int, "90", HideWhenKey: "insertIfElse", HideWhenValue: "true"),
            },
            Summarize = s => PropEx.GetBool(s.Props, "insertIfElse")
                ? $"Sound ID {PropEx.GetInt(s.Props, "calibrationId", 1)} · If Sound ≥{PropEx.GetInt(s.Props, "threshold", 90)} · timeout {PropEx.GetInt(s.Props, "timeoutMs", 20000)}ms · {TimeoutPolicyText(s)}"   // v0.9.31
                : PropEx.GetBool(s.Props, "armed")
                    ? $"Sound ID {PropEx.GetInt(s.Props, "calibrationId", 1)} · trigger ≥{PropEx.GetInt(s.Props, "threshold", 90)} → {PropEx.GetString(s.Props, "act", "left")} click (armed)"
                    : PropEx.GetString(s.Props, "responseRoute", "inline") == "splash"
                        ? $"Catch sound ID {PropEx.GetInt(s.Props, "calibrationId", 2)} · Peak {PropEx.GetInt(s.Props, "peakMin", 0)}–{PropEx.GetInt(s.Props, "peakMax", 511)} · timeout {PropEx.GetInt(s.Props, "timeoutMinSec", 18)}–{PropEx.GetInt(s.Props, "timeoutMaxSec", 22)}s · detected → child response"
                        : $"Sound ID {PropEx.GetInt(s.Props, "calibrationId", 1)} · Peak {PropEx.GetInt(s.Props, "peakMin", 0)}–{PropEx.GetInt(s.Props, "peakMax", 511)} · P{PropEx.GetInt(s.Props, "soundPriority", 0)} · timeout {PropEx.GetInt(s.Props, "timeoutMs", 20000)}ms · {TimeoutPolicyText(s)}",
            Commands = s =>
            {
                int thr = PropEx.GetInt(s.Props, "threshold", 90);
                int minMs = PropEx.GetInt(s.Props, "minDurationMs", 60);
                int timeout = PropEx.GetInt(s.Props, "timeoutMs", 20000);
                bool ifElse = PropEx.GetBool(s.Props, "insertIfElse");   // v0.9.31 — the If/Else structure carries the reaction
                if (!ifElse && PropEx.GetBool(s.Props, "armed"))
                {
                    int act = PropEx.GetString(s.Props, "act", "left") switch { "right" => 2, "middle" => 3, _ => 1 };   // §15.5: act is numeric
                    return new[] { $"TRGSND|{thr},{minMs},{timeout},{act},{PropEx.GetInt(s.Props, "reactMin", 80)},{PropEx.GetInt(s.Props, "reactMax", 180)},{PropEx.GetInt(s.Props, "holdMin", 30)},{PropEx.GetInt(s.Props, "holdMax", 90)}" };
                }
                return new[] { $"WSND|{thr},{minMs},{timeout}" };
            },
        },
        ["waitForLight"] = new StepDefinition
        {
            // v0.9.39 - BH1750 (GY-302/GY-30) light sensor on the Pico I2C bus. Same If/Else
            // machinery and scope visuals as waitForSound (§16.6.5 parity): the range must hold
            // stable for N seconds before it counts, then the plan branches (or the board itself
            // presses a key in armed mode).
            Label = "Wait For Light (BH1750)", ColorResourceKey = "StepFindImageBrush", DefaultDelay = 0, IsContainer = true, IsScopeContainer = true,
            Fields = new FieldDef[]
            {
                new("title", "Group title (blank = default name)", FieldKind.Text, ""),
                new("luxCenter", "Lux centre - press Calibrate while the real screen state is shown", FieldKind.Int, "1250"),
                new("luxTolerance", "Lux tolerance +/- - keep 50 or more lux of dead zone between states", FieldKind.Int, "50"),
                new("stableSec", "Stabilize for N second(s) — may be fractional (e.g. 0.5 = half a second)", FieldKind.Float, "2"),
                new("sampleMode", "Sensor mode - hires: 1 lux / ~120ms per sample · lowres: 4 lux / ~16ms", FieldKind.Combo, "hires", new[] { "hires", "lowres" }),
                new("timeoutMs", "Timeout (ms)", FieldKind.Int, "20000"),
                new("onTimeout", "On timeout", FieldKind.Combo, "global", new[] { "global", "stopWithAlarm", "stopQuiet", "continue" }),
                new("insertIfElse", "Insert If-Else (children = Then - range matched · Else - not matched)", FieldKind.Check, "false"),
                new("armed", "Armed reaction: the board presses the key by itself on detection (TRGLUX)", FieldKind.Check, "false", HideWhenKey: "insertIfElse", HideWhenValue: "true"),
                new("key", "Armed key", FieldKind.Combo, "E", KeyMap.KeyNames.ToArray(), HideWhenKey: "insertIfElse", HideWhenValue: "true"),
                new("reactMin", "React min (ms)", FieldKind.Int, "80", HideWhenKey: "insertIfElse", HideWhenValue: "true"),
                new("reactMax", "React max (ms)", FieldKind.Int, "180", HideWhenKey: "insertIfElse", HideWhenValue: "true"),
                new("holdMin", "Hold min (ms)", FieldKind.Int, "30", HideWhenKey: "insertIfElse", HideWhenValue: "true"),
                new("holdMax", "Hold max (ms)", FieldKind.Int, "90", HideWhenKey: "insertIfElse", HideWhenValue: "true"),
            },
            Summarize = s => PropEx.GetBool(s.Props, "insertIfElse")
                ? $"If Light {LuxLow(s)}-{LuxHigh(s)} lux for {StableSecText(s)}s · timeout {PropEx.GetInt(s.Props, "timeoutMs", 20000)}ms · {TimeoutPolicyText(s)}"
                : PropEx.GetBool(s.Props, "armed")
                    ? $"Light trigger {LuxLow(s)}-{LuxHigh(s)} lux -> key {PropEx.GetString(s.Props, "key", "E")} (armed)"
                    : $"Wait for light {LuxLow(s)}-{LuxHigh(s)} lux for {StableSecText(s)}s · timeout {PropEx.GetInt(s.Props, "timeoutMs", 20000)}ms · {TimeoutPolicyText(s)}",
            Commands = s =>
            {
                int lo = LuxLow(s), hi = LuxHigh(s);
                int stableMs = Math.Max(0, (int)(PropEx.GetDouble(s.Props, "stableSec", 2) * 1000));
                int timeout = PropEx.GetInt(s.Props, "timeoutMs", 20000);
                int mode = PropEx.GetString(s.Props, "sampleMode", "hires") == "lowres" ? 1 : 0;
                bool ifElse = PropEx.GetBool(s.Props, "insertIfElse");
                if (!ifElse && PropEx.GetBool(s.Props, "armed"))
                    return new[] { $"TRGLUX|{lo},{hi},{stableMs},{timeout},{mode},{LuxVk(s)},{PropEx.GetInt(s.Props, "reactMin", 80)},{PropEx.GetInt(s.Props, "reactMax", 180)},{PropEx.GetInt(s.Props, "holdMin", 30)},{PropEx.GetInt(s.Props, "holdMax", 90)}" };
                return new[] { $"WLUX|{lo},{hi},{stableMs},{timeout},{mode}" };
            },
        },
        ["findImage"] = new StepDefinition
        {
            Label = "Find Image On Screen", ColorResourceKey = "StepFindImageBrush", DefaultDelay = 55, IsContainer = true, IsScopeContainer = true,
            Fields = new FieldDef[]
            {
                new("title", "Group title (blank = default name)", FieldKind.Text, ""),
                new("pictures", "Picture file(s) — one per line (OR logic)", FieldKind.Multiline, ""),
                new("similarity", "Similarity %", FieldKind.Int, "75"),
                new("searchScope", "Where to search", FieldKind.Combo, "entireScreen", new[] { "entireScreen", "fromCursor", "region" }),
                new("x", "Region X (scope=region)", FieldKind.Int, "0"),
                new("y", "Region Y", FieldKind.Int, "0"),
                new("w", "Region width", FieldKind.Int, "400"),
                new("h", "Region height", FieldKind.Int, "300"),
                new("neverTimeout", "Never timeout", FieldKind.Check, "false"),
                new("timeoutValue", "Timeout", FieldKind.Int, "3"),
                new("timeoutUnit", "Timeout unit", FieldKind.Combo, "second", new[] { "ms", "second", "minute", "hour" }),
                new("onFound", "When found", FieldKind.Combo, "moveAndClick", new[] { "moveAndClick", "moveOnly", "clickRestoreCursor", "none" }),
                new("onTimeout", "On timeout", FieldKind.Combo, "continue", new[] { "stopWithTip", "stopSilent", "continue" }),
                new("insertIfElse", "Insert If-Else (children = Then branch; §3.3.1)", FieldKind.Check, "false"),
                new("humanMove", "Humanized mouse approach (WindMouse path + pauses · off = instant jump)", FieldKind.Check, "true"),
                new("pauseBeforeMin", "Approach pause BEFORE move — min (ms)", FieldKind.Int, "60", HideWhenKey: "humanMove", HideWhenValue: "false"),
                new("pauseBeforeMax", "Approach pause BEFORE move — max (ms)", FieldKind.Int, "220", HideWhenKey: "humanMove", HideWhenValue: "false"),
                new("pauseAfterMin", "Settle pause AFTER arrival — min (ms)", FieldKind.Int, "80", HideWhenKey: "humanMove", HideWhenValue: "false"),
                new("pauseAfterMax", "Settle pause AFTER arrival — max (ms)", FieldKind.Int, "280", HideWhenKey: "humanMove", HideWhenValue: "false"),
                new("midPauseChance", "Mid-path hesitation chance % (0 = off)", FieldKind.Int, "6", HideWhenKey: "humanMove", HideWhenValue: "false"),
                new("midPauseMin", "Hesitation pause — min (ms)", FieldKind.Int, "80", HideWhenKey: "humanMove", HideWhenValue: "false"),
                new("midPauseMax", "Hesitation pause — max (ms)", FieldKind.Int, "250", HideWhenKey: "humanMove", HideWhenValue: "false"),
                new("overshootChance", "Overshoot & correct chance %", FieldKind.Int, "12", HideWhenKey: "humanMove", HideWhenValue: "false"),
                new("curveMinPct", "Path curvature MIN % · 0–100 bow · 101–200 true arc", FieldKind.Int, "20", HideWhenKey: "humanMove", HideWhenValue: "false"),
                new("curveMaxPct", "Path curvature MAX % · modulated smoothly during the move", FieldKind.Int, "40", HideWhenKey: "humanMove", HideWhenValue: "false"),
                new("moveTimeMin", "Approach move duration — min (ms) · 0/0 = speed-based (Options)", FieldKind.Int, "0", HideWhenKey: "humanMove", HideWhenValue: "false"),
                new("moveTimeMax", "Approach move duration — max (ms)", FieldKind.Int, "0", HideWhenKey: "humanMove", HideWhenValue: "false"),
            },
            Summarize = s => {
                var ifElse = PropEx.GetBool(s.Props, "insertIfElse");
                // v0.9.42 - original AMK naming: without a condition this step is only a
                // picture search (جستجو برای تصویر); with insertIfElse it becomes the If head.
                var head = ifElse ? "If image found" : "Search for image";
                return $"{head} · {PropEx.GetInt(s.Props, "similarity", 75)}% · {PropEx.GetString(s.Props, "searchScope", "entireScreen")} · timeout {(PropEx.GetBool(s.Props, "neverTimeout") ? "never" : PropEx.GetInt(s.Props, "timeoutValue", 3) + " " + PropEx.GetString(s.Props, "timeoutUnit", "second"))}";
            },
            // PC-side vision — executed by RunEngine via VisionService
        },
        ["parallelGroup"] = new StepDefinition
        {
            Label = "Parallel Group", ColorResourceKey = "StepFlowBrush", DefaultDelay = 0, IsContainer = true, IsScopeContainer = true,
            Fields = new FieldDef[]
            {
                new("title", "Group title (blank = default name)", FieldKind.Text, ""),
            },
            Summarize = s => $"⚡ Parallel Group · {s.Children.Count} step(s) run simultaneously · next step waits for the longest",
            // v0.9.15 — structural: the runner executes children concurrently and joins on the longest
        },
        ["comment"] = new StepDefinition
        {
            Label = "Comment", ColorResourceKey = "StepDelayBrush", DefaultDelay = 0,
            Fields = new FieldDef[] { new("text", "Comment", FieldKind.Multiline, "") },
            // v0.9.55 - the structural markers (next / else / end if) render BARE and lowercase
            // instead of "# Next"/"# Else": they are flow rails, not comments (user request).
            Summarize = s => MarkerOrComment(PropEx.GetString(s.Props, "text").Replace('\n', ' ')),
        },
        ["rawCommand"] = new StepDefinition
        {
            Label = "Raw Command", ColorResourceKey = "StepDelayBrush", DefaultDelay = 0,
            Fields = new FieldDef[] { new("cmd", "Raw board command (pipe separator — e.g. WSND|90,60,20000)", FieldKind.Text, "PING") },
            Summarize = s => "Raw: " + PropEx.GetString(s.Props, "cmd", "PING"),
            Commands = s => new[] { PropEx.GetString(s.Props, "cmd", "PING") },
        },
        ["openFile"] = new StepDefinition
        {
            Label = "Open File / Program", ColorResourceKey = "StepFlowBrush", DefaultDelay = 500,
            Fields = new FieldDef[]
            {
                new("path", "File path", FieldKind.Text, "", BrowseFilter: "All files|*.*"),
                new("args", "Arguments (optional)", FieldKind.Text, ""),
                new("windowState", "Window state", FieldKind.Combo, "normal", new[] { "normal", "maximized", "minimized" }),
            },
            Summarize = s => "Open: " + (PropEx.GetString(s.Props, "path", "") ?? ""),
            // PC-side — no board command; ScriptGenerator emits Start-Process
        },
        ["buzzer"] = new StepDefinition
        {
            Label = "Buzzer Beep", ColorResourceKey = "StepFlowBrush", DefaultDelay = 0,
            Fields = new FieldDef[]
            {
                new("preset", "Tone pattern", FieldKind.Combo, "short", new[]
                    { "short", "double", "notification", "warning", "success", "error", "rising", "falling", "custom" }),
                new("volume", "Volume (1–100%)", FieldKind.Int, "80"),
                new("envelope", "Tone edge", FieldKind.Combo, "smooth",
                    new[] { "sharp", "smooth", "fade-in", "fade-out" }),
                new("tempo", "Note speed (25–400%; 100 = normal)", FieldKind.Int, "100"),
                new("pattern", "Custom sequence — freq:duration,pause;... (example 900:150,80;1200:250)", FieldKind.Text,
                    "900:150,80;1200:250", HideWhenKey: "preset", HideUnlessValue: "custom"),
            },
            Summarize = s => $"Buzzer {PropEx.GetInt(s.Props, "volume", 100)}% · " + (PropEx.GetString(s.Props, "preset", "short") == "custom"
                ? PropEx.GetString(s.Props, "pattern", "900:150")
                : PropEx.GetString(s.Props, "preset", "short")),
            Commands = s => BuildBuzzerCommands(s.Props),
        },
        ["playAudio"] = new StepDefinition
        {
            Label = "Play Audio", ColorResourceKey = "StepFindImageBrush", DefaultDelay = 0,
            Fields = new FieldDef[]
            {
                new("path", "Audio file (wav / mp3)", FieldKind.Text, "", BrowseFilter: "Audio files|*.wav;*.mp3|All files|*.*"),
                new("loop", "Loop until manual stop", FieldKind.Check, "false"),
                new("outputDevice", "Output device — pick from the list (-1=auto)", FieldKind.AudioDevice, "-1"),   // v0.9.43 — dropdown, not a bare index
            },
            Summarize = s =>
            {
                var p = PropEx.GetString(s.Props, "path", "");
                var label = string.IsNullOrWhiteSpace(p) ? "Play audio" : System.IO.Path.GetFileName(p);
                var dev = PropEx.GetInt(s.Props, "outputDevice", -1);
                var devNote = dev >= 0 ? $" [#{dev}]" : "";
                return (PropEx.GetBool(s.Props, "loop") ? "🔁 " : "▶ ") + label + devNote;
            },
            // PC-side — uses NAudio WaveOutEvent with per-step outputDevice index (§v0.9.33)
        },
        ["runExe"] = new StepDefinition
        {
            Label = "Run Exe", ColorResourceKey = "StepPackageBrush", DefaultDelay = 500,
            Fields = new FieldDef[]
            {
                new("path", "Executable path", FieldKind.Text, "", BrowseFilter: "Executables|*.exe|All files|*.*"),
                new("args", "Arguments (optional)", FieldKind.Text, ""),
                new("windowState", "Window state", FieldKind.Combo, "normal", new[] { "normal", "maximized", "minimized" }),
            },
            Summarize = s => "Run: " + (System.IO.Path.GetFileName(PropEx.GetString(s.Props, "path", "")) ?? ""),
            // PC-side — Process.Start (like openFile but with pink category for easy visual distinction)
        },
        ["playScript"] = new StepDefinition
        {
            Label = "Play Script (.amsj)", ColorResourceKey = "StepFlowBrush", DefaultDelay = 0,
            Fields = new FieldDef[]
            {
                new("path", ".amsj script path", FieldKind.Text, "", BrowseFilter: "AMS scripts|*.amsj|All files|*.*"),
            },
            Summarize = s => "Play: " + (PropEx.GetString(s.Props, "path", "") ?? ""),
            // PC-side — loads .amsj via DocumentService and runs (v0.9.32).
            // .amk paths still work via AmkImporter (backward compat).
        },
        ["label"] = new StepDefinition
        {
            Label = "Insert Label", ColorResourceKey = "StepFlowBrush", DefaultDelay = 0,
            Fields = new FieldDef[]
            {
                new("label", "Label name (jump target for Go To Label)", FieldKind.Text, "label1"),
            },
            Summarize = s => "🏷 " + PropEx.GetString(s.Props, "label", "label"),
            // Inert marker — the runner just passes through it; "gotoLabel" jumps here.
        },
        ["gotoLabel"] = new StepDefinition
        {
            Label = "Go To Label", ColorResourceKey = "StepFlowBrush", DefaultDelay = 0,
            Fields = new FieldDef[]
            {
                // options are injected at dialog time from the script's current labels (MainViewModel)
                new("label", "Label to jump to", FieldKind.EditableCombo, "", Array.Empty<string>()),
            },
            Summarize = s => "Go To Label → " + PropEx.GetString(s.Props, "label"),
            // App-side jump — RunEngine throws GotoSignal; the level holding the label catches it.
        },
    };

    /// <summary>Builds the GP6 passive-buzzer contract. Custom syntax is
    /// freq:duration,pause;freq:duration (Hz/ms); pause is optional after each tone.</summary>
    public static IReadOnlyList<string> BuildBuzzerCommands(IReadOnlyDictionary<string, object?> p)
    {
        string preset = PropEx.GetString(p, "preset", "short");
        string pattern = preset switch
        {
            "short" => "1000:180",
            "double" => "1000:140,100;1000:140",
            "notification" => "880:110,45;1175:170",
            "warning" => "700:180,90;700:180,90;700:300",
            "success" => "900:120,70;1300:220",
            "error" => "440:180,70;330:240",
            "rising" => "523:90,35;659:90,35;784:160",
            "falling" => "784:90,35;659:90,35;523:160",
            "custom" => PropEx.GetString(p, "pattern", "900:150"),
            _ => throw new FormatException("unknown buzzer preset '" + preset + "'"),
        };
        int volume = PropEx.GetInt(p, "volume", 100);
        if (volume is < 1 or > 100) throw new FormatException("buzzer volume must be 1..100 percent");
        int tempo = PropEx.GetInt(p, "tempo", 100);
        if (tempo is < 25 or > 400) throw new FormatException("buzzer note speed must be 25..400 percent");
        string envelope = PropEx.GetString(p, "envelope", "sharp").Trim().ToLowerInvariant();
        if (envelope is not ("sharp" or "smooth" or "fade-in" or "fade-out"))
            throw new FormatException("unknown buzzer envelope '" + envelope + "'");
        var commands = new List<string>();
        foreach (var raw in pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sides = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            var timing = sides.Length == 2 ? sides[1].Split(',', StringSplitOptions.TrimEntries) : Array.Empty<string>();
            if (sides.Length != 2 || timing.Length is < 1 or > 2
                || !int.TryParse(sides[0], out int freq) || !int.TryParse(timing[0], out int duration)
                || (timing.Length == 2 && !int.TryParse(timing[1], out _)))
                throw new FormatException("buzzer pattern must be freq:duration,pause;... (Hz/ms)");
            int pause = timing.Length == 2 ? int.Parse(timing[1]) : 0;
            if (freq is < 30 or > 20000) throw new FormatException("buzzer frequency must be 30..20000 Hz");
            if (duration <= 0 || pause < 0) throw new FormatException("buzzer duration must be positive and pause non-negative");
            duration = Math.Max(1, (int)Math.Round(duration * 100.0 / tempo, MidpointRounding.AwayFromZero));
            if (pause > 0)
                pause = Math.Max(1, (int)Math.Round(pause * 100.0 / tempo, MidpointRounding.AwayFromZero));
            commands.Add(volume == 100 && envelope == "sharp"
                ? $"BEEP|{freq},{duration}"
                : $"BEEP|{freq},{duration},{volume},{envelope}");
            if (pause > 0) commands.Add($"DLY|{pause}");
        }
        if (commands.Count == 0) throw new FormatException("buzzer pattern is empty");
        return commands;
    }

    public static IReadOnlyList<string> BuildArmBuzzerCommands(IReadOnlyDictionary<string, object?> p)
    {
        string preset = PropEx.GetString(p, "armCuePreset", "off");
        if (preset == "off") return Array.Empty<string>();
        return BuildBuzzerCommands(new Dictionary<string, object?>
        {
            ["preset"] = preset,
            ["volume"] = PropEx.GetInt(p, "armCueVolume", 60),
            ["envelope"] = PropEx.GetString(p, "armCueEnvelope", "smooth"),
            ["tempo"] = PropEx.GetInt(p, "armCueTempo", 100),
            ["pattern"] = PropEx.GetString(p, "armCuePattern", "880:100,40;1175:150"),
        });
    }

    public static StepDefinition Get(string type) => Defs[type];
    public static string Summarize(StepNode s)
        => ApplyCustomTitle(s, Defs.TryGetValue(s.Type, out var d) ? d.Summarize(s) : s.Type);

    /// <summary>v0.9.55 - every container head can be renamed through its "title" field. The type
    /// icon stays in front of the custom name so the block's nature is never lost, and the tail
    /// after the first " · " separator (step counts, modes) is preserved.</summary>
    private static readonly Dictionary<string, string> ContainerIcons = new(StringComparer.Ordinal)
    {
        ["forLoop"] = "🔁",
        ["parallelGroup"] = "⚡",
        ["randomPackage"] = "🎲",
        ["waitForSound"] = "🔊",
        ["waitForLight"] = "💡",
        ["findImage"] = "🔍",
    };

    public static string ContainerIcon(string type) => ContainerIcons.TryGetValue(type, out var i) ? i : "";

    private static string ApplyCustomTitle(StepNode s, string summary)
    {
        if (!ContainerIcons.TryGetValue(s.Type, out var icon)) return summary;
        var title = PropEx.GetString(s.Props, "title").Trim();
        if (title.Length == 0) return summary;
        var sep = summary.IndexOf(" · ", StringComparison.Ordinal);
        var tail = sep >= 0 ? summary[sep..] : "";
        return icon + " " + title + tail;
    }

    /// <summary>v0.9.55 - "Next", "Else..." and "End If" are structural markers, not comments.</summary>
    public static bool IsStructuralMarker(string text)
    {
        var t = text.Trim();
        return t.Equals("Next", StringComparison.OrdinalIgnoreCase)
            || t.Equals("End If", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Else", StringComparison.OrdinalIgnoreCase);
    }

    private static string MarkerOrComment(string text)
        => IsStructuralMarker(text) ? text.Trim().ToLowerInvariant() : "# " + text;
    public static string ColorKey(string type) => Defs.TryGetValue(type, out var d) ? d.ColorResourceKey : "StepDelayBrush";
    public static IReadOnlyList<string> GetCommands(StepNode s)
        => Defs.TryGetValue(s.Type, out var d) && d.Commands is not null ? d.Commands(s) : Array.Empty<string>();

    /// <summary>v0.9.46 — per-step keyboard routing. "default" emits the legacy command and lets
    /// code.py's global KBD_ON_ARM decide; explicit choices add a Pico-understood wire envelope.
    /// PC-local pseudo commands (CLIPBOARD/DLY) stay unwrapped.</summary>
    public static string RouteKeyboardCommand(StepNode s, string command)
    {
        if (s.Type is not ("keystroke" or "typeText" or "keyDown" or "keyUp")) return command;
        if (!(command.StartsWith("KTEXT|", StringComparison.Ordinal)
              || command.StartsWith("KCOMBO|", StringComparison.Ordinal)
              || command.StartsWith("KDOWN|", StringComparison.Ordinal)
              || command.StartsWith("KUP|", StringComparison.Ordinal))) return command;
        return PropEx.GetString(s.Props, "keyboardBoard", "default") switch
        {
            "pico" => "KBDPICO|" + command,
            "promicro" => "KBDARM|" + command,
            _ => command,
        };
    }

    private static string KeyboardBoardHint(IReadOnlyDictionary<string, object?> p)
        => PropEx.GetString(p, "keyboardBoard", "default") switch
        {
            "pico" => " · board Pico",
            "promicro" => " · board Pro Micro",
            _ => "",
        };

    /// <summary>v0.9.16 — deterministic test path. Swaps in a seeded Random and holds _rngSync
    /// for the WHOLE call so a Parallel Group branch cannot swap the seed mid-run.
    /// Safe nesting: lock (Monitor) is reentrant on the same thread, so the inner NextInt/
    /// RandRange locks do NOT deadlock. finally guarantees the default RNG is restored even on exceptions.</summary>
    public static IReadOnlyList<string> GetCommands(StepNode s, int seed)
    {
        lock (_rngSync)
        {
            var prev = Rng;
            Rng = new Random(seed);
            try { return GetCommands(s); }
            finally { Rng = prev; }
        }
    }

    private static string Title(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>v0.7.8 / firmware 1.7 — optional random press-hold range appended to
    /// MCLICK/KCOMBO as ",hmin,hmax". Empty when unset → firmware 1.6 stays compatible.</summary>
    private static string HoldSuffix(IReadOnlyDictionary<string, object?> p)
    {
        int hmin = PropEx.GetInt(p, "holdMin"), hmax = PropEx.GetInt(p, "holdMax");
        if (hmax <= 0) return "";
        if (hmax < hmin) (hmin, hmax) = (hmax, hmin);
        return $",{hmin},{hmax}";
    }

    private static string HoldText(IReadOnlyDictionary<string, object?> p)
    {
        int hmin = PropEx.GetInt(p, "holdMin"), hmax = PropEx.GetInt(p, "holdMax");
        if (hmax <= 0) return "";
        if (hmax < hmin) (hmin, hmax) = (hmax, hmin);
        return $" · hold {hmin}–{hmax}ms";
    }

    private static int KeyVk(IReadOnlyDictionary<string, object?> p)
    {
        var name = PropEx.GetString(p, "key", "F4");
        if (!KeyMap.VK.TryGetValue(name, out var vk)) throw new InvalidOperationException($"Unknown key: {name}");
        return vk;
    }

    private static string ComboText(IReadOnlyDictionary<string, object?> p)
    {
        var parts = new List<string>();
        foreach (var (name, key) in new[] { ("Ctrl", "modCtrl"), ("Shift", "modShift"), ("Alt", "modAlt"), ("Win", "modWin") })
            if (PropEx.GetBool(p, key)) parts.Add(name);
        parts.Add(PropEx.GetString(p, "key", "F4"));
        return string.Join("+", parts);
    }

    private static string ComboVk(IReadOnlyDictionary<string, object?> p)
    {
        var vks = new List<int>();
        foreach (var (name, key) in new[] { ("Ctrl", "modCtrl"), ("Shift", "modShift"), ("Alt", "modAlt"), ("Win", "modWin") })
            if (PropEx.GetBool(p, key)) vks.Add(KeyMap.Modifiers[name]);
        vks.Add(KeyVk(p));
        return string.Join("+", vks);
    }

    /// <summary>v0.9.24 — reverse-lookup: find the step type whose shared Fields array is the
    /// given reference (lets the generic StepDialog apply step-specific Persian overrides
    /// without changing its call sites). Extracted for TestRunner.</summary>
    public static string? FindTypeByFields(IReadOnlyList<FieldDef> fields)
        => Defs.FirstOrDefault(kv => ReferenceEquals(kv.Value.Fields, fields)).Key;

    /// <summary>v0.9.25 — read-only view of all definitions (TestRunner's Persian-coverage sweep).</summary>
    public static IReadOnlyDictionary<string, StepDefinition> All => Defs;
    /// <summary>v0.9.36 — quick type-only check for whether a definition is a container (accepts children).</summary>
    public static bool IsContainer(string type) => Defs.TryGetValue(type, out var d) && d.IsContainer;

    /// <summary>v0.9.42 - the three If-capable steps (findImage / waitForSound / waitForLight)
    /// are containers ONLY while insertIfElse is on. Without a condition they are plain steps:
    /// nothing may be nested under them and they own no vein/scope. (User report: three steps
    /// were indented under a conditionless Find Image, and the same happened under Wait For Light.)</summary>
    public static bool IsConditionalContainer(string type)
        => type is "findImage" or "waitForSound" or "waitForLight";

    public static bool OpensIfElse(StepNode n)
        => IsConditionalContainer(n.Type) && PropEx.GetBool(n.Props, "insertIfElse");

    public static bool IsCatchResponseContainer(StepNode n)
        => n.Type == "waitForSound"
           && PropEx.GetString(n.Props, "responseRoute", "inline") == "splash";

    /// <summary>True when this concrete node may adopt child steps.</summary>
    public static bool AcceptsChildren(StepNode n)
        => Get(n.Type).IsContainer
           && (!IsConditionalContainer(n.Type) || OpensIfElse(n) || IsCatchResponseContainer(n));

    /// <summary>True when this concrete node draws a scope band / vein in the flat list.</summary>
    public static bool IsScopeContainerNode(StepNode n)
        => Get(n.Type).IsScopeContainer
           && (!IsConditionalContainer(n.Type) || OpensIfElse(n) || IsCatchResponseContainer(n));

    /// <summary>v0.9.23 — session typing fallbacks (human-calibrated defaults 80/220 ms,
    /// personalised by Options → "Measure from my hand"). TypeTextCommands uses them only
    /// when a step omits hmin/hmax (imports, hand-edited files).</summary>
    public static int TypingFallbackMinMs = 80;
    public static int TypingFallbackMaxMs = 220;

    /// <summary>
    /// KTEXT is ASCII-only (firmware rejects chars outside 32-126) and frames are
    /// line-based — so text is split on newlines (Enter = KCOMBO|13) and chunked
    /// to 60 chars (MAX_PT = 96 on the board). Clipboard mode = PC sets the
    /// clipboard, board only sends Ctrl+V (§5.5.6). Secret steps always use the
    /// clipboard and are masked in logs by the runner (§17.6).
    /// </summary>
    public static IReadOnlyList<string> TypeTextCommands(IReadOnlyDictionary<string, object?> p)
    {
        var text = PropEx.GetString(p, "text");
        bool secret = PropEx.GetBool(p, "secret");
        string mode = PropEx.GetString(p, "mode", "keystrokes");
        // v0.9.2 — auto-fallback to clipboard mode for non-ASCII text (Persian/Arabic/CJK etc.)
        // KTEXT firmware only accepts 0x20–0x7E; throwing forced the user to manually
        // re-save the step in clipboard mode. Now we do it transparently.
        // v0.9.11 — newline is NOT a clipboard trigger: multi-line keystrokes text is typed
        // line-by-line with Enter (KCOMBO|13) between lines, keeping the human per-key timing.
        // The v0.9.2 regex also caught '\n' and silently demoted every multi-line step to paste.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        bool useClipboard = secret || mode == "clipboard" || Regex.IsMatch(normalized, @"[^\x20-\x7E\n]");
        if (useClipboard)
            return new[] { "CLIPBOARD:" + normalized, "KCOMBO|162+86" };
        int hmin = PropEx.GetInt(p, "hmin", TypingFallbackMinMs), hmax = PropEx.GetInt(p, "hmax", TypingFallbackMaxMs);
        int wmin = PropEx.GetInt(p, "wmin"), wmax = PropEx.GetInt(p, "wmax");
        // v0.9.11 — extra human layers, all range-based and re-rolled per occurrence:
        // punctuation pauses (after . , ! ? ; :), occasional thinking pauses between words,
        // and typo + backspace correction (KTEXT slip → DLY notice → KCOMBO|8 → retype).
        int pmin = Math.Max(0, PropEx.GetInt(p, "pmin")), pmax = Math.Max(0, PropEx.GetInt(p, "pmax"));
        if (pmax < pmin) (pmin, pmax) = (pmax, pmin);
        int thinkChance = Math.Clamp(PropEx.GetInt(p, "thinkChance"), 0, 100);
        int thinkMin = Math.Max(0, PropEx.GetInt(p, "thinkMin", 800));
        int thinkMax = Math.Max(0, PropEx.GetInt(p, "thinkMax", 2200));
        if (thinkMax < thinkMin) (thinkMin, thinkMax) = (thinkMax, thinkMin);
        // The persisted typoEveryMin/Max names mean what they say: choose the
        // eligible-character distance to the next corrected slip.  This works
        // for one-word login/password text without turning 7..12 into 7..12
        // errors in a short string.
        int typoCharMin = PropEx.GetInt(p, "typoEveryMin");
        int typoCharMax = PropEx.GetInt(p, "typoEveryMax");
        if (typoCharMax < typoCharMin) (typoCharMin, typoCharMax) = (typoCharMax, typoCharMin);
        typoCharMin = Math.Max(1, typoCharMin);
        typoCharMax = Math.Max(0, typoCharMax);
        int typoChance = Math.Clamp(PropEx.GetInt(p, "typoChance"), 0, 100);
        bool typoCharMode = typoCharMax > 0;
        // v0.9.13 — word pause PROBABILITY: 100 = after every word (the old metronome feel the
        // user reported: a pause after every space). 40–70 looks human. Files saved before this
        // field existed have no key and keep 100, so their behavior is unchanged.
        int wordPauseChance = p.ContainsKey("wordPauseChance")
            ? Math.Clamp(PropEx.GetInt(p, "wordPauseChance", 100), 0, 100)
            : 100;
        // Word segmentation is needed by word pauses AND every v0.9.11+ layer; with all of them
        // off, keep the legacy whole-line 60-char chunking byte-identical for old files.
        bool wordMode = wmax > 0 || pmax > 0 || thinkChance > 0 || typoChance > 0 || typoCharMode;
        var cmds = new List<string>();
        var lines = normalized.Split('\n');
        var lineWords = lines.Select(line => Regex.Split(line, @"\s+").Where(w => w.Length > 0).ToArray()).ToArray();
        var typoPositions = new Dictionary<(int Line, int Word), List<int>>();
        if (typoCharMode)
        {
            var candidates = new List<(int Line, int Word, int Pos)>();
            for (int li = 0; li < lineWords.Length; li++)
                for (int wi = 0; wi < lineWords[li].Length; wi++)
                    for (int pos = 0; pos < lineWords[li][wi].Length; pos++)
                        if ("1234567890qwertyuiopasdfghjklzxcvbnm".IndexOf(
                                char.ToLowerInvariant(lineWords[li][wi][pos])) >= 0)
                            candidates.Add((li, wi, pos));
            int cursor = RandRange(typoCharMin, typoCharMax) - 1;
            while (cursor < candidates.Count)
            {
                var candidate = candidates[cursor];
                var key = (candidate.Line, candidate.Word);
                if (!typoPositions.TryGetValue(key, out var positions))
                    typoPositions[key] = positions = new List<int>();
                positions.Add(candidate.Pos);
                cursor += RandRange(typoCharMin, typoCharMax);
            }
        }
        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            if (wordMode)
            {
                var words = lineWords[li];
                // v0.9.13 — stream merging: text accumulates in `pending` and is chunked ONLY at
                // real pause points (word/thinking/punctuation/typo). Previously every word was
                // its own KTEXT command, so the serial round-trip after each word's space made a
                // constant inter-command gap. When no pause fires, words flow as ONE stream and
                // the space is just another keystroke with the normal per-key delay.
                string pending = "";
                void FlushPending()
                {
                    for (int i = 0; i < pending.Length; i += 60)
                        cmds.Add($"KTEXT|{hmin},{hmax},{pending.Substring(i, Math.Min(60, pending.Length - i))}");
                    pending = "";
                }
                for (int wi = 0; wi < words.Length; wi++)
                {
                    // v0.9.0 bug fix — the space between words was never typed, so
                    // "hello world" arrived as "helloworld". Fold the space into the word
                    // chunk BEFORE the inter-word pause (humans pause after the space).
                    string tail = wi < words.Length - 1 ? " " : "";
                    string typed = words[wi] + tail;

                    // Character-interval mode picks ordered eligible characters across TYPE.
                    // Each slip uses an adjacent QWERTY key, pauses, Backspaces, and resumes from
                    // the correct character. Multiple slips can occur in the same one-word text.
                    if (typoPositions.TryGetValue((li, wi), out var selected))
                    {
                        int start = 0;
                        foreach (int pos in selected)
                        {
                            if (QwertyNeighbor(words[wi][pos]) is not char wrong) continue;
                            FlushPending();
                            string slip = words[wi][start..pos] + wrong;
                            for (int i = 0; i < slip.Length; i += 60)
                                cmds.Add($"KTEXT|{hmin},{hmax},{slip.Substring(i, Math.Min(60, slip.Length - i))}");
                            cmds.Add($"DLY|{RandRange(Math.Max(hmax, 120), hmax * 2 + 200)}");   // noticed the slip
                            cmds.Add("KCOMBO|8");                                                // Backspace
                            cmds.Add($"DLY|{RandRange(hmin, hmax)}");
                            start = pos;                                                         // correct char is next
                        }
                        typed = words[wi][start..] + tail;
                    }
                    else if (!typoCharMode && typoChance > 0 && NextInt(100) < typoChance
                             && words[wi].Length >= 2 && words[wi].Length <= 60)
                    {
                        int pos = 1 + NextInt(words[wi].Length - 1);
                        if (QwertyNeighbor(words[wi][pos]) is char wrong)
                        {
                            FlushPending();
                            cmds.Add($"KTEXT|{hmin},{hmax},{words[wi][..pos]}{wrong}");
                            cmds.Add($"DLY|{RandRange(Math.Max(hmax, 120), hmax * 2 + 200)}");
                            cmds.Add("KCOMBO|8");
                            cmds.Add($"DLY|{RandRange(hmin, hmax)}");
                            typed = words[wi][pos..] + tail;
                        }
                    }

                    // v0.9.13 — punctuation pauses: split AFTER . , ! ? ; : and rest a moment.
                    // Text flows into `pending`; only a real pause flushes the stream to KTEXT.
                    var segs = pmax > 0 ? SplitAfterPunctuation(typed) : new List<string> { typed };
                    for (int si = 0; si < segs.Count; si++)
                    {
                        pending += segs[si];
                        if (si < segs.Count - 1)
                        {
                            FlushPending();
                            cmds.Add($"DLY|{RandRange(pmin, pmax)}");
                        }
                    }
                    if (wi < words.Length - 1)
                    {
                        // v0.9.13 — the word pause fires only with wordPauseChance% per word;
                        // without it the next word simply continues the same stream (no fixed gap).
                        if (wmax > 0 && NextInt(100) < wordPauseChance)
                        {
                            FlushPending();
                            cmds.Add($"DLY|{RandRange(wmin, wmax)}");
                        }
                        // v0.9.11 — occasional thinking pause between words (like the mouse
                        // long-break layer: rare, longer, clearly human).
                        if (thinkChance > 0 && thinkMax > 0 && NextInt(100) < thinkChance)
                        {
                            FlushPending();
                            cmds.Add($"DLY|{RandRange(thinkMin, thinkMax)}");
                        }
                    }
                }
                FlushPending();   // v0.9.13 — emit whatever the stream accumulated at line end
            }
            else
            {
                for (int i = 0; i < line.Length; i += 60)
                    cmds.Add($"KTEXT|{hmin},{hmax},{line.Substring(i, Math.Min(60, line.Length - i))}");
            }
            if (li < lines.Length - 1) cmds.Add("KCOMBO|13");   // Enter between lines
        }
        return cmds;
    }

    /// <summary>Inclusive random range; swapped bounds are tolerated, equal bounds are fixed.</summary>
    private static int RandRange(int min, int max)
    {
        if (max < min) (min, max) = (max, min);
        return max <= min ? Math.Max(0, min) : min + NextInt(max - min + 1);   // v0.9.15 — locked
    }

    /// <summary>v0.9.11 — splits after sentence punctuation, keeping the mark at the end of its
    /// own segment; never splits after the FINAL character (that pause belongs to the word/line
    /// boundary, not to the punctuation layer).</summary>
    private static List<string> SplitAfterPunctuation(string s)
    {
        var outp = new List<string>();
        const string marks = ".,!?;:";
        int start = 0;
        for (int i = 0; i < s.Length - 1; i++)
            if (marks.IndexOf(s[i]) >= 0)
            {
                outp.Add(s.Substring(start, i + 1 - start));
                start = i + 1;
            }
        if (start < s.Length) outp.Add(s[start..]);
        if (outp.Count == 0 && s.Length > 0) outp.Add(s);
        return outp;
    }

    /// <summary>v0.9.11 — a physically adjacent QWERTY key (the slip humans actually make).
    /// Letter case is preserved; characters with no neighbor (punctuation, etc.) return null
    /// so no typo is simulated for them.</summary>
    private static char? QwertyNeighbor(char c)
    {
        string[] rows = { "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm" };
        char lower = char.ToLowerInvariant(c);
        foreach (var row in rows)
        {
            int i = row.IndexOf(lower);
            if (i < 0) continue;
            int j = i + (NextInt(2) == 0 ? -1 : 1);
            if (j < 0 || j >= row.Length) j = i == 0 ? 1 : i - 1;
            char n = row[j];
            return char.IsUpper(c) ? char.ToUpperInvariant(n) : n;
        }
        return null;
    }
}
