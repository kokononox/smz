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

            // mouseMove -> selected RMOUSE region; idle explicitly OFF (gen-1 defaults it ON)
            var pexMove = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("mouseMove", new Dictionary<string, object?> { ["x"] = 700, ["y"] = 400, ["w"] = 180, ["h"] = 120, ["human"] = true }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(pexMove.Text.Contains("RMOUSE|region=700,400,180,120|before=60,220|after=80,280|curve=20,40|mid=6:80,250|over=12|idle=1,1:0,0\n"),
                "mouseMove exports a random point within the chosen rectangle as humanized PLAN|2 RMOUSE");
            Assert(!pexMove.Text.Contains("idle=5,12"),
                "v0.9.65: the engine's built-in idle default never leaks into a point move");
            var pexNoHuman = PlanExporter.Compile(new List<StepNode>
            {
                PexStep("mouseMove", new Dictionary<string, object?> { ["x"] = 5, ["y"] = 6, ["human"] = false }),
            }, pexSettings, 1920, 1080, "f", "T");
            Assert(pexNoHuman.Text.Contains("RMOUSE|region=5,6,1,1|before=60,220")
                   && pexNoHuman.Flags.Any(f => f.Contains("human=false cannot be preserved")),
                "portable export warns that human=false is not supported by PLAN|2 RMOUSE");

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
            foreach(var op in new[]{"WSND|91,70,8000","KEY|combo=162+65","KDOWN|160","KUP|160","WHEEL|-3","LABEL|again","GOTO|again","RAW|PING","RPKG|all,1,2","PKGITEM","ENDPKG","PGROUP","PARITEM","ENDPAR"})
                Assert(pexParityText.Contains(op),"v0.9.66: C# parity emits "+op);
            Assert(pexParityText.Split('\n').Count(x=>x=="KEY|combo=91+82|hold=40,90")==3,
                "v0.9.66: runExe/openFile/playAudio emit Win+R macros");

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

