# Creates (or updates) the desktop shortcut "XTreme Prueba": it opens the newest finished test copy made by
# tools\probar.ps1 (probar.ps1 -Abrir -Acceso), never the exe in bin\: an app running from bin\ locks the DLLs and the
# next build fails without anyone noticing. It needs nothing else running, and probar.ps1 calls this after every copy.
#   tools\make-shortcut.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $PSScriptRoot 'probar.ps1'
$icon = Join-Path $root 'src\XtremeDaw.App\Assets\XTreme.ico'
$desktop = [Environment]::GetFolderPath('Desktop')
# Not plain "XTreme": that is the name an installed version would use
$lnk = Join-Path $desktop 'XTreme Prueba.lnk'
$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$s.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$s.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" -Abrir -Acceso"
$s.WorkingDirectory = $root
$s.IconLocation = "$icon,0"
$s.WindowStyle = 7 # minimized: the console does not flash
$s.Description = "XTreme (la $([char]0xFA)ltima copia de prueba)"
$s.Save()
"Listo: $lnk"

# the old Banco de pruebas' shortcuts (XT-WorkBench, taken out in #37: the tickets live in ClaudeWorkBench now)
foreach ($old in 'XT-WorkBench.lnk', 'XTreme Banco de pruebas.lnk') {
    Remove-Item (Join-Path $desktop $old) -ErrorAction SilentlyContinue
}
