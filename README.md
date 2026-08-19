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

## Installer bauen

Das Setup ist ein **MSI** (WiX 5), self-contained: die .NET-8-Laufzeit und libVLC stecken im Paket,
auf dem Zielrechner muss nichts vorinstalliert sein. Voraussetzung ist 64-Bit-Windows.

Einmalig die Werkzeuge installieren:

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add --global WixToolset.UI.wixext/5.0.2
```

Dann bauen:

```powershell
pwsh -File installer\build-installer.ps1
```

Ergebnis: `installer\out\V60CameraControl-<Version>-x64.msi` (ca. 83 MB, installiert ca. 247 MB).
Das Skript ruft `dotnet publish -r win-x64 --self-contained` auf und packt den Ordner anschließend per WiX.

- Andere Versionsnummer: `-Version 1.1.0` (sonst wird die `<Version>` aus der csproj genommen)
- Nur MSI neu packen, ohne Rebuild: `-SkipPublish`
- WiX 6/7 sind bewusst nicht im Einsatz — sie verlangen die Zustimmung zur kostenpflichtigen
  „Open Source Maintenance Fee"-Lizenz; WiX 5 ist frei

Das Setup installiert nach `C:\Program Files\V60 Camera Control` (im Dialog änderbar), legt
Verknüpfungen im Startmenü und auf dem Desktop an und erscheint regulär unter *Apps & Features*.
Ein neues MSI mit höherer Versionsnummer ersetzt die alte Installation automatisch (Major Upgrade).
Einstellungen und Presets unter `%AppData%\V60Control\` bleiben bei Deinstallation erhalten.

### Signieren

Ohne Signatur zeigt Windows beim ersten Start eine SmartScreen-Warnung („Der Computer wurde durch
Windows geschützt"). Mit einem Code-Signing-Zertifikat verschwindet sie. Das Skript signiert dann
`V60Control.exe`/`.dll` **vor** dem Verpacken und das fertige MSI danach — beides mit SHA-256 und
RFC-3161-Zeitstempel, damit die Signatur auch nach Ablauf des Zertifikats gültig bleibt.

Zertifikat im Windows-Zertifikatspeicher (auch Hardware-Token / HSM):

```powershell
pwsh -File installer\build-installer.ps1 -CertThumbprint <Fingerabdruck>
```

Den Fingerabdruck zeigt `Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert`.

Alternativ direkt aus einer `.pfx` — das Passwort liest das Skript aus der Umgebungsvariablen
`V60_SIGN_PFX_PASSWORD`, damit es nicht in der Kommandozeile oder im Skript landet:

```powershell
pwsh -File installer\build-installer.ps1 -PfxPath C:\pfad\zertifikat.pfx
```

Fehlt `signtool.exe`, holt `-AcquireSignTool` sie einmalig als NuGet-Paket nach `installer\.tools\`;
alternativ `winget install Microsoft.WindowsSDK`. Fremde Binärdateien (libVLC, .NET-Laufzeit) werden
bewusst **nicht** mitsigniert — die tragen die Signatur ihrer eigenen Hersteller.

### Deinstallation

Die App erscheint unter *Einstellungen → Apps → Installierte Apps* bzw. *Systemsteuerung →
Programme und Features* als „V60 Camera Control" von J2Code und lässt sich dort regulär entfernen.
Ändern/Reparieren sind ausgeblendet (`ARPNOMODIFY`, `ARPNOREPAIR`), es gibt also nur „Deinstallieren".

### Dateien

- `installer\build-installer.ps1` — Publish, Signierung, MSI-Build
- `installer\V60Control.wxs` — WiX-Paketdefinition (Verzeichnisse, Verknüpfungen, Upgrade-Logik)
- `installer\License.rtf` — Lizenztext im Setup-Dialog (u. a. LGPL-Hinweis für libVLC)
