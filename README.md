# V60 Camera Control

Native Windows-Desktop-App (WPF, .NET 8) zur Steuerung der **Edis V60CL-N** PTZ-Kamera.

## Funktionen

- **Live-Video mit minimaler Latenz** über RTSP (LibVLC, Netzwerkpuffer einstellbar 0–500 ms, Standard 100 ms)
- **PTZ-Steuerung in alle Richtungen** (inkl. Diagonalen) per VISCA over IP (TCP, Port 5678) — Buttons gedrückt halten oder Pfeiltasten nutzen
- **Zoom & Fokus** (Nah/Fern/Auto), Geschwindigkeiten per Slider
- **Presets mit Vorschaubild**: Beim Speichern wird die Position in der Kamera abgelegt und ein Snapshot des Livebilds als Thumbnail gesichert. Klick auf die Kachel fährt die Position an; Rechtsklick: Aktualisieren / Umbenennen / Löschen
- Main-/Sub-Stream wählbar, RTSP über TCP oder UDP

## Bedienung

1. `dotnet build -c Release` (einmalig) — Ausgabe: `V60Control\bin\Release\net8.0-windows\V60Control.exe`
2. Kamera-IP eintragen → **Verbinden** (Standard-IP der Kamera: `192.168.100.88`, Web-Login admin/admin)
3. Tastatur: Pfeiltasten = Schwenken/Neigen · Bild↑/Bild↓ = Zoom · Pos1 = Home

## Tipps für wenig Verzögerung

- Puffer-Slider so weit runterdrehen, wie das Bild stabil bleibt (50–100 ms sind meist gut)
- Bei schwachem WLAN: Sub-Stream verwenden oder Kamera per Kabel anschließen
- „RTSP über TCP" abschalten (UDP) spart nochmal einige ms, ist aber empfindlicher gegen Paketverlust

## Datenablage

Einstellungen & Presets: `%AppData%\V60Control\` (settings.json, presets.json, thumbnails\)

## Technik

- `Visca/ViscaClient.cs` — VISCA-over-IP-Befehle (Pan/Tilt/Zoom/Fokus/Presets 1–254)
- `MainWindow.xaml(.cs)` — UI, Video (LibVLCSharp), Tastatur- und Pad-Steuerung
- `Services/Storage.cs` — JSON-Persistenz
