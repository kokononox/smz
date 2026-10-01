using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>
/// v0.9.39 - per-system Pico output. Builds a CircuitPython bundle (code.py + boot.py +
/// pico-calibration.json + README-FLASH.md) out of the "Wait For Light" steps of the open plan.
/// The lux ranges are machine specific (monitor, ambient light, sensor position), so the export
/// is meant to be run once on every PC and copied onto that PC's Pico.
/// Sensor: BH1750 (GY-302 / GY-30) on I2C0, SDA=GP20 / SCL=GP21, ADDR-&gt;GND =&gt; 0x23,
/// continuous H-resolution (0x10, ~120 ms/sample), lux = raw / 1.2.
/// v0.9.64f - template body resynced to the golden standalone firmware (code64b): portable plan.txt
/// engine + persistent cursor across Stop/Start, arm-ack watchdog, coalesced fire-and-forget MMOVE,
/// press/release KTEXT typing, conditional start/stop button release + GP4 panic hold. plan_engine.py
/// and plan.txt are NOT part of this bundle - the firmware boots bridge-only without them; the
/// portable plan ships via its own zip (Classroom-Studio-code64b.zip).
/// </summary>
public static class PicoFirmwareExporter
{
    public const string BundleVersion = "0.9.64f";   // v0.9.64f — template resynced to the golden standalone firmware line (code64b): plan engine + persistent cursor + ack watchdog + coalescing + conditional start/stop release + GP4 panic hold

    /// <summary>One calibrated screen state taken from a Wait For Light step.</summary>
    public sealed record LightState(string Name, int LuxLow, int LuxHigh, int StableMs, int TimeoutMs, int Mode, int KeyVk, string KeyName, bool Armed);

    /// <summary>Walks the whole step tree and reads every waitForLight range from its board command.</summary>
    public static List<LightState> CollectStates(IEnumerable<StepNode> steps)
    {
        var acc = new List<LightState>();
        Walk(steps, acc);
        return acc;
    }

    private static void Walk(IEnumerable<StepNode>? steps, List<LightState> acc)
    {
        if (steps is null) return;
        foreach (var s in steps)
        {
            if (s.Type == "waitForLight")
            {
                var cmd = StepDefinitions.GetCommands(s).FirstOrDefault() ?? "";
                var parts = cmd.Split('|');
                var args = parts.Length > 1 ? parts[1].Split(',') : Array.Empty<string>();
                bool armed = cmd.StartsWith("TRGLUX", StringComparison.Ordinal);
                acc.Add(new LightState(
                    string.IsNullOrWhiteSpace(s.Name) ? "light" + (acc.Count + 1) : s.Name,
                    Arg(args, 0, 0), Arg(args, 1, 0), Arg(args, 2, 2000), Arg(args, 3, 20000),
                    Arg(args, 4, 0), armed ? Arg(args, 5, 69) : 0,
                    PropEx.GetString(s.Props, "key", "E"), armed));
            }
            Walk(s.Children, acc);
        }
    }

    private static int Arg(string[] a, int i, int dflt)
        => a.Length > i && int.TryParse(a[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : dflt;

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");


    /// <summary>Converts an Options hotkey gesture to Win32 VK values that code.py maps to USB HID usages.
    /// Kept independent from RegisterHotKey so the exported Pico exactly follows the saved two gestures.</summary>
    public static int[] HotkeyToVirtualKeys(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return Array.Empty<int>();
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Array.Empty<int>();
        var result = new List<int>();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            result.Add(parts[i].ToLowerInvariant() switch
            {
                "shift" => 0xA0, "ctrl" or "control" => 0xA2,
                "alt" => 0xA4, "win" or "windows" => 0x5B,
                _ => throw new InvalidDataException("unsupported Pico hotkey modifier: " + parts[i]),
            });
        }
        string key = parts[^1];
        int vk;
        if (key.Length == 1 && char.IsLetter(key[0])) vk = char.ToUpperInvariant(key[0]);
        else if (key.Length == 1 && char.IsDigit(key[0])) vk = key[0];
        else if (key.Length == 2 && (key[0] == 'D' || key[0] == 'd') && char.IsDigit(key[1])) vk = key[1];
        else if (key.StartsWith("F", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24) vk = 0x70 + fn - 1;
        else if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(key[6..], out int np) && np is >= 0 and <= 9) vk = 0x60 + np;
        else vk = key.ToLowerInvariant() switch
        {
            "back" or "backspace" => 0x08, "tab" => 0x09, "clear" => 0x0C,
            "enter" or "return" => 0x0D, "pause" => 0x13, "capslock" => 0x14,
            "escape" => 0x1B, "space" => 0x20, "pageup" => 0x21, "pagedown" => 0x22,
            "end" => 0x23, "home" => 0x24, "left" => 0x25, "up" => 0x26,
            "right" => 0x27, "down" => 0x28, "printscreen" => 0x2C,
            "insert" => 0x2D, "delete" => 0x2E, "scroll" or "scrolllock" => 0x91,
            "multiply" => 0x6A, "add" => 0x6B, "subtract" => 0x6D,
            "decimal" => 0x6E, "divide" => 0x6F,
            "oemplus" => 0xBB, "oemcomma" => 0xBC, "oemminus" => 0xBD,
            "oemperiod" => 0xBE, "oemquestion" => 0xBF, "oemtilde" => 0xC0,
            "oemopenbrackets" => 0xDB, "oempipe" => 0xDC,
            "oemclosebrackets" => 0xDD, "oemquotes" => 0xDE,
            _ => throw new InvalidDataException("unsupported Pico hotkey key: " + key),
        };
        result.Add(vk);
        return result.ToArray();
    }

    private static string HotkeyTuple(string? gesture)
    {
        var values = HotkeyToVirtualKeys(gesture);
        if (values.Length == 0) return "()";
        return "(" + string.Join(", ", values) + (values.Length == 1 ? "," : "") + ")";
    }

    /// <summary>The per-system calibration file the firmware reads at boot.</summary>
    public static string BuildCalibrationJson(IReadOnlyList<LightState> states, string machine)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"generator\": \"Classroom Studio v" + BundleVersion + "\",");
        sb.AppendLine("  \"system\": \"" + Escape(machine) + "\",");
        sb.AppendLine("  \"generatedUtc\": \"" + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\",");
        sb.AppendLine("  \"sensor\": { \"chip\": \"BH1750\", \"board\": \"GY-302/GY-30\", \"i2c\": \"I2C0 SDA=GP20 SCL=GP21\", \"addr\": 35 },");
        sb.AppendLine("  \"states\": [");
        for (int i = 0; i < states.Count; i++)
        {
            var st = states[i];
            sb.Append("    { \"name\": \"" + Escape(st.Name) + "\", \"luxLow\": " + st.LuxLow
                + ", \"luxHigh\": " + st.LuxHigh + ", \"stableMs\": " + st.StableMs
                + ", \"timeoutMs\": " + st.TimeoutMs + ", \"mode\": " + st.Mode
                + ", \"keyVk\": " + st.KeyVk + ", \"key\": \"" + Escape(st.KeyName)
                + "\", \"armed\": " + (st.Armed ? "true" : "false") + " }");
            sb.AppendLine(i < states.Count - 1 ? "," : "");
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>code.py for this system (the placeholders are filled from the current plan).</summary>
    public static string BuildCodePy(IReadOnlyList<LightState> states, string machine,
        string loopMode = "forever", int loopCount = 0, int loopSeconds = 0, bool keyboardOnArm = false,
        string? runStopHotkey = "Shift+F1", string? pauseResumeHotkey = "Shift+F3")
        => CodeTemplate
            .Replace("__MACHINE__", machine)
            .Replace("__GENERATED__", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Replace("__VERSION__", BundleVersion)
            .Replace("__STATE_COUNT__", states.Count.ToString(CultureInfo.InvariantCulture))
            .Replace("__LOOP_MODE__", loopMode)
            .Replace("__LOOP_COUNT__", loopCount.ToString(CultureInfo.InvariantCulture))
            .Replace("__LOOP_SECONDS__", loopSeconds.ToString(CultureInfo.InvariantCulture));
        // v0.9.60 — final hardware contract: the keyboard always runs on the Pico and the
        // keypad is fixed (GP4 = Num Lock start/stop, GP3 = Scroll Lock pause/resume), so
        // keyboardOnArm and the Options gestures no longer reach the firmware. The
        // parameters stay for call-site compatibility.

    /// <summary>boot.py: enables the second USB serial so HID and the command channel coexist.</summary>
    public static string BuildBootPy() => BootTemplate;

    private const string BootTemplate = """
        # Classroom Studio - Pico boot configuration (CircuitPython)
        # Enables the second USB serial (data) so the app / bridge.py can send
        # WLUX / TRGLUX / LCAL commands while the HID keyboard stays available.
        import usb_cdc

        usb_cdc.enable(console=True, data=True)
        """;

    private const string CodeTemplate = """"
        # Classroom Studio v__VERSION__ - Raspberry Pi Pico light-sensor + portable-plan firmware (CircuitPython)
        # System: __MACHINE__   generated: __GENERATED__   calibrated states: __STATE_COUNT__
        # Sensor: BH1750 (GY-302 / GY-30) on I2C0 - SDA=GP20, SCL=GP21, ADDR->GND => 0x23
        # Board roles: the Pico is the executive brain - keyboard (USB HID) + light sensor + macro
        # engine. Mouse and the sound sensor belong to the Arduino Pro Micro, which the Pico drives
        # as its arm over UART0: GP16 = TX -> Pro Micro RX, GP17 = RX <- Pro Micro TX, common GND,
        # 115200 8N1.
        #
        # v0.9.60 - hardened consolidation (bench-proven as the 0.9.59h/k/m/n line):
        #   * BH1750 OPTIONAL: the brain boots and answers PING even with the sensor unplugged;
        #     WLUX/TRGLUX/LCAL then answer ERR|NOSENSOR and armed light states stay silent.
        #   * Serial RX is a bounded BYTE buffer - no per-read string concat, no heap churn.
        #   * The Pro Micro arm is pumped EVERY loop iteration: its EVT| lines stream to the PC
        #     live and its small TX buffer can never fill up and wedge the arm.
        #   * Mouse commands are fire-and-ack (forwarded to the arm, OK answered at once) so dense
        #     human paths stay smooth; sound/SETRES still wait for the arm's real reply.
        #   * The keyboard ALWAYS runs on this Pico: KBDPICO| and the legacy arm keyboard envelope
        #     are both consumed locally - the arm never types.
        #   * Fixed keypad: GP4->GND = Num Lock = Start/Stop, GP3->GND = Scroll Lock = Pause/Resume.
        #     Both drive the standalone engine AND send the HID key to the PC.
        #   * Safe default: boot never starts the macro. The standalone engine waits for GP4
        #     (Num Lock) unless AUTOSTART below is explicitly True, and stays silent for 3 s after
        #     the last host command so host-driven runs never double-fire the armed states.
        #
        # Copy code.py + boot.py + pico-calibration.json onto CIRCUITPY and put the
        # adafruit_hid package into /lib. Full instructions: README-FLASH.md
        
        import time
        import random
        import json
        import board
        import busio
        import usb_cdc
        import usb_hid
        from adafruit_hid.keyboard import Keyboard
        from adafruit_hid.keycode import Keycode
        
        ADDR = 0x23
        POWER_ON = 0x01
        RESET = 0x07
        CONT_HIRES = 0x10   # 1 lux, ~120 ms per sample (max 180 ms)
        CONT_LORES = 0x13   # 4 lux, ~16 ms per sample
        
        # Play Options baked at export time: how often the armed standalone states run.
        LOOP_MODE = "__LOOP_MODE__"      # once | times | timed | forever
        LOOP_COUNT = __LOOP_COUNT__      # passes for "times"
        LOOP_SECONDS = __LOOP_SECONDS__  # seconds for "timed"
        
        # v0.9.60 - safe default: copying/booting code.py NEVER starts the macro by itself.
        # Set True only for a deliberate power-on-autorun scenario.
        AUTOSTART = False
        
        # v0.9.61-plan1 - portable plan: when /plan.txt exists and no host is driving, the
        # standalone engine runs the plan (random mouse / click / type / delay / loop /
        # wait-for-light) with the SAME humanization as the PC app and fresh randomness
        # every pass. PLAN_DEBUG=True prints step lines on the USB console.
        PLAN_DEBUG = False
        PLAN_PATH = "/plan.txt"
        
        # v0.9.62 - death-of-motion fixes (field evidence: 2026-09-09 record, 53.4 s run):
        #   * GP4 start RE-ARMS the engine (start_engine): passes reset + flow ledger wiped.
        #     The v0.9.61 LOOP_MODE="once" trap (passes never reset, so one early pass end -
        #     a stray USB byte's PlanAbort or a swallowed error - silenced Num Lock until the
        #     next power-cycle) is gone: every start revives the run.
        #   * Arm-ack watchdog in pump_arm: the unframed pico<->arm UART could lose OK|MMOVE
        #     acks and permanently skew _arm_lag; ARM_LAG_MAX lost acks meant eternal
        #     coalescing - a frozen mouse on a fully living Pico. Lag with no ack for
        #     ARM_ACK_TIMEOUT now self-heals and flushes the newest target.
        #   * Faults are VISIBLE with no PC attached: plan run errors ALWAYS print + flash
        #     the onboard LED x3; plan parse errors print even with PLAN_DEBUG=False; LED
        #     state: solid = running, slow blink = paused, off = stopped; x2 flashes = boot.
        #   * Boot banner prints the version; PONG reports pico-light 0.9.62.
        #
        # v0.9.64 - persistent portable cursor across Stop/Start (keeps all v0.9.63 shields):
        #   ghost button events correlated with dense MMOVE traffic on the unframed UART,
        #   and one Right Down never saw its Up (a button held at the HID level = Windows
        #   unclickable until Ctrl+Alt+Del). release_all_buttons() sends MUP|left/right/middle
        #   on every engine stop, every engine start (clean slate), every plan abort/error,
        #   and on host HALT/BYE - a held button can never survive a stop.
        #   (ships as code63.py - built from the code62 tree via splice edits)
        #
        # v0.9.64b - no more start/stop teleport (record analysis 2026-09-09, 14 GP4 toggles):
        #   arm fw>=1.8 syncs its tracked axes into EVERY button report (cursor_sync), so the
        #   v0.9.63 unconditional MUP x3 on start/stop teleported the cursor back to the last
        #   plan point whenever the user had moved the physical mouse while stopped (5 snaps
        #   of 964-1216 px in the same millisecond as the keypress, each followed by exactly
        #   three identical position reports = the three MUP reports).
        #   * release_all_buttons is now CONDITIONAL on the routine GP4 start/stop path: the
        #     Pico tracks MDOWN/MUP itself (_held_buttons) and releases only genuinely held
        #     buttons - empty set = zero MUP = zero cursor_sync = zero teleport.
        #   * force=True keeps the full three-button v0.9.63 shield on the abnormal paths
        #     (host HALT/BYE, plan abort, plan run error).
        #   * Panic gesture: holding GP4 >= 1 s force-releases all three buttons + 5 LED flashes.
        #   * Known limit (unchanged, documented): if the mouse was moved while stopped, the
        #     plan resumes from its stored position and its first move re-homes the cursor
        #     into the plan region - HID has no position feedback channel. The complete fix
        #     for that case is app-side cursor sync on start (queued for the C# line).
        
        SPECIAL_VK = {
            0x0D: Keycode.ENTER, 0x1B: Keycode.ESCAPE, 0x20: Keycode.SPACE, 0x09: Keycode.TAB,
            0x08: Keycode.BACKSPACE, 0x25: Keycode.LEFT_ARROW, 0x27: Keycode.RIGHT_ARROW,
            0x26: Keycode.UP_ARROW, 0x28: Keycode.DOWN_ARROW,
        }
        # modifiers too, so KCOMBO shortcuts (Ctrl/Shift/Alt/Win + key) work on the Pico
        MOD_VK = {
            0xA0: Keycode.LEFT_SHIFT, 0xA1: Keycode.RIGHT_SHIFT,
            0xA2: Keycode.LEFT_CONTROL, 0xA3: Keycode.RIGHT_CONTROL,
            0xA4: Keycode.LEFT_ALT, 0xA5: Keycode.RIGHT_ALT,
            0x5B: Keycode.LEFT_GUI, 0x5C: Keycode.RIGHT_GUI,
        }
        DIGITS = ("ZERO", "ONE", "TWO", "THREE", "FOUR", "FIVE", "SIX", "SEVEN", "EIGHT", "NINE")
        
        
        def keycode_for_vk(vk):
            if 0x41 <= vk <= 0x5A:
                return getattr(Keycode, chr(vk))
            if 0x30 <= vk <= 0x39:
                return getattr(Keycode, DIGITS[vk - 0x30])
            if 0x70 <= vk <= 0x7B:
                return getattr(Keycode, "F" + str(vk - 0x6F))
            return SPECIAL_VK.get(vk, MOD_VK.get(vk, Keycode.E))   # modifiers included
        
        
        # v0.9.60d - ASCII -> Keycode name for the US layout. The adafruit_hid 6.1.10 bundle on
        # /lib has NO Keyboard.write method, so KTEXT types via press/release instead.
        _KTEXT_PLAIN = {
            " ": "SPACE", ".": "PERIOD", ",": "COMMA", "-": "MINUS", "=": "EQUALS",
            "/": "FORWARD_SLASH", ";": "SEMICOLON", "'": "QUOTE", "[": "LEFT_BRACKET",
            "]": "RIGHT_BRACKET", "\\": "BACKSLASH", "`": "GRAVE_ACCENT",
        }
        _KTEXT_SHIFTED = {
            "!": "ONE", "@": "TWO", "#": "THREE", "$": "FOUR", "%": "FIVE", "^": "SIX",
            "&": "SEVEN", "*": "EIGHT", "(": "NINE", ")": "ZERO", "_": "MINUS",
            "+": "EQUALS", "?": "FORWARD_SLASH", ":": "SEMICOLON", "\"": "QUOTE",
            "{": "LEFT_BRACKET", "}": "RIGHT_BRACKET", "|": "BACKSLASH", "~": "GRAVE_ACCENT",
            "<": "COMMA", ">": "PERIOD",
        }
        
        
        def _ascii_key(ch):
            # returns (keycode, need_shift); (None, False) when the char is not US-printable
            o = ord(ch)
            if 97 <= o <= 122:                        # a-z
                return getattr(Keycode, ch.upper(), None), False
            if 65 <= o <= 90:                         # A-Z
                return getattr(Keycode, ch, None), True
            if 48 <= o <= 57:                         # 0-9 (reuse the proven DIGITS table)
                return getattr(Keycode, DIGITS[o - 48], None), False
            if ch in _KTEXT_PLAIN:
                return getattr(Keycode, _KTEXT_PLAIN[ch], None), False
            if ch in _KTEXT_SHIFTED:
                return getattr(Keycode, _KTEXT_SHIFTED[ch], None), True
            return None, False
        
        
        class Bh1750:
            def __init__(self, i2c, mode=CONT_HIRES):
                self._i2c = i2c
                self._buf = bytearray(2)
                self.mode = None
                self._cmd(POWER_ON)
                self._cmd(RESET)
                self.set_mode(mode)
        
            def _cmd(self, value):
                while not self._i2c.try_lock():
                    pass
                try:
                    self._i2c.writeto(ADDR, bytes([value]))
                finally:
                    self._i2c.unlock()
        
            def set_mode(self, mode):
                if mode == self.mode:
                    return
                self.mode = mode
                self._cmd(mode)          # sent once per mode change, never per sample
                time.sleep(0.2 if mode == CONT_HIRES else 0.05)
        
            def lux(self):
                while not self._i2c.try_lock():
                    pass
                try:
                    self._i2c.readfrom_into(ADDR, self._buf)
                finally:
                    self._i2c.unlock()
                raw = (self._buf[0] << 8) | self._buf[1]
                return raw / 1.2         # datasheet conversion
        
        
        def median(values):
            ordered = sorted(values)
            return ordered[len(ordered) // 2]
        
        
        def load_states():
            try:
                with open("/pico-calibration.json", "r") as fh:
                    return json.load(fh).get("states", [])
            except Exception:
                return []
        
        
        kbd = Keyboard(usb_hid.devices)
        serial = usb_cdc.data if usb_cdc.data is not None else usb_cdc.console
        states = load_states()
        window = []
        _lux_seq = 0       # read-only LUX? sequence; resets on every Pico boot
        
        # PLAN2 hotfix h1 - optional engine, but import failures must never be silent.
        _pe_import_error = None
        try:
            import plan_engine as _pe
        except Exception as exc:
            _pe = None
            _pe_import_error = exc
            print("plan: engine import failed:", exc)
        
        # v0.9.60 - the light sensor is OPTIONAL. A loose SDA/SCL wire must never kill the brain
        # before its command loop (that was the silent boot death): without the sensor the light
        # commands answer ERR|NOSENSOR and everything else keeps working.
        try:
            i2c = busio.I2C(board.GP21, board.GP20)   # SCL, SDA
            sensor = Bh1750(i2c)
        except Exception:
            sensor = None
        
        # Commands that are not the brain's own job: forwarded to the Pro Micro arm.
        ARM_PREFIXES = ("MMOVE", "MCLICK", "MWHEEL", "MDOWN", "MUP", "SETRES", "WSND", "TRGSND", "SCAL")
        # v0.9.60 - mouse goes fire-and-ack (smooth dense paths); the rest waits for the arm reply.
        # MCLICK waits for physical completion so an in-flight hold can be aborted.
        FAST_MOUSE_PREFIXES = ("MMOVE", "MWHEEL", "MDOWN", "MUP")
        # Keyboard commands: ALWAYS typed locally by this Pico (final contract).
        KBD_PREFIXES = ("KTEXT", "KCOMBO", "KDOWN", "KUP")
        
        ARM_BAUD = 57600       # v0.9.64d - must match Serial1.begin() in the arm sketch (fw >= 2.4)
        
        try:
            arm = busio.UART(board.GP16, board.GP17, baudrate=ARM_BAUD, timeout=0.2)
        except Exception:
            arm = None
        
        # v0.9.60 - fixed keypad (contract): GP4->GND = Num Lock = Start/Stop,
        # GP3->GND = Scroll Lock = Pause/Resume. Active-low with pull-ups, edge-detected.
        try:
            import digitalio
            btn1 = digitalio.DigitalInOut(board.GP4)
            btn1.direction = digitalio.Direction.INPUT
            btn1.pull = digitalio.Pull.UP
            btn2 = digitalio.DigitalInOut(board.GP3)
            btn2.direction = digitalio.Direction.INPUT
            btn2.pull = digitalio.Pull.UP
            _last_btn1 = True
            _last_btn2 = True
        except Exception:
            btn1 = btn2 = None
            _last_btn1 = _last_btn2 = True
        
        # v0.9.62 - onboard LED (GP25): headless state + fault signalling. Absent on Pico W
        # (its LED lives on the wifi chip) - then led stays None and every LED call no-ops.
        try:
            led = digitalio.DigitalInOut(board.LED)
            led.direction = digitalio.Direction.OUTPUT
            led.value = False
        except Exception:
            led = None
        
        engine_on = AUTOSTART        # GP4 toggles this (Num Lock = Start/Stop)
        engine_paused = False        # GP3 toggles this (Scroll Lock = Pause/Resume)
        _btn1_since = None           # v0.9.64b - GP4 press timestamp for the panic-hold gesture
        last_host_cmd = time.monotonic()
        
        
        def _serial_write_line(text):
            try:
                serial.write((text + "\n").encode("utf-8"))
            except Exception:
                pass
        
        
        _arm_buf = bytearray()
        _arm_lag = 0          # v0.9.60c - MMOVEs written to the arm minus the arm's OK|MMOVE acks
        _pending_move = None  # v0.9.60c - newest coalesced absolute MMOVE while the arm is behind
        _held_buttons = set()  # v0.9.64b - mouse buttons the Pico itself drove down and has not released
        ARM_LAG_MAX = 2       # v0.9.64d - real back-pressure. 8 unacked MMOVEs are ~220 bytes and
                              # the Pro Micro's Serial1 RX buffer is 64 bytes: the overflow ate a
                              # multi-byte chunk (sometimes a whole '#XX|' frame header) and fw 2.3
                              # executed the header-less remnant as a bare MMOVE -> 1000 px teleport.
                              # 2 in flight = ~60 bytes, always under the buffer.
        _moves_dropped = 0    # v0.9.64d - path points skipped because the arm was busy
        _noframe_errors = 0   # v0.9.64e - lines arm fw 2.4 refused for a missing '#' frame
        _last_sent_xy = None  # v0.9.64e - previous MMOVE target actually written to the arm
        _sent_jumps = 0       # v0.9.64e - jumps in the SENT stream (a Pico-side bug, not the wire)
        _drops_at_last_send = 0  # v0.9.64f - coalesce counter at the previous write (gap = expected)
        _partial_writes = 0   # v0.9.64f - short UART writes: a truncated frame the checksum cannot catch
        MOVE_JUMP_PX = 200    # v0.9.64e - a humanized path step is 2-3 px; 200 px is never legitimate
        _diag_last_print = 0.0
        _arm_last_ack = time.monotonic()  # v0.9.62 - the last OK|MMOVE the arm actually sent
        ARM_ACK_TIMEOUT = 1.0  # v0.9.62 - lag this old with zero acks = the ledger drifted: self-heal
        
        # v0.9.64c - arm-link integrity frames (needs arm fw >= 2.3). Every line written to the
        # Pro Micro is wrapped as '#' + 2 hex (sum of the payload bytes mod 256) + '|' + payload.
        # The unframed UART drops bytes in the field: 2026-09-10 hardware run, three ~1280 px
        # out-and-back teleports in one 32 s plan pass, each landing exactly on the target minus
        # the leading '1' of x (MMOVE|1319,364 executed as 319,364). A framed line whose sum does
        # not match is dropped by the arm with ERR|CKSUM and NEVER executed - and a dropped path
        # point is invisible, because the next point of the humanized path is 2-3 px away.
        ARM_FRAMING = True     # set False only for an arm running fw <= 2.2
        _cksum_errors = 0      # v0.9.64c - corrupted lines the arm rejected since boot
        
        
        def pump_arm():
            """v0.9.60 - permanent arm pump. Called on EVERY loop iteration (and inside every
            blocking wait) so the Pro Micro's small TX buffer can never fill up and wedge it.
            Arm EVT| lines stream to the PC live; fire-acked mouse OKs are discarded; every
            other reply is returned for a waiting forward_to_arm."""
            global _arm_buf, _arm_lag, _pending_move, _arm_last_ack, _cksum_errors   # 60c: flow; 62: watchdog; 64c: cksum
            global _noframe_errors                                                   # 64e: strict-framing rejects
            if arm is None:
                return []
            try:
                n = arm.in_waiting
                if n:
                    _arm_buf.extend(arm.read(n))
                    if len(_arm_buf) > 1024:              # runaway-garbage guard
                        _arm_buf = _arm_buf[-256:]         # v0.9.60b - CP-safe (no del-slice)
            except Exception:
                return []
            ready = []
            while True:
                nl = _arm_buf.find(b"\n")
                if nl < 0:
                    break
                raw = bytes(_arm_buf[:nl])
                _arm_buf = _arm_buf[nl + 1:]             # v0.9.60b - CP-safe (no del-slice)
                line = raw.decode("utf-8", "replace").strip()
                if not line:
                    continue
                if line.startswith("EVT|"):
                    _serial_write_line(line)              # arm events reach the PC live
                    continue
                if line.startswith("ERR|NOFRAME"):
                    # v0.9.64e - arm fw 2.4 refused a line with no integrity frame. This is
                    # the case 0.9.64c could not see: an RX gap ate the '#XX|' header and
                    # fw 2.3 would have executed the remnant.
                    _noframe_errors += 1
                    if _arm_lag > 0:
                        _arm_lag -= 1
                    print("arm: NOFRAME drop #%d (header lost, line rejected)" % _noframe_errors)
                    continue
                if line.startswith("ERR|CKSUM"):
                    # v0.9.64c - the arm caught a corrupted line and refused to execute it.
                    # It will never be acked, so the lag ledger must be settled here, and the
                    # drop is printed: a rising counter = a wiring/baud problem, not a bug.
                    _cksum_errors += 1
                    if _arm_lag > 0:
                        _arm_lag -= 1
                    print("arm: CKSUM drop #%d (line rejected, not executed)" % _cksum_errors)
                    continue
                parts = line.split("|")
                if len(parts) > 1 and parts[0] == "OK" and parts[1] in FAST_MOUSE_PREFIXES:
                    if parts[1] == "MMOVE":               # v0.9.60c - the arm caught up one move
                        _arm_last_ack = time.monotonic()  # v0.9.62 - feed the ack watchdog
                        if _arm_lag > 0:
                            _arm_lag -= 1
                        if _pending_move is not None and _arm_lag < ARM_LAG_MAX:
                            if _arm_write(_pending_move):   # flush the coalesced latest target
                                _arm_lag += 1
                            _pending_move = None
                    continue                              # mouse OKs are never forwarded to the PC
                ready.append(line)
            # v0.9.62 - arm-ack watchdog. The UART has no framing: one lost OK|MMOVE skews the
            # lag ledger forever, and ARM_LAG_MAX lost acks froze the mouse (eternal coalescing)
            # while the Pico stayed fully alive - the 2026-09-09 field death. Lag this old with
            # zero acks cannot be real work (a plain MMOVE acks in <200 ms), so the ledger is
            # reset; the newest pending target flushes only while the engine is actually running.
            if _arm_lag > 0 and time.monotonic() - _arm_last_ack > ARM_ACK_TIMEOUT:
                print("arm: ack watchdog reset (lag was %d)" % _arm_lag)
                _arm_lag = 0
                _arm_last_ack = time.monotonic()   # v0.9.64c - no immediate re-trigger
                if _pending_move is not None:
                    # v0.9.64c - DISCARD the stale coalesced target, never flush it. By the time
                    # the watchdog fires the target is up to a second old (hundreds of px behind
                    # the live path) and replaying it is exactly the catch-up dart seen on
                    # hardware. The plan engine sends the next path point ~10 ms later, 2-3 px
                    # from the cursor, so dropping it costs nothing.
                    _pending_move = None
            return ready
        
        
        def _frame(line):
            """v0.9.64c - arm fw>=2.3 integrity frame: '#' + 2 hex byte-sum + '|' + payload."""
            total = 0
            for b in line.encode("utf-8"):
                total = (total + b) & 0xFF
            return "#%02X|%s" % (total, line)
        
        
        def _note_sent(line):
            """v0.9.64f - sent-stream watchdog on the ONE place every line is written.
            A gap right after a coalesce drop is expected (the dropped mid-points ARE the
            gap and the arm interpolates across them), so those are tagged 'after-drop'.
            A jump with no drop in between is a genuine Pico-side bug and says NO-DROP."""
            global _last_sent_xy, _sent_jumps, _drops_at_last_send
            if not line.startswith("MMOVE|"):
                return
            try:
                _p = line.split("|")[1].split(",")
                nx = int(_p[0])
                ny = int(_p[1])
            except Exception:
                return
            prev = _last_sent_xy
            coalesced = _moves_dropped != _drops_at_last_send
            _last_sent_xy = (nx, ny)
            _drops_at_last_send = _moves_dropped
            if prev is None:
                return
            dx = nx - prev[0]
            dy = ny - prev[1]
            if dx * dx + dy * dy <= MOVE_JUMP_PX * MOVE_JUMP_PX:
                return
            _sent_jumps += 1
            print("plan: SENT JUMP #%d %s -> %d,%d (dx=%d dy=%d) %s" % (
                _sent_jumps, prev, nx, ny, dx, dy,
                "after-drop" if coalesced else "NO-DROP"))
        
        
        def _arm_write(line):
            global _partial_writes
            if arm is None:
                return False
            try:
                out = _frame(line) if ARM_FRAMING else line     # v0.9.64c
                buf = (out + "\n").encode("utf-8")
                n = arm.write(buf)
                if n is not None and n != len(buf):
                    # v0.9.64f - a short write truncates the frame mid-line and the arm
                    # splices the remnant onto the next one. The checksum is computed
                    # before the truncation, so it cannot catch this.
                    _partial_writes += 1
                    print("arm: PARTIAL WRITE #%d (%d of %d bytes)" % (_partial_writes, n, len(buf)))
                _note_sent(line)      # v0.9.64f - covers EVERY write path, flush included
                return True
            except Exception:
                return False
        
        
        def forward_fast(line):
            """v0.9.60c - mouse fast path. Discrete clicks/wheel still fire-and-ack. Dense
            MMOVE (streamed by send_path, which never reads per-move acks) is fire-and-forget
            AND flow-controlled: when the arm falls ARM_LAG_MAX behind we coalesce to the
            newest absolute target instead of blocking the USB read (the old per-move OK|MMOVE
            ack backed up USB TX - the PC reads only 1 per 12 - and wedged the link)."""
            global _arm_lag, _pending_move, _moves_dropped
            head = line.split("|")[0]
            if head == "MMOVE":
                if _arm_lag >= ARM_LAG_MAX:
                    # v0.9.64d - the arm is busy: keep ONLY the newest target and never write.
                    # Queueing more bytes is what overflowed the arm's 64-byte RX buffer; the
                    # arm interpolates in 3 px micro-steps, so a skipped mid-path point is invisible.
                    _moves_dropped += 1
                    _pending_move = line          # absolute move: the newest target wins
                    return None                   # no per-move ack (send_path never reads them)
                if _arm_write(line):
                    _arm_lag += 1
                else:
                    return "ERR|NOARM|MMOVE"
                return None                       # no per-move ack
            # v0.9.64b - track held buttons so start/stop releases only what is truly held
            if head == "MDOWN" and "|" in line:
                _held_buttons.add(line.split("|")[1].split(",")[0].strip().lower())
            elif head == "MUP" and "|" in line:
                _held_buttons.discard(line.split("|")[1].split(",")[0].strip().lower())
            if not _arm_write(line):
                return "ERR|NOARM|" + head
            return "OK|" + head
        
        
        def _flow_reset():
            """v0.9.60e - wipe the 60c mouse flow ledger (HALT/BYE/SETRES): no stale coalesced
            move may flush into the next run, and leftover lag must not slow the next path."""
            global _arm_lag, _pending_move
            _arm_lag = 0
            _pending_move = None
        
        
        def release_all_buttons(force=False):
            """v0.9.63 - panic release: ghost MDOWN/MCLICK corruption on the unframed UART (or
            an arm reset mid-click) can leave a button logically held = Windows unclickable
            until Ctrl+Alt+Del (field evidence 2026-09-09: Right Down at 15.96 s never released).
            v0.9.64b - CONDITIONAL by default: the Pico tracks MDOWN/MUP itself, so a routine
            GP4 start/stop releases only genuinely held buttons. An empty set means ZERO MUP
            traffic - and since arm fw>=1.8 syncs its tracked axes into every button report,
            zero MUP also means zero cursor_sync and zero start/stop teleport (record
            2026-09-09: 5 snaps of 964-1216 px exactly at the keypress). force=True keeps the
            full three-button shield for the abnormal paths (host HALT/BYE, plan abort/run
            error) and for the panic gesture (GP4 held >= 1 s)."""
            targets = ("left", "right", "middle") if force else tuple(sorted(_held_buttons))
            for btn in targets:
                _arm_write("MUP|" + btn)
            _held_buttons.clear()
        
        
        _host_abort_buf = bytearray()
        
        
        def _poll_host_halt():
            """Forward host HALT to the arm while the Pico waits for MCLICK."""
            global _host_abort_buf
            if serial is None:
                return False
            try:
                n = serial.in_waiting
                if n:
                    _host_abort_buf.extend(serial.read(n))
            except Exception:
                return False
            saw_halt = False
            while True:
                nl = _host_abort_buf.find(b"\n")
                if nl < 0:
                    break
                raw = bytes(_host_abort_buf[:nl])
                _host_abort_buf = _host_abort_buf[nl + 1:]
                line = raw.decode("utf-8", "replace").rstrip("\r")
                if not line:
                    continue
                if line == "HALT":
                    _arm_write("HALT")
                    saw_halt = True
                else:
                    _serial_write_line("ERR|BUSY|" + line.split("|", 1)[0])
            return saw_halt
        
        
        def _forward_once(line, timeout_s):
            """Blocking forward for commands whose reply the PC needs (sound, SETRES, HALT/BYE).
            Keeps pumping while waiting so arm EVT| lines still stream to the PC.
            v0.9.60e - stale-reply guard: whatever the arm still owes when a NEW blocking command
            starts belongs to an older (or aborted) command. Drain it BEFORE writing, so a late
            OK|HALT can never be mis-paired as the answer to the next SETRES."""
            head = line.split("|")[0]
            pump_arm()                        # v0.9.60e - first sweep of stale arm lines
            time.sleep(0.02)                  # v0.9.60e - let an in-flight stale byte land
            pump_arm()                        # v0.9.60e - second sweep: the pipe is truly quiet
            if not _arm_write(line):
                return "ERR|NOARM|" + head
            end = time.monotonic() + timeout_s
            abort_sent = False
            while time.monotonic() < end:
                if not abort_sent and _poll_host_halt():
                    abort_sent = True
                for reply in pump_arm():
                    if reply.split("|")[0] in ("OK", "ERR"):
                        return reply
                time.sleep(0.005)
            return ("ERR|ABORTED|" + head) if abort_sent else ("ERR|TIMEOUT|" + head)
        
        
        def forward_to_arm(line, timeout_s, retries=0):
            """v0.9.61 - retry wrapper: the pico<->arm UART has no framing, so one lost byte
            (field-observed as a 5 s SETRES silence that killed a timed run) used to surface
            as ERR|TIMEOUT and abort everything. Idempotent commands (SETRES) get one retry
            on a freshly drained pipe before giving up."""
            reply = _forward_once(line, timeout_s)
            attempt = 0
            while reply.startswith("ERR|TIMEOUT|") and attempt < retries:
                attempt += 1
                time.sleep(0.15)                  # let a busy arm finish what it was doing
                reply = _forward_once(line, timeout_s)
            return reply
        
        
        def _mclick_timeout(line):
            """Physical-completion timeout for randomized MCLICK holds."""
            try:
                fields = line.split("|", 1)[1].split(",")
                count = max(1, int(fields[1])) if len(fields) > 1 else 1
                hmin = max(0, int(fields[2])) if len(fields) > 2 else 45
                hmax = max(hmin, int(fields[3])) if len(fields) > 3 else hmin
                return max(5.0, 2.0 + (count * hmax + max(0, count - 1) * 140) / 1000.0)
            except Exception:
                return 5.0
        
        def read_lux_telemetry():
            # Read-only telemetry: no key, mouse, buzzer, calibration or plan side effect.
            global _lux_seq
            if sensor is None:
                return "ERR|NOSENSOR|LUX"
            if _arm_lag > 0 or _pending_move is not None:
                return "ERR|BUSY|LUX"
            try:
                value = sensor.lux()
            except Exception:
                return "ERR|I2C|LUX"
            if value is None or value != value or value < 0:
                return "ERR|I2C|LUX"
            _lux_seq = (_lux_seq + 1) & 0xFFFFFFFF
            mode = "lowres" if sensor.mode == CONT_LORES else "hires"
            return "OK|LUX|seq=%d|lux=%.1f|mode=%s|sensor=ok" % (_lux_seq, value, mode)
        
        
        def sample():
            window.append(sensor.lux())
            if len(window) > 5:
                window.pop(0)
            return median(window)
        
        
        def press(vk, hold_ms):
            code = keycode_for_vk(vk)
            kbd.press(code)
            time.sleep(max(10, hold_ms) / 1000)
            kbd.release(code)
        
        
        def tap_key(code):
            """v0.9.60 - one fixed keypad tap (Num Lock / Scroll Lock) towards the PC."""
            try:
                kbd.press(code)
                time.sleep(0.04)
                kbd.release(code)
            except Exception:
                pass
        
        
        def start_engine():
            """v0.9.62 - every GP4 start RE-ARMS the engine: the passes counter resets (kills
            the v0.9.61 LOOP_MODE=once trap, where one early pass end silenced Num Lock until
            the next power-cycle), the play timer restarts, and the mouse ledger is wiped."""
            global passes, started
            passes = 0
            started = time.monotonic()
            _flow_reset()
            # v0.9.64: deliberately keep _plan_mouse_pos across Stop/Start. Only the UART
            # flow ledger resets; the next portable path resumes from its last sent point.
            release_all_buttons()      # v0.9.64b - tracked-held only (a pointless MUP teleports the cursor)
        
        
        def led_fault(n):
            """v0.9.62 - n rapid onboard-LED flashes = a fault code readable with no PC."""
            if led is None:
                return
            for _ in range(n):
                led.value = True
                time.sleep(0.08)
                led.value = False
                time.sleep(0.08)
        
        
        def poll_keypad():
            """v0.9.60 - fixed mapping, independent of the app's Options: GP4 toggles the
            standalone engine (and sends Num Lock), GP3 toggles pause (and sends Scroll Lock).
            v0.9.62 - a start edge re-arms via start_engine; the LED mirrors engine state."""
            global engine_on, engine_paused, _last_btn1, _last_btn2, _btn1_since
            if btn1 is None:
                return
            b1 = not btn1.value
            b2 = not btn2.value
            if b1 and not _last_btn1:
                _btn1_since = time.monotonic()   # v0.9.64b - measure the hold for the panic gesture
                engine_on = not engine_on
                if engine_on:
                    start_engine()            # v0.9.62 - a start always revives the run
                else:
                    engine_paused = False
                    release_all_buttons()     # v0.9.64b - tracked-held only (a pointless MUP teleports the cursor)
                tap_key(Keycode.KEYPAD_NUMLOCK)
            if b2 and not _last_btn2:
                engine_paused = not engine_paused
                tap_key(Keycode.SCROLL_LOCK)
            if not b1 and _last_btn1 and _btn1_since is not None:   # v0.9.64b - GP4 release edge
                if time.monotonic() - _btn1_since >= 1.0:             # >= 1 s hold = panic release
                    release_all_buttons(force=True)
                    led_fault(5)                                      # 5 flashes = panic release done
                _btn1_since = None
            _last_btn1 = b1
            _last_btn2 = b2
            if led is not None:               # v0.9.62 - solid = running, slow blink = paused, off = stopped
                if engine_on and engine_paused:
                    led.value = (time.monotonic() % 1.0) < 0.5
                else:
                    led.value = engine_on
        
        
        def wait_range(lo, hi, stable_ms, timeout_ms, mode):
            sensor.set_mode(CONT_LORES if mode == 1 else CONT_HIRES)
            deadline = time.monotonic() + timeout_ms / 1000
            inside_since = None
            while time.monotonic() < deadline:
                pump_arm()                            # v0.9.60 - the arm is drained even mid-wait
                value = sample()
                if lo <= value <= hi:
                    if inside_since is None:
                        inside_since = time.monotonic()
                    elif (time.monotonic() - inside_since) * 1000 >= stable_ms:
                        return value      # stabilized inside the range for n seconds
                else:
                    inside_since = None
                time.sleep(0.02)
            return None
        
        
        def ints(fields, count, default=0):
            out = []
            for i in range(count):
                try:
                    out.append(int(fields[i]))
                except Exception:
                    out.append(default)
            return out
        
        
        def handle_keyboard(line, head, plan_mode=False):
            # The Pico owns the keyboard (final contract) - always local execution.
            if head == "KDOWN":
                kbd.press(keycode_for_vk(int(line.split("|")[1])))
                return "OK|KDOWN"
            if head == "KUP":
                kbd.release(keycode_for_vk(int(line.split("|")[1])))
                return "OK|KUP"
            if head == "KCOMBO":
                # KCOMBO|vk+vk+...[,hmin,hmax] - press in order, random hold, release in reverse
                body = line.split("|", 1)[1]
                hold = 0.0
                if "," in body:
                    body, holdpart = body.split(",", 1)
                    hp = holdpart.split(",")
                    try:
                        h0 = int(hp[0])
                        h1 = int(hp[1]) if len(hp) > 1 else h0
                        hold = random.uniform(max(0, h0), max(0, h1)) if max(h0, h1) > 0 else 0.0
                    except Exception:
                        hold = 0.0
                codes = [keycode_for_vk(int(v)) for v in body.split("+") if v]
                for c in codes:
                    kbd.press(c)
                time.sleep(max(10.0, hold) / 1000)
                for c in reversed(codes):
                    kbd.release(c)
                return "OK|KCOMBO"
            if head == "KTEXT":
                # KTEXT|hmin,hmax,text - v0.9.60d: type via press/release (kbd.write is absent on
                # the adafruit_hid 6.1.10 bundle). Per-key humanized delay preserved.
                parts = line.split("|", 1)[1].split(",", 2)
                try:
                    hmin, hmax = int(parts[0]), int(parts[1])
                except Exception:
                    hmin, hmax = 0, 0
                txt = parts[2] if len(parts) > 2 else ""
                for ch in txt:
                    if _ascii_key(ch)[0] is None:
                        return "ERR|ASCII|KTEXT"
                for ch in txt:
                    pump_arm()               # v0.9.60e - the arm is drained even mid-typing
                    if plan_mode:
                        # PLAN2_H6_CONTROL_FIX: service GP4 between every character. A Stop
                        # edge aborts portable typing through the canonical PlanAbort path.
                        poll_keypad()
                        if not engine_on:
                            return "ABORT|KTEXT"
                    if serial is not None and serial.in_waiting:   # v0.9.60e - no USB RX overflow
                        buffer.extend(serial.read(serial.in_waiting))   #   during a long chunk
                    kc, sh = _ascii_key(ch)
                    if sh:
                        kbd.press(Keycode.LEFT_SHIFT)
                    kbd.press(kc)
                    kbd.release(kc)
                    if sh:
                        kbd.release(Keycode.LEFT_SHIFT)
                    if hmax > 0:
                        _type_delay = hmin + random.random() * (hmax - hmin if hmax > hmin else 0)
                        if plan_mode:
                            if not _plan_sleep_ms(_type_delay):
                                return "ABORT|KTEXT"
                        else:
                            time.sleep(_type_delay / 1000)
                return "OK|KTEXT"
            return "ERR|UNKNOWN|" + head
        
        
        def _beep_sequence(line):
        
        
            import pwmio
        
        
            tone = None
        
        
            try:
        
        
                fields = line.split("|", 2)


                if len(fields) != 3:


                    return "ERR|ARG|BEEPSEQ"


                style = fields[1].split(",", 1)


                volume = max(1, min(100, int(style[0])))


                notes = [tuple(int(x) for x in part.split(",")) for part in fields[2].split(";")]
        
        
                tone = pwmio.PWMOut(board.GP6, duty_cycle=0, frequency=notes[0][0], variable_frequency=True)
        
        
                for freq, duration, pause in notes:
        
        
                    tone.frequency = freq; tone.duty_cycle = int(32768 * volume / 100)
        
        
                    time.sleep(duration / 1000.0)
        
        
                    tone.duty_cycle = 0
        
        
                    if pause: time.sleep(pause / 1000.0)
        
        
                return "OK|BEEPSEQ"
        
        
            finally:
        
        
                if tone is not None:
        
        
                    tone.duty_cycle = 0; tone.deinit()
        
        
        
        def handle(line):
        
        
            if line.startswith("BEEPSEQ|"): return _beep_sequence(line)
            if line == "LUX?":
                return read_lux_telemetry()
            if line == "PING":
                return "OK|PONG|pico-light __VERSION__|role=brain+keyboard+light|arm=promicro|beepseq=1|framing=%d|baud=%d|lagmax=%d|dropped=%d|cksum=%d|noframe=%d|sentjumps=%d|partial=%d" % (1 if ARM_FRAMING else 0, ARM_BAUD, ARM_LAG_MAX, _moves_dropped, _cksum_errors, _noframe_errors, _sent_jumps, _partial_writes)
            if line.startswith("LCAL|"):
                if sensor is None:
                    return "ERR|NOSENSOR|LCAL"
                ms = ints(line.split("|")[1].split(","), 1, 2000)[0] or 2000
                sensor.set_mode(CONT_HIRES)
                end = time.monotonic() + ms / 1000
                readings = []
                while time.monotonic() < end:
                    pump_arm()                        # v0.9.60 - the arm is drained even mid-calibration
                    readings.append(sensor.lux())
                    time.sleep(0.02)
                if not readings:
                    readings = [sensor.lux()]
                lo = int(min(readings))
                hi = int(max(readings))
                avg = int(sum(readings) / len(readings))
                return "OK|LCAL|min=%d|max=%d|avg=%d" % (lo, hi, avg)
            if line.startswith("WLUX|"):
                if sensor is None:
                    return "ERR|NOSENSOR|WLUX"
                a = ints(line.split("|")[1].split(","), 5)
                got = wait_range(a[0], a[1], a[2] or 2000, a[3] or 20000, a[4])
                if got is None:
                    return "ERR|TIMEOUT|WLUX"
                return "OK|WLUX|lux=%d" % int(got)
            if line.startswith("TRGLUX|"):
                if sensor is None:
                    return "ERR|NOSENSOR|TRGLUX"
                a = ints(line.split("|")[1].split(","), 10)
                got = wait_range(a[0], a[1], a[2] or 2000, a[3] or 20000, a[4])
                if got is None:
                    return "ERR|TIMEOUT|TRGLUX"
                time.sleep(max(0, a[6]) / 1000)   # reaction delay (reactMin)
                press(a[5], a[8] or 40)           # board-side keypress (holdMin)
                return "EVT|TRGLUX|lux=%d|vk=%d" % (int(got), a[5])
            if line.startswith("BEEP|"):
                try:
                    import pwmio
                    fields = line.split("|", 1)[1].split(",")
                    if len(fields) < 2 or len(fields) > 4:
                        return "ERR|ARG|BEEP"
                    frequency, duration = int(fields[0]), int(fields[1])
                    volume = int(fields[2]) if len(fields) > 2 else 100
                    envelope = fields[3] if len(fields) > 3 else "sharp"
                    if (not 30 <= frequency <= 20000 or duration <= 0 or
                            not 1 <= volume <= 100 or
                            envelope not in ("sharp", "smooth", "fade-in", "fade-out")):
                        return "ERR|RANGE|BEEP"
                    tone = pwmio.PWMOut(board.GP6, duty_cycle=0, frequency=frequency, variable_frequency=True)
                    try:
                        target = int(32768 * volume / 100)
                        total = duration / 1000
                        edge = min(.08, total / 3)
                        started = time.monotonic()
                        while True:
                            elapsed = time.monotonic() - started
                            if elapsed >= total:
                                break
                            level = 1.0
                            if envelope in ("smooth", "fade-in") and elapsed < edge:
                                level = elapsed / edge
                            if envelope in ("smooth", "fade-out") and elapsed > total - edge:
                                level = min(level, (total - elapsed) / edge)
                            tone.duty_cycle = max(0, min(32768, int(target * level)))
                            time.sleep(.004 if envelope != "sharp" else total)
                    finally:
                        tone.duty_cycle = 0
                        tone.deinit()
                    return "OK|BEEP"
                except Exception:
                    return "ERR|BUZZER|BEEP"
            if line in ("HALT", "BYE"):
                _flow_reset()                # v0.9.60e - stop wipes the mouse flow ledger
                release_all_buttons(force=True)  # v0.9.64b - full shield on host HALT/BYE
                if arm is not None:
                    forward_to_arm(line, 2)          # stop the arm too
                return "OK|" + line
            # v0.9.60 - final contract: the Pico ALWAYS owns the keyboard. Both envelopes are
            # consumed locally; the legacy arm envelope is accepted for backward compatibility
            # but is never forwarded (the Pro Micro never types).
            if line.startswith("KBDPICO|"):
                line = line.split("|", 1)[1]
            elif line.startswith("KBDARM|"):
                line = line.split("|", 1)[1]
            head = line.split("|")[0]
            if head in KBD_PREFIXES:
                return handle_keyboard(line, head)
            if head == "MCLICK":
                return forward_to_arm(line, _mclick_timeout(line))
            if head in FAST_MOUSE_PREFIXES:
                return forward_fast(line)
            if head in ARM_PREFIXES:
                if head == "SETRES":
                    _flow_reset()            # v0.9.60e - a new run starts with a clean ledger
                tmo = 30 if head in ("WSND", "TRGSND", "SCAL") else 5
                return forward_to_arm(line, tmo, 1 if head == "SETRES" else 0)
            return "ERR|UNKNOWN|" + line
        
        
        
        _plan_cache = None          # v0.9.61 - parsed plan ops, or False when unavailable
        _plan_mouse_pos = None      # v0.9.64 - last MMOVE actually sent by portable plan; survives Start/Stop
        
        
        def _plan_sleep_ms(ms):
            """v0.9.61 - pump-aware plan delay: keeps the arm drained and the keypad alive while
            a plan step waits. Returns False when the plan must abort (host traffic arrived or
            the keypad stopped the engine). Scroll Lock pause FREEZES the delay, not the plan."""
            global last_host_cmd
            end = time.monotonic() + ms / 1000
            while True:
                pump_arm()
                poll_keypad()
                if not engine_on:
                    return False
                if serial is not None and serial.in_waiting:
                    last_host_cmd = time.monotonic()   # host is back - bridge mode wins
                    return False
                if not engine_paused and time.monotonic() >= end:
                    return True
                time.sleep(0.01)
        
        
        class _PlanCtx:
            """v0.9.61 - adapter from plan_engine ops to this firmware's drivers."""
            screen_w = 1920
            screen_h = 1080
            speed_min = 0
            speed_max = 2000
        
            def get_mouse_pos(self):
                # None only on a fresh Pico boot; plan_engine then uses screen centre once.
                return _plan_mouse_pos
        
            def set_mouse_pos(self, x, y):
                global _plan_mouse_pos
                _plan_mouse_pos = (int(x), int(y))
        
            def now(self):
                return time.monotonic()
        
            def log(self, msg):
                if PLAN_DEBUG:
                    print("plan:", msg)
        
            def sleep_ms(self, ms):
                return _plan_sleep_ms(ms)
        
            def mmove(self, x, y):
                forward_fast("MMOVE|%d,%d,abs,2" % (x, y))   # fw>=1.9 micro-steps; 1.8 = plain jump
        
            def mclick(self, btn, count, hmin, hmax):
                line = "MCLICK|%s,%d" % (btn, count)
                if hmax > 0:
                    line += ",%d,%d" % (hmin, hmax)
                reply = forward_to_arm(line, _mclick_timeout(line))
                if reply.startswith("ERR|"):
                    raise _pe.PlanAbort()
        
            def ktext(self, hmin, hmax, text):
                _typed = handle_keyboard("KTEXT|%d,%d,%s" % (hmin, hmax, text), "KTEXT", True)
                if _typed == "ABORT|KTEXT":
                    raise _pe.PlanAbort()
        
            def kcombo(self, vk):
                handle_keyboard("KCOMBO|%d" % vk, "KCOMBO")
        
            def wait_light(self, lo, hi, stable_ms, timeout_ms, mode):
                if sensor is None:
                    return False
                return wait_range(lo, hi, stable_ms, timeout_ms, mode) is not None
        
            def key(self, vk, hold_ms):
                press(vk, hold_ms)
        
            # PLAN|2 firmware contract. Keep transport behavior byte-for-byte on the
            # accepted 0.9.64f line; these methods only adapt new plan operations to it.
            plan_api = 3
        
            def gate(self):
                global last_host_cmd
                while True:
                    pump_arm()
                    poll_keypad()
                    if not engine_on:
                        return False
                    if serial is not None and serial.in_waiting:
                        last_host_cmd = time.monotonic()
                        return False
                    if not engine_paused:
                        return True
                    time.sleep(0.01)
        
            def _ok(self, reply, what):
                if reply is None or str(reply).startswith("ERR|"):
                    raise RuntimeError("%s failed: %s" % (what, reply))
                return reply
        
            def setres(self, w, h):
                reply = forward_to_arm("SETRES|%d,%d" % (int(w), int(h)), 5, 1)
                return self._ok(reply, "SETRES")
        
            def key_combo(self, vks, hmin, hmax):
                body = "+".join(str(int(v)) for v in vks)
                reply = handle_keyboard("KCOMBO|%s,%d,%d" % (body, int(hmin), int(hmax)), "KCOMBO")
                return self._ok(reply, "KCOMBO")
        
            def kdown(self, vk):
                return self._ok(handle_keyboard("KDOWN|%d" % int(vk), "KDOWN"), "KDOWN")
        
            def kup(self, vk):
                return self._ok(handle_keyboard("KUP|%d" % int(vk), "KUP"), "KUP")
        
            def wheel(self, delta):
                return self._ok(forward_fast("MWHEEL|%d" % int(delta)), "MWHEEL")
        
            def raw(self, line):
                reply = handle(str(line))
                if reply is None:
                    return "OK|RAW"
                return self._ok(reply, "RAW")
        
            def read_plan_file(self, name):
                # parse_plan already rejects traversal; repeat the guard at the filesystem boundary.
                if not name or "/" in name or "\\" in name or ":" in name or not name.lower().endswith(".txt"):
                    raise ValueError("unsafe include file name")
                with open("/" + name, "r") as fh:
                    return fh.read()
        
            def wait_sound(self, threshold, min_ms, timeout_ms):
                reply = forward_to_arm(
                    "WSND|%d,%d,%d" % (int(threshold), int(min_ms), int(timeout_ms)),
                    max(5.0, int(timeout_ms) / 1000.0 + 2.0))
                if reply.startswith("OK|WSND|DETECTED"):
                    return True
                if reply.startswith("ERR|TIMEOUT|WSND"):
                    return False
                if "ABORTED" in reply:
                    return None
                raise RuntimeError("WSND failed: " + reply)
        
            def trg_sound(self, threshold, min_ms, timeout_ms, action,
                          react_min, react_max, hold_min, hold_max):
                reply = forward_to_arm(
                    "TRGSND|%d,%d,%d,%d,%d,%d,%d,%d" %
                    (int(threshold), int(min_ms), int(timeout_ms), int(action),
                     int(react_min), int(react_max), int(hold_min), int(hold_max)),
                    max(5.0, int(timeout_ms) / 1000.0 + 3.0))
                if reply.startswith("OK|TRGSND"):
                    return None if "ABORTED" in reply else True
                if reply.startswith("ERR|TIMEOUT|TRGSND"):
                    return False
                raise RuntimeError("TRGSND failed: " + reply)
        
            def beep(self, freq, ms):
                # PLAN|2 v3 buzzer contract. Import pwmio lazily to keep boot RAM low.
                import pwmio
                tone = None
                try:
                    tone = pwmio.PWMOut(board.GP6, duty_cycle=0,
                                        frequency=int(freq), variable_frequency=True)
                    tone.duty_cycle = 32768
                    if not _plan_sleep_ms(int(ms)):
                        raise _pe.PlanAbort()
                finally:
                    if tone is not None:
                        try:
                            tone.duty_cycle = 0
                            tone.deinit()
                        except Exception:
                            pass
        
        
        def plan_pass():
            """v0.9.61 - run one pass of /plan.txt. Returns True when a plan exists and ran
            (even if it aborted early), False when there is no plan to run."""
            global _plan_cache
            if _pe is None:
                return False
            if _plan_cache is None:
                try:
                    with open(PLAN_PATH, "r") as fh:
                        text = fh.read()
                    _plan_cache = _pe.parse_plan(text)
                    print("plan: loaded", len(_plan_cache), "ops")   # v0.9.62 - confirm a good parse once per boot
                except Exception as exc:
                    if isinstance(exc, OSError):
                        pass                                 # no plan.txt - normal bridge-only mode
                    else:
                        print("plan: load failed:", exc)    # v0.9.62 - parse errors ALWAYS visible
                        led_fault(4)                         # 4 flashes = plan parse fault
                    _plan_cache = False
            if not _plan_cache:
                return False
            try:
                _pe.run_plan(_plan_cache, _PlanCtx())
                return "done"                     # PLAN2_H6_CONTROL_FIX: finite plan completed
            except _pe.PlanAbort:
                release_all_buttons(force=True)   # v0.9.64b - full shield on the abort path
                return "abort"
            except Exception as exc:
                # v0.9.62 - ALWAYS reported (was PLAN_DEBUG-only: a swallowed error read as a
                # silent death). 3 LED flashes = run fault, visible with no PC attached.
                print("plan: run error:", exc)
                led_fault(3)
                release_all_buttons(force=True)   # v0.9.64b - full shield on the error path
                return "error"
        
        
        def standalone_pass():
            # No host command pending: run the armed states of this system in order.
            if sensor is None:
                return                                # v0.9.60 - light states stay silent without a sensor
            for st in states:
                if not st.get("armed"):
                    continue
                got = wait_range(st.get("luxLow", 0), st.get("luxHigh", 0),
                                 st.get("stableMs", 2000), st.get("timeoutMs", 20000),
                                 st.get("mode", 0))
                if got is not None:
                    press(st.get("keyVk", 69), 40)
        
        
        def loop_due():
            # Play Options baked at export: how often the armed states run standalone
            if LOOP_MODE == "once":
                return passes == 0
            if LOOP_MODE == "times":
                return passes < LOOP_COUNT
            if LOOP_MODE == "timed":
                return time.monotonic() - started < LOOP_SECONDS
            return True                      # forever
        
        
        buffer = bytearray()               # v0.9.60 - bounded byte buffer, not string concat
        passes = 0
        started = time.monotonic()
        # v0.9.64 - boot banner on the USB console (any serial tool proves the flashed version)
        # + two LED flashes = the brain booted alive.
        print("pico-light __VERSION__ | GP4=NumLock start/stop (hold 1s=panic release) | GP3=ScrollLock pause | arm framing=%s baud=%d lagmax=%d | diag=full" % (ARM_FRAMING, ARM_BAUD, ARM_LAG_MAX))
        led_fault(2)
        while True:
            try:
                pump_arm()                            # v0.9.60 - the arm is drained EVERY iteration
                if serial is not None and serial.in_waiting:
                    buffer.extend(serial.read(serial.in_waiting))
                    if len(buffer) > 4096:            # runaway guard: keep the newest 1 KB
                        buffer = buffer[-1024:]          # v0.9.60b - CP-safe (no del-slice)
                    while True:
                        nl = buffer.find(b"\n")
                        if nl < 0:
                            break
                        raw = bytes(buffer[:nl])
                        buffer = buffer[nl + 1:]         # v0.9.60b - CP-safe (no del-slice)
                        line = raw.decode("utf-8", "replace").strip()
                        if not line:
                            continue
                        last_host_cmd = time.monotonic()
                        try:
                            _reply = handle(line)
                            if _reply is not None:        # v0.9.60c - MMOVE is fire-and-forget (None)
                                _serial_write_line(_reply)
                        except Exception:
                            _serial_write_line("ERR|EXC|" + line.split("|")[0])
                else:
                    poll_keypad()
                    host_quiet = time.monotonic() - last_host_cmd >= 3   # no double-fire after host runs
                    if engine_on and not engine_paused and host_quiet and loop_due():
                        _plan_result = plan_pass()  # v0.9.61 - a portable plan takes precedence
                        if _plan_result:
                            passes += 1
                            if _plan_result == "done":
                                # PLAN2_H6_CONTROL_FIX: a finite portable plan owns its loop
                                # policy internally. Its return is a real natural Stop even when
                                # the legacy light-state LOOP_MODE is "forever".
                                engine_on = False
                                engine_paused = False
                                release_all_buttons()
                                tap_key(Keycode.KEYPAD_NUMLOCK)
                        elif states:
                            standalone_pass()
                            passes += 1
                            if not loop_due():
                                engine_on = False
                                engine_paused = False
                                release_all_buttons()
                                tap_key(Keycode.KEYPAD_NUMLOCK)
                    time.sleep(0.02)
            except Exception:
                # v0.9.60 - never-die: a bad line or a transient USB hiccup must never kill
                # code.py. (KeyboardInterrupt/Ctrl+C still stops it - it is not an Exception.)
                try:
                    time.sleep(0.05)
                except Exception:
                    pass

        """";

    /// <summary>Per-system flashing + calibration instructions (Persian, like the other docs).</summary>
    public static string BuildReadme(IReadOnlyList<LightState> states, string machine,
        string loopMode = "forever", int loopCount = 0, int loopSeconds = 0, bool keyboardOnArm = false)   // v0.9.44
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Pico light sensor - " + machine);
        sb.AppendLine();
        sb.AppendLine("Classroom Studio v" + BundleVersion + " - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        sb.AppendLine();
        sb.AppendLine("Play Options baked in: loop=" + loopMode + (loopMode == "times" ? " x" + loopCount : loopMode == "timed" ? " " + loopSeconds + "s" : "") + " | keyboard=Pico (fixed v0.9.60 contract) | keypad GP4=NumLock start/stop, GP3=ScrollLock pause/resume | AUTOSTART=False");
        sb.AppendLine();
        sb.AppendLine("\u0627\u06cc\u0646 \u062e\u0631\u0648\u062c\u06cc \u0645\u062e\u0635\u0648\u0635 \u0647\u0645\u06cc\u0646 \u0633\u06cc\u0633\u062a\u0645 \u0627\u0633\u062a\u061b \u0645\u0642\u062f\u0627\u0631 \u0644\u0648\u06a9\u0633 \u062f\u0631 \u0647\u0631 \u0645\u0627\u0646\u06cc\u062a\u0648\u0631 \u0648 \u0647\u0631 \u0627\u062a\u0627\u0642 \u0645\u062a\u0641\u0627\u0648\u062a \u0627\u0633\u062a\u060c \u067e\u0633 \u062f\u0631 \u0647\u0631 \u0633\u06cc\u0633\u062a\u0645 \u06cc\u06a9\u200c\u0628\u0627\u0631 \u0627\u06cc\u0646 \u062e\u0631\u0648\u062c\u06cc \u0631\u0627 \u0628\u0633\u0627\u0632 \u0648 \u0631\u0648\u06cc \u0628\u0631\u062f \u0628\u0631\u06cc\u0632.");
        sb.AppendLine();
        sb.AppendLine("## \u0641\u0627\u06cc\u0644\u200c\u0647\u0627");
        sb.AppendLine();
        sb.AppendLine("- `code.py` - \u0641\u0631\u0645\u200c\u0648\u0631 \u0627\u0635\u0644\u06cc (CircuitPython)");
        sb.AppendLine("- `boot.py` - \u0641\u0639\u0627\u0644 \u06a9\u0631\u062f\u0646 \u0633\u0631\u06cc\u0627\u0644 \u062f\u0648\u0645 \u06a9\u0646\u0627\u0631 \u06a9\u06cc\u0628\u0648\u0631\u062f HID");
        sb.AppendLine("- `pico-calibration.json` - \u0628\u0627\u0632\u0647\u200c\u0647\u0627\u06cc \u0644\u0648\u06a9\u0633 \u0647\u0645\u06cc\u0646 \u0633\u06cc\u0633\u062a\u0645");
        sb.AppendLine();
        sb.AppendLine("## \u0645\u0631\u0627\u062d\u0644 \u0631\u06cc\u062e\u062a\u0646 \u0631\u0648\u06cc \u0628\u0631\u062f");
        sb.AppendLine();
        sb.AppendLine("1. \u062f\u06a9\u0645\u0647 BOOTSEL \u0631\u0627 \u0646\u06af\u0647 \u062f\u0627\u0631 \u0648 \u067e\u06cc\u06a9\u0648 \u0631\u0627 \u0628\u0647 USB \u0628\u0632\u0646\u061b \u062f\u0631\u0627\u06cc\u0648 RPI-RP2 \u0628\u0627\u0644\u0627 \u0645\u06cc\u200c\u0622\u06cc\u062f.");
        sb.AppendLine("2. \u0641\u0627\u06cc\u0644 UF2 \u0645\u0631\u0628\u0648\u0637 \u0628\u0647 CircuitPython 9.x \u0631\u0627 \u062f\u0631 \u0627\u06cc\u0646 \u062f\u0631\u0627\u06cc\u0648 \u06a9\u067e\u06cc \u06a9\u0646 (\u0641\u0642\u0637 \u0628\u0627\u0631 \u0627\u0648\u0644 \u0628\u0631\u0627\u06cc \u0647\u0631 \u0628\u0631\u062f).");
        sb.AppendLine("3. \u0628\u0639\u062f \u0627\u0632 \u0631\u06cc\u0633\u062a\u060c \u062f\u0631\u0627\u06cc\u0648 CIRCUITPY \u0645\u06cc\u200c\u0622\u06cc\u062f: \u0647\u0631 \u0633\u0647 \u0641\u0627\u06cc\u0644 \u0628\u0627\u0644\u0627 \u0631\u0627 \u062f\u0631 \u0631\u06cc\u0634\u0647\u200c\u06cc \u0622\u0646 \u0628\u0631\u06cc\u0632.");
        sb.AppendLine("4. \u067e\u0648\u0634\u0647\u200c\u06cc `adafruit_hid` \u0631\u0627 \u0627\u0632 CircuitPython Library Bundle \u062f\u0631 `CIRCUITPY/lib` \u0628\u06af\u0630\u0627\u0631.");
        sb.AppendLine("5. \u0633\u06cc\u0645\u200c\u06a9\u0634\u06cc GY-302/GY-30: `VCC -> 3V3 (pin 36)` \u060c `GND -> GND (pin 38)` \u060c `SDA -> GP20 (pin 26)` \u060c `SCL -> GP21 (pin 27)` \u060c `ADDR -> GND` (\u0622\u062f\u0631\u0633 0x23).");
        sb.AppendLine("6. \u0633\u0646\u0633\u0648\u0631 \u0631\u0627 \u0628\u0627 \u0647\u0648\u062f \u062a\u0627\u0631\u06cc\u06a9 \u0631\u0648\u06cc \u0646\u0627\u062d\u06cc\u0647\u200c\u06cc \u0645\u0648\u0631\u062f\u0646\u0637\u0631 \u0635\u0641\u062d\u0647 \u062b\u0627\u0628\u062a \u06a9\u0646 (\u0646\u0648\u0631 \u0645\u062d\u06cc\u0637 \u0646\u0628\u0627\u06cc\u062f \u0628\u0631\u0633\u062f).");
        sb.AppendLine("7. \u062f\u0631 \u0628\u0631\u0646\u0627\u0645\u0647: Insert -> Wait For Light\u060c \u0647\u0645\u0627\u0646 \u0648\u0636\u0639\u06cc\u062a \u0631\u0627 \u0631\u0648\u06cc \u0635\u0641\u062d\u0647 \u0628\u06cc\u0627\u0648\u0631 \u0648 \u062f\u06a9\u0645\u0647\u200c\u06cc Calibrate \u0631\u0627 \u0628\u0632\u0646 (LCAL)\u061b \u062a\u0644\u0648\u0631\u0627\u0646\u0633 \u0631\u0627 \u062f\u0633\u062a\u200c\u06a9\u0645 \u06f5\u06f0 \u0644\u0648\u06a9\u0633 \u0628\u06af\u0630\u0627\u0631.");
        sb.AppendLine("8. \u0628\u06cc\u0646 \u062f\u0648 \u0648\u0636\u0639\u06cc\u062a \u0645\u062a\u0641\u0627\u0648\u062a \u062d\u062f\u0627\u0642\u0644 \u06f5\u06f0 \u0644\u0648\u06a9\u0633 \u0641\u0627\u0635\u0644\u0647\u200c\u06cc \u062e\u0627\u0644\u06cc (dead zone) \u0644\u0627\u0632\u0645 \u0627\u0633\u062a.");
        sb.AppendLine();
        sb.AppendLine("## \u0628\u0627\u0632\u0647\u200c\u0647\u0627\u06cc \u0627\u06cc\u0646 \u0646\u0642\u0634\u0647");
        sb.AppendLine();
        if (states.Count == 0)
        {
            sb.AppendLine("\u0647\u06cc\u0686 \u06af\u0627\u0645 Wait For Light \u062f\u0631 \u0627\u06cc\u0646 \u0646\u0642\u0634\u0647 \u0646\u06cc\u0633\u062a - \u0641\u0631\u0645\u200c\u0648\u0631 \u0641\u0642\u0637 \u0628\u0647 \u0641\u0631\u0645\u0627\u0646\u200c\u0647\u0627\u06cc \u0628\u0631\u0646\u0627\u0645\u0647 (WLUX/LCAL) \u067e\u0627\u0633\u062e \u0645\u06cc\u200c\u062f\u0647\u062f.");
        }
        else
        {
            sb.AppendLine("| state | lux range | stable | timeout | mode | key | armed |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var st in states)
                sb.AppendLine("| " + st.Name + " | " + st.LuxLow + "-" + st.LuxHigh + " | " + (st.StableMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)
                    + "s | " + st.TimeoutMs + "ms | " + (st.Mode == 1 ? "lowres" : "hires") + " | " + st.KeyName + " | " + (st.Armed ? "yes" : "no") + " |");
        }
        sb.AppendLine();
        sb.AppendLine("## \u067e\u0631\u0648\u062a\u06a9\u0644 \u0633\u0631\u06cc\u0627\u0644");
        sb.AppendLine();
        sb.AppendLine("- `PING` -> `OK|PONG|pico-light " + BundleVersion + "`");
        sb.AppendLine("- `LCAL|ms` -> `OK|LCAL|min=..|max=..|avg=..` (\u062f\u06a9\u0645\u0647\u200c\u06cc Calibrate \u0647\u0645\u06cc\u0646 \u0631\u0627 \u0645\u06cc\u200c\u0632\u0646\u062f)");
        sb.AppendLine("- `WLUX|luxLow,luxHigh,stableMs,timeoutMs,mode` -> `OK|WLUX|lux=..` \u06cc\u0627 `ERR|TIMEOUT|WLUX`");
        sb.AppendLine("- `TRGLUX|luxLow,luxHigh,stableMs,timeoutMs,mode,vk,reactMin,reactMax,holdMin,holdMax` -> `EVT|TRGLUX|...` (\u06a9\u0644\u06cc\u062f \u0631\u0627 \u062e\u0648\u062f \u0628\u0631\u062f \u0645\u06cc\u200c\u0632\u0646\u062f)");
        sb.AppendLine("- `HALT` / `BYE` -> `OK|...`");
        sb.AppendLine("- v0.9.60: without the BH1750 wired, WLUX/TRGLUX/LCAL answer `ERR|NOSENSOR|...` and the brain stays alive (keyboard + arm keep working)");
        sb.AppendLine("- v0.9.64f: dense mouse paths stream fire-and-forget with coalescing (the newest target always lands); clicks stay fire-and-ack; arm events (EVT|) stream to the PC live");
        sb.AppendLine("- v0.9.64f: GP4 start/stop no longer teleports the cursor - the Pico tracks held buttons and releases only genuinely held ones (hold GP4 >= 1 s = panic release-all)");
        return sb.ToString();
    }

    /// <summary>Writes the whole bundle next to <paramref name="codePyPath"/> and returns the written paths.</summary>
    public static IReadOnlyList<string> Export(string codePyPath, IEnumerable<StepNode> steps, string machine,
        string loopMode = "forever", int loopCount = 0, int loopSeconds = 0, bool keyboardOnArm = false)   // v0.9.44 — Play Options + keyboard board
    {
        var dir = Path.GetDirectoryName(codePyPath);
        if (string.IsNullOrEmpty(dir)) throw new IOException("no target folder for the Pico bundle");
        var states = CollectStates(steps);
        var written = new List<string>();
        void Put(string name, string text)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllText(p, text);
            written.Add(p);
        }
        var settings = AppSettings.Load();
        Put(string.IsNullOrWhiteSpace(Path.GetFileName(codePyPath)) ? "code.py" : Path.GetFileName(codePyPath),
            BuildCodePy(states, machine, loopMode, loopCount, loopSeconds, keyboardOnArm,
                        settings.RunStopHotkey, settings.PauseResumeHotkey));
        Put("boot.py", BuildBootPy());
        Put("pico-calibration.json", BuildCalibrationJson(states, machine));
        Put("README-FLASH.md", BuildReadme(states, machine, loopMode, loopCount, loopSeconds, keyboardOnArm));   // v0.9.44
        return written;
    }
}
