using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// Compiled (8 of the 23 app actions): randomMousePosition, mouseMove (emitted as an
/// RMOUSE rectangle; the engine samples a fresh destination within it on every execution),
/// mouseClick, typeText, delay, forLoop, waitForLight (plain + armed), comment. Blocked (15):
/// findImage, waitForSound, keystroke, keyDown, keyUp, mouseScroll, label, gotoLabel,
/// rawCommand, randomPackage, parallelGroup, playAudio, playScript, runExe, openFile.
/// If/Else heads (insertIfElse) are blocked too - IFLUX/ELSE/ENDIF are gen-2 ops.
///
/// PLAN|2 compensations vs plan_gen (gen-3): the PLAN|2 engine DEFAULTS mid-pauses ON (12%) and
/// idle breaks ON (1000-5000 ms every 5-12 moves) when a key is omitted, so this exporter emits
/// mid= and idle= EXPLICITLY on every move (mid=0:0,0 / idle=1,1:0,0 disable them), and the
/// mouseMove emulation always carries idle=1,1:0,0 (one step never schedules a long idle break).
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
            var (t0, t1) = Pair(PropEx.GetInt(p, "moveTimeMin", 0), PropEx.GetInt(p, "moveTimeMax", 0));
            if (t1 > 0) parts.Add("mt=" + t0 + "," + t1);
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
            int x = PropEx.GetInt(n.Props, "x", 600), y = PropEx.GetInt(n.Props, "y", 497);
            if (PropEx.GetString(n.Props, "moveMode", "fixed") == "handSample")
            {
                if (!HandMovementSample.TryDecode(PropEx.GetString(n.Props, "handSample"), out var sample))
                { Error(n, "handSample mode needs a valid ten-second mouse sample"); return; }
                var path = HandMovementSample.Compact(sample.Segments, HandMovementSample.ReplaySegmentLimit);
                var payload = string.Join(";", path.Select(seg =>
                    seg.DelayMs.ToString(CultureInfo.InvariantCulture) + "," +
                    seg.Dx.ToString(CultureInfo.InvariantCulture) + "," +
                    seg.Dy.ToString(CultureInfo.InvariantCulture)));
                Emit(n, new[] { "HANDPATH|" + payload }, "HANDPATH");
                return;
            }
            int w = PropEx.GetInt(n.Props, "w", 1), h = PropEx.GetInt(n.Props, "h", 1);
            if (w <= 0 || h <= 0)
            { Error(n, "region width/height must be positive (got " + w + "x" + h + ")"); return; }
            if (!PropEx.GetBool(n.Props, "human", true))
                Flag(n, "PLAN|2 RMOUSE always uses a humanized path; human=false cannot be preserved in portable export");
            Emit(n, new[] { "RMOUSE|region=" + x + "," + y + "," + w + "," + h + Tuning(n, (1, 1, 0, 0)) }, "MOVETO");
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
            if (y1 > 0) parts.Add("typo=" + y0 + "," + y1);
            else if (PropEx.GetInt(p, "typoChance", 0) > 0)
                Flag(n, "legacy typoChance % is not portable - use 'typo every N words' (typoEveryMin/Max); "
                        + "the step types WITHOUT typos in this plan");
            Emit(n, new[] { "TYPE|text=" + text + "|" + string.Join("|", parts) }, "TYPE");
        }


        private int Vk(StepNode n,string name,string what){if(KeyMap.VK.TryGetValue(name,out var vk)||VkAliases.TryGetValue(name,out vk))return vk;Error(n,"unknown key name '"+name+"' for "+what);return 0;}
        private void KeyboardFlag(StepNode n){if(PropEx.GetString(n.Props,"keyboardBoard","default")=="promicro")Flag(n,"keyboard executor 'promicro' is bridge-mode only; on the portable plan the Pico types it");}
        private void EmitMouseScroll(StepNode n)=>Emit(n,new[]{"WHEEL|"+PropEx.GetInt(n.Props,"delta",-1)},"WHEEL");
        private void EmitKeystroke(StepNode n){var p=n.Props;var keys=new List<int>();if(PropEx.GetBool(p,"modCtrl"))keys.Add(162);if(PropEx.GetBool(p,"modShift"))keys.Add(160);if(PropEx.GetBool(p,"modAlt"))keys.Add(164);if(PropEx.GetBool(p,"modWin"))keys.Add(91);var vk=Vk(n,PropEx.GetString(p,"key","F4"),"keystroke");if(vk==0)return;keys.Add(vk);var(h0,h1)=Pair(PropEx.GetInt(p,"holdMin",0),PropEx.GetInt(p,"holdMax",0));var line="KEY|combo="+string.Join("+",keys);if(h1>0)line+="|hold="+h0+","+h1;KeyboardFlag(n);Emit(n,new[]{line},"KEY");}
        private void EmitKeyState(StepNode n,bool down){var vk=Vk(n,PropEx.GetString(n.Props,"key","SHIFT"),down?"keyDown":"keyUp");if(vk==0)return;KeyboardFlag(n);Emit(n,new[]{(down?"KDOWN|":"KUP|")+vk},down?"KDOWN":"KUP");}
        private void EmitWaitForSound(StepNode n){var p=n.Props;int t=PropEx.GetInt(p,"threshold",90),m=PropEx.GetInt(p,"minDurationMs",60),to=PropEx.GetInt(p,"timeoutMs",20000);if(PropEx.GetBool(p,"armed")){int act=PropEx.GetString(p,"act","left")switch{"right"=>2,"middle"=>3,_=>1};var(r0,r1)=Pair(PropEx.GetInt(p,"reactMin",80),PropEx.GetInt(p,"reactMax",180));var(h0,h1)=Pair(PropEx.GetInt(p,"holdMin",30),PropEx.GetInt(p,"holdMax",90));Emit(n,new[]{"TRGSND|"+t+","+m+","+to+","+act+","+r0+","+r1+","+h0+","+h1},"TRGSND");}else Emit(n,new[]{"WSND|"+t+","+m+","+to},"WSND");}
        private (int lo,int hi,int stable,int timeout,int mode) LuxArgs(Dictionary<string,object?> p){int c=PropEx.GetInt(p,"luxCenter",1250),tol=Math.Max(1,PropEx.GetInt(p,"luxTolerance",50));return(Math.Max(0,c-tol),c+tol,Math.Max(0,(int)Math.Round(PropEx.GetDouble(p,"stableSec",2)*1000)),PropEx.GetInt(p,"timeoutMs",20000),PropEx.GetString(p,"sampleMode","hires")=="lowres"?1:0);}
        private void EmitLabel(StepNode n){var name=PropEx.GetString(n.Props,"label","label1").Trim();if(name.Length==0||name.Contains('=')||name.Contains('|')){Error(n,"bad label name '"+name+"'");return;}if(!_labels.Add(name)){Error(n,"duplicate label '"+name+"'");return;}Emit(n,new[]{"LABEL|"+name},"LABEL");}
        private void EmitGoto(StepNode n){var name=PropEx.GetString(n.Props,"label").Trim();if(name.Length==0){Error(n,"Go To Label with no label chosen");return;}Emit(n,new[]{"GOTO|"+name},"GOTO");}
        private void EmitRaw(StepNode n){var cmd=PropEx.GetString(n.Props,"cmd","PING").Trim();if(cmd.Length==0||cmd.Contains('\n')||cmd.Contains('\r')){Error(n,"raw command must be one non-empty line");return;}Emit(n,new[]{"RAW|"+cmd},"RAW");}
        private void EmitRandomPackage(StepNode n){var kids=n.Children.Where(c=>!c.IsDisabled&&!IsMarker(c)).ToList();if(kids.Count==0){Error(n,"random package has no enabled children");return;}if(kids.Any(c=>Conditional.Contains(c.Type)&&PropEx.GetBool(c.Props,"insertIfElse"))){Error(n,"an If/Else structure cannot live inside a Random Package");return;}var mode=PropEx.GetString(n.Props,"mode","shuffleAll");int mn=1,mx=kids.Count;string em="all";if(mode=="randomSubset"){em="pick";mn=Math.Max(0,PropEx.GetInt(n.Props,"minCount",1));mx=Math.Min(kids.Count,PropEx.GetInt(n.Props,"maxCount",10));if(mn>mx)(mn,mx)=(mx,mn);}else if(mode!="shuffleAll"){Error(n,"unknown random package mode '"+mode+"'");return;}Lines.Add("RPKG|"+em+","+mn+","+mx);Count("RPKG");for(int i=0;i<kids.Count;i++){if(i>0)Lines.Add("PKGITEM");Walk(new List<StepNode>{kids[i]});}Lines.Add("ENDPKG");EmitDelay(n);}
        private static readonly HashSet<string> ParallelOk=new(){"mouseMove","mouseClick","mouseScroll","keystroke","keyDown","keyUp","typeText","delay","rawCommand","comment"};
        private void EmitParallelGroup(StepNode n){var kids=n.Children.Where(c=>!c.IsDisabled&&!IsMarker(c)).ToList();if(kids.Count<2){Error(n,"a Parallel Group needs at least two enabled branches");return;}var before=Errors.Count;foreach(var c in kids)if(!ParallelOk.Contains(c.Type))Error(c,"'"+c.Type+"' cannot live inside a Parallel Group on the Pico");if(Errors.Count>before)return;Lines.Add("PGROUP");Count("PGROUP");for(int i=0;i<kids.Count;i++){if(i>0)Lines.Add("PARITEM");Walk(new List<StepNode>{kids[i]});}Lines.Add("ENDPAR");EmitDelay(n);}
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
        var known = new HashSet<string>{"PLAN","SCREEN","SPEED","DELAY","LOOP","LOOPTIME","ENDLOOP","RMOUSE","MOVETO","CLICK","TYPE","WLIGHT","WSND","TRGSND","IFSND","IFLUX","ELSE","ENDIF","KEY","KDOWN","KUP","WHEEL","LABEL","GOTO","RAW","RPKG","PKGITEM","ENDPKG","PGROUP","PARITEM","ENDPAR","INCLUDE","BEEP"};
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
            if (op == "INCLUDE" && (!HasKv(fields, "file", out var file) || file.Length == 0 ||
                file.Any(ch => ch < 32 || ch > 126 || "/\\:|%".Contains(ch)))) Bad("unsafe INCLUDE filename");
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
    private const string EngineTemplate = __ENGINE_CORE_TEMPLATE__;
    private const string MotionTemplate = __ENGINE_MOTION_TEMPLATE__;
    private const string TypingTemplate = __ENGINE_TYPING_TEMPLATE__;
}
