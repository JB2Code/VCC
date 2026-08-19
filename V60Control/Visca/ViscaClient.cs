using System.Net.Sockets;

namespace V60Control.Visca;

/// <summary>
/// VISCA-over-IP-Client für die Edis V60CL-N (und kompatible PTZ-Kameras).
/// Sendet Standard-VISCA-Bytefolgen über eine persistente TCP-Verbindung
/// (Kamera-„PTZ Port", Standard 5678).
///
/// Die Verbindung wird aktiv gehalten: Die Kamera schließt einen untätigen Socket
/// nach kurzer Zeit, deshalb geht regelmäßig eine folgenlose Abfrage raus. Reißt die
/// Verbindung trotzdem ab (WLAN-Aussetzer, Kamera-Neustart), verbindet der Client
/// selbstständig neu, bis <see cref="Disconnect"/> gerufen wird.
/// </summary>
public sealed class ViscaClient : IDisposable
{
    /// <summary>Kürzer als die Leerlauf-Grenze der Kamera (ca. 60 s).</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    /// <summary>CAM_VersionInq – fragt nur die Firmware-Kennung ab und verändert nichts.</summary>
    private static readonly byte[] KeepAliveCommand = [0x81, 0x09, 0x00, 0x02, 0xFF];

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _gate = new();

    private TcpClient? _tcp;
    private NetworkStream? _stream;

    private string? _host;
    private int _port;
    /// <summary>Wird beim Verbindungsabbruch ausgelöst, damit die Pflegeschleife nicht
    /// bis zum nächsten Keepalive-Termin schläft, sondern sofort neu verbindet.</summary>
    private TaskCompletionSource _connectionLost = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Eine Dauerfahrt läuft, deren Stopp noch nicht bestätigt ist.
    /// Preset-Anfahrten zählen nicht dazu – die beendet die Kamera von selbst.</summary>
    private volatile bool _motionOutstanding;
    /// <summary>Lebt so lange, wie die Verbindung gehalten werden soll. Abgebrochen
    /// wird sie nur durch <see cref="Disconnect"/> – ein Verbindungsabbruch allein
    /// beendet die Sitzung nicht, sondern löst einen Neuversuch aus.</summary>
    private CancellationTokenSource? _session;

    public bool IsConnected => _tcp?.Connected == true;

    public event Action<bool>? ConnectionChanged;

    /// <summary>Die Verbindung ist weg und ein neuer Versuch läuft.</summary>
    public event Action? Reconnecting;

    // ------------------------------------------------------------------
    //  Verbindungsaufbau und -pflege
    // ------------------------------------------------------------------

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        Disconnect();

        _host = host;
        _port = port;

        var session = new CancellationTokenSource();
        _session = session;

        // Erster Versuch bewusst ohne Netz: Schlägt er fehl, soll der Benutzer die
        // Fehlermeldung sehen statt einer still im Hintergrund laufenden Schleife.
        try
        {
            await OpenSocketAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            session.Cancel();
            session.Dispose();
            _session = null;
            throw;
        }

        _ = Task.Run(() => MaintainAsync(session.Token), CancellationToken.None);
    }

    private async Task OpenSocketAsync(CancellationToken ct)
    {
        if (_host is null) throw new InvalidOperationException("Kein Host gesetzt.");

        var tcp = new TcpClient { NoDelay = true };
        tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout);

        try
        {
            await tcp.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        lock (_gate)
        {
            // Zuerst zurücksetzen: Ein Abriss unmittelbar nach dem Verbinden darf
            // nicht in einer bereits erfüllten Meldung untergehen.
            _connectionLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _tcp = tcp;
            _stream = tcp.GetStream();
        }

        _ = Task.Run(() => DrainRepliesAsync(tcp), CancellationToken.None);
        ConnectionChanged?.Invoke(true);
    }

    /// <summary>Hält die Verbindung offen: im Normalfall per Keepalive, nach einem
    /// Abriss per Neuversuch.</summary>
    private async Task MaintainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (IsConnected)
            {
                // Bis zum nächsten Keepalive warten – oder sofort weiter, sobald die
                // Verbindung abreißt.
                var lost = _connectionLost.Task;
                await Task.WhenAny(lost, Task.Delay(KeepAliveInterval, ct)).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                if (IsConnected) await SendAsync(KeepAliveCommand).ConfigureAwait(false);
            }
            else
            {
                Reconnecting?.Invoke();
                try
                {
                    await OpenSocketAsync(ct).ConfigureAwait(false);

                    // War beim Abriss eine Dauerfahrt aktiv, fährt die Kamera bis heute
                    // weiter – ihr Stopp ist nie angekommen. Jetzt nachholen.
                    if (_motionOutstanding) await StopAllAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Fehlgeschlagen – kurz warten und erneut versuchen.
                    try { await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
    }

    /// <summary>Vom Benutzer ausgelöstes Trennen: beendet auch die Wiederverbindung.</summary>
    public void Disconnect()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            try { session.Cancel(); } catch { }
            session.Dispose();
        }
        DropConnection();
    }

    /// <summary>Socket schließen, Sitzung aber am Leben lassen – die Wiederverbindung
    /// greift dann automatisch.</summary>
    private void DropConnection()
    {
        bool had;
        lock (_gate)
        {
            had = _tcp is not null;
            try { _stream?.Dispose(); } catch { }
            try { _tcp?.Dispose(); } catch { }
            _stream = null;
            _tcp = null;
            _connectionLost.TrySetResult();
        }
        if (had) ConnectionChanged?.Invoke(false);
    }

    /// <summary>Antworten der Kamera (ACK/Completion) auslesen und verwerfen,
    /// damit der Socket-Puffer nicht volläuft. Ein Lesen von 0 Bytes bedeutet:
    /// die Gegenseite hat die Verbindung geschlossen.</summary>
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

        // Nur reagieren, wenn dieser Socket noch der aktive ist – sonst wurde er
        // bereits durch einen neuen ersetzt.
        if (ReferenceEquals(tcp, _tcp)) DropConnection();
    }

    // ------------------------------------------------------------------
    //  Befehle
    // ------------------------------------------------------------------

    /// <summary>Sendet einen Befehl. Rückgabe: ob er die Kamera erreicht hat.</summary>
    private async Task<bool> SendAsync(params byte[] cmd)
    {
        var stream = _stream;
        if (stream is null) return false;

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(cmd).ConfigureAwait(false);
            return true;
        }
        catch
        {
            DropConnection();
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Startet eine Dauerfahrt oder beendet sie und merkt sich dabei, ob die
    /// Kamera gerade fährt. Geht der Stopp verloren, kann er nach dem Wiederverbinden
    /// nachgeholt werden.</summary>
    private async Task<bool> SendMotionAsync(bool moving, params byte[] cmd)
    {
        if (moving) _motionOutstanding = true;
        bool sent = await SendAsync(cmd).ConfigureAwait(false);
        if (!moving && sent) _motionOutstanding = false;
        return sent;
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
        return SendMotionAsync(panDir != 0 || tiltDir != 0,
            0x81, 0x01, 0x06, 0x01,
            Clamp(panSpeed, 1, 0x18), Clamp(tiltSpeed, 1, 0x14), pd, td, 0xFF);
    }

    public Task PanTiltStopAsync(int panSpeed, int tiltSpeed)
        => PanTiltAsync(0, 0, panSpeed, tiltSpeed);

    /// <summary>Hält alle laufenden Bewegungen an. Eine begonnene VISCA-Fahrt läuft,
    /// bis sie ausdrücklich gestoppt wird – geht ein Stopp verloren (Fokuswechsel,
    /// Verbindungsabriss), fährt die Kamera sonst weiter und verliert ihre Position.</summary>
    public async Task StopAllAsync()
    {
        await PanTiltStopAsync(1, 1).ConfigureAwait(false);
        await ZoomAsync(0, 0).ConfigureAwait(false);
        await FocusAsync(0, 0).ConfigureAwait(false);
    }

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
        return SendMotionAsync(dir != 0, 0x81, 0x01, 0x04, 0x07, b, 0xFF);
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
        return SendMotionAsync(dir != 0, 0x81, 0x01, 0x04, 0x08, b, 0xFF);
    }

    public Task FocusAutoAsync() => SendAsync(0x81, 0x01, 0x04, 0x38, 0x02, 0xFF);
    public Task FocusManualAsync() => SendAsync(0x81, 0x01, 0x04, 0x38, 0x03, 0xFF);
    public Task FocusOnePushAsync() => SendAsync(0x81, 0x01, 0x04, 0x18, 0x01, 0xFF);

    /// <summary>
    /// Tempo, mit dem die Kamera Presets anfährt (Pan 1–24, Tilt 1–20).
    ///
    /// Achtung: Das ist eine Hersteller-Erweiterung, kein Sony-Standardbefehl – im
    /// offiziellen VISCA-Satz werden Presets mit fester Geschwindigkeit angefahren.
    /// Kameras, die die Befehle nicht kennen, antworten mit einem Syntaxfehler und
    /// ignorieren sie folgenlos; das Anfahren bleibt dann wie bisher.
    /// </summary>
    public async Task SetPresetSpeedAsync(int panSpeed, int tiltSpeed)
    {
        await SendAsync(0x81, 0x01, 0x03, 0x01, Clamp(panSpeed, 1, 0x18), 0xFF).ConfigureAwait(false);
        await SendAsync(0x81, 0x01, 0x03, 0x02, Clamp(tiltSpeed, 1, 0x14), 0xFF).ConfigureAwait(false);
    }

    public Task PresetSetAsync(byte slot) => SendAsync(0x81, 0x01, 0x04, 0x3F, 0x01, slot, 0xFF);
    public Task PresetRecallAsync(byte slot) => SendAsync(0x81, 0x01, 0x04, 0x3F, 0x02, slot, 0xFF);
    public Task PresetClearAsync(byte slot) => SendAsync(0x81, 0x01, 0x04, 0x3F, 0x00, slot, 0xFF);

    public void Dispose()
    {
        Disconnect();
        _sendLock.Dispose();
    }
}
