# Makes the XTreme icon and writes a multi-size .ico, the title bar's 64 px logo and the PNGs in docs\.
# A black vinyl with an orange label and a black X on it, round at every size, transparent around it.
# tools\icon\Vinilo.cs draws it, at each size's own resolution: shaded from $RenderedFrom up, flat below (taskbar,
# shortcuts, title bar). Needs nothing outside Windows PowerShell.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\XtremeDaw.App\Assets'
# The shaded record is used from this size up; below it the flat mark
$RenderedFrom = 128
Add-Type -Path (Join-Path $PSScriptRoot 'icon\Vinilo.cs') -ReferencedAssemblies System.Drawing

function New-IconPng([int]$s) {
  return , [Vinilo]::Png($s, $s -lt $RenderedFrom)
}

$out = Join-Path $assets 'XTreme.ico'
# 72 and 96: a 48 px shortcut at 150 and 200 % display scaling would otherwise scale the frame down
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
Write-Output "OK $out"
