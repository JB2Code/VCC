namespace V60Control.Models;

public class AppSettings
{
    public string CameraIp { get; set; } = "192.168.100.88";
    public int ViscaPort { get; set; } = 5678;
    public int RtspPort { get; set; } = 554;

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

    public string GetRtspUrl()
    {
        if (!string.IsNullOrWhiteSpace(RtspUrlOverride))
            return RtspUrlOverride!;
        return $"rtsp://{CameraIp}:{RtspPort}/{StreamNumber}";
    }
}
