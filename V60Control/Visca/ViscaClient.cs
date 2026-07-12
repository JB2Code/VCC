using System.Net.Sockets;

namespace V60Control.Visca;

/// <summary>
/// VISCA-over-IP-Client für die Edis V60CL-N (und kompatible PTZ-Kameras).
/// Sendet Standard-VISCA-Bytefolgen über eine persistente TCP-Verbindung
/// (Kamera-„PTZ Port", Standard 5678).
/// </summary>
public sealed class ViscaClient : IDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _tcp?.Connected == true;
    public event Action<bool>? ConnectionChanged;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        Disconnect();
        var tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        _tcp = tcp;
        _stream = tcp.GetStream();
        _ = Task.Run(() => DrainRepliesAsync(tcp));
        ConnectionChanged?.Invoke(true);
    }

    public void Disconnect()
    {
        var had = _tcp is not null;
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
        if (had) ConnectionChanged?.Invoke(false);
    }

    /// <summary>Antworten der Kamera (ACK/Completion) auslesen und verwerfen,
    /// damit der Socket-Puffer nicht volläuft.</summary>
    private async Task DrainRepliesAsync(TcpClient tcp)
    {
        var buf = new byte[256];
        try
        {
            var stream = tcp.GetStream();
            while (tcp.Connected)
            {
                int n = await stream.ReadAsync(buf).ConfigureAwait(false);
                if (n == 0) break;
            }
        }
        catch { }
        if (ReferenceEquals(tcp, _tcp)) Disconnect();
    }

    private async Task SendAsync(params byte[] cmd)
    {
        var stream = _stream;
        if (stream is null) return;
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(cmd).ConfigureAwait(false);
        }
        catch
        {
            Disconnect();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static byte Clamp(int v, int min, int max) => (byte)Math.Clamp(v, min, max);

    /// <summary>
    /// Kontinuierliche Pan/Tilt-Fahrt. Richtung: -1 / 0 / +1.
    /// panDir: -1 = links, +1 = rechts. tiltDir: -1 = runter, +1 = hoch.
    /// panSpeed 1–24, tiltSpeed 1–20.
    /// </summary>
    public Task PanTiltAsync(int panDir, int tiltDir, int panSpeed, int tiltSpeed)
    {
        byte pd = panDir switch { < 0 => 0x01, > 0 => 0x02, _ => 0x03 };
        byte td = tiltDir switch { > 0 => 0x01, < 0 => 0x02, _ => 0x03 };
        return SendAsync(0x81, 0x01, 0x06, 0x01,
            Clamp(panSpeed, 1, 0x18), Clamp(tiltSpeed, 1, 0x14), pd, td, 0xFF);
    }

    public Task PanTiltStopAsync(int panSpeed, int tiltSpeed)
        => PanTiltAsync(0, 0, panSpeed, tiltSpeed);

    public Task HomeAsync() => SendAsync(0x81, 0x01, 0x06, 0x04, 0xFF);

    /// <summary>Zoom: dir +1 = Tele (rein), -1 = Weitwinkel (raus), 0 = Stopp. Speed 0–7.</summary>
    public Task ZoomAsync(int dir, int speed)
    {
        byte b = dir switch
        {
            > 0 => (byte)(0x20 | Clamp(speed, 0, 7)),
            < 0 => (byte)(0x30 | Clamp(speed, 0, 7)),
            _ => 0x00
        };
        return SendAsync(0x81, 0x01, 0x04, 0x07, b, 0xFF);
    }

    /// <summary>Fokus: dir +1 = fern, -1 = nah, 0 = Stopp. Speed 0–7.</summary>
    public Task FocusAsync(int dir, int speed)
    {
        byte b = dir switch
        {
            > 0 => (byte)(0x20 | Clamp(speed, 0, 7)),
            < 0 => (byte)(0x30 | Clamp(speed, 0, 7)),
            _ => 0x00
        };
        return SendAsync(0x81, 0x01, 0x04, 0x08, b, 0xFF);
    }

    public Task FocusAutoAsync() => SendAsync(0x81, 0x01, 0x04, 0x38, 0x02, 0xFF);
    public Task FocusManualAsync() => SendAsync(0x81, 0x01, 0x04, 0x38, 0x03, 0xFF);
    public Task FocusOnePushAsync() => SendAsync(0x81, 0x01, 0x04, 0x18, 0x01, 0xFF);

    public Task PresetSetAsync(byte slot) => SendAsync(0x81, 0x01, 0x04, 0x3F, 0x01, slot, 0xFF);
    public Task PresetRecallAsync(byte slot) => SendAsync(0x81, 0x01, 0x04, 0x3F, 0x02, slot, 0xFF);
    public Task PresetClearAsync(byte slot) => SendAsync(0x81, 0x01, 0x04, 0x3F, 0x00, slot, 0xFF);

    public void Dispose()
    {
        Disconnect();
        _sendLock.Dispose();
    }
}
