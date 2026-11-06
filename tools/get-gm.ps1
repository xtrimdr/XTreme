# Baja el banco General MIDI (GeneralUser GS v2, de S. Christian Collins) a lib\gm, fuera de git como los DLL de FFmpeg.
# Es el sonido del instrumento General MIDI nativo (GmSynth): ~31 MB y su licencia (uso libre, también comercial, y se
# puede redistribuir con un programa), de un commit fijo para que todos usen el mismo banco. Va en el instalador.
# Correlo de nuevo con -Reemplazar para bajarlo otra vez.
param(
    [string]$Commit = '684543d5e5efaef08d02be50dcda8d552478fa60',
    [switch]$Reemplazar
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root 'lib\gm'
$bank = Join-Path $dest 'GeneralUser-GS.sf2'

if ((Test-Path $bank) -and -not $Reemplazar) {
    Write-Host "Ya está: $bank (usá -Reemplazar para bajarlo de nuevo)"
    exit 0
}
New-Item -ItemType Directory -Force $dest | Out-Null
$base = "https://raw.githubusercontent.com/mrbumpy409/GeneralUser-GS/$Commit"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$part = "$bank.part"
Invoke-WebRequest "$base/GeneralUser-GS.sf2" -OutFile $part -UseBasicParsing
if ((Get-Item $part).Length -lt 30MB) { Remove-Item $part; throw 'El banco bajó incompleto.' }
Move-Item -Force $part $bank
Invoke-WebRequest "$base/documentation/LICENSE.txt" -OutFile (Join-Path $dest 'LICENSE-GeneralUser-GS.txt') -UseBasicParsing
Write-Host "Banco General MIDI en $dest" -ForegroundColor Green
