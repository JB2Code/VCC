using System.Text.Json.Serialization;

namespace V60Control.Models;

public class AppSettings
{
    public string CameraIp { get; set; } = "192.168.100.88";
    public int ViscaPort { get; set; } = 5678;
    public int RtspPort { get; set; } = 554;

    /// <summary>Login der Kamera (RTSP-Authentifizierung). Standard laut Werkseinstellung: admin/admin.</summary>
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "admin";

    /// <summary>1 = Main-Stream (beste Qualität), 2 = Sub-Stream (geringere Bandbreite).</summary>
    public int StreamNumber { get; set; } = 1;

    /// <summary>Netzwerkpuffer in ms. Kleiner = weniger Delay, größer = stabiler.</summary>
    public int LatencyMs { get; set; } = 100;

    /// <summary>RTSP über TCP statt UDP (stabiler, minimal mehr Latenz).</summary>
    public bool RtspOverTcp { get; set; } = true;

    /// <summary>Optionale komplette RTSP-URL; überschreibt IP/Port/Stream, wenn gesetzt.</summary>
    public string? RtspUrlOverride { get; set; }

    public bool AudioMuted { get; set; } = true;

    public int PanTiltSpeed { get; set; } = 12;   // 1..24
    public int ZoomSpeed { get; set; } = 5;       // 0..7
    public int FocusSpeed { get; set; } = 4;      // 0..7

    /// <summary>Tempo beim Anfahren von Presets (1..14). Bewusst unter dem
    /// Protokoll-Maximum von 24: Presets werden langsamer ruhiger und meist
    /// wiederholgenauer angefahren.</summary>
    public int PresetSpeed { get; set; } = 6;

    public string GetRtspUrl()
    {
        if (!string.IsNullOrWhiteSpace(RtspUrlOverride))
            return RtspUrlOverride!;

        string credentials = "";
        if (!string.IsNullOrEmpty(Username))
        {
            // Benutzername/Passwort URL-codieren, damit Sonderzeichen (@ : / usw.) die URL nicht zerstören
            string user = Uri.EscapeDataString(Username);
            string pass = Uri.EscapeDataString(Password ?? "");
            credentials = $"{user}:{pass}@";
        }
        return $"rtsp://{credentials}{CameraIp}:{RtspPort}/{StreamNumber}";
    }

    /// <summary>Wie GetRtspUrl, aber ohne Passwort im Klartext – für Statusanzeige/Logs.</summary>
    [JsonIgnore]
    public string RtspUrlMasked
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(RtspUrlOverride))
                return RtspUrlOverride!;
            string credentials = string.IsNullOrEmpty(Username) ? "" : $"{Username}:***@";
            return $"rtsp://{credentials}{CameraIp}:{RtspPort}/{StreamNumber}";
        }
    }
}
