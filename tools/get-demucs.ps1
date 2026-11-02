# Arma los modelos de "Separar en stems" (StemSeparator) en lib\demucs, fuera de git: baja los pesos de Demucs (Meta) y
# los convierte a ONNX con make-demucs-onnx.py, en un entorno de Python aparte. Pesan unos 425 MB y tardan unos minutos.
# El código de Demucs es MIT, pero la licencia de sus pesos no está clara: por eso no van en el instalador (ver PENDIENTES).
# Correlo de nuevo con -Reemplazar para rehacerlos.
param(
    [string]$Entorno = (Join-Path $env:TEMP 'xtreme-demucs'),
    [switch]$Reemplazar
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root 'lib\demucs'
if ($Reemplazar -and (Test-Path $dest)) { Remove-Item -Recurse -Force $dest }

if (-not (Get-Command uv -ErrorAction SilentlyContinue)) { throw 'Hace falta uv (https://docs.astral.sh/uv/).' }
$python = Join-Path $Entorno 'Scripts\python.exe'
if (-not (Test-Path $python)) { uv venv --python 3.12 $Entorno; if ($LASTEXITCODE) { throw 'No se pudo crear el entorno.' } }
uv pip install --python $python demucs==4.0.1 torch==2.5.1 torchaudio==2.5.1 'numpy<2' onnx onnxruntime
if ($LASTEXITCODE) { throw 'No se pudieron instalar los paquetes.' }

$env:TORCH_HOME = Join-Path $Entorno 'torch'
$env:PYTHONUTF8 = '1'
& $python -I (Join-Path $PSScriptRoot 'make-demucs-onnx.py') $dest
if ($LASTEXITCODE) { throw 'No se pudieron convertir los modelos.' }
Write-Host "Modelos de Demucs en $dest" -ForegroundColor Green
