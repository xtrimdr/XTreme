# Baja el banco General MIDI GeneralUser GS v2 (de S. Christian Collins) a %APPDATA%\XTreme\gm, el mismo lugar donde lo
# deja la app al bajarlo desde Configuración → Componentes. No va con la app ni en el instalador (todos los bancos son
# descargas opcionales, #304): esto es para que xtlab tenga un banco con qué probar. Un commit fijo, así todos usan el
# mismo banco (GmBank.Downloads tiene su tamaño y su SHA-256). Correlo de nuevo con -Reemplazar para bajarlo otra vez.
param(
    [string]$Commit = '684543d5e5efaef08d02be50dcda8d552478fa60',
    [switch]$Reemplazar
)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:APPDATA 'XTreme\gm'
$bank = Join-Path $dest 'GeneralUser-GS.sf2'
$sha = '9575028C7A1F589F5770FCCC8CFF2734566AF40CD26ED836944E9A5152688CFE'

if ((Test-Path $bank) -and -not $Reemplazar) {
    Write-Host "Ya está: $bank (usá -Reemplazar para bajarlo de nuevo)"
    exit 0
}
New-Item -ItemType Directory -Force $dest | Out-Null
$base = "https://raw.githubusercontent.com/mrbumpy409/GeneralUser-GS/$Commit"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$part = "$bank.part"
Invoke-WebRequest "$base/GeneralUser-GS.sf2" -OutFile $part -UseBasicParsing
if ((Get-FileHash $part -Algorithm SHA256).Hash -ne $sha) { Remove-Item $part; throw 'El banco bajó incompleto o distinto.' }
Move-Item -Force $part $bank
Write-Host "Banco General MIDI en $dest" -ForegroundColor Green
