using System.Drawing;
using Ams.UI.Services;

namespace Ams.UI.ViewModels;

public partial class MainViewModel
{
    // Modern autonomous Pico Guard needs a host sample because the Pico/ARM
    // cannot query the Windows cursor. This is deliberately separate from the
    // legacy Golden-100 export/runtime path.
    private CancellationTokenSource? _cursorSyncCts;
    private Task? _cursorSyncTask;

    private void StartCursorSync()
    {
        StopCursorSync();
        if (_bridge is null || !PicoPresent) return;

        _cursorSyncCts = new CancellationTokenSource();
        _cursorSyncTask = CursorSyncLoopAsync(_cursorSyncCts.Token);
    }

    private void StopCursorSync()
    {
        var cts = _cursorSyncCts;
        _cursorSyncCts = null;
        _cursorSyncTask = null;
        try { cts?.Cancel(); } catch { }
        cts?.Dispose();
    }

    private async Task CursorSyncLoopAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            var bridge = _bridge;
            if (bridge is null || bridge.State != BridgeState.Connected || !PicoPresent) return;

            Point point = System.Windows.Forms.Cursor.Position;
            try
            {
                var reply = await bridge.SendAsync(CursorOriginSync.Command(point), 1.0, ct)
                    .ConfigureAwait(false);
                if (first && CursorOriginSync.IsAcknowledged(reply))
                {
                    first = false;
                    Log($"cursor origin sync enabled: {point.X},{point.Y}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Do not flood the serial log every 250 ms. A modern firmware
                // update can be installed after the app is already connected.
                if (first)
                {
                    first = false;
                    Log("cursor origin sync unavailable: " + ex.Message);
                }
            }

            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}