using Ams.UI.Models;
using Ams.UI.Services;

var passed = 0;
var failed = 0;
void Check(bool condition, string name)
{
    if (condition) { passed++; Console.WriteLine("PASS: " + name); }
    else { failed++; Console.WriteLine("FAIL: " + name); }
}
void Rejects(string reply, string name)
{
    try { LightTelemetryParser.Parse(reply); Check(false, name); }
    catch (LightTelemetryProtocolException) { Check(true, name); }
}

await LightExecutionAuthorizationConcurrencyContract.RunAsync();

var at = new DateTimeOffset(2026, 9, 14, 19, 0, 0, TimeSpan.Zero);
var ok = LightTelemetryParser.Parse("OK|LUX|seq=4294967295|lux=59.2|mode=hires|sensor=ok", at);
Check(ok.IsSuccess && ok.Sequence == uint.MaxValue && ok.Lux == 59.2 && ok.Mode == "hires" && ok.ReceivedAt == at,
    "typed parser accepts valid success and uint wrap boundary");
var low = LightTelemetryParser.Parse("OK|LUX|seq=0|lux=0.0|mode=lowres|sensor=ok");
Check(low.IsSuccess && low.Sequence == 0 && low.Lux == 0 && low.Mode == "lowres",
    "typed parser accepts lowres and zero after sequence wrap");
var native = LightTelemetryParser.Parse("OK|LUX|lux=51.7|sensor=ok|age=40");
Check(native.IsSuccess && native.Lux == 51.7 && native.Sequence is null && native.Mode is null,
    "typed parser accepts Native Pico sample age");
Check(LightTelemetryParser.Parse("ERR|NOSENSOR|LUX").Status == LightTelemetryStatus.NoSensor,
    "missing sensor maps to typed status");
Check(LightTelemetryParser.Parse("ERR|I2C|LUX").Status == LightTelemetryStatus.I2cError,
    "I2C failure maps to typed status");
Check(LightTelemetryParser.Parse("ERR|BUSY|LUX").Status == LightTelemetryStatus.Busy,
    "Busy maps to typed status");
Rejects("LUX?", "request text cannot be parsed as a reply");
Rejects("OK|LUX|seq=1|lux=NaN|mode=hires|sensor=ok", "NaN is rejected");
Rejects("OK|LUX|seq=1|lux=-1|mode=hires|sensor=ok", "negative lux is rejected");
Rejects("OK|LUX|seq=1|lux=1.0|mode=turbo|sensor=ok", "unknown mode is rejected");
Rejects("OK|LUX|seq=1|lux=1.0|mode=hires|sensor=bad", "invalid sensor marker is rejected");
Rejects("OK|LUX|lux=1.0|sensor=ok|age=-1", "negative Native sample age is rejected");
Rejects("OK|LUX|lux=1.0|sensor=ok|extra=1", "unknown compact telemetry field is rejected");
Rejects("ERR|NOSENSOR|LUX|extra", "typed errors must be exact");
var dump = LightCalibrationDumpParser.Parse(
    "OK|CALDUMP|LIGHT|revision=42|mask=91|profiles=1:5:25,5:250:290,8:599:620");
Check(dump.Revision == 42 && dump.Mask == 0x91 && dump.Profiles.Count == 3
      && dump.Profiles[0].CenterLux == 1.5 && dump.Profiles[0].ToleranceLux == 1.0
      && dump.Profiles[2].CenterLux == 60.95 && dump.Profiles[2].ToleranceLux == 1.05,
    "light calibration dump parses tenths and partial profile mask");
var importTargets = LightStateDefaults.CreateInitialProfiles();
Check(LightCalibrationDumpParser.Apply(dump, importTargets) == 3
      && importTargets[0].LuxCenter == 1.5 && importTargets[4].LuxCenter == 27
      && importTargets[7].LuxTolerance == 1.05
      && importTargets[1].LuxCenter == 25,
    "board calibration import atomically updates only returned ranges");
try
{
    LightCalibrationDumpParser.Parse(
        "OK|CALDUMP|LIGHT|revision=42|mask=01|profiles=1:30:20");
    Check(false, "light calibration dump rejects inverted range");
}
catch (LightCalibrationDumpProtocolException)
{
    Check(true, "light calibration dump rejects inverted range");
}
try
{
    LightCalibrationDumpParser.Parse(
        "OK|CALDUMP|LIGHT|revision=42|mask=03|profiles=1:10:20");
    Check(false, "light calibration dump rejects mismatched mask");
}
catch (LightCalibrationDumpProtocolException)
{
    Check(true, "light calibration dump rejects mismatched mask");
}
Check(LightCalibrationDumpParser.Parse(
        "OK|CALDUMP|LIGHT|revision=0|mask=00|profiles=none").Profiles.Count == 0,
    "empty board calibration dump is represented explicitly");
Check(LightWatchService.DefaultIntervalMs == 250,
    "Watch default interval is 250 ms");
Check(new[] { 100, 250, 500 }.All(ms =>
        LightWatchService.GetStaleThreshold(TimeSpan.FromMilliseconds(ms)) == TimeSpan.FromSeconds(2)),
    "stale threshold keeps the two-second floor for fast intervals");
Check(LightWatchService.GetStaleThreshold(TimeSpan.FromMilliseconds(1000)) == TimeSpan.FromSeconds(3),
    "stale threshold grows to three intervals when larger");

var oneRead = new FakeBridge(new[] { "OK|LUX|seq=7|lux=12.5|mode=hires|sensor=ok" });
IBoardBridge oneReadContract = oneRead;
var read = await oneReadContract.ReadLightAsync();
Check(read.Sequence == 7 && oneRead.Commands.SequenceEqual(new[] { "LUX?" }) && oneRead.LastTimeoutSeconds == 2,
    "ReadLightAsync sends exact LUX? through the serialized bridge path");

var watchBridge = new FakeBridge(Enumerable.Range(1, 20)
    .Select(i => $"OK|LUX|seq={i}|lux={50 + i}.0|mode=hires|sensor=ok")) { DelayMs = 45 };
var watch = new LightWatchService(watchBridge);
var samples = new List<LightTelemetrySample>();
watch.SampleReceived += sample => { lock (samples) samples.Add(sample); };
await watch.StartAsync(TimeSpan.FromMilliseconds(100));
try
{
    await watch.StartAsync(TimeSpan.FromMilliseconds(100));
    Check(false, "duplicate Watch start is rejected");
}
catch (InvalidOperationException) { Check(true, "duplicate Watch start is rejected"); }
await Task.Delay(360);
await watch.StopAsync();
var stoppedCalls = watchBridge.Commands.Count;
await Task.Delay(150);
Check(stoppedCalls >= 3 && watchBridge.MaxInFlight == 1,
    "Watch takes repeated samples with exactly one request in flight");
Check(watchBridge.Commands.Count == stoppedCalls && !watch.IsRunning,
    "StopAsync cancels and fully joins the Watch loop");
Check(samples.Count >= 3 && samples.Select(s => s.Sequence)
        .SequenceEqual(Enumerable.Range(1, samples.Count).Select(i => (uint?)i)),
    "Watch publishes completed typed samples in sequence order");
Check(stoppedCalls - samples.Count is 0 or 1,
    "cancellation drops at most the final in-flight sample");
await watch.DisposeAsync();

var disconnected = new FakeBridge(Array.Empty<string>()) { Connected = false };
try
{
    await new LightWatchService(disconnected).StartAsync(TimeSpan.FromMilliseconds(250));
    Check(false, "Watch requires a connected bridge");
}
catch (InvalidOperationException) { Check(true, "Watch requires a connected bridge"); }
try
{
    await new LightWatchService(oneRead).StartAsync(TimeSpan.FromMilliseconds(125));
    Check(false, "Watch rejects unsupported intervals");
}
catch (ArgumentOutOfRangeException) { Check(true, "Watch rejects unsupported intervals"); }

Console.WriteLine($"=== Light telemetry results: {passed} passed, {failed} failed ===");
return failed == 0 ? 0 : 1;

sealed class FakeBridge : IBoardBridge
{
    private readonly Queue<string> _replies;
    private int _inFlight;
    public FakeBridge(IEnumerable<string> replies) => _replies = new Queue<string>(replies);
    public List<string> Commands { get; } = new();
    public int DelayMs { get; init; }
    public int MaxInFlight { get; private set; }
    public double? LastTimeoutSeconds { get; private set; }
    public bool Connected { get; init; } = true;
    public event EventHandler<string>? LineReceived { add { } remove { } }
    public event EventHandler<BridgeState>? StateChanged { add { } remove { } }
    public BridgeState State => Connected ? BridgeState.Connected : BridgeState.Disconnected;
    public string? FirmwareVersion => "phase-three-test";
    public string? Port => "FAKE";
    public Task ConnectAsync(string portName, CancellationToken ct = default) => Task.CompletedTask;
    public async Task<string> SendAsync(string command, double? timeoutSeconds = null, CancellationToken ct = default)
    {
        var current = Interlocked.Increment(ref _inFlight);
        MaxInFlight = Math.Max(MaxInFlight, current);
        try
        {
            lock (Commands) Commands.Add(command);
            LastTimeoutSeconds = timeoutSeconds;
            if (DelayMs > 0) await Task.Delay(DelayMs, ct);
            lock (_replies)
                return _replies.Count > 0 ? _replies.Dequeue() : "ERR|BUSY|LUX";
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }
    public Task SendAbortAsync() => Task.CompletedTask;
    public Task<string> SendPathAsync(IReadOnlyList<(int X, int Y, int DelayMs)> points, CancellationToken ct = default)
        => Task.FromResult("OK|PATH,0");
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
