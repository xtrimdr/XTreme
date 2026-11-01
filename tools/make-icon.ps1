# Makes the XTreme icon and writes a multi-size .ico, the title bar's 64 px logo and the PNGs in docs\.
# A black vinyl with a giant orange label and a black X printed on it, round at every size, transparent around it.
# Big, the record is rendered in Blender (tools\icon\vinilo.py: top view, anisotropic grooves, four lights on the
# diagonals). Small (taskbar, shortcuts, title bar), the same idea is drawn flat here.
# Everything in between goes to artifacts\icon (not in git).
#   tools\make-icon.ps1               -> renders and writes the icon (under a minute)
#   tools\make-icon.ps1 -SinRender    -> only writes the icon again from the last render (to tweak the sizes)
param([switch]$SinRender)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\XtremeDaw.App\Assets'
$work = Join-Path $root 'artifacts\icon'
$big = Join-Path $work 'disco.png'
# The rendered record is used from this size up; below it the flat mark, which still reads at 16 px
$RenderedFrom = 128
$BigRender = 1024
# The flat mark, in fractions of the icon's side: the vinyl left around the label (at least 1 px), and the X
$MarkVinyl = 0.07
$MarkXHalf = 0.22
$MarkXWidth = 0.15
# Drawn this many times bigger and reduced: GDI+'s own antialiasing leaves a 16 px circle lopsided
$MarkOversample = 8
# Brighter than the render's copper: at this size the label is a flat color and has to carry the contrast
$MarkOrange = '#FF7A1A'
$MarkBlack = '#0B0B0D'

# Runs an outside program and returns its output: Windows PowerShell turns anything it writes to stderr (Blender's
# harmless warnings) into an error under 'Stop', so its result is checked by the caller instead
function Run([string]$exe) {
  $ErrorActionPreference = 'Continue'
  & $exe @args 2>&1 | ForEach-Object { "$_" }
}

function Need([string]$what, [string]$path, [string]$how) {
  if (-not $path -or -not (Test-Path $path)) { throw "Falta $what. $how" }
  $path
}

if (-not $SinRender) {
  $blender = Get-ChildItem 'C:\Program Files\Blender Foundation\*\blender.exe' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
  $blender = Need 'Blender' $blender 'Instalalo con: winget install BlenderFoundation.Blender'
  New-Item -ItemType Directory -Force $work | Out-Null
  Write-Host 'Renderizando en Blender...' -ForegroundColor Cyan
  $scene = Join-Path $PSScriptRoot 'icon\vinilo.py'
  # '--' quoted: bare, PowerShell would take it as the end of Run's own parameters and drop it
  $out = Run $blender -b --factory-startup -P $scene '--' $big $BigRender
  if (-not ($out | Select-String '^OK ')) { $out | Select-Object -Last 15 | Write-Host; throw 'El render de Blender falló.' }
}
$lensBig = [System.Drawing.Image]::FromFile((Need 'el render' $big 'Corré make-icon.ps1 sin -SinRender.'))

function Save-Png($bmp) {
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  return , $ms.ToArray()
}

# The small icon, flat
function New-MarkPng([int]$s) {
  $b = $s * $MarkOversample
  $hi = New-Object System.Drawing.Bitmap $b, $b
  $g = [System.Drawing.Graphics]::FromImage($hi)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $black = [System.Drawing.ColorTranslator]::FromHtml($MarkBlack)
  $orange = [System.Drawing.ColorTranslator]::FromHtml($MarkOrange)
  $g.FillEllipse((New-Object System.Drawing.SolidBrush $black), 0, 0, $b, $b)
  $v = [Math]::Max($MarkVinyl * $s, 1) * $MarkOversample
  $g.FillEllipse((New-Object System.Drawing.SolidBrush $orange), [float]$v, [float]$v, [float]($b - 2 * $v), [float]($b - 2 * $v))
  $pen = New-Object System.Drawing.Pen $black, ([float]($MarkXWidth * $b))
  $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
  $c = $b / 2.0; $h = $MarkXHalf * $b
  $g.DrawLine($pen, [float]($c - $h), [float]($c - $h), [float]($c + $h), [float]($c + $h))
  $g.DrawLine($pen, [float]($c - $h), [float]($c + $h), [float]($c + $h), [float]($c - $h))
  $g.Dispose()
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.DrawImage($hi, 0, 0, $s, $s)
  $g.Dispose(); $hi.Dispose()
  Save-Png $bmp
}

function New-IconPng([int]$s) {
  if ($s -lt $RenderedFrom) { return New-MarkPng $s }
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.DrawImage($lensBig, 0, 0, $s, $s)
  $g.Dispose()
  Save-Png $bmp
}

$out = Join-Path $assets 'XTreme.ico'
# 72 and 96: a 48 px shortcut at 150 and 200 % display scaling would otherwise scale the rendered frame down
$sizes = 16, 20, 24, 32, 40, 48, 64, 72, 96, 128, 256
$images = $sizes | ForEach-Object { , (New-IconPng $_) }
$fs = [System.IO.File]::Create($out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
  $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
  $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
[System.IO.File]::WriteAllBytes((Join-Path $root 'docs\icon-256.png'), $images[-1])
[System.IO.File]::WriteAllBytes((Join-Path $root 'docs\icon-32.png'), $images[3])
# The title bar's logo (24 px, sharp up to 250 % display scaling), and the big one of the splash, "Acerca de" and the READMEs
[System.IO.File]::WriteAllBytes((Join-Path $assets 'XTreme-64.png'), $images[6])
$icon512 = New-IconPng 512
[System.IO.File]::WriteAllBytes((Join-Path $assets 'XTreme-512.png'), $icon512)
[System.IO.File]::WriteAllBytes((Join-Path $root 'docs\xtreme-icon.png'), $icon512)
$lensBig.Dispose()
Write-Output "OK $out"
