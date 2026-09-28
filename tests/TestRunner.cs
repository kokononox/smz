using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ams.UI.Models;
using Ams.UI.Services;
using Ams.UI.ViewModels;

class TestRunner
{
    static int passed = 0, failed = 0;
    // v0.9.58c — wall-clock ceilings are machine properties and flake on shared CI runners
    static readonly bool IsCi = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

    static void Assert(bool cond, string msg)
    {
        if (cond) { Console.WriteLine($"PASS: {msg}"); passed++; }
        else { Console.WriteLine($"FAIL: {msg}"); failed++; }
    }

    [STAThread]   // v0.8.0 — the accordion test builds a MainViewModel (WPF brushes)
    static void Main()
    {
        // ── Step 1: StepDefinitions exist ────────────────────────────
        var errorDef = StepDefinitions.Get("raiseError");
        Assert(errorDef.Label == "Raise Error / Stop Macro" && errorDef.Fields.Any(f => f.Key == "message"),
            "raiseError definition exists with a message field");
        Assert(StepDefinitions.Get("waitForSound").Fields.Any(f => f.Key == "onTimeout" && f.Options!.Contains("global")),
            "legacy waitForSound remains loadable with its per-step timeout policy");
        Assert(StepDefinitions.Get("splashListener").Fields.Count == 0,
            "Build 95: Splash Listener is a zero-configuration scoped marker");
        Assert(StepDefinitions.Get("waitForLight").Fields.Any(f => f.Key == "onTimeout" && f.Options!.Contains("stopWithAlarm")),
            "waitForLight exposes a per-step timeout policy");

        var compactLux = LightTelemetryParser.Parse("OK|LUX|lux=60.0|sensor=ok");
        Assert(compactLux.Status == LightTelemetryStatus.Ok && compactLux.Lux == 60.0
               && compactLux.Sequence is null && compactLux.Mode is null,
            "Light telemetry accepts compact Pico OK|LUX response");
        var fullLux = LightTelemetryParser.Parse("OK|LUX|seq=7|lux=41.7|mode=hires|sensor=ok");
        Assert(fullLux.Status == LightTelemetryStatus.Ok && fullLux.Sequence == 7
               && fullLux.Mode == "hires" && fullLux.Lux == 41.7,
            "Light telemetry preserves the full sequenced response");

        var def = StepDefinitions.Get("openFile");
        Assert(def.Label == "Open File / Program", "openFile definition exists");

        def = StepDefinitions.Get("playScript");
        Assert(def.Label == "Play Script (.amsj)", "playScript definition exists");

        var launchRecoveryDef = StepDefinitions.Get("callLaunchDcRecovery");
        Assert(launchRecoveryDef.Label == "Run/Call Launch DC Recovery" && launchRecoveryDef.Fields.Count == 0,
            "Launch DC Recovery call is a first-class zero-configuration action");
        var launchActionVm = new MainViewModel();
        launchActionVm.InitializePipelineTabs();
        Assert(!launchActionVm.IsLaunchPipeline, "Launch recovery action is disabled outside the Launch tab");
        var launchTab = launchActionVm.PipelineTabs.Single(t => t.Kind == PipelineKind.Launch);
        launchActionVm.SwitchPipelineCommand.Execute(launchTab);
        Assert(launchActionVm.IsLaunchPipeline, "Launch recovery action becomes available in the Launch tab");

        // ── Step 2: Commands generation ──────────────────────────────
        var mouseClick = new StepNode
        {
            Type = "mouseClick",
            Props = new Dictionary<string, object?> { { "button", "left" }, { "action", "single" } }
        };
        var cmds = StepDefinitions.GetCommands(mouseClick);
        Assert(cmds.Contains("MCLICK|left,1"), $"mouseClick command correct (got: {string.Join(", ", cmds)})");

        var mouseMove = new StepNode
        {
            Type = "mouseMove",
            Props = new Dictionary<string, object?> { { "x", 100 }, { "y", 200 }, { "human", true } }
        };
        cmds = StepDefinitions.GetCommands(mouseMove);
        Assert(cmds.Contains("MMOVE|100,200,abs,1"), $"mouseMove command correct (got: {string.Join(", ", cmds)})");

        // Modern autonomous Pico Guard receives the real Windows cursor origin
        // from Studio; it must never silently fall back to screen centre.
        var cursorCommand = CursorOriginSync.Command(new System.Drawing.Point(321, 654));
        Assert(cursorCommand == "CURSOR|321,654", "cursor origin command preserves the real host position");
        Assert(CursorOriginSync.IsAcknowledged("OK|CURSOR"), "cursor origin local acknowledgement is accepted");

        var typeTextSecret = new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", "secret123" }, { "secret", true }, { "mode", "clipboard" } }
        };
        cmds = StepDefinitions.GetCommands(typeTextSecret);
        Assert(cmds.Count == 2 && cmds[0].StartsWith("CLIPBOARD:"),
            $"typeText secret produces CLIPBOARD cmd (got: {string.Join(", ", cmds)})");

        // ── Step 3a: typeText word humanize ─────────────────────────
        var typeTextWord = new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?>
            {
                { "text", "hello world foo" },
                { "hmin", 25 }, { "hmax", 70 },
                { "wmin", 100 }, { "wmax", 180 }
            }
        };
        cmds = StepDefinitions.GetCommands(typeTextWord);
        Assert(cmds.Any(c => c.StartsWith("DLY|")),
            $"typeText word humanize inserts DLY (got: {string.Join(", ", cmds)})");
        // Each word should be its own KTEXT; DLY between words
        int dlyCount = cmds.Count(c => c.StartsWith("DLY|"));
        int ktextCount = cmds.Count(c => c.StartsWith("KTEXT|"));
        Assert(dlyCount == 2 && ktextCount == 3,
            $"typeText 'hello world foo': 3 KTEXT + 2 DLY (got {ktextCount}KTEXT/{dlyCount}DLY, cmds: {string.Join(" | ", cmds)})");

        // word humanize disabled when wmax == 0
        var typeTextNoWord = new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?>
            {
                { "text", "hello world" },
                { "hmin", 25 }, { "hmax", 70 },
                { "wmin", 0 }, { "wmax", 0 }
            }
        };
        cmds = StepDefinitions.GetCommands(typeTextNoWord);
        Assert(!cmds.Any(c => c.StartsWith("DLY|")),
            $"typeText no word humanize: no DLY (got: {string.Join(", ", cmds)})");

        // ── Step 3b: v0.9.11 — typeText human layers (punctuation / thinking / typo) ──
        // KTEXT|hmin,hmax,text — payload is after the SECOND comma (text itself may contain ',').
        static string KtextPayload(string c)
        {
            var s = c["KTEXT|".Length..];
            int first = s.IndexOf(',');
            int second = s.IndexOf(',', first + 1);
            return second < 0 ? "" : s[(second + 1)..];
        }
        var typePunct = new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?>
            {
                { "text", "Hi, ok. Bye" }, { "hmin", 25 }, { "hmax", 70 },
                { "pmin", 200 }, { "pmax", 200 }
            }
        };
        cmds = StepDefinitions.GetCommands(typePunct);
        Assert(cmds.Count(c => c == "DLY|200") == 2,
            $"punctuation pauses fire after ',' and '.' mid-line (got {cmds.Count(c => c == "DLY|200")}×DLY|200)");
        var punctTyped = string.Concat(cmds.Where(c => c.StartsWith("KTEXT|")).Select(KtextPayload));
        Assert(punctTyped == "Hi, ok. Bye",
            $"punctuation splitting preserves the exact source text (got \"{punctTyped}\")");

        var typeThink = new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?>
            {
                { "text", "one two three" },
                { "thinkChance", 100 }, { "thinkMin", 1500 }, { "thinkMax", 1500 }
            }
        };
        cmds = StepDefinitions.GetCommands(typeThink);
        Assert(cmds.Count(c => c == "DLY|1500") == 2,
            $"thinking pause fires between words only at 100% (got {cmds.Count(c => c == "DLY|1500")}×DLY|1500)");

        var typeTypo = new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", "hello world" }, { "typoChance", 100 } }
        };
        cmds = StepDefinitions.GetCommands(typeTypo);
        Assert(cmds.Count(c => c == "KCOMBO|8") == 2,
            $"typoChance=100 injects one backspace correction per word (got {cmds.Count(c => c == "KCOMBO|8")})");
        var typoTyped = string.Concat(cmds.Where(c => c.StartsWith("KTEXT|")).Select(KtextPayload));
        Assert(typoTyped.Length == "hello world".Length + 2,
            $"typo sequence types exactly one slip char per word before correcting (typed {typoTyped.Length} chars)");

        // ── Typo interval: corrected slip every N eligible characters ──
        var tenWords = string.Join(" ", Enumerable.Range(0, 10).Select(i => $"word{i}"));
        cmds = StepDefinitions.GetCommands(new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", "zodiak999999" }, { "typoEveryMin", 7 }, { "typoEveryMax", 12 } }
        });
        Assert(cmds.Count(c => c == "KCOMBO|8") == 1,
            $"typo interval 7–12 injects one correction into a 12-character password (got {cmds.Count(c => c == "KCOMBO|8")})");

        int countMin = int.MaxValue, countMax = 0;
        var correctionCounts = new HashSet<int>();
        for (int seed = 0; seed < 24; seed++)
        {
            var cc = StepDefinitions.GetCommands(new StepNode
            {
                Type = "typeText",
                Props = new Dictionary<string, object?> { { "text", "abcdefghijklmnopqrstuvwxyz" }, { "typoEveryMin", 7 }, { "typoEveryMax", 12 } }
            }, seed);
            int bs = cc.Count(x => x == "KCOMBO|8");
            countMin = Math.Min(countMin, bs); countMax = Math.Max(countMax, bs);
            correctionCounts.Add(bs);
        }
        Assert(countMin >= 2 && countMax <= 3,
            $"typo interval 7–12 spaces corrections across 26 characters (got {countMin}–{countMax})");
        Assert(correctionCounts.Count > 1,
            $"typo interval is re-rolled after corrections (distinct counts: {string.Join(",", correctionCounts.OrderBy(x=>x))})");

        cmds = StepDefinitions.GetCommands(new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", "abcdefg" }, { "typoEveryMin", 7 }, { "typoEveryMax", 7 } }
        });
        Assert(cmds.Count(c => c == "KCOMBO|8") == 1,
            $"the interval boundary itself receives one corrected slip (got {cmds.Count(c => c == "KCOMBO|8")})");

        cmds = StepDefinitions.GetCommands(new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", tenWords }, { "typoChance", 100 }, { "typoEveryMin", 5 }, { "typoEveryMax", 5 } }
        });
        Assert(cmds.Count(c => c == "KCOMBO|8") > 5,
            $"character interval takes precedence over legacy typoChance (got {cmds.Count(c => c == "KCOMBO|8")})");

        // ── v0.9.13 — word-pause probability + stream merging (no fixed gap after space) ──
        cmds = StepDefinitions.GetCommands(new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", "hello world foo" }, { "wmin", 100 }, { "wmax", 180 }, { "wordPauseChance", 0 } }
        });
        Assert(cmds.Count == 1 && cmds[0] == "KTEXT|80,220,hello world foo",
            $"wordPauseChance=0 streams the whole line as ONE chunk (got: {string.Join(" | ", cmds)})");

        cmds = StepDefinitions.GetCommands(new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", "hello world foo" }, { "wmin", 100 }, { "wmax", 180 }, { "wordPauseChance", 100 } }
        });
        Assert(cmds.Count(c => c.StartsWith("KTEXT|")) == 3 && cmds.Count(c => c.StartsWith("DLY|")) == 2,
            "wordPauseChance=100 keeps the legacy pause-after-every-word behavior");

        bool sawMerged = false, sawSplit = false;
        for (int seed = 0; seed < 40 && !(sawMerged && sawSplit); seed++)
        {
            var pc = StepDefinitions.GetCommands(new StepNode
            {
                Type = "typeText",
                Props = new Dictionary<string, object?> { { "text", "hello world foo" }, { "wmin", 100 }, { "wmax", 180 }, { "wordPauseChance", 50 } }
            });
            int k = pc.Count(c => c.StartsWith("KTEXT|"));
            if (k == 1) sawMerged = true;
            if (k == 3) sawSplit = true;
            Assert(string.Concat(pc.Where(c => c.StartsWith("KTEXT|")).Select(KtextPayload)) == "hello world foo",
                "merged and split streams always preserve the exact text");
        }
        Assert(sawMerged && sawSplit, "wordPauseChance=50 produces both flowing and pausing runs");

        // ── v0.9.14 — tunable human move everywhere (Find Image / Mouse Position) ──
        var gentleDefaulted = HumanMouse.Config.FromProps(new Dictionary<string, object?>(), 0, 2300, gentleDefaults: true);
        Assert(gentleDefaulted.PauseBeforeMinMs == 60 && gentleDefaulted.PauseBeforeMaxMs == 220
               && gentleDefaulted.PauseAfterMinMs == 80 && gentleDefaulted.PauseAfterMaxMs == 280
               && gentleDefaulted.MidPauseChancePct == 6 && gentleDefaulted.OvershootChancePct == 12
               && gentleDefaulted.CurveMinPct == 20 && gentleDefaulted.CurveMaxPct == 40
               && gentleDefaulted.IdlePauseMaxMs == 0 && gentleDefaulted.IdleEveryMaxMoves == 0,
            "gentleDefaults with no keys reproduces the old fixed Gentle preset (idle breaks off)");

        var gentleTuned = HumanMouse.Config.FromProps(new Dictionary<string, object?>
            { { "curveMinPct", 120 }, { "curveMaxPct", 180 }, { "moveTimeMin", 800 }, { "moveTimeMax", 1600 } }, 0, 2300, gentleDefaults: true);
        Assert(gentleTuned.CurveMinPct == 120 && gentleTuned.CurveMaxPct == 180 && gentleTuned.MoveTimeMaxMs == 1600,
            "gentleDefaults respects explicit per-step overrides (arc range + duration range)");

        var regularDefaults = HumanMouse.Config.FromProps(new Dictionary<string, object?>(), 0, 2300);
        Assert(regularDefaults.PauseBeforeMinMs == 120 && regularDefaults.IdlePauseMaxMs == 5000,
            "the Random Mouse Position defaults path is unchanged");

        var ttFields = StepDefinitions.Get("typeText").Fields;
        Assert(ttFields.First(f => f.Key == "hmin").HideWhenKey == "mode"
               && ttFields.First(f => f.Key == "typoEveryMax").HideWhenValue == "clipboard",
            "typeText tuning fields are marked hidden in clipboard mode");
        Assert(StepDefinitions.Get("findImage").Fields.Any(f => f.Key == "curveMinPct" && f.HideWhenKey == "humanMove")
               && StepDefinitions.Get("mouseMove").Fields.Any(f => f.Key == "moveTimeMax" && f.HideWhenKey == "human"),
            "Find Image and Mouse Position carry the tunable human-move fields with hide-when-off metadata");

        // ── v0.9.15 — accuracy guarantee: humanized paths NEVER lose the target ──
        // curve/duration/overshoot/hesitation only reshape intermediate points and timing; the
        // final waypoint is always exactly (tx,ty) and every point stays on screen.
        var fuzzRng = new Random(20260828);
        int missed = 0, offScreen = 0;
        for (int i = 0; i < 150; i++)
        {
            var fcfg = HumanMouse.Config.FromProps(new Dictionary<string, object?>
            {
                { "curveMinPct", fuzzRng.Next(0, 201) }, { "curveMaxPct", fuzzRng.Next(0, 201) },
                { "overshootChance", fuzzRng.Next(0, 101) }, { "midPauseChance", fuzzRng.Next(0, 101) },
                { "moveTimeMin", 0 }, { "moveTimeMax", fuzzRng.Next(0, 2000) },
            }, 0, 2300);
            int fsx = fuzzRng.Next(0, 1900), fsy = fuzzRng.Next(0, 1050);
            int ftx = fuzzRng.Next(0, 1900), fty = fuzzRng.Next(0, 1050);
            var fplan = HumanMouse.PlanMove(fsx, fsy, ftx, fty, fcfg,
                new HumanMouse.PausePlanner(new Random(fuzzRng.Next())), new Random(fuzzRng.Next()), 1920, 1080);
            var flast = fplan.Waypoints[^1];
            if (flast.X != ftx || flast.Y != fty) missed++;
            if (fplan.Waypoints.Any(p => p.X < 0 || p.X >= 1920 || p.Y < 0 || p.Y >= 1080)) offScreen++;
        }
        Assert(missed == 0, $"150 fuzzed humanized moves all land EXACTLY on the chosen target ({missed} missed)");
        Assert(offScreen == 0, $"all fuzzed paths stay on screen ({offScreen} escapes)");

        // ── v0.9.15 — Parallel Group: children run concurrently, next step joins on the longest ──
        static StepNode DelayStep(int ms) => new()
        {
            Type = "delay",
            Props = new Dictionary<string, object?> { { "minMs", ms }, { "maxMs", ms } },
        };

        var pg = new StepNode { Type = "parallelGroup" };
        pg.Children.Add(DelayStep(300));
        pg.Children.Add(DelayStep(300));
        var sw = Stopwatch.StartNew();
        new RunEngine(new FakeBridge(), _ => { }, 1920, 1080).RunAsync(new[] { pg }, CancellationToken.None).Wait();
        sw.Stop();
        // v0.9.58c — ceiling is diagnostic-only on CI; overlap itself is proven by MaxInFlight asserts
        if (IsCi) Console.WriteLine($"INFO(CI): two 300ms branches overlapped, elapsed {sw.ElapsedMilliseconds}ms");
        else Assert(sw.ElapsedMilliseconds < 520,
            $"parallel group: two 300ms delays overlap (elapsed {sw.ElapsedMilliseconds}ms; sequential would be ≥600)");

        pg = new StepNode { Type = "parallelGroup" };
        pg.Children.Add(DelayStep(120));
        pg.Children.Add(DelayStep(450));
        sw.Restart();
        new RunEngine(new FakeBridge(), _ => { }, 1920, 1080).RunAsync(new[] { pg }, CancellationToken.None).Wait();
        sw.Stop();
        Assert(sw.ElapsedMilliseconds >= 400,
            $"parallel group join blocks until the longest branch finishes (elapsed {sw.ElapsedMilliseconds}ms, floor 400)");
        // v0.9.58c — ceiling is diagnostic-only on CI (loaded runners stretch delays)
        if (IsCi) Console.WriteLine($"INFO(CI): longest-branch join elapsed {sw.ElapsedMilliseconds}ms (≈450 nominal)");
        else Assert(sw.ElapsedMilliseconds < 750,
            $"parallel group joins on the LONGEST branch (elapsed {sw.ElapsedMilliseconds}ms ≈ 450, not 570)");

        var seqAfter = new StepNode { Type = "parallelGroup" };
        seqAfter.Children.Add(DelayStep(400));
        seqAfter.Children.Add(new StepNode { Type = "comment", Props = new Dictionary<string, object?> { { "text", "fast branch" } } });
        sw.Restart();
        new RunEngine(new FakeBridge(), _ => { }, 1920, 1080)
            .RunAsync(new StepNode[] { seqAfter, DelayStep(10) }, CancellationToken.None).Wait();
        sw.Stop();
        Assert(sw.ElapsedMilliseconds >= 380,
            $"the step AFTER a parallel group runs only after the join (elapsed {sw.ElapsedMilliseconds}ms)");

        var conc = new FakeBridge { ArtificialDelayMs = 150 };
        var pgConc = new StepNode { Type = "parallelGroup" };
        pgConc.Children.Add(new StepNode { Type = "typeText", Props = new Dictionary<string, object?> { { "text", "aaaa" } } });
        pgConc.Children.Add(new StepNode { Type = "typeText", Props = new Dictionary<string, object?> { { "text", "bbbb" } } });
        new RunEngine(conc, _ => { }, 1920, 1080).RunAsync(new[] { pgConc }, CancellationToken.None).Wait();
        Assert(conc.MaxInFlight >= 2,
            $"two branches truly overlapped on the bridge (max in-flight {conc.MaxInFlight} ≥ 2)");

        var seqCtl = new FakeBridge { ArtificialDelayMs = 50 };
        new RunEngine(seqCtl, _ => { }, 1920, 1080).RunAsync(new[]
        {
            new StepNode { Type = "typeText", Props = new Dictionary<string, object?> { { "text", "aaaa" } } },
            new StepNode { Type = "typeText", Props = new Dictionary<string, object?> { { "text", "bbbb" } } },
        }, CancellationToken.None).Wait();
        Assert(seqCtl.MaxInFlight == 1, "outside a parallel group commands stay strictly sequential");

        var parMouse = new FakeBridge();
        var pgMouse = new StepNode { Type = "parallelGroup" };
        pgMouse.Children.Add(new StepNode
        {
            Type = "randomMousePosition",
            Props = new Dictionary<string, object?> { ["x"] = 100, ["y"] = 100, ["w"] = 500, ["h"] = 400 },
        });
        pgMouse.Children.Add(new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", "hi" } },
        });
        new RunEngine(parMouse, _ => { }, 1920, 1080).RunAsync(new[] { pgMouse }, CancellationToken.None).Wait();
        Assert(parMouse.PathCalls == 0 && parMouse.Sent.Any(c => c.StartsWith("MMOVE|") && c.EndsWith(",abs,0")),
            "inside a parallel group the mouse is app-paced per-point (interleaves with typing)");
        Assert(parMouse.Sent.Any(c => c.StartsWith("KTEXT|")),
            "the typing branch ran concurrently on the same bridge");

        var pgScript = Path.Combine(Path.GetTempPath(), "ams_test_pg.ps1");
        try
        {
            var pgNode = new StepNode { Type = "parallelGroup" };
            pgNode.Children.Add(new StepNode { Type = "typeText", Props = new Dictionary<string, object?> { { "text", "hello" } } });
            ScriptGenerator.Generate(pgScript, new[] { pgNode }, "AUTO", @"C:\x", 0, 2300, 1920, 1080);
            var pgs = File.ReadAllText(pgScript);
            Assert(pgs.Contains("PARALLEL GROUP") && pgs.Contains("KTEXT|"),
                "generated script warns and emits parallel-group children sequentially (one serial channel)");
        }
        finally { if (File.Exists(pgScript)) File.Delete(pgScript); }

        // defaults stay legacy-identical: no extra pauses, no typos
        var typeLegacy = new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", "plain text" } }
        };
        cmds = StepDefinitions.GetCommands(typeLegacy);
        Assert(cmds.Count == 1 && cmds[0] == "KTEXT|80,220,plain text",
            $"defaults produce the single legacy chunk (got: {string.Join(" | ", cmds)})");

        // v0.9.11 — multi-line keystrokes text is TYPED with Enter between lines (not demoted
        // to clipboard by the non-ASCII regex catching '\n')
        var typeMulti = new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", "first line\nsecond line" } }
        };
        cmds = StepDefinitions.GetCommands(typeMulti);
        Assert(cmds.Contains("KCOMBO|13") && !cmds.Any(c => c.StartsWith("CLIPBOARD:")),
            $"multi-line keystrokes text is typed with Enter, not pasted (got: {string.Join(" | ", cmds)})");

        // ── Step 3: ScriptGenerator openFile emits Start-Process ─────
        var steps = new List<StepNode>
        {
            new StepNode
            {
                Type = "openFile",
                Name = "Notepad",
                Delay = 500,
                Props = new Dictionary<string, object?>
                {
                    { "path", @"C:\Windows\notepad.exe" },
                    { "args", "" },
                    { "windowState", "normal" }
                }
            }
        };
        var scriptPath = Path.Combine(Path.GetTempPath(), "ams_test_macro.ps1");
        try
        {
            ScriptGenerator.Generate(scriptPath, steps, "AUTO", @"C:\Users\wasteland\Documents\ams\pc");
            var script = File.ReadAllText(scriptPath);
            Assert(script.Contains("Start-Process"), "openFile generates Start-Process in script");
            Assert(script.Contains("notepad.exe"), "Script contains the executable path");
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }

        // ── Step 4: AppSettings round-trip ───────────────────────────
        var settings = new AppSettings { Port = "COM3", PythonDir = @"C:\test\py", ToolkitDir = @"C:\test\tk" };
        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json);
        Assert(loaded != null && loaded.Port == "COM3" && loaded.ToolkitDir == @"C:\test\tk",
            "AppSettings round-trip preserves ToolkitDir");
        var legacyPlayback = JsonSerializer.Deserialize<AppSettings>("{\"PlayRepeatMode\":\"times\",\"PlayRepeatTimes\":7,\"ShutdownWhenFinished\":true,\"NoActivateWhenStopped\":true}");
        Assert(legacyPlayback != null && legacyPlayback.PlayRepeatMode == "times"
               && legacyPlayback.PlayRepeatTimes == 7 && legacyPlayback.ShutdownWhenFinished
               && legacyPlayback.NoActivateWhenStopped,
            "legacy playback settings still deserialize after the UI cleanup");

        // ── Step 5: StepDefinitions summarization ────────────────────
        var loop = new StepNode
        {
            Type = "forLoop",
            Props = new Dictionary<string, object?> { { "mode", "time" }, { "timeValue", 5 }, { "timeUnit", "minute" } }
        };
        Assert(StepDefinitions.Summarize(loop) == "For 5 minute",
            $"forLoop summary correct: '{StepDefinitions.Summarize(loop)}'");

        var sound = new StepNode
        {
            Type = "waitForSound",
            Props = new Dictionary<string, object?> { { "threshold", 90 }, { "armed", true }, { "act", "left" } }
        };
        Assert(StepDefinitions.Summarize(sound).Contains("armed"),
            $"sound armed summary correct: '{StepDefinitions.Summarize(sound)}'");

        // ── Step 6: findImage step definition fields ─────────────────
        var fiDef = StepDefinitions.Get("findImage");
        Assert(fiDef.IsContainer, "findImage is a container step");
        Assert(fiDef.Fields.Any(f => f.Key == "pictures"), "findImage has 'pictures' field");

        // ── Step 7: Delay step default values ────────────────────────
        var delay = new StepNode
        {
            Type = "delay",
            Props = new Dictionary<string, object?> { { "minMs", 100 }, { "maxMs", 300 } }
        };
        Assert(StepDefinitions.Summarize(delay) == "Delay 100 to 300 ms",
            $"delay summary: '{StepDefinitions.Summarize(delay)}'");

        // ── Step 8: REAL IMPORT TEST — daroon1.amk ──────────────────
        Console.WriteLine();
        Console.WriteLine("--- Import Test: daroon1.amk ---");

        // We need to call AmkImporter which runs python subprocess
        // Use Task.Run with a short timeout
        // v0.9.58b — daroon1.amk lives only on the dev machine; on CI the import/PNG/native steps skip cleanly
        var daroonAmk = @"C:\Users\wasteland\Documents\ams\pc\daroon1.amk";
        var daroonPresent = File.Exists(daroonAmk);
        var importTask = daroonPresent
            ? Task.Run(() => AmkImporter.Import(daroonAmk, @"C:\Users\wasteland\Documents\ams\pc"))
            : null;

        var result = importTask is not null && importTask.Wait(TimeSpan.FromSeconds(60)) ? importTask.Result : null;

        if (result == null)
        {
            if (daroonPresent) Assert(false, "Import timed out or failed");
            else Console.WriteLine("SKIP: daroon1.amk not present on this machine — daroon import test skipped (CI-safe)");
        }
        else
        {
            Console.WriteLine($"Top-level steps: {result.Roots.Count}");
            foreach (var w in result.Warnings) Console.WriteLine($"  WARN: {w}");

            int totalCount = 0;
            int findImageCount = 0;
            int forLoopCount = 0;
            int commentCount = 0;
            int keyDownCount = 0;
            int keyUpCount = 0;
            int delayCount = 0;
            int rndMvCount = 0;
            int elseComments = 0;
            int withPictures = 0;
            int disabledCount = 0;

            void CountNodes(IEnumerable<StepNode> nodes)
            {
                foreach (var n in nodes)
                {
                    totalCount++;
                    if (n.Type == "findImage")
                    {
                        findImageCount++;
                        var pics = n.Props.GetValueOrDefault("pictures") as string;
                        if (!string.IsNullOrEmpty(pics)) withPictures++;
                    }
                    if (n.Type == "forLoop") forLoopCount++;
                    if (n.Type == "comment")
                    {
                        commentCount++;
                        var t = n.Props.GetValueOrDefault("text") as string;
                        if (n.Name == "Else" || (t != null && t.StartsWith("Else"))) elseComments++;
                    }
                    if (n.Type == "keyDown") keyDownCount++;
                    if (n.Type == "keyUp") keyUpCount++;
                    if (n.Type == "delay") delayCount++;
                    if (n.Type == "randomMousePosition") rndMvCount++;
                    if (n.IsDisabled) disabledCount++;
                    CountNodes(n.Children);
                }
            }
            CountNodes(result.Roots);

            Assert(result.Roots.Count >= 50, $"Import produced >=50 top-level steps (got {result.Roots.Count})");
            Assert(findImageCount >= 3, $"findImage steps: {findImageCount} (expected >=3)");
            Assert(forLoopCount >= 5, $"forLoop steps: {forLoopCount} (expected >=5)");
            Assert(commentCount >= 10, $"comment steps (Else/Next/EndIf): {commentCount} (expected >=10)");
            Assert(elseComments >= 2, $"Else branch comments: {elseComments} (expected >=2)");
            Assert(withPictures >= 3, $"findImage with picture paths: {withPictures} (expected >=3)");
            Assert(keyDownCount >= 5, $"keyDown steps: {keyDownCount}");
            Assert(keyUpCount >= 5, $"keyUp steps: {keyUpCount}");
            Assert(delayCount >= 10, $"delay steps: {delayCount}");
            Assert(rndMvCount >= 5, $"randomMousePosition steps: {rndMvCount}");
            Assert(disabledCount == 0, $"No disabled steps by default (got {disabledCount})");

            Console.WriteLine();
            Console.WriteLine("--- Tree Summary ---");
            Console.WriteLine($"Total nodes: {totalCount}");
            Console.WriteLine($"  findImage: {findImageCount}");
            Console.WriteLine($"  forLoop: {forLoopCount}");
            Console.WriteLine($"  comment (Else/Next/EndIf): {commentCount}");
            Console.WriteLine($"  keyDown/keyUp: {keyDownCount}/{keyUpCount}");
            Console.WriteLine($"  delay: {delayCount}");
            Console.WriteLine($"  randomMousePosition: {rndMvCount}");
        }

        // ── Step 9: PNG extraction verification ──────────────────────
        Console.WriteLine();
        Console.WriteLine("--- PNG Extraction Test ---");

        var imgDir = Path.Combine(Path.GetTempPath(), "ams-png-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(imgDir);
        var pngSuccess = false;
        try
        {
            var psi = new ProcessStartInfo("python",
                $"\"C:/Users/wasteland/Documents/ams/pc/amk_decoder.py\" " +
                $"\"C:/Users/wasteland/Documents/ams/pc/daroon1.amk\" " +
                $"--json \"{Path.Combine(Path.GetTempPath(), "decoded_test.json")}\" " +
                $"--imgdir \"{imgDir}\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                EnvironmentVariables = { ["PYTHONIOENCODING"] = "utf-8" }
            };
            using var p = Process.Start(psi);
            if (p is null) throw new InvalidOperationException("PNG decoder process could not be started");
            // Read both streams concurrently to avoid pipe-buffer deadlock
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit(30000);
            outTask.Wait(); errTask.Wait();
            var stderr = errTask.Result;
            if (p.ExitCode == 0)
            {
                var pngFiles = Directory.GetFiles(imgDir, "*.png");
                if (pngFiles.Length >= 9)
                {
                    pngSuccess = true;
                    Console.WriteLine($"  Extracted {pngFiles.Length} PNG images");
                    foreach (var f in pngFiles.Take(5))
                        Console.WriteLine($"    {Path.GetFileName(f)}: {new FileInfo(f).Length} bytes");
                }
            }
            else
            {
                Console.WriteLine($"  Decoder failed (exit {p.ExitCode}): {stderr.Substring(0, Math.Min(200, stderr.Length))}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  PNG test error: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(imgDir, true); } catch { }
            var jf = Path.Combine(Path.GetTempPath(), "decoded_test.json");
            if (File.Exists(jf)) File.Delete(jf);
        }
        if (!pngSuccess && !daroonPresent)
            Console.WriteLine("SKIP: daroon1.amk not present on this machine — PNG extraction test skipped (CI-safe)");
        else
            Assert(pngSuccess, "PNG images extracted from daroon1.amk");

        // ── Step 10: v0.7.8 — Random Package definition + hold params (firmware 1.7) ──
        Console.WriteLine();
        Console.WriteLine("--- v0.7.8: Random Package + hold range + DelayMax ---");

        var pkgDef = StepDefinitions.Get("randomPackage");
        Assert(pkgDef.Label == "Random Package", "randomPackage definition exists");
        Assert(pkgDef.IsContainer && pkgDef.IsScopeContainer, "randomPackage is a scope container");
        Assert(pkgDef.Fields.Any(f => f.Key == "mode") && pkgDef.Fields.Any(f => f.Key == "minCount") && pkgDef.Fields.Any(f => f.Key == "maxCount"),
            "randomPackage has mode/minCount/maxCount fields");

        var clickNoHold = new StepNode { Type = "mouseClick", Props = new Dictionary<string, object?> { { "button", "left" }, { "action", "single" } } };
        Assert(StepDefinitions.GetCommands(clickNoHold)[0] == "MCLICK|left,1",
            "mouseClick without hold stays firmware-1.6 compatible");

        var clickHold = new StepNode { Type = "mouseClick", Props = new Dictionary<string, object?> { { "button", "right" }, { "action", "double" }, { "holdMin", 30 }, { "holdMax", 90 } } };
        Assert(StepDefinitions.GetCommands(clickHold)[0] == "MCLICK|right,2,30,90",
            $"mouseClick with hold range (got {StepDefinitions.GetCommands(clickHold)[0]})");

        var clickSwap = new StepNode { Type = "mouseClick", Props = new Dictionary<string, object?> { { "button", "left" }, { "holdMin", 90 }, { "holdMax", 30 } } };
        Assert(StepDefinitions.GetCommands(clickSwap)[0] == "MCLICK|left,1,30,90", "hold min/max swap is normalized");

        var keyHold = new StepNode { Type = "keystroke", Props = new Dictionary<string, object?> { { "modCtrl", true }, { "key", "F4" }, { "holdMin", 40 }, { "holdMax", 120 } } };
        Assert(StepDefinitions.GetCommands(keyHold)[0] == "KCOMBO|162+115,40,120",
            $"keystroke with hold range (got {StepDefinitions.GetCommands(keyHold)[0]})");

        var sn = new StepNode { Type = "delay", Delay = 100, DelayMax = 300 };
        var sn2 = JsonSerializer.Deserialize<StepNode>(JsonSerializer.Serialize(sn));
        Assert(sn2 != null && sn2.Delay == 100 && sn2.DelayMax == 300, "DelayMax survives JSON round-trip");
        Assert(!JsonSerializer.Serialize(new StepNode { Type = "delay", Delay = 100 }).Contains("DelayMax"),
            "DelayMax=0 is omitted from .amsj (clean files)");

        // ── Step 11: v0.7.8 — package picking (pure) + engine execution (FakeBridge) ──
        StepNode MkBtn(string name) => new()
        {
            Type = "rawCommand",
            Name = name,
            Props = new Dictionary<string, object?> { { "cmd", "PING|" + name } },
        };

        var pkg = new StepNode { Type = "randomPackage", Props = new Dictionary<string, object?> { { "mode", "shuffleAll" } } };
        for (int i = 1; i <= 10; i++) pkg.Children.Add(MkBtn("b" + i));
        pkg.Children[3].IsDisabled = true;   // b4 stays out of the pool

        var rng = new Random(42);
        var picks = RunEngine.PickPackageSteps(pkg, rng);
        Assert(picks.Count == 9, $"shuffleAll picks every enabled child once (got {picks.Count})");
        Assert(picks.All(p => p.Name != "b4"), "disabled child excluded from the pool");
        Assert(picks.Select(p => p.Name).Distinct().Count() == 9, "no child fires twice in shuffleAll");

        var baseline = picks.Select(p => p.Name).ToList();
        bool fresh = false;
        for (int t = 0; t < 5 && !fresh; t++)
            if (!RunEngine.PickPackageSteps(pkg, rng).Select(p => p.Name).SequenceEqual(baseline)) fresh = true;
        Assert(fresh, "every pass draws a fresh order (checked 5 passes)");

        var pkgSub = new StepNode { Type = "randomPackage", Props = new Dictionary<string, object?> { { "mode", "randomSubset" }, { "minCount", 3 }, { "maxCount", 5 } } };
        for (int i = 1; i <= 10; i++) pkgSub.Children.Add(MkBtn("s" + i));
        bool countsOk = true, distinctOk = true;
        for (int t = 0; t < 200; t++)
        {
            var sub = RunEngine.PickPackageSteps(pkgSub, rng);
            if (sub.Count < 3 || sub.Count > 5) countsOk = false;
            if (sub.Select(p => p.Name).Distinct().Count() != sub.Count) distinctOk = false;
        }
        Assert(countsOk, "randomSubset count stays within [3,5] over 200 passes");
        Assert(distinctOk, "randomSubset never repeats a child within one pass");

        var pkgSwap = new StepNode { Type = "randomPackage", Props = new Dictionary<string, object?> { { "mode", "randomSubset" }, { "minCount", 50 }, { "maxCount", 2 } } };
        for (int i = 1; i <= 4; i++) pkgSwap.Children.Add(MkBtn("w" + i));
        var swapPicks = RunEngine.PickPackageSteps(pkgSwap, rng);
        Assert(swapPicks.Count >= 2 && swapPicks.Count <= 4, $"min/max swap + pool cap handled (got {swapPicks.Count})");

        var fb = new FakeBridge();
        var engine = new RunEngine(fb, _ => { }, 1920, 1080);
        engine.RunAsync(new[] { pkg }, CancellationToken.None).Wait();
        Assert(fb.Sent.Count > 0 && fb.Sent[0] == "SETRES|1920,1080", "SETRES still goes first");
        var pings = fb.Sent.Where(c => c.StartsWith("PING|")).ToList();
        Assert(pings.Count == 9 && !pings.Contains("PING|b4") && pings.Distinct().Count() == 9,
            $"engine runs the shuffled package (got {pings.Count} pings)");

        var pkgScript = Path.Combine(Path.GetTempPath(), "ams_test_pkg.ps1");
        try
        {
            ScriptGenerator.Generate(pkgScript, new[] { pkg }, "AUTO", @"C:\x");
            Assert(File.ReadAllText(pkgScript).Contains("Random Package"), "script generator marks Random Package (app-side) in the .ps1");
        }
        finally { if (File.Exists(pkgScript)) File.Delete(pkgScript); }

        // ── Step 12: v0.7.9 — FindElseBranch (real If/Else for Find Image) ──
        Console.WriteLine();
        Console.WriteLine("--- v0.7.9: FindElseBranch ---");

        StepNode MkComment(string text) => new()
        {
            Type = "comment",
            Props = new Dictionary<string, object?> { { "text", text } },
        };
        var fi = new StepNode { Type = "findImage", Props = new Dictionary<string, object?> { { "insertIfElse", true } } };
        var els = MkComment("Else");
        els.Children.Add(MkBtn("elseStep"));
        var endIf = MkComment("End If");

        var sibs = new List<StepNode> { fi, els, endIf };
        Assert(ReferenceEquals(RunEngine.FindElseBranch(sibs, 0), els), "Else marker found after the If step");

        var sibsNoElse = new List<StepNode> { fi, endIf };
        Assert(RunEngine.FindElseBranch(sibsNoElse, 0) is null, "End If without Else → no branch");

        var sibsLeft = new List<StepNode> { fi, MkBtn("stray"), els };
        Assert(RunEngine.FindElseBranch(sibsLeft, 0) is null, "scan stops at a non-comment sibling (left the If scope)");

        var elsImported = MkComment("Else — 3 step(s) preserved below; branch execution lands later");
        var sibs2 = new List<StepNode> { fi, elsImported, endIf };
        Assert(ReferenceEquals(RunEngine.FindElseBranch(sibs2, 0), elsImported), "imported AMK Else text (\"Else — …\") recognized");

        // ── Step 13: v0.8.0 — accordion collapse in the flat steps list ──
        Console.WriteLine();
        Console.WriteLine("--- v0.8.0: accordion collapse ---");

        var vm2 = new Ams.UI.ViewModels.MainViewModel();
        var loop2 = new StepNode { Type = "forLoop", Props = new Dictionary<string, object?> { { "mode", "count" }, { "count", 3 } } };
        loop2.Children.Add(MkBtn("inner1"));
        loop2.Children.Add(MkBtn("inner2"));
        vm2.Steps.Add(loop2);
        vm2.Steps.Add(MkBtn("top"));

        vm2.ToggleCollapse(loop2);   // first call builds the flat list (collapsed)
        Assert(vm2.FlatSteps.Count == 2, $"collapsed loop hides its children (got {vm2.FlatSteps.Count} rows, want 2)");
        Assert(vm2.FlatSteps[0].ToggleGlyph == "▸", "collapsed row shows ▸");
        vm2.ToggleCollapse(loop2);   // expand again
        Assert(vm2.FlatSteps.Count == 4, $"expanded loop shows children again (got {vm2.FlatSteps.Count} rows, want 4)");
        Assert(vm2.FlatSteps[1].Number == "1.1", $"child numbering intact (got {vm2.FlatSteps[1].Number})");
        Assert(vm2.FlatSteps[0].ToggleGlyph == "▾", "expanded row shows ▾");

        // ── Step 14: v0.8.2 — native embedded-picture extraction (.amk BMPN) ──
        Console.WriteLine();
        Console.WriteLine("--- v0.8.2: native image extraction ---");

        var amkPath = @"C:\Users\wasteland\Documents\ams\pc\daroon1.amk";
        var exDir = Path.Combine(Path.GetTempPath(), "ams-imgx-" + Guid.NewGuid().ToString("n"));
        try
        {
            if (!daroonPresent)
            {
                Console.WriteLine("SKIP: daroon1.amk not present on this machine — native image extraction test skipped (CI-safe)");
            }
            else
            {
                var imgs = AmkImageExtractor.ExtractImages(amkPath, exDir);
                Assert(imgs.Count >= 3, $"native extractor found embedded pictures (got {imgs.Count}, want >= 3)");
                Assert(imgs.Count > 0 && imgs.All(File.Exists), "all extracted PNGs exist on disk");
                if (imgs.Count > 0)
                {
                    using var bmp = new System.Drawing.Bitmap(imgs[0]);
                    Assert(bmp.Width > 1 && bmp.Height > 1, $"extracted image loads with dimensions ({bmp.Width}x{bmp.Height})");
                }
            }
        }
        finally { try { Directory.Delete(exDir, true); } catch { } }

        // ── Step 15: v0.8.3 — Insert Label / Go To Label ──
        Console.WriteLine();
        Console.WriteLine("--- v0.8.3: label / go to label ---");

        Assert(StepDefinitions.Get("label").Label == "Insert Label", "label step definition exists");
        Assert(StepDefinitions.Get("gotoLabel").Label == "Go To Label", "gotoLabel step definition exists");
        var lbl = new StepNode { Type = "label", Props = new Dictionary<string, object?> { ["label"] = "start" } };
        lbl.RefreshSummary();
        Assert(lbl.Summary.Contains("start"), $"label summary shows the name (got: {lbl.Summary})");
        Assert(RunEngine.FindLabelRow(new List<StepNode> { lbl }, "START") == 0, "FindLabelRow is case-insensitive");
        Assert(RunEngine.FindLabelRow(new List<StepNode> { lbl }, "nope") == -1, "FindLabelRow returns -1 when missing");

        // forward jump: steps between the goto and its label are skipped
        var fbL1 = new FakeBridge();
        new RunEngine(fbL1, _ => { }, 1920, 1080).RunAsync(new List<StepNode>
        {
            MkBtn("L1"),
            new() { Type = "gotoLabel", Props = new Dictionary<string, object?> { ["label"] = "skip" } },
            MkBtn("L2"),
            new() { Type = "label", Props = new Dictionary<string, object?> { ["label"] = "skip" } },
            MkBtn("L3"),
        }, CancellationToken.None).Wait();
        Assert(fbL1.Sent.Contains("PING|L1") && fbL1.Sent.Contains("PING|L3") && !fbL1.Sent.Contains("PING|L2"),
            $"goto jumps forward over steps (sent: {string.Join(", ", fbL1.Sent)})");

        // cross-level: a goto inside a loop finds a ROOT-level label (GotoSignal unwinds)
        var fbL2 = new FakeBridge();
        var loopL = new StepNode { Type = "forLoop", Props = new Dictionary<string, object?> { ["mode"] = "count", ["count"] = 1 } };
        loopL.Children.Add(MkBtn("in"));
        loopL.Children.Add(new StepNode { Type = "gotoLabel", Props = new Dictionary<string, object?> { ["label"] = "out" } });
        new RunEngine(fbL2, _ => { }, 1920, 1080).RunAsync(new List<StepNode>
        {
            loopL,
            MkBtn("middle"),
            new() { Type = "label", Props = new Dictionary<string, object?> { ["label"] = "out" } },
            MkBtn("after"),
        }, CancellationToken.None).Wait();
        Assert(fbL2.Sent.Contains("PING|in") && fbL2.Sent.Contains("PING|after") && !fbL2.Sent.Contains("PING|middle"),
            $"goto escapes a loop to a root-level label (sent: {string.Join(", ", fbL2.Sent)})");

        // unresolved label: graceful stop, warning logged, no crash
        var logsL = new List<string>();
        var fbL3 = new FakeBridge();
        new RunEngine(fbL3, logsL.Add, 1920, 1080).RunAsync(new List<StepNode>
        {
            new() { Type = "gotoLabel", Props = new Dictionary<string, object?> { ["label"] = "nowhere" } },
            MkBtn("x"),
        }, CancellationToken.None).Wait();
        Assert(!fbL3.Sent.Contains("PING|x") && logsL.Any(m => m.Contains("not found")),
            "unresolved goto stops the script with a warning (no crash)");

        // ── Step 16: v0.9.0–v0.9.2 — HumanMouse (WindMouse + pause manager + dense stream) ──
        Console.WriteLine();
        Console.WriteLine("--- v0.9.2: HumanMouse engine ---");

        var hmRng = new Random(7);
        var hmPlanner = new HumanMouse.PausePlanner(hmRng);
        var hmCfg = new HumanMouse.Config();
        var plan = HumanMouse.PlanMove(10, 10, 900, 700, hmCfg, hmPlanner, hmRng, 1920, 1080);
        Assert(plan.Waypoints.Count >= 30,
            $"v0.9.2: dense human micro-step stream (got {plan.Waypoints.Count} pts)");
        Assert(plan.ControlPoints.Count >= 1 && plan.ControlPoints.Count <= 8,
            $"fallback control points stay few (got {plan.ControlPoints.Count})");
        Assert(plan.Waypoints[^1].X == 900 && plan.Waypoints[^1].Y == 700,
            $"path lands exactly on target (got {plan.Waypoints[^1].X},{plan.Waypoints[^1].Y})");
        Assert(plan.Waypoints.All(p => p.X >= 0 && p.X < 1920 && p.Y >= 0 && p.Y < 1080),
            "all waypoints stay on screen (clamped)");
        Assert(plan.BeforeMs >= 120 && plan.BeforeMs <= 450, $"reaction pause in range (got {plan.BeforeMs})");
        Assert(plan.AfterMs >= 150 && plan.AfterMs <= 600, $"settle pause in range (got {plan.AfterMs})");

        var plan2 = HumanMouse.PlanMove(100, 100, 5000, 5000, hmCfg, hmPlanner, hmRng, 1920, 1080);
        Assert(plan2.Waypoints[^1].X == 1919 && plan2.Waypoints[^1].Y == 1079, "off-screen target is clamped");

        // v0.9.2 — micro-step size ≈ recorded human median (3 px); cadence ≈ median 5 ms
        var stepLens = new List<int>();
        for (int i = 1; i < plan.Waypoints.Count; i++)
            stepLens.Add(Math.Abs(plan.Waypoints[i].X - plan.Waypoints[i - 1].X) + Math.Abs(plan.Waypoints[i].Y - plan.Waypoints[i - 1].Y));
        stepLens.Sort();
        Assert(stepLens.Count > 0 && stepLens[stepLens.Count / 2] <= 6,
            $"median micro-step ≈ human 3px (got {stepLens[stepLens.Count / 2]})");
        var dlys = plan.Waypoints.Select(p => p.DelayMs).Where(d => d > 0).OrderBy(d => d).ToList();
        Assert(dlys.Count > 0 && dlys[dlys.Count / 2] <= 12,
            $"median cadence ≈ human 5ms (got {dlys[dlys.Count / 2]})");

        // v0.9.1 — dense trail decimates to a few control points ending exactly on target
        var dense = HumanMouse.WindMouse(0, 0, 1500, 900, hmRng);
        var ctrlPts = HumanMouse.ToControlPoints(dense, 4);
        Assert(ctrlPts.Count == 4 && ctrlPts[^1].X == 1500 && ctrlPts[^1].Y == 900,
            $"dense trail decimated to control points ending on target (got {ctrlPts.Count} pts, end {ctrlPts[^1].X},{ctrlPts[^1].Y})");

        // every-N-moves long break fires exactly on cadence and re-rolls
        var pk = new HumanMouse.PausePlanner(new Random(1));
        var pcfg = new HumanMouse.Config { IdleEveryMinMoves = 2, IdleEveryMaxMoves = 2, IdlePauseMinMs = 3000, IdlePauseMaxMs = 3000 };
        Assert(pk.RollLongPauseMs(pcfg) == 0, "move 1: no long break yet");
        Assert(pk.RollLongPauseMs(pcfg) == 3000, "move 2: the 0-5s long break fires");
        Assert(pk.RollLongPauseMs(pcfg) == 0, "move 3: counter reset after the break");

        // speed floor: a near-0 px/s draw can no longer stall a move for minutes
        var slowCfg = new HumanMouse.Config { SpeedMinPxPerSec = 0, SpeedMaxPxPerSec = 2300 };
        int worstMs = 0;
        for (int t = 0; t < 500; t++) worstMs = Math.Max(worstMs, HumanMouse.MoveTimeMs(400, slowCfg, hmRng));
        Assert(worstMs > 0 && worstMs <= 30000, $"move time bounded (worst {worstMs} ms over 500 draws)");
        Assert(HumanMouse.MoveTimeMs(500, new HumanMouse.Config { SpeedMaxPxPerSec = 0 }, hmRng) == 0,
            "speed max 0 = firmware-paced move (no app delays)");

        // old .amsj (no pause props) → human defaults, no crash
        var oldCfg = HumanMouse.Config.FromProps(new Dictionary<string, object?> { { "x", 100 }, { "y", 100 }, { "w", 500 }, { "h", 400 } }, 0, 2300);
        Assert(oldCfg.IdlePauseMaxMs == 5000 && oldCfg.PauseBeforeMinMs == 120, "legacy randomMousePosition props get the human defaults");
        Assert(oldCfg.CurveMinPct == 15 && oldCfg.CurveMaxPct == 45,
            "legacy/default fixed curve 30 migrates automatically to dynamic 15–45 range");

        // typeText word mode keeps the spaces (v0.9.0 regression fix: "hello world" was typed as "helloworld")
        var wtxt = new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", "hello world" }, { "hmin", 25 }, { "hmax", 70 }, { "wmin", 100 }, { "wmax", 180 } }
        };
        var wcmds = StepDefinitions.GetCommands(wtxt);
        Assert(wcmds.Any(c => c == "KTEXT|25,70,hello "), $"word chunk keeps its trailing space (got: {string.Join(" | ", wcmds)})");

        // v0.9.2 (Claude Code's addition — kept): non-ASCII text auto-falls back to clipboard mode
        var typeTextPersian = new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", "آمار دست واقع شما" }, { "hmin", 25 }, { "hmax", 70 } },
        };
        var pcmds = StepDefinitions.GetCommands(typeTextPersian);
        Assert(pcmds.Count == 2 && pcmds[0].StartsWith("CLIPBOARD:"),
            $"typeText Persian auto-fallback (got: {string.Join(", ", pcmds)})");

        // ── v0.9.8: dynamic ranges — exact min=max remains available for geometry baselines ──
        static double MaxPerpDevPct(List<HumanMouse.Waypoint> pts, int sx, int sy, int tx, int ty)
        {
            double dx = tx - sx, dy = ty - sy, d = Math.Sqrt(dx * dx + dy * dy);
            double ux = dx / d, uy = dy / d, mx = 0;
            foreach (var p in pts) mx = Math.Max(mx, Math.Abs((p.X - sx) * uy - (p.Y - sy) * ux));
            return mx / d * 100;
        }
        static double MaxConsecutiveJump(List<HumanMouse.Waypoint> pts, int sx, int sy)
        {
            double mx = 0; int px = sx, py = sy;
            foreach (var p in pts)
            {
                mx = Math.Max(mx, Math.Sqrt((p.X - px) * (double)(p.X - px) + (p.Y - py) * (double)(p.Y - py)));
                px = p.X; py = p.Y;
            }
            return mx;
        }
        static bool LeavesTargetAfterArrival(List<HumanMouse.Waypoint> pts, int tx, int ty)
        {
            bool arrived = false;
            foreach (var p in pts)
            {
                double d = Math.Sqrt((p.X - tx) * (double)(p.X - tx) + (p.Y - ty) * (double)(p.Y - ty));
                if (d <= 5) arrived = true;
                else if (arrived && d > 15) return true;
            }
            return false;
        }
        var flat = HumanMouse.PlanMove(100, 400, 1300, 400,
            HumanMouse.Config.FromProps(new Dictionary<string, object?> { { "curveMinPct", 0 }, { "curveMaxPct", 0 }, { "overshootChance", 0 } }, 0, 2300),
            new HumanMouse.PausePlanner(new Random(3)), new Random(3), 1920, 1080);
        var bowed = HumanMouse.PlanMove(100, 400, 1300, 400,
            HumanMouse.Config.FromProps(new Dictionary<string, object?> { { "curveMinPct", 100 }, { "curveMaxPct", 100 }, { "overshootChance", 0 } }, 0, 2300),
            new HumanMouse.PausePlanner(new Random(3)), new Random(3), 1920, 1080);
        var arced = HumanMouse.PlanMove(100, 400, 1300, 400,
            HumanMouse.Config.FromProps(new Dictionary<string, object?> { { "curveMinPct", 200 }, { "curveMaxPct", 200 }, { "overshootChance", 0 } }, 0, 2300),
            new HumanMouse.PausePlanner(new Random(3)), new Random(3), 1920, 1080);
        double devFlat = MaxPerpDevPct(flat.Waypoints, 100, 400, 1300, 400);
        double devBowed = MaxPerpDevPct(bowed.Waypoints, 100, 400, 1300, 400);
        double devArced = MaxPerpDevPct(arced.Waypoints, 100, 400, 1300, 400);
        Assert(devFlat < 2.0, $"curvePct=0 path is nearly straight (max perp dev {devFlat:F2}% < 2%)");
        Assert(devBowed > devFlat * 1.5, $"curvePct=100 is visibly curvier ({devBowed:F2}% vs {devFlat:F2}%)");
        Assert(arced.Arced && arced.ArcHeightPx > 300,
            $"curvePct=200 selects a real semicircular arc (height {arced.ArcHeightPx}px)");
        Assert(MaxConsecutiveJump(arced.Waypoints, 100, 400) < 8.0,
            $"arc stream has no reset/back-jump (max micro-step {MaxConsecutiveJump(arced.Waypoints, 100, 400):F1}px)");
        Assert(!LeavesTargetAfterArrival(arced.Waypoints, 1300, 400),
            "arc stream never leaves the target after first arrival (v0.9.5 reset regression)");

        var arc120 = HumanMouse.PlanMove(100, 400, 1300, 400,
            HumanMouse.Config.FromProps(new Dictionary<string, object?> { { "curveMinPct", 120 }, { "curveMaxPct", 120 }, { "overshootChance", 0 } }, 0, 2300),
            new HumanMouse.PausePlanner(new Random(33)), new Random(33), 1920, 1080);
        Assert(arc120.Arced && MaxPerpDevPct(arc120.Waypoints, 100, 400, 1300, 400) > 15.0,
            $"curvePct=120 is already clearly circular ({MaxPerpDevPct(arc120.Waypoints, 100, 400, 1300, 400):F1}% bow)");
        Assert(!LeavesTargetAfterArrival(arc120.Waypoints, 1300, 400),
            "curvePct=120 does not append/replay a second path from the original start");

        // v0.9.5 — arc mode: curvePct=200 produces big sweeping arcs via bulge waypoints
        int arcCount = 0;
        for (int i = 0; i < 12; i++)
        {
            var ap = HumanMouse.PlanMove(200, 300, 1500, 700,
                HumanMouse.Config.FromProps(new Dictionary<string, object?> { { "curveMinPct", 200 }, { "curveMaxPct", 200 }, { "overshootChance", 0 } }, 0, 2300),
                new HumanMouse.PausePlanner(new Random(10 + i)), new Random(10 + i), 1920, 1080);
            if (ap.Arced && MaxPerpDevPct(ap.Waypoints, 200, 300, 1500, 700) > 20.0) arcCount++;
            Assert(ap.Waypoints[^1].X == 1500 && ap.Waypoints[^1].Y == 700, "arc move lands exactly on target");
            Assert(MaxConsecutiveJump(ap.Waypoints, 200, 300) < 8.0, "arc move contains no large internal reset jump");
            Assert(!LeavesTargetAfterArrival(ap.Waypoints, 1500, 700), "arc move never replays from start after arrival");
        }
        Assert(arcCount == 12, $"arc mode at curvePct=200 always sweeps visibly (got {arcCount}/12 with dev>20%)");
        Assert(devArced > devBowed * 1.5, $"curvePct=200 arc mode is much curvier than 100 ({devArced:F2}% vs {devBowed:F2}%)");

        // All continuous min/max settings vary INSIDE one movement, not only between movements.
        var curveProfile = HumanMouse.BuildCurveProfile(120, 190, 7, new Random(808));
        var curveSamples = Enumerable.Range(0, 101)
            .Select(i => HumanMouse.SampleCurveProfile(curveProfile, i / 100.0)).ToList();
        Assert(curveSamples.Min() >= 120 && curveSamples.Max() <= 190 && curveSamples.Max() - curveSamples.Min() > 35,
            $"curvature continuously explores 120–190 inside one path ({curveSamples.Min():F1}–{curveSamples.Max():F1})");
        Assert(curveSamples.Zip(curveSamples.Skip(1), (a, b) => Math.Abs(a - b)).Max() < 6,
            "curvature profile changes smoothly without abrupt per-point jumps");

        var speedProfile = HumanMouse.BuildRangeProfile(400, 1400, 7, new Random(809), lowAtBothEnds: true);
        var speedSamples = Enumerable.Range(0, 101)
            .Select(i => HumanMouse.SampleCurveProfile(speedProfile, i / 100.0)).ToList();
        Assert(speedSamples.Min() >= 400 && speedSamples.Max() <= 1400 && speedSamples.Max() - speedSamples.Min() > 500,
            $"speed continuously explores configured range inside one path ({speedSamples.Min():F0}–{speedSamples.Max():F0}px/s)");

        var timingPts = Enumerable.Range(1, 300).Select(i => new HumanMouse.Waypoint(i * 3, 300, 0)).ToList();
        HumanMouse.AssignDynamicStreamDelays(timingPts, 0, 300, 400, 1400, new Random(810));
        Assert(timingPts.All(p => p.DelayMs > 0) && timingPts.Select(p => p.DelayMs).Distinct().Count() >= 3,
            "dynamic speed profile produces multiple micro-step delay levels in one move");

        var spacingSpine = Enumerable.Range(0, 101).Select(i => new HumanMouse.Waypoint(i * 10, 200, 0)).ToList();
        var variableSpacing = HumanMouse.ResampleByArcVariable(spacingSpine, 2.0, 3.2, new Random(811));
        var stepSizes = variableSpacing.Zip(variableSpacing.Skip(1), (a, b) => Math.Abs(b.X - a.X)).Distinct().ToList();
        Assert(stepSizes.Count >= 2 && stepSizes.Max() <= 4,
            "micro-step spacing changes through 2.0–3.2px during one move without a jump");

        var legacyCurve = HumanMouse.Config.FromProps(new Dictionary<string, object?> { { "curvePct", 180 } }, 0, 2300);
        Assert(legacyCurve.CurveMinPct == 165 && legacyCurve.CurveMaxPct == 195,
            "legacy fixed curvePct=180 migrates to dynamic 165–195 range");

        // ── v0.9.10: per-step movement duration range (moveTimeMin/moveTimeMax ms) ──
        var durFixed = HumanMouse.PlanMove(100, 400, 1100, 400,
            HumanMouse.Config.FromProps(new Dictionary<string, object?> { { "moveTimeMin", 800 }, { "moveTimeMax", 800 }, { "overshootChance", 0 }, { "midPauseChance", 0 } }, 0, 2300),
            new HumanMouse.PausePlanner(new Random(42)), new Random(42), 1920, 1080);
        int durSum = durFixed.Waypoints.Sum(w => w.DelayMs);
        Assert(durFixed.TargetMoveMs == 1080
               && Math.Abs(durSum - durFixed.TargetMoveMs) <= durFixed.Waypoints.Count,
            $"fixed 800ms baseline scales to the long-move target (target={durFixed.TargetMoveMs} sum={durSum}ms)");

        var durRangeCfg = HumanMouse.Config.FromProps(new Dictionary<string, object?> { { "moveTimeMin", 400 }, { "moveTimeMax", 1500 }, { "overshootChance", 0 }, { "midPauseChance", 0 } }, 0, 2300);
        var durSums = Enumerable.Range(0, 12)
            .Select(i => HumanMouse.PlanMove(100, 400, 1100, 400, durRangeCfg,
                new HumanMouse.PausePlanner(new Random(50 + i)), new Random(50 + i), 1920, 1080)
                .Waypoints.Sum(w => w.DelayMs)).ToList();
        Assert(durSums.Min() >= 500 && durSums.Max() <= 2200 && durSums.Distinct().Count() > 6,
            $"duration range 400–1500 draws a fresh distance-scaled target (min={durSums.Min()} max={durSums.Max()} distinct={durSums.Distinct().Count()})");

        var durNoSpeed = HumanMouse.PlanMove(100, 400, 1100, 400,
            HumanMouse.Config.FromProps(new Dictionary<string, object?> { { "moveTimeMin", 600 }, { "moveTimeMax", 600 }, { "midPauseChance", 0 } }, 0, 0),
            new HumanMouse.PausePlanner(new Random(77)), new Random(77), 1920, 1080);
        Assert(durNoSpeed.Waypoints.Sum(w => w.DelayMs) >= 500,
            "duration range still times the path when the global speed range is disabled (max 0)");
        Assert(HumanMouse.Config.FromProps(new Dictionary<string, object?>(), 0, 2300).MoveTimeMaxMs == 0,
            "old files without duration fields keep speed-driven timing (0/0)");

        // Natural-v1: duration follows distance while preserving the configured range as
        // the medium-distance baseline.  Transport cadence and endpoint math stay unchanged.
        int naturalShort = HumanMouse.DistanceScaledMoveMs(800, 800, 80, new Random(900));
        int naturalMedium = HumanMouse.DistanceScaledMoveMs(800, 800, 325, new Random(900));
        int naturalLong = HumanMouse.DistanceScaledMoveMs(800, 800, 650, new Random(900));
        Assert(naturalShort < naturalMedium && naturalMedium < naturalLong
               && naturalShort >= 120 && naturalLong <= 30000,
            $"natural-v1 duration scales with distance ({naturalShort} < {naturalMedium} < {naturalLong})");

        var naturalShortPlan = HumanMouse.PlanMove(100, 300, 250, 300,
            HumanMouse.Config.FromProps(new Dictionary<string, object?> {
                { "moveTimeMin", 800 }, { "moveTimeMax", 800 },
                { "overshootChance", 100 }, { "midPauseChance", 100 },
                { "midPauseMin", 70 }, { "midPauseMax", 70 }
            }, 0, 0),
            new HumanMouse.PausePlanner(new Random(901)), new Random(901), 1920, 1080);
        Assert(!naturalShortPlan.Overshot,
            "natural-v1 does not overshoot short moves");

        var naturalLongPlan = HumanMouse.PlanMove(100, 300, 700, 300,
            HumanMouse.Config.FromProps(new Dictionary<string, object?> {
                { "moveTimeMin", 800 }, { "moveTimeMax", 800 },
                { "overshootChance", 100 }, { "midPauseChance", 0 },
                { "curveMinPct", 3 }, { "curveMaxPct", 22 }
            }, 0, 0),
            new HumanMouse.PausePlanner(new Random(902)), new Random(902), 1920, 1080);
        Assert(naturalLongPlan.Overshot
               && naturalLongPlan.Waypoints[^1].X == 700
               && naturalLongPlan.Waypoints[^1].Y == 300,
            "natural-v1 long-move overshoot returns to the exact endpoint");

        // v0.9.9 — every key name captured by WPF must map to a Win32 global-hotkey VK.
        Assert(GlobalHotkeyService.TryParseGesture("Shift+Add", out var shiftAddMods, out var shiftAddVk)
               && (shiftAddMods & 0x4) != 0 && shiftAddVk == 0x6B,
            "global hotkey maps Shift+Add to VK_ADD (numpad +)");
        Assert(GlobalHotkeyService.TryParseGesture("Shift+Subtract", out _, out var subtractVk) && subtractVk == 0x6D,
            "global hotkey maps numpad Subtract");
        Assert(GlobalHotkeyService.TryParseGesture("Ctrl+Multiply", out var ctrlMulMods, out var multiplyVk)
               && (ctrlMulMods & 0x2) != 0 && multiplyVk == 0x6A,
            "global hotkey maps Ctrl+Multiply to VK_MULTIPLY");
        Assert(GlobalHotkeyService.TryParseGesture("Ctrl+Divide", out _, out var divideVk) && divideVk == 0x6F,
            "global hotkey maps numpad Divide");
        Assert(GlobalHotkeyService.TryParseGesture("Alt+OemPlus", out var altMods, out var oemPlusVk)
               && (altMods & 0x1) != 0 && oemPlusVk == 0xBB,
            "global hotkey maps Alt+OemPlus and preserves Alt modifier");
        Assert(GlobalHotkeyService.TryParseGesture("Ctrl+NumPad7", out _, out var numpad7Vk) && numpad7Vk == 0x67,
            "global hotkey maps NumPad0..9 keys");
        Assert(!GlobalHotkeyService.TryParseGesture("Ctrl+DefinitelyNotAKey", out _, out _),
            "invalid global hotkey is rejected deterministically");

        // generated script: human-mouse function wired, PS 5.1-safe RNG, balanced braces
        var hmScript = Path.Combine(Path.GetTempPath(), "ams_test_hm.ps1");
        try
        {
            ScriptGenerator.Generate(hmScript, new[]
            {
                new StepNode
                {
                    Type = "randomMousePosition",
                    Props = new Dictionary<string, object?> { { "x", 100 }, { "y", 100 }, { "w", 500 }, { "h", 400 }, { "curveMinPct", 120 }, { "curveMaxPct", 190 }, { "moveTimeMin", 400 }, { "moveTimeMax", 1500 } },
                },
                new StepNode
                {
                    Type = "mouseMove",
                    Props = new Dictionary<string, object?> { { "x", 700 }, { "y", 400 }, { "human", true } },
                },
                new StepNode
                {
                    Type = "typeText",
                    Props = new Dictionary<string, object?> { { "text", "hello world" }, { "wmin", 100 }, { "wmax", 100 } },
                },
            }, "AUTO", @"C:\x", 0, 2300, 1920, 1080);
            var ps = File.ReadAllText(hmScript);
            Assert(ps.Contains("Move-HumanMouse"), "generated script uses the human-mouse function");
            Assert(!ps.Contains("[Math]::Random"), "no invalid [Math]::Random in the PS 5.1 output");
            Assert(ps.Count(ch => ch == '{') == ps.Count(ch => ch == '}'), "generated script braces are balanced");
            Assert(ps.Contains("Move-HumanMouse 700 400"), "humanized Mouse Position step uses the engine too");
            Assert(ps.Contains("curvMin=120; curvMax=190"), "generated script preserves the full curvature range");
            Assert(ps.Contains("$curveKnots") && ps.Contains("$speedKnots") && ps.Contains("$spacingKnots"),
                "generated script continuously profiles curvature, speed and micro-step spacing");
            Assert(ps.Contains("mtMin=400; mtMax=1500"), "generated script carries the per-step duration range");
            Assert(ps.Contains("$targetMoveMs"), "generated script samples a fresh duration target per move");
            Assert(ps.Contains("Step-Delay 100"), "generated script emits word pauses as app-side Step-Delay");
            Assert(!ps.Contains("Send-Cmd \"DLY|"), "generated script never sends app-side DLY to the board");
        }
        finally { if (File.Exists(hmScript)) File.Delete(hmScript); }

        // v0.9.2 — the engine streams the whole path in ONE bridge call (send_path)
        var fbS = new FakeBridge();
        var rndStep2 = new StepNode
        {
            Type = "randomMousePosition",
            Props = new Dictionary<string, object?> { ["x"] = 100, ["y"] = 100, ["w"] = 500, ["h"] = 400 },
        };
        new RunEngine(fbS, _ => { }, 1920, 1080).RunAsync(new[] { rndStep2 }, CancellationToken.None).Wait();
        Assert(fbS.PathCalls == 1 && fbS.LastPath is { Count: > 10 } && !fbS.Sent.Any(c => c.StartsWith("MMOVE|")),
            $"randomMousePosition streams one dense path (calls={fbS.PathCalls}, pts={fbS.LastPath?.Count})");
        var lastPt = fbS.LastPath![^1];
        Assert(lastPt.X >= 100 && lastPt.X < 600 && lastPt.Y >= 100 && lastPt.Y < 500,
            $"stream ends inside the region (got {lastPt.X},{lastPt.Y})");

        // old bridge.py (no send_path) → control-point fallback with firmware smoothstep
        var fbOld = new FakeBridge { UnknownOp = true };
        new RunEngine(fbOld, _ => { }, 1920, 1080).RunAsync(new[] { rndStep2 }, CancellationToken.None).Wait();
        Assert(fbOld.PathCalls == 1 && fbOld.Sent.Any(c => c.StartsWith("MMOVE|") && c.EndsWith(",abs,1")),
            "old bridge falls back to human=1 control points");

        // ── Step 17: v0.9.16 — seeded GetCommands (deterministic RNG for tests) ──
        Console.WriteLine();
        Console.WriteLine("--- v0.9.16: seeded GetCommands ---");

        var seedNode = new StepNode
        {
            Type = "typeText",
            Props = new Dictionary<string, object?> { { "text", "hello world again" }, { "typoChance", 50 }, { "wordPauseChance", 50 }, { "wmin", 10 }, { "wmax", 200 } },
        };
        var seededA = StepDefinitions.GetCommands(seedNode, 42);
        var seededB = StepDefinitions.GetCommands(seedNode, 42);
        Assert(seededA.SequenceEqual(seededB),
            "v0.9.16: GetCommands(node, 42) twice → identical output (deterministic)");

        var distinctCounts = new HashSet<int>();
        for (int si = 0; si < 24; si++) distinctCounts.Add(StepDefinitions.GetCommands(seedNode, 1000 + si).Count);
        Assert(distinctCounts.Count > 1,
            $"v0.9.16: 24 seeds produce variety ({distinctCounts.Count} distinct counts; counts are a narrow space so full uniqueness is not guaranteed)");

        var distinctSeqs = new HashSet<string>();
        for (int si = 0; si < 24; si++) distinctSeqs.Add(string.Join("\u0001", StepDefinitions.GetCommands(seedNode, 1000 + si)));
        Assert(distinctSeqs.Count > 20,
            $"v0.9.16: 24 seeds produce near-unique sequences ({distinctSeqs.Count}/24 distinct)");

        var afterSeeded = StepDefinitions.GetCommands(seedNode);
        Assert(afterSeeded.Count > 0 && afterSeeded.Any(c => c.StartsWith("KTEXT|")),
            "v0.9.16: default RNG restored and unseeded path works after seeded calls");

        // ── Step 18: v0.9.17 — single-key hotkey capture + blocklist ──────────────────────
        Console.WriteLine();
        Console.WriteLine("--- v0.9.17: single-key hotkeys ---");

        // Single F-keys accepted as bare keys (mods == 0)
        Assert(GlobalHotkeyService.TryParseGesture("F7", out var f7Mods, out var f7Vk)
               && f7Mods == 0 && f7Vk == 0x76,
            "v0.9.17: TryParseGesture('F7') → mods=0, vk=0x76 (VK_F7)");

        Assert(GlobalHotkeyService.TryParseGesture("F24", out _, out var f24Vk)
               && f24Vk == 0x87,
            "v0.9.17: TryParseGesture('F24') → vk=0x87 (VK_F24)");

        // Numpad keys accepted as bare keys
        Assert(GlobalHotkeyService.TryParseGesture("Multiply", out var mulMods, out var mulVk)
               && mulMods == 0 && mulVk == 0x6A,
            "v0.9.17: TryParseGesture('Multiply') → mods=0, vk=0x6A (VK_MULTIPLY)");

        Assert(GlobalHotkeyService.TryParseGesture("Add", out var addMods, out var addVk)
               && addMods == 0 && addVk == 0x6B,
            "v0.9.17: TryParseGesture('Add') → mods=0, vk=0x6B (VK_ADD)");

        Assert(GlobalHotkeyService.TryParseGesture("Decimal", out _, out var decVk)
               && decVk == 0x6E,
            "v0.9.17: TryParseGesture('Decimal') → vk=0x6E (VK_DECIMAL)");

        Assert(GlobalHotkeyService.TryParseGesture("Subtract", out _, out var subVk)
               && subVk == 0x6D,
            "v0.9.17: TryParseGesture('Subtract') → vk=0x6D (VK_SUBTRACT)");

        Assert(GlobalHotkeyService.TryParseGesture("Divide", out _, out var divVk)
               && divVk == 0x6F,
            "v0.9.17: TryParseGesture('Divide') → vk=0x6F (VK_DIVIDE)");

        // Navigation keys accepted as bare
        Assert(GlobalHotkeyService.TryParseGesture("Insert", out var insMods, out _)
               && insMods == 0,
            "v0.9.17: TryParseGesture('Insert') → mods=0");
        Assert(GlobalHotkeyService.TryParseGesture("Delete", out _, out _)
               && true,
            "v0.9.17: TryParseGesture('Delete') parses OK");
        Assert(GlobalHotkeyService.TryParseGesture("Home", out _, out _)
               && true,
            "v0.9.17: TryParseGesture('Home') parses OK");
        Assert(GlobalHotkeyService.TryParseGesture("End", out _, out _)
               && true,
            "v0.9.17: TryParseGesture('End') parses OK");
        Assert(GlobalHotkeyService.TryParseGesture("PageUp", out _, out _)
               && true,
            "v0.9.17: TryParseGesture('PageUp') parses OK");
        Assert(GlobalHotkeyService.TryParseGesture("PageDown", out _, out _)
               && true,
            "v0.9.17: TryParseGesture('PageDown') parses OK");
        Assert(GlobalHotkeyService.TryParseGesture("PrintScreen", out _, out _)
               && true,
            "v0.9.17: TryParseGesture('PrintScreen') parses OK");
        Assert(GlobalHotkeyService.TryParseGesture("Scroll", out _, out _)
               && true,
            "v0.9.17: TryParseGesture('Scroll') parses OK");
        Assert(GlobalHotkeyService.TryParseGesture("Pause", out _, out _)
               && true,
            "v0.9.17: TryParseGesture('Pause') parses OK");

        // Combo paths still work correctly
        Assert(GlobalHotkeyService.TryParseGesture("Ctrl+Add", out var ctrlAddMods, out var ctrlAddVk)
               && (ctrlAddMods & 0x2) != 0 && ctrlAddVk == 0x6B,
            "v0.9.17: TryParseGesture('Ctrl+Add') mods has MOD_CTRL, vk=0x6B");
        Assert(GlobalHotkeyService.TryParseGesture("Shift+F1", out var sF1Mods, out var sF1Vk)
               && (sF1Mods & 0x4) != 0 && sF1Vk == 0x70,
            "v0.9.17: TryParseGesture('Shift+F1') mods has MOD_SHIFT, vk=0x70");
        Assert(GlobalHotkeyService.TryParseGesture("Alt+Insert", out var altInsMods, out _)
               && (altInsMods & 0x1) != 0,
            "v0.9.17: TryParseGesture('Alt+Insert') mods has MOD_ALT");

        // Invalid combos still rejected
        Assert(!GlobalHotkeyService.TryParseGesture("Ctrl+DefinitelyNotAKey", out _, out _),
            "v0.9.17: invalid combo still rejected");

        // Blocklist: plain letters, digits, Space, Enter, Tab, Back are NOT safe as single keys
        // (these are checked via IsSafeAsSingleKey — test via TryParseGesture with modifier
        //  to confirm combos still work, then rely on the IsSafeAsSingleKey logic)
        Assert(GlobalHotkeyService.TryParseGesture("Ctrl+A", out var caMods, out _)
               && (caMods & 0x2) != 0,
            "v0.9.17: 'Ctrl+A' accepted as combo (modifier present)");
        Assert(GlobalHotkeyService.TryParseGesture("Space", out var spMods, out _)
               && spMods == 0,
            "v0.9.17: TryParseGesture('Space') parses OK — safety check is in UI, not parser");
        Assert(GlobalHotkeyService.TryParseGesture("Enter", out var enMods, out _)
               && enMods == 0,
            "v0.9.17: TryParseGesture('Enter') parses OK — safety check is in UI, not parser");

        // The IsSafeAsSingleKey guard rejects printable/OEM keys at the UI layer:
        // tested implicitly via the hint-message path; here we confirm the parser
        // itself accepts them so the service can work for combo bindings.
        Assert(GlobalHotkeyService.TryParseGesture("A", out var aMods, out _)
               && aMods == 0,
            "v0.9.17: TryParseGesture('A') → mods=0 (parser accepts; UI blocks unsafe single keys)");
        Assert(GlobalHotkeyService.TryParseGesture("Tab", out _, out _)
               && true,
            "v0.9.17: TryParseGesture('Tab') parses OK");
        Assert(GlobalHotkeyService.TryParseGesture("Back", out _, out _)
               && true,
            "v0.9.17: TryParseGesture('Back') parses OK");
        Assert(GlobalHotkeyService.TryParseGesture("OemMinus", out _, out _)
               && true,
            "v0.9.17: TryParseGesture('OemMinus') parses OK");
        Assert(GlobalHotkeyService.TryParseGesture("OemPlus", out _, out _)
               && true,
            "v0.9.17: TryParseGesture('OemPlus') parses OK");

        // Modifiers alone still rejected
        Assert(!GlobalHotkeyService.TryParseGesture("Ctrl", out _, out _),
            "v0.9.17: bare 'Ctrl' rejected (not a valid gesture)");
        Assert(!GlobalHotkeyService.TryParseGesture("Shift", out _, out _),
            "v0.9.17: bare 'Shift' rejected");
        Assert(!GlobalHotkeyService.TryParseGesture("Alt", out _, out _),
            "v0.9.17: bare 'Alt' rejected");

        // Empty / whitespace rejected
        Assert(!GlobalHotkeyService.TryParseGesture("", out _, out _),
            "v0.9.17: empty gesture rejected");
        Assert(!GlobalHotkeyService.TryParseGesture("   ", out _, out _),
            "v0.9.17: whitespace gesture rejected");

        // ── Step 19: v0.9.18 — image-detection accuracy ──────────────────────
        // Regression cover for the false-positive report: a red template was matching a
        // visually unrelated blue icon. Root causes were (a) (B+G+R)/3 grayscale, which maps
        // pure red, green and blue all to 85, (b) NCC being blind to brightness/contrast,
        // (c) "70% similar" being fed in as a raw NCC 0.70 bar, and (d) a step-3 strided
        // coarse scan that aliased past the true peak.
        Console.WriteLine();
        Console.WriteLine("--- Step 19: v0.9.18 image detection ---");

        // packed BGR canvas helpers (stride = w*3), mirroring VisionService.ToRgbPooled
        static byte[] V18Canvas(int w, int h, byte b, byte g, byte r)
        {
            var buf = new byte[w * h * 3];
            for (int i = 0; i < w * h; i++) { buf[i * 3] = b; buf[i * 3 + 1] = g; buf[i * 3 + 2] = r; }
            return buf;
        }
        static void V18Blit(byte[] dst, int dw, int sx, int sy, byte[] src, int sw, int sh)
        {
            for (int j = 0; j < sh; j++)
                Array.Copy(src, j * sw * 3, dst, ((sy + j) * dw + sx) * 3, sw * 3);
        }
        static byte[] V18Icon(int w, int h, byte fb, byte fg, byte fr, byte bb, byte bg, byte br)
        {
            var buf = V18Canvas(w, h, bb, bg, br);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool inside = y >= h / 4 && y < 3 * h / 4 && x >= w / 4 && x < 3 * w / 4;
                bool edge = x < 2 || y < 2 || x >= w - 2 || y >= h - 2;
                if (!inside && !edge) continue;
                int i = (y * w + x) * 3;
                buf[i] = fb; buf[i + 1] = fg; buf[i + 2] = fr;
            }
            return buf;
        }

        const int V18W = 200, V18H = 120, V18T = 40;
        var v18Red = V18Icon(V18T, V18T, 0, 0, 220, 245, 245, 245);
        var v18Blue = V18Icon(V18T, V18T, 220, 0, 0, 245, 245, 245);

        // 19.1 — similarity calibration: "70%" must no longer mean a raw NCC of 0.70
        Assert(Math.Abs(VisionService.MapSimilarity(70) - 0.921) < 0.002,
            "v0.9.18: 70% similarity maps to NCC ~0.921 (was 0.70)");
        Assert(VisionService.MapSimilarity(1) >= 0.85,
            "v0.9.18: NCC bar never drops below 0.85, even at 1%");
        Assert(VisionService.MapSimilarity(100) > VisionService.MapSimilarity(85)
               && VisionService.MapSimilarity(85) > VisionService.MapSimilarity(70),
            "v0.9.18: similarity -> NCC mapping is monotonic");
        Assert(VisionService.ColorTolerance(70) > VisionService.ColorTolerance(100),
            "v0.9.18: colour tolerance tightens as similarity rises");

        // 19.2 — true positive, aligned to the pyramid factor
        var v18Scene = V18Canvas(V18W, V18H, 245, 245, 245);
        V18Blit(v18Scene, V18W, 120, 60, v18Red, V18T, V18T);
        var v18Hit = VisionService.MatchForTest(v18Scene, V18W, V18H, v18Red, V18T, V18T, 70);
        Assert(v18Hit.HasValue && v18Hit.Value.x == 120 && v18Hit.Value.y == 60,
            "v0.9.18: aligned red icon located at exactly (120,60)");

        // 19.3 — true positive at every 1px sub-grid offset (the old step-3 scan missed these)
        int v18OffFound = 0;
        for (int v18Off = 0; v18Off < 6; v18Off++)
        {
            var v18S = V18Canvas(V18W, V18H, 245, 245, 245);
            V18Blit(v18S, V18W, 100 + v18Off, 40 + v18Off, v18Red, V18T, V18T);
            var v18H2 = VisionService.MatchForTest(v18S, V18W, V18H, v18Red, V18T, V18T, 70);
            if (v18H2.HasValue && v18H2.Value.x == 100 + v18Off && v18H2.Value.y == 40 + v18Off)
                v18OffFound++;
        }
        Assert(v18OffFound == 6,
            $"v0.9.18: red icon found at all 6 sub-grid offsets (got {v18OffFound}/6)");

        // 19.4 — THE REPORTED BUG: a blue icon must NOT satisfy a red template
        v18Scene = V18Canvas(V18W, V18H, 245, 245, 245);
        V18Blit(v18Scene, V18W, 120, 60, v18Blue, V18T, V18T);
        Assert(!VisionService.MatchForTest(v18Scene, V18W, V18H, v18Red, V18T, V18T, 70).HasValue,
            "v0.9.18: blue icon REJECTED for a red template at 70% (was a false positive)");

        bool v18AnyFalse = false;
        for (int v18Sim = 1; v18Sim <= 100; v18Sim += 11)
            if (VisionService.MatchForTest(v18Scene, V18W, V18H, v18Red, V18T, V18T, v18Sim).HasValue)
                v18AnyFalse = true;
        Assert(!v18AnyFalse,
            "v0.9.18: blue-vs-red never matches at ANY similarity 1..100");

        // 19.5 — low-contrast template on a flat screen must not match
        var v18Faint = V18Icon(V18T, V18T, 130, 130, 130, 134, 134, 134);
        var v18Flat = V18Canvas(V18W, V18H, 128, 128, 128);
        Assert(!VisionService.MatchForTest(v18Flat, V18W, V18H, v18Faint, V18T, V18T, 70).HasValue,
            "v0.9.18: faint low-contrast template rejected on a flat screen");

        // 19.6 — a zero-variance template can never match
        var v18FlatTpl = V18Canvas(V18T, V18T, 200, 200, 200);
        Assert(!VisionService.MatchForTest(v18Flat, V18W, V18H, v18FlatTpl, V18T, V18T, 70).HasValue,
            "v0.9.18: uniform template rejected (zero variance)");

        // 19.7 — legitimate duplicates must STILL match (the distinctiveness gate must not
        //          reject a template that genuinely appears twice on screen)
        v18Scene = V18Canvas(V18W, V18H, 245, 245, 245);
        V18Blit(v18Scene, V18W, 10, 61, v18Red, V18T, V18T);
        V18Blit(v18Scene, V18W, 140, 61, v18Red, V18T, V18T);
        Assert(VisionService.MatchForTest(v18Scene, V18W, V18H, v18Red, V18T, V18T, 70).HasValue,
            "v0.9.18: two identical icons still HIT (duplicate, not ambiguity)");

        // 19.8 — small 16x16 templates get the 0.95 floor but must still match themselves
        var v18SmRed = V18Icon(16, 16, 0, 0, 220, 245, 245, 245);
        var v18SmBlue = V18Icon(16, 16, 220, 0, 0, 245, 245, 245);
        v18Scene = V18Canvas(V18W, V18H, 245, 245, 245);
        V18Blit(v18Scene, V18W, 101, 51, v18SmRed, 16, 16);
        Assert(VisionService.MatchForTest(v18Scene, V18W, V18H, v18SmRed, 16, 16, 70).HasValue,
            "v0.9.18: 16x16 red icon matches itself under the 0.95 floor");
        v18Scene = V18Canvas(V18W, V18H, 245, 245, 245);
        V18Blit(v18Scene, V18W, 101, 51, v18SmBlue, 16, 16);
        Assert(!VisionService.MatchForTest(v18Scene, V18W, V18H, v18SmRed, 16, 16, 70).HasValue,
            "v0.9.18: 16x16 blue icon rejected for a red template");

        // 19.9 — degenerate inputs must be rejected, never crash
        Assert(!VisionService.MatchForTest(V18Canvas(20, 20, 0, 0, 0), 20, 20,
                                          v18Red, V18T, V18T, 70).HasValue,
            "v0.9.18: template larger than the search area rejected safely");

        // ── Step 20: v0.9.19 — foreground colour gate ──────────────────────
        // v0.9.18 averaged the colour difference over the WHOLE template, so a captured icon
        // (small coloured glyph on a plain field) diluted a wrong glyph colour below the
        // tolerance and colour looked disabled. Also the "genuine duplicate" rescue accepted
        // two same-shape wrong-colour icons without checking the runner-up's colour.
        Console.WriteLine();
        Console.WriteLine("--- Step 20: v0.9.19 foreground colour gate ---");

        // realistic captured icon: gw x gw coloured glyph centred on a plain w x h field
        static byte[] V20Glyph(int w, int h, int gw, byte fb, byte fg, byte fr, byte bb, byte bg, byte br)
        {
            var buf = V18Canvas(w, h, bb, bg, br);
            int gx = (w - gw) / 2, gy = (h - gw) / 2;
            for (int y = gy; y < gy + gw; y++)
            for (int x = gx; x < gx + gw; x++)
            {
                int i = (y * w + x) * 3;
                buf[i] = fb; buf[i + 1] = fg; buf[i + 2] = fr;
            }
            return buf;
        }

        var v20Red = V20Glyph(V18T, V18T, 12, 0, 0, 220, 245, 245, 245);
        var v20Blue = V20Glyph(V18T, V18T, 12, 220, 0, 0, 245, 245, 245);

        // 20.1 — THE DILUTION BUG: wrong glyph colour on the same silhouette must be
        //         rejected; v0.9.18's whole-template MAD (13.2) sat below the tolerance (18.5)
        var v20Scene = V18Canvas(V18W, V18H, 245, 245, 245);
        V18Blit(v20Scene, V18W, 120, 60, v20Blue, V18T, V18T);
        Assert(!VisionService.MatchForTest(v20Scene, V18W, V18H, v20Red, V18T, V18T, 70).HasValue,
            "v0.9.19: small blue glyph REJECTED for red template at 70% (dilution bug)");
        Assert(!VisionService.MatchForTest(v20Scene, V18W, V18H, v20Red, V18T, V18T, 85).HasValue,
            "v0.9.19: small blue glyph REJECTED for red template at 85%");

        // 20.2 — the same template must still find the real icon at an off-grid position
        v20Scene = V18Canvas(V18W, V18H, 245, 245, 245);
        V18Blit(v20Scene, V18W, 101, 51, v20Red, V18T, V18T);
        var v20Hit = VisionService.MatchForTest(v20Scene, V18W, V18H, v20Red, V18T, V18T, 70);
        Assert(v20Hit.HasValue && v20Hit.Value.x == 101 && v20Hit.Value.y == 51,
            "v0.9.19: real small red glyph still found at exactly (101,51)");

        // 20.3 — genuine duplicates (same colour, twice) must still HIT
        v20Scene = V18Canvas(V18W, V18H, 245, 245, 245);
        V18Blit(v20Scene, V18W, 10, 61, v20Red, V18T, V18T);
        V18Blit(v20Scene, V18W, 140, 61, v20Red, V18T, V18T);
        Assert(VisionService.MatchForTest(v20Scene, V18W, V18H, v20Red, V18T, V18T, 70).HasValue,
            "v0.9.19: two identical small glyphs still HIT (colour-verified duplicate)");

        // 20.4 — two WRONG-colour lookalikes must NOT be rescued by the duplicate clause
        v20Scene = V18Canvas(V18W, V18H, 245, 245, 245);
        V18Blit(v20Scene, V18W, 10, 61, v20Blue, V18T, V18T);
        V18Blit(v20Scene, V18W, 140, 61, v20Blue, V18T, V18T);
        Assert(!VisionService.MatchForTest(v20Scene, V18W, V18H, v20Red, V18T, V18T, 70).HasValue,
            "v0.9.19: two blue lookalikes REJECTED (duplicate rescue is colour-checked)");

        // 20.5 — sliver templates can never match reliably
        var v20Sliver = V18Canvas(2, 48, 200, 200, 200);
        Assert(!VisionService.MatchForTest(v20Scene, V18W, V18H, v20Sliver, 2, 48, 70).HasValue,
            "v0.9.19: 2x48 sliver template rejected outright");

        // 20.6 — a slight hue shift within tolerance must STILL match (no over-strictness)
        var v20RedSoft = V20Glyph(V18T, V18T, 12, 10, 8, 225, 245, 245, 245);
        v20Scene = V18Canvas(V18W, V18H, 245, 245, 245);
        V18Blit(v20Scene, V18W, 97, 53, v20RedSoft, V18T, V18T);
        v20Hit = VisionService.MatchForTest(v20Scene, V18W, V18H, v20Red, V18T, V18T, 85);
        Assert(v20Hit.HasValue && v20Hit.Value.x == 97 && v20Hit.Value.y == 53,
            "v0.9.19: slight hue shift (220->225) still HITs at 85%");

        // ── Step 21: v0.9.20/v0.9.21 — parallel interleave + bridge discovery ─────────────────
        // Regression cover for the Parallel Group freeze reports: v0.9.19's 8-char KTEXT chunks
        // held the single bridge channel for 0.6–2.4 s each; v0.9.20's 1-char ops still carried
        // board-side per-key delays (recorded 408 ms median key cadence = double-paced typing);
        // v0.9.21 types via instant KTEXT|0,0 ops with the cadence fully app-side.
        // and the release-zip layout (exe inside src\Ams.UI) could not find bridge.py.
        Console.WriteLine();
        Console.WriteLine("--- Step 21: v0.9.20/v0.9.21 parallel interleave + bridge discovery ---");

        // 21.1 — KTEXT splits into one op per char, header preserved, payload intact
        var v20Ops = RunEngine.ChunkKtextForParallel("KTEXT|80,300,hello!");
        Assert(v20Ops.Count == 6, "v0.9.20: KTEXT chunked to 1 char per op (got " + v20Ops.Count + ")");
        Assert(v20Ops[0] == "KTEXT|0,0,h" && v20Ops[5] == "KTEXT|0,0,!",
            "v0.9.21: per-char ops use the instant 0,0 header (no board-side delay)");
        string v20Joined = string.Concat(v20Ops.ConvertAll(o => o.Substring("KTEXT|0,0,".Length)));
        Assert(v20Joined == "hello!", "v0.9.20: chunked payloads reassemble to the original text");

        // 21.2 — payload containing a comma survives (header parsing stops at the 2nd comma)
        var v20Comma = RunEngine.ChunkKtextForParallel("KTEXT|10,20,a,b");
        Assert(v20Comma.Count == 3 && v20Comma[1] == "KTEXT|0,0,,",
            "v0.9.21: comma inside the payload is a literal char, not a delimiter");

        // 21.3 — bridge.py discovery: an exe deep in the repo tree finds the repo-level bridge\
        string v20Tmp = Path.Combine(Path.GetTempPath(), "ams-bridge-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(v20Tmp, "src", "Ams.UI"));
            Directory.CreateDirectory(Path.Combine(v20Tmp, "bridge"));
            File.WriteAllText(Path.Combine(v20Tmp, "bridge", "bridge.py"), "# test");
            var v20Cands = PythonBoardBridge.BridgeScriptCandidates(
                Path.Combine(v20Tmp, "src", "Ams.UI") + Path.DirectorySeparatorChar);
            string? v20Found = null;
            foreach (var c in v20Cands) { if (File.Exists(c)) { v20Found = c; break; } }
            Assert(v20Found != null && v20Found.Replace('/', Path.DirectorySeparatorChar)
                       .EndsWith("bridge" + Path.DirectorySeparatorChar + "bridge.py"),
                "v0.9.20: bridge.py found by walking up from a deep exe folder (release-zip layout)");

            // 21.4 — flat publish layout: bridge.py loose next to the exe
            string v20Flat = Path.Combine(Path.GetTempPath(), "ams-flat-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(v20Flat);
                File.WriteAllText(Path.Combine(v20Flat, "bridge.py"), "# test");
                var v20Cands2 = PythonBoardBridge.BridgeScriptCandidates(v20Flat + Path.DirectorySeparatorChar);
                Assert(v20Cands2.Count >= 2 && File.Exists(v20Cands2[1]),
                    "v0.9.20: flat-publish bridge.py (loose next to the exe) is a candidate");
            }
            finally { try { Directory.Delete(v20Flat, true); } catch { } }
        }
        finally { try { Directory.Delete(v20Tmp, true); } catch { } }

        // ── Step 22: v0.9.23 — human-calibrated defaults ────────────────────
        // Recorded human baseline (user's own 57.8 s recording): mouse velocity median
        // ~140 px/s, p99 ~452; typing median ~204 ms/key. The old factory defaults
        // (0–2300 px/s, 25–70 ms keys) sat far outside the human envelope.
        Console.WriteLine();
        Console.WriteLine("--- Step 22: v0.9.23 human-calibrated defaults ---");

        // v0.9.24 — recalibrated from the user's dense hand recording (median ≈980 px/s):
        Assert(new AppSettings().MouseMoveSpeedMin == 300 && new AppSettings().MouseMoveSpeedMax == 2000,
            "v0.9.24: human mouse speed defaults 300/2000 px/s (dense recording)");
        Assert(AppSettings.NormalizeSpeedDefaults(0, 2300) == (300, 2000),
            "v0.9.24: untouched factory range migrates to the current human defaults");
        Assert(AppSettings.NormalizeSpeedDefaults(200, 800) == (200, 800),
            "v0.9.23: custom user speed range preserved");
        Assert(AppSettings.NormalizeSpeedDefaults(0, 0) == (0, 0),
            "v0.9.23: deliberately disabled timing (0/0) preserved");
        Assert(new HumanMouse.Config().SpeedMaxPxPerSec == 2000,
            "v0.9.24: HumanMouse.Config speed default aligned to 2000");
        Assert(StepDefinitions.TypingFallbackMinMs == 80 && StepDefinitions.TypingFallbackMaxMs == 220,
            "v0.9.23: human typing fallback 80/220 ms");
        Assert(new AppSettings().TypeKeyMinMs == 80 && new AppSettings().TypeKeyMaxMs == 220,
            "v0.9.23: personal typing cadence defaults 80/220 ms");

        // ── Step 23: v0.9.23 — typing-aware mouse suppression + calibration math ────────────
        // v0.9.21 fixed the queueing (no freezes) but the mouse stayed 100% active while a
        // sibling typed (p90 mouse-after-key latency 43 ms vs the human's 982 ms). The fix:
        // half speed + irregular micro-rests while typing is active.
        Console.WriteLine();
        Console.WriteLine("--- Step 23: v0.9.23 typing-aware suppression + calibration ---");

        Assert(RunEngine.IsTypingActive(1000, 1800) && !RunEngine.IsTypingActive(1000, 1950)
               && !RunEngine.IsTypingActive(0, 100),
            "v0.9.23: typing-active window is 900 ms and needs a real first keystroke");
        Assert(RunEngine.SuppressedMouseDelayMs(8) == 16 && RunEngine.SuppressedMouseDelayMs(0) == 1,
            "v0.9.23: suppression halves mouse speed, never produces 0 ms");

        // calibration: synthetic typist ~125 ms/key (uniform 100–149) + mouse ~300 px/s
        var v23Keys = new List<long>();
        var v23Rng = new Random(42);
        long v23T = 0;
        for (int i = 0; i < 60; i++) { v23Keys.Add(v23T); v23T += 100 + v23Rng.Next(50); }
        var v23Mouse = new List<(long, double, double)>();
        double v23X = 0, v23Y = 0;
        for (int i = 0; i < 300; i++)
        {
            v23T += 10;
            double step = 2.4 + v23Rng.NextDouble() * 1.4;   // 240–380 px/s at 10 ms cadence
            v23X += step; v23Mouse.Add((v23T, v23X, v23Y));
        }
        var v23Cal = CalibrationAnalyzer.Compute(v23Keys, v23Mouse);
        Assert(v23Cal is not null, "v0.9.23: calibration accepts a full sample set");
        Assert(v23Cal!.KeyMinMs >= 40 && v23Cal.KeyMaxMs <= 600
               && v23Cal.KeyMinMs <= v23Cal.KeyMedianMs && v23Cal.KeyMedianMs <= v23Cal.KeyMaxMs,
            $"v0.9.23: typing range sane and brackets the median (got {v23Cal.KeyMinMs}/{v23Cal.KeyMedianMs:F0}/{v23Cal.KeyMaxMs})");
        Assert(v23Cal.MouseMinPxPerSec >= 150 && v23Cal.MouseMaxPxPerSec <= 2500
               && v23Cal.MouseMinPxPerSec <= v23Cal.MouseMedianPxPerSec && v23Cal.MouseMedianPxPerSec <= v23Cal.MouseMaxPxPerSec,
            $"v0.9.23: mouse range sane and brackets the median (got {v23Cal.MouseMinPxPerSec}/{v23Cal.MouseMedianPxPerSec:F0}/{v23Cal.MouseMaxPxPerSec})");
        Assert(Math.Abs(v23Cal.MouseMedianPxPerSec - 300) < 90,
            $"v0.9.23: mouse median recovers the synthetic ~300 px/s (got {v23Cal.MouseMedianPxPerSec:F0})");
        Assert(CalibrationAnalyzer.Compute(new List<long> { 0, 100 }, v23Mouse) is null,
            "v0.9.23: calibration refuses too few keystrokes");
        Assert(CalibrationAnalyzer.Compute(v23Keys, new List<(long, double, double)> { (0, 0, 0), (10, 5, 0) }) is null,
            "v0.9.23: calibration refuses too few mouse samples");

        // ── Step 24: v0.9.24 — Persian step dialogs + hand-speed recalibration ────────────
        Console.WriteLine();
        Console.WriteLine("--- Step 24: v0.9.24 Persian dialogs + speed recalibration ---");

        static bool V24HasFa(string s) => s.Any(c => c >= '؀' && c <= 'ۿ');

        Assert(V24HasFa(StepTextsFa.Get("typeText", "hmin", "x")),
            "v0.9.24: shared key hmin has a Persian description");
        Assert(StepTextsFa.Get("typeText", "text", "en") != StepTextsFa.Get("comment", "text", "en"),
            "v0.9.24: colliding key 'text' resolves per step type");
        Assert(StepTextsFa.Get(null, "curveMinPct", "en") != "en",
            "v0.9.24: shared key works without a step type");
        Assert(StepTextsFa.Get("typeText", "no-such-key", "English fallback") == "English fallback",
            "v0.9.24: unknown keys fall back to the English label");
        Assert(StepDefinitions.FindTypeByFields(StepDefinitions.Get("typeText").Fields) == "typeText",
            "v0.9.24: reverse field lookup resolves the step type");
        Assert(StepDefinitions.Get("randomMousePosition").Fields.First(f => f.Key == "overshootChance").Default == "25",
            "v0.9.24: overshoot default 25% (user's hand overshot 2/4 recorded moves)");
        Assert(StepDefinitions.Get("randomMousePosition").Fields.First(f => f.Key == "midPauseMax").Default == "500",
            "v0.9.24: mid-pause default max 500ms (recorded hesitations ran 297–485ms)");

        // ── Step 25: v0.9.25 — full Persian coverage (all dialogs + panels) ────────────
        // v0.9.24 covered only the generic StepDialog; the special dialogs (Find Image,
        // Options, Region Picker, Calibration) and the main window panels stayed English.
        Console.WriteLine();
        Console.WriteLine("--- Step 25: v0.9.25 full Persian coverage ---");

        // 25.1 — completeness sweep: EVERY field of EVERY step definition is covered
        int v25Total = 0, v25Missing = 0;
        foreach (var kv in StepDefinitions.All)
        foreach (var f in kv.Value.Fields)
        {
            v25Total++;
            if (!StepTextsFa.Has(kv.Key, f.Key)) v25Missing++;
        }
        Assert(v25Total > 100 && v25Missing == 0,
            $"v0.9.25: every step field has a Persian entry ({v25Total} fields, {v25Missing} missing)");

        // 25.2 — special dialogs are Persian on disk (walk up to find the repo layout)
        static string V25ReadView(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 9 && dir is not null; i++, dir = dir.Parent)
            {
                foreach (var cand in new[] {
                    Path.Combine(dir.FullName, "ams-shell", "src", "Ams.UI", "Views", name),
                    Path.Combine(dir.FullName, "ams-shell", "src", "Ams.UI", name),
                    Path.Combine(dir.FullName, "src", "Ams.UI", "Views", name),
                    Path.Combine(dir.FullName, "src", "Ams.UI", name) })
                    if (File.Exists(cand)) return File.ReadAllText(cand);
            }
            throw new FileNotFoundException("view not found in repo layout: " + name);
        }

        var v25Find = V25ReadView("SearchPictureDialog.xaml");
        Assert(V24HasFa(v25Find) && !v25Find.Contains("BMP Similar") && !v25Find.Contains("What to do when")
               && !v25Find.Contains("Step Name:") && !v25Find.Contains("Search the picture in"),
            "v0.9.25: Find Image dialog fully Persian (headers, labels, buttons, combo items)");
        var v25Opt = V25ReadView("OptionsDialog.xaml");
        Assert(V24HasFa(v25Opt) && !v25Opt.Contains("Python folder") && !v25Opt.Contains("Set key combos")
               && !v25Opt.Contains("COM port"),
            "v0.9.25: Options dialog fully Persian (tabs, labels, hotkey rows)");
        var v25Cal = V25ReadView("CalibrationWindow.xaml");
        Assert(V24HasFa(v25Cal) && !v25Cal.Contains("Type naturally"),
            "v0.9.25: Calibration window Persian");
        var v25Region = V25ReadView("RegionPickerWindow.xaml");
        Assert(V24HasFa(v25Region) && !v25Region.Contains("Drag to select"),
            "v0.9.25: Region picker Persian");
        var v25Main = V25ReadView("MainWindow.xaml");
        Assert(V24HasFa(v25Main) && !v25Main.Contains("Shutdown computer when repeating finished")
               && !v25Main.Contains("Play Options") && !v25Main.Contains("Delay after step"),
            "v0.9.25: main window panels Persian (Play Options / Inspector / Serial log)");
        // option VALUES stay English (user rule)
        var ms_pat = "Content=" + "\u0022" + "ms" + "\u0022";
        var sec_pat = "Content=" + "\u0022" + "second" + "\u0022";
        var ins_pat = "Header=" + "\u0022" + "_Insert" + "\u0022";
        Assert(v25Find.Contains(ms_pat) && v25Find.Contains(sec_pat) && v25Main.Contains(ins_pat),
            "v0.9.25: option values/units and menus stay English per the user rule");
        // ── Step 26: v0.9.26 — smooth mouse glide while typing (capped suppression + rare rests) ────────────
        // Recorded evidence (app run, 72.6 s parallel type+mouse): the mouse froze mid-move for
        // 12.4 s and 11.0 s while keystrokes flowed normally; 77 pauses >=100 ms during typing.
        // Dense hand record: in-typing mouse gaps p90 = 6 ms, max = 351 ms, zero freezes >=500 ms.
        // Causes: unbounded doubling of move-time-stretched waypoint delays + every-2-6-key rests
        // firing back-to-back (the 900 ms typing-active window stays on for a whole burst).
        Console.WriteLine();
        Console.WriteLine("--- Step 26: v0.9.26 capped suppression + rare typing rests ---");

        Assert(RunEngine.SuppressedMouseDelayMs(8) == 16 && RunEngine.SuppressedMouseDelayMs(0) == 1,
            "v0.9.26: ordinary micro-steps still double, never 0 ms");
        Assert(RunEngine.SuppressedMouseDelayMs(100) == RunEngine.SuppressedMouseDelayCapMs
               && RunEngine.SuppressedMouseDelayMs(2000) == RunEngine.SuppressedMouseDelayCapMs
               && RunEngine.SuppressedMouseDelayMs(7000) == RunEngine.SuppressedMouseDelayCapMs,
            "v0.9.26: suppressed delay capped — a stretched move-time tail cannot park the cursor");
        Assert(RunEngine.SuppressedMouseDelayCapMs >= 100 && RunEngine.SuppressedMouseDelayCapMs <= 200,
            $"v0.9.26: cap stays sub-perceptual (got {RunEngine.SuppressedMouseDelayCapMs} ms)");

        var v26Rng = new Random(26);
        int v26MinE = int.MaxValue, v26MaxE = int.MinValue;
        for (int i = 0; i < 20000; i++)
        {
            int e = RunEngine.NextTypingRestEveryKeys(v26Rng);
            v26MinE = Math.Min(v26MinE, e); v26MaxE = Math.Max(v26MaxE, e);
        }
        Assert(v26MinE == 10 && v26MaxE == 24,
            $"v0.9.26: mouse rests every 10–24 keys while typing (got {v26MinE}–{v26MaxE}; was 2–6)");

        var v26Rng2 = new Random(262);
        int v26Long = 0, v26MinR = int.MaxValue, v26MaxR = int.MinValue;
        for (int i = 0; i < 20000; i++)
        {
            int r = RunEngine.TypingRestMs(v26Rng2);
            if (r > 300) v26Long++;
            v26MinR = Math.Min(v26MinR, r); v26MaxR = Math.Max(v26MaxR, r);
        }
        Assert(v26MinR >= 90 && v26MaxR <= 700,
            $"v0.9.26: rest length within 90–700 ms (got {v26MinR}–{v26MaxR}; v0.9.23 allowed up to 1200)");
        double v26LongRate = v26Long / 20000.0;
        Assert(v26LongRate > 0.05 && v26LongRate < 0.15,
            $"v0.9.26: ~10% of rests stretch to 350–700 ms (got {v26LongRate:P1})");
        Console.WriteLine();
        // ── Step 27: v0.9.27 — instant parallel audio (UI-dispatcher MediaPlayer) ────────────
        // User-recorded: playAudio inside a Parallel Group started 5–10 s late. Sequential steps
        // resume on the UI thread (pumped Dispatcher); parallel branches run on Task.Run pool
        // threads (no pump) where MediaPlayer Open/Play stalls and MediaEnded never fires.
        Console.WriteLine();
        Console.WriteLine("--- Step 27: v0.9.27 audio on dispatcher thread ---");

        static string V27ReadSrc(string rel)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 9 && dir is not null; i++, dir = dir.Parent)
            {
                foreach (var cand in new[] {
                    Path.Combine(dir.FullName, "ams-shell", "src", "Ams.UI", rel),
                    Path.Combine(dir.FullName, "src", "Ams.UI", rel) })
                    if (File.Exists(cand)) return File.ReadAllText(cand);
            }
            throw new FileNotFoundException("source not found in repo layout: " + rel);
        }

        var v27re = V27ReadSrc(Path.Combine("Services", "RunEngine.cs"));
        int v27Case = v27re.IndexOf("case \"playAudio\":", StringComparison.Ordinal);
        int v27Stop = v27re.IndexOf("public void StopAllAudio()", StringComparison.Ordinal);
        Assert(v27Case > 0 && v27Stop > v27Case, "v0.9.27: playAudio case and StopAllAudio located");
        string v27CaseBody = v27re.Substring(v27Case, v27Stop - v27Case);
        Assert(v27CaseBody.Contains("Dispatcher.Invoke"),
            "v0.9.27: playAudio creates/opens/plays on the UI dispatcher (parallel branches have no pump)");
        int v27Invoke = v27CaseBody.IndexOf("Dispatcher.Invoke", StringComparison.Ordinal);
        int v27Ended = v27CaseBody.IndexOf("PlaybackStopped", v27Invoke + 1);
        Assert(v27Invoke >= 0 && v27Ended > v27Invoke,
            "v0.9.27: PlaybackStopped attaches inside dispatcher (loop replay + self-close, v0.9.33 NAudio)");
        Assert(v27re.Substring(v27Stop, Math.Min(900, v27re.Length - v27Stop)).Contains("Current?.Dispatcher"),
            "v0.9.27: StopAllAudio marshals Stop/Close to the dispatcher thread (thread affinity)");
        Assert(v27re.Substring(v27Stop, Math.Min(900, v27re.Length - v27Stop)).Contains("HasShutdownStarted"),
            "v0.9.27: StopAllAudio guards dispatcher shutdown (no hang/throw at app exit)");
        Console.WriteLine();
        // ── Step 28: v0.9.28 — vision warm-up + first-round always-search + per-strip timeout ────────────
        // User-measured: with entireScreen scope the FIRST poll round took ~15 s (cold tier-0 JIT +
        // 4-strip integral builds), so playAudio fired ~15 s after the image was already visible.
        Console.WriteLine();
        Console.WriteLine("--- Step 28: v0.9.28 vision warmup + honest per-strip timeout ---");

        Assert(VisionService.Warmup(),
            "v0.9.28: warmup synthetic match finds the planted 36x36 needle (hot path primed)");

        var v28vs = V27ReadSrc(Path.Combine("Services", "VisionService.cs"));
        Assert(v28vs.Contains("public static bool Warmup()"),
            "v0.9.28: VisionService.Warmup exists");
        Assert(v28vs.Contains("polls > 0 && Rng.NextDouble() < 0.3"),
            "v0.9.28: stochastic 30% skip never applies to the first poll round");
        Assert(v28vs.Contains("var roundSw = Stopwatch.StartNew();"),
            "v0.9.28: every poll round is timed and slow rounds are logged (evidence)");

        var v28re = V27ReadSrc(Path.Combine("Services", "RunEngine.cs"));
        Assert(v28re.Contains("VisionService.Warmup();"),
            "v0.9.28: engine primes the vision hot path at run start");
        Console.WriteLine();
        // ── Step 29: v0.9.29 — loop dialog clarity + per-step label revival ────────────
        // Screenshot evidence: the For Loop dialog showed count+time fields in EVERY mode
        // (user: "contradiction?"), and the mode label stayed English — the dialog resolved
        // its step type from the CONCATENATED fields array, which never reference-matched
        // a definition, so every per-step Persian override (FaByStep) was dead since v0.9.24.
        Console.WriteLine();
        Console.WriteLine("--- Step 29: v0.9.29 loop field visibility + per-step label fix ---");

        Assert(!StepFieldVisibility.FieldVisible("time", null, "count")
               && !StepFieldVisibility.FieldVisible("infinite", null, "count"),
            "v0.9.29: count field hidden in time and infinite modes");
        Assert(StepFieldVisibility.FieldVisible("count", null, "count")
               && StepFieldVisibility.FieldVisible("time", null, "time"),
            "v0.9.29: the field of the ACTIVE loop law stays visible");
        Assert(StepFieldVisibility.FieldVisible("true", "false", null)
               && !StepFieldVisibility.FieldVisible("false", "false", null),
            "v0.9.29: v0.9.14 hide-when behavior unchanged");

        Assert(StepTextsFa.Has(null, "__name") && StepTextsFa.Has(null, "__delayMax"),
            "v0.9.29: shell fields (step name / delay / delay max) have Persian labels");
        Assert(StepTextsFa.Get("forLoop", "mode", "Loop mode").Contains("حالت"),
            "v0.9.29: forLoop mode label resolves to Persian via the per-step dict");

        var v29sd = V27ReadSrc(Path.Combine("Views", "StepDialog.xaml.cs"));
        Assert(v29sd.Contains("stepType ?? StepDefinitions.FindTypeByFields(fields)"),
            "v0.9.29: dialog receives the explicit step-type key (per-step overrides no longer dead)");
        Console.WriteLine();
        // ── Step 30: v0.9.30 — undo/redo snapshots + structural flat If/Else/EndIf ────────────
        // User reports: no undo/redo for steps, no Ctrl+X/C/V for the step clipboard, and the
        // flat "Else / End If" marker rows looked structural but were not (flat steps between
        // them ran unconditionally). All three fixed in v0.9.30.
        Console.WriteLine();
        Console.WriteLine("--- Step 30: v0.9.30 undo snapshots + flat If/Else ---");

        StepNode Mk30(string type, string key, object val) => new()
        {
            Type = type,
            Props = new Dictionary<string, object?> { { key, val } },
        };
        var v30tree = new List<StepNode>
        {
            new() { Type = "forLoop", Props = new Dictionary<string, object?> { { "mode", "time" }, { "timeValue", 10 } } },
            Mk30("comment", "text", "End If"),
        };
        v30tree[0].Children.Add(Mk30("keyDown", "key", "V"));
        var v30back = StepTreeSerializer.Restore(StepTreeSerializer.Snapshot(v30tree));
        Assert(v30back.Count == 2 && v30back[0].Type == "forLoop" && v30back[0].Children.Count == 1
               && PropEx.GetString(v30back[0].Props, "mode") == "time"
               && PropEx.GetInt(v30back[0].Props, "timeValue") == 10,
            "v0.9.30: tree snapshot round-trips structure and props");
        Assert(ReferenceEquals(v30back[0].Children[0].Parent, v30back[0]) && v30back[1].Parent is null,
            "v0.9.30: restore rewires the [JsonIgnore] Parent links");

        var v30sibs = new List<StepNode>
        {
            Mk30("findImage", "insertIfElse", true),          // 0
            Mk30("comment", "text", "Else"),                  // 1
            Mk30("keyUp", "key", "V"),                        // 2 — flat Else body
            Mk30("comment", "text", "End If"),                // 3
            Mk30("delay", "minMs", 100),                      // 4
        };
        Assert(RunEngine.LocateElseBlock(v30sibs, 0) == (1, 3),
            "v0.9.30: flat Else…End If block located around a real step body");
        Assert(RunEngine.LocateElseBlock(new List<StepNode> { Mk30("findImage", "insertIfElse", true), Mk30("keyUp", "key", "V") }, 0) == (-1, -1),
            "v0.9.30: no markers → no structural block");
        Assert(RunEngine.LocateElseBlock(new List<StepNode> { v30sibs[0], v30sibs[1], v30sibs[2] }, 0) == (-1, -1),
            "v0.9.30: Else without End If stays non-structural (safe legacy)");
        var v30farElse = new List<StepNode> { v30sibs[0], Mk30("keyUp", "key", "V"), v30sibs[1], v30sibs[3] };
        Assert(RunEngine.LocateElseBlock(v30farElse, 0) == (-1, -1),
            "v0.9.30: Else after a non-comment sibling is not the If's branch (v0.7.9 contract)");

        var v30vm = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        Assert(v30vm.Contains("private void Snapshot()") && v30vm.Contains("private void Undo()"),
            "v0.9.30: undo/redo snapshot infrastructure present in the view model");
        var v30xaml = V27ReadSrc("MainWindow.xaml");
        Assert(v30xaml.Contains("UndoCommand") && v30xaml.Contains("PasteCommand"),
            "v0.9.30: Ctrl+Z / Ctrl+V keybindings wired in the main window");
        Console.WriteLine();
        // ── Step 31: v0.9.31 — If/Else for sound detection ────────────
        // User request: "the If capability should exist for sound too — if the sensor hears
        // the target sound, do X, else …; the armed reaction should be the Else branch, not
        // buried in the detection options". waitForSound gains insertIfElse: children = Then
        // (heard), Else block = not heard; armed options hide while If/Else is on.
        Console.WriteLine();
        Console.WriteLine("--- Step 31: v0.9.31 sound If/Else ---");

        var v31ifElse = new StepNode { Type = "waitForSound", Props = new Dictionary<string, object?>
            { { "insertIfElse", true }, { "armed", true }, { "threshold", 90 }, { "timeoutMs", 20000 } } };
        Assert(StepDefinitions.GetCommands(v31ifElse)[0].StartsWith("WSND|"),
            "v0.9.31: If/Else mode sends plain WSND — the reaction lives in the branches, not the armed options");
        var v31armed = new StepNode { Type = "waitForSound", Props = new Dictionary<string, object?>
            { { "armed", true }, { "threshold", 90 } } };
        Assert(StepDefinitions.GetCommands(v31armed)[0].StartsWith("TRGSND|"),
            "v0.9.31: armed TRGSND preserved when If/Else is off (back-compat)");

        var v31re = V27ReadSrc(Path.Combine("Services", "RunEngine.cs"));
        Assert(v31re.Contains("bool heard = await RunWaitForSoundAsync"),
            "v0.9.31: waitForSound yields a heard/not-heard outcome for branching");
        var v31vm = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        Assert(v31vm.Contains("\"findImage\" or \"waitForSound\""),
            "v0.9.31: Else markers are offered for sound steps too");
        var v31sd = V27ReadSrc(Path.Combine("Models", "StepDefinitions.cs"));
        Assert(v31sd.Contains("Insert If-Else (children = Then") && v31sd.Contains("HideWhenKey: \"insertIfElse\""),
            "v0.9.31: sound step gains insertIfElse; armed options hide when it is on");
        Console.WriteLine();
        // ── Step 32: v0.9.32 — sound calibrate fallback (SCAL + WSND-probe) ────────────
        Console.WriteLine();
        Console.WriteLine("--- Step 32: v0.9.32 sound calibrate fallback ---");

        var v32vm = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        Assert(v32vm.Contains("SCAL|2000"),
            "v0.9.32: primary SCAL path kept");
        Assert(v32vm.Contains("calibrate probe: WSND"),
            "v0.9.32: WSND binary-search fallback exists");
        Assert(v32vm.Contains("calibrate (WSND fallback): silence floor"),
            "v0.9.32: fallback reports the measured floor and suggestion");
        var v32dlg = V27ReadSrc(Path.Combine("Views", "StepDialog.xaml.cs"));
        Assert(v32dlg.Contains("نمونه‌برداری از سنسور صدا"),
            "v0.9.32: measuring label no longer promises a 2-second window");
        var v32csp = V27ReadSrc("Ams.UI.csproj");
        Assert(v32csp.Contains("<Version>0.9.67</Version>"),
            "v0.9.32: assembly version bumped (the v0.9.31 build had shipped with 0.9.29)");
        Console.WriteLine();
        // ── Step 33: v0.9.33 — loop replay fix + marker guard for sound If/Else ────────────
        Console.WriteLine();
        Console.WriteLine("--- Step 33: v0.9.33 loop replay + marker guard fixes ---");

        var v33re = V27ReadSrc(Path.Combine("Services", "RunEngine.cs"));
        Assert(v33re.Contains("LoopableOutput"),
            "v0.9.33: playback tracked with a stop-flag wrapper");
        Assert(v33re.Contains("StopRequested"),
            "v0.9.33: loop replay never resurrects after StopAllAudio");
        Assert(v33re.Contains("using default"),
            "v0.9.33: out-of-range output device falls back to default with a log line");
        var v33vm = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        int v33or = 0, v33pos = 0;
        while ((v33pos = v33vm.IndexOf(" or \"waitForSound\"", v33pos, StringComparison.Ordinal)) >= 0) { v33or++; v33pos += 18; }
        Assert(v33or >= 3,
            "v0.9.33: structural-marker delete protection covers sound If/Else too (add/edit/delete guards)");
        var v33fa = V27ReadSrc(Path.Combine("Models", "StepTextsFa.cs"));
        Assert(v33fa.Contains("-۱=خودکار"),
            "v0.9.33: outputDevice label fixed (was mangled)");
        Console.WriteLine();
        // ── Step 34 (retro): v0.9.34 scope-container + accordion + indent checks ────────────
        Console.WriteLine();
        Console.WriteLine("--- Step 34: v0.9.34 retro scope-container + accordion + indent ---");

        var v34sd = V27ReadSrc(Path.Combine("Models", "StepDefinitions.cs"));
        int v34sc = 0, v34p = 0;
        while ((v34p = v34sd.IndexOf("IsContainer = true, IsScopeContainer = true", v34p, StringComparison.Ordinal)) >= 0) { v34sc++; v34p += 10; }
        Assert(v34sc >= 2,
            "v0.9.34: waitForSound is a scope container too (accordion/bracket parity with findImage)");
        var v34fs = V27ReadSrc(Path.Combine("Models", "FlatStepRow.cs"));
        Assert(v34fs.Contains("insertIfElse") && v34fs.Contains("Depth * 22"),
            "v0.9.34: toggle lives on the If row (not bare Else rows) + deeper nesting indent");
        var v34vm = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        Assert(v34vm.Contains("LocateElseMarkersForUi"),
            "v0.9.35+: UI marker ownership is separate from runtime LocateElseBlock");
        // v0.9.41 - was hard-coded to "v0.9.34" and started failing the moment the banner
        // moved on; the intent is "the banner names the current version", so check the shape.
        Assert(v34vm.Contains("Classroom Studio v0.9."),
            "v0.9.34+: the startup banner carries the current version");
        var v34csp = V27ReadSrc("Ams.UI.csproj");
        Assert(v34csp.Contains("<Version>0.9.67</Version>"),
            "v0.9.34: assembly version bumped (current build shipped as 0.9.35)");
        Console.WriteLine();
        // ── Step 35: v0.9.35 — Else/End If marker cluster fixes ──────────────────────────────
        Console.WriteLine();
        Console.WriteLine("--- Step 35: v0.9.35 Else/End If marker cluster ---");

        StepNode MkIf() => new StepNode { Type = "findImage", Props = new Dictionary<string, object?> { ["insertIfElse"] = true } };
        StepNode MkElse() => new StepNode { Type = "comment", Props = new Dictionary<string, object?> { ["text"] = "Else" } };
        StepNode MkEnd() => new StepNode { Type = "comment", Props = new Dictionary<string, object?> { ["text"] = "End If" } };
        StepNode MkFlat() => new StepNode { Type = "keystroke", Props = new Dictionary<string, object?>() };

        // 1) flat step BETWEEN the If and its markers → markers still count as existing (defect A)
        var s1 = new List<StepNode> { MkIf(), MkFlat(), MkElse(), MkEnd() };
        Assert(MainViewModel.HasElseMarkersForUi(s1, 0),
            "v0.9.35: flat step between If and markers — no duplicate pair on edit");

        // 2) only a LATER If's pair exists → not borrowed (defect B)
        var s2 = new List<StepNode> { MkIf(), new StepNode { Type = "comment", Props = new Dictionary<string, object?> { ["text"] = "Next" } }, MkIf(), MkElse(), MkEnd() };
        Assert(!MainViewModel.HasElseMarkersForUi(s2, 0) && MainViewModel.HasElseMarkersForUi(s2, 2),
            "v0.9.35: a later If's markers are never borrowed (missing pair gets created)");

        // 3) sanitizer keeps the first pair, removes duplicate pairs
        var s3 = new List<StepNode> { MkIf(), MkElse(), MkEnd(), MkElse(), MkEnd() };
        int removed = MainViewModel.SanitizeElseMarkers(s3, 0);
        Assert(removed == 1 && s3.Count(n => n.Type == "comment") == 2,
            "v0.9.35: orphan duplicate pair removed, first pair kept");

        // 4) two blocks in one list — the SECOND block's markers are protected too (defect C)
        var elseA = MkElse(); var endA = MkEnd(); var elseB = MkElse(); var endB = MkEnd();
        var s4 = new List<StepNode> { MkIf(), elseA, endA, MkIf(), elseB, endB };
        Assert(MainViewModel.IsStructuralMarkerIn(s4, elseB) && MainViewModel.IsStructuralMarkerIn(s4, endB),
            "v0.9.35: delete guard covers every block in the list, not just the first");

        // 5) orphan pair after the first block stays deletable (cleanup remains possible)
        var orphanElse = MkElse(); var orphanEnd = MkEnd();
        var s5 = new List<StepNode> { MkIf(), elseA, endA, orphanElse, orphanEnd };
        Assert(MainViewModel.IsStructuralMarkerIn(s5, elseA) && !MainViewModel.IsStructuralMarkerIn(s5, orphanElse),
            "v0.9.35: orphan duplicate markers are NOT protected (user can delete them)");
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("--- Step 36: v0.9.36 atomic overlay + review fixes ---");

        // 1-2) one UI locator owns both existence and delete protection, including flat legacy rows
        var v36if = MkIf(); var v36flat = MkFlat(); var v36else = MkElse(); var v36end = MkEnd();
        var v36siblings = new List<StepNode> { v36if, v36flat, v36else, v36end };
        var v36pair = MainViewModel.LocateElseMarkersForUi(v36siblings, 0);
        Assert(v36pair == (2, 3),
            "v0.9.36: UI locator returns the exact Else/End If pair across flat rows");
        Assert(MainViewModel.IsStructuralMarkerIn(v36siblings, v36else)
               && MainViewModel.IsStructuralMarkerIn(v36siblings, v36end),
            "v0.9.36: flat-layout structural markers remain protected");

        // 3-6) the visual is one ListBox-level Border, positioned from actual SummaryText geometry
        var v36row = V27ReadSrc(Path.Combine("Models", "FlatStepRow.cs"));
        Assert(!v36row.Contains("BandSeg") && !v36row.Contains("InSelectedScope"),
            "v0.9.36: no permanent/per-row vein state remains");
        var v36xaml = V27ReadSrc("MainWindow.xaml");
        Assert(v36xaml.Contains(@"x:Name=""ScopeVeinCanvas""")
               && v36xaml.Contains(@"x:Name=""ScopeVeinBracket"""),
            "v0.9.36: exactly one ListBox-level scope bracket exists");
        Assert(!v36xaml.Contains(@"ItemsSource=""{Binding Bands}""")
               && !v36xaml.Contains("RedLineMargin"),
            "v0.9.36: old always-on and fragmented row veins are removed");
        var v36code = V27ReadSrc("MainWindow.xaml.cs");
        Assert(v36code.Contains("SummaryText") && v36code.Contains("TranslatePoint")
               && v36code.Contains("UpdateScopeVeinOverlay"),
            "v0.9.36: bracket anchors to actual rendered text coordinates");

        // 7-9) review fixes: repaired files stay dirty; Import and bulk edits snapshot correctly
        Assert(v34vm.Contains("_dirty = sanitized > 0"),
            "v0.9.36: automatic Open cleanup cannot be silently lost");
        int v36import = v34vm.IndexOf("private async Task ImportAmk", StringComparison.Ordinal);
        int v36snap = v34vm.IndexOf("Snapshot();", v36import, StringComparison.Ordinal);
        int v36clear = v34vm.IndexOf("Steps.Clear();", v36import, StringComparison.Ordinal);
        Assert(v36import >= 0 && v36snap > v36import && v36snap < v36clear,
            "v0.9.36: Import snapshots the previous document before clearing it");
        Assert(v34vm.Contains("batch edit is one undoable mutation")
               && v34vm.Contains("bulk enable/disable must be undoable"),
            "v0.9.36: batch delay and bulk enable/disable mutations are undoable");

        // 10-11) conditional blocks are atomic for destructive clipboard operations; paste order is stable
        Assert(v34vm.Contains("ExpandConditionalSelection")
               && v34vm.Contains("ownedMarkers.Contains"),
            "v0.9.36: deleting/cutting an If head includes its owned Else/End If pair");
        Assert(v34vm.Contains("targetList.Insert(insertAt++, n)")
               && v34vm.Contains("an If/Else block is atomic"),
            "v0.9.36: paste keeps sibling order and unsafe partial block moves are blocked");

        // 12) version/banner
        Assert(v34csp.Contains("<Version>0.9.67</Version>")
               && v34vm.Contains("Classroom Studio v0.9.67"),
            "v0.9.36: version and banner match");
        Console.WriteLine();
        // ── Step 37: v0.9.37 — mandatory If/Else structure + white step rows ────────
        Console.WriteLine();
        Console.WriteLine("--- Step 37: v0.9.37 mandatory If/Else structure + white rows ---");
        StepNode H37If() => new StepNode { Type = "findImage", Props = new Dictionary<string, object?> { ["insertIfElse"] = true } };
        StepNode H37Else() => new StepNode { Type = "comment", Props = new Dictionary<string, object?> { ["text"] = "Else" } };
        StepNode H37End() => new StepNode { Type = "comment", Props = new Dictionary<string, object?> { ["text"] = "End If" } };
        StepNode H37Flat() => new StepNode { Type = "keystroke", Props = new Dictionary<string, object?>() };
        static string? H37Text(StepNode n) => n.Props.TryGetValue("text", out var v) ? v as string : null;

        // 1-2) a bare If is repaired with its OWN pair, and healing is idempotent
        var h37bare = new List<StepNode> { H37If() };
        int h37n = MainViewModel.HealMissingElseMarkers(h37bare);
        Assert(h37n == 2 && h37bare.Count == 3 && H37Text(h37bare[1]) == "Else" && H37Text(h37bare[2]) == "End If",
            "v0.9.37: an If with no markers gets Else + End If inserted directly after it");
        Assert(MainViewModel.HealMissingElseMarkers(h37bare) == 0,
            "v0.9.37: healing an already-complete document is a no-op (idempotent)");

        // 3) a complete block is left untouched
        var h37ok = new List<StepNode> { H37If(), H37Else(), H37End() };
        Assert(MainViewModel.HealMissingElseMarkers(h37ok) == 0 && h37ok.Count == 3,
            "v0.9.37: a paired If/Else block is never touched");

        // 4-5) the daroon1.amsj shape: a NESTED If had consumed the only markers
        var h37dar = new List<StepNode> { H37If(), H37If(), H37Else(), H37End() };
        int h37darN = MainViewModel.HealMissingElseMarkers(h37dar);
        Assert(h37darN == 2 && h37dar.Count == 6
               && H37Text(h37dar[1]) == "Else" && H37Text(h37dar[2]) == "End If",
            "v0.9.37: the outer If gets its own pair when a nested If owned the markers (daroon1.amsj defect)");
        Assert(MainViewModel.HasElseMarkersForUi(h37dar, 0) && MainViewModel.HasElseMarkersForUi(h37dar, 3),
            "v0.9.37: after healing, BOTH the outer and the nested If are paired");

        // 6) recursion into container children, with a flat row before the If
        var h37loop = new StepNode { Type = "forLoop", Props = new Dictionary<string, object?>() };
        h37loop.Children.Add(H37Flat());
        h37loop.Children.Add(H37If());
        var h37root = new List<StepNode> { h37loop };
        Assert(MainViewModel.HealMissingElseMarkers(h37root) == 2 && h37loop.Children.Count == 4
               && H37Text(h37loop.Children[2]) == "Else" && H37Text(h37loop.Children[3]) == "End If",
            "v0.9.37: unpaired Ifs nested inside a container are healed too");

        // 7-11) source wiring: open/import heal, white rows, bracket anchor, version+banner
        var v37vm = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v37mw = V27ReadSrc("MainWindow.xaml.cs");
        var v37csp = V27ReadSrc("Ams.UI.csproj");
        Assert(v37vm.Contains("int healed = HealAllElseMarkers();")
               && v37vm.Contains("_dirty = sanitized > 0 || healed > 0 || loopHealed > 0 || lifted > 0;"),   // v0.9.46 — loop-marker repair keeps the file dirty too
            "v0.9.37: File→Open heals structure and keeps the repaired document dirty");
        Assert(v37vm.Contains("HealAllElseMarkers();   // v0.9.37"),
            "v0.9.37: .amk import also lands with complete If/Else structure");
        Assert(v37vm.Contains("TintFor(List<StepNode> scopes) => Transparent_;")
               && !v37vm.Contains("LoopTints") && !v37vm.Contains("PackageTints"),
            "v0.9.37: pastel scope tints removed — rows keep the white list background");
        Assert(v37mw.Contains("double left = double.NaN;") && v37mw.Contains("candidate < left"),
            "v0.9.37: the scope bracket anchors at the leftmost row, so it spans the Else row");
        Assert(v37csp.Contains("<Version>0.9.67</Version>")
               && v37vm.Contains("Classroom Studio v0.9.67"),
            "v0.9.37: version and banner match");
        // ── Step 38: v0.9.38 — accordion toggle beside the text + Else bend on the bracket ──
        Console.WriteLine();
        Console.WriteLine("--- Step 38: v0.9.38 toggle beside the text + Else bend ---");
        var v38xaml = V27ReadSrc("MainWindow.xaml");
        var v38mw = V27ReadSrc("MainWindow.xaml.cs");
        var v38vm = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v38csp = V27ReadSrc("Ams.UI.csproj");

        // 1-4) the toggle left the gutter and now lives in the description cell
        Assert(v38xaml.Contains(@"x:Name=""RowTextAnchor"""),
            "v0.9.38: the description cell is wrapped in the named RowTextAnchor grid");
        int v38anchor = v38xaml.IndexOf(@"x:Name=""RowTextAnchor""", StringComparison.Ordinal);
        int v38toggle = v38xaml.IndexOf("ScopeToggle_PreviewMouseLeftButtonDown", StringComparison.Ordinal);
        Assert(v38anchor > 0 && v38toggle > v38anchor,
            "v0.9.38: the accordion toggle is rendered inside the text cell, not in the left gutter");
        Assert(v38xaml.Contains(@"x:Name=""SummaryText"" Grid.Column=""1"""),
            "v0.9.38: the description text sits right of the toggle inside the same cell");
        Assert(v38xaml.Contains(@"<ColumnDefinition Width=""16"" />"),
            "v0.9.38: the old 28px toggle gutter shrank to a 16px spacer");

        // 5-7) the Else bend
        Assert(v38xaml.Contains(@"x:Name=""ScopeVeinElseTick"""),
            "v0.9.38: the overlay canvas owns a dedicated Else bend element");
        Assert(v38mw.Contains("ScopeVeinElseTick.Visibility = Visibility.Visible;")
               && v38mw.Contains("ScopeVeinElseTick.Visibility = Visibility.Collapsed;"),
            "v0.9.38: the Else bend is shown for the selected block and reset on every update");
        // v0.9.41 - v0.9.40 moved the overlay from SelectedNode to ScopeVeinNode; the Else bend
        // still has to be requested, just for the vein node.
        Assert(v38mw.Contains("TryGetScopeElseRow(vm.ScopeVeinNode, out int elseRow)"),
            "v0.9.38: the overlay asks the view model for the Else row of the vein scope");

        // 8-9) the view model publishes the Else row of every scope head
        Assert(v38vm.Contains("public bool TryGetScopeElseRow(StepNode node, out int elseRow)")
               && v38vm.Contains("_scopeElseRows.Clear();"),
            "v0.9.38: scope Else rows are published and rebuilt with the flat list");
        Assert(v38vm.Contains("_scopeElseRows[n] = FlatSteps.Count - 1;"),
            "v0.9.38: the Else marker row index is captured while the scope markers are consumed");

        // 10) the vein anchors on the whole text cell, so it never overlaps the toggle
        Assert(v38mw.Contains(@"FindNamedDescendant<FrameworkElement>(row, ""RowTextAnchor"")"),
            "v0.9.38: the bracket anchors on the text cell (toggle included), left of the toggle");

        // 11) version/banner
        Assert(v38csp.Contains("<Version>0.9.67</Version>")
               && v38vm.Contains("Classroom Studio v0.9.67"),
            "v0.9.38: version and banner match");
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine();
        // ── Step 39: v0.9.39 — light sensor action (BH1750) + per-system Pico firmware export ──
        Console.WriteLine();
        Console.WriteLine("--- Step 39: v0.9.39 light sensor action + Pico export ---");
        var v39sd  = V27ReadSrc(Path.Combine("Models", "StepDefinitions.cs"));
        var v39re  = V27ReadSrc(Path.Combine("Services", "RunEngine.cs"));
        var v39sg  = V27ReadSrc(Path.Combine("Services", "ScriptGenerator.cs"));
        var v39pf  = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
        var v39vm  = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v39fa  = V27ReadSrc(Path.Combine("Models", "StepTextsFa.cs"));
        var v39row = V27ReadSrc(Path.Combine("Models", "FlatStepRow.cs"));
        var v39x   = V27ReadSrc("MainWindow.xaml");
        var v39csp = V27ReadSrc("Ams.UI.csproj");

        // 1-3) the new step type exists, is a scope container and speaks the new board commands
        Assert(v39sd.Contains("[\"waitForLight\"] = new StepDefinition")
               && v39sd.Contains("IsContainer = true, IsScopeContainer = true"),
            "v0.9.39: waitForLight is a scope container like waitForSound");
        Assert(v39sd.Contains("\"luxCenter\"") && v39sd.Contains("\"luxTolerance\"")
               && v39sd.Contains("\"stableSec\"") && v39sd.Contains("\"sampleMode\""),
            "v0.9.39: the light step exposes centre/tolerance/stabilization/mode fields");
        Assert(v39sd.Contains("WLUX|") && v39sd.Contains("TRGLUX|"),
            "v0.9.39: the light step emits WLUX (host waits) and TRGLUX (armed board keypress)");

        // 4-5) runner: same If/Else machinery as sound
        Assert(v39re.Contains("case \"waitForLight\":")
               && v39re.Contains("private async Task<bool> RunWaitForLightAsync("),
            "v0.9.39: the runner handles waitForLight through its own await helper");
        int v39case = v39re.IndexOf("case \"waitForLight\":", StringComparison.Ordinal);
        int v39else = v39re.IndexOf("LocateElseBlock(list, si)", v39case, StringComparison.Ordinal);
        Assert(v39case > 0 && v39else > v39case,
            "v0.9.39: the light step branches into the Else block when the range never stabilizes");

        // 6-7) script generator + flat row/scope plumbing
        Assert(v39sg.Contains("case \"waitForLight\":") && v39sg.Contains("lightTimeoutSec"),
            "v0.9.39: generated scripts use a window-proportional timeout for the light step");
        Assert(v39row.Contains("or \"waitForLight\"") && v39vm.Contains("or \"waitForLight\""),
            "v0.9.39: the light step joins the findImage/waitForSound If-Else row plumbing");

        // 8-9) calibrate button (LCAL) wired exactly like the sound calibration
        Assert(v39vm.Contains("private async Task<int?> CalibrateLightRange()")
               && v39vm.Contains("LCAL|3000"),
            "v0.9.39: Calibrate samples the BH1750 with LCAL and returns the range centre");
        Assert(v39vm.Contains("calibrate = CalibrateLightRange; calibrateKey = \"luxCenter\""),
            "v0.9.39: the step dialog shows the Calibrate button for waitForLight");

        // 10-12) per-system Pico export
        Assert(v39vm.Contains("private void ExportPicoFirmware()")
               && v39x.Contains("{Binding ExportPicoFirmwareCommand}"),
            "v0.9.39: File menu exports the Pico firmware for this system");
        Assert(v39pf.Contains("public static IReadOnlyList<string> Export(")
               && v39pf.Contains("pico-calibration.json") && v39pf.Contains("README-FLASH.md")
               && v39pf.Contains("boot.py"),
            "v0.9.39: the exporter writes code.py, boot.py, the calibration file and the flashing guide");
        Assert(v39pf.Contains("board.GP21, board.GP20") && v39pf.Contains("ADDR = 0x23")
               && v39pf.Contains("raw / 1.2") && v39pf.Contains("from adafruit_hid.keyboard import Keyboard"),
            "v0.9.39: the firmware matches the pin dictionary (SDA=GP20/SCL=GP21, 0x23, lux=raw/1.2) and presses keys over HID");

        // 13-14) UI entry points and Persian labels
        Assert(v39x.Contains("CommandParameter=\"waitForLight\"")
               && v39x.Contains("Text=\"Light\""),
            "v0.9.39: Insert menu, context menu and the rail all offer the light step");
        Assert(v39fa.Contains("[\"luxCenter\"]") && v39fa.Contains("[\"stableSec\"]")
               && v39fa.Contains("[\"waitForLight:key\"]"),
            "v0.9.39: the light fields have Persian labels in the step dialog");

        // 15) version/banner
        Assert(v39csp.Contains("<Version>0.9.67</Version>")
               && v39vm.Contains("Classroom Studio v0.9.67"),
            "v0.9.39: version and banner match");

        // ── Step 40: v0.9.40 — the accordion toggle reveals the scope vein ──
        Console.WriteLine();
        Console.WriteLine("--- Step 40: v0.9.40 accordion toggle reveals the scope vein ---");
        var v40vm  = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v40cs  = V27ReadSrc("MainWindow.xaml.cs");
        var v40csp = V27ReadSrc("Ams.UI.csproj");

        // 1-4) one vein source of truth that the toggle can drive
        Assert(v40vm.Contains("public StepNode? ScopeVeinNode"),
            "v0.9.40: the view model exposes ScopeVeinNode as the single vein source of truth");
        Assert(v40vm.Contains("_veinFocusNode ?? SelectedNode"),
            "v0.9.40: ScopeVeinNode prefers the toggled block and falls back to the selection");
        Assert(v40vm.Contains("public void FocusScopeVein(StepNode? node)"),
            "v0.9.40: FocusScopeVein sets the vein target without touching the selection");
        Assert(v40vm.Contains("partial void OnSelectedNodeChanged(StepNode? value)"),
            "v0.9.40: moving the selection clears the toggle-driven vein focus");

        // 5-6) both toggle paths (view model and the click handler) focus the vein
        Assert(v40vm.Contains("public void ToggleCollapse(StepNode n)")
               && v40vm.Contains("FocusScopeVein(n);"),
            "v0.9.40: ToggleCollapse focuses the vein on the toggled block (expand and collapse)");
        Assert(v40cs.Contains("ScopeToggle_PreviewMouseLeftButtonDown")
               && v40cs.Contains("vm.FocusScopeVein(row.Node);"),
            "v0.9.40: the accordion toggle click reveals the vein and redraws the overlay");

        // 7-9) the overlay reads the new node and no selection-only path is left
        Assert(v40cs.Contains("vm.ScopeVeinNode is null")
               && v40cs.Contains("vm.TryGetScopeRange(vm.ScopeVeinNode"),
            "v0.9.40: the overlay resolves its row range from ScopeVeinNode");
        Assert(!v40cs.Contains("vm.TryGetScopeRange(vm.SelectedNode")
               && !v40cs.Contains("vm.TryGetScopeElseRow(vm.SelectedNode"),
            "v0.9.40: no selection-only vein path is left in the overlay");
        Assert(v40cs.Contains("vm.TryGetScopeElseRow(vm.ScopeVeinNode"),
            "v0.9.40: the v0.9.38 Else bend follows the same vein node");

        // 10) earlier behaviour preserved
        Assert(v40cs.Contains("ScopeVeinElseTick.Visibility = Visibility.Visible;")
               && v40vm.Contains("waitForLight"),
            "v0.9.40: the v0.9.38 Else bend and the v0.9.39 light step are preserved");

        // 11) version/banner
        Assert(v40csp.Contains("<Version>0.9.67</Version>")
               && v40vm.Contains("Classroom Studio v0.9.67"),
            "v0.9.40: version and banner match");

        // ── Step 41: v0.9.41 — spelling, marker veins, rail submenus, restart removal ──
        Console.WriteLine();
        Console.WriteLine("--- Step 41: v0.9.41 spelling, marker veins, rail submenus, restart removal ---");
        var v41vm   = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v41cs   = V27ReadSrc("MainWindow.xaml.cs");
        var v41xaml = V27ReadSrc("MainWindow.xaml");
        var v41sd   = V27ReadSrc(Path.Combine("Models", "StepDefinitions.cs"));
        var v41fa   = V27ReadSrc(Path.Combine("Models", "StepTextsFa.cs"));
        var v41fsr  = V27ReadSrc(Path.Combine("Models", "FlatStepRow.cs"));
        var v41doc  = V27ReadSrc(Path.Combine("Services", "DocumentService.cs"));
        var v41hm   = V27ReadSrc(Path.Combine("Services", "HumanMouse.cs"));
        var v41ren  = V27ReadSrc(Path.Combine("Services", "RunEngine.cs"));
        var v41csp  = V27ReadSrc("Ams.UI.csproj");

        // 1-4) spelling: no U+FFFD anywhere and the hold range uses a real en dash
        Assert(!v41sd.Contains('\uFFFD') && !v41vm.Contains('\uFFFD') && !v41xaml.Contains('\uFFFD'),
            "v0.9.41: no replacement characters are left in the step/view sources");
        Assert(!v41hm.Contains('\uFFFD') && !v41ren.Contains('\uFFFD'),
            "v0.9.41: the garbled comments in HumanMouse/RunEngine are repaired");
        Assert(v41sd.Contains("hold {hmin}\u2013{hmax}ms"),
            "v0.9.41: the press-hold summary renders as a clean hold 88-180ms range");
        Assert(!v41xaml.Contains("\u0627\u0646\u062a\u0637\u0627\u0631"),
            "v0.9.41: the misspelled Persian light tooltip is corrected");

        // 5-6) the step-qualified light labels finally resolve (they were in the shared table)
        Assert(StepTextsFa.Has("waitForLight", "key") && StepTextsFa.Has("waitForLight", "holdMin")
               && StepTextsFa.Has("waitForLight", "holdMax"),
            "v0.9.41: the armed light fields resolve their Persian labels");
        Assert(v41fa.IndexOf("[\"waitForLight:holdMin\"]", StringComparison.Ordinal)
               > v41fa.IndexOf("FaByStep", StringComparison.Ordinal),
            "v0.9.41: the qualified light keys live in FaByStep, not the shared table");

        // 7-8) the rule: a toggle means children, and children mean a vein
        Assert(v41fsr.Contains("public bool ShowToggle"),
            "v0.9.41: a row still derives its toggle from having children");
        Assert(v41vm.Contains("_scopeRanges[m] = (markerStart, FlatSteps.Count - 1);"),
            "v0.9.41: an Else marker row that owns sub-steps registers its own vein range");

        // 9-10) the random-restart-during-play feature is gone for good
        Assert(!v41xaml.Contains("RandomReboot") && !v41vm.Contains("RandomReboot")
               && !v41doc.Contains("RandomReboot"),
            "v0.9.41: the random restart option is gone from UI, view model and settings");
        Assert(!v41vm.Contains("rebootTask") && !v41vm.Contains("/r /t 0"),
            "v0.9.41: no reboot scheduling is left in the run loop");

        // 11-14) the vertical rail mirrors the Insert tab, grouped into per-section submenus
        string[] v41types = { "mouseClick", "mouseMove", "mouseScroll", "randomMousePosition", "keystroke", "typeText", "keyDown", "keyUp", "delay", "forLoop", "randomPackage", "parallelGroup", "findImage", "splashListener", "waitForLight", "openFile", "buzzer", "runExe", "playScript", "label", "gotoLabel", "comment", "rawCommand" };
        int v41rs = v41xaml.IndexOf("<!-- Icon rail", StringComparison.Ordinal);
        int v41rj = v41xaml.IndexOf("<!-- Steps column", StringComparison.Ordinal);
        Assert(v41rs > 0 && v41rj > v41rs,
            "v0.9.41: the icon rail block is still identifiable");
        string v41rail = v41xaml.Substring(v41rs, v41rj - v41rs);
        int v41miss = 0;
        foreach (var t in v41types)
            if (!v41rail.Contains("CommandParameter=\"" + t + "\"")) v41miss++;
        Assert(v41miss == 0,
            $"v0.9.41: every Insert entry is reachable from the rail ({v41types.Length} types, {v41miss} missing)");
        int v41menus = 0, v41p = 0;
        while ((v41p = v41rail.IndexOf("<Button.ContextMenu>", v41p, StringComparison.Ordinal)) >= 0)
        { v41menus++; v41p += 20; }
        Assert(v41menus == 6,
            $"v0.9.41: every multi-entry rail section owns its own submenu ({v41menus} submenus)");
        Assert(v41cs.Contains("private void RailSubmenu_PreviewMouseLeftButtonDown")
               && v41cs.Contains("menu.IsOpen = true;")
               && v41rail.Contains("RailSubmenu_PreviewMouseLeftButtonDown"),
            "v0.9.41: clicking a rail caret opens that section's submenu");

        // 15) version/banner
        Assert(v41csp.Contains("<Version>0.9.67</Version>") && v41vm.Contains("Classroom Studio v0.9.67"),
            "v0.9.41: version and banner match");

        // ── Step 42: v0.9.42 — a step without a condition adopts nothing ──
        Console.WriteLine();
        Console.WriteLine("--- Step 42: v0.9.42 conditionless steps adopt nothing and are named as a plain search ---");
        var v42vm  = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v42doc = V27ReadSrc(Path.Combine("Services", "DocumentService.cs"));
        var v42csp = V27ReadSrc("Ams.UI.csproj");

        StepNode Mk42(string type, bool cond) => new StepNode { Type = type, Props = new Dictionary<string, object?> { ["insertIfElse"] = cond } };
        StepNode Plain42(string type) => new StepNode { Type = type, Props = new Dictionary<string, object?>() };

        // 1-5) containership is a property of the NODE, not of the step type
        Assert(!StepDefinitions.AcceptsChildren(Mk42("findImage", false)),
            "v0.9.42: a Find Image without a condition accepts no children");
        Assert(StepDefinitions.AcceptsChildren(Mk42("findImage", true)),
            "v0.9.42: a Find Image with Insert If-Else is still the If head");
        Assert(!StepDefinitions.AcceptsChildren(Mk42("waitForLight", false))
               && StepDefinitions.AcceptsChildren(Mk42("waitForLight", true)),
            "v0.9.42: Wait For Light follows the same rule (same user-reported bug)");
        Assert(!StepDefinitions.AcceptsChildren(Mk42("waitForSound", false)),
            "v0.9.42: Wait For Sound without a condition accepts no children");
        Assert(StepDefinitions.AcceptsChildren(Plain42("forLoop"))
               && StepDefinitions.AcceptsChildren(Plain42("randomPackage"))
               && StepDefinitions.AcceptsChildren(Plain42("parallelGroup")),
            "v0.9.42: unconditional containers (loop / package / parallel) are unchanged");

        // 6-7) naming: the original program calls it a picture search when there is no condition
        Assert(StepDefinitions.Summarize(Mk42("findImage", false)).StartsWith("Search for image", StringComparison.Ordinal),
            "v0.9.42: conditionless Find Image is named Search for image (got: " + StepDefinitions.Summarize(Mk42("findImage", false)) + ")");
        Assert(StepDefinitions.Summarize(Mk42("findImage", true)).StartsWith("If image found", StringComparison.Ordinal),
            "v0.9.42: with a condition it reads as the If head (got: " + StepDefinitions.Summarize(Mk42("findImage", true)) + ")");

        // 8-9) migration: orphan children are lifted out as the next siblings, order preserved
        var v42head = Mk42("findImage", false);
        foreach (var k42 in new[] { "A", "B", "C" })
            v42head.Children.Add(new StepNode { Type = "keystroke", Name = k42, Parent = v42head });
        var v42list = new List<StepNode> { Plain42("delay"), v42head, Plain42("delay") };
        int v42lifted = DocumentService.LiftOrphanChildren(v42list);
        Assert(v42lifted == 3 && v42head.Children.Count == 0 && v42list.Count == 6,
            $"v0.9.42: the three steps under a conditionless Find Image are released ({v42lifted} lifted, {v42list.Count} rows)");
        Assert(v42list[2].Name == "A" && v42list[3].Name == "B" && v42list[4].Name == "C"
               && v42list[2].Parent is null,
            "v0.9.42: they land right after the step, in the original order, at its own level");

        // 10-11) real branches are never touched
        var v42if = Mk42("findImage", true);
        v42if.Children.Add(new StepNode { Type = "keystroke", Name = "then", Parent = v42if });
        var v42else = new StepNode { Type = "comment", Props = new Dictionary<string, object?> { ["text"] = "Else" } };
        v42else.Children.Add(new StepNode { Type = "keystroke", Name = "else", Parent = v42else });
        var v42keep = new List<StepNode> { v42if, v42else };
        Assert(DocumentService.LiftOrphanChildren(v42keep) == 0 && v42keep.Count == 2
               && v42if.Children.Count == 1 && v42else.Children.Count == 1,
            "v0.9.42: a real If head and its Else marker keep their branches");
        var v42light = Mk42("waitForLight", false);
        v42light.Children.Add(new StepNode { Type = "keystroke", Name = "L", Parent = v42light });
        var v42ll = new List<StepNode> { v42light };
        Assert(DocumentService.LiftOrphanChildren(v42ll) == 1 && v42ll.Count == 2 && v42ll[1].Name == "L",
            "v0.9.42: the light step releases its nested rows too");

        // 12-14) the three insert paths are node-aware and the migration is wired in
        Assert(v42vm.Contains("StepDefinitions.AcceptsChildren(selected)")
               && v42vm.Contains("StepDefinitions.AcceptsChildren(target)")
               && v42vm.Contains("StepDefinitions.AcceptsChildren(sel)"),
            "v0.9.42: paste, drag-into and Insert all ask the node, not the type");
        Assert(!v42vm.Contains("StepDefinitions.IsContainer(selected.Type)")
               && !v42vm.Contains("StepDefinitions.Get(sel.Type).IsContainer")
               && !v42vm.Contains("StepDefinitions.Get(target.Type).IsContainer"),
            "v0.9.42: no type-only container check is left on an insert path");
        Assert(v42doc.Contains("public static int LiftOrphanChildren")
               && v42vm.Contains("DocumentService.LiftOrphanChildren(Steps)"),
            "v0.9.42: opening, importing and editing run the release migration");

        // 15) version/banner
        Assert(v42csp.Contains("<Version>0.9.67</Version>") && v42vm.Contains("Classroom Studio v0.9.67"),
            "v0.9.42: version and banner match");

        // ── Step 43: v0.9.43 — seven user-reported UI items ─────────────────────
        Console.WriteLine();
        Console.WriteLine("--- Step 43: v0.9.43 hover rail menus · clearable log · visible Undo/Redo · paired hotkeys · manual port · audio device dropdown ---");
        var v43mw   = V27ReadSrc("MainWindow.xaml");
        var v43mwc  = V27ReadSrc("MainWindow.xaml.cs");
        var v43vm   = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v43doc  = V27ReadSrc(Path.Combine("Services", "DocumentService.cs"));
        var v43hk   = V27ReadSrc(Path.Combine("Services", "GlobalHotkeyService.cs"));
        var v43pbb  = V27ReadSrc(Path.Combine("Services", "PythonBoardBridge.cs"));
        var v43opt  = V27ReadSrc(Path.Combine("Views", "OptionsDialog.xaml"));
        var v43dlg  = V27ReadSrc(Path.Combine("Views", "StepDialog.xaml.cs"));
        var v43def  = V27ReadSrc(Path.Combine("Models", "StepDefinitions.cs"));
        var v43fa   = V27ReadSrc(Path.Combine("Models", "StepTextsFa.cs"));
        var v43br   = V27ReadSrc(Path.Combine("..", "..", "bridge", "bridge.py"));
        var v43csp  = V27ReadSrc("Ams.UI.csproj");

        static HashSet<string> V43Params(string region)
            => System.Text.RegularExpressions.Regex.Matches(region, "CommandParameter=\"([a-zA-Z]+)\"")
                 .Select(m => m.Groups[1].Value).ToHashSet();
        static string V43Between(string s, string a, string b)
        {
            int i = s.IndexOf(a, StringComparison.Ordinal);
            int j = i < 0 ? -1 : s.IndexOf(b, i + a.Length, StringComparison.Ordinal);
            return i < 0 || j < 0 ? "" : s[i..j];
        }
        var v43insert = V43Params(V43Between(v43mw, "Header=\"_Insert\"", "Header=\"_Tools\""));
        var v43rail   = V43Params(V43Between(v43mw, "<!-- Icon rail", "<!-- Steps column"));
        var v43ctx    = V43Params(V43Between(v43mw, "Header=\"Add Action\"", "InputGestureText=\"Ctrl+X\""));

        Assert(v43insert.Count == 25,
            $"the Insert menu lists all 25 step types, including Launch DC Recovery (got {v43insert.Count})");
        Assert(v43insert.SetEquals(v43rail),
            "v0.9.43: the vertical rail covers EVERY Insert-tab item (missing: " + string.Join(",", v43insert.Except(v43rail)) + ")");
        Assert(v43insert.SetEquals(v43ctx),
            "v0.9.43: the right-click Add Action menu covers every Insert item too (parallelGroup / playAudio / runExe were missing)");
        Assert(v43mw.Contains("<EventSetter Event=\"MouseEnter\" Handler=\"RailButton_MouseEnter\" />")
               && v43mwc.Contains("private void RailButton_MouseEnter(")
               && v43mwc.Contains("Keyboard.FocusedElement"),
            "v0.9.43: rail submenus open on hover, guarded against stealing typing focus");
        Assert(v43mw.Contains("Command=\"{Binding ClearLogCommand}\"")
               && v43vm.Contains("private void ClearLog()")
               && v43vm.Contains("LogLines.Clear();"),
            "v0.9.43: the serial log has a clear button wired to LogLines.Clear()");
        Assert(v43vm.Contains("UndoCap = 100")
               && v43mw.Contains("Header=\"_Undo\" InputGestureText=\"Ctrl+Z\" Command=\"{Binding UndoCommand}\"")
               && v43mw.Contains("Header=\"_Redo\" InputGestureText=\"Ctrl+Y\" Command=\"{Binding RedoCommand}\""),
            "v0.9.43: Undo/Redo are visible in the Edit menu (100 levels — more than the requested 10)");
        Assert(v43hk.Contains("\"runstop\"") && v43hk.Contains("\"pauseresume\"")
               && !v43hk.Contains("HKID_STOP")
               && v43vm.Contains("private void RunOrStop()") && v43vm.Contains("private void PauseOrResume()"),
            "v0.9.43: hotkeys are paired — one key toggles run/stop, another pause/resume");
        Assert(v43doc.Contains("public string RunStopHotkey") && v43doc.Contains("public string PauseResumeHotkey")
               && v43doc.Contains("s.RunStopHotkey = s.RunHotkey;") && v43doc.Contains("s.PauseResumeHotkey = s.PauseHotkey;")
               && v43opt.Contains("Tag=\"runstopHk\"") && v43opt.Contains("Tag=\"pauseresumeHk\"")
               && !v43opt.Contains("Tag=\"stopHk\"") && !v43opt.Contains("Tag=\"resumeHk\""),
            "v0.9.43: settings migrate the old 4 hotkeys into the 2 paired ones; the Options dialog shows 2 rows");
        Assert(v43br.Contains("op == \"list_ports\"")
               && v43pbb.Contains("ListPortsAsync") && v43pbb.Contains("EnsureProcess()")
               && v43mw.Contains("x:Name=\"PortPicker\"")
               && v43vm.Contains("RefreshPorts") && v43vm.Contains("EffectivePort()"),
            "v0.9.43: manual board connect — the status-bar port picker is fed by bridge list_ports");
        Assert(MainViewModel.PortDeviceFromText("COM5 — USB Serial Device") == "COM5"
               && MainViewModel.PortDeviceFromText("  COM3  ") == "COM3"
               && MainViewModel.PortDeviceFromText("") == "AUTO"
               && MainViewModel.PortDeviceFromText(null) == "AUTO",
            "v0.9.43: a picked port label parses to the bare device; empty means AUTO");
        Assert(v43def.Contains("FieldKind { Text, Multiline, Int, Combo, EditableCombo, Check, AudioDevice, Float }")
               && v43def.Contains("new(\"outputDevice\", \"Output device — pick from the list (-1=auto)\", FieldKind.AudioDevice, \"-1\")")
               && v43dlg.Contains("MakeAudioDeviceCombo") && v43dlg.Contains("FieldKind.AudioDevice =>")
               && v43fa.Contains("-۱=خودکار"),
            "v0.9.43: the audio output device is a dropdown of real NAudio devices, not a bare number");
        Assert(v43csp.Contains("<Version>0.9.67</Version>") && v43vm.Contains("Classroom Studio v0.9.67"),
            "v0.9.43: version and banner match");

        // ── Step 44: v0.9.44 — rail-leave close · parallel-group vein · Pico play options ·
        //     per-board presence LEDs · keyboard board choice ──
        Console.WriteLine();
        Console.WriteLine("--- Step 44: v0.9.44 rail-leave close · parallel vein · pico play options · board LEDs · keyboard board ---");
        var v44mw   = V27ReadSrc("MainWindow.xaml");
        var v44mwc  = V27ReadSrc("MainWindow.xaml.cs");
        var v44vm   = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v44doc  = V27ReadSrc(Path.Combine("Services", "DocumentService.cs"));
        var v44exp  = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
        var v44opt  = V27ReadSrc(Path.Combine("Views", "OptionsDialog.xaml"));
        var v44optc = V27ReadSrc(Path.Combine("Views", "OptionsDialog.xaml.cs"));
        var v44csp  = V27ReadSrc("Ams.UI.csproj");

        // 1-2) leaving the rail closes the hover-opened submenu (both open paths tracked)
        Assert(v44mw.Contains("MouseLeave=\"RailBorder_MouseLeave\"")
               && v44mwc.Contains("DispatcherTimer _railDwellTimer")
               && v44mwc.Contains("DispatcherTimer _railWatchTimer")
               && v44mwc.Contains("IsCursorInside(host) || IsCursorInside(menu)"),
            "v0.9.44+: leaving both rail host and popup closes the submenu through one watcher");
        Assert(v44mwc.Contains("OpenRailMenu(host)") && v44mwc.Contains("CloseRailMenu()")
               && !v44mwc.Contains("private async void RailBorder_MouseLeave"),
            "v0.9.44+: both open paths share one non-async state machine (no popup capture loop)");

        // 3-5) a Parallel Group with children registers a scope range → the red vein shows
        Assert(v44vm.Contains("opensParallel = n.Type == \"parallelGroup\"")
               && v44vm.Contains("opensLoop || opensIf || opensPackage || opensParallel")
               && !v44vm.Contains("container.Type == \"parallelGroup\") return false;"),
            "v0.9.44→48: Renumber registers a scope range for Parallel Group; the no-closing-marker rule is retracted — it now closes with # Next like every child-capable block");
        Assert(StepDefinitions.IsScopeContainerNode(new StepNode { Type = "parallelGroup" }),
            "v0.9.44: a parallel group is a scope container node");
        var v44pg = new StepNode { Type = "parallelGroup" };
        v44pg.Children.Add(new StepNode { Type = "keystroke", Name = "k", Parent = v44pg });
        Assert(StepDefinitions.AcceptsChildren(v44pg),
            "v0.9.44: a parallel group still accepts children (unchanged)");

        // 6-8) the Pico export bakes the Play Options into code.py
        var v44timed = PicoFirmwareExporter.BuildCodePy(new List<PicoFirmwareExporter.LightState>(), "TESTMACHINE", "timed", 0, 600, false);
        Assert(v44timed.Contains("LOOP_MODE = \"timed\"") && v44timed.Contains("LOOP_SECONDS = 600")
               && v44timed.Contains("def loop_due()"),
            "v0.9.44: code.py carries the baked play options (timed 600s) with a loop_due gate");
        var v44once = PicoFirmwareExporter.BuildCodePy(new List<PicoFirmwareExporter.LightState>(), "M", "once", 0, 0, false);
        Assert(v44once.Contains("LOOP_MODE = \"once\""),
            "v0.9.44: once-mode bakes too (run the armed states a single pass)");
        Assert(v44vm.Contains("PicoFirmwareExporter.Export(dlg.FileName, Steps, Environment.MachineName,")
               && v44vm.Contains("_settings.PlayRepeatMode"),
            "v0.9.44: the export call passes the live Play Options");

        // 9-12) per-board presence LEDs fed by the post-connect PING
        Assert(MainViewModel.ParseBoardPresence("OK|PONG|pico-light 0.9.44|role=brain+keyboard+light|arm=promicro") == (true, true),
            "v0.9.44: a Pico brain answer lights both LEDs");
        Assert(MainViewModel.ParseBoardPresence("OK|PONG") == (false, true),
            "v0.9.44: a plain firmware-1.6 board lights only the Pro Micro arm LED");
        Assert(MainViewModel.ParseBoardPresence("OK|PONG|pico-light 0.9.44|role=brain|arm=missing") == (true, false),
            "v0.9.44: a brain without an arm lights only the Pico LED");
        Assert(v44mw.Contains("PicoPresent") && v44mw.Contains("ArmPresent")
               && v44vm.Contains("ParseBoardPresence") && v44vm.Contains("SendAsync(\"PING\")"),
            "v0.9.44: the status bar has Pico / Pro Micro indicators fed by the post-connect PING");

        // 13-15) keyboard board choice in Options, affecting code.py
        Assert(v44doc.Contains("public string KeyboardBoard")
               && v44opt.Contains("KbdArmRadio") && v44optc.Contains("KeyboardBoard")
               && v44vm.Contains("_settings.KeyboardBoard"),
            "v0.9.44: Options chooses which board executes the keyboard (pico / promicro)");
        // v0.9.60 — final contract: the keyboard ALWAYS runs on the Pico; the legacy
        // keyboard-board option no longer reaches the firmware (it stays an app-side default only).
        var v44arm = PicoFirmwareExporter.BuildCodePy(new List<PicoFirmwareExporter.LightState>(), "M", "forever", 0, 0, true);
        var v44pico = PicoFirmwareExporter.BuildCodePy(new List<PicoFirmwareExporter.LightState>(), "M", "forever", 0, 0, false);
        Assert(!v44arm.Contains("KBD_ON_ARM") && !v44pico.Contains("KBD_ON_ARM")
               && v44arm.Contains("KBD_PREFIXES") && v44pico.Contains("def handle_keyboard("),
            "v0.9.60: code.py always types locally on the Pico, whatever the legacy keyboard-board option says");

        // 16) version/banner
        Assert(v44csp.Contains("<Version>0.9.67</Version>") && v44vm.Contains("Classroom Studio v0.9.67"),
            "v0.9.44: version and banner match");

        // ── Step 45: v0.9.45 — stable rail popup · visible board roles · structural Next ──
        Console.WriteLine();
        Console.WriteLine("--- Step 45: v0.9.45 stable rail popup · visible board roles · structural Next ---");
        var v45mw   = V27ReadSrc("MainWindow.xaml");
        var v45mwc  = V27ReadSrc("MainWindow.xaml.cs");
        var v45vm   = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v45opt  = V27ReadSrc(Path.Combine("Views", "OptionsDialog.xaml"));
        var v45optc = V27ReadSrc(Path.Combine("Views", "OptionsDialog.xaml.cs"));
        var v45exp  = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
        var v45csp  = V27ReadSrc("Ams.UI.csproj");

        // 1-4) ContextMenu popup has one debounced owner — no raw MouseEnter open / async races
        Assert(v45mwc.Contains("_railDwellTimer") && v45mwc.Contains("TimeSpan.FromMilliseconds(220)")
               && v45mwc.Contains("ConfigureRailMenuTimers();"),
            "v0.9.45: rail hover uses one 220ms dwell timer configured once");
        Assert(v45mwc.Contains("_railWatchTimer") && v45mwc.Contains("_railOutsideTicks >= 3")
               && v45mwc.Contains("IsCursorInside(host) || IsCursorInside(menu)"),
            "v0.9.45+: one watcher closes only after the real cursor leaves both host and popup");
        Assert(v45mwc.Contains("private void OpenRailMenu(") && v45mwc.Contains("private void CloseRailMenu()")
               && v45mwc.Contains("menu.Closed -= RailMenu_Closed"),
            "v0.9.45: hover and caret click share an idempotent open/close state machine");
        Assert(!v45mwc.Contains("private async void RailBorder_MouseLeave")
               && !v45mwc.Contains("Task.Delay(400)"),
            "v0.9.45: the racing async MouseLeave implementation is gone");

        // 5-7) board executor is a dedicated visible tab, not a clipped horizontal row
        Assert(v45opt.Contains("x:Name=\"TabRoles\"") && v45opt.Contains("Content=\"تقسیم کار بردها\"")
               && v45opt.Contains("x:Name=\"RolesPanel\""),
            "v0.9.45: Options has a dedicated visible Board Roles tab");
        Assert(v45opt.Contains("Raspberry Pi Pico — مغز")
               && v45opt.Contains("Arduino Pro Micro — بازو"),
            "v0.9.45: both keyboard executor choices are explicit and readable");
        Assert(v45optc.Contains("RolesPanel.Visibility = tab == 3")
               && v45optc.Contains("TabRoles_Click") && v45opt.Contains("Width=\"500\""),
            "v0.9.45: tab switching exposes the roles panel and the dialog is wide enough");

        // 8-13) every loop owns one protected, self-healing # Next terminator
        var v45loop = new StepNode { Type = "forLoop" };
        v45loop.Children.Add(new StepNode { Type = "delay", Parent = v45loop });
        var v45list = new List<StepNode> { v45loop };
        Assert(MainViewModel.HealMissingLoopMarkers(v45list) == 1 && v45list.Count == 2
               && v45list[1].Type == "comment" && PropEx.GetString(v45list[1].Props, "text") == "Next",
            "v0.9.45: a bare loop receives one immediate # Next closing row");
        Assert(MainViewModel.HealMissingLoopMarkers(v45list) == 0 && v45list.Count == 2,
            "v0.9.45: loop-marker healing is idempotent");
        Assert(MainViewModel.IsStructuralMarkerIn(v45list, v45list[1]),
            "v0.9.45: the owned # Next marker is structural/protected");
        var v45inner = new StepNode { Type = "forLoop", Parent = v45loop };
        v45loop.Children.Add(v45inner);
        Assert(MainViewModel.HealMissingLoopMarkers(v45list) == 1
               && v45loop.Children.Count == 3 && PropEx.GetString(v45loop.Children[2].Props, "text") == "Next",
            "v0.9.45: nested loops receive their own Next recursively");
        Assert(v45vm.Contains("EnsureLoopMarker(newNode)") && v45vm.Contains("EnsureLoopMarker(node)")
               && v45vm.Contains("HealAllLoopMarkers();"),
            "v0.9.45: add/edit/open/import/paste paths create or heal Next markers");
        Assert(v45vm.Contains("if (container.Type == \"forLoop\") return t == \"Next\";")
               && v45vm.Contains("_scopeRanges[n] = (start, FlatSteps.Count - 1)"),
            "v0.9.45: the loop vein closes on its visible Next row");
        Assert(v45vm.Contains("OwnsNextMarker(item)")
               && v45vm.Contains("owned.Add(next)"),
            "v0.9.45: loop + Next are atomic for delete/cut/copy; Next cannot be orphaned");

        // 15) version family
        Assert(v45csp.Contains("<Version>0.9.67</Version>")
               && v45vm.Contains("Classroom Studio v0.9.67")
               && v45exp.Contains("BundleVersion = \"0.9.64f\""),
            "v0.9.45: app version, banner and Pico bundle version match");

        // ── Step 46: v0.9.46 — real cursor popup close · per-step keyboard board · Next inset ──
        Console.WriteLine();
        Console.WriteLine("--- Step 46: v0.9.46 cursor geometry · per-step keyboard routing · Next inset ---");
        var v46mw   = V27ReadSrc("MainWindow.xaml");
        var v46mwc  = V27ReadSrc("MainWindow.xaml.cs");
        var v46vm   = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v46row  = V27ReadSrc(Path.Combine("Models", "FlatStepRow.cs"));
        var v46defs = V27ReadSrc(Path.Combine("Models", "StepDefinitions.cs"));
        var v46fa   = V27ReadSrc(Path.Combine("Models", "StepTextsFa.cs"));
        var v46run  = V27ReadSrc(Path.Combine("Services", "RunEngine.cs"));
        var v46exp  = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
        var v46csp  = V27ReadSrc("Ams.UI.csproj");

        // 1-3) popup close uses actual screen-space cursor, never stale Popup.IsMouseOver
        Assert(v46mwc.Contains("System.Windows.Forms.Control.MousePosition")
               && v46mwc.Contains("PointToScreen(new System.Windows.Point(0, 0))")
               && v46mwc.Contains("PointToScreen(new System.Windows.Point(element.ActualWidth, element.ActualHeight))"),
            "v0.9.46: rail watcher compares the real cursor with host/popup screen rectangles");
        Assert(v46mwc.Contains("IsCursorInside(host) || IsCursorInside(menu)")
               && !v46mwc.Contains("menu.IsMouseOver || host?.IsMouseOver"),
            "v0.9.46: stale WPF IsMouseOver can no longer keep the popup open until click");
        Assert(v46mwc.Contains("catch (InvalidOperationException) { return false; }")
               && v46mwc.Contains("_railOutsideTicks >= 3"),
            "v0.9.46: disconnected popup visuals close safely through the single watcher");

        // 4-10) every keyboard step has Default/Pico/Pro Micro override with safe transport behavior
        foreach (var type in new[] { "keystroke", "typeText", "keyDown", "keyUp" })
        {
            var field = StepDefinitions.Get(type).Fields.SingleOrDefault(f => f.Key == "keyboardBoard");
            Assert(field is not null && field.Options is not null
                   && field.Options.SequenceEqual(new[] { "default", "pico", "promicro" }),
                $"v0.9.46: {type} exposes default/pico/promicro keyboard executor");
        }
        var v46key = new StepNode { Type = "keyDown", Props = new() { ["key"] = "A", ["keyboardBoard"] = "pico" } };
        Assert(StepDefinitions.RouteKeyboardCommand(v46key, "KDOWN|65") == "KBDPICO|KDOWN|65",
            "v0.9.46: a Pico-targeted step gets the KBDPICO envelope");
        v46key.Props["keyboardBoard"] = "promicro";
        Assert(StepDefinitions.RouteKeyboardCommand(v46key, "KDOWN|65") == "KBDARM|KDOWN|65"
               && StepDefinitions.RouteKeyboardCommand(v46key, "DLY|20") == "DLY|20",
            "v0.9.46: an arm-targeted step is enveloped while PC-local pseudo commands stay local");
        v46key.Props["keyboardBoard"] = "default";
        Assert(StepDefinitions.RouteKeyboardCommand(v46key, "KDOWN|65") == "KDOWN|65",
            "v0.9.46: Default preserves the global Options behavior and old plans");
        Assert(RunEngine.ResolveKeyboardRouteForTransport("KBDARM|KUP|65", false) == "KUP|65"
               && RunEngine.ResolveKeyboardRouteForTransport("KBDARM|KUP|65", true) == "KBDARM|KUP|65",
            "v0.9.46: direct Pro Micro unwraps its route; connected Pico receives the envelope");
        bool v46PicoRejected = false;
        try { RunEngine.ResolveKeyboardRouteForTransport("KBDPICO|KUP|65", false); }
        catch (InvalidOperationException) { v46PicoRejected = true; }
        Assert(v46PicoRejected,
            "v0.9.46: a Pico-targeted step fails clearly when only a direct Pro Micro is connected");
        // v0.9.60: keyboard always-on-Pico — firmware consumes KBDPICO/KBDARM locally, never forwards
        Assert(v46exp.Contains("line.startswith(\"KBDPICO|\")")
               || v46exp.Contains("line.startswith(\"KBDARM|\")"),
            "v0.9.46+v0.9.60: exported code.py still handles KBDPICO/KBDARM envelopes locally");

        // 11-12) v0.9.46 introduced a 12px Next inset; v0.9.47 retracted it for head alignment
        Assert(v46row.Contains("SummaryInset") && v46mw.Contains("SummaryInset"),
            "v0.9.46→47→55: the summary inset is back, marker rows only (Step 55)");
        Assert(v46row.Contains("public Thickness Indent => new(Depth * 22"),
            "v0.9.46: Next label leaves row depth/numbering and the red vein hierarchy unchanged");

        // 13-15) wiring, Persian coverage and version family
        Assert(v46run.Contains("StepDefinitions.RouteKeyboardCommand(s, cmd)")
               && v46vm.Contains("() => PicoPresent"),
            "v0.9.46: RunEngine routes per step using the live board identity");
        Assert(v46fa.Contains("keystroke:keyboardBoard") && v46fa.Contains("typeText:keyboardBoard")
               && v46fa.Contains("keyDown:keyboardBoard") && v46fa.Contains("keyUp:keyboardBoard"),
            "v0.9.46: all four new step fields have Persian labels");
        Assert(v46csp.Contains("<Version>0.9.67</Version>")
               && v46vm.Contains("Classroom Studio v0.9.67")
               && v46exp.Contains("BundleVersion = \"0.9.64f\""),
            "v0.9.46: app version, banner and Pico bundle version match");

        // ── Step 47: v0.9.47 — the structural Next label aligns with its loop head ──
        Console.WriteLine();
        Console.WriteLine("--- Step 47: v0.9.47 Next label alignment with the scope head ---");
        var v47row = V27ReadSrc(Path.Combine("Models", "FlatStepRow.cs"));
        var v47mw  = V27ReadSrc("MainWindow.xaml");
        var v47vm  = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v47csp = V27ReadSrc("Ams.UI.csproj");
        var v47exp = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));

        // 1-3) the Next-only inset is gone from the model and the row template
        Assert(v47row.Contains("SummaryInset") && v47row.Contains("IsMarkerRow"),
            "v0.9.47→55: FlatStepRow defines a marker-only summary inset again");
        Assert(v47mw.Contains("Margin=\"{Binding SummaryInset}\""),
            "v0.9.47→55: the row template binds the per-row summary margin again");
        Assert(!v47row.Contains("new Thickness(12, 0, 0, 0)"),
            "v0.9.47: the 12px Next shift is fully retracted");

        // 4-6) alignment is structural: shared depth + fixed toggle column
        Assert(v47row.Contains("public Thickness Indent => new(Depth * 22"),
            "v0.9.47: row indent still derives only from Depth (22px per level)");
        Assert(v47mw.Contains("x:Name=\"RowTextAnchor\"")
               && v47mw.Contains("<ColumnDefinition Width=\"14\" />")
               && v47mw.Contains("x:Name=\"SummaryText\""),
            "v0.9.47: the 14px toggle column is fixed, so every summary text starts at the same x");
        Assert(v47vm.Contains("if (container.Type == \"forLoop\") return t == \"Next\";"),
            "v0.9.47: Next is still the loop's own sibling closing row, not a child");

        // 7-9) numbering, vein endpoint and version family untouched
        Assert(v47vm.Contains("_scopeRanges[n] = (start, FlatSteps.Count - 1)"),
            "v0.9.47: the red vein still closes on the visible Next row");
        Assert(v47row.Contains("public string Number") && v47row.Contains("public int Depth"),
            "v0.9.47: row numbering and depth model are unchanged");
        Assert(v47csp.Contains("<Version>0.9.67</Version>")
               && v47vm.Contains("Classroom Studio v0.9.67")
               && v47exp.Contains("BundleVersion = \"0.9.64f\""),
            "v0.9.47: app version, banner and Pico bundle version match");

        Console.WriteLine();
        Console.WriteLine("--- Step 48: v0.9.48 Next everywhere, atomic drag unit, edge auto-scroll, collapsed Options tabs ---");

        // 1-6) behavioural: every condition-less child-capable container closes with # Next
        var v48pg = new StepNode { Type = "parallelGroup" };
        v48pg.Children.Add(new StepNode { Type = "delay", Parent = v48pg });
        var v48list = new List<StepNode> { v48pg };
        Assert(MainViewModel.HealMissingLoopMarkers(v48list) == 1 && v48list.Count == 2
               && PropEx.GetString(v48list[1].Props, "text") == "Next",
            "v0.9.48: a Parallel Group without a closing row receives its # Next");
        var v48pkg = new StepNode { Type = "randomPackage" };
        v48pkg.Children.Add(new StepNode { Type = "delay", Parent = v48pkg });
        var v48list2 = new List<StepNode> { v48pkg };
        Assert(MainViewModel.HealMissingLoopMarkers(v48list2) == 1 && v48list2.Count == 2
               && PropEx.GetString(v48list2[1].Props, "text") == "Next",
            "v0.9.48: a Random Package without a closing row receives its # Next");
        Assert(MainViewModel.HealMissingLoopMarkers(v48list) == 0 && MainViewModel.HealMissingLoopMarkers(v48list2) == 0,
            "v0.9.48: healing stays idempotent — a block that already owns its # Next is untouched");
        Assert(MainViewModel.IsStructuralMarkerIn(v48list, v48list[1])
               && MainViewModel.IsStructuralMarkerIn(v48list2, v48list2[1]),
            "v0.9.48: the # Next of a parallel group / package is delete-protected like a loop's");
        Assert(MainViewModel.FindClosingMarkerOwnerIn(v48list, v48list[1], out var v48eb) == v48pg && v48eb is null,
            "v0.9.48: the drop-target resolver knows which block owns a # Next row");
        var v48if = MkIf(); var v48else = MkElse(); var v48end = MkEnd();
        var v48iflist = new List<StepNode> { v48if, v48else, v48end };
        Assert(MainViewModel.FindClosingMarkerOwnerIn(v48iflist, v48end, out var v48eb2) == v48if && v48eb2 == v48else
               && MainViewModel.FindElseMarkerOwnerIn(v48iflist, v48else) == v48if,
            "v0.9.48: End If / Else rows resolve to their owning If head and its Else branch");

        // 7-12) source-level guards
        var v48vm   = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v48mw   = V27ReadSrc("MainWindow.xaml.cs");
        var v48optc = V27ReadSrc(Path.Combine("Views", "OptionsDialog.xaml.cs"));
        var v48optx = V27ReadSrc(Path.Combine("Views", "OptionsDialog.xaml"));
        var v48csp  = V27ReadSrc("Ams.UI.csproj");
        var v48exp  = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
        Assert(v48vm.Contains("OwnsNextMarker")
               && v48vm.Contains("type is \"forLoop\" or \"parallelGroup\" or \"randomPackage\""),
            "v0.9.48: one helper names every condition-less child-capable container");
        Assert(!v48vm.Contains("if (container.Type == \"parallelGroup\") return false;"),
            "v0.9.48: the vein walk no longer treats a parallel group's # Next as a foreign row");
        Assert(v48vm.Contains("var moveSet = new List<StepNode> { node }")
               && v48vm.Contains("moveSet.Contains(target)"),
            "v0.9.48: a drag moves the container together with its # Next row — never left behind, never separated");
        Assert(v48vm.Contains("FindClosingMarkerOwner") && v48vm.Contains("adoptParent") && v48vm.Contains("prependParent"),
            "v0.9.48: drops onto marker rows resolve inside the block, so no marker can be detached by a drop");
        Assert(v48mw.Contains("ScrollToVerticalOffset") && v48mw.Contains("FindDescendant<"),
            "v0.9.48: the list auto-scrolls near its edges during a drag, so a bottom row can reach the top");
        Assert(!v48optc.Contains("Visibility.Hidden") && v48optc.Contains("Visibility.Collapsed")
               && !v48optx.Contains("Visibility=\"Hidden\""),
            "v0.9.48: inactive Options tabs are Collapsed — Hidden kept reserving their height and stretched the dialog");

        // 13) version family
        Assert(v48csp.Contains("<Version>0.9.67</Version>")
               && v48vm.Contains("Classroom Studio v0.9.67")
               && v48exp.Contains("BundleVersion = \"0.9.64f\""),
            "v0.9.48: app version, banner and Pico bundle version match");

        Console.WriteLine();
        Console.WriteLine("--- Step 49: v0.9.49 direction-aware plain-row drops + docked Options dialog ---");

        // 1-5) the reported no-op: dropping Key Down (1.2) onto the middle of Type text (1.1)
        Assert(MainViewModel.DropBeforeForPlainRow("into", true, 1, 0),
            "v0.9.49: the reported case — the row below dropped onto the middle of the row above inserts BEFORE it (no silent no-op)");
        Assert(!MainViewModel.DropBeforeForPlainRow("into", true, 0, 1),
            "v0.9.49: dragging down onto the middle of the row below still lands after it (unchanged)");
        Assert(MainViewModel.DropBeforeForPlainRow("before", true, 1, 0)
               && !MainViewModel.DropBeforeForPlainRow("after", true, 0, 1),
            "v0.9.49: explicit before/after bands are respected verbatim");
        Assert(!MainViewModel.DropBeforeForPlainRow("into", false, 3, 0),
            "v0.9.49: a cross-list middle drop keeps the previous after-behaviour");
        Assert(MainViewModel.IsElseMarkerRow(new StepNode { Type = "comment", Props = new Dictionary<string, object?> { ["text"] = "Else" } })
               && !MainViewModel.IsElseMarkerRow(new StepNode { Type = "comment", Props = new Dictionary<string, object?> { ["text"] = "Next" } }),
            "v0.9.49: the view can ask the view model which rows are Else rows (for the three-band drop)");

        // 6-9) source-level guards
        var v49vm   = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v49mw   = V27ReadSrc("MainWindow.xaml.cs");
        var v49optc = V27ReadSrc(Path.Combine("Views", "OptionsDialog.xaml.cs"));
        var v49optx = V27ReadSrc(Path.Combine("Views", "OptionsDialog.xaml"));
        var v49csp  = V27ReadSrc("Ams.UI.csproj");
        var v49exp  = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
        Assert(v49vm.Contains("DropBeforeForPlainRow(mode, sameSiblings, nodeIdxBefore, targetIdxBefore)")
               && v49vm.Contains("sameSiblings") && v49vm.Contains("nodeIdxBefore"),
            "v0.9.49: MoveNode decides a plain-row middle drop by drag direction");
        Assert(v49mw.Contains("canInto") && v49mw.Contains("IsElseMarkerRow(target)") && v49mw.Contains("1.0 - edge"),
            "v0.9.49: plain rows split the drop band 50/50 — only containers and Else rows keep the middle band");
        Assert(v49optc.Contains("WindowStartupLocation.Manual") && v49optc.Contains("Owner.ActualWidth")
               && v49optc.Contains("SystemParameters.WorkArea"),
            "v0.9.49: the Options dialog docks to the owner's right edge instead of floating");
        Assert(v49optx.Contains("Grid.Row=\"1\"") && v49optx.Contains("RowDefinition Height=\"*\""),
            "v0.9.49: the Options layout pins the buttons to the bottom of the docked dialog");

        // 10) version family
        Assert(v49csp.Contains("<Version>0.9.67</Version>")
               && v49vm.Contains("Classroom Studio v0.9.67")
               && v49exp.Contains("BundleVersion = \"0.9.64f\""),
            "v0.9.49: app version, banner and Pico bundle version match");


        // ── v0.9.50: board preparation (USB-identity HEX builder, IDE board install, ISP flash assets, checkup) ──
        static string V50FindRepoFile(string rel)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 9 && dir is not null; i++, dir = dir.Parent)
            {
                foreach (var cand in new[] {
                    Path.Combine(dir.FullName, "ams-shell", rel),
                    Path.Combine(dir.FullName, rel) })
                    if (File.Exists(cand)) return cand;
            }
            throw new FileNotFoundException("repo file not found: " + rel);
        }
        static string V50Sha256(string text, System.Text.Encoding enc)
            => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(enc.GetBytes(text))).ToLowerInvariant();

        var v50cat = V50FindRepoFile(Path.Combine("caterina", "Caterina.hex"));
        var v50flash = Ams.UI.Services.BoardHexService.ParseHex(v50cat);
        Assert(v50flash.Length == Ams.UI.Services.BoardHexService.FlashSize
               && v50flash[Ams.UI.Services.BoardHexService.DeviceDescOffset + 8] == 0x50
               && v50flash[Ams.UI.Services.BoardHexService.DeviceDescOffset + 9] == 0x1D,
            "v0.9.50: bundled Caterina.hex parses to a 32 KB image carrying the stock AMS identity 0x1D50");

        // golden case A — Microchip identity + serial "Shool-6DA59208" (bytes proven by the original tool)
        var v50patchedA = Ams.UI.Services.BoardHexService.PatchHex(v50flash, "Shool-6DA59208",
            vid: 0x04D8, pid: 0x000A, classType: 0x02, subclass: 0x00, protocol: 0x00,
            product: "CDC RS-232 Emulation Demo", manufacturer: "Microchip");
        Assert(V50Sha256(Ams.UI.Services.BoardHexService.ToHex(v50patchedA), System.Text.Encoding.ASCII)
               == "a1f6560e6d2afb8828eb84f6232e08a5574d8949505a2fd7b9394abefab21064",
            "v0.9.50: patched HEX is byte-identical to the original tool (golden sha256, Microchip identity)");
        Assert(Convert.ToHexString(v50patchedA.AsSpan(Ams.UI.Services.BoardHexService.DeviceDescOffset, 18))
               == "1201100102000008D8040A00010002010201",
            "v0.9.50: device descriptor carries VID 04D8 / PID 000A with CDC class and string indexes");
        Assert(v50patchedA[Ams.UI.Services.BoardHexService.StringDescBase] == 30
               && v50patchedA[Ams.UI.Services.BoardHexService.StringDescBase + 1] == 0x03,
            "v0.9.50: the serial string descriptor lands at 0x7F00 with its length prefix");

        // golden case B — serial only: identity untouched
        var v50patchedB = Ams.UI.Services.BoardHexService.PatchHex(v50flash, "AMS-0A1B2C3D");
        Assert(V50Sha256(Ams.UI.Services.BoardHexService.ToHex(v50patchedB), System.Text.Encoding.ASCII)
               == "7f365e44de98c4d153d6b3ee17f3037ba9ac59018ba191c697c38fdece30e252",
            "v0.9.50: serial-only patch is byte-identical to the original tool (golden sha256)");
        Assert(v50patchedB[Ams.UI.Services.BoardHexService.DeviceDescOffset + 8] == 0x50
               && v50patchedB[Ams.UI.Services.BoardHexService.DeviceDescOffset + 11] == 0x61,
            "v0.9.50: serial-only patch keeps VID/PID untouched (0x1D50 / 0x615E)");

        // validation mirrors the original tool's messages
        var v50err = 0;
        try { Ams.UI.Services.BoardHexService.ParseVidPid("1234"); } catch (ArgumentException ex) { if (ex.Message.Contains("0x1D50")) v50err++; }
        try { Ams.UI.Services.BoardHexService.ParseVidPid("0x0000"); } catch (ArgumentException) { v50err++; }
        try { Ams.UI.Services.BoardHexService.ValidateSerial(""); } catch (ArgumentException) { v50err++; }
        try { Ams.UI.Services.BoardHexService.ValidateSerial(new string('x', 30)); } catch (ArgumentException) { v50err++; }
        try { Ams.UI.Services.BoardHexService.ValidateSerial("سریال"); } catch (ArgumentException) { v50err++; }
        try { Ams.UI.Services.BoardHexService.ValidateUsbString("محصول", "نام محصول"); } catch (ArgumentException) { v50err++; }
        Assert(v50err == 6, "v0.9.50: all six invalid inputs are rejected with the original tool's messages");
        Assert(Ams.UI.Services.BoardHexService.ParseVidPid("0x1D50") == 0x1D50
               && System.Text.RegularExpressions.Regex.IsMatch(Ams.UI.Services.BoardHexService.GenerateRandomSerial("T"), @"^T-[0-9A-F]{8}$"),
            "v0.9.50: a valid VID parses and random serials match prefix-XXXXXXXX");

        // boards.txt block — golden text + idempotent install/uninstall on a scratch file
        var v50block = Ams.UI.Services.BoardsTxtService.BuildBoardBlock(name: "AMS Macro Studio",
            bootVid: "0x04D8", bootPid: "0x000A", appPid: "0x000B",
            product: "CDC RS-232 Emulation", manufacturer: "Microchip", crossCore: true);
        Assert(V50Sha256(v50block, System.Text.Encoding.UTF8) == "46ff0f97defb6e8765487ba17dd05b7dce655e83027f77956bba196d3b589d7f",
            "v0.9.50: boards.txt block is byte-identical to the original tool (golden sha256)");
        var v50tmp = Path.Combine(Path.GetTempPath(), "ams-boardprep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(v50tmp);
        try
        {
            var v50bt = Path.Combine(v50tmp, "boards.txt");
            File.WriteAllText(v50bt, "# stock\n\n");
            Ams.UI.Services.BoardsTxtService.InstallBoardBlock(v50bt, v50block);
            var v50once = File.ReadAllText(v50bt);
            Ams.UI.Services.BoardsTxtService.InstallBoardBlock(v50bt, v50block);   // again — replace, not duplicate
            Assert(File.ReadAllText(v50bt) == v50once
                   && v50once.Split(Ams.UI.Services.BoardsTxtService.BoardMarkStart).Length == 2,
                "v0.9.50: installing the board block twice is idempotent (exactly one managed block)");
            Assert(Ams.UI.Services.BoardsTxtService.UninstallBoardBlock(v50bt)
                   && !File.ReadAllText(v50bt).Contains("AMS-BOARD"),
                "v0.9.50: uninstall removes the block and leaves zero markers");
        }
        finally { Directory.Delete(v50tmp, true); }

        // checkup classification + bundled assets
        Assert(Ams.UI.Services.BoardCheckupService.ClassifyPort(0x1D50, 0x615E).Mode == "بوت‌لودر"
               && Ams.UI.Services.BoardCheckupService.ClassifyPort(0x04D8, 0x000B).Mode == "اپلیکیشن"
               && Ams.UI.Services.BoardCheckupService.ClassifyPort(0xFFFF, 0xFFFF).Name is null,
            "v0.9.50: checkup classifies bootloader/app identities and unknown pairs");
        Assert(Ams.UI.Services.BundledAssets.Find(AppContext.BaseDirectory, "caterina", "Caterina.hex") is not null
               && Ams.UI.Services.BundledAssets.Find(AppContext.BaseDirectory, "tools", "isp_flash.py") is not null,
            "v0.9.50: Caterina.hex and isp_flash.py are bundled and locatable from the build output");

        // source-level asserts
        var v50hexSvc = V27ReadSrc(Path.Combine("Services", "BoardHexService.cs"));
        var v50btSvc = V27ReadSrc(Path.Combine("Services", "BoardsTxtService.cs"));
        var v50chkSvc = V27ReadSrc(Path.Combine("Services", "BoardCheckupService.cs"));
        var v50win = V27ReadSrc(Path.Combine("Views", "BoardPrepWindow.xaml"));
        var v50vm2 = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v50mw2 = V27ReadSrc("MainWindow.xaml");
        var v50csp2 = V27ReadSrc("Ams.UI.csproj");
        var v50exp2 = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
        Assert(v50hexSvc.Contains("PatchHex") && v50hexSvc.Contains("DeviceDescOffset = 0x7EE2")
               && v50btSvc.Contains("BuildBoardBlock") && v50chkSvc.Contains("Handshake"),
            "v0.9.50: the three board-preparation services exist with their core APIs");
        Assert(v50win.Contains("ساخت HEX") && v50win.Contains("نصب در IDE") && v50win.Contains("چکاپ برد"),
            "v0.9.50: the board preparation window has the Persian tabs");
        Assert(v50mw2.Contains("BoardPrepCommand") && v50vm2.Contains("BoardPrepWindow"),
            "v0.9.50: the Tools menu opens the board preparation window");
        // v0.9.51 — version pins move with the release (were v0.9.50)
        Assert(v50csp2.Contains("<Version>0.9.67</Version>")
               && v50vm2.Contains("Classroom Studio v0.9.67")
               && v50exp2.Contains("BundleVersion = \"0.9.64f\""),
            "v0.9.50→51: app version, banner and Pico bundle version match");

        // ── v0.9.51: COM-history cleanup (phantom detection + elevated delete with backup) ──
        Assert(BoardCleanupService.PortNumber("COM13") == 13
               && BoardCleanupService.PortNumber("com7") == 7,
            "v0.9.51: PortNumber parses COM names case-insensitively");
        Assert(BoardCleanupService.PortNumber("COM") == -1
               && BoardCleanupService.PortNumber("COM0") == -1
               && BoardCleanupService.PortNumber("X13") == -1,
            "v0.9.51: PortNumber rejects empty, zero and non-COM names");
        {
            var db = new byte[32];
            db[0] = 0b00000011;   // COM1 + COM2 in use
            db[2] = 0b00000001;   // COM17 in use
            var cleared = BoardCleanupService.ClearComDbBits(db, new[] { 17 });
            Assert(cleared[2] == 0 && cleared[0] == 0b00000011 && cleared.Length == 32,
                "v0.9.51: ComDB bit for COM17 cleared, COM1/COM2 untouched");
            var untouched = BoardCleanupService.ClearComDbBits(db, new[] { 0, 257 });
            Assert(untouched[0] == 0b00000011 && untouched[2] == 0b00000001,
                "v0.9.51: ComDB cleanup ignores out-of-range port numbers");
        }
        {
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "COM17" };
            Assert(BoardCheckupService.IsPhantom("COM13", live)
                   && !BoardCheckupService.IsPhantom("com17", live),
                "v0.9.51: phantom = assigned port absent from the live set (case-insensitive)");
            var v51rows = new List<BoardCheckupService.PortInfo>
            {
                new("COM13", "USB Serial Device", 0x1D50, 0x615E, "5&3a692ce1&0&10", "پیش‌فرض AMS", "بوت‌لودر", false, true, @"SYSTEM\CurrentControlSet\Enum\USB\VID_1D50&PID_615E\5&3a692ce1&0&10"),
                new("COM17", "USB Serial Device", 0x04D8, 0x000B, "6&36f80025&0&0000", "Microchip CDC Demo", "اپلیکیشن", false, false, @"SYSTEM\CurrentControlSet\Enum\USB\VID_04D8&PID_000B\6&36f80025&0&0000"),
                new("COM20", "ZyXEL", 0x0586, 0x3440, "x", null, null, false, true, @"SYSTEM\CurrentControlSet\Enum\USB\VID_0586&PID_3440\x"),
                new("COM99", "plain", 0, 0, "", null, null, false, true, ""),
            };
            var v51targets = BoardCheckupService.PhantomBoardsOfKnownFamilies(v51rows, BoardCheckupService.KnownVidPidSet());
            Assert(v51targets.Count == 1 && v51targets[0].Device == "COM13",
                "v0.9.51: cleanup candidates = phantom + known family + registry path");
        }
        Assert(BoardCleanupService.VidPidFromRegPath(@"SYSTEM\CurrentControlSet\Enum\USB\VID_1D50&PID_615E\6&55c4b84&0&0000") == (0x1D50, 0x615E),
            "v0.9.51: registry instance path parses VID/PID");
        {
            var allow = new HashSet<(int, int)> { (0x1D50, 0x615E) };
            Assert(BoardCleanupService.IsAllowedRegPath(@"SYSTEM\CurrentControlSet\Enum\USB\VID_1D50&PID_615E\inst1", allow)
                   && !BoardCleanupService.IsAllowedRegPath(@"SYSTEM\CurrentControlSet\Enum\USB", allow)
                   && !BoardCleanupService.IsAllowedRegPath(@"SYSTEM\CurrentControlSet\Services\VID_1D50&PID_615E\inst1", allow)
                   && !BoardCleanupService.IsAllowedRegPath(@"SYSTEM\CurrentControlSet\Enum\USB\VID_0586&PID_3440\inst1", allow),
                @"v0.9.51: cleanup honours only allow-listed Enum\USB instance paths");
        }
        {
            var req = new BoardCleanupService.CleanupRequest(
                new List<BoardCleanupService.CleanupItem> { new(@"SYSTEM\CurrentControlSet\Enum\USB\VID_1D50&PID_615E\a", 13) },
                new List<string> { "1D50:615E" }, "C:\tmp", "C:\tmp\r.log");
            var back = System.Text.Json.JsonSerializer.Deserialize<BoardCleanupService.CleanupRequest>(
                System.Text.Json.JsonSerializer.Serialize(req));
            Assert(back is not null && back.Items.Count == 1 && back.Items[0].Port == 13 && back.AllowedVidPid[0] == "1D50:615E",
                "v0.9.51: the cleanup request round-trips through JSON");
        }
        {
            var phantomRow = new BoardCheckupService.PortInfo("COM13", "USB Serial Device", 0x1D50, 0x615E, "sn", "پیش‌فرض AMS", "بوت‌لودر", false, true, "path");
            var liveRow = phantomRow with { IsPhantom = false };
            Assert(BoardCheckupService.DescribePort(phantomRow).Contains("تاریخچه")
                   && !BoardCheckupService.DescribePort(liveRow).Contains("تاریخچه"),
                "v0.9.51: history rows carry the visible marker, live rows do not");
        }
        {
            var rows51 = new List<BoardCheckupService.PortInfo>
            {
                new("COM10", "prog", 0x2341, 0x8036, "", "Arduino Leonardo", "احتمالاً پروگرمر (ArduinoISP)", true, true, "p"),
                new("COM13", "board", 0x1D50, 0x615E, "", "پیش‌فرض AMS", "بوت‌لودر", false, true, "p"),
            };
            var msgs = BoardCheckupService.SummarizeCheckup(rows51, new Dictionary<string, string?>());
            Assert(!msgs.Any(m => m.Text.Contains("آنلاین"))
                   && msgs.Any(m => m.Text.Contains("تاریخچه") && m.Text.Contains("COM10") && m.Text.Contains("COM13")),
                "v0.9.51: phantom programmer/boards are not reported online and are listed as history");
        }
        {
            // every Persian instruction/note TextBlock in the board-prep window is right-aligned.
            // (char-literal quotes — the v0.9.32 lesson: no escaped quotes inside generated C#)
            var x51 = V27ReadSrc(Path.Combine("Views", "BoardPrepWindow.xaml"));
            int faBlocks = 0, unaligned = 0;
            foreach (var mm in System.Text.RegularExpressions.Regex.Matches(x51, "<TextBlock[^>]*>").Cast<System.Text.RegularExpressions.Match>())
            {
                var tag = mm.Value;
                int ti = tag.IndexOf("Text=", StringComparison.Ordinal);
                if (ti < 0) continue;
                int qs = tag.IndexOf('"', ti + 5);
                int qe = qs < 0 ? -1 : tag.IndexOf('"', qs + 1);
                if (qs < 0 || qe < 0) continue;
                var val = tag.Substring(qs + 1, qe - qs - 1);
                if (!val.Any(ch => ch >= '؀' && ch <= 'ۿ')) continue;
                faBlocks++;
                if (!tag.Contains("TextAlignment=", StringComparison.Ordinal)) unaligned++;
            }
            Assert(faBlocks >= 5 && unaligned == 0,
                "v0.9.51: every Persian instruction TextBlock in BoardPrepWindow is right-aligned");
        }
        // source-level asserts
        var v51cln = V27ReadSrc(Path.Combine("Services", "BoardCleanupService.cs"));
        var v51chk = V27ReadSrc(Path.Combine("Services", "BoardCheckupService.cs"));
        var v51win = V27ReadSrc(Path.Combine("Views", "BoardPrepWindow.xaml"));
        var v51wcs = V27ReadSrc(Path.Combine("Views", "BoardPrepWindow.xaml.cs"));
        var v51app = V27ReadSrc("App.xaml.cs");
        var v51csp = V27ReadSrc("Ams.UI.csproj");
        var v51vm3 = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var v51exp = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
        Assert(v51cln.Contains("ClearComDbBits") && v51cln.Contains("DeleteSubKeyTree") && v51cln.Contains("reg.exe"),
            "v0.9.51: the cleanup service carries arbiter-bit, delete and backup logic");
        Assert(v51chk.Contains("LiveSerialCommPorts") && v51chk.Contains("IsPhantom"),
            "v0.9.51: the checkup service carries phantom detection");
        Assert(v51win.Contains("BtnCleanupCom") && v51win.Contains("TxtBoardSpecs")
               && v51wcs.Contains("BtnCleanupCom_Click") && v51app.Contains("--com-cleanup"),
            "v0.9.51: the checkup tab has the cleanup button, the board-specs card, and the app has the elevated entry");

        // ── v0.9.52: step-by-step wizard, right-to-left layout, classroom devices, hot-plug ──
        Assert(HotPlugWatcher.PortNumber("COM17") == 17 && HotPlugWatcher.PortNumber("com3") == 3
               && HotPlugWatcher.PortNumber("LPT1") == 0 && HotPlugWatcher.PortNumber(null) == 0,
            "v0.9.52: hot-plug watcher reads COM numbers and ignores everything else");
        {
            var before52 = new List<string> { "COM3", "COM4" };
            var after52 = new List<string> { "com3", "COM4", "COM17" };
            var arrived52 = HotPlugWatcher.Added(before52, after52);
            var left52 = HotPlugWatcher.Added(after52, before52);
            Assert(arrived52.Count == 1 && arrived52[0] == "COM17",
                "v0.9.52: a board plugged in later shows up as a new port (case-insensitive)");
            Assert(left52.Count == 0,
                "v0.9.52: nothing is reported as unplugged when the old ports are still there");
            Assert(HotPlugWatcher.Added(after52, before52.GetRange(0, 1)).Count == 0
                   && HotPlugWatcher.Added(before52, null).Count == 0,
                "v0.9.52: the port diff tolerates shorter and null sets");
            Assert(HotPlugWatcher.DescribeChange(arrived52, new List<string> { "COM4" }).Contains("COM17")
                   && HotPlugWatcher.DescribeChange(null, null).Length == 0,
                "v0.9.52: the port-change log line names the ports and stays empty when nothing changed");
        }
        Assert(HotPlugWatcher.ShouldAutoConnect(true, false, 1, true)
               && !HotPlugWatcher.ShouldAutoConnect(false, false, 1, true)
               && !HotPlugWatcher.ShouldAutoConnect(true, true, 1, true)
               && !HotPlugWatcher.ShouldAutoConnect(true, false, 0, true)
               && !HotPlugWatcher.ShouldAutoConnect(true, false, 1, false),
            "v0.9.52: auto-connect fires only when idle, disconnected and a port really arrived");
        Assert(HotPlugWatcher.CurrentPorts() is not null && HotPlugWatcher.PollMs > 0 && HotPlugWatcher.PollMs <= 3000,
            "v0.9.52: the live port scan never throws and polls at a sane interval");
        {
            var vm52 = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
            Assert(vm52.Contains("StartPortWatcher") && vm52.Contains("DispatcherTimer")
                   && vm52.Contains("ConnectCommand.Execute") && vm52.Contains("SyncPortChoices"),
                "v0.9.52: the shell starts the watcher and connects itself when a board appears");

            var x52 = V27ReadSrc(Path.Combine("Views", "BoardPrepWindow.xaml"));
            var c52 = V27ReadSrc(Path.Combine("Views", "BoardPrepWindow.xaml.cs"));
            Assert(x52.Contains("FlowDirection=" + '"' + "RightToLeft" + '"'),
                "v0.9.52: the board window itself is right-to-left, not just per-label alignment");
            int ltr52 = System.Text.RegularExpressions.Regex.Matches(x52, "FlowDirection=.LeftToRight.").Count;
            Assert(ltr52 >= 10,
                "v0.9.52: log, preview and technical fields stay left-to-right inside the RTL window");
            int icons52 = System.Text.RegularExpressions.Regex.Matches(x52, "<Viewbox").Count;
            int paths52 = System.Text.RegularExpressions.Regex.Matches(x52, "<Path ").Count;
            Assert(icons52 >= 4 && paths52 >= 12,
                "v0.9.52: every step tab carries a drawing of what happens in that step");
            Assert(x52.Contains("BtnNextStep") && x52.Contains("BtnPrevStep") && x52.Contains("TxtStepHint")
                   && c52.Contains("BtnNextStep_Click") && c52.Contains("GoStep"),
                "v0.9.52: the wizard navigator moves the user forward and back through the steps");
            Assert(c52.Contains("ClampStep") && c52.Contains("StepHint") && c52.Contains("SyncWizardNav"),
                "v0.9.52: the step number is clamped to 1..4 and every  step explains itself");
            Assert(x52.Contains("TxtBoardSpecs1") && c52.Contains("TxtBoardSpecs1.Text = specs")
                   && c52.Contains("TxtBoardSpecs.Text = specs"),
                "v0.9.52: board specs are entered once in step 1 and mirrored into step 2");

            var hx52 = V27ReadSrc(Path.Combine("Services", "BoardHexService.cs"));
            var ck52 = V27ReadSrc(Path.Combine("Services", "BoardCheckupService.cs"));
            var schools = new[] { "stm32", "xiao", "microchip", "legospike", "m5stack" };
            int found52 = 0;
            foreach (var key in schools) if (hx52.Contains('"' + key + '"')) found52++;
            Assert(found52 == schools.Length,
                "v0.9.52: the identity list carries the six classroom devices schools hand to students");
            Assert(hx52.Contains("0x0694, 0x0009") && hx52.Contains("0x303A, 0x1001") && hx52.Contains("0x04D8, 0x000A"),
                "v0.9.52: Pico, LEGO SPIKE and Circuit Playground use their real USB identities");
            Assert(ck52.Contains("(0x0694, 0x0009)") && ck52.Contains("(0x0694, 0x000A)")
                   && ck52.Contains("(0x303A, 0x1001)") && ck52.Contains("(0x303A, 0x1002)"),
                "v0.9.52: checkup recognises the new devices in both bootloader and application mode");
        }
        Assert(v51csp.Contains("<Version>0.9.67</Version>")
               && v51vm3.Contains("Classroom Studio v0.9.67")
               && v51exp.Contains("BundleVersion = \"0.9.64f\""),
            "v0.9.52: version pins for this release (csproj + banner + bundle)");


        // ── v0.9.53: correct live-port detection (PnP presence) + COM-history deletion ladder ──
        Console.WriteLine("--- Step 53: PnP presence detection + COM-history deletion ladder ---");
        {
            var ck53 = V27ReadSrc(Path.Combine("Services", "BoardCheckupService.cs"));
            var cl53 = V27ReadSrc(Path.Combine("Services", "BoardCleanupService.cs"));
            var win53 = V27ReadSrc(Path.Combine("Views", "BoardPrepWindow.xaml.cs"));
            var vm53 = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));

            Assert(BoardCheckupService.DeviceInstanceId("VID_2341&PID_8036&MI_00", "6&299f51fa&0&0000")
                       == @"USB\VID_2341&PID_8036&MI_00\6&299f51fa&0&0000",
                "v0.9.53: a scan row is turned into the PnP device instance id cfgmgr32 and pnputil expect");
            Assert(BoardCheckupService.DeviceInstanceId("", "6&1") == ""
                   && BoardCheckupService.DeviceInstanceId("VID_2341&PID_8036", " ") == "",
                "v0.9.53: an incomplete row yields no instance id instead of a broken one");

            var live53 = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "COM17" };
            Assert(BoardCheckupService.IsHistoryRow(true, "COM11", live53) == false,
                "v0.9.53: a port the PnP manager reports as attached is live even when SERIALCOMM omits it");
            Assert(BoardCheckupService.IsHistoryRow(false, "COM17", live53) == true,
                "v0.9.53: a detached devnode is history even when a stale SERIALCOMM value remains");
            Assert(BoardCheckupService.IsHistoryRow(null, "COM17", live53) == false
                   && BoardCheckupService.IsHistoryRow(null, "COM11", live53) == true,
                "v0.9.53: when cfgmgr32 cannot answer, the SERIALCOMM set is the fallback");
            Assert(BoardCheckupService.PresenceSource(null).Contains("SERIALCOMM")
                   && BoardCheckupService.PresenceSource(true).Contains("PnP"),
                "v0.9.53: the log states which source decided presence");
            Assert(ck53.Contains("cfgmgr32.dll") && ck53.Contains("CM_Locate_DevNodeW")
                   && ck53.Contains("SerialPort.GetPortNames()"),
                "v0.9.53: presence uses cfgmgr32 like the reference tool, with the driver name list as backup");

            var comp53 = @"SYSTEM\CurrentControlSet\Enum\USB\VID_2341&PID_8036&MI_00\6&299f51fa&0&0000";
            var vp53 = BoardCleanupService.VidPidFromRegPath(comp53);
            Assert(vp53 is not null && vp53.Value.Vid == 0x2341 && vp53.Value.Pid == 0x8036,
                "v0.9.53: the reported case — a composite path with &MI_00 now parses instead of being rejected");
            Assert(BoardCleanupService.ParentKeyFromRegPath(comp53) == "VID_2341&PID_8036&MI_00",
                "v0.9.53: the .reg backup exports the real parent key, function suffix included");
            Assert(BoardCleanupService.InstanceIdFromRegPath(comp53)
                       == @"USB\VID_2341&PID_8036&MI_00\6&299f51fa&0&0000",
                "v0.9.53: the history entry yields the instance id pnputil removes");
            Assert(BoardCleanupService.InstanceIdFromRegPath(@"SYSTEM\CurrentControlSet\Services\Foo") == "",
                "v0.9.53: paths outside Enum-USB still parse to nothing (scope guard intact)");

            var exp53 = BoardCleanupService.ExpandFamilies(new[] { (0x1D50, 0x615E) });
            Assert(exp53.Contains((0x1D50, 0x615E)) && exp53.Contains((0x1D50, 0x615F)),
                "v0.9.53: allowing a bootloader identity also allows its application PID (boot + 1)");
            Assert(BoardCleanupService.IsAllowedRegPath(
                       @"SYSTEM\CurrentControlSet\Enum\USB\VID_1D50&PID_615F&MI_00\6&328b31fb&0&0000", exp53),
                "v0.9.53: the exact entry v0.9.52 refused (1D50:615F composite) is now in scope");
            Assert(!BoardCleanupService.IsAllowedRegPath(
                       @"SYSTEM\CurrentControlSet\Enum\USB\VID_0586&PID_3440\6&35c831b8&0&0005", exp53),
                "v0.9.53: unrelated families (the 3G modem ports) stay out of scope");

            var plan53 = BoardCleanupService.DeletePlan();
            Assert(plan53.Length == 3 && plan53[0] == "pnputil" && plan53[2] == "system-task",
                "v0.9.53: removal tries pnputil first and only then registry surgery");
            Assert(cl53.Contains("SeTakeOwnershipPrivilege") && cl53.Contains("SeRestorePrivilege")
                   && cl53.Contains("AdjustTokenPrivileges"),
                "v0.9.53: the elevated run enables the disabled privileges that caused the unauthorized-operation failures");
            Assert(cl53.Contains("DeleteViaSystemTask") && cl53.Contains("/ru SYSTEM"),
                "v0.9.53: the last resort runs reg.exe as SYSTEM, the owner of the Enum-USB subtree");
            Assert(cl53.Contains("deletedPorts") && cl53.Contains("ClearArbiterBits(deletedPorts"),
                "v0.9.53: only ports that really went away get their arbiter bit freed");
            Assert(win53.Contains("BoardCleanupService.ExpandFamilies"),
                "v0.9.53: the dry-run list uses the same allow-list as the elevated run, so it cannot over-promise");
            Assert(!vm53.Contains("IsBusyRunning") && vm53.Contains("ShouldAutoConnect(idle, IsRunning"),
                "v0.9.53: hot-plug auto-connect uses the real busy flag (build fix)");
        }


        // ── v0.9.55: researched device defaults auto-filled into both steps, editable step-2
        // board-spec form restored, trimmed device list, attached-only port scan ──
        Console.WriteLine("--- Step 54: device defaults + editable board specs + attached-only scan ---");
        {
            var hx54 = V27ReadSrc(Path.Combine("Services", "BoardHexService.cs"));
            var ck54 = V27ReadSrc(Path.Combine("Services", "BoardCheckupService.cs"));
            var x54 = V27ReadSrc(Path.Combine("Views", "BoardPrepWindow.xaml"));
            var c54 = V27ReadSrc(Path.Combine("Views", "BoardPrepWindow.xaml.cs"));

            var gone = new[] { "\"microbit\"", "\"microbit2\"", "\"calliope\"", "\"picoedu\"", "\"circuitplay\"", "\"esp32\"" };
            Assert(gone.All(k => !hx54.Contains(k)),
                "v0.9.55: micro:bit, Calliope, Adafruit, ESP32-S2 and Raspberry Pi are gone from the device list");
            Assert(!ck54.Contains("(0x0D28,") && !ck54.Contains("(0x2E8A,") && !ck54.Contains("(0x239A,")
                   && !ck54.Contains("(0x303A, 0x4001)"),
                "v0.9.55: checkup no longer classifies the removed families");
            Assert(BoardHexService.DeviceModes.Length == 26
                   && BoardHexService.DeviceModes.Any(m => m.Key == "stm32")
                   && BoardHexService.DeviceModes.Any(m => m.Key == "legospike"),
                "v0.9.55: the six board identities remain alongside the 20 keyboards");

            foreach (var m in BoardHexService.DeviceModes)
            {
                var dd = BoardHexService.DefaultsFor(m.Key);
                Assert(dd.BoardId.Length > 0 && dd.BoardName.Length > 0 && dd.BootVid.StartsWith("0x")
                       && dd.BootPid.StartsWith("0x") && dd.AppPid != dd.BootPid
                       && dd.Product.Length > 0 && dd.Manufacturer.Length > 0,
                    $"v0.9.55: {m.Key} ships a complete default board spec (nothing left blank)");
            }

            var mc = BoardHexService.DefaultsFor("microchip");
            Assert(mc.BoardId == "microchip" && mc.BootVid == "0x04D8" && mc.BootPid == "0x000A"
                   && mc.AppPid == "0x000B" && mc.Product == "CDC RS-232 Emulation Demo"
                   && mc.Manufacturer == "Microchip",
                "v0.9.55: the Microchip defaults match the reference tool's board-spec card");
            var stm = BoardHexService.DefaultsFor("stm32");
            Assert(stm.BootVid == "0x0483" && stm.BootPid == "0x5740" && stm.AppPid == "0x5741"
                   && stm.Product == "STM32 Virtual COM Port" && stm.Manufacturer == "STMicroelectronics",
                "v0.9.55: the STM32 defaults are the real USB strings of that device");
            var dflt = BoardHexService.DefaultsFor("none");
            Assert(dflt.BoardId == "ams" && dflt.BoardName == "Classroom Studio Board",
                "v0.9.55: the AMS preset keeps the ams board id and Classroom Studio Board name");
            Assert(BoardHexService.SanitizeBoardId("My Board!") == "myboard"
                   && BoardHexService.SanitizeBoardId("") == "ams",
                "v0.9.55: board ids are sanitised to lowercase Arduino ids");

            var boxes54 = new[] { "TxtBoardId", "TxtBoardName", "TxtBootVid", "TxtBootPid",
                                  "TxtAppPid", "TxtBoardProduct", "TxtBoardManuf" };
            Assert(boxes54.All(b => x54.Contains("x:Name=\"" + b + "\"")),
                "v0.9.55: step 2 has the editable board-spec form back (id, name, VID, PIDs, product, maker)");
            Assert(x54.Contains("TextChanged=\"BoardSpec_Changed\"") && x54.Contains("BtnSpecsReset")
                   && x54.Contains("BtnSpecsRefresh"),
                "v0.9.55: editing a board-spec field refreshes the preview, and defaults can be restored");
            Assert(x54.Contains("قابل ویرایش") && x54.Contains("PID اپلیکیشن = PID بوت‌لودر + ۱"),
                "v0.9.55: the form explains the auto-fill and the application-PID rule in Persian");
            Assert(c54.Contains("FillIdentityDefaults") && c54.Contains("FillBoardSpecFields")
                   && c54.Contains("BoardSpecsFromUi"),
                "v0.9.55: selecting a device fills step 1 and step 2 with that device's defaults");
            Assert(c54.Contains("BuildBoardBlock(boardId: s.BoardId, name: s.BoardName")
                   && c54.Contains("InstallBoardBlock(path, block, s.BoardId)")
                   && c54.Contains("UninstallBoardBlock(path, boardId)"),
                "v0.9.55: the preview, the install and the uninstall all use the edited board specs");
            Assert(c54.Contains("_syncingSpecs") && c54.Contains("if (_loading || _syncingSpecs) return;"),
                "v0.9.55: auto-fill never fights the user's own typing");

            var rows54 = new List<BoardCheckupService.PortInfo>
            {
                new("COM17", "USB Serial Device", 0x04D8, 0x000B, "s1", "Microchip CDC Demo", "اپلیکیشن", false, false, "reg1"),
                new("COM18", "ZyXEL 3G Modem", 0x0586, 0x3440, "s2", null, null, false, true, "reg2"),
                new("COM19", "ZyXEL 3G Modem", 0x0586, 0x3440, "s3", null, null, false, true, "reg3"),
            };
            Assert(BoardCheckupService.VisibleRows(rows54, false).Count == 1
                   && BoardCheckupService.VisibleRows(rows54, false)[0].Device == "COM17",
                "v0.9.55: the port scan lists attached ports only, like the reference tool");
            Assert(BoardCheckupService.VisibleRows(rows54, true).Count == 3,
                "v0.9.55: history rows are still available behind the switch");
            var line54 = BoardCheckupService.ScanSummaryLine(rows54, false);
            Assert(line54.Contains("1") && line54.Contains("2")
                   && BoardCheckupService.ScanSummaryLine(rows54, true).Contains("2"),
                "v0.9.55: the scan log line counts attached ports and hidden history separately");
            Assert(x54.Contains("ChkShowHistory") && c54.Contains("ChkShowHistory_Changed")
                   && c54.Contains("RenderPortRows"),
                "v0.9.55: the checkup tab has a history switch and re-renders without a new scan");

            var csp54 = V27ReadSrc("Ams.UI.csproj");
            var vm54 = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
            var exp54 = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
            Assert(csp54.Contains("<Version>0.9.67</Version>")
                   && vm54.Contains("Classroom Studio v0.9.67")
                   && exp54.Contains("BundleVersion = \"0.9.64f\""),
                "v0.9.55: version pins for this release (csproj + banner + bundle)");
        }

        // ═══ step 55 ─ v0.9.55: keyboard identities, scrollbar spacing, tooltip vs submenu,
        // docked settings panel, renameable container heads, bare next/else markers ═══
        {
            var hex55 = V27ReadSrc(Path.Combine("Services", "BoardHexService.cs"));
            var chk55 = V27ReadSrc(Path.Combine("Services", "BoardCheckupService.cs"));

            var kb55 = new[]
            {
                ("g413tklse", 0x046D, 0xC33A), ("g413se", 0x046D, 0xC33C),
                ("gproxtklrapid", 0x046D, 0xC35E), ("blackwidowte", 0x1532, 0x011C),
                ("blackwidowxte", 0x1532, 0x021B), ("celeritas2", 0x1AF3, 0x0025),
                ("mx83tkl", 0x046A, 0x00B1), ("alloyorigins", 0x0951, 0x16E5),
                ("alloyorigins60", 0x0951, 0x16E9), ("alloyorigins65", 0x0951, 0x16EB),
                ("duckyone2mini", 0x04D9, 0x0348), ("duckyone2promini", 0x04D9, 0x0356),
                ("apexprotkl", 0x1038, 0x1614), ("apexpromini", 0x1038, 0x1646),
                ("keychronk8", 0x3434, 0x0180), ("das5qs2", 0x24F0, 0x2038),
                ("zmk650wp", 0x258A, 0x0006), ("vanguardpro96", 0x1B1C, 0x1BC4),
                ("shikarik515", 0x0C45, 0x7A0C), ("gomk87rs", 0x258A, 0x010C),
            };

            var missingModes = kb55.Where(k => BoardHexService.DeviceModes.All(m => m.Key != k.Item1)).ToList();
            Assert(missingModes.Count == 0,
                "v0.9.55: all twenty keyboard identities are selectable device modes");

            Assert(kb55.All(k =>
                {
                    var m = BoardHexService.ModeFor(k.Item1);
                    return m.Key == k.Item1 && m.Vid == k.Item2 && m.Pid == k.Item3 && m.ClassType == 0x02;
                }),
                "v0.9.55: every keyboard preset keeps its researched VID/PID and stays CDC (class 0x02)");

            Assert(kb55.All(k =>
                {
                    var d = BoardHexService.DefaultsFor(k.Item1);
                    return d.BootVid == "0x" + k.Item2.ToString("X4") && d.BootPid == "0x" + k.Item3.ToString("X4") && d.AppPid == "0x" + (k.Item3 + 1).ToString("X4")
                           && d.Product.Length > 0 && d.Manufacturer.Length > 0;
                }),
                "v0.9.55: selecting a keyboard prefills the board-spec defaults (product + manufacturer)");

            Assert(kb55.All(k => BoardCheckupService.ClassifyPort(k.Item2, k.Item3).Name is not null
                                 && BoardCheckupService.ClassifyPort(k.Item2, k.Item3 + 1).Name is not null),
                "v0.9.55: the checkup tab recognises both the bootloader and the application PID");

            Assert(BoardHexService.DeviceModes.Select(m => m.Key).Distinct().Count() == BoardHexService.DeviceModes.Length,
                "v0.9.55: no duplicate device-mode keys after the twenty additions");

            Assert(hex55.Contains("twenty researched macro-less keyboards")
                   && chk55.Contains("v0.9.55 - keyboard identities"),
                "v0.9.55: the new identity tables are documented in place");

            // scrollbar no longer touches the Persian text
            var sd55 = V27ReadSrc(Path.Combine("Views", "StepDialog.xaml"));
            var sp55 = V27ReadSrc(Path.Combine("Views", "SearchPictureDialog.xaml"));
            var bp55 = V27ReadSrc(Path.Combine("Views", "BoardPrepWindow.xaml"));
            Assert(sd55.Contains("MaxHeight=\"480\" VerticalScrollBarVisibility=\"Auto\" Padding=\"14,0,14,0\"")
                   && sp55.Contains("VerticalScrollBarVisibility=\"Auto\" Padding=\"14,0,14,0\"")
                   && System.Text.RegularExpressions.Regex.Matches(bp55, "Padding=\"14,0,14,0\"").Count >= 3,
                "v0.9.55: scrolled panes keep a 14px inset so the scrollbar never sits on the text");

            // rail tooltip must not paint over the hover submenu
            var mwx55 = V27ReadSrc("MainWindow.xaml");
            var mwc55 = V27ReadSrc("MainWindow.xaml.cs");
            Assert(mwx55.Contains("ToolTipService.Placement\" Value=\"Bottom")
                   && mwx55.Contains("ToolTipService.VerticalOffset\" Value=\"8")
                   && mwc55.Contains("ToolTipService.SetIsEnabled(host, false)")
                   && mwc55.Contains("ToolTipService.SetIsEnabled(_openRailHost, true)"),
                "v0.9.55: the hover hint drops below the icon and switches off while its submenu is open");

            // settings hosted inside the main window
            var od55 = V27ReadSrc(Path.Combine("Views", "OptionsDialog.xaml.cs"));
            var odx55 = V27ReadSrc(Path.Combine("Views", "OptionsDialog.xaml"));
            var vm55 = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
            Assert(mwx55.Contains("x:Name=\"SettingsOverlay\"") && mwx55.Contains("x:Name=\"SettingsHost\"")
                   && mwc55.Contains("public void ShowSettingsPanel(") && mwc55.Contains("public void HideSettingsPanel()")
                   && od55.Contains("public System.Windows.UIElement EmbedContent()")
                   && od55.Contains("public event System.EventHandler<bool>? Completed")
                   && !od55.Contains("DialogResult = true;")
                   && odx55.Contains("Click=\"Cancel_Click\"")
                   && vm55.Contains("mw.ShowSettingsPanel(dlg.EmbedContent())"),
                "v0.9.55: the settings tab and all its sections live inside the main window, not a separate one");

            // renameable container heads keep their icon
            var sdefs55 = V27ReadSrc(Path.Combine("Models", "StepDefinitions.cs"));
            var fa55 = V27ReadSrc(Path.Combine("Models", "StepTextsFa.cs"));
            foreach (var ct in new[] { "forLoop", "parallelGroup", "randomPackage", "waitForSound", "waitForLight", "findImage" })
                Assert(StepDefinitions.Get(ct).Fields.Any(f => f.Key == "title")
                       && StepDefinitions.ContainerIcon(ct).Length > 0,
                    $"v0.9.55: {ct} can be renamed and owns a type icon");

            var pg55 = new StepNode { Type = "parallelGroup" };
            var plain55 = pg55.Summary;
            pg55.Props["title"] = "کلیک همزمان";
            pg55.RefreshSummary();
            Assert(plain55.Contains("Parallel Group")
                   && pg55.Summary.StartsWith(StepDefinitions.ContainerIcon("parallelGroup"))
                   && pg55.Summary.Contains("کلیک همزمان")
                   && pg55.Summary.Contains("step(s)"),
                "v0.9.55: a renamed head shows icon + custom title and keeps its informative tail");

            Assert(fa55.Contains("[\"title\"]"),
                "v0.9.55: the group-title field has a Persian label");

            // bare, lowercase structural markers that hug the vein
            var nextNode55 = new StepNode { Type = "comment" };
            nextNode55.Props["text"] = "Next";
            var elseNode55 = new StepNode { Type = "comment" };
            elseNode55.Props["text"] = "Else";
            var noteNode55 = new StepNode { Type = "comment" };
            noteNode55.Props["text"] = "یادداشت";
            Assert(nextNode55.Summary == "next" && elseNode55.Summary == "else"
                   && noteNode55.Summary.StartsWith("# "),
                "v0.9.55: markers render as bare lowercase next/else while real comments keep the # prefix");

            var markerRow55 = new FlatStepRow(nextNode55, "1.1", 1, System.Windows.Media.Brushes.Transparent, null);
            var plainRow55 = new FlatStepRow(noteNode55, "1.2", 1, System.Windows.Media.Brushes.Transparent, null);
            Assert(markerRow55.IsMarkerRow && markerRow55.SummaryInset.Left < 0
                   && !plainRow55.IsMarkerRow && plainRow55.SummaryInset.Left == 0
                   && mwx55.Contains("Margin=\"{Binding SummaryInset}\""),
                "v0.9.55: marker rows reclaim the toggle gutter and sit tighter against the red vein");

            var csp55 = V27ReadSrc("Ams.UI.csproj");
            var vmb55 = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
            var exp55 = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
            Assert(csp55.Contains("<Version>0.9.67</Version>")
                   && vmb55.Contains("Classroom Studio v0.9.67")
                   && exp55.Contains("BundleVersion = \"0.9.64f\""),
                "v0.9.55: version pins for this release (csproj + banner + bundle)");
        }

        // Modern exporter/runtime contracts are kept out of the Golden-100
        // contract. The full TestRunner still executes them when this guard is true.
        if (Environment.GetEnvironmentVariable("GOLDEN_100_ONLY") != "1")
        {
        // ── Step 56: v0.9.56 — UART arm moved from GP0/GP1 to GP16/GP17 (wiring v6) ──
        Console.WriteLine();
        Console.WriteLine("--- Step 56: v0.9.56 UART GP0/GP1 -> GP16/GP17 ---");
        var pfe56 = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
        var csproj56 = V27ReadSrc("Ams.UI.csproj");
        var exp56 = pfe56;
        Assert(pfe56.Contains("busio.UART(board.GP16, board.GP17, baudrate=ARM_BAUD, timeout=0.2)"),
            "v0.9.56: firmware uses GP16/GP17 for UART arm");
        Assert(!pfe56.Contains("board.GP0") && !pfe56.Contains("board.GP1,"),
            "v0.9.56: old GP0/GP1 UART pins are gone from firmware exporter");
        Assert(pfe56.Contains("GP16 = TX") && pfe56.Contains("GP17 = RX"),
            "v0.9.56: generated header comment mentions GP16 = TX and GP17 = RX");
        Assert(pfe56.Contains("busio.I2C(board.GP21, board.GP20)"),
            "v0.9.56: BH1750 I2C pins untouched (GP21/GP20)");
        Assert(PicoFirmwareExporter.BundleVersion == "0.9.64f",
            "v0.9.56: BundleVersion is 0.9.64f");
        var tmp56 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pfe56_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tmp56);
        try {
            PicoFirmwareExporter.Export(System.IO.Path.Combine(tmp56, "code.py"), new List<StepNode>(), Environment.MachineName);
            var code56 = System.IO.File.ReadAllText(System.IO.Path.Combine(tmp56, "code.py"));
            Assert(code56.Contains("board.GP16, board.GP17"),
                "v0.9.56: exported code.py carries GP16/GP17");
            Assert(!code56.Contains("board.GP0"),
                "v0.9.56: exported code.py does not contain board.GP0");
            var boot56 = System.IO.File.ReadAllText(System.IO.Path.Combine(tmp56, "boot.py"));
            Assert(boot56.Contains("usb_cdc.enable(console=True, data=True)"),
                "v0.9.56: boot.py still enables CDC console+data");
        } finally {
            if (System.IO.Directory.Exists(tmp56)) System.IO.Directory.Delete(tmp56, true);
        }
        Assert(csproj56.Contains("<Version>0.9.67</Version>"),
            "v0.9.56: csproj version is 0.9.67");


        // ── Step 57: v0.9.57 — portable / self-contained build ──
        Console.WriteLine();
        Console.WriteLine("--- Step 57: v0.9.58 portable/self-contained ---");
        // v0.9.57 — bridge files live in ams-shell/bridge/
        var _p57root = new System.IO.DirectoryInfo(AppContext.BaseDirectory).Parent?.Parent?.Parent?.Parent?.FullName ?? "";
        var p57bridge = File.ReadAllText(System.IO.Path.Combine(_p57root, "ams-shell", "bridge", "bridge.py"));
        var p57pp = V27ReadSrc(Path.Combine("Services", "PortablePaths.cs"));
        var p57doc = V27ReadSrc(Path.Combine("Services", "DocumentService.cs"));
        var p57pbb = V27ReadSrc(Path.Combine("Services", "PythonBoardBridge.cs"));
        var p57vm = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var p57csp = V27ReadSrc("Ams.UI.csproj");

        var _bdir = System.IO.Path.Combine(_p57root, "ams-shell", "bridge");
        Assert(File.Exists(Path.Combine(_bdir, "ams_serial.py"))
               && File.Exists(Path.Combine(_bdir, "ams_crypto.py"))
               && File.Exists(Path.Combine(_bdir, "serial", "__init__.py"))
               && File.Exists(Path.Combine(_bdir, "serial", "tools", "list_ports.py")),
            "v0.9.58: ams_serial.py, ams_crypto.py, and vendored pyserial bundled in bridge/");
        Assert(p57bridge.Contains("sys.path.insert(0, os.path.dirname"),
            "v0.9.58: bridge.py puts bundled stack first on sys.path");
        Assert(p57bridge.Contains("detect_board_port") && p57bridge.Contains("0x2E8A, 0x0005"),
            "v0.9.58: brain-first detect_board_port with Pico VID/PID");
        Assert(p57bridge.Contains("event") && p57bridge.Contains("stage"),
            "v0.9.58: bridge emits stage events");
        Assert(p57bridge.Contains("bridge_started") && p57bridge.Contains("stack_imported")
               && p57bridge.Contains("port_open") && p57bridge.Contains("hello_ok"),
            "v0.9.58: all four stage event names present");
        Assert(p57pp.Contains("FirstExistingDir") && p57pp.Contains("FirstExistingFile"),
            "v0.9.58: PortablePaths has FirstExistingDir and FirstExistingFile");
        Assert(!p57doc.Contains("wasteland"),
            "v0.9.58: no hardcoded wasteland path in DocumentService defaults");
        Assert(p57doc.Contains("Directory.Exists") && p57doc.Contains("SerialPort.GetPortNames"),
            "v0.9.58: AppSettings.Load sanitizes stale paths");
        Assert(p57vm.Contains("PortablePaths.FirstExistingDir"),
            "v0.9.58: CreateBridge prefers bundled bridge dir over settings");
        Assert(p57pbb.Contains("PortablePaths.FindPython()"),
            "v0.9.58: PythonBoardBridge uses PortablePaths.FindPython()");
        Assert(p57pbb.Contains("TimeSpan.FromSeconds(15)"),
            "v0.9.58: ConnectAsync has 15s timeout");
        Assert(p57csp.Contains("<Version>0.9.67</Version>"),
            "v0.9.58: csproj version is 0.9.67");
        // (a) csproj bundles the whole bridge/ folder with wildcard
        Assert(p57csp.Contains(@"bridge\**") && p57csp.Contains("CopyToOutputDirectory"),
            "v0.9.58: csproj uses bridge wildcard with CopyToOutputDirectory");
        // (b) v0.9.60: FIXED keypad contract — GP4 = Num Lock (start/stop), GP3 = Scroll Lock
        // (pause/resume), independent of the Options gestures (supersedes the v0.9.58d wiring).
        var p58exp = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
        var p58keys = PicoFirmwareExporter.HotkeyToVirtualKeys("Ctrl+Add");
        Assert(p58keys.SequenceEqual(new[] { 0xA2, 0x6B }),
            "v0.9.58d: Pico exporter converts configured modifier + numpad key");
        Assert(p58exp.Contains("digitalio.DigitalInOut(board.GP4)")
               && p58exp.Contains("digitalio.DigitalInOut(board.GP3)")
               && p58exp.Contains("Keycode.KEYPAD_NUMLOCK")
               && p58exp.Contains("Keycode.SCROLL_LOCK"),
            "v0.9.60: fixed keypad on GP4/GP3 emits Num Lock and Scroll Lock");
        var p58bridge = V27ReadSrc(Path.Combine("..", "..", "bridge", "bridge.py"));
        Assert(p58bridge.Contains(".split(\";\") if d.strip()"),
            "v0.9.58d: send_path parses semicolon-delimited delays, not individual characters");
        var p58hotkeys = V27ReadSrc(Path.Combine("Services", "GlobalHotkeyService.cs"));
        Assert(!p58hotkeys.Contains("hard_numlock") && !p58hotkeys.Contains("hard_scroll"),
            "v0.9.58d: fixed lock-key registrations and their startup warnings are gone");
        var p58xaml = V27ReadSrc("MainWindow.xaml");
        var p58cs = V27ReadSrc("MainWindow.xaml.cs");
        Assert(p58xaml.Contains("SerialLogList") && p58xaml.Contains("کپی کل لاگ")
               && p58cs.Contains("CopyAllLog_Click") && p58cs.Contains("Clipboard.SetText"),
            "v0.9.58d: serial log has selected/all clipboard copy actions");
        // custom GP6 buzzer: replaces only insertion UI; legacy playAudio remains loadable.
        var buzDef = StepDefinitions.Get("buzzer");
        var buzCustom = new Dictionary<string, object?> { ["preset"] = "custom", ["pattern"] = "900:150,80;1200:250" };
        Assert(buzDef.Label == "Buzzer Beep" && StepDefinitions.Get("playAudio").Label == "Play Audio",
            "custom buzzer: new action exists and legacy playAudio remains registered");
        Assert(StepDefinitions.BuildBuzzerCommands(buzCustom).SequenceEqual(new[] { "BEEP|900,150", "DLY|80", "BEEP|1200,250" }),
            "custom buzzer: custom sequence compiles to BEEP/DLY commands");
        bool badBuzzer = false;
        try { StepDefinitions.BuildBuzzerCommands(new Dictionary<string, object?> { ["preset"]="custom", ["pattern"]="25000:10" }); }
        catch (FormatException) { badBuzzer = true; }
        Assert(badBuzzer, "custom buzzer: out-of-range frequency is rejected");
        Assert(p58xaml.Contains("CommandParameter=\"buzzer\"") && !p58xaml.Contains("CommandParameter=\"playAudio\""),
            "custom buzzer: all insertion surfaces use buzzer instead of Play Audio");
        var buzFw = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
        Assert(buzFw.Contains("PWMOut(board.GP6") && !buzFw.Contains("PWMOut(board.GP5") && !buzFw.Contains("board.D9"),
            "custom buzzer: passive PWM is Pico GP6 only");

        // (c) meta guard: every version PIN in this file matches the current release.
        // Pin lines are the assertions that check the csproj Version tag, the app banner or
        // the Pico bundle version. Version strings inside test DATA (PONG replies like
        // "pico-light 0.9.44") and historical message labels ("v0.9.50: ...") are NOT pins:
        // the label form v0.9.N: is excluded explicitly. v0.9.58b — the first draft had a
        // raw newline inside the char literal (compile error) and counted data strings, so
        // it could never go green; the file path is now resolved by walking up from the
        // build output (CI runs `dotnet run` from the repo root, so the cwd differs).
        string srcRunner = "";
        for (DirectoryInfo? dirWalk = new DirectoryInfo(AppContext.BaseDirectory); dirWalk is not null && srcRunner.Length == 0; dirWalk = dirWalk.Parent)
        {
            foreach (var candPath in new[] { Path.Combine(dirWalk.FullName, "tests", "TestRunner.cs"), Path.Combine(dirWalk.FullName, "TestRunner.cs") })
                if (File.Exists(candPath)) { srcRunner = File.ReadAllText(candPath); break; }
        }
        Assert(srcRunner.Length > 1000, "v0.9.58: meta guard located TestRunner.cs on disk");
        var pinned = new List<int>();
        var pinnedBundle = new List<int>();   // bundle pins are tracked apart: the firmware template may stay on an older line
        foreach (var metaLine in srcRunner.Split('\n'))
        {
            if (!metaLine.Contains("<Version>0.9.") && !metaLine.Contains("Classroom Studio v0.9.") && !metaLine.Contains("BundleVersion")) continue;
            foreach (System.Text.RegularExpressions.Match metaMatch in System.Text.RegularExpressions.Regex.Matches(metaLine, @"0\.9\.(\d+)"))
            {
                bool isLabel = metaMatch.Index > 0 && metaLine[metaMatch.Index - 1] == 'v'
                               && metaMatch.Index + metaMatch.Length < metaLine.Length && metaLine[metaMatch.Index + metaMatch.Length] == ':';
                if (!isLabel) (metaLine.Contains("BundleVersion") ? pinnedBundle : pinned).Add(int.Parse(metaMatch.Groups[1].Value));
            }
        }
        var curMinor = 67;
        var curBundleMinor = 64;   // the firmware template is unchanged in this release, so the bundle stays on its older line
        var pinnedText = string.Join(", ", pinned.Distinct().OrderBy(n => n));
        Assert(pinned.Count > 0 && pinned.Distinct().All(n => n == curMinor),
            $"v0.9.58: all app version pins match current release 0.9.{curMinor} (found: {pinnedText})");
        var pinnedBundleText = string.Join(", ", pinnedBundle.Distinct().OrderBy(n => n));
        Assert(pinnedBundle.Count > 0 && pinnedBundle.Distinct().All(n => n == curBundleMinor),
            $"v0.9.58: all bundle pins stay on the untouched firmware line 0.9.{curBundleMinor}b (found: {pinnedBundleText})");

        // ── Step 58: v0.9.60 — hardened firmware consolidation + UTF-8 transport + log multi-select ──
        Console.WriteLine();
        Console.WriteLine("--- Step 58: v0.9.60 firmware consolidation + transport hardening ---");
        var exp60 = V27ReadSrc(Path.Combine("Services", "PicoFirmwareExporter.cs"));
        var brg60 = V27ReadSrc(Path.Combine("..", "..", "bridge", "bridge.py"));
        var pbb60 = V27ReadSrc(Path.Combine("Services", "PythonBoardBridge.cs"));
        var mwx60 = V27ReadSrc("MainWindow.xaml");
        var sd60 = V27ReadSrc(Path.Combine("Views", "StepDialog.xaml.cs"));
        var vm60 = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
        var csp60 = V27ReadSrc("Ams.UI.csproj");

        Assert(PicoFirmwareExporter.BundleVersion == "0.9.64f"
               && csp60.Contains("<Version>0.9.67</Version>")
               && vm60.Contains("Classroom Studio v0.9.67")
               && exp60.Contains("BundleVersion = \"0.9.64f\""),
            "v0.9.60: version pins (firmware bundle, csproj, app banner)");

        // firmware contract: optional sensor / never-die / byte buffer / arm pump / fixed keypad
        Assert(exp60.Contains("ERR|NOSENSOR"),
            "v0.9.60: WLUX/TRGLUX/LCAL answer ERR|NOSENSOR when the BH1750 is unplugged");
        Assert(exp60.Contains("AUTOSTART = False"),
            "v0.9.60: the standalone engine never autoruns on boot (AUTOSTART=False)");
        Assert(exp60.Contains("digitalio.DigitalInOut(board.GP4)")
               && exp60.Contains("digitalio.DigitalInOut(board.GP3)")
               && exp60.Contains("Keycode.KEYPAD_NUMLOCK")
               && exp60.Contains("Keycode.SCROLL_LOCK"),
            "v0.9.60: fixed keypad GP4 = Num Lock start/stop, GP3 = Scroll Lock pause/resume");
        Assert(exp60.Contains("buffer = bytearray()") && !exp60.Contains("buffer += serial"),
            "v0.9.60: serial RX uses a bounded byte buffer, not string concatenation");
        Assert(exp60.Contains("def pump_arm()") && exp60.Contains("def forward_fast("),
            "v0.9.60: permanent arm pump + fire-and-ack mouse path");
        Assert(!exp60.Contains("KBD_ON_ARM =")
               && exp60.Contains("line.startswith(\"KBDARM|\")")
               && exp60.Contains("line.startswith(\"KBDPICO|\")"),
            "v0.9.60: keyboard always on the Pico; both legacy envelopes are consumed locally");

        var code60 = PicoFirmwareExporter.BuildCodePy(new List<PicoFirmwareExporter.LightState>(), "M");
        Assert(code60.Contains("AUTOSTART = False") && code60.Contains("pico-light 0.9.64f")
               && !code60.Contains("__KBD_ON_ARM__") && !code60.Contains("__RUNSTOP_HOTKEY__")
               && !code60.Contains("__PAUSERESUME_HOTKEY__") && !code60.Contains("__LOOP_MODE__"),
            "v0.9.60: exported code.py is fully baked with the fixed control contract");

        // UTF-8 transport: the bridge must never die while REPORTING an error (the cp1252 crash)
        Assert(brg60.Contains("reconfigure(encoding=\"utf-8\", errors=\"replace\")")
               && brg60.Contains("ensure_ascii=True"),
            "v0.9.60: bridge stdio is forced to UTF-8 with an ASCII-safe emit fallback");
        Assert(pbb60.Contains("StandardOutputEncoding = Encoding.UTF8")
               && pbb60.Contains("StandardErrorEncoding = Encoding.UTF8")
               && pbb60.Contains("PYTHONIOENCODING"),
            "v0.9.60: the C# side reads the bridge as UTF-8 too");

        // serial log: multi-select so copy-selected-lines is actually usable
        Assert(mwx60.Contains("x:Name=\"SerialLogList\"")
               && System.Text.RegularExpressions.Regex.IsMatch(mwx60,
                    "x:Name=\"SerialLogList\"[^>]*SelectionMode=\"Extended\"",
                    System.Text.RegularExpressions.RegexOptions.Singleline),
            "v0.9.60: the serial log allows Ctrl/Shift multi-selection for copy-selected");

        // fractional stableSec end-to-end
        var wl60 = new StepNode
        {
            Type = "waitForLight",
            Props = new Dictionary<string, object?> { { "stableSec", 0.5 } },
        };
        var wl60cmds = StepDefinitions.GetCommands(wl60);
        Assert(wl60cmds.Count == 1 && wl60cmds[0].StartsWith("WLUX|") && wl60cmds[0].Split(',')[2] == "500",
            $"v0.9.60: stableSec 0.5 becomes 500ms on the wire (got: {string.Join(", ", wl60cmds)})");
        wl60.RefreshSummary();
        Assert(wl60.Summary.Contains("0.5s"),
            $"v0.9.60: the row summary shows the fractional stabilize time (got: {wl60.Summary})");
        var wl60int = new StepNode
        {
            Type = "waitForLight",
            Props = new Dictionary<string, object?> { { "stableSec", 2 } },
        };
        Assert(StepDefinitions.GetCommands(wl60int)[0].Split(',')[2] == "2000",
            "v0.9.60: legacy integer stableSec plans still produce the same wire command");
        Assert(sd60.Contains("TryParseFloat") && sd60.Contains("CultureInfo.InvariantCulture"),
            "v0.9.60: the step dialog accepts 0.5 regardless of the Windows display language");

        // ── Step 59: v0.9.65 — portable plan exporter (PLAN|1 gen-1 contract, firmware 0.9.64f) ──
        Console.WriteLine();
        // ── Step 59: v0.9.65 — portable plan exporter (PLAN|2 gen-1 contract, firmware 0.9.64b) ──
        Console.WriteLine();
        // ── Step 59: v0.9.65 — portable plan exporter (PLAN|2 gen-1 contract, firmware 0.9.64b) ──
        Console.WriteLine();
        // PLAN2_PARITY_TESTS
        // ── Step 59: v0.9.65 — portable plan exporter (PLAN|2 gen-1 contract, firmware 0.9.64b) ──
        Console.WriteLine();
        Console.WriteLine("--- Step 59: v0.9.65 plan exporter (PLAN|2) ---");
        {
            static string PexNormEol(string s) => s.Replace("\r\n", "\n").TrimEnd() + "\n";
            static string? PexReadRepo(string rel)
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
                {
                    var cand = Path.Combine(dir.FullName, rel);
                    if (File.Exists(cand)) return File.ReadAllText(cand);
                }
                return null;
            }
            static StepNode PexStep(string type, Dictionary<string, object?>? props = null,
                                    int delay = 0, int delayMax = 0, bool disabled = false)
                => new() { Type = type, Props = props ?? new Dictionary<string, object?>(),
                           Delay = delay, DelayMax = delayMax, IsDisabled = disabled };
            static List<string> PexOps(string text)
                => text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();

            var pexSettings = new AppSettings();

            // header + settings-driven SCREEN/SPEED
            var pexEmpty = PlanExporter.Compile(new List<StepNode>(), pexSettings, 1920, 1080, "fixture.amsj", "TESTPC");
            Assert(pexEmpty.Text.StartsWith("PLAN|2\n") && pexEmpty.Text.Contains("\nSCREEN|1920,1080\n")
                   && pexEmpty.Text.Contains("\nSPEED|300,2000\n"),
                "v0.9.65: plan header is PLAN|2 with SCREEN and SPEED from settings");

            // randomMousePosition: the exact golden line (proven by sim_plan_export.py on the real engine)
            var pexRand = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("randomMousePosition", new Dictionary<string, object?>
                {
                    ["x"] = 1301, ["y"] = 0, ["w"] = 378, ["h"] = 1049,
                    ["pauseBeforeMin"] = 120, ["pauseBeforeMax"] = 450,
                    ["pauseAfterMin"] = 150, ["pauseAfterMax"] = 600,
                    ["midPauseChance"] = 12, ["midPauseMin"] = 100, ["midPauseMax"] = 500,
                    ["idleEveryMin"] = 5, ["idleEveryMax"] = 12, ["idlePauseMin"] = 800, ["idlePauseMax"] = 3000,
                    ["overshootChance"] = 25, ["curveMinPct"] = 22, ["curveMaxPct"] = 155,
                    ["moveTimeMin"] = 0, ["moveTimeMax"] = 333,
                }, delay: 55, delayMax: 55),
            }, pexSettings, 1920, 1080, "fixture.amsj", "TESTPC");
            Assert(pexRand.Text.Contains("RMOUSE|region=1301,0,378,1049|before=120,450|after=150,600|curve=22,155|mid=12:100,500|over=25|mt=0,333|idle=5,12:800,3000\n"),
                "v0.9.65: randomMousePosition emits the exact golden RMOUSE line");
            Assert(pexRand.Text.TrimEnd().EndsWith("DELAY|55,55"),
                "v0.9.65: per-step delay-after lands after the op");
            Assert(pexRand.Counts.Contains("RMOUSE x1") && pexRand.Disabled.Count == 0,
                "v0.9.65: emission counts are reported");

            // mouseMove -> deterministic 1x1 RMOUSE region; idle explicitly OFF (gen-1 defaults it ON)
            var pexMove = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("mouseMove", new Dictionary<string, object?> { ["x"] = 700, ["y"] = 400, ["human"] = true }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(pexMove.Text.Contains("MOVETO|x=700|y=400|before=60,220|after=80,280|curve=20,40|mid=6:80,250|over=12|idle=1,1:0,0\n"),
                "v0.9.66: mouseMove emits native PLAN|2 MOVETO");
            Assert(!pexMove.Text.Contains("idle=5,12"),
                "v0.9.65: the engine's built-in idle default never leaks into a point move");
            var pexNoHuman = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("mouseMove", new Dictionary<string, object?> { ["x"] = 5, ["y"] = 6, ["human"] = false }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(pexNoHuman.Text.Contains("MOVETO|x=5|y=6|human=0\n"),
                "v0.9.66: human=false emits native non-human MOVETO");

            var hand = new HandMovementSample.Sample(10_000, new System.Drawing.Point(100, 100),
                new System.Drawing.Point(130, 106), new[]
                {
                    new HandMovementSample.Segment(100, 10, 2),
                    new HandMovementSample.Segment(120, 12, 3),
                    new HandMovementSample.Segment(140, 8, 1),
                });
            var encodedHand = HandMovementSample.Encode(hand);
            Assert(HandMovementSample.TryDecode(encodedHand, out var decodedHand)
                   && decodedHand.Segments.Count == 3 && decodedHand.End.X == 130
                   && HandMovementSample.Displacement(decodedHand) == new System.Drawing.Point(30, 6),
                "v0.9.68: ten-second hand sample codec round-trips without keyboard/text data");
            Assert(HandMovementSample.CaptureIntervalMs == 8
                   && HandMovementSample.ReplaySegmentLimit == 1280,
                "v0.9.69: hand capture retains 8ms detail without exceeding the safe UART replay rate");
            Assert(StepDefinitions.Get("mouseMove").Label == "Mouse Movement"
                   && StepDefinitions.Get("mouseMove").Fields.Any(f => f.Key == "moveMode")
                   && StepDefinitions.Get("mouseMove").Fields.Any(f => f.Key == "handSample"),
                "v0.9.69: mouse movement exposes fixed and relative hand-gesture modes");
            var pexHand = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("mouseMove", new Dictionary<string, object?>
                { ["x"] = 130, ["y"] = 106, ["moveMode"] = "handSample", ["handSample"] = encodedHand }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(pexHand.Text.Contains("HANDPATH|100,10,2;120,12,3;140,8,1\n")
                   && !pexHand.Text.Contains("RAW|MMOVE|") && !pexHand.Text.Contains("MOVETO|"),
                "v0.9.69: sampled hand motion exports as a compact native relative HID path");
            var handNode = PexStep("mouseMove", new Dictionary<string, object?>
                { ["x"] = 999, ["y"] = 777, ["moveMode"] = "handSample", ["handSample"] = encodedHand });
            Assert(StepDefinitions.Get("mouseMove").Summarize(handNode).Contains("Δ(30, 6)")
                   && !StepDefinitions.Get("mouseMove").Summarize(handNode).Contains("999"),
                "v0.9.69: hand gesture summary reports relative displacement, never an absolute destination");
            var runEngineSource = V27ReadSrc(Path.Combine("Services", "RunEngine.cs"));
            Assert(runEngineSource.Contains("MMOVE|{segment.Dx},{segment.Dy},rel,2")
                   && !runEngineSource.Contains("ReplayHandMovementAsync(handSample, PropEx.GetInt"),
                "v0.9.69: desktop replay uses the same relative HID contract as portable export");
            var cadenceSample = new HandMovementSample.Sample(10_000,
                new System.Drawing.Point(0, 0), new System.Drawing.Point(35, 0), new[]
                {
                    new HandMovementSample.Segment(10, 5, 0),
                    new HandMovementSample.Segment(10, 6, 0),
                    new HandMovementSample.Segment(10, 7, 0),
                    new HandMovementSample.Segment(10, 8, 0),
                    new HandMovementSample.Segment(10, 9, 0),
                });
            var encodedCadence = HandMovementSample.Encode(cadenceSample);
            Assert(HandMovementSample.TryGetSpeedRange(cadenceSample, out var cadenceMin, out var cadenceMax)
                   && cadenceMin == 500 && cadenceMax == 800,
                "sampled Random Mouse cadence uses robust 20th/80th percentile speeds");
            Assert(StepDefinitions.Get("randomMousePosition").Fields.Any(f => f.Key == "handSample"),
                "Random Mouse Position exposes a stored hand-sample profile");
            var sampledRandom = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("randomMousePosition", new Dictionary<string, object?>
                {
                    ["x"] = 10, ["y"] = 20, ["w"] = 300, ["h"] = 200,
                    ["handSample"] = encodedCadence,
                }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(sampledRandom.Text.Contains("|speed=500,800|"),
                "sampled Random Mouse exports the user's measured speed band");
            var sampledRandomWithLegacyDuration = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("randomMousePosition", new Dictionary<string, object?>
                {
                    ["x"] = 10, ["y"] = 20, ["w"] = 300, ["h"] = 200,
                    ["handSample"] = encodedCadence,
                    ["moveTimeMin"] = 16, ["moveTimeMax"] = 159,
                }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(sampledRandomWithLegacyDuration.Text.Contains("|speed=500,800|")
                   && !sampledRandomWithLegacyDuration.Text.Contains("|mt="),
                "fresh Random Mouse hand sample overrides stale explicit duration fields");
            var sampledCfg = HumanMouse.Config.FromProps(new Dictionary<string, object?>
            {
                ["handSample"] = encodedCadence,
                ["moveTimeMin"] = 16, ["moveTimeMax"] = 159,
            }, 300, 2000);
            Assert(sampledCfg.SpeedMinPxPerSec == 500 && sampledCfg.SpeedMaxPxPerSec == 800
                   && sampledCfg.MoveTimeMinMs == 0 && sampledCfg.MoveTimeMaxMs == 0,
                "desktop Random Mouse uses sampled cadence as its single timing source");
            var stepDialogSource = V27ReadSrc(Path.Combine("Views", "StepDialog.xaml.cs"));
            var mainVmMouseSource = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
            Assert(stepDialogSource.Contains("randomMousePosition\" && f.Key == \"h\"")
                   && stepDialogSource.Contains("انتخاب مختصات روی صفحه")
                   && mainVmMouseSource.Contains("PickPointOnScreen")
                   && V27ReadSrc(Path.Combine("Views", "PointPickerWindow.xaml")).Contains("PointPickerWindow")
                   && stepDialogSource.Contains("مقصد و هندسه همچنان تصادفی‌اند"),
                "Random Mouse has sampling UI and Move to Position has a one-click point picker");

            // CLICK with swapped hold bounds
            var pexClick = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("mouseClick", new Dictionary<string, object?>
                { ["button"] = "right", ["action"] = "double", ["holdMin"] = 90, ["holdMax"] = 30 }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(pexClick.Text.Contains("CLICK|btn=right|n=2|hold=30,90\n"),
                "v0.9.65: click line with swap-normalized hold");

            // TYPE %-encoding + word pauses + settings cadence defaults
            var pexType = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("typeText", new Dictionary<string, object?> { ["text"] = "Hello 50%|world\nline2", ["hmin"] = 90, ["hmax"] = 180 }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(pexType.Text.Contains("TYPE|text=Hello 50%25%7Cworld%0Aline2|h=90,180\n"),
                "v0.9.65: TYPE percent-encoding (% -> %25, | -> %7C, newline -> %0A)");
            var pexWords = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("typeText", new Dictionary<string, object?> { ["text"] = "hi there you", ["wmin"] = 100, ["wmax"] = 180, ["wordPauseChance"] = 100 }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(pexWords.Text.Contains("TYPE|text=hi there you|h=80,220|w=100,180|wp=100\n"),
                "v0.9.65: word pauses + typing cadence defaults from settings");

            // PLAN|2 parity matrix: all portable actions emit; only explicit hardware/clipboard blockers remain.
            var pexParity = new List<StepNode>
            {
                PexStep("waitForSound", new Dictionary<string, object?> { ["threshold"] = 91, ["minDurationMs"] = 70, ["timeoutMs"] = 8000 }),
                PexStep("keystroke", new Dictionary<string, object?> { ["modCtrl"] = true, ["key"] = "A" }),
                PexStep("keyDown", new Dictionary<string, object?> { ["key"] = "SHIFT" }),
                PexStep("keyUp", new Dictionary<string, object?> { ["key"] = "SHIFT" }),
                PexStep("mouseScroll", new Dictionary<string, object?> { ["delta"] = -3 }),
                PexStep("label", new Dictionary<string, object?> { ["label"] = "again" }),
                PexStep("gotoLabel", new Dictionary<string, object?> { ["label"] = "again" }),
                PexStep("rawCommand", new Dictionary<string, object?> { ["cmd"] = "PING" }),
                PexStep("runExe", new Dictionary<string, object?> { ["path"] = "C:\\Tools\\demo.exe" }),
                PexStep("openFile", new Dictionary<string, object?> { ["path"] = "C:\\Data\\readme.txt" }),
                PexStep("playAudio", new Dictionary<string, object?> { ["path"] = "C:\\Data\\tone.wav", ["mode"] = "playerMacro" }),
            };
            var pexPkg=PexStep("randomPackage",new Dictionary<string,object?>{{"mode","shuffleAll"}});
            pexPkg.Children.Add(PexStep("delay",new Dictionary<string,object?>{{"minMs",1},{"maxMs",1}}));
            pexPkg.Children.Add(PexStep("mouseClick")); pexParity.Add(pexPkg);
            var pexPar=PexStep("parallelGroup"); pexPar.Children.Add(PexStep("mouseClick"));
            pexPar.Children.Add(PexStep("mouseScroll",new Dictionary<string,object?>{{"delta",1}})); pexParity.Add(pexPar);
            var pexParityText=PlanExporter.Compile(pexParity,pexSettings,1920,1080,"f","T").Text;
            Assert(pexParityText.Contains("WSNDP|1,") && pexParityText.Contains(",91,70,8000"),
                "Build 74: C# parity emits bound WSNDP for Wait For Sound");
            foreach(var op in new[]{"KEY|combo=162+65","KDOWN|160","KUP|160","WHEEL|-3","LABEL|again","GOTO|again","RAW|PING","RPKG|all,1,2","PKGITEM","ENDPKG","PGROUP","PARITEM","ENDPAR"})
                Assert(pexParityText.Contains(op),"v0.9.66: C# parity emits "+op);
            Assert(pexParityText.Split('\n').Count(x=>x=="KEY|combo=91+82|hold=40,90")==3,
                "v0.9.66: runExe/openFile/playAudio emit Win+R macros");

            // Build 90: buzzer is a Pico-local cooperative op. It must remain
            // exportable after a plain Wait For Sound inside looped parallel branches.
            var soundParallel = PexStep("parallelGroup");
            foreach (var id in new[] { 1, 2 })
            {
                var branch = PexStep("forLoop", new Dictionary<string, object?>
                {
                    ["mode"] = "count", ["count"] = 1,
                });
                branch.Children.Add(PexStep("waitForSound", new Dictionary<string, object?>
                {
                    ["calibrationId"] = id, ["threshold"] = id == 1 ? 130 : 30,
                    ["minDurationMs"] = 60, ["timeoutMs"] = 20000,
                    ["armed"] = false, ["insertIfElse"] = false,
                }));
                branch.Children.Add(PexStep("buzzer", new Dictionary<string, object?>
                {
                    ["preset"] = id == 1 ? "warning" : "success",
                }));
                soundParallel.Children.Add(branch);
            }
            var soundParallelText = PlanExporter.Compile(
                new List<StepNode> { soundParallel }, pexSettings, 1920, 1080, "s1.amsj", "T").Text;
            Assert(soundParallelText.Contains("PGROUP\nLOOP|1\nWSNDP|1,")
                   && soundParallelText.Contains("\nPARITEM\nLOOP|1\nWSNDP|2,")
                   && soundParallelText.Contains("BEEP|700,180")
                   && soundParallelText.Contains("BEEP|900,120"),
                "Build 90: looped parallel Wait For Sound + Buzzer exports cooperatively");

            // findImage remains an explicit blocking error.
            try { PlanExporter.Compile(new List<StepNode>{PexStep("findImage")},pexSettings,1920,1080,"f","T"); Assert(false,"v0.9.66: findImage must block"); }
            catch(PlanExporter.PlanBlockedException bx){Assert(bx.Errors.Any(e=>e.Contains("machine vision")),"v0.9.66: findImage blocks explicitly");}

            // typing blockers
            try
            {
                PlanExporter.Compile(new List<StepNode> { PexStep("typeText", new Dictionary<string, object?> { ["text"] = "\u0633\u0644\u0627\u0645" }) }, pexSettings, 1920, 1080, "f", "T");
                Assert(false, "v0.9.65: non-ASCII typing must be blocked");
            }
            catch (PlanExporter.PlanBlockedException bx)
            {
                Assert(bx.Errors.Any(e => e.Contains("non-ASCII")), "v0.9.65: non-ASCII typing blocked with the ASCII rule");
            }
            try
            {
                PlanExporter.Compile(new List<StepNode> { PexStep("typeText", new Dictionary<string, object?> { ["text"] = "x", ["secret"] = true }) }, pexSettings, 1920, 1080, "f", "T");
                Assert(false, "v0.9.65: secret typing must be blocked");
            }
            catch (PlanExporter.PlanBlockedException bx)
            {
                Assert(bx.Errors.Any(e => e.Contains("clipboard")), "v0.9.65: secret typing blocked (no PC clipboard on the Pico)");
            }
            try
            {
                PlanExporter.Compile(new List<StepNode> { PexStep("typeText", new Dictionary<string, object?> { ["text"] = "x", ["mode"] = "clipboard" }) }, pexSettings, 1920, 1080, "f", "T");
                Assert(false, "v0.9.65: clipboard mode must be blocked");
            }
            catch (PlanExporter.PlanBlockedException bx)
            {
                Assert(bx.Errors.Any(e => e.Contains("clipboard mode")), "v0.9.65: clipboard mode blocked");
            }

            // If/Else heads now emit native PLAN|2 IFLUX/IFSND + ELSE/ENDIF.
            var pexIf=PexStep("waitForLight",new Dictionary<string,object?>{{"insertIfElse",true},{"luxCenter",100},{"luxTolerance",10}});
            pexIf.Children.Add(PexStep("delay",new Dictionary<string,object?>{{"minMs",5},{"maxMs",5}}));
            var pexElse=PexStep("comment",new Dictionary<string,object?>{{"text","Else"}});
            pexElse.Children.Add(PexStep("mouseClick"));
            var pexIfText=PlanExporter.Compile(new List<StepNode>{pexIf,pexElse,PexStep("comment",new Dictionary<string,object?>{{"text","End If"}})},pexSettings,1920,1080,"f","T").Text;
            Assert(pexIfText.Contains("IFLUX|90,110,2000,20000,0\n")&&pexIfText.Contains("\nELSE\n")&&pexIfText.Contains("\nENDIF\n"),
                "v0.9.66: waitForLight If/Else emits IFLUX/ELSE/ENDIF");
            var pexSndIf=PexStep("waitForSound",new Dictionary<string,object?>{{"insertIfElse",true},{"threshold",92}});
            var pexSndText=PlanExporter.Compile(new List<StepNode>{pexSndIf,PexStep("comment",new Dictionary<string,object?>{{"text","End If"}})},pexSettings,1920,1080,"f","T").Text;
            Assert(pexSndText.Contains("IFSND|92,60,20000\n")&&pexSndText.Contains("\nENDIF\n"),"v0.9.66: waitForSound If emits IFSND/ENDIF");
            // nested blockers inside a blocked findImage head are still NAMED
            var pexFi = PexStep("findImage", new Dictionary<string, object?> { ["insertIfElse"] = true });
            pexFi.Children.Add(PexStep("findImage"));
            pexFi.Children.Add(PexStep("typeText", new Dictionary<string, object?> { ["text"] = "x", ["secret"] = true }));
            try
            {
                PlanExporter.Compile(new List<StepNode>
                {
                    pexFi,
                    PexStep("comment", new Dictionary<string, object?> { ["text"] = "Else" }),
                    PexStep("comment", new Dictionary<string, object?> { ["text"] = "End If" }),
                }, pexSettings, 1920, 1080, "f", "T");
                Assert(false, "v0.9.65: findImage must be blocked");
            }
            catch (PlanExporter.PlanBlockedException bx)
            {
                Assert(bx.Errors.Count == 3,
                    "v0.9.65: nested findImage + secret typing are NAMED inside a blocked head (3 errors)");
            }
            // stray structural marker
            try
            {
                PlanExporter.Compile(new List<StepNode> { PexStep("comment", new Dictionary<string, object?> { ["text"] = "Next" }) }, pexSettings, 1920, 1080, "f", "T");
                Assert(false, "v0.9.65: a stray Next marker must be an error");
            }
            catch (PlanExporter.PlanBlockedException bx)
            {
                Assert(bx.Errors.Any(e => e.Contains("no matching block head")), "v0.9.65: stray marker error");
            }
            // unknown step type
            try
            {
                PlanExporter.Compile(new List<StepNode> { PexStep("mysteryNewStep") }, pexSettings, 1920, 1080, "f", "T");
                Assert(false, "v0.9.65: an unknown step type must be an error");
            }
            catch (PlanExporter.PlanBlockedException bx)
            {
                Assert(bx.Errors.Any(e => e.Contains("unknown step type")), "v0.9.65: unknown step type error");
            }

            // forLoop modes + the Next marker consumption
            var pexLoop = PexStep("forLoop", new Dictionary<string, object?> { ["mode"] = "count", ["count"] = 2 });
            pexLoop.Children.Add(PexStep("mouseClick", new Dictionary<string, object?> { ["button"] = "left", ["action"] = "single" }));
            var pexLoopPlan = PlanExporter.Compile(new List<StepNode>
            {
                pexLoop,
                PexStep("comment", new Dictionary<string, object?> { ["text"] = "Next" }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(pexLoopPlan.Text.Contains("\nLOOP|2\n") && pexLoopPlan.Text.Contains("\nENDLOOP\n"),
                "v0.9.65: forLoop emits LOOP|2 ... ENDLOOP");
            Assert(!PexOps(pexLoopPlan.Text).Any(l => l == "# Next") && pexLoopPlan.Text.Split('\n').Count(l => l == "# Next") == 0,
                "v0.9.65: the loop head consumes its Next marker");
            var pexLoopTime = PlanExporter.Compile(new List<StepNode>
                { PexStep("forLoop", new Dictionary<string, object?> { ["mode"] = "time", ["timeValue"] = 5, ["timeUnit"] = "minute" }) },
                pexSettings, 1920, 1080, "f", "T");
            Assert(pexLoopTime.Text.Contains("\nLOOPTIME|300\n"), "v0.9.65: forLoop time 5 minute -> LOOPTIME|300");
            var pexLoopInf = PlanExporter.Compile(new List<StepNode>
                { PexStep("forLoop", new Dictionary<string, object?> { ["mode"] = "infinite" }) },
                pexSettings, 1920, 1080, "f", "T");
            Assert(pexLoopInf.Text.Contains("\nLOOP|0\n"), "v0.9.65: infinite loop -> LOOP|0");

            // Play Options wrapper (hard rule): once = bare, times N = LOOP|N, timed = LOOPTIME|seconds
            var pexTimes = PlanExporter.Compile(new List<StepNode> { PexStep("mouseClick") },
                new AppSettings { PlayRepeatMode = "times", PlayRepeatTimes = 3 }, 1920, 1080, "f", "T");
            var pexTimesOps = PexOps(pexTimes.Text);
            Assert(pexTimesOps[3] == "LOOP|3" && pexTimesOps[^1] == "ENDLOOP",
                "v0.9.65: Play Options times-3 wraps the whole body");
            var pexTimed = PlanExporter.Compile(new List<StepNode> { PexStep("mouseClick") },
                new AppSettings { PlayRepeatMode = "timed", PlayRepeatValue = 1, PlayRepeatUnit = "minute" }, 1920, 1080, "f", "T");
            Assert(pexTimed.Text.Contains("\nLOOPTIME|60\n"), "v0.9.65: Play Options timed 1 minute -> LOOPTIME|60");

            // waitForLight plain + armed (fractional stableSec -> ms)
            var pexLight = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("waitForLight", new Dictionary<string, object?>
                { ["luxCenter"] = 1250, ["luxTolerance"] = 50, ["stableSec"] = 0.5, ["timeoutMs"] = 9000, ["sampleMode"] = "lowres" }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(pexLight.Text.Contains("WLIGHT|1200,1300,500,9000,1\n"),
                "v0.9.65: plain WLIGHT (fractional stableSec becomes ms)");
            var pexArmed = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("waitForLight", new Dictionary<string, object?>
                { ["luxCenter"] = 1000, ["luxTolerance"] = 100, ["stableSec"] = 2, ["timeoutMs"] = 20000, ["sampleMode"] = "hires",
                  ["armed"] = true, ["key"] = "F4", ["holdMin"] = 30, ["holdMax"] = 90, ["reactMin"] = 80, ["reactMax"] = 180 }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(pexArmed.Text.Contains("WLIGHT|900,1100,2000,20000,0|key=115,30,90|react=80,180\n"),
                "v0.9.65: armed WLIGHT with key + react");

            // disabled steps counted and skipped; comments become # lines
            var pexDis = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("mouseClick", disabled: true),
                PexStep("comment", new Dictionary<string, object?> { ["text"] = "hello note" }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(!pexDis.Text.Contains("CLICK|") && pexDis.Text.Contains("# hello note") && pexDis.Disabled.Count == 1,
                "v0.9.65: disabled steps are counted and skipped; comments become # lines");

            // KeyboardBoard=promicro is a documented FLAG (bridge-mode only)
            var pexArm = PlanExporter.Compile(new List<StepNode> { PexStep("typeText", new Dictionary<string, object?> { ["text"] = "abc" }) },
                new AppSettings { KeyboardBoard = "promicro" }, 1920, 1080, "f", "T");
            Assert(pexArm.Flags.Any(f => f.Contains("KeyboardBoard=promicro")) && pexArm.Text.Contains("# FLAG"),
                "v0.9.65: KeyboardBoard=promicro is a documented FLAG, not silent");

            // delay swap normalization + a container's delay-after lands AFTER its ENDLOOP
            var pexDelay = PlanExporter.Compile(new List<StepNode>
                { PexStep("delay", new Dictionary<string, object?> { ["minMs"] = 500, ["maxMs"] = 100 }) },
                pexSettings, 1920, 1080, "f", "T");
            Assert(pexDelay.Text.Contains("\nDELAY|100,500\n"), "v0.9.65: delay step is swap-normalized");
            var pexLoopD = PexStep("forLoop", new Dictionary<string, object?> { ["mode"] = "count", ["count"] = 1 }, delay: 250);
            pexLoopD.Children.Add(PexStep("delay", new Dictionary<string, object?> { ["minMs"] = 5, ["maxMs"] = 5 }));
            var pexLoopDOps = PexOps(PlanExporter.Compile(new List<StepNode> { pexLoopD }, pexSettings, 1920, 1080, "f", "T").Text);
            Assert(pexLoopDOps[^1] == "DELAY|250" && pexLoopDOps[^2] == "ENDLOOP",
                "v0.9.65: a container's delay-after lands AFTER its ENDLOOP (app semantics)");

            // PLAN2_BUNDLE_TESTS
            // PLAN2_SPLIT_RUNTIME_MIGRATION — recursive compile is a preflighted, all-or-nothing bundle.
            var pexBundleTmp = Path.Combine(Path.GetTempPath(), "pex_bundle_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pexBundleTmp);
            try
            {
                var srcDir = Path.Combine(pexBundleTmp, "src");
                var outDir = Path.Combine(pexBundleTmp, "out");
                Directory.CreateDirectory(srcDir); Directory.CreateDirectory(outDir);
                var rootPath = Path.Combine(srcDir, "root.amsj");
                var grandPath = Path.Combine(srcDir, "grand.amsj");
                var child1Path = Path.Combine(srcDir, "child1.amsj");
                var child2Path = Path.Combine(srcDir, "child2.amsj");
                DocumentService.Save(grandPath, new[] { PexStep("mouseClick") });
                DocumentService.Save(child1Path, new[] { PexStep("playScript", new Dictionary<string, object?> { ["path"] = "grand.amsj" }) });
                DocumentService.Save(child2Path, new[] { PexStep("playScript", new Dictionary<string, object?> { ["path"] = "grand.amsj" }) });
                var rootBundle = new List<StepNode>
                {
                    PexStep("playScript", new Dictionary<string, object?> { ["path"] = "child1.amsj" }),
                    PexStep("playScript", new Dictionary<string, object?> { ["path"] = "child2.amsj" }),
                };
                DocumentService.Save(rootPath, rootBundle);
                var bundleWritten = PlanExporter.Export(Path.Combine(outDir, "plan.txt"), rootBundle,
                    pexSettings, 1920, 1080, rootPath, "TESTPC");
                Assert(bundleWritten.Count == 8 && new[] { "plan.txt", "child1.txt", "child2.txt", "grand.txt", "plan_engine.py", "plan_motion.py", "plan_typing.py", "README-PLAN.md" }.All(n => File.Exists(Path.Combine(outDir, n))),
                    "v0.9.66: recursive playScript exports root + every child + engine/readme");
                Assert(bundleWritten.Count(f => Path.GetFileName(f).Equals("grand.txt", StringComparison.OrdinalIgnoreCase)) == 1,
                    "v0.9.66: duplicate reference to one child source emits one plan file");
                Assert(File.ReadAllText(Path.Combine(outDir, "child1.txt")).Contains("INCLUDE|file=grand.txt"),
                    "v0.9.66: child plans keep recursive INCLUDE links");

                // Same output basename from two different source paths is ambiguous and must block.
                var oneDir = Path.Combine(srcDir, "one"); var twoDir = Path.Combine(srcDir, "two");
                Directory.CreateDirectory(oneDir); Directory.CreateDirectory(twoDir);
                DocumentService.Save(Path.Combine(oneDir, "same.amsj"), new[] { PexStep("mouseClick") });
                DocumentService.Save(Path.Combine(twoDir, "same.amsj"), new[] { PexStep("delay") });
                var collision = new List<StepNode>
                {
                    PexStep("playScript", new Dictionary<string, object?> { ["path"] = Path.Combine("one", "same.amsj") }),
                    PexStep("playScript", new Dictionary<string, object?> { ["path"] = Path.Combine("two", "same.amsj") }),
                };
                try { PlanExporter.Export(Path.Combine(outDir, "collision-root.txt"), collision, pexSettings, 1920, 1080, rootPath, "TESTPC"); Assert(false, "v0.9.66: duplicate include output must block"); }
                catch (PlanExporter.PlanBlockedException bx) { Assert(bx.Errors.Any(e => e.Contains("duplicate include output")), "v0.9.66: duplicate output-name guard is explicit"); }

                // Missing nested file is found during preflight, before any destination write.
                var missingChild = Path.Combine(srcDir, "missing-child.amsj");
                DocumentService.Save(missingChild, new[] { PexStep("playScript", new Dictionary<string, object?> { ["path"] = "nope.amsj" }) });
                var missingOut = Path.Combine(pexBundleTmp, "missing-out"); Directory.CreateDirectory(missingOut);
                try { PlanExporter.Export(Path.Combine(missingOut, "plan.txt"), new[] { PexStep("playScript", new Dictionary<string, object?> { ["path"] = "missing-child.amsj" }) }, pexSettings, 1920, 1080, rootPath, "TESTPC"); Assert(false, "v0.9.66: nested missing include must block"); }
                catch (PlanExporter.PlanBlockedException bx) { Assert(bx.Errors.Any(e => e.Contains("child not found")), "v0.9.66: nested missing-file guard fires in preflight"); }
                Assert(!Directory.EnumerateFileSystemEntries(missingOut).Any(), "v0.9.66: failed include preflight writes zero files");

                // Cycle A -> B -> A is rejected, not silently skipped.
                var aPath = Path.Combine(srcDir, "a.amsj"); var bPath = Path.Combine(srcDir, "b.amsj");
                DocumentService.Save(aPath, new[] { PexStep("playScript", new Dictionary<string, object?> { ["path"] = "b.amsj" }) });
                DocumentService.Save(bPath, new[] { PexStep("playScript", new Dictionary<string, object?> { ["path"] = "a.amsj" }) });
                try { PlanExporter.Export(Path.Combine(outDir, "cycle-root.txt"), new[] { PexStep("playScript", new Dictionary<string, object?> { ["path"] = "a.amsj" }) }, pexSettings, 1920, 1080, rootPath, "TESTPC"); Assert(false, "v0.9.66: include cycle must block"); }
                catch (PlanExporter.PlanBlockedException bx) { Assert(bx.Errors.Any(e => e.Contains("include cycle")), "v0.9.66: include cycle guard reports the chain"); }

                // Root(0) -> d1(1) -> ... -> d4(4) is legal; a d5 child exceeds the cap.
                for (int d = 5; d >= 1; d--)
                {
                    var nodes = d == 5
                        ? new[] { PexStep("mouseClick") }
                        : new[] { PexStep("playScript", new Dictionary<string, object?> { ["path"] = "d" + (d + 1) + ".amsj" }) };
                    DocumentService.Save(Path.Combine(srcDir, "d" + d + ".amsj"), nodes);
                }
                try { PlanExporter.Export(Path.Combine(outDir, "depth-root.txt"), new[] { PexStep("playScript", new Dictionary<string, object?> { ["path"] = "d1.amsj" }) }, pexSettings, 1920, 1080, rootPath, "TESTPC"); Assert(false, "v0.9.66: include depth 5 must block"); }
                catch (PlanExporter.PlanBlockedException bx) { Assert(bx.Errors.Any(e => e.Contains("depth cap 4")), "v0.9.66: recursive include depth cap is four"); }

                // Force publication failure on child.txt after plan.txt moved; every old file returns.
                var rollbackOut = Path.Combine(pexBundleTmp, "rollback-out"); Directory.CreateDirectory(rollbackOut);
                var rollbackChild = Path.Combine(srcDir, "rollback-child.amsj");
                DocumentService.Save(rollbackChild, new[] { PexStep("mouseClick") });
                File.WriteAllText(Path.Combine(rollbackOut, "plan.txt"), "OLD PLAN");
                File.WriteAllText(Path.Combine(rollbackOut, "plan_engine.py"), "OLD ENGINE");
                File.WriteAllText(Path.Combine(rollbackOut, "plan_motion.py"), "OLD MOTION");
                File.WriteAllText(Path.Combine(rollbackOut, "plan_typing.py"), "OLD TYPING");
                File.WriteAllText(Path.Combine(rollbackOut, "README-PLAN.md"), "OLD README");
                Directory.CreateDirectory(Path.Combine(rollbackOut, "rollback-child.txt"));
                try { PlanExporter.Export(Path.Combine(rollbackOut, "plan.txt"), new[] { PexStep("playScript", new Dictionary<string, object?> { ["path"] = "rollback-child.amsj" }) }, pexSettings, 1920, 1080, rootPath, "TESTPC"); Assert(false, "v0.9.66: forced multi-file publish failure must throw"); }
                catch (IOException) { Assert(true, "v0.9.66: forced child-plan publish failure surfaced"); }
                Assert(File.ReadAllText(Path.Combine(rollbackOut, "plan.txt")) == "OLD PLAN" &&
                       File.ReadAllText(Path.Combine(rollbackOut, "plan_engine.py")) == "OLD ENGINE" &&
                       File.ReadAllText(Path.Combine(rollbackOut, "plan_motion.py")) == "OLD MOTION" &&
                       File.ReadAllText(Path.Combine(rollbackOut, "plan_typing.py")) == "OLD TYPING" &&
                       File.ReadAllText(Path.Combine(rollbackOut, "README-PLAN.md")) == "OLD README",
                    "v0.9.66: multi-file rollback restores the complete old bundle");
                Assert(!Directory.GetFiles(rollbackOut).Any(f => f.EndsWith(".tmp") || f.EndsWith(".bak")),
                    "v0.9.66: rollback removes all transaction artifacts");
            }
            finally { if (Directory.Exists(pexBundleTmp)) Directory.Delete(pexBundleTmp, true); }

            // Validator must close the exact top container and reject a second ELSE.
            static bool PexValidatorRejects(string plan, string fragment)
            {
                try
                {
                    typeof(PlanExporter).GetMethod("ValidatePlan", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                        .Invoke(null, new object[] { plan });
                    return false;
                }
                catch (System.Reflection.TargetInvocationException ex)
                {
                    return ex.InnerException is PlanExporter.PlanBlockedException bx && bx.Errors.Any(e => e.Contains(fragment));
                }
            }
            Assert(PexValidatorRejects("PLAN|2\nLOOP|1\nIFSND|1,1,1\nENDLOOP\nENDIF\n", "cannot close IF"),
                "v0.9.66: typed stack rejects cross-nested ENDLOOP");
            Assert(PexValidatorRejects("PLAN|2\nIFSND|1,1,1\nELSE\nELSE\nENDIF\n", "duplicate ELSE"),
                "v0.9.66: typed stack rejects duplicate ELSE");
            Assert(PexValidatorRejects("PLAN|2\nRPKG|all,1,1\nPGROUP\nENDPKG\nENDPAR\n", "cannot close PGROUP"),
                "v0.9.66: typed stack rejects package/group cross nesting");

            // all three embedded runtime modules are byte-identical to generated split goldens
            var pexEngineOnDisk = PexReadRepo(Path.Combine("portable", "plan3", "CIRCUITPY-SPLIT", "plan_engine.py"));
            var pexMotionOnDisk = PexReadRepo(Path.Combine("portable", "plan3", "CIRCUITPY-SPLIT", "plan_motion.py"));
            var pexTypingOnDisk = PexReadRepo(Path.Combine("portable", "plan3", "CIRCUITPY-SPLIT", "plan_typing.py"));
            Assert(pexEngineOnDisk is not null && pexMotionOnDisk is not null && pexTypingOnDisk is not null,
                "v0.9.66: all generated split runtime modules found for golden compare");
            Assert(PexNormEol(pexEngineOnDisk!) == PexNormEol(PlanExporter.BuildEnginePy()) &&
                   PexNormEol(pexMotionOnDisk!) == PexNormEol(PlanExporter.BuildMotionPy()) &&
                   PexNormEol(pexTypingOnDisk!) == PexNormEol(PlanExporter.BuildTypingPy()),
                "v0.9.66: all embedded split runtime modules are byte-identical to generated goldens");
            Assert(PlanExporter.PlanFormatVersion == 2 && PlanExporter.EngineVersion == "0.9.66",
                "v0.9.65: the plan exporter targets the current firmware bundle line (PLAN|2)");

            // Export() writes the whole bundle
            var pexTmp = Path.Combine(Path.GetTempPath(), "pex59_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pexTmp);
            try
            {
                var pexWritten = PlanExporter.Export(Path.Combine(pexTmp, "plan.txt"),
                    new List<StepNode> { PexStep("randomMousePosition", new Dictionary<string, object?> { ["x"] = 100, ["y"] = 100, ["w"] = 200, ["h"] = 200 }) },
                    pexSettings, 1920, 1080, "fixture.amsj", "TESTPC");
                Assert(pexWritten.Count == 5 && File.Exists(Path.Combine(pexTmp, "plan.txt"))
                       && File.Exists(Path.Combine(pexTmp, "plan_engine.py"))
                       && File.Exists(Path.Combine(pexTmp, "plan_motion.py"))
                       && File.Exists(Path.Combine(pexTmp, "plan_typing.py"))
                       && File.Exists(Path.Combine(pexTmp, "README-PLAN.md")),
                    "v0.9.65: Export writes plan.txt + plan_engine.py + README-PLAN.md");
                Assert(File.ReadAllText(Path.Combine(pexTmp, "plan.txt")).StartsWith("PLAN|2\n")
                       && File.ReadAllText(Path.Combine(pexTmp, "plan_engine.py")).Contains("def parse_plan")
                       && File.ReadAllText(Path.Combine(pexTmp, "plan_motion.py")).Contains("def _exec_rmouse")
                       && File.ReadAllText(Path.Combine(pexTmp, "plan_typing.py")).Contains("def plan_typing"),
                    "v0.9.65: the written bundle carries the plan and the real engine");
            }
            finally { if (Directory.Exists(pexTmp)) Directory.Delete(pexTmp, true); }
        }
        }

        // Current Studio contract: the one-click AutoCycle export is the exact
        // Golden-100 inventory. Resumable remains archived and must not add a
        // resume runtime to the active export.
        var cycleFwTmp = Path.Combine(Path.GetTempPath(), "cyclefw_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cycleFwTmp);
        try
        {
            var cycleCode = Path.Combine(cycleFwTmp, "code.py");
            var cycleWritten = AutoCycleFirmwareBundle.Export(cycleCode, Array.Empty<StepNode>(),
                "CURRENT-EXPORT-REGRESSION", "once", 1, 0, false);
            var cycleText = File.ReadAllText(cycleCode);
            Assert(cycleWritten.Count == 25
                   && File.Exists(Path.Combine(cycleFwTmp, "SHA256SUMS.txt"))
                   && File.Exists(Path.Combine(cycleFwTmp, "code.py"))
                   && !File.Exists(Path.Combine(cycleFwTmp, "resume_essentials_runtime.py")),
                "current Studio: one-click Pico export writes the complete Golden-100 inventory");
            Assert(cycleText.Length > 50000
                   && !cycleText.Contains("AUTO_CYCLE_PATCH_0967_H6")
                   && !cycleText.Contains("import plan_cycle as _pc"),
                "current Studio: export preserves the Golden-100 code.py without modern runtime patches");
        }
        finally { if (Directory.Exists(cycleFwTmp)) Directory.Delete(cycleFwTmp, true); }

        // Modern AutoCycle contracts are intentionally separate from the
        // legacy/Golden-100 contract. CI can set GOLDEN_100_ONLY=1 to validate
        // the exact 25-file baseline without requiring modern runtime payloads.
        if (Environment.GetEnvironmentVariable("GOLDEN_100_ONLY") != "1")
        {
        // Modern split-memory export is a separate contract; it must not replace
        // or mutate the legacy Golden-100 exporter.
        var modernTmp = Path.Combine(Path.GetTempPath(), "modernfw_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modernTmp);
        try
        {
            var modernWritten = ModernAutoCycleFirmwareBundle.Export(Path.Combine(modernTmp, "code.py"));
            var modernManifestEntries = File.ReadAllText(
                Path.Combine(modernTmp, "SHA256SUMS.txt"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            Assert(modernWritten.Count >= 36
                   && File.Exists(Path.Combine(modernTmp, "plan_engine_parse.py"))
                   && File.Exists(Path.Combine(modernTmp, "plan_engine_game.py"))
                   && File.Exists(Path.Combine(modernTmp, "plan_engine_human.py"))
                   && File.Exists(Path.Combine(modernTmp, "plan_engine_login.py"))
                   && File.Exists(Path.Combine(modernTmp, "plan_engine_exec.py"))
                   && File.Exists(Path.Combine(modernTmp, "plan_engine_parallel.py"))
                   && File.Exists(Path.Combine(modernTmp, "sound_step_calibration.py"))
                   && File.Exists(Path.Combine(modernTmp, "calibration_fit.py"))
                   && File.Exists(Path.Combine(modernTmp, "restart_cycle.py"))
                   && File.Exists(Path.Combine(modernTmp, "restart_windows.py"))
                   && modernManifestEntries >= 32,
                "modern AutoCycle export writes the split-memory bundle and manifest");
            // Windows checkout expands LF to CRLF and the packaging workflow applies the
            // verified calibration-heap overlay. Keep a bounded deferred entrypoint without
            // pinning the old pre-overlay byte count.
            Assert(File.ReadAllText(Path.Combine(modernTmp, "code.py")).Length < 55000
                   && File.ReadAllText(Path.Combine(modernTmp, "code.py")).Contains("DeferredPlanEngine"),
                "modern AutoCycle export uses the small deferred-loading entrypoint");
            var modernRuntime = File.ReadAllText(Path.Combine(modernTmp, "combined_guard_runtime.py"));
            var modernExec = File.ReadAllText(Path.Combine(modernTmp, "plan_engine_exec.py"));
            Assert(modernRuntime.Contains("mouse_mode = \"relative\"")
                   && modernRuntime.Contains("MMOVE|%d,%d,rel,2")
                   && modernRuntime.Contains("ASND|%d,%d,%d")
                   && modernRuntime.Contains("def sound_parallel_safe")
                   && modernExec.Contains("ctx.mmove_relative(dx, dy)")
                   && File.ReadAllText(Path.Combine(modernTmp, "code.py")).Contains("relative-native-before-route"),
                "modern AutoCycle export uses hostless relative mouse without a cursor bridge");
            Assert(modernRuntime.Contains("line.startswith(\"EVT|ASND|DETECTED\")")
                   && modernRuntime.Contains("line.startswith(\"EVT|ASND|TIMEOUT\")")
                   && modernRuntime.Contains("EVT|SOUND|listen|source=async")
                   && modernRuntime.Contains("mode=async"),
                "Build 81: packaged Classroom runtime preserves the ARM 2.8.2-S4 async sound contract");
            var modernCode = File.ReadAllText(Path.Combine(modernTmp, "code.py"));
            Assert(modernCode.Contains("_LIGHT_ROUTE_COMMANDS")
                   && modernCode.Contains("\"RMOUSE\"")
                   && modernCode.Contains("\"TYPE\"")
                   && modernCode.Contains("\"LABEL\"")
                   && modernCode.Contains("\"GOTO\"")
                   && modernCode.Contains("import plan_engine_login as login_helper")
                   && modernCode.Contains("\"RAW\"")
                   && modernCode.Contains("\"LOOPTIME\"")
                   && modernCode.Contains("\"ENDLOOP\"")
                   && modernCode.Contains("elif command == \"RAW\":")
                   && modernCode.Contains("ctx.mmove_relative(int(fields[0]), int(fields[1]))"),
                "looped hand-sampled RAW/MMOVE routes stay on the low-memory light-route executor");
            var loginHelper = File.ReadAllText(Path.Combine(modernTmp, "plan_engine_login.py"));
            Assert(!loginHelper.Contains("import plan_engine_parse")
                   && loginHelper.Contains("def run_rmouse(")
                   && loginHelper.Contains("def run_type(")
                   && loginHelper.Length < 14000,
                "Login/DC light helper preserves Natural Mouse and typing without importing the full parser");
            var gameHelper = File.ReadAllText(Path.Combine(modernTmp, "plan_engine_game.py"));
            Assert(!gameHelper.Contains("import plan_engine_parse")
                   && !gameHelper.Contains("import plan_engine_exec")
                   && gameHelper.Contains("def run_game(")
                   && gameHelper.Contains("def _parallel(")
                   && gameHelper.Contains("sound_parallel_safe")
                   && gameHelper.Contains("elif op == \"LABEL\"")
                   && gameHelper.Contains("elif op == \"GOTO\"")
                   && gameHelper.Contains("GOTO label not found")
                   && gameHelper.Length < 22000,
                "Game light helper streams fishing and supports LABEL/GOTO without the full parser");

            var repairedWatch = V27ReadSrc(Path.Combine("Services", "LightWatchService.cs"));
            var repairedBridge = V27ReadSrc(Path.Combine("Services", "PythonBoardBridge.cs"));
            var repairedDeploy = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.AutoCycleModern.cs"));
            var watchFault = repairedWatch.IndexOf("WatchFaulted?.Invoke(ex);", StringComparison.Ordinal);
            var watchDelay = repairedWatch.IndexOf("var remaining", watchFault, StringComparison.Ordinal);
            Assert(watchFault >= 0 && watchDelay > watchFault
                   && repairedWatch.Substring(watchFault, watchDelay - watchFault).Contains("break;"),
                "Light Watch stops polling after one transport fault");
            Assert(repairedBridge.Contains("op is (\"send\" or \"send_path\")")
                   && repairedBridge.Contains("SetState(BridgeState.Disconnected)")
                   && repairedBridge.Contains("failed.Kill(entireProcessTree: true)")
                   && repairedBridge.Contains("Port = null;"),
                "bridge kills the stale sidecar and releases COM after send faults");
            var repairedRepoRoot = new DirectoryInfo(AppContext.BaseDirectory)
                .Parent?.Parent?.Parent?.Parent?.FullName ?? "";
            var repairedPythonBridge = File.ReadAllText(
                Path.Combine(repairedRepoRoot, "ams-shell", "bridge", "bridge.py"));
            Assert(repairedPythonBridge.Contains("stale = state[\"link\"]")
                   && repairedPythonBridge.Contains("failed = state[\"link\"]")
                   && repairedPythonBridge.Contains("Pico brain not found on any serial port"),
                "portable bridge closes stale links and never falls into keyless direct-board mode");
            var repairedTransition = File.ReadAllText(
                Path.Combine(modernTmp, "guard_transition.py"));
            var repairedCalibration = File.ReadAllText(
                Path.Combine(modernTmp, "guard_calibration_protocol.py"));
            var repairedCalibrationFit = File.ReadAllText(
                Path.Combine(modernTmp, "calibration_fit.py"));
            Assert(repairedTransition.Contains("game-reentry-after-unknown")
                   && repairedTransition.Contains("\"execute\": False"),
                "Game re-entry after an optical spike restores stage 5 without replaying the macro");
            Assert(repairedCalibration.Contains("def calibrated_profile(")
                   && repairedCalibration.Contains("0.95")
                   && repairedCalibration.Contains("max(center - low, high - center)")
                   && repairedCalibrationFit.Contains("def prepare(")
                   && repairedCalibrationFit.Contains("SAFETY_GAP = 0.25")
                   && repairedCalibrationFit.Contains("MINIMUM = 0.5")
                   && modernRuntime.Contains("__import__(\"calibration_fit\")")
                   && modernRuntime.Replace("\r", "").Length < 40000,
                "light calibration covers asymmetric samples and adaptively fits adjacent profiles");
            var repairedVm = V27ReadSrc(Path.Combine("ViewModels", "MainViewModel.cs"));
            Assert(repairedVm.Contains("_bridge.StateChanged += OnBridgeStateChanged")
                   && repairedVm.Contains("private void OnBridgeStateChanged")
                   && repairedVm.Contains("Connection = ConnectionState.Disconnected")
                   && repairedVm.Contains("ConnectButtonText = \"Connect\""),
                "Build 71: UI follows authoritative bridge disconnects instead of staying green");
            Assert(repairedVm.Contains("_bridge.State != BridgeState.Connected")
                   && repairedVm.Contains("Connect را بزنید"),
                "Build 71: sound calibration rejects a stale bridge before SCAL/WSND probes");
            Assert(repairedVm.Contains("OK|CURSOR", StringComparison.Ordinal)
                   && repairedVm.Contains("ERR|UNKNOWN|SCAL", StringComparison.Ordinal)
                   && repairedVm.Contains("ERR|UNKNOWN|WSND", StringComparison.Ordinal)
                   && repairedVm.Contains("ERR|TIMEOUT|WSND", StringComparison.Ordinal),
                "Build 72: cursor ACK noise is hidden and unsupported sound replies never become threshold 300");
            Assert(modernCode.Contains("elif line.startswith(\"SCAL|\"):")
                   && modernCode.Contains("elif line.startswith(\"WSND|\"):")
                   && modernCode.Contains("reply = self.arm.send(line, ms / 1000.0 + 3)")
                   && modernCode.Contains("reply = self.arm.send(line, timeout_ms / 1000.0 + 3)"),
                "Build 72: Pico host commands proxy sound calibration and probes to the Pro Micro");
            Assert(repairedDeploy.Contains("private async Task ExportAutoCycleModern()")
                   && repairedDeploy.Contains("await StopLightWatchAsync();"),
                "Pico deployment stops Light Watch before USB autoreload");

            var current = new PipelineWorkspace();
            foreach (var tab in current.Tabs) tab.Steps.Clear();
            current[PipelineKind.Desktop].Steps.Add(new StepNode
            {
                Type = "comment",
                Props = new Dictionary<string, object?> { ["text"] = "CURRENT-MOUSE-TEST-ONLY" },
            });
            var exportedLightProfiles = new[]
            {
                new LightStateProfile { Id="desktop", Name="Desktop", Enabled=true, LuxCenter=43.3, LuxTolerance=0.5, StableDurationMs=750, HysteresisLux=1 },
                new LightStateProfile { Id="login-or-dc", Name="Login", Enabled=true, LuxCenter=4, LuxTolerance=2, StableDurationMs=750, HysteresisLux=1 },
                new LightStateProfile { Id="character-dashboard", Name="Character", Enabled=true, LuxCenter=16.7, LuxTolerance=0.5, StableDurationMs=750, HysteresisLux=1 },
                new LightStateProfile { Id="entering-game-loading", Name="Loading", Enabled=true, LuxCenter=38.3, LuxTolerance=1, StableDurationMs=750, HysteresisLux=1 },
                new LightStateProfile { Id="game", Name="Game", Enabled=true, LuxCenter=25.8, LuxTolerance=0.5, StableDurationMs=750, HysteresisLux=1 },
                new LightStateProfile { Id="targeted", Name="Targeted", Enabled=true, LuxCenter=20, LuxTolerance=2, StableDurationMs=750, HysteresisLux=1 },
            };
            ModernAutoCycleFirmwareBundle.ExportCurrentProject(
                Path.Combine(modernTmp, "code.py"), current, new AppSettings(), exportedLightProfiles,
                1920, 1080, "test mous.amsj", "CURRENT-PROJECT-REGRESSION");
            var currentDesktop = File.ReadAllText(Path.Combine(modernTmp, "desktop_steps.txt"));
            var currentSnapshot = File.ReadAllText(Path.Combine(modernTmp, "autocycle.amsj"));
            var currentEngine = File.ReadAllText(Path.Combine(modernTmp, "plan_engine.py"));
            Assert(currentDesktop.Contains("CURRENT-MOUSE-TEST-ONLY")
                   && currentSnapshot.Contains("CURRENT-MOUSE-TEST-ONLY")
                   && !currentDesktop.Contains("Win+2", StringComparison.OrdinalIgnoreCase),
                "modern one-click export replaces template routes and snapshot with the open project");
            using var exportedCalibration = JsonDocument.Parse(File.ReadAllText(Path.Combine(modernTmp, "guard-calibration.json")));
            using var exportedTransition = JsonDocument.Parse(File.ReadAllText(Path.Combine(modernTmp, "guard-transition.json")));
            var calRoot = exportedCalibration.RootElement;
            var transitionRoot = exportedTransition.RootElement;
            var calRevision = calRoot.GetProperty("revision").GetString();
            var transitionRevision = transitionRoot.GetProperty("calibrationRevision").GetString();
            var exportedGame = calRoot.GetProperty("profiles").GetProperty("game");
            var transitionGame = transitionRoot.GetProperty("profiles").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == "game");
            Assert(calRevision == transitionRevision
                   && exportedGame.GetProperty("center").GetDouble() == 25.8
                   && exportedGame.GetProperty("tolerance").GetDouble() == 0.5
                   && transitionGame.GetProperty("center").GetDouble() == 25.8
                   && transitionGame.GetProperty("stableMs").GetInt32() == 750,
                "Build 73: current editable light profiles replace both Pico Guard contracts");

            Assert(currentEngine.Length < 8000
                   && currentEngine.Contains("from plan_engine_parse import")
                   && currentEngine.Contains("def run_plan(plan, ctx):")
                   && currentEngine.Contains("import plan_engine_exec as executor")
                   && currentEngine.Contains("plan-lite-relative")
                   && !currentEngine.Contains("Generated memory-fit core"),
                "modern current-project export preserves the split-engine facade after legacy plan generation");
            var manifestDesktop = File.ReadLines(Path.Combine(modernTmp, "SHA256SUMS.txt"))
                .Single(line => line.EndsWith("  desktop_steps.txt", StringComparison.Ordinal));
            var desktopHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(modernTmp, "desktop_steps.txt")))).ToLowerInvariant();
            Assert(manifestDesktop == desktopHash + "  desktop_steps.txt",
                "modern one-click export rebuilds SHA256SUMS after current routes are generated");
            var manifestEngine = File.ReadLines(Path.Combine(modernTmp, "SHA256SUMS.txt"))
                .Single(line => line.EndsWith("  plan_engine.py", StringComparison.Ordinal));
            var engineHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(modernTmp, "plan_engine.py")))).ToLowerInvariant();
            Assert(manifestEngine == engineHash + "  plan_engine.py",
                "modern one-click export hashes the restored split-engine facade");
            foreach (var profileFile in new[] { "guard-calibration.json", "guard-transition.json" })
            {
                var manifestProfile = File.ReadLines(Path.Combine(modernTmp, "SHA256SUMS.txt"))
                    .Single(line => line.EndsWith("  " + profileFile, StringComparison.Ordinal));
                var profileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(Path.Combine(modernTmp, profileFile)))).ToLowerInvariant();
                Assert(manifestProfile == profileHash + "  " + profileFile,
                    "Build 73: manifest hashes exported " + profileFile);
            }
            var watchedLoop = new StepNode
            {
                Type = "forLoop",
                Props = new Dictionary<string, object?> { ["mode"] = "infinite" },
                Children =
                {
                    new StepNode { Type = "delay", Props = new Dictionary<string, object?> { ["minMs"] = 20, ["maxMs"] = 20 } },
                    new StepNode { Type = "splashListener" },
                },
            };
            current[PipelineKind.Game].Steps.Add(watchedLoop);
            current[PipelineKind.Whisper].Steps.Add(new StepNode
            {
                Type = "buzzer", Props = new Dictionary<string, object?> { ["preset"] = "warning" },
            });
            var whisperProfile = current.SoundProfiles.Single(x => x.Id == 1);
            whisperProfile.Enabled = true; whisperProfile.PeakMin = 20; whisperProfile.PeakMax = 80;
            whisperProfile.Priority = 10; whisperProfile.CooldownMs = 1800;
            current[PipelineKind.Splash].Steps.Add(new StepNode
            {
                Type = "keystroke", Props = new Dictionary<string, object?> { ["key"] = "F" },
            });
            var splashProfile = current.SoundProfiles.Single(x => x.Id == 2);
            splashProfile.Enabled = true; splashProfile.PeakMin = 25; splashProfile.PeakMax = 95;
            splashProfile.Priority = 5; splashProfile.CooldownMs = 900;
            splashProfile.TimeoutMinSec = 19; splashProfile.TimeoutMaxSec = 24;
            ModernAutoCycleFirmwareBundle.ExportCurrentProject(
                Path.Combine(modernTmp, "code.py"), current, new AppSettings(), exportedLightProfiles,
                1920, 1080, "test sound-watch.amsj", "CURRENT-PROJECT-REGRESSION");
            var watchedGame = File.ReadAllText(Path.Combine(modernTmp, "game_steps.txt"));
            var whisperRoute = File.ReadAllText(Path.Combine(modernTmp, "whisper_steps.txt"));
            var watchedSnapshot = File.ReadAllText(Path.Combine(modernTmp, "autocycle.amsj"));
            Assert(watchedGame.Contains("SOUNDWATCH|whisper,20,80,60,10,1800,whisper_steps.txt,global")
                   && watchedGame.Contains("splash,25,95,60,5,900,splash_steps.txt,scoped")
                   && watchedGame.Contains("WPROFILE|splash,19000,24000")
                   && whisperRoute.Contains("BEEP|700,180")
                   && watchedSnapshot.Contains("soundProfiles")
                   && watchedSnapshot.Contains("\"Enabled\": true"),
                "Build 95: global Whisper and dedicated Splash Listener use profile-owned timeout range");

            var legacyWorkspace = new PipelineWorkspace();
            foreach (var tab in legacyWorkspace.Tabs) tab.Steps.Clear();
            legacyWorkspace[PipelineKind.Game].Steps.Add(new StepNode
            {
                Type = "waitForSound",
                Props = new Dictionary<string, object?>
                {
                    ["responseRoute"] = "splash", ["timeoutMinSec"] = 17,
                    ["timeoutMaxSec"] = 23, ["armed"] = false, ["insertIfElse"] = false,
                },
            });
            var legacyWorkspaceJson = PipelineWorkspaceSerializer.Serialize(legacyWorkspace)
                .Replace("\"pipelineVersion\": 5", "\"pipelineVersion\": 4")
                .Replace("\"TimeoutMinSec\": 18,", "")
                .Replace("\"TimeoutMaxSec\": 22,", "");
            var migratedWorkspace = PipelineWorkspaceSerializer.Deserialize(legacyWorkspaceJson);
            var migratedSplash = migratedWorkspace.SoundProfiles.Single(x => x.Id == 2);
            Assert(migratedWorkspace[PipelineKind.Game].Steps
                       .Any(x => x.Type == "splashListener")
                   && migratedSplash.TimeoutMinSec == 17
                   && migratedSplash.TimeoutMaxSec == 23,
                "Build 95: legacy responseRoute=splash migrates to Splash Listener and profile timeout");

            ModernAutoCycleFirmwareBundle.VerifyExportedTarget(modernTmp);
            Assert(true, "Build 93: target read-back accepts 34 valid hashes and matching Guard revisions");
            var corruptGuardPath = Path.Combine(modernTmp, "guard-calibration.json");
            var validGuardBytes = File.ReadAllBytes(corruptGuardPath);
            File.WriteAllText(corruptGuardPath, "37|STATE|debug-cross-link");
            var rejectedCorruption = false;
            try { ModernAutoCycleFirmwareBundle.VerifyExportedTarget(modernTmp); }
            catch (IOException) { rejectedCorruption = true; }
            Assert(rejectedCorruption,
                "Build 76: target read-back rejects a Guard file cross-linked with debug data");
            File.WriteAllBytes(corruptGuardPath, validGuardBytes);
            ModernAutoCycleFirmwareBundle.VerifyExportedTarget(modernTmp);

            var safeDebugCode = File.ReadAllText(Path.Combine(modernTmp, "code.py"));
            Assert(!safeDebugCode.Contains("_DEBUG_FILE")
                   && !safeDebugCode.Contains("disable_concurrent_write_protection=True")
                   && safeDebugCode.Contains("NVM and live CDC events are sufficient"),
                "Build 76: runtime diagnostics never write to the USB-mounted CIRCUITPY FAT volume");
            Assert(repairedDeploy.Contains("HALT|SILENT")
                   && repairedDeploy.Contains("VerifyExportedTarget")
                   && repairedDeploy.Contains("hashes and Guard revisions OK"),
                "Build 76: export quiesces Pico and verifies target bytes before reporting success");
        }
        finally { if (Directory.Exists(modernTmp)) Directory.Delete(modernTmp, true); }

        // ci-36 follow-up: the UI-selected Random Package must become a real
        // resume_essentials.txt PLAN|2 pre-pass, never an empty silent manager.
        var essentialsTmp = Path.Combine(Path.GetTempPath(), "essentials_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(essentialsTmp);
        try
        {
            var package = new StepNode
            {
                Type = "randomPackage",
                Props = new Dictionary<string, object?>
                {
                    ["mode"] = "shuffleAll", ["minCount"] = 1, ["maxCount"] = 1,
                },
            };
            package.Children.Add(new StepNode
            {
                Type = "typeText",
                Props = new Dictionary<string, object?>
                {
                    ["text"] = " resume essential", ["mode"] = "keystrokes",
                    ["hmin"] = 80, ["hmax"] = 120,
                },
            });
            var essentialRoots = new List<StepNode> { package };
            ResumeEssentialsContract.Mark(essentialRoots, package);
            var essentialSettings = new AppSettings
            {
                PlayRepeatMode = "times", PlayRepeatTimes = 3,
                RestartMinMinutes = 2, RestartMaxMinutes = 3,
                AutoResumeEnabled = true, AutoResumeMinMinutes = 1, AutoResumeMaxMinutes = 2,
                PostRestartLaunchEnabled = true, PostRestartTaskbarSlot = 1,
                PostRestartLaunchBeforeMinSeconds = 1, PostRestartLaunchBeforeMaxSeconds = 3,
                PostRestartLaunchAfterMinSeconds = 20, PostRestartLaunchAfterMaxSeconds = 40,
            };
            var essentialWritten = AutoCyclePlanBundle.Export(Path.Combine(essentialsTmp, "plan.txt"),
                essentialRoots, essentialSettings, 1920, 1080, "essential.amsj", "TESTPC");
            var essentialText = File.ReadAllText(Path.Combine(essentialsTmp, "resume_essentials.txt"));
            var rootText = File.ReadAllText(Path.Combine(essentialsTmp, "plan.txt"));
            Assert(essentialWritten.Count == 11
                   && File.Exists(Path.Combine(essentialsTmp, "resume_essentials_runtime.py")),
                "v0.9.67: AutoCycle plan bundle writes runtime plus resume_essentials.txt");
            Assert(essentialText.StartsWith("PLAN|2\n")
                   && essentialText.Contains("RPKG|all,1,1")
                   && essentialText.Contains("TYPE|text= resume essential")
                   && !essentialText.Contains("LOOP|3"),
                "v0.9.67: selected Random Package is compiled once as the Resume Essentials pre-pass");
            Assert(rootText.Contains("POSTLAUNCH|1,1,1,3,20,40")
                   && !essentialText.Contains("POSTLAUNCH|"),
                "v0.9.67: Restart Launch is root-only and precedes the Resume Essentials pre-pass");
        }
        finally { if (Directory.Exists(essentialsTmp)) Directory.Delete(essentialsTmp, true); }

        }

        Console.WriteLine($"=== Results: {passed} passed, {failed} failed ===");
        Environment.Exit(failed > 0 ? 1 : 0);
    }
}

/// <summary>v0.7.8 tests — in-memory bridge that records commands and replies OK.</summary>
sealed class FakeBridge : IBoardBridge
{
    public readonly List<string> Sent = new();
    public bool UnknownOp;                 // v0.9.2 — simulate an old bridge.py without send_path
    public int PathCalls;
    public List<(int X, int Y, int DelayMs)>? LastPath;
    public int ArtificialDelayMs;          // v0.9.15 — per-op latency for concurrency tests
    public int InFlight;
    public int MaxInFlight;
    public async Task<string> SendPathAsync(IReadOnlyList<(int X, int Y, int DelayMs)> points, CancellationToken ct = default)
    {
        Interlocked.Increment(ref InFlight);
        try
        {
            PathCalls++;
            if (UnknownOp) throw new InvalidOperationException("unknown op");
            if (ArtificialDelayMs > 0) await Task.Delay(ArtificialDelayMs);
            MaxInFlight = Math.Max(MaxInFlight, InFlight);
            LastPath = points.ToList();
            return "OK|PATH," + points.Count;
        }
        finally { Interlocked.Decrement(ref InFlight); }
    }
    public event EventHandler<string>? LineReceived { add { } remove { } }
    public event EventHandler<BridgeState>? StateChanged { add { } remove { } }
    public BridgeState State => BridgeState.Connected;
    public string? FirmwareVersion => "1.7-test";
    public string? Port => "FAKE";
    public Task ConnectAsync(string portName, CancellationToken ct = default) => Task.CompletedTask;
    public async Task<string> SendAsync(string command, double? timeoutSeconds = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref InFlight);
        try
        {
            if (ArtificialDelayMs > 0) await Task.Delay(ArtificialDelayMs);
            MaxInFlight = Math.Max(MaxInFlight, InFlight);
            lock (Sent) Sent.Add(command);   // v0.9.15 — Parallel Group branches share this list
            return "OK|" + command.Split('|')[0];
        }
        finally { Interlocked.Decrement(ref InFlight); }
    }
    public Task SendAbortAsync() => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
