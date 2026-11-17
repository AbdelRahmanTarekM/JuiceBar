namespace Bfm.Interop;

/// <summary>
/// Keeps one overlapped read pending on a HID collection and reports unsolicited device events.
/// Costs no CPU while idle; stops by itself when the device goes away.
/// </summary>
internal sealed class HidEventWatcher : IDisposable
{
    readonly HidChannel _channel;
    readonly CancellationTokenSource _cts = new();

    public bool IsAlive { get; private set; } = true;

    HidEventWatcher(HidChannel channel) => _channel = channel;

    public static HidEventWatcher? Start(HidInterface ifc, Func<byte[], bool> isEvent, Action onEvent)
    {
        var ch = HidChannel.Open(ifc);
        if (ch is null) return null;
        var w = new HidEventWatcher(ch);
        _ = w.Loop(isEvent, onEvent);
        return w;
    }

    async Task Loop(Func<byte[], bool> isEvent, Action onEvent)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var r = await _channel.ReadAsync(_cts.Token).ConfigureAwait(false);
                if (r is null) break;
                if (isEvent(r)) onEvent();
            }
        }
        catch (Exception ex)
        {
            Core.Log.Write("hid watcher stopped: " + ex.Message);
        }
        finally
        {
            IsAlive = false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Dispose();
        _cts.Dispose();
    }
}
