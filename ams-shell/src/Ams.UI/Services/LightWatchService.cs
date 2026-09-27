using System.Diagnostics;

using Ams.UI.Models;

namespace Ams.UI.Services;

/// <summary>
/// Cancellation-safe polling loop for read-only light telemetry. Calls are awaited serially,
/// so the service can never overlap LUX? requests or bypass the bridge command lock.
/// </summary>
public sealed class LightWatchService : IAsyncDisposable
{
    public const int DefaultIntervalMs = 250;
    private static readonly HashSet<int> AllowedIntervalsMs = new() { 100, 250, 500, 1000 };
    private readonly IBoardBridge _bridge;
    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private Task? _runner;

    public LightWatchService(IBoardBridge bridge) => _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));

    public event Action<LightTelemetrySample>? SampleReceived;
    public event Action<Exception>? WatchFaulted;

    public bool IsRunning
    {
        get { lock (_gate) return _runner is { IsCompleted: false }; }
    }

    public static TimeSpan GetStaleThreshold(TimeSpan sampleInterval)
    {
        if (sampleInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        return TimeSpan.FromMilliseconds(Math.Max(2000, sampleInterval.TotalMilliseconds * 3));
    }

    public Task StartAsync(TimeSpan interval, CancellationToken ct = default)
    {
        var intervalMs = checked((int)interval.TotalMilliseconds);
        if (!AllowedIntervalsMs.Contains(intervalMs) || interval != TimeSpan.FromMilliseconds(intervalMs))
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be exactly 100, 250, 500, or 1000 ms.");
        if (_bridge.State != BridgeState.Connected)
            throw new InvalidOperationException("Bridge must be connected before Watch starts.");

        lock (_gate)
        {
            if (_runner is { IsCompleted: false })
                throw new InvalidOperationException("Light Watch is already running.");
            _runCts?.Dispose();
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runner = RunAsync(interval, _runCts.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? runner;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            runner = _runner;
            cts = _runCts;
            cts?.Cancel();
        }
        if (runner is null) return;
        try { await runner.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_runner, runner))
                {
                    _runner = null;
                    _runCts = null;
                    cts?.Dispose();
                }
            }
        }
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _bridge.State == BridgeState.Connected)
        {
            var elapsed = Stopwatch.StartNew();
            try
            {
                var sample = await _bridge.ReadLightAsync(ct).ConfigureAwait(false);
                SampleReceived?.Invoke(sample);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                WatchFaulted?.Invoke(ex);
                break;
            }

            var remaining = interval - elapsed.Elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
