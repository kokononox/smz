using GuardHardwareMonitor;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
var d = new ArmDiagnostics();
var pong = "OK|PONG|combined-pico-guard-executor|native=abvm|abi=1|format=2|hid=on|uart=on|arm-ready=0|arm-usb=3|arm-ver=unknown|profiles=9|role=brain";
string first = d.Observe(pong)!;
Check(first.Contains("Not ready") && first.Contains("cached"), "initial state and cached usb");
Check(d.Observe(pong) is null, "repeated PONG should not spam");
string unknown = d.Observe("ERR|GUARD|ARM|ready=0|version=unknown|detail=reply=ERR|UNKNOWN")!;
Check(unknown.Contains("unrecognized-command") && unknown.Contains("GP16") && unknown.Contains("does not prove"), "user log unknown");
Check(d.Observe(pong) is null, "PONG must preserve fault, and not spam");
string timeout = d.Observe("ERR|ARM|detail=ack-timeout|version=unknown")!;
Check(timeout.Contains("No acknowledgement") && timeout.Contains("cannot identify"), "timeout");
Check(d.Observe("ERR|ARM|detail=reply=ERR|CKSUM|version=unknown")!.Contains("checksum"), "checksum");
Check(d.Observe("ERR|ARM|detail=reply=ERR|NOFRAME|version=unknown")!.Contains("Frame"), "noframe");
Check(d.Observe("ERR|ARM|detail=reply=ERR|INCOMPATIBLE|HVER|version=unknown")!.Contains("missing required"), "incompatible");
string healthy = d.Observe(pong.Replace("arm-ready=0", "arm-ready=1").Replace("arm-ver=unknown", "arm-ver=2.8.3-S5"))!;
Check(healthy.Contains("accepted") && d.Fault is null && healthy.Contains("does not verify"), "healthy resets fault");
Check(d.Observe("CONTROL|pause-ignored|state=stopped") is null, "ignore unrelated event");
var legacy = new ArmDiagnostics();
Check(legacy.Observe("OK|PONG|portable|role=brain")!.Contains("unavailable"), "legacy fields unknown");
var down = new ArmDiagnostics();
Check(down.Observe(pong.Replace("arm-usb=3", "arm-usb=1"))!.Contains("DOWN"), "USB down");
var offline = new ArmDiagnostics();
Check(offline.ObserveLogLine("[21:26:25.341] board identity: " + pong + " -> pico=on, arm=off")!.Contains("Not ready"), "Classroom log PONG");
Check(offline.ObserveLogLine("[20:51:45.454] [FAIL] ERR|GUARD|ARM|ready=0|version=unknown|detail=reply=ERR|UNKNOWN")!.Contains("unrecognized-command"), "monitor log error");
Check(offline.ObserveLogLine("[20:51:48] CONTROL|pause-ignored|state=stopped") is null, "offline unrelated event");
Console.WriteLine("Guard monitor diagnostics: all checks passed");
