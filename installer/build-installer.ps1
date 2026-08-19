<#
    Baut das MSI-Setup fuer V60 Camera Control.

    Ablauf:  dotnet publish (self-contained, win-x64)  ->  [signieren]  ->  wix build  ->  [signieren]

    Voraussetzungen (einmalig):
        dotnet tool install --global wix --version 5.0.2
        wix extension add --global WixToolset.UI.wixext/5.0.2

    Aufrufe:
        pwsh -File installer\build-installer.ps1
        pwsh -File installer\build-installer.ps1 -Version 1.1.0
        pwsh -File installer\build-installer.ps1 -CertThumbprint A1B2...   # signiert
#>
[CmdletBinding()]
param(
    # Ueberschreibt die Versionsnummer aus der csproj (Format x.y.z).
    [string]$Version,

    # publish-Schritt ueberspringen und vorhandenen Ordner weiterverwenden.
    [switch]$SkipPublish,

    # --- Signierung (optional) -------------------------------------------
    # Variante A: Zertifikat liegt im Windows-Zertifikatspeicher (auch Hardware-Token).
    # Thumbprint zeigt:  Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert
    [string]$CertThumbprint,

    # Variante B: Zertifikat als .pfx-Datei. Das Passwort erwartet das Skript in der
    # Umgebungsvariablen V60_SIGN_PFX_PASSWORD - so steht es in keiner Datei und in
    # keinem Kommandozeilen-Verlauf.
    [string]$PfxPath,

    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    # signtool.exe bei Bedarf als NuGet-Paket nach installer\.tools\ holen.
    [switch]$AcquireSignTool
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $PSScriptRoot
$csproj     = Join-Path $root 'V60Control\V60Control.csproj'
$publishDir = Join-Path $PSScriptRoot 'publish'
$outDir     = Join-Path $PSScriptRoot 'out'
$toolsDir   = Join-Path $PSScriptRoot '.tools'
$iconFile   = Join-Path $root 'V60Control\app.ico'
$licenseRtf = Join-Path $PSScriptRoot 'License.rtf'

$signing = [bool]($CertThumbprint -or $PfxPath)

# ==========================================================================
#  Werkzeuge
# ==========================================================================

# Der Top-Level-dotnet ist auf diesem Rechner die x86-Variante; WPF/win-x64
# braucht den x64-Host.
$dotnet = @(
    'C:\Program Files\dotnet\x64\dotnet.exe'
    'C:\Program Files\dotnet\dotnet.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $dotnet) { throw 'dotnet nicht gefunden.' }

$wix = Get-Command wix -ErrorAction SilentlyContinue
if ($wix) { $wix = $wix.Source }
else {
    $candidate = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
    if (Test-Path $candidate) { $wix = $candidate }
    else { throw 'WiX fehlt. Installieren mit:  dotnet tool install --global wix --version 5.0.2' }
}

function Get-SignTool {
    # 1. Windows SDK
    $sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -match '\\x64\\' } |
           Sort-Object FullName -Descending | Select-Object -First 1
    if ($sdk) { return $sdk.FullName }

    # 2. Bereits nach installer\.tools\ geholt
    $cached = Get-ChildItem $toolsDir -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
    if ($cached) { return $cached.FullName }

    # 3. Auf Wunsch als NuGet-Paket nachladen
    if ($AcquireSignTool) {
        Write-Host '==> signtool.exe wird von nuget.org geladen (Microsoft.Windows.SDK.BuildTools) ...' -ForegroundColor Cyan
        New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
        $nupkg = Join-Path $toolsDir 'sdk-buildtools.zip'
        $url   = 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/10.0.22621.3233/microsoft.windows.sdk.buildtools.10.0.22621.3233.nupkg'
        Invoke-WebRequest -Uri $url -OutFile $nupkg
        Expand-Archive $nupkg -DestinationPath (Join-Path $toolsDir 'sdk') -Force
        Remove-Item $nupkg -Force
        $found = Get-ChildItem (Join-Path $toolsDir 'sdk') -Recurse -Filter signtool.exe |
                 Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
        if ($found) { return $found.FullName }
    }

    throw "signtool.exe nicht gefunden. Entweder das Windows SDK installieren (winget install Microsoft.WindowsSDK) oder dieses Skript einmal mit -AcquireSignTool aufrufen - dann holt es signtool als NuGet-Paket nach installer\.tools\ (ca. 30 MB Download von nuget.org)."
}

function Invoke-Sign {
    param([Parameter(Mandatory)][string[]]$Path, [Parameter(Mandatory)][string]$SignTool)

    $signArgs = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')

    if ($CertThumbprint) {
        $signArgs += @('/sha1', $CertThumbprint)
    }
    else {
        if (-not (Test-Path $PfxPath)) { throw "PFX-Datei nicht gefunden: $PfxPath" }
        $pw = $env:V60_SIGN_PFX_PASSWORD
        if (-not $pw) {
            throw 'Passwort fuer die .pfx fehlt. Vor dem Build in $env:V60_SIGN_PFX_PASSWORD setzen. Komfortabler und sicherer: die .pfx einmalig mit Import-PfxCertificate in den Zertifikatspeicher uebernehmen und danach nur noch -CertThumbprint verwenden.'
        }
        $signArgs += @('/f', $PfxPath, '/p', $pw)
    }

    & $SignTool @signArgs @Path
    if ($LASTEXITCODE -ne 0) { throw "Signieren fehlgeschlagen (Exit $LASTEXITCODE)." }
}

# ==========================================================================
#  Version
# ==========================================================================
if (-not $Version) {
    $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' ist nicht im Format x.y.z." }

Write-Host "==> V60 Camera Control $Version" -ForegroundColor Cyan
if ($signing) {
    $signTool = Get-SignTool
    Write-Host "    Signierung aktiv ($signTool)"
} else {
    Write-Host '    ohne Signierung - SmartScreen wird beim ersten Start warnen' -ForegroundColor Yellow
}

# ==========================================================================
#  1. Publish
# ==========================================================================
if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    Write-Host '==> dotnet publish (self-contained, win-x64) ...' -ForegroundColor Cyan
    & $dotnet publish $csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=none `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen (Exit $LASTEXITCODE)." }
}

$exe = Join-Path $publishDir 'V60Control.exe'
if (-not (Test-Path $exe)) { throw "V60Control.exe fehlt in $publishDir." }
if (-not (Test-Path (Join-Path $publishDir 'libvlc\win-x64\libvlc.dll'))) {
    throw 'libvlc\win-x64 fehlt im publish-Ordner - ohne die Bibliothek gibt es kein Videobild.'
}

$payload = '{0:N0} MB' -f ((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "    Nutzdaten: $payload"

# ==========================================================================
#  2. Anwendung signieren (vor dem Verpacken)
# ==========================================================================
# Nur die eigenen Binaries. libvlc & Co. gehoeren VideoLAN bzw. Microsoft -
# fremde Dateien mit dem eigenen Zertifikat zu ueberschreiben waere falsch.
if ($signing) {
    Write-Host '==> Anwendung signieren ...' -ForegroundColor Cyan
    Invoke-Sign -SignTool $signTool -Path @($exe, (Join-Path $publishDir 'V60Control.dll'))
}

# ==========================================================================
#  3. MSI bauen
# ==========================================================================
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$msi = Join-Path $outDir "V60CameraControl-$Version-x64.msi"

Write-Host '==> wix build ...' -ForegroundColor Cyan
& $wix build (Join-Path $PSScriptRoot 'V60Control.wxs') `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -d Version=$Version `
    -d PublishDir=$publishDir `
    -d IconFile=$iconFile `
    -d LicenseFile=$licenseRtf `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build fehlgeschlagen (Exit $LASTEXITCODE)." }

# ==========================================================================
#  4. MSI signieren
# ==========================================================================
if ($signing) {
    Write-Host '==> MSI signieren ...' -ForegroundColor Cyan
    Invoke-Sign -SignTool $signTool -Path @($msi)

    $sig = Get-AuthenticodeSignature $msi
    Write-Host "    Status: $($sig.Status) - $($sig.SignerCertificate.Subject)"
    if ($sig.Status -ne 'Valid') { throw "Signatur ist nicht gueltig: $($sig.StatusMessage)" }
}

$size = '{0:N0} MB' -f ((Get-Item $msi).Length / 1MB)
Write-Host ''
Write-Host "Fertig: $msi  ($size)" -ForegroundColor Green
if (-not $signing) { Write-Host 'Hinweis: unsigniert.' -ForegroundColor Yellow }
