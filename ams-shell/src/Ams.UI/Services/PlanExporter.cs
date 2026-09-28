using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Ams.UI.Models;

namespace Ams.UI.Services;

// PLAN2_PARITY_MIGRATION
// PLAN2_BUNDLE_MIGRATION
// PLAN2_SPLIT_RUNTIME_MIGRATION

/// <summary>
/// v0.9.65 - File → Export Pico Plan… (phase 2 of the portable line): compiles the open step
/// tree into a portable plan.txt for the Pico. Generated from tools/PlanExporter.cs.tpl by
/// tools/make_plan_exporter.py (the PLAN|2 engine is spliced in from portable/plan3/CIRCUITPY/plan_engine.py -
/// never edit the embedded copy by hand). Sandbox-proven by sim/sim_plan_export.py against the
/// real engine (54/54). Narrowed to the GEN-1 contract.
///
/// Target contract (project page, 2026-09-10): the PLAN|2 plan engine on the drive (firmware
/// pico-light 0.9.66 = portable/plan3/CIRCUITPY/plan_engine.py) accepts ONLY the header PLAN|2 and its
/// 11 ops: PLAN, SCREEN, SPEED, RMOUSE, CLICK, TYPE, DELAY, LOOP, LOOPTIME, ENDLOOP, WLIGHT.
/// The gen-3 engine (PLAN|2, portable/plan3/tools/plan_gen.py) belongs to the parked 0.9.66
/// line, so every step that needs it is a BLOCKING ERROR here - never silently skipped.
///
/// Compiled (8 of the 23 app actions): randomMousePosition, mouseMove (emitted as a
/// deterministic 1x1 RMOUSE region - the engine draws tx=randint(x, x+0)=x every pass),
/// mouseClick, typeText, delay, forLoop, waitForLight (plain + armed), comment. Blocked (15):
/// findImage, waitForSound, keystroke, keyDown, keyUp, mouseScroll, label, gotoLabel,
/// rawCommand, randomPackage, parallelGroup, playAudio, playScript, runExe, openFile.
/// If/Else heads (insertIfElse) are blocked too - IFLUX/ELSE/ENDIF are gen-2 ops.
///
/// PLAN|2 compensations vs plan_gen (gen-3): the PLAN|2 engine DEFAULTS mid-pauses ON (12%) and
/// idle breaks ON (1000-5000 ms every 5-12 moves) when a key is omitted, so this exporter emits
/// mid= and idle= EXPLICITLY on every move (mid=0:0,0 / idle=1,1:0,0 disable them), and the
/// mouseMove emulation always carries idle=1,1:0,0 (a point-to-point move never idles).
/// </summary>
public static class PlanExporter
{
    /// <summary>The on-drive engine format this exporter targets (firmware pico-light 0.9.66).</summary>
    public const int PlanFormatVersion = 2;
    public const string EngineVersion = "0.9.66";

    /// <summary>One successful compilation: the plan text plus the human-readable reports.</summary>
    public sealed record PlanResult(string Text, IReadOnlyList<string> Flags,
                                    IReadOnlyList<string> Disabled, IReadOnlyList<string> Counts);

    /// <summary>Thrown when any step cannot be expressed on PLAN|2 - the file is NOT written.</summary>
    public sealed class PlanBlockedException : Exception
    {
        public IReadOnlyList<string> Errors { get; }
        public PlanBlockedException(IReadOnlyList<string> errors)
            : base("plan export blocked: " + errors.Count + " problem(s)") => Errors = errors;
    }

    private static readonly Dictionary<string, int> TimeUnitSec = new()
    { ["second"] = 1, ["minute"] = 60, ["hour"] = 3600 };

    private static readonly HashSet<string> Conditional = new() { "findImage", "waitForSound", "waitForLight" };
    private static readonly HashSet<string> LoopOwners = new() { "forLoop", "randomPackage", "parallelGroup" };

    // Bare-modifier aliases on top of KeyMap.VK (plan_gen parity: the armed-key field accepts them).
    private static readonly Dictionary<string, int> VkAliases = new()
    { ["SHIFT"] = 160, ["CTRL"] = 162, ["CONTROL"] = 162, ["ALT"] = 164, ["WIN"] = 91, ["LWIN"] = 91 };

    private static (int, int) Pair(int mn, int mx) => mx < mn ? (mx, mn) : (mn, mx);

    /// <summary>TYPE text encoder: '|', '%' and newline are structural in plan.txt; the board
    /// types ASCII 0x20-0x7E only, anything else is a blocking error (2026-09-04 decision).</summary>
    private static string PctType(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Replace("\r\n", "\n").Replace("\r", "\n"))
        {
            if (ch == '\n') sb.Append("%0A");
            else if (ch == '%') sb.Append("%25");
            else if (ch == '|') sb.Append("%7C");
            else if (ch >= 32 && ch <= 126) sb.Append(ch);
            else throw new FormatException(
                "non-ASCII/control character '" + ch + "' - the board types ASCII only. "
                + "Keep the text Latin (non-Latin typing is out of scope for the port).");
        }
        return sb.ToString();
    }

    /// <summary>The app's structural markers are comment steps: Next / Else… / End If.</summary>
    private static bool IsMarker(StepNode n, string? kind = null)
    {
        if (n.Type != "comment") return false;
        var t = PropEx.GetString(n.Props, "text").Trim().ToLowerInvariant();
        return kind switch
        {
            "next" => t == "next",
            "else" => t.StartsWith("else", StringComparison.Ordinal),
            "endif" => t.StartsWith("end if", StringComparison.Ordinal),
            _ => t == "next" || t.StartsWith("else", StringComparison.Ordinal)
                 || t.StartsWith("end if", StringComparison.Ordinal),
        };
    }

    private sealed class Gen
    {
        private readonly AppSettings _settings;
        public readonly List<string> Lines = new();
        public readonly List<string> Errors = new();
        public readonly List<string> Flags = new();
        public readonly List<string> Disabled = new();
        public readonly Dictionary<string, int> Counts = new();
        public int Markers;
        private readonly Dictionary<StepNode, string> _numbering = new();
        private readonly HashSet<string> _labels = new(StringComparer.Ordinal);
        private readonly HashSet<int> _soundCalibrationIds = new();
        private readonly string _sourcePath;

        public Gen(AppSettings settings, string sourcePath)
        { _settings = settings; _sourcePath = sourcePath; }

        public void NumberTree(IEnumerable<StepNode> nodes, string prefix)
        {
            int i = 0;
            foreach (var n in nodes)
            {
                i++;
                var num = prefix.Length > 0 ? prefix + "." + i : i.ToString(CultureInfo.InvariantCulture);
                _numbering[n] = num;
                NumberTree(n.Children, num);
            }
        }

        private string Num(StepNode n) => _numbering.TryGetValue(n, out var s) ? s : "?";

        private string Title(StepNode n)
        {
            var name = (n.Name ?? "").Trim();
            return "step " + Num(n) + " (" + n.Type + ")" + (name.Length > 0 ? " - «" + name + "»" : "");
        }

        public void Error(StepNode n, string msg) => Errors.Add(Title(n) + ": " + msg);

        public void Flag(StepNode n, string msg)
        {
            Flags.Add(Title(n) + ": " + msg);
            Lines.Add("# FLAG " + Flags[^1]);
        }

        private void Count(string key) => Counts[key] = Counts.TryGetValue(key, out var c) ? c + 1 : 1;

        private void Emit(StepNode n, IEnumerable<string> ops, string? countType = null)
        {
            Lines.AddRange(ops);
            Count(countType ?? n.Type);
            EmitDelay(n);
        }

        /// <summary>Per-step 'delay after' - exactly the app's Delay / DelayMax semantics.</summary>
        private void EmitDelay(StepNode n)
        {
            int d = n.Delay, dm = n.DelayMax;
            if (dm > 0)
            {
                var (lo, hi) = Pair(d, dm);
                if (hi > 0) Lines.Add("DELAY|" + lo.ToString(CultureInfo.InvariantCulture) + "," + hi.ToString(CultureInfo.InvariantCulture));
            }
            else if (d > 0)
            {
                Lines.Add("DELAY|" + d.ToString(CultureInfo.InvariantCulture));
            }
        }

        public void Walk(IList<StepNode> nodes)
        {
            int i = 0;
            while (i < nodes.Count)
            {
                var n = nodes[i];
                if (n.IsDisabled)
                {
                    Disabled.Add(Title(n));
                    i++;
                    continue;
                }
                if (IsMarker(n))
                {
                    Error(n, "structural marker '" + PropEx.GetString(n.Props, "text").Trim() + "' has no matching "
                             + "block head - open and re-save the plan in the app (it self-heals), then export again");
                    i++;
                    continue;
                }
                if (Conditional.Contains(n.Type) && PropEx.GetBool(n.Props, "insertIfElse"))
                {
                    i = EmitIf(nodes, i);
                    continue;
                }
                EmitStep(n);
                i = LoopOwners.Contains(n.Type) ? ConsumeNext(nodes, i) : i + 1;
            }
        }

        /// <summary>A loop/package/group head owns the following Next marker (v0.9.48).</summary>
        private int ConsumeNext(IList<StepNode> nodes, int i)
        {
            if (i + 1 < nodes.Count && IsMarker(nodes[i + 1], "next"))
            {
                Markers++;
                return i + 2;
            }
            return i + 1;
        }

        /// <summary>An insertIfElse head is a BLOCKING error on PLAN|2 (IFLUX/ELSE/ENDIF are gen-2);
        /// its Else/End If markers are still consumed so they do not cascade as stray-marker errors,
        /// and nested blockers are still NAMED.</summary>
        private int EmitIf(IList<StepNode> nodes, int i)
        {
            var n=nodes[i]; StepNode? elseNode=null,endifNode=null; int j=i+1;
            if(j<nodes.Count&&IsMarker(nodes[j],"else")){elseNode=nodes[j];j++;}
            if(j<nodes.Count&&IsMarker(nodes[j],"endif")){endifNode=nodes[j];j++;}
            if(endifNode is null) Error(n,"insertIfElse is on but the matching 'End If' marker is missing");
            if(n.Type=="findImage") { Error(n,"findImage needs machine vision - it cannot run on the Pico"); CollectBlockers(n); foreach(var sub in new[]{elseNode,endifNode})if(sub is not null)CollectBlockers(sub); return j; }
            var p=n.Props;
            if(n.Type=="waitForSound") { Lines.Add("IFSND|"+PropEx.GetInt(p,"threshold",90)+","+PropEx.GetInt(p,"minDurationMs",60)+","+PropEx.GetInt(p,"timeoutMs",20000)); Count("IFSND"); }
            else { var(lo,hi,stable,to,mode)=LuxArgs(p); Lines.Add("IFLUX|"+lo+","+hi+","+stable+","+to+","+mode); Count("IFLUX"); }
            Walk(n.Children); if(elseNode is not null){Lines.Add("ELSE");Markers++;Walk(elseNode.Children);} if(endifNode is not null){Lines.Add("ENDIF");Markers++;} EmitDelay(n); return j;
        }

        /// <summary>A blocked container hides its subtree from the walk; sweep it so every nested
        /// findImage/secret/clipboard step is still NAMED in the blocking report.</summary>
        private void CollectBlockers(StepNode node)
        {
            foreach (var c in node.Children)
            {
                if (c.IsDisabled) continue;
                if (c.Type == "findImage")
                    Error(c, "findImage needs machine vision - it cannot run on the Pico (nested "
                             + "inside an already-blocked step)");
                else if (c.Type == "typeText"
                         && (PropEx.GetBool(c.Props, "secret")
                             || PropEx.GetString(c.Props, "mode", "keystrokes") == "clipboard"))
                    Error(c, "secret/clipboard typing needs a PC clipboard (nested inside an "
                             + "already-blocked step)");
                CollectBlockers(c);
            }
        }

        private void EmitStep(StepNode n)
        {
            switch(n.Type)
            {
                case "comment":EmitComment(n);return; case "delay":EmitDelayStep(n);return;
                case "randomMousePosition":EmitRandomMouse(n);return; case "mouseMove":EmitMouseMove(n);return;
                case "mouseClick":EmitMouseClick(n);return; case "mouseScroll":EmitMouseScroll(n);return;
                case "keystroke":EmitKeystroke(n);return; case "keyDown":EmitKeyState(n,true);return; case "keyUp":EmitKeyState(n,false);return;
                case "typeText":EmitTypeText(n);return; case "forLoop":EmitForLoop(n);return;
                case "buzzer":EmitBuzzer(n);return;
                case "waitForSound":EmitWaitForSound(n);return; case "waitForLight":EmitWaitForLight(n);return;
                case "label":EmitLabel(n);return; case "gotoLabel":EmitGoto(n);return; case "rawCommand":EmitRaw(n);return;
                case "randomPackage":EmitRandomPackage(n);return; case "parallelGroup":EmitParallelGroup(n);return;
                case "runExe":EmitLaunch(n,false);return; case "openFile":EmitLaunch(n,true);return; case "playAudio":EmitAudio(n);return; case "playScript":EmitInclude(n);return;
                case "findImage":Error(n,"findImage needs machine vision - it cannot run on the Pico");CollectBlockers(n);return;
                default:Error(n,"unknown step type '"+n.Type+"' - this exporter does not know it (supported: the 23 app actions)");return;
            }
        }

        private void EmitComment(StepNode n)
        {
            Lines.Add("# " + PropEx.GetString(n.Props, "text").Replace("\n", " "));
            Count("comment");
        }

        private void EmitDelayStep(StepNode n)
        {
            var (lo, hi) = Pair(PropEx.GetInt(n.Props, "minMs", 0), PropEx.GetInt(n.Props, "maxMs", 333));
            Emit(n, new[] { hi > lo ? "DELAY|" + lo + "," + hi : "DELAY|" + lo }, "DELAY");
        }

        private void EmitBuzzer(StepNode n)
        {
            IReadOnlyList<string> commands;
            try { commands = StepDefinitions.BuildBuzzerCommands(n.Props); }
            catch (FormatException ex) { Error(n, ex.Message); return; }
            foreach (var command in commands)
            {
                if (command.StartsWith("BEEP|", StringComparison.Ordinal))
                {
                    Lines.Add(command);
                    Count("BEEP");
                }
                else if (command.StartsWith("DLY|", StringComparison.Ordinal)
                         && int.TryParse(command[4..], NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out var pause))
                {
                    Lines.Add("DELAY|" + pause + "," + pause);
                    Count("DELAY");
                }
            }
            EmitDelay(n);
        }

        /// <summary>The humanized-move layers; PLAN|2 forces EVERY key explicit (its built-in defaults
        /// enable mid-pauses at 12% and idle breaks at 1000-5000 ms - the app defaults differ, so an
        /// omitted key would change behavior on the Pico).</summary>
        private string Tuning(StepNode n, (int i0, int i1, int p0, int p1) idle)
        {
            var p = n.Props;
            var (b0, b1) = Pair(PropEx.GetInt(p, "pauseBeforeMin", 60), PropEx.GetInt(p, "pauseBeforeMax", 220));
            var (a0, a1) = Pair(PropEx.GetInt(p, "pauseAfterMin", 80), PropEx.GetInt(p, "pauseAfterMax", 280));
            var (c0, c1) = Pair(PropEx.GetInt(p, "curveMinPct", 20), PropEx.GetInt(p, "curveMaxPct", 40));
            var parts = new List<string>
            {
                "before=" + b0 + "," + b1,
                "after=" + a0 + "," + a1,
                "curve=" + c0 + "," + c1,
            };
            var mch = Math.Max(0, Math.Min(100, PropEx.GetInt(p, "midPauseChance", 6)));
            var (m0, m1) = Pair(PropEx.GetInt(p, "midPauseMin", 80), PropEx.GetInt(p, "midPauseMax", 250));
            parts.Add("mid=" + mch + ":" + m0 + "," + m1);
            parts.Add("over=" + Math.Max(0, Math.Min(100, PropEx.GetInt(p, "overshootChance", 12))));
            var sampledMin = 0;
            var sampledMax = 0;
            var hasSampledCadence = n.Type == "randomMousePosition"
                && HandMovementSample.TryDecode(PropEx.GetString(p, "handSample"), out var sample)
                && HandMovementSample.TryGetSpeedRange(sample, out sampledMin, out sampledMax);
            var (t0, t1) = Pair(PropEx.GetInt(p, "moveTimeMin", 0), PropEx.GetInt(p, "moveTimeMax", 0));
            if (!hasSampledCadence && t1 > 0) parts.Add("mt=" + t0 + "," + t1);
            if (hasSampledCadence)
                parts.Add("speed=" + sampledMin + "," + sampledMax);
            parts.Add("idle=" + idle.i0 + "," + idle.i1 + ":" + idle.p0 + "," + idle.p1);
            return "|" + string.Join("|", parts);
        }

        private void EmitRandomMouse(StepNode n)
        {
            var p = n.Props;
            int x = PropEx.GetInt(p, "x", 1301), y = PropEx.GetInt(p, "y", 0);
            int w = PropEx.GetInt(p, "w", 378), h = PropEx.GetInt(p, "h", 1049);
            if (w <= 0 || h <= 0)
            {
                Error(n, "region width/height must be positive (got " + w + "x" + h + ")");
                return;
            }
            var (i0, i1) = Pair(PropEx.GetInt(p, "idleEveryMin", 5), PropEx.GetInt(p, "idleEveryMax", 12));
            var (p0, p1) = Pair(PropEx.GetInt(p, "idlePauseMin", 800), PropEx.GetInt(p, "idlePauseMax", 3000));
            // explicit-off is read from the RAW idlePauseMax (max pause = 0 => no idle breaks),
            // BEFORE Pair swaps the bounds - otherwise (min=800, max=0) swaps to (0,800) and the
            // off-intent is lost.
            var idle = PropEx.GetInt(p, "idlePauseMax", 3000) > 0 ? (i0, i1, p0, p1) : (1, 1, 0, 0);
            Emit(n, new[] { "RMOUSE|region=" + x + "," + y + "," + w + "," + h + Tuning(n, idle) }, "RMOUSE");
        }

        private void EmitMouseMove(StepNode n)
        {
            int x=PropEx.GetInt(n.Props,"x",600), y=PropEx.GetInt(n.Props,"y",497);
            if (PropEx.GetString(n.Props, "moveMode", "fixed") == "handSample")
            {
                if (!HandMovementSample.TryDecode(PropEx.GetString(n.Props, "handSample"), out var sample))
                { Error(n, "handSample mode needs a valid ten-second mouse sample"); return; }
                var path = HandMovementSample.Compact(sample.Segments, HandMovementSample.ReplaySegmentLimit);
                // DelayMs is the time BEFORE this captured cursor change. Keep
                // all samples in one light-runtime command: this preserves the
                // gesture without allocating one Pico command tuple per point.
                var payload = string.Join(";", path.Select(seg =>
                    seg.DelayMs.ToString(CultureInfo.InvariantCulture) + "," +
                    seg.Dx.ToString(CultureInfo.InvariantCulture) + "," +
                    seg.Dy.ToString(CultureInfo.InvariantCulture)));
                Emit(n, new[] { "HANDPATH|" + payload }, "HANDPATH");
                return;
            }
            if(!PropEx.GetBool(n.Props,"human",true)){Emit(n,new[]{"MOVETO|x="+x+"|y="+y+"|human=0"},"MOVETO");return;}
            Emit(n,new[]{"MOVETO|x="+x+"|y="+y+Tuning(n,(1,1,0,0))},"MOVETO");
        }

        private void EmitMouseClick(StepNode n)
        {
            var p = n.Props;
            var btn = PropEx.GetString(p, "button", "left");
            if (btn is not ("left" or "middle" or "right"))
            {
                Error(n, "unknown mouse button '" + btn + "'");
                return;
            }
            int count = PropEx.GetString(p, "action", "single") == "double" ? 2 : 1;
            var (hmin, hmax) = Pair(PropEx.GetInt(p, "holdMin", 0), PropEx.GetInt(p, "holdMax", 0));
            var line = "CLICK|btn=" + btn + "|n=" + count;
            if (hmax > 0) line += "|hold=" + hmin + "," + hmax;
            Emit(n, new[] { line }, "CLICK");
        }

        private void EmitTypeText(StepNode n)
        {
            var p = n.Props;
            if (PropEx.GetBool(p, "secret"))
            {
                Error(n, "secret steps type through the Windows clipboard, which does not exist on a "
                         + "bare Pico - re-enter the text as plain keystrokes (or keep this plan in PC mode)");
                return;
            }
            if (PropEx.GetString(p, "mode", "keystrokes") == "clipboard")
            {
                Error(n, "clipboard mode needs a PC clipboard - the portable plan can only type "
                         + "keystrokes. Switch the step to keystrokes mode (text must be ASCII)");
                return;
            }
            string text;
            try { text = PctType(PropEx.GetString(p, "text")); }
            catch (FormatException ex) { Error(n, ex.Message); return; }
            if (text.Length == 0) { Error(n, "empty text - nothing to type"); return; }

            var (hmin, hmax) = Pair(PropEx.GetInt(p, "hmin", _settings.TypeKeyMinMs),
                                    PropEx.GetInt(p, "hmax", _settings.TypeKeyMaxMs));
            var parts = new List<string> { "h=" + hmin + "," + hmax };
            var (wmin, wmax) = Pair(PropEx.GetInt(p, "wmin", 0), PropEx.GetInt(p, "wmax", 0));
            if (wmax > 0)
            {
                parts.Add("w=" + wmin + "," + wmax);
                if (p.ContainsKey("wordPauseChance"))
                    parts.Add("wp=" + Math.Max(0, Math.Min(100, PropEx.GetInt(p, "wordPauseChance", 100))));
            }
            var (pmin, pmax) = Pair(PropEx.GetInt(p, "pmin", 0), PropEx.GetInt(p, "pmax", 0));
            if (pmax > 0) parts.Add("p=" + pmin + "," + pmax);
            var tch = Math.Max(0, Math.Min(100, PropEx.GetInt(p, "thinkChance", 0)));
            if (tch > 0)
            {
                var (t0, t1) = Pair(PropEx.GetInt(p, "thinkMin", 800), PropEx.GetInt(p, "thinkMax", 2200));
                if (t1 > 0) parts.Add("think=" + tch + ":" + t0 + "," + t1);
            }
            var (y0, y1) = Pair(PropEx.GetInt(p, "typoEveryMin", 0), PropEx.GetInt(p, "typoEveryMax", 0));
            if (y1 > 0) parts.Add("typochars=" + y0 + "," + y1);
            else if (PropEx.GetInt(p, "typoChance", 0) > 0)
                Flag(n, "legacy typoChance % is not portable - use the character interval (typoEveryMin/Max); "
                        + "the step types WITHOUT typos in this plan");
            Emit(n, new[] { "TYPE|text=" + text + "|" + string.Join("|", parts) }, "TYPE");
        }


        private int Vk(StepNode n,string name,string what){if(KeyMap.VK.TryGetValue(name,out var vk)||VkAliases.TryGetValue(name,out vk))return vk;Error(n,"unknown key name '"+name+"' for "+what);return 0;}
        private void KeyboardFlag(StepNode n){if(PropEx.GetString(n.Props,"keyboardBoard","default")=="promicro")Flag(n,"keyboard executor 'promicro' is bridge-mode only; on the portable plan the Pico types it");}
        private void EmitMouseScroll(StepNode n)=>Emit(n,new[]{"WHEEL|"+PropEx.GetInt(n.Props,"delta",-1)},"WHEEL");
        private void EmitKeystroke(StepNode n){var p=n.Props;var keys=new List<int>();if(PropEx.GetBool(p,"modCtrl"))keys.Add(162);if(PropEx.GetBool(p,"modShift"))keys.Add(160);if(PropEx.GetBool(p,"modAlt"))keys.Add(164);if(PropEx.GetBool(p,"modWin"))keys.Add(91);var vk=Vk(n,PropEx.GetString(p,"key","F4"),"keystroke");if(vk==0)return;keys.Add(vk);var(h0,h1)=Pair(PropEx.GetInt(p,"holdMin",0),PropEx.GetInt(p,"holdMax",0));var line="KEY|combo="+string.Join("+",keys);if(h1>0)line+="|hold="+h0+","+h1;KeyboardFlag(n);Emit(n,new[]{line},"KEY");}
        private void EmitKeyState(StepNode n,bool down){var vk=Vk(n,PropEx.GetString(n.Props,"key","SHIFT"),down?"keyDown":"keyUp");if(vk==0)return;KeyboardFlag(n);Emit(n,new[]{(down?"KDOWN|":"KUP|")+vk},down?"KDOWN":"KUP");}
        private string SoundBinding(StepNode n,int id,int threshold,int minimum)
        {
            var title=PropEx.GetString(n.Props,"title",n.Name??"").Trim();
            var raw=Encoding.UTF8.GetBytes(id+"|"+Num(n)+"|"+title+"|"+threshold+"|"+minimum);
            return Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant()[..12];
        }
        private void EmitWaitForSound(StepNode n)
        {
            var p=n.Props;int t=PropEx.GetInt(p,"threshold",90),m=PropEx.GetInt(p,"minDurationMs",60),to=PropEx.GetInt(p,"timeoutMs",20000);
            if(PropEx.GetString(p,"responseRoute","inline")=="splash")
            {
                int lo=PropEx.GetInt(p,"timeoutMinSec",18),hi=PropEx.GetInt(p,"timeoutMaxSec",22);
                if(lo>hi)(lo,hi)=(hi,lo);
                if(lo<1||hi>300){Error(n,"Splash timeout range must be between 1 and 300 seconds");return;}
                Emit(n,new[]{"WPROFILE|splash,"+(lo*1000)+","+(hi*1000)},"WPROFILE");return;
            }
            if(PropEx.GetBool(p,"armed")){int act=PropEx.GetString(p,"act","left")switch{"right"=>2,"middle"=>3,_=>1};var(r0,r1)=Pair(PropEx.GetInt(p,"reactMin",80),PropEx.GetInt(p,"reactMax",180));var(h0,h1)=Pair(PropEx.GetInt(p,"holdMin",30),PropEx.GetInt(p,"holdMax",90));Emit(n,new[]{"TRGSND|"+t+","+m+","+to+","+act+","+r0+","+r1+","+h0+","+h1},"TRGSND");return;}
            int id=PropEx.GetInt(p,"calibrationId",1);
            if(id is not (1 or 2)){Error(n,"sound calibration ID must be 1 or 2");return;}
            if(!_soundCalibrationIds.Add(id)){Error(n,"sound calibration ID "+id+" is already assigned to another enabled Wait For Sound step");return;}
            int peakMin=PropEx.GetInt(p,"peakMin",0),peakMax=PropEx.GetInt(p,"peakMax",511),priority=PropEx.GetInt(p,"soundPriority",0);
            if(peakMin<0||peakMax<1||peakMax>511||peakMin>peakMax){Error(n,"sound Peak range must satisfy 0 <= Min <= Max <= 511");return;}
            if(priority is < -100 or > 100){Error(n,"sound priority must be between -100 and 100");return;}
            Emit(n,new[]{"WSNDP|"+id+","+SoundBinding(n,id,t,m)+","+t+","+m+","+to+","+peakMin+","+peakMax+","+priority},"WSNDP");
        }
        private (int lo,int hi,int stable,int timeout,int mode) LuxArgs(Dictionary<string,object?> p){int c=PropEx.GetInt(p,"luxCenter",1250),tol=Math.Max(1,PropEx.GetInt(p,"luxTolerance",50));return(Math.Max(0,c-tol),c+tol,Math.Max(0,(int)Math.Round(PropEx.GetDouble(p,"stableSec",2)*1000)),PropEx.GetInt(p,"timeoutMs",20000),PropEx.GetString(p,"sampleMode","hires")=="lowres"?1:0);}
        private void EmitLabel(StepNode n){var name=PropEx.GetString(n.Props,"label","label1").Trim();if(name.Length==0||name.Contains('=')||name.Contains('|')){Error(n,"bad label name '"+name+"'");return;}if(!_labels.Add(name)){Error(n,"duplicate label '"+name+"'");return;}Emit(n,new[]{"LABEL|"+name},"LABEL");}
        private void EmitGoto(StepNode n){var name=PropEx.GetString(n.Props,"label").Trim();if(name.Length==0){Error(n,"Go To Label with no label chosen");return;}Emit(n,new[]{"GOTO|"+name},"GOTO");}
        private void EmitRaw(StepNode n){var cmd=PropEx.GetString(n.Props,"cmd","PING").Trim();if(cmd.Length==0||cmd.Contains('\n')||cmd.Contains('\r')){Error(n,"raw command must be one non-empty line");return;}Emit(n,new[]{"RAW|"+cmd},"RAW");}
        private void EmitRandomPackage(StepNode n){var kids=n.Children.Where(c=>!c.IsDisabled&&!IsMarker(c)).ToList();if(kids.Count==0){Error(n,"random package has no enabled children");return;}if(kids.Any(c=>Conditional.Contains(c.Type)&&PropEx.GetBool(c.Props,"insertIfElse"))){Error(n,"an If/Else structure cannot live inside a Random Package");return;}var mode=PropEx.GetString(n.Props,"mode","shuffleAll");int mn=1,mx=kids.Count;string em="all";if(mode=="randomSubset"){em="pick";mn=Math.Max(0,PropEx.GetInt(n.Props,"minCount",1));mx=Math.Min(kids.Count,PropEx.GetInt(n.Props,"maxCount",10));if(mn>mx)(mn,mx)=(mx,mn);}else if(mode!="shuffleAll"){Error(n,"unknown random package mode '"+mode+"'");return;}Lines.Add("RPKG|"+em+","+mn+","+mx);Count("RPKG");for(int i=0;i<kids.Count;i++){if(i>0)Lines.Add("PKGITEM");Walk(new List<StepNode>{kids[i]});}Lines.Add("ENDPKG");EmitDelay(n);}
        private static readonly HashSet<string> ParallelLeaf=new(){"randomMousePosition","mouseMove","mouseClick","mouseScroll","keystroke","keyDown","keyUp","typeText","delay","rawCommand","comment","buzzer"};
        private bool ParallelCompatible(StepNode n)
        {
            if(n.Type=="waitForSound")
                return !PropEx.GetBool(n.Props,"armed")&&!PropEx.GetBool(n.Props,"insertIfElse");
            if(n.Type is "forLoop" or "randomPackage")
                return n.Children.Where(c=>!c.IsDisabled&&!IsMarker(c)).All(ParallelCompatible);
            return ParallelLeaf.Contains(n.Type);
        }
        private void EmitParallelGroup(StepNode n){var kids=n.Children.Where(c=>!c.IsDisabled&&!IsMarker(c)).ToList();if(kids.Count<2){Error(n,"a Parallel Group needs at least two enabled branches");return;}var before=Errors.Count;foreach(var c in kids)if(!ParallelCompatible(c))Error(c,"'"+c.Type+"' cannot live inside a cooperative Parallel Group on the Pico (armed/If sound waits are also unsupported)");if(Errors.Count>before)return;Lines.Add("PGROUP");Count("PGROUP");for(int i=0;i<kids.Count;i++){if(i>0)Lines.Add("PARITEM");Walk(new List<StepNode>{kids[i]});}Lines.Add("ENDPAR");EmitDelay(n);}
        private static string QuoteRun(string value)=>value.Contains(' ')?"\""+value+"\"":value;
        private void EmitRunMacro(StepNode n,string command,string kind){string enc;try{enc=PctType(command);}catch(FormatException ex){Error(n,ex.Message);return;}Emit(n,new[]{"# "+kind,"KEY|combo=91+82|hold=40,90","DELAY|350,650","TYPE|text="+enc,"DELAY|140,260","KEY|combo=13|hold=40,90","DELAY|600,1200"},kind);}
        private void EmitLaunch(StepNode n,bool shellOpen){var p=n.Props;var path=PropEx.GetString(p,"path").Trim();if(path.Length==0){Error(n,"no path set");return;}var args=PropEx.GetString(p,"args");var state=PropEx.GetString(p,"windowState","normal");string cmd;if(state=="minimized")cmd="cmd /c start /min \"\" "+QuoteRun(path)+(args.Length>0?" "+args:"");else{if(state=="maximized")Flag(n,"'maximized' cannot be expressed through the Run box - launching visible/normal");cmd=QuoteRun(path)+(args.Length>0?" "+args:"");}EmitRunMacro(n,cmd,shellOpen?"openFile":"runExe");}
        private void EmitAudio(StepNode n){var p=n.Props;var path=PropEx.GetString(p,"path").Trim();if(path.Length==0){Error(n,"no audio path set");return;}if(PropEx.GetString(p,"mode","playerMacro")!="playerMacro"){Error(n,"playAudio mode is PC-only; use playerMacro on the Pico");return;}var esc=path.Replace("'","''");var cmd=path.EndsWith(".wav",StringComparison.OrdinalIgnoreCase)?"powershell -w hidden -c \"(New-Object Media.SoundPlayer '"+esc+"').PlaySync()\"":"powershell -w hidden -c \"Add-Type -AssemblyName presentationCore;$p=New-Object System.Windows.Media.MediaPlayer;$p.Open([uri]'"+esc+"');$p.Play()\"";EmitRunMacro(n,cmd,"playAudio");}
        private void EmitInclude(StepNode n){var raw=PropEx.GetString(n.Props,"path").Trim();if(raw.Length==0){Error(n,"no .amsj path set");return;}var sourceFull=Path.GetFullPath(_sourcePath);var full=Path.IsPathRooted(raw)?raw:Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFull)??".",raw));if(!File.Exists(full)){Error(n,"playScript child not found: "+raw);return;}var baseName=Path.GetFileNameWithoutExtension(full);if(baseName.Any(ch=>ch<32||ch>126||"/\\:|%".Contains(ch))){Error(n,"playScript file name cannot live on the Pico drive");return;}Emit(n,new[]{"INCLUDE|file="+baseName+".txt"},"INCLUDE");}

        private void EmitForLoop(StepNode n)
        {
            var p = n.Props;
            var mode = PropEx.GetString(p, "mode", "count");
            string head;
            if (mode == "time")
            {
                var unit = PropEx.GetString(p, "timeUnit", "minute");
                if (!TimeUnitSec.TryGetValue(unit, out var factor))
                {
                    Error(n, "unknown time unit '" + unit + "'");
                    return;
                }
                var sec = PropEx.GetInt(p, "timeValue", 10) * factor;
                if (sec <= 0)
                {
                    Error(n, "loop time must be positive (got " + PropEx.GetInt(p, "timeValue", 10) + " " + unit + ")");
                    return;
                }
                head = "LOOPTIME|" + sec;
            }
            else if (mode == "infinite")
            {
                head = "LOOP|0";
            }
            else if (mode == "count")
            {
                var cnt = PropEx.GetInt(p, "count", 10);
                if (cnt <= 0)
                {
                    Error(n, "loop count must be positive (got " + cnt + ") - use 'infinite' for forever");
                    return;
                }
                head = "LOOP|" + cnt;
            }
            else
            {
                Error(n, "unknown loop mode '" + mode + "'");
                return;
            }
            Lines.Add(head);
            Count("LOOP");
            Walk(n.Children);
            Lines.Add("ENDLOOP");
            EmitDelay(n);   // the app's delay lands after the whole loop
        }

        private void EmitWaitForLight(StepNode n){var p=n.Props;var(lo,hi,stable,to,mode)=LuxArgs(p);var head="WLIGHT|"+lo+","+hi+","+stable+","+to+","+mode;if(PropEx.GetBool(p,"armed")){var vk=Vk(n,PropEx.GetString(p,"key","E"),"waitForLight armed key");if(vk==0)return;var(h0,h1)=Pair(PropEx.GetInt(p,"holdMin",30),PropEx.GetInt(p,"holdMax",90));var(r0,r1)=Pair(PropEx.GetInt(p,"reactMin",80),PropEx.GetInt(p,"reactMax",180));head+="|key="+vk+","+h0+","+h1+"|react="+r0+","+r1;}Emit(n,new[]{head},"WLIGHT");}
    }

    /// <summary>Compiles the step tree into plan text. Throws PlanBlockedException when any step
    /// cannot run on PLAN|2 - nothing is written in that case (never a partial plan).</summary>
    public static PlanResult Compile(IList<StepNode> roots, AppSettings settings,
        int screenW, int screenH, string sourceName, string machine)
    {
        var gen = new Gen(settings, sourceName);
        gen.NumberTree(roots, "");
        gen.Walk(roots);

        var body = new List<string>(gen.Lines);
        // Play Options wrapper (hard rule): once = bare, times N = LOOP|N, timed = LOOPTIME|seconds
        var mode = settings.PlayRepeatMode;
        if (mode == "times")
        {
            if (settings.PlayRepeatTimes <= 0)
                gen.Errors.Add("settings: PlayRepeatTimes must be a positive integer (got " + settings.PlayRepeatTimes + ")");
            else
            {
                body.Insert(0, "LOOP|" + settings.PlayRepeatTimes);
                body.Add("ENDLOOP");
            }
        }
        else if (mode == "timed")
        {
            var unit = settings.PlayRepeatUnit;
            var sec = settings.PlayRepeatValue * (TimeUnitSec.TryGetValue(unit, out var f) ? f : 0);
            if (!TimeUnitSec.ContainsKey(unit) || sec <= 0)
                gen.Errors.Add("settings: bad timed repeat (" + settings.PlayRepeatValue + " " + unit + ")");
            else
            {
                body.Insert(0, "LOOPTIME|" + sec);
                body.Add("ENDLOOP");
            }
        }
        else if (mode != "once")
        {
            gen.Errors.Add("settings: unknown PlayRepeatMode '" + mode + "' (once|times|timed)");
        }
        if (settings.KeyboardBoard == "promicro")
        {
            gen.Flags.Add("settings: KeyboardBoard=promicro is bridge-mode only; on the portable plan "
                          + "the Pico types every keyboard step (keyboard=Pico contract)");
            body.Insert(0, "# FLAG " + gen.Flags[^1]);
        }
        if (gen.Errors.Count > 0) throw new PlanBlockedException(gen.Errors);

        var sb = new StringBuilder();
        sb.Append("PLAN|2\n");
        sb.Append("# generated by Classroom Studio PlanExporter (engine " + EngineVersion + ") from " + Path.GetFileName(sourceName) + "\n");
        sb.Append("# machine: " + machine + " · generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "\n");
        sb.Append("SCREEN|" + screenW + "," + screenH + "\n");
        sb.Append("SPEED|" + settings.MouseMoveSpeedMin + "," + settings.MouseMoveSpeedMax + "\n\n");
        foreach (var l in body) sb.Append(l).Append('\n');
        var text = sb.ToString();
        ValidatePlan(text);   // dogfood: a generator bug must never reach a drive
        return new PlanResult(text, gen.Flags, gen.Disabled,
                              gen.Counts.OrderBy(kv => kv.Key).Select(kv => kv.Key + " x" + kv.Value).ToList());
    }

    /// <summary>Writes plan.txt, every recursively compiled child plan, plan_engine.py + plan_motion.py + plan_typing.py (the split PLAN|2 runtime) and
    /// README-PLAN.md next to <paramref name="planPath"/>. Returns the written paths.</summary>
    private sealed record CompiledBundle(PlanResult Root, IReadOnlyList<(string FileName, string Text)> Children);

    private static AppSettings ChildSettings(AppSettings source) => new()
    {
        PlayRepeatMode = "once",
        MouseMoveSpeedMin = source.MouseMoveSpeedMin,
        MouseMoveSpeedMax = source.MouseMoveSpeedMax,
        TypeKeyMinMs = source.TypeKeyMinMs,
        TypeKeyMaxMs = source.TypeKeyMaxMs,
        KeyboardBoard = source.KeyboardBoard,
    };

    /// <summary>Compile a portable fragment exactly once, ignoring the root Play Options wrapper.</summary>
    public static PlanResult CompileOnce(IList<StepNode> roots, AppSettings settings,
        int screenW, int screenH, string sourceName, string machine)
        => Compile(roots, ChildSettings(settings), screenW, screenH, sourceName, machine);

    /// <summary>Preflights and compiles every reachable playScript document before the first
    /// destination is touched. Root depth is zero; four included levels are allowed.</summary>
    private static CompiledBundle CompileBundle(IList<StepNode> roots, AppSettings settings,
        int screenW, int screenH, string sourcePath, string machine, string rootOutputName)
    {
        var rootResult = Compile(roots, settings, screenW, screenH, sourcePath, machine);
        var children = new List<(string FileName, string Text)>();
        var errors = new List<string>();
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [rootOutputName] = Path.GetFullPath(sourcePath),
        };
        var active = new List<string> { Path.GetFullPath(sourcePath) };

        void Visit(IList<StepNode> nodes, string ownerPath, int ownerDepth)
        {
            foreach (var node in nodes)
            {
                if (node.IsDisabled) continue;
                if (node.Type == "playScript")
                {
                    var raw = PropEx.GetString(node.Props, "path").Trim();
                    if (raw.Length == 0) { errors.Add("include in " + Path.GetFileName(ownerPath) + ": no .amsj path set"); continue; }
                    var ownerDir = Path.GetDirectoryName(Path.GetFullPath(ownerPath)) ?? ".";
                    var full = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(ownerDir, raw));
                    if (!File.Exists(full)) { errors.Add("include in " + Path.GetFileName(ownerPath) + ": child not found: " + raw); continue; }
                    if (active.Contains(full, StringComparer.OrdinalIgnoreCase))
                    {
                        errors.Add("include cycle: " + string.Join(" -> ", active.Select(Path.GetFileName).Concat(new[] { Path.GetFileName(full) })));
                        continue;
                    }
                    if (ownerDepth >= 4)
                    {
                        errors.Add("include depth cap 4 exceeded at " + Path.GetFileName(full));
                        continue;
                    }
                    var outputName = Path.GetFileNameWithoutExtension(full) + ".txt";
                    if (outputName.Any(ch => ch < 32 || ch > 126 || "/\\:|%".Contains(ch)))
                    {
                        errors.Add("include output name is unsafe for CIRCUITPY: " + outputName);
                        continue;
                    }
                    if (outputSources.TryGetValue(outputName, out var prior) &&
                        !string.Equals(prior, full, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add("duplicate include output '" + outputName + "' from " + prior + " and " + full);
                        continue;
                    }
                    outputSources[outputName] = full;
                    if (completed.Contains(full)) continue; // same source may be referenced more than once; emit once

                    List<StepNode> childSteps;
                    try { childSteps = DocumentService.Load(full); }
                    catch (Exception ex) { errors.Add("include '" + raw + "' is not a valid .amsj: " + ex.Message); continue; }

                    PlanResult childResult;
                    try { childResult = Compile(childSteps, ChildSettings(settings), screenW, screenH, full, machine); }
                    catch (PlanBlockedException bx)
                    {
                        errors.AddRange(bx.Errors.Select(e => "include " + Path.GetFileName(full) + ": " + e));
                        continue;
                    }
                    children.Add((outputName, childResult.Text));
                    active.Add(full);
                    Visit(childSteps, full, ownerDepth + 1);
                    active.RemoveAt(active.Count - 1);
                    completed.Add(full);
                }
                Visit(node.Children, ownerPath, ownerDepth);
            }
        }

        Visit(roots, sourcePath, 0);
        if (errors.Count > 0) throw new PlanBlockedException(errors);
        return new CompiledBundle(rootResult, children);
    }

    public static IReadOnlyList<string> Export(string planPath, IList<StepNode> steps,
        AppSettings settings, int screenW, int screenH, string sourceName, string machine)
    {
        var full = Path.GetFullPath(planPath);
        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir)) throw new IOException("cannot resolve the folder of " + planPath);
        var bundle = CompileBundle(steps, settings, screenW, screenH, sourceName, machine, Path.GetFileName(full));
        var enginePath = Path.Combine(dir, "plan_engine.py");
        var motionPath = Path.Combine(dir, "plan_motion.py");
        var typingPath = Path.Combine(dir, "plan_typing.py");
        var readmePath = Path.Combine(dir, "README-PLAN.md");
        var payloads = new List<(string Path, string Text)> { (full, bundle.Root.Text) };
        payloads.AddRange(bundle.Children.Select(c => (Path.Combine(dir, c.FileName), c.Text)));
        payloads.Add((enginePath, BuildEnginePy()));
        payloads.Add((motionPath, BuildMotionPy()));
        payloads.Add((typingPath, BuildTypingPy()));
        payloads.Add((readmePath, BuildReadme(bundle.Root, Path.GetFileName(sourceName), machine)));

        var tx = Guid.NewGuid().ToString("N");
        var temps = payloads.Select(p => p.Path + "." + tx + ".tmp").ToArray();
        var backups = payloads.Select(p => p.Path + "." + tx + ".bak").ToArray();
        var published = new List<int>();
        try
        {
            // No destination is touched until every root/child/runtime/readme payload is staged.
            for (int i = 0; i < payloads.Count; i++)
                File.WriteAllText(temps[i], payloads[i].Text, new UTF8Encoding(false));
            for (int i = 0; i < payloads.Count; i++)
            {
                if (File.Exists(payloads[i].Path)) File.Move(payloads[i].Path, backups[i]);
                File.Move(temps[i], payloads[i].Path);
                published.Add(i);
            }
            foreach (var b in backups) if (File.Exists(b)) File.Delete(b);
            return payloads.Select(p => p.Path).ToList();
        }
        catch
        {
            // Restore every prior file, including a backup made just before the failing move.
            foreach (var i in published.AsEnumerable().Reverse())
                if (File.Exists(payloads[i].Path)) File.Delete(payloads[i].Path);
            for (int i = 0; i < payloads.Count; i++)
                if (File.Exists(backups[i])) File.Move(backups[i], payloads[i].Path, true);
            throw;
        }
        finally
        {
            foreach (var p in temps.Concat(backups))
                if (File.Exists(p)) File.Delete(p);
        }
    }

    /// <summary>The PLAN|2 plan engine, embedded verbatim (portable/plan3/CIRCUITPY/plan_engine.py).
    /// Normalized to LF so the written file is byte-stable regardless of the .cs line endings.</summary>
    public static string BuildEnginePy() => EngineTemplate.Replace("\r\n", "\n");
    public static string BuildMotionPy() => MotionTemplate.Replace("\r\n", "\n");
    public static string BuildTypingPy() => TypingTemplate.Replace("\r\n", "\n");

    private static string BuildReadme(PlanResult result, string sourceName, string machine)
    {
        var sb = new StringBuilder();
        sb.Append("# راهنمای پلن پرتابل پیکو (PLAN|2 - فریم‌ور 0.9.66)\n\n");
        sb.Append("همه‌ی فایل‌های این bundle را روی درایو CIRCUITPY کپی کن (کنار code.py از «Export Pico Firmware»):\n");
        sb.Append("- plan.txt ← پلن اصلی؛ فایل‌های *.txt دیگر ← playScriptهای کامپایل‌شده\n");
        sb.Append("- plan_engine.py + plan_motion.py + plan_typing.py ← runtime کامل کم‌حافظه‌ی PLAN|2\n\n");
        sb.Append("- منبع: " + sourceName + " · سیستم: " + machine + "\n");
        sb.Append("- استپ‌های کامپایل‌شده: " + (result.Counts.Count > 0 ? string.Join(" · ", result.Counts) : "-") + "\n");
        if (result.Disabled.Count > 0)
            sb.Append("- استپ‌های غیرفعال (شمرده شدند، اجرا نمی‌شوند): " + result.Disabled.Count + "\n");
        if (result.Flags.Count > 0)
        {
            sb.Append("\n## قیدها (FLAG)\n\n");
            foreach (var f in result.Flags) sb.Append("- " + f + "\n");
        }
        sb.Append("\n## اجرا روی برد\n\n");
        sb.Append("- Num Lock = شروع/توقف پلن · Scroll Lock = مکث/ادامه (همان کیپد روی برد)\n");
        sb.Append("- اگر کنسول «plan: load failed» + ۴ فلاش LED داد: نسخه‌ی هدر plan.txt با فریم‌ور نمی‌خواند ← دوباره اکسپورت کن.\n");
        sb.Append("- برای دیدن لاگ اجرا، PLAN_DEBUG را در code.py روشن کن و کنسول سریال را باز کن.\n");
        return sb.ToString();
    }

    /// <summary>Structural self-check mirroring the PLAN|2 parser: op whitelist, PLAN|2 first,
    /// loop balance, RMOUSE region quad, WLIGHT positionals, TYPE text=, DELAY/SCREEN/SPEED ints.
    /// A failure here is a generator BUG - it throws before anything reaches a file.</summary>
    private static void ValidatePlan(string text)
    {
        var stack = new List<(string Kind, bool ElseSeen, int Line)>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var gotos = new List<string>();
        var known = new HashSet<string>{"PLAN","SCREEN","SPEED","DELAY","LOOP","LOOPTIME","ENDLOOP","RMOUSE","MOVETO","CLICK","TYPE","WLIGHT","WSND","WSNDP","WPROFILE","TRGSND","IFSND","IFLUX","ELSE","ENDIF","KEY","KDOWN","KUP","WHEEL","LABEL","GOTO","RAW","HANDPATH","RPKG","PKGITEM","ENDPKG","PGROUP","PARITEM","ENDPAR","INCLUDE","BEEP"};
        bool first = true;
        string previousOp = "";
        var lines = text.Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var fields = line.Split('|');
            var op = fields[0];
            void Bad(string why) => throw new PlanBlockedException(new[] { "PlanExporter BUG: line " + (li + 1) + ": " + why + " (please report)" });
            void Need(string kind, string closer)
            {
                if (stack.Count == 0 || stack[^1].Kind != kind)
                    Bad(closer + " cannot close " + (stack.Count == 0 ? "the root" : stack[^1].Kind));
            }

            if (!known.Contains(op)) Bad("unknown op '" + op + "'");
            if (first && (op != "PLAN" || fields.Length < 2 || fields[1] != "2")) Bad("PLAN must be first with version 2");
            if (op == "PLAN" && !first) Bad("duplicate/non-first PLAN");

            switch (op)
            {
                case "LOOP": case "LOOPTIME": stack.Add(("LOOP", false, li + 1)); break;
                case "IFSND": case "IFLUX": stack.Add(("IF", false, li + 1)); break;
                case "RPKG": stack.Add(("RPKG", false, li + 1)); break;
                case "PGROUP": stack.Add(("PGROUP", false, li + 1)); break;
                case "ENDLOOP": Need("LOOP", op); stack.RemoveAt(stack.Count - 1); break;
                case "ELSE":
                    Need("IF", op);
                    if (stack[^1].ElseSeen) Bad("duplicate ELSE in one IF block");
                    stack[^1] = ("IF", true, stack[^1].Line);
                    break;
                case "ENDIF": Need("IF", op); stack.RemoveAt(stack.Count - 1); break;
                case "PKGITEM":
                    Need("RPKG", op);
                    if (previousOp is "RPKG" or "PKGITEM") Bad("empty Random Package item");
                    break;
                case "ENDPKG":
                    Need("RPKG", op);
                    if (previousOp is "RPKG" or "PKGITEM") Bad("empty Random Package item");
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case "PARITEM":
                    Need("PGROUP", op);
                    if (previousOp is "PGROUP" or "PARITEM") Bad("empty Parallel Group branch");
                    break;
                case "ENDPAR":
                    Need("PGROUP", op);
                    if (previousOp is "PGROUP" or "PARITEM") Bad("empty Parallel Group branch");
                    stack.RemoveAt(stack.Count - 1);
                    break;
            }

            if (op == "LABEL")
            {
                if (fields.Length < 2 || fields[1].Length == 0 || !labels.Add(fields[1])) Bad("bad/duplicate LABEL");
            }
            if (op == "GOTO")
            {
                if (fields.Length < 2 || fields[1].Length == 0) Bad("empty GOTO");
                gotos.Add(fields[1]);
            }
            if (op == "WSNDP")
            {
                if (fields.Length != 2) Bad("WSNDP needs one comma payload");
                var values = fields[1].Split(',');
                if (values.Length is not (5 or 8 or 10) || values[0] is not ("1" or "2")
                    || values[1].Length != 12 || values[1].Any(ch => !Uri.IsHexDigit(ch))
                    || !values.Skip(2).Take(values.Length == 5 ? 3 : 6).All(IsInt)
                    || (values.Length == 10 && (values[8].Length == 0
                        || values[8].Any(ch => ch < 32 || ch > 126 || "/\\:|%".Contains(ch))
                        || !IsInt(values[9]))))
                    Bad("bad WSNDP profile contract");
            }
            if (op == "WPROFILE")
            {
                if (fields.Length != 2) Bad("WPROFILE needs one comma payload");
                var values = fields[1].Split(',');
                if (values.Length != 3 || values[0] != "splash" || !IsInt(values[1]) || !IsInt(values[2]))
                    Bad("bad WPROFILE contract");
            }
            if (op == "INCLUDE" && (!HasKv(fields, "file", out var file) || file.Length == 0 ||
                file.Any(ch => ch < 32 || ch > 126 || "/\\:|%".Contains(ch)))) Bad("unsafe INCLUDE filename");
            if (op == "HANDPATH")
            {
                if (fields.Length != 2 || fields[1].Length == 0) Bad("HANDPATH needs samples");
                var samples = fields[1].Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (samples.Length is < 1 or > 2000) Bad("HANDPATH sample count out of range");
                foreach (var sample in samples)
                {
                    var values = sample.Split(',');
                    if (values.Length != 3 || !values.All(IsInt)) Bad("HANDPATH needs delay,dx,dy samples");
                    int delay = int.Parse(values[0], CultureInfo.InvariantCulture);
                    int dx = int.Parse(values[1], CultureInfo.InvariantCulture);
                    int dy = int.Parse(values[2], CultureInfo.InvariantCulture);
                    if (delay is < 1 or > 60_000 || Math.Abs((long)dx) > 8192 || Math.Abs((long)dy) > 8192)
                        Bad("HANDPATH sample out of range");
                }
            }
            first = false;
            previousOp = op;
        }
        if (stack.Count > 0)
            throw new PlanBlockedException(new[] { "PlanExporter BUG: line " + stack[^1].Line + ": unclosed " + stack[^1].Kind + " container (please report)" });
        foreach (var target in gotos)
            if (!labels.Contains(target)) throw new PlanBlockedException(new[] { "PlanExporter BUG: GOTO target '" + target + "' is undefined (please report)" });
    }

    private static bool IsInt(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private static bool IsIntPair(string s)
    {
        var parts = s.Split(',');
        return parts.Length is >= 1 and <= 2 && parts.All(IsInt);
    }

    private static bool HasKv(string[] fields, string key, out string value)
    {
        foreach (var f in fields.Skip(1))
            if (f.StartsWith(key + "=", StringComparison.Ordinal)) { value = f[(key.Length + 1)..]; return true; }
        value = "";
        return false;
    }

    // Hardware-proven split runtime. Generated from the canonical engine; do not hand-edit.
    private const string EngineTemplate = """"
# Generated memory-fit core from canonical plan_engine.py; do not hand-edit.
import time
import math
import random

class PlanAbort(Exception):
    pass

def _load_mouse_pos(ctx, ops):
    sw, sh = (ctx.screen_w, ctx.screen_h)
    for op, prm in ops:
        if op == 'SCREEN':
            sw, sh = prm['v']
            break
    saved = None
    getter = getattr(ctx, 'get_mouse_pos', None)
    if getter is not None:
        try:
            saved = getter()
        except Exception:
            saved = None
    if saved is None or len(saved) < 2:
        return [sw // 2, sh // 2]
    return [_clamp(int(saved[0]), 0, max(0, sw - 1)), _clamp(int(saved[1]), 0, max(0, sh - 1))]

def _save_mouse_pos(ctx, pos):
    setter = getattr(ctx, 'set_mouse_pos', None)
    if setter is not None:
        setter(int(pos[0]), int(pos[1]))

def _below(n):
    return random.randrange(n) if n > 0 else 0

def rand_range(mn, mx):
    if mx < mn:
        mn, mx = (mx, mn)
    if mx <= 0:
        return 0
    return mn if mx <= mn else random.randint(mn, mx)

def _clamp(v, lo, hi):
    return lo if v < lo else hi if v > hi else v

def pct_dec(s):
    out = []
    i = 0
    while i < len(s):
        if s[i] == '%' and i + 2 < len(s) + 1 and (i + 2 <= len(s) - 1):
            try:
                out.append(chr(int(s[i + 1:i + 3], 16)))
                i += 3
                continue
            except Exception:
                pass
        out.append(s[i])
        i += 1
    return ''.join(out)
_OPS = ('PLAN', 'SCREEN', 'SPEED', 'RMOUSE', 'CLICK', 'TYPE', 'DELAY', 'LOOP', 'LOOPTIME', 'ENDLOOP', 'WLIGHT', 'STATELOOP', 'MOVETO', 'KEY', 'KDOWN', 'KUP', 'WHEEL', 'RAW', 'WSND', 'TRGSND', 'IFSND', 'IFLUX', 'ELSE', 'ENDIF', 'LABEL', 'GOTO', 'INCLUDE', 'RPKG', 'PKGITEM', 'ENDPKG', 'PGROUP', 'PARITEM', 'ENDPAR', 'BEEP')
_V2_OPS = frozenset(_OPS[11:])

def _pair(s, what, line_no):
    parts = s.split(',')
    try:
        a, b = (int(parts[0]), int(parts[1]))
    except Exception:
        raise ValueError("line %d: bad %s '%s'" % (line_no, what, s))
    return (a, b) if b >= a else (b, a)

def _quad(s, what, line_no):
    parts = s.split(',')
    if len(parts) != 4:
        raise ValueError("line %d: bad %s '%s'" % (line_no, what, s))
    try:
        return tuple((int(p) for p in parts))
    except Exception:
        raise ValueError("line %d: bad %s '%s'" % (line_no, what, s))

def _link_blocks(ops):
    stack = []
    labels = {}
    for i, (op, prm) in enumerate(ops):
        if op in ('LOOP', 'LOOPTIME', 'IFSND', 'IFLUX'):
            stack.append(i)
        elif op == 'ENDLOOP':
            ops[stack.pop()][1]['end_ip'] = i
        elif op == 'ELSE':
            ops[stack[-1]][1]['else_line'] = i
        elif op == 'ENDIF':
            sp = ops[stack.pop()][1]
            sp['endif_ip'] = i + 1
            if 'else_line' in sp:
                ops[sp['else_line']][1]['endif_ip'] = i + 1
                sp['else_ip'] = sp['else_line'] + 1
            else:
                sp['else_ip'] = i + 1
        elif op == 'LABEL':
            if prm['name'] in labels:
                raise ValueError("duplicate LABEL '%s'" % prm['name'])
            labels[prm['name']] = i
    for op, prm in ops:
        if op != 'GOTO':
            continue
        lip = labels.get(prm['name'])
        if lip is None:
            raise ValueError("GOTO '%s' has no matching LABEL" % prm['name'])
        gc = prm.get('_chain', ())
        lc = ops[lip][1].get('_chain', ())
        if lc != gc[:len(lc)]:
            raise ValueError("GOTO '%s': the label is not visible from this block" % prm['name'])
_PAR_OK = ('MOVETO', 'CLICK', 'KEY', 'KDOWN', 'KUP', 'WHEEL', 'TYPE', 'DELAY', 'RAW', 'BEEP')
_PKG_MODES = ('pick', 'all', 'seq')

def _extract_containers(text):
    lines = text.split('\n')
    out = []
    table = []
    i = 0
    while i < len(lines):
        head = lines[i].strip()
        kind = head.split('|')[0].upper()
        if kind not in ('RPKG', 'PGROUP'):
            if kind in ('PKGITEM', 'ENDPKG', 'PARITEM', 'ENDPAR'):
                raise ValueError('line %d: %s outside a package/parallel block' % (i + 1, kind))
            out.append(lines[i])
            i += 1
            continue
        sep = 'PKGITEM' if kind == 'RPKG' else 'PARITEM'
        end = 'ENDPKG' if kind == 'RPKG' else 'ENDPAR'
        items = [[]]
        depth = 0
        closed = False
        j = i + 1
        while j < len(lines):
            k = lines[j].strip().split('|')[0].upper()
            if k in ('RPKG', 'PGROUP'):
                depth += 1
            elif k in ('ENDPKG', 'ENDPAR'):
                if depth == 0:
                    if k != end:
                        raise ValueError('line %d: %s closes a %s block' % (j + 1, k, kind))
                    closed = True
                    break
                depth -= 1
            elif k == sep and depth == 0:
                items.append([])
                j += 1
                continue
            items[-1].append(lines[j])
            j += 1
        if not closed:
            raise ValueError('line %d: %s is never closed with %s' % (i + 1, kind, end))
        table.append(['\n'.join(b) for b in items])
        out.append(head + (',' if '|' in head else '|') + '#%d' % (len(table) - 1))
        i = j + 1
    return ('\n'.join(out), table)

def _parse_v3(op, fields, prm, line_no, ctab):
    if op in ('PKGITEM', 'ENDPKG', 'PARITEM', 'ENDPAR'):
        raise ValueError('line %d: stray %s' % (line_no, op))
    body = fields[1] if len(fields) > 1 else ''
    if op == 'BEEP':
        parts = body.split(',')
        if len(parts) != 2:
            raise ValueError('line %d: BEEP needs freq,ms' % line_no)
        try:
            prm['v'] = (int(parts[0]), int(parts[1]))
        except Exception:
            raise ValueError("line %d: bad BEEP '%s'" % (line_no, body))
        if not 30 <= prm['v'][0] <= 20000 or prm['v'][1] < 0:
            raise ValueError('line %d: BEEP out of range (30..20000 Hz)' % line_no)
        return
    parts = body.split(',')
    if not parts[-1].startswith('#'):
        raise ValueError('line %d: %s must open a block body' % (line_no, op))
    bodies = ctab[int(parts[-1][1:])]
    progs = [parse_plan('PLAN|2\n' + b) for b in bodies]
    if op == 'RPKG':
        if len(parts) != 4:
            raise ValueError('line %d: RPKG needs mode,min,max' % line_no)
        mode = parts[0].strip().lower()
        if mode not in _PKG_MODES:
            raise ValueError("line %d: unknown RPKG mode '%s' (pick|all|seq)" % (line_no, parts[0]))
        try:
            mn, mx = (int(parts[1]), int(parts[2]))
        except Exception:
            raise ValueError("line %d: bad RPKG counts '%s'" % (line_no, body))
        if not progs:
            raise ValueError('line %d: RPKG has no items' % line_no)
        if mn < 0 or mx < mn or mx > len(progs):
            raise ValueError('line %d: RPKG counts out of range (%d items)' % (line_no, len(progs)))
        prm['mode'], prm['mn'], prm['mx'], prm['progs'] = (mode, mn, mx, progs)
        return
    if len(parts) != 1:
        raise ValueError('line %d: PGROUP takes no arguments' % line_no)
    if len(progs) < 2:
        raise ValueError('line %d: PGROUP needs at least two branches' % line_no)
    for b in progs:
        for o, _p in b:
            if o != 'PLAN' and o not in _PAR_OK:
                raise ValueError('line %d: %s is not allowed inside PGROUP' % (line_no, o))
    prm['progs'] = progs

def parse_plan(text):
    text, _ctab = _extract_containers(text)
    ops = []
    loop_stack = []
    else_seen = set()
    for line_no, raw in enumerate(text.split('\n'), 1):
        line = raw.strip()
        if not line or line.startswith('#'):
            continue
        fields = line.split('|')
        op = fields[0].upper()
        if op not in _OPS:
            raise ValueError("line %d: unknown op '%s'" % (line_no, fields[0]))
        prm = {}
        prm['_chain'] = tuple((b[2] for b in loop_stack))
        if op in ('RPKG', 'PGROUP', 'BEEP', 'PKGITEM', 'ENDPKG', 'PARITEM', 'ENDPAR'):
            _parse_v3(op, fields, prm, line_no, _ctab)
            ops.append((op, prm))
            continue
        if op in ('PLAN', 'SCREEN', 'SPEED', 'DELAY', 'LOOP', 'LOOPTIME'):
            body = fields[1] if len(fields) > 1 else ''
            if op == 'PLAN':
                try:
                    prm['v'] = int(body or '0')
                except Exception:
                    raise ValueError('line %d: bad PLAN version' % line_no)
                if prm['v'] not in (1, 2):
                    raise ValueError('line %d: unsupported PLAN version %d' % (line_no, prm['v']))
            elif op == 'SCREEN':
                parts = body.split(',')
                try:
                    prm['v'] = (int(parts[0]), int(parts[1]))
                except Exception:
                    raise ValueError("line %d: bad SCREEN '%s'" % (line_no, body))
            elif op == 'SPEED':
                prm['v'] = _pair(body, op, line_no)
            elif op == 'DELAY':
                prm['v'] = _pair(body + (',' + body if ',' not in body else ''), op, line_no)
            elif op == 'LOOP':
                prm['n'] = int(body or '1')
                loop_stack.append((op, line_no, len(ops)))
            else:
                prm['sec'] = float(body or '0')
                loop_stack.append((op, line_no, len(ops)))
        elif op == 'ENDLOOP':
            if not loop_stack or loop_stack[-1][0] in ('IFSND', 'IFLUX'):
                raise ValueError('line %d: ENDLOOP without LOOP' % line_no)
            loop_stack.pop()
        elif op == 'ENDIF':
            if not loop_stack or loop_stack[-1][0] not in ('IFSND', 'IFLUX'):
                raise ValueError('line %d: ENDIF without IFSND/IFLUX' % line_no)
            loop_stack.pop()
        elif op == 'ELSE':
            if not loop_stack or loop_stack[-1][0] not in ('IFSND', 'IFLUX'):
                raise ValueError('line %d: ELSE outside an IF block' % line_no)
            if loop_stack[-1][1] in else_seen:
                raise ValueError('line %d: duplicate ELSE in one IF block' % line_no)
            else_seen.add(loop_stack[-1][1])
        elif op == 'STATELOOP':
            vals = {}
            for kv in fields[1:]:
                if '=' not in kv:
                    raise ValueError('line %d: STATELOOP wants key=value' % line_no)
                k, v = kv.split('=', 1)
                vals[k.strip().lower()] = v.strip()
            try:
                prm['poll'] = max(25, int(vals.get('poll', '250')))
                prm['stable'] = max(0, int(vals.get('stable', '750')))
                prm['hysteresis'] = max(0, int(vals.get('hysteresis', '0')))
                prm['timeout'] = max(prm['poll'], int(vals.get('timeout', '1500')))
            except Exception:
                raise ValueError('line %d: bad STATELOOP timing' % line_no)
            raw_routes = vals.get('routes', '')
            routes = []
            for raw_route in raw_routes.split(','):
                bits = raw_route.split(':')
                if len(bits) != 4 or not bits[0] or (not bits[3].endswith('.txt')):
                    raise ValueError("line %d: bad STATELOOP route '%s'" % (line_no, raw_route))
                try:
                    lo, hi = (int(bits[1]), int(bits[2]))
                except Exception:
                    raise ValueError('line %d: bad STATELOOP lux range' % line_no)
                if hi < lo:
                    lo, hi = (hi, lo)
                routes.append({'id': bits[0], 'lo': lo, 'hi': hi, 'file': bits[3]})
            if not routes:
                raise ValueError('line %d: STATELOOP needs routes=' % line_no)
            prm['routes'] = routes
            prm['fallback'] = vals.get('fallback', 'STOP').upper()
            if prm['fallback'] not in ('STOP', 'FIRST'):
                raise ValueError('line %d: STATELOOP fallback must be STOP or FIRST' % line_no)
        elif op in ('WSND', 'TRGSND', 'IFSND', 'IFLUX'):
            pa = fields[1].split(',') if len(fields) > 1 else []
            need = {'WSND': 3, 'IFSND': 3, 'IFLUX': 5, 'TRGSND': 8}[op]
            if len(pa) < need:
                raise ValueError('line %d: %s needs %d comma numbers' % (line_no, op, need))
            try:
                prm['a'] = [int(x) for x in pa[:need]]
            except Exception:
                raise ValueError('line %d: bad %s numbers' % (line_no, op))
            if op in ('IFSND', 'IFLUX'):
                loop_stack.append((op, line_no, len(ops)))
        elif op in ('KDOWN', 'KUP', 'WHEEL'):
            try:
                prm['v'] = int(fields[1]) if len(fields) > 1 else 0
            except Exception:
                raise ValueError('line %d: %s needs a number' % (line_no, op))
            if op != 'WHEEL' and (not 0 < prm['v'] < 256):
                raise ValueError('line %d: %s vk out of range' % (line_no, op))
        elif op in ('LABEL', 'GOTO'):
            nm = fields[1].strip() if len(fields) > 1 else ''
            if not nm or '=' in nm:
                raise ValueError('line %d: %s needs a plain name' % (line_no, op))
            prm['name'] = nm
        elif op == 'RAW':
            prm['line'] = '|'.join(fields[1:]).strip()
            if not prm['line']:
                raise ValueError('line %d: RAW needs a board line' % line_no)
        elif op == 'WLIGHT':
            pos = fields[1].split(',') if len(fields) > 1 else []
            if len(pos) < 5:
                raise ValueError('line %d: WLIGHT needs lo,hi,stable,to,mode' % line_no)
            try:
                prm['lo'], prm['hi'] = (int(pos[0]), int(pos[1]))
                prm['stable'], prm['to'], prm['mode'] = (int(pos[2]), int(pos[3]), int(pos[4]))
            except Exception:
                raise ValueError('line %d: bad WLIGHT numbers' % line_no)
            for kv in fields[2:]:
                if '=' not in kv:
                    raise ValueError("line %d: bad arg '%s'" % (line_no, kv))
                k, v = kv.split('=', 1)
                if k == 'key':
                    t = v.split(',')
                    prm['key'] = (int(t[0]), int(t[1]) if len(t) > 1 else 30, int(t[2]) if len(t) > 2 else 90)
                elif k == 'react':
                    prm['react'] = _pair(v, k, line_no)
                else:
                    raise ValueError("line %d: unknown WLIGHT key '%s'" % (line_no, k))
        else:
            for kv in fields[1:]:
                if '=' not in kv:
                    raise ValueError("line %d: bad arg '%s' (want key=value)" % (line_no, kv))
                k, v = kv.split('=', 1)
                prm[k] = v
            if op == 'RMOUSE':
                if 'region' not in prm:
                    raise ValueError('line %d: RMOUSE needs region=x,y,w,h' % line_no)
                prm['region'] = _quad(prm['region'], 'region', line_no)
                for k in ('mt', 'curve', 'before', 'after'):
                    if k in prm:
                        prm[k] = _pair(prm[k], k, line_no)
                if 'mid' in prm:
                    c, _, r = prm['mid'].partition(':')
                    prm['mid'] = (int(c), _pair(r, 'mid', line_no))
                if 'idle' in prm:
                    e, _, r = prm['idle'].partition(':')
                    prm['idle'] = (_pair(e, 'idle-every', line_no), _pair(r, 'idle-pause', line_no))
                if 'over' in prm:
                    prm['over'] = int(prm['over'])
            elif op == 'CLICK':
                prm['btn'] = prm.get('btn', 'left')
                prm['n'] = int(prm.get('n', '1'))
                if 'hold' in prm:
                    prm['hold'] = _pair(prm['hold'], 'hold', line_no)
            elif op == 'TYPE':
                if 'text' not in prm:
                    raise ValueError('line %d: TYPE needs text=' % line_no)
                prm['text'] = pct_dec(prm['text'])
                for k in ('h', 'w', 'p', 'typo', 'typos'):
                    if k in prm:
                        prm[k] = _pair(prm[k], k, line_no)
                if 'think' in prm:
                    c, _, r = prm['think'].partition(':')
                    prm['think'] = (int(c), _pair(r, 'think', line_no))
                if 'wp' in prm:
                    prm['wp'] = int(prm['wp'])
            elif op == 'MOVETO':
                if 'x' not in prm or 'y' not in prm:
                    raise ValueError('line %d: MOVETO needs x= and y=' % line_no)
                try:
                    prm['x'] = int(prm['x'])
                    prm['y'] = int(prm['y'])
                    prm['human'] = int(prm.get('human', '1'))
                except Exception:
                    raise ValueError('line %d: bad MOVETO numbers' % line_no)
                for k in ('mt', 'curve', 'before', 'after'):
                    if k in prm:
                        prm[k] = _pair(prm[k], k, line_no)
                if 'mid' in prm:
                    c, _, r = prm['mid'].partition(':')
                    prm['mid'] = (int(c), _pair(r, 'mid', line_no))
                if 'idle' in prm:
                    e, _, r = prm['idle'].partition(':')
                    prm['idle'] = (_pair(e, 'idle-every', line_no), _pair(r, 'idle-pause', line_no))
                if 'over' in prm:
                    prm['over'] = int(prm['over'])
            elif op == 'KEY':
                if 'combo' not in prm:
                    raise ValueError('line %d: KEY needs combo=vk+vk' % line_no)
                try:
                    prm['combo'] = [int(v) for v in prm['combo'].split('+') if v != '']
                except Exception:
                    raise ValueError('line %d: bad KEY combo' % line_no)
                if not prm['combo'] or any((v <= 0 or v > 255 for v in prm['combo'])):
                    raise ValueError('line %d: bad KEY combo' % line_no)
                if 'hold' in prm:
                    prm['hold'] = _pair(prm['hold'], 'hold', line_no)
            elif op == 'INCLUDE':
                nm = prm.get('file', '')
                if not nm or any((ch in nm for ch in '/\\:')) or (not nm.lower().endswith('.txt')):
                    raise ValueError('line %d: INCLUDE needs a plain *.txt file name' % line_no)
        ops.append((op, prm))
    if loop_stack:
        _k, _ln, _ = loop_stack[-1]
        raise ValueError('line %d: %s without %s' % (_ln, _k, 'ENDIF' if _k in ('IFSND', 'IFLUX') else 'ENDLOOP'))
    if not ops or ops[0][0] != 'PLAN':
        raise ValueError('plan must start with PLAN|1 or PLAN|2')
    _link_blocks(ops)
    return ops

class PausePlanner:

    def __init__(self):
        self.moves_since = 0
        self.next_idle_at = -1

    def mid_pause(self, c):
        if c['mid_chance'] > 0 and _below(100) < c['mid_chance']:
            return rand_range(c['mid_min'], c['mid_max'])
        return 0

    def roll_long(self, c):
        if c['idle_pause_max'] <= 0 or c['idle_every_max'] <= 0:
            return 0
        self.moves_since += 1
        if self.next_idle_at < 0:
            self.next_idle_at = max(1, rand_range(c['idle_every_min'], c['idle_every_max']))
        if self.moves_since < self.next_idle_at:
            return 0
        self.moves_since = 0
        self.next_idle_at = max(1, rand_range(c['idle_every_min'], c['idle_every_max']))
        return rand_range(c['idle_pause_min'], c['idle_pause_max'])

class _LightStateChanged(Exception):

    def __init__(self, state_id):
        self.state_id = state_id

class _LiveLightSession:

    def __init__(self, prm, ctx):
        try:
            from live_light_guard import LightStateGuard, state_spec
        except ImportError as exc:
            raise ValueError('STATELOOP needs live_light_guard.py in the portable bundle') from exc
        self.ctx = ctx
        self.prm = prm
        self.routes = {r['id']: r for r in prm['routes']}
        specs = [state_spec(r['id'], r['lo'], r['hi'], r['file']) for r in prm['routes']]
        self.guard = LightStateGuard(specs, prm['stable'], prm['hysteresis'], prm['timeout'])
        self.current = None
        self.next_poll = -1

    def _lux(self):
        reader = getattr(self.ctx, 'read_lux', None)
        if reader is None:
            reader = getattr(self.ctx, 'light_lux', None)
        if reader is not None:
            try:
                value = reader()
                return None if value is None else float(value)
            except Exception:
                return None
        waiter = getattr(self.ctx, 'wait_light', None)
        if waiter is None:
            return None
        for route in self.prm['routes']:
            try:
                if waiter(route['lo'], route['hi'], 0, self.prm['poll'], 0):
                    return (route['lo'] + route['hi']) / 2.0
            except Exception:
                return None
        return None

    def poll(self, force=False):
        now = int(self.ctx.now() * 1000)
        if not force and self.next_poll > now:
            return
        self.next_poll = now + self.prm['poll']
        state_id = self.guard.update(self._lux(), now)
        if state_id is None:
            if self.current is not None:
                self.ctx.log('light guard unsafe - stopping')
            raise PlanAbort()
        if self.current is None:
            self.current = state_id
            self.ctx.log('light state -> ' + state_id)
        elif state_id != self.current:
            old = self.current
            self.current = state_id
            self.ctx.log('light state %s -> %s' % (old, state_id))
            raise _LightStateChanged(state_id)

def _run_state_loop(prm, ctx, pos, pauses, inc):
    session = _LiveLightSession(prm, ctx)
    session.poll(True)
    while True:
        route = session.routes.get(session.current)
        if route is None:
            if prm['fallback'] == 'FIRST':
                route = prm['routes'][0]
                session.current = route['id']
            else:
                raise PlanAbort()
        try:
            sub = parse_plan(ctx.read_plan_file(route['file']))
        except Exception as exc:
            ctx.log('light route failed: ' + str(exc))
            raise PlanAbort()
        setattr(ctx, '_live_light_guard', session)
        try:
            run_plan(sub, ctx, _pos=pos, _pauses=pauses, _inc=inc + (route['file'],))
        except _LightStateChanged:
            continue
        finally:
            if getattr(ctx, '_live_light_guard', None) is session:
                delattr(ctx, '_live_light_guard')
        session.poll(True)

def run_plan(ops, ctx, _pos=None, _pauses=None, _inc=()):
    if any((o in _V2_OPS for o, _ in ops)) and getattr(ctx, 'plan_api', 1) < 2:
        raise ValueError('this plan uses v2 ops but the firmware ctx is plan_api 1 - flash code65+')
    pauses = _pauses if _pauses is not None else PausePlanner()
    pos = _pos if _pos is not None else _load_mouse_pos(ctx, ops)
    labels = {}
    for _li, (_o, _p) in enumerate(ops):
        if _o == 'LABEL':
            labels[_p['name']] = _li
    inc = tuple(_inc)
    i = 0
    stack = []
    while i < len(ops):
        op, prm = ops[i]
        _gate = getattr(ctx, 'gate', None)
        if _gate is not None and (not _gate()):
            raise PlanAbort()
        _live_guard = getattr(ctx, '_live_light_guard', None)
        if _live_guard is not None:
            _live_guard.poll()
        if op == 'PLAN':
            pass
        elif op == 'SCREEN':
            ctx.screen_w, ctx.screen_h = prm['v']
            _sr = getattr(ctx, 'setres', None)
            if _sr is not None:
                _sr(prm['v'][0], prm['v'][1])
            pos[0] = _clamp(pos[0], 0, max(0, ctx.screen_w - 1))
            pos[1] = _clamp(pos[1], 0, max(0, ctx.screen_h - 1))
            _save_mouse_pos(ctx, pos)
        elif op == 'SPEED':
            ctx.speed_min, ctx.speed_max = prm['v']
        elif op == 'DELAY':
            if not ctx.sleep_ms(rand_range(*prm['v'])):
                raise PlanAbort()
        elif op == 'LOOP':
            stack.append([i, prm['n'], None])
        elif op == 'LOOPTIME':
            stack.append([i, 0, ctx.now() + prm['sec']])
        elif op == 'ENDLOOP':
            top = stack[-1]
            if top[2] is not None:
                if ctx.now() < top[2]:
                    i = top[0]
                else:
                    stack.pop()
            elif top[1] == 0:
                i = top[0]
            else:
                top[1] -= 1
                if top[1] > 0:
                    i = top[0]
                else:
                    stack.pop()
        elif op == 'RMOUSE':
            _exec_rmouse(prm, ctx, pauses, pos)
        elif op == 'CLICK':
            hold = prm.get('hold', (0, 0))
            ctx.mclick(prm['btn'], prm['n'], hold[0], hold[1])
        elif op == 'TYPE':
            for cmd in plan_typing(prm['text'], prm):
                if cmd[0] == 'KTEXT':
                    ctx.ktext(cmd[1], cmd[2], cmd[3])
                elif cmd[0] == 'DLY':
                    if not ctx.sleep_ms(cmd[1]):
                        raise PlanAbort()
                else:
                    ctx.kcombo(cmd[1])
        elif op == 'STATELOOP':
            _run_state_loop(prm, ctx, pos, pauses, inc)
        elif op == 'WLIGHT':
            ok = ctx.wait_light(prm['lo'], prm['hi'], prm['stable'], prm['to'], prm['mode'])
            if ok and 'key' in prm:
                vk, hmn, hmx = prm['key']
                rmn, rmx = prm.get('react', (80, 180))
                if not ctx.sleep_ms(rand_range(rmn, rmx)):
                    raise PlanAbort()
                ctx.key(vk, rand_range(hmn, hmx))
            ctx.log('wlight ' + ('match' if ok else 'timeout'))
        elif op == 'WSND':
            r = ctx.wait_sound(prm['a'][0], prm['a'][1], prm['a'][2])
            if r is None:
                raise PlanAbort()
            ctx.log('wsnd ' + ('heard' if r else 'timeout - continue'))
        elif op == 'IFSND':
            r = ctx.wait_sound(prm['a'][0], prm['a'][1], prm['a'][2])
            if r is None:
                raise PlanAbort()
            ctx.log('ifsnd ' + ('heard -> then' if r else 'timeout -> else'))
            if not r:
                i = prm['else_ip']
                continue
        elif op == 'TRGSND':
            a = prm['a']
            r = ctx.trg_sound(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7])
            if r is None:
                raise PlanAbort()
            ctx.log('trgsnd ' + ('fired' if r else 'timeout - continue'))
        elif op == 'IFLUX':
            ok2 = ctx.wait_light(prm['a'][0], prm['a'][1], prm['a'][2], prm['a'][3], prm['a'][4])
            ctx.log('iflux ' + ('match -> then' if ok2 else 'timeout -> else'))
            if not ok2:
                i = prm['else_ip']
                continue
        elif op == 'ELSE':
            i = prm['endif_ip']
            continue
        elif op == 'ENDIF':
            pass
        elif op == 'MOVETO':
            _exec_rmouse(prm, ctx, pauses, pos, (prm['x'], prm['y']))
        elif op == 'KEY':
            hold = prm.get('hold', (0, 0))
            ctx.key_combo(prm['combo'], hold[0], hold[1])
        elif op == 'KDOWN':
            ctx.kdown(prm['v'])
        elif op == 'KUP':
            ctx.kup(prm['v'])
        elif op == 'WHEEL':
            ctx.wheel(prm['v'])
        elif op == 'RAW':
            if ctx.raw(prm['line']) is None:
                raise PlanAbort()
        elif op == 'LABEL':
            pass
        elif op == 'GOTO':
            lip = labels.get(prm['name'])
            if lip is None:
                ctx.log('goto: label not found: ' + prm['name'] + ' - plan stopped')
                return
            while stack and (not stack[-1][0] < lip < ops[stack[-1][0]][1].get('end_ip', 1 << 30)):
                stack.pop()
            i = lip
            continue
        elif op == 'RPKG':
            progs = prm['progs']
            order = list(range(len(progs)))
            if prm['mode'] != 'seq':
                for _k in range(len(order) - 1, 0, -1):
                    _j = _below(_k + 1)
                    order[_k], order[_j] = (order[_j], order[_k])
                if prm['mode'] == 'pick':
                    order = order[:rand_range(prm['mn'], prm['mx'])]
            ctx.log('package: %d of %d' % (len(order), len(progs)))
            for _ix in order:
                run_plan(progs[_ix], ctx, _pos=pos, _pauses=pauses, _inc=inc)
        elif op == 'PGROUP':
            _rr = [[o for o in p if o[0] != 'PLAN'] for p in prm['progs']]
            while True:
                _live = False
                for _b in _rr:
                    if _b:
                        _live = True
                        run_plan([_b.pop(0)], ctx, _pos=pos, _pauses=pauses, _inc=inc)
                if not _live:
                    break
        elif op == 'BEEP':
            _bp = getattr(ctx, 'beep', None)
            if _bp is None:
                raise ValueError('BEEP needs a plan_api>=3 firmware (buzzer pin)')
            _bp(prm['v'][0], prm['v'][1])
        elif op == 'INCLUDE':
            nm = prm['file']
            if nm in inc:
                ctx.log('include cycle: ' + nm + ' - skipped')
            elif len(inc) >= 4:
                ctx.log('include depth cap (4): ' + nm + ' - skipped')
            else:
                try:
                    sub = parse_plan(ctx.read_plan_file(nm))
                except Exception as exc:
                    ctx.log('include failed: ' + nm + ': ' + str(exc))
                else:
                    run_plan(sub, ctx, _pos=pos, _pauses=pauses, _inc=inc + (nm,))
        i += 1
_motion_module = None
_typing_module = None

def _exec_rmouse(prm, ctx, pauses, pos, target=None):
    global _motion_module
    if _motion_module is None:
        import plan_motion as _motion_module
        _motion_module.PlanAbort = PlanAbort
    return _motion_module._exec_rmouse(prm, ctx, pauses, pos, target)

def plan_typing(text, prm):
    global _typing_module
    if _typing_module is None:
        import plan_typing as _typing_module
    return _typing_module.plan_typing(text, prm)

"""";
    private const string MotionTemplate = """"
# Generated from canonical plan_engine.py; do not hand-edit.
import time
import math
import random

def _save_mouse_pos(ctx, pos):
    setter = getattr(ctx, 'set_mouse_pos', None)
    if setter is not None:
        setter(int(pos[0]), int(pos[1]))

def _rf():
    return random.random()

def _below(n):
    return random.randrange(n) if n > 0 else 0

def rand_range(mn, mx):
    if mx < mn:
        mn, mx = (mx, mn)
    if mx <= 0:
        return 0
    return mn if mx <= mn else random.randint(mn, mx)

def _clamp(v, lo, hi):
    return lo if v < lo else hi if v > hi else v

def tuned_wind(dist, curve):
    cv = 0.3 if curve < 0 else _clamp(curve, 0.0, 2.0)
    base = 0.3 + cv * 3.0 if cv < 1.0 else 3.3 + (cv - 1.0) * 4.7
    return base * _clamp(dist / 400.0, 0.35, 1.0)

def build_range_profile(mn, mx, knot_count, low_ends=False):
    if mx < mn:
        mn, mx = (mx, mn)
    knot_count = int(_clamp(knot_count, 2, 12))
    if abs(mx - mn) < 1e-09:
        return [float(mn)] * knot_count
    span = mx - mn
    knots = []
    upper = _below(2) == 1
    for i in range(knot_count):
        force_low = low_ends and (i == 0 or i == knot_count - 1)
        band = _rf() * 0.18 if force_low else 0.6 + _rf() * 0.4 if upper else _rf() * 0.4
        knots.append(mn + span * band)
        upper = not upper
    return knots

def sample_profile(knots, t):
    if not knots:
        return 0.0
    if len(knots) == 1:
        return knots[0]
    p = _clamp(t, 0.0, 1.0) * (len(knots) - 1)
    i = min(len(knots) - 2, int(math.floor(p)))
    u = p - i
    smooth = u * u * (3.0 - 2.0 * u)
    return knots[i] + (knots[i + 1] - knots[i]) * smooth

def _poly_len(pts):
    total = 0.0
    for i in range(1, len(pts)):
        total += math.sqrt((pts[i][0] - pts[i - 1][0]) ** 2 + (pts[i][1] - pts[i - 1][1]) ** 2)
    return total

def windmouse(sx, sy, tx, ty, wind, gravity, curve, profile):
    sqrt3, sqrt5 = (math.sqrt(3.0), math.sqrt(5.0))
    x, y = (float(sx), float(sy))
    vx = vy = wx = wy = 0.0
    dist0 = max(1.0, math.sqrt((tx - sx) ** 2 + (ty - sy) ** 2))
    if dist0 < 3:
        return [[tx, ty, 0]]
    max_step = _clamp(dist0 / 15.0, 6.0, 30.0)
    stop_radius = min(8.0, max(2.0, dist0 * 0.02))
    pts = []
    guard = 0
    while guard < 2000:
        guard += 1
        dist = math.sqrt((tx - x) ** 2 + (ty - y) ** 2)
        if dist < stop_radius:
            break
        live_wind = wind
        if profile:
            progress = _clamp(1.0 - dist / dist0, 0.0, 1.0)
            live_wind = tuned_wind(dist0, sample_profile(profile, progress) / 100.0)
        wmag = min(live_wind, dist)
        if dist >= stop_radius * 4:
            wx = wx / sqrt3 + (2 * _rf() - 1) * wmag / sqrt5
            wy = wy / sqrt3 + (2 * _rf() - 1) * wmag / sqrt5
        else:
            wx /= sqrt3
            wy /= sqrt3
            max_step = max(2.5, max_step / sqrt5)
        vx += wx + gravity * (tx - x) / dist
        vy += wy + gravity * (ty - y) / dist
        vmag = math.sqrt(vx * vx + vy * vy)
        if vmag > max_step:
            vx = vx / vmag * max_step
            vy = vy / vmag * max_step
        x += vx
        y += vy
        pts.append([int(round(x)), int(round(y)), 0])
    pts.append([tx, ty, 0])
    return pts

def resample_variable(spine, mn_sp, mx_sp):
    if not spine:
        return []
    if len(spine) == 1:
        return [list(spine[0])]
    if mx_sp < mn_sp:
        mn_sp, mx_sp = (mx_sp, mn_sp)
    mn_sp = max(0.5, mn_sp)
    mx_sp = max(mn_sp, mx_sp)
    total = _poly_len(spine)
    if total < 1e-06:
        return [list(spine[-1])]
    profile = build_range_profile(mn_sp, mx_sp, 5)
    outp = []
    next_at = sample_profile(profile, 0.0)
    acc = 0.0
    for i in range(1, len(spine)):
        x0, y0 = (spine[i - 1][0], spine[i - 1][1])
        seg = math.sqrt((spine[i][0] - x0) ** 2 + (spine[i][1] - y0) ** 2)
        if seg < 1e-06:
            continue
        while acc + seg >= next_at:
            u = (next_at - acc) / seg
            outp.append([int(round(x0 + (spine[i][0] - x0) * u)), int(round(y0 + (spine[i][1] - y0) * u)), 0])
            phase = _clamp(next_at / total, 0.0, 1.0)
            next_at += max(0.5, sample_profile(profile, phase))
        acc += seg
    last = spine[-1]
    if not outp or outp[-1][0] != last[0] or outp[-1][1] != last[1]:
        outp.append([last[0], last[1], 0])
    return outp

def assign_dynamic_delays(pts, sx, sy, smin, smax, target_ms=0):
    for p in pts:
        p[2] = 0
    if not pts or (smax <= 0 and target_ms <= 0):
        return
    shape_only = smax <= 0
    lo = 150 if shape_only else max(150, min(smin, smax))
    hi = 2000 if shape_only else max(lo, max(smin, smax))
    knot_count = int(_clamp(4 + len(pts) // 120, 4, 9))
    prof = build_range_profile(float(lo), float(hi), knot_count, low_ends=True)
    segs = []
    path = 0.0
    px, py = (sx, sy)
    for p in pts:
        s = math.sqrt((p[0] - px) ** 2 + (p[1] - py) ** 2)
        segs.append(s)
        path += s
        px, py = (p[0], p[1])
    if path < 1e-06:
        return
    weights = []
    wsum = 0.0
    travelled = 0.0
    for i in range(len(pts)):
        phase = _clamp((travelled + segs[i] * 0.5) / path, 0.0, 1.0)
        speed = _clamp(sample_profile(prof, phase), lo, hi)
        w = segs[i] * 1000.0 / max(1.0, speed)
        weights.append(w)
        wsum += w
        travelled += segs[i]
    if wsum <= 0:
        return
    scale = target_ms / wsum if target_ms > 0 else 1.0
    for i in range(len(pts)):
        pts[i][2] = max(1, int(round(weights[i] * scale)))

def curve_height_ratio(pct):
    p = _clamp(float(pct), 0.0, 200.0)
    if p <= 100.0:
        return 0.08 * (p / 100.0) ** 1.15
    return 0.08 + 0.42 * math.sqrt((p - 100.0) / 100.0)

def _ray_to_edge(x, y, dx, dy, w, h, margin):
    max_x = max(margin, w - 1.0 - margin)
    max_y = max(margin, h - 1.0 - margin)
    limit = float('inf')
    if abs(dx) > 1e-09:
        limit = min(limit, (max_x - x) / dx if dx > 0 else (margin - x) / dx)
    if abs(dy) > 1e-09:
        limit = min(limit, (max_y - y) / dy if dy > 0 else (margin - y) / dy)
    return max(0.0, limit) if limit != float('inf') else 0.0

def build_arc(sx, sy, tx, ty, total_ms, cmin, cmax, w, h):
    dx, dy = (tx - sx, ty - sy)
    dist = math.sqrt(dx * dx + dy * dy)
    if dist < 3:
        return ([[tx, ty, max(0, total_ms)]], 0.0, total_ms)
    cmin = _clamp(cmin, 0, 200)
    cmax = _clamp(cmax, 0, 200)
    if cmax < cmin:
        cmin, cmax = (cmax, cmin)
    ux, uy = (dx / dist, dy / dist)
    nx, ny = (-uy, ux)
    skew = _rf() * 0.14 - 0.07
    wobble = 0.025 + _rf() * 0.045
    phase0 = _rf() * math.pi * 2.0
    samples = int(_clamp(math.ceil(dist / 12.0), 32, 240))
    knot_count = int(_clamp(4 + int(dist / 320.0), 4, 8))
    knots = build_range_profile(cmin, cmax, knot_count)

    def fit_for_side(side):
        fit = 1.0
        for i in range(1, samples):
            t = i / samples
            q = _clamp(t + skew * math.sin(math.pi * t), 0.0, 1.0)
            along = 0.5 - 0.5 * math.cos(math.pi * q)
            shape = math.sin(math.pi * q) * (1.0 + wobble * math.sin(2.0 * math.pi * t + phase0))
            bx = sx + ux * dist * along
            by = sy + uy * dist * along
            required = dist * curve_height_ratio(sample_profile(knots, t)) * max(0.0, shape)
            if required < 1e-06:
                continue
            room = _ray_to_edge(bx, by, nx * side, ny * side, w, h, 2.0)
            fit = min(fit, room / required)
        return _clamp(fit * 0.94, 0.0, 1.0)
    fit_pos, fit_neg = (fit_for_side(+1), fit_for_side(-1))
    if fit_pos >= 0.9 and fit_neg >= 0.9:
        side = -1 if _below(2) == 0 else +1
        fit = fit_pos if side > 0 else fit_neg
    elif fit_pos >= fit_neg:
        side, fit = (+1, fit_pos)
    else:
        side, fit = (-1, fit_neg)
    spine = [[sx, sy, 0]]
    actual_height = 0.0
    for i in range(1, samples):
        t = i / samples
        q = _clamp(t + skew * math.sin(math.pi * t), 0.0, 1.0)
        along = 0.5 - 0.5 * math.cos(math.pi * q)
        shape = math.sin(math.pi * q) * (1.0 + wobble * math.sin(2.0 * math.pi * t + phase0))
        local_h = dist * curve_height_ratio(sample_profile(knots, t)) * fit
        normal = side * local_h * max(0.0, shape)
        actual_height = max(actual_height, abs(normal))
        spine.append([int(round(sx + ux * dist * along + nx * normal)), int(round(sy + uy * dist * along + ny * normal)), 0])
    spine.append([tx, ty, 0])
    path_len = _poly_len(spine)
    arc_ms = 0 if total_ms <= 0 else int(_clamp(round(total_ms * max(1.0, path_len / dist)), 60, 30000))
    scale = max(1.0, dist / 2500.0)
    return (resample_variable(spine, 2.0 * scale, 3.2 * scale), actual_height, arc_ms)
_DEFAULT_CFG = dict(before_min=120, before_max=450, after_min=150, after_max=600, mid_chance=12, mid_min=100, mid_max=400, idle_every_min=5, idle_every_max=12, idle_pause_min=1000, idle_pause_max=5000, over_chance=15, curve_min=15, curve_max=45, speed_min=0, speed_max=2000, mt_min=0, mt_max=0)

def plan_move(sx, sy, tx, ty, c, pauses, w, h):
    tx = int(_clamp(tx, 0, max(0, w - 1)))
    ty = int(_clamp(ty, 0, max(0, h - 1)))
    sx = int(_clamp(sx, 0, max(0, w - 1)))
    sy = int(_clamp(sy, 0, max(0, h - 1)))
    dist = math.sqrt((tx - sx) ** 2 + (ty - sy) ** 2)
    speed_lo = max(150, c['speed_min'])
    speed_hi = max(speed_lo, c['speed_max'])
    total_ms = 0 if c['speed_max'] <= 0 else int(_clamp(dist * 1000.0 / max(1.0, (speed_lo + speed_hi) / 2.0), 60, 30000))
    curve_min = int(_clamp(c['curve_min'], 0, 200))
    curve_max = int(_clamp(c['curve_max'], 0, 200))
    if curve_max < curve_min:
        curve_min, curve_max = (curve_max, curve_min)
    sampled = curve_min if curve_max == curve_min else random.randint(curve_min, curve_max)
    curve = _clamp(sampled / 100.0, 0.0, 2.0)
    mt_min, mt_max = (max(0, c['mt_min']), max(0, c['mt_max']))
    if mt_max < mt_min:
        mt_min, mt_max = (mt_max, mt_min)
    target_ms = rand_range(max(1, mt_min), max(1, mt_max)) if mt_max > 0 else 0
    dense = []
    overshoot_idx = -1
    if curve_max > 100 and dist >= 80:
        dense, _height, _arc_ms = build_arc(sx, sy, tx, ty, total_ms, curve_min, curve_max, w, h)
    elif dist >= 60 and c['over_chance'] > 0 and (_below(100) < c['over_chance']):
        ux, uy = ((tx - sx) / dist, (ty - sy) / dist)
        cscale = min(1.0, curve)
        over = int(_clamp(dist * (0.03 + _rf() * 0.05) * cscale, 2, 20))
        perp = int(_rf() * (2.0 + cscale * 5.0) - (1.0 + cscale * 2.5))
        ox = int(_clamp(tx + int(ux * over - uy * perp), 0, max(0, w - 1)))
        oy = int(_clamp(ty + int(uy * over + ux * perp), 0, max(0, h - 1)))
        leg1_ms = max(40, int(total_ms * 0.8))
        leg2_ms = max(30, total_ms - leg1_ms)
        dense += _build_leg(sx, sy, ox, oy, leg1_ms, curve, curve_min, curve_max)
        overshoot_idx = len(dense) - 1
        dense += _build_leg(ox, oy, tx, ty, leg2_ms, curve, curve_min, curve_max)
    else:
        dense = _build_leg(sx, sy, tx, ty, total_ms, curve, curve_min, curve_max)
    for p in dense:
        p[0] = int(_clamp(p[0], 0, max(0, w - 1)))
        p[1] = int(_clamp(p[1], 0, max(0, h - 1)))
    assign_dynamic_delays(dense, sx, sy, c['speed_min'], c['speed_max'], target_ms)
    if overshoot_idx >= 0:
        dense[overshoot_idx][2] += rand_range(60, 180)
    mid = pauses.mid_pause(c)
    if mid > 0 and len(dense) >= 8:
        dense[2 + _below(len(dense) - 4)][2] += mid
    return {'before': rand_range(c['before_min'], c['before_max']), 'after': rand_range(c['after_min'], c['after_max']), 'long': pauses.roll_long(c), 'target': (tx, ty), 'pts': dense}

def _build_leg(sx, sy, tx, ty, total_ms, curve, cmin, cmax):
    dist0 = max(1.0, math.sqrt((tx - sx) ** 2 + (ty - sy) ** 2))
    profile = None
    if cmin >= 0 and cmax >= 0:
        profile = build_range_profile(cmin, cmax, int(_clamp(4 + int(dist0 / 320.0), 4, 8)))
    spine = windmouse(sx, sy, tx, ty, tuned_wind(dist0, curve), 14.0, curve, profile)
    with_start = [[sx, sy, 0]] + spine
    scale = max(1.0, dist0 / 2500.0)
    return resample_variable(with_start, 2.0 * scale, 3.2 * scale)

def _exec_rmouse(prm, ctx, pauses, pos, target=None):
    rx, ry, rw, rh = prm.get('region', (0, 0, ctx.screen_w, ctx.screen_h))
    c = dict(_DEFAULT_CFG)
    c['speed_min'], c['speed_max'] = (ctx.speed_min, ctx.speed_max)
    if 'mt' in prm:
        c['mt_min'], c['mt_max'] = prm['mt']
    if 'curve' in prm:
        c['curve_min'], c['curve_max'] = prm['curve']
    if 'before' in prm:
        c['before_min'], c['before_max'] = prm['before']
    if 'after' in prm:
        c['after_min'], c['after_max'] = prm['after']
    if 'mid' in prm:
        c['mid_chance'], (c['mid_min'], c['mid_max']) = prm['mid']
    if 'idle' in prm:
        (c['idle_every_min'], c['idle_every_max']), (c['idle_pause_min'], c['idle_pause_max']) = prm['idle']
    if 'over' in prm:
        c['over_chance'] = prm['over']
    if target is None:
        tx = random.randint(rx, rx + max(0, rw - 1))
        ty = random.randint(ry, ry + max(0, rh - 1))
    else:
        tx, ty = target
    if prm.get('human', 1) == 0:
        ctx.mmove(tx, ty)
        pos[0], pos[1] = (tx, ty)
        _save_mouse_pos(ctx, pos)
        return
    plan = plan_move(pos[0], pos[1], tx, ty, c, pauses, ctx.screen_w, ctx.screen_h)
    ctx.log('rmouse -> (%d,%d) %d pts' % (tx, ty, len(plan['pts'])))
    if not ctx.sleep_ms(plan['before']):
        raise PlanAbort()
    for pt in plan['pts']:
        ctx.mmove(pt[0], pt[1])
        pos[0], pos[1] = (pt[0], pt[1])
        _save_mouse_pos(ctx, pos)
        if not ctx.sleep_ms(pt[2]):
            raise PlanAbort()
    pos[0], pos[1] = plan['target']
    _save_mouse_pos(ctx, pos)
    if not ctx.sleep_ms(plan['after']):
        raise PlanAbort()
    if plan['long'] > 0:
        ctx.log('idle break %d ms' % plan['long'])
        if not ctx.sleep_ms(plan['long']):
            raise PlanAbort()

"""";
    private const string TypingTemplate = """"
# Generated from canonical plan_engine.py; do not hand-edit.
import time
import math
import random

def _below(n):
    return random.randrange(n) if n > 0 else 0

def rand_range(mn, mx):
    if mx < mn:
        mn, mx = (mx, mn)
    if mx <= 0:
        return 0
    return mn if mx <= mn else random.randint(mn, mx)

def _clamp(v, lo, hi):
    return lo if v < lo else hi if v > hi else v
_QWERTY_ROWS = ('1234567890', 'qwertyuiop', 'asdfghjkl', 'zxcvbnm')
_PUNCT = '.,!?;:'

def _qwerty_neighbor(ch):
    lower = ch.lower()
    for row in _QWERTY_ROWS:
        i = row.find(lower)
        if i < 0:
            continue
        j = i + (-1 if _below(2) == 0 else 1)
        if j < 0 or j >= len(row):
            j = 1 if i == 0 else i - 1
        n = row[j]
        return n.upper() if ch.isupper() else n
    return None

def _split_punct(s):
    outp = []
    start = 0
    for i in range(len(s) - 1):
        if s[i] in _PUNCT:
            outp.append(s[start:i + 1])
            start = i + 1
    if start < len(s):
        outp.append(s[start:])
    if not outp and s:
        outp.append(s)
    return outp

def plan_typing(text, p):
    hmin, hmax = p.get('h', (80, 220))
    wmin, wmax = p.get('w', (0, 0))
    wp = _clamp(p.get('wp', 100), 0, 100)
    pmin, pmax = p.get('p', (0, 0))
    think_chance, think_range = p.get('think', (0, (800, 2200)))
    think_chance = _clamp(think_chance, 0, 100)
    think_min, think_max = think_range
    typo_count_min, typo_count_max = p.get('typos', (0, 0))
    if typo_count_max < typo_count_min:
        typo_count_min, typo_count_max = typo_count_max, typo_count_min
    typo_count_min = max(0, typo_count_min)
    typo_count_max = max(0, typo_count_max)
    typo_count_mode = typo_count_max > 0
    typo_char_min, typo_char_max = p.get('typochars', (0, 0))
    if typo_char_max < typo_char_min:
        typo_char_min, typo_char_max = typo_char_max, typo_char_min
    typo_char_min = max(1, typo_char_min)
    typo_char_max = max(0, typo_char_max)
    typo_char_mode = typo_char_max > 0
    typo_min, typo_max = p.get('typo', (0, 0))
    typo_cadence = not typo_char_mode and not typo_count_mode and typo_max > 0
    next_typo_at = max(1, rand_range(typo_min, typo_max)) if typo_cadence else -1
    words_since_typo = 0
    word_mode = (wmax > 0 or pmax > 0 or think_chance > 0 or
                 typo_char_mode or typo_count_mode or typo_cadence)
    cmds = []
    lines = text.replace('\r\n', '\n').replace('\r', '\n').split('\n')
    line_words = [[wd for wd in line.split() if wd] for line in lines]
    pending = []
    typo_positions = {}

    if typo_char_mode:
        candidates = []
        for li, words in enumerate(line_words):
            for wi, word in enumerate(words):
                for pos, ch in enumerate(word):
                    if ch.lower() in '1234567890qwertyuiopasdfghjklzxcvbnm':
                        candidates.append((li, wi, pos))
        cursor = rand_range(typo_char_min, typo_char_max) - 1
        while cursor < len(candidates):
            li, wi, pos = candidates[cursor]
            typo_positions.setdefault((li, wi), []).append(pos)
            cursor += rand_range(typo_char_min, typo_char_max)
    elif typo_count_mode:
        candidates = []
        for li, words in enumerate(line_words):
            for wi, word in enumerate(words):
                for pos, ch in enumerate(word):
                    if ch.lower() in '1234567890qwertyuiopasdfghjklzxcvbnm':
                        candidates.append((li, wi, pos))
        wanted = min(len(candidates), rand_range(typo_count_min, typo_count_max))
        for i in range(wanted):
            j = i + _below(len(candidates) - i)
            candidates[i], candidates[j] = candidates[j], candidates[i]
            li, wi, pos = candidates[i]
            typo_positions.setdefault((li, wi), []).append(pos)
        for positions in typo_positions.values():
            positions.sort()

    def flush():
        s = ''.join(pending)
        pending.clear()
        for i in range(0, len(s), 60):
            cmds.append(('KTEXT', hmin, hmax, s[i:i + 60]))
    for li, line in enumerate(lines):
        if word_mode:
            words = line_words[li]
            for wi, word in enumerate(words):
                tail = ' ' if wi < len(words) - 1 else ''
                typed = word + tail
                selected = typo_positions.get((li, wi), ())
                if selected:
                    start = 0
                    for pos in selected:
                        wrong = _qwerty_neighbor(word[pos])
                        if wrong is None:
                            continue
                        flush()
                        slip = word[start:pos] + wrong
                        for ci in range(0, len(slip), 60):
                            cmds.append(('KTEXT', hmin, hmax, slip[ci:ci + 60]))
                        cmds.append(('DLY', rand_range(max(hmax, 120), hmax * 2 + 200)))
                        cmds.append(('KCOMBO', 8))
                        cmds.append(('DLY', rand_range(hmin, hmax)))
                        start = pos
                    typed = word[start:] + tail
                else:
                    typo_due = typo_cadence and (words_since_typo := (words_since_typo + 1)) >= next_typo_at
                    if typo_due and 2 <= len(word) <= 60:
                        pos = 1 + _below(len(word) - 1)
                        wrong = _qwerty_neighbor(word[pos])
                        if wrong is not None:
                            flush()
                            cmds.append(('KTEXT', hmin, hmax, word[:pos] + wrong))
                            cmds.append(('DLY', rand_range(max(hmax, 120), hmax * 2 + 200)))
                            cmds.append(('KCOMBO', 8))
                            cmds.append(('DLY', rand_range(hmin, hmax)))
                            typed = word[pos:] + tail
                            words_since_typo = 0
                            next_typo_at = max(1, rand_range(typo_min, typo_max))
                segs = _split_punct(typed) if pmax > 0 else [typed]
                for si, seg in enumerate(segs):
                    pending.append(seg)
                    if si < len(segs) - 1:
                        flush()
                        cmds.append(('DLY', rand_range(pmin, pmax)))
                if wi < len(words) - 1:
                    if wmax > 0 and _below(100) < wp:
                        flush()
                        cmds.append(('DLY', rand_range(wmin, wmax)))
                    if think_chance > 0 and think_max > 0 and (_below(100) < think_chance):
                        flush()
                        cmds.append(('DLY', rand_range(think_min, think_max)))
            flush()
        else:
            for i in range(0, len(line), 60):
                cmds.append(('KTEXT', hmin, hmax, line[i:i + 60]))
        if li < len(lines) - 1:
            cmds.append(('KCOMBO', 13))
    return cmds
"""";
}
