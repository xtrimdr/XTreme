# The one way to try a change: builds the solution, runs xtlab's DSP checks, copies the app to its own folder
# and opens that copy. The app never runs from bin\, so an open XTreme never blocks the next build, and every
# copy carries a prueba.txt mark (commit, uncommitted changes, time) that the app shows in its title bar.
# The tickets live in ClaudeWorkBench (wb); this script only builds, checks, copies and opens.
#   tools\probar.ps1                        -> build, check, copy and open
#   tools\probar.ps1 -Proyecto D:\...\MiTema -> same, opening that project directly
#   tools\probar.ps1 -NoAbrir               -> build, check and copy, without opening
#   tools\probar.ps1 -Abrir                 -> open the newest finished copy without building
#   tools\probar.ps1 -Abrir -Acceso         -> same, from the desktop shortcut: it has no console, so a failure shows a window
#   tools\probar.ps1 -Abrir -Probar 12      -> same, from ClaudeWorkBench's Probar (the ticket number is only logged)
#   tools\probar.ps1 -SinComprobar          -> skip xtlab comprobar-dsp (UI-only changes in a hurry)
#   tools\probar.ps1 -SoloCompilar          -> build only, in its turn and below normal priority
# Several sessions share this folder, so building, checking and copying go one at a time: the script holds the turn that
# xtlab (HeavyTurn.cs) and dotnet build (turno.targets) also take until the copy is done, and runs below normal priority
# so the machine stays usable.
# Before opening, if a XTreme is already open, a window offers to close it and open the new one (it saves first);
# one open from bin\ is asked about before building, since it blocks it. A XTreme with no window left is not asked
# about: it already saved and closed, and it would hold the audio interface and the project.
# Exit codes: 0 opened (or built, with -NoAbrir), 1 it failed, 2 the user cancelled, 3 nobody answered the question,
# 4 another session kept the turn for too long.
param(
    # what to try: only shown (the ticket in ClaudeWorkBench says it)
    [Parameter(Position = 0)][string]$Que = '',
    [string]$Proyecto = '',
    [switch]$Abrir,
    [switch]$Acceso,
    [int]$Probar = 0,
    [switch]$SinComprobar,
    [switch]$NoAbrir,
    [switch]$SoloCompilar
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'src\XtremeDaw.App\bin\Debug\net10.0-windows'
# Not under AppData: run from an MSIX-packaged tool (the Claude desktop app), Windows redirects AppData writes into
# the package's own LocalCache and the desktop shortcut would never find the copies
$copies = Join-Path $root 'pruebas'
$xtlab = Join-Path $root 'src\XtremeDaw.Lab\bin\Debug\net10.0-windows\xtlab.exe'
# How many copies stay around; older ones are deleted (unless one is still open)
$Keep = 5

function Fail([string]$text) {
    Write-Host ''
    Write-Host "NO SE PUEDE PROBAR: $text" -ForegroundColor Red
    SessionLog "probar.ps1 no pudo: $text"
    ShortcutError $text
    exit 1
}

# From the desktop shortcut the console is hidden: says what failed in a window, always on top (0x40000), with a stop
# icon (0x10). Only there: an agent's command must never wait on a window nobody asked for.
function ShortcutError([string]$text) {
    if (-not $Acceso) { return }
    try { [void](New-Object -ComObject WScript.Shell).Popup("No se pudo abrir XTreme.`n`n$text", 0, 'XTreme · Probar', 0x40010) } catch { }
}

# The app's own sesiones.log: a window this script closes looks like one closed by hand, so it says so there
function SessionLog([string]$text) {
    try {
        $dir = Join-Path $env:APPDATA 'XTreme'
        New-Item -ItemType Directory -Force $dir | Out-Null
        Add-Content -Path (Join-Path $dir 'sesiones.log') -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')  $text" -Encoding UTF8
    } catch { }
}
# An unexpected error must not vanish without a trace either
trap { SessionLog "probar.ps1 falló: $_ $($_.InvocationInfo.PositionMessage)"; ShortcutError "$_"; break }

# How long a XTreme with its window gone gets to end on its own
$GhostWaitSec = 3
# How long the question stays up: less than the two minutes an agent's command gets, so it hears back
$AskWaitSec = 100
# How long the new copy gets to appear, and how long it has to stay up to count as opened
$StartWaitSec = 15
$AliveWaitSec = 8
# How long to wait for the turn: a little under the ten minutes an agent's command can last, so it hears back
$TurnWaitSec = 540

# The app's process, and its name before the rename (a test copy from then may still be open)
$AppProcesses = 'XTreme'

# The open XTremes
function AppRunning {
    @(Get-Process $AppProcesses -ErrorAction SilentlyContinue)
}

# Closes one XTreme through its window, so the project is saved and a take in progress is kept. The window may stay
# up asking something (changes it could not save, an export running): it gets time and is never forced. Once the window
# is gone, a process that doesn't end has nothing left to lose and is ended.
function CloseApp($p) {
    $p.Refresh()
    if ($p.MainWindowHandle -ne 0) { [void]$p.CloseMainWindow() }
    $until = (Get-Date).AddSeconds($AskWaitSec)
    $gone = $null
    while (-not $p.HasExited -and (Get-Date) -lt $until) {
        $p.Refresh()
        if ($p.MainWindowHandle -ne 0) { $gone = $null }
        elseif (-not $gone) { $gone = Get-Date }
        elseif (((Get-Date) - $gone).TotalSeconds -gt $GhostWaitSec) {
            SessionLog "probar.ps1 termina pid $($p.Id): sin ventana y sin terminar"
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            [void]$p.WaitForExit(5000)
            return
        }
        Start-Sleep -Milliseconds 200
    }
    if (-not $p.HasExited) { Fail 'XTreme sigue abierto: está pidiendo algo en su ventana. Contestale y volvé a correr esto.' }
}

# Offers in a window of its own (the agent's console cannot be answered) to close the XTreme windows that are open.
# It is always on top and in the taskbar, whoever has the focus: a message box with a hidden owner opened behind
# everything and the script waited forever. It stops asking when there is nothing left to ask: XTreme was closed by
# hand, or nobody answered in $AskWaitSec s. Returns 'closed', 'cancel' or 'timeout'.
function AskClose($procs) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    [System.Windows.Forms.Application]::EnableVisualStyles()
    $names = ($procs | ForEach-Object { if ($_.MainWindowTitle) { $_.MainWindowTitle } else { 'XTreme' } }) -join "`n"
    $script:askAnswer = 'cancel'
    $script:askProcs = $procs
    $script:askStart = Get-Date
    $form = New-Object System.Windows.Forms.Form -Property @{
        Text = 'XTreme · Probar'; TopMost = $true; StartPosition = 'CenterScreen'; FormBorderStyle = 'FixedDialog'
        MaximizeBox = $false; MinimizeBox = $false; ShowInTaskbar = $true; AutoSize = $true; AutoSizeMode = 'GrowAndShrink'
        Font = New-Object System.Drawing.Font('Segoe UI', 10)
    }
    $script:askForm = $form
    $body = New-Object System.Windows.Forms.FlowLayoutPanel -Property @{ FlowDirection = 'TopDown'; AutoSize = $true; Padding = New-Object System.Windows.Forms.Padding(14) }
    $body.Controls.Add((New-Object System.Windows.Forms.Label -Property @{ Text = "XTreme está abierto:`n$names"; AutoSize = $true; MaximumSize = New-Object System.Drawing.Size(460, 0) }))
    $row = New-Object System.Windows.Forms.FlowLayoutPanel -Property @{ FlowDirection = 'LeftToRight'; AutoSize = $true; Margin = New-Object System.Windows.Forms.Padding(0, 14, 0, 0) }
    foreach ($c in @(@('Cerrar y volver a abrir', 'close'), @('Cancelar', 'cancel'))) {
        $b = New-Object System.Windows.Forms.Button -Property @{ Text = $c[0]; Tag = $c[1]; AutoSize = $true; Padding = New-Object System.Windows.Forms.Padding(8, 3, 8, 3) }
        $b.add_Click({ param($sender, $e) $script:askAnswer = $sender.Tag; $script:askForm.Close() })
        [void]$row.Controls.Add($b)
    }
    [void]$body.Controls.Add($row)
    [void]$form.Controls.Add($body)
    # no button takes the focus: a Space or an Enter meant for something else must not answer
    $form.ActiveControl = $body
    $timer =New-Object System.Windows.Forms.Timer -Property @{ Interval = 400 }
    $timer.add_Tick({
        if (@($script:askProcs | Where-Object { -not $_.HasExited }).Count -eq 0) { $script:askAnswer = 'gone'; $script:askForm.Close() }
        elseif (((Get-Date) - $script:askStart).TotalSeconds -gt $AskWaitSec) { $script:askAnswer = 'timeout'; $script:askForm.Close() }
    })
    $form.add_Shown({ $script:askForm.Activate() })
    $timer.Start()
    [void]$form.ShowDialog()
    $timer.Dispose()
    $form.Dispose()
    if ($script:askAnswer -eq 'gone') { return 'closed' }
    if ($script:askAnswer -ne 'close') { return $script:askAnswer }
    SessionLog "probar.ps1 cierra pid $(($procs | ForEach-Object Id) -join ', ')"
    foreach ($p in $procs) { CloseApp $p }
    Write-Host 'XTreme cerrado.' -ForegroundColor DarkGray
    return 'closed'
}

# Who holds the turn, as the holder wrote it in the file
function TurnHolder([string]$path) {
    try {
        $f = [System.IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
        try { return (New-Object System.IO.StreamReader($f)).ReadToEnd() } finally { $f.Dispose() }
    } catch { return 'otro proceso' }
}

# The same turn as HeavyTurn.cs: an open file, which Windows frees when this script dies, however it dies
function TakeTurn {
    $dir = Join-Path $root 'src\XtremeDaw.Lab\bin\Debug\net10.0-windows'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $path = Join-Path $dir 'turno.lock'
    $until = (Get-Date).AddSeconds($TurnWaitSec)
    $told = $false
    while ($true) {
        try {
            $f = [System.IO.File]::Open($path, 'OpenOrCreate', 'ReadWrite', 'Read')
            $f.SetLength(0)
            $text = [System.Text.Encoding]::UTF8.GetBytes("probar.ps1 (pid $PID)")
            $f.Write($text, 0, $text.Length)
            $f.Flush()
            return $f
        } catch {
            if ((Get-Date) -gt $until) {
                Write-Host "NO SE PUEDE PROBAR: sigue ocupado después de $TurnWaitSec s: $(TurnHolder $path)." -ForegroundColor Yellow
                SessionLog "probar.ps1: sin turno después de $TurnWaitSec s"
                exit 4
            }
            if (-not $told) { Write-Host "Esperando turno: $(TurnHolder $path)." -ForegroundColor DarkGray }
            $told = $true
            Start-Sleep -Milliseconds 500
        }
    }
}

function Launch([string]$dir) {
    $running = AppRunning
    foreach ($p in $running) { if ($p.MainWindowHandle -eq 0) { CloseApp $p } }
    $open = @($running | Where-Object { -not $_.HasExited })
    $asked = if ($open.Count) { AskClose $open } else { 'closed' }
    if ($asked -eq 'cancel') {
        Write-Host 'No abrí nada: elegiste cancelar.' -ForegroundColor Yellow
        exit 2
    }
    if ($asked -eq 'timeout') {
        Write-Host "No abrí nada: nadie contestó en $AskWaitSec s." -ForegroundColor Yellow
        SessionLog 'probar.ps1: nadie contestó si cerraba XTreme'
        exit 3
    }
    $a = @()
    if ($Proyecto) { $a += $Proyecto }
    $exe = Join-Path $dir 'XTreme.exe'
    SessionLog "probar.ps1 abre $exe$(if ($Probar -gt 0) { " (Probar #$Probar)" })"
    # Opened through Explorer, so it runs as the user's own app even when this script runs inside a packaged tool
    # (otherwise it would read and write a redirected copy of %APPDATA%\XTreme, not the user's settings)
    $lnk = Join-Path $dir 'abrir.lnk'
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $dir
    $s.Arguments = ($a | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
    $s.Save()
    explorer.exe $lnk
    # Explorer says nothing if it failed: only the process itself proves it opened
    $opened = Get-Date
    $until = $opened.AddSeconds($StartWaitSec)
    $app = $null
    while (-not $app -and (Get-Date) -lt $until) {
        $app = AppRunning | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
        if (-not $app) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $app) { Fail "pedí abrir $exe y no arrancó en $StartWaitSec s." }
    # having started proves little: a crash in the first frames comes seconds later, and the reason is in the event log
    if ($app.WaitForExit($AliveWaitSec * 1000)) {
        $crash = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = '.NET Runtime'; StartTime = $opened } -MaxEvents 1 -ErrorAction SilentlyContinue
        $why = if ($crash) { (($crash.Message -split "`r?`n" | Where-Object { $_ -match 'Exception Info|^\s+at XTreme' } | Select-Object -First 3) -join ' ').Trim() } else { 'sin error registrado' }
        Fail "XTreme abrió y se cerró enseguida: $why"
    }
    Write-Host "Abierto: $exe" -ForegroundColor Green
}

# prueba.txt is written last: a folder without it is a copy still being made (or one that died halfway)
function NewestCopy {
    Get-ChildItem $copies -Directory -ErrorAction SilentlyContinue |
        Where-Object { (Test-Path (Join-Path $_.FullName 'prueba.txt')) -and (Test-Path (Join-Path $_.FullName 'XTreme.exe')) } |
        Sort-Object Name -Descending | Select-Object -First 1
}

if ($Abrir) {
    $last = NewestCopy
    if (-not $last) { Fail 'todavía no hay ninguna copia de prueba. Corré tools\probar.ps1 una vez.' }
    Launch $last.FullName
    exit 0
}

# An app opened straight from bin\ (an old shortcut, or by hand) locks the DLLs and the build fails
$fromBin = @(AppRunning | Where-Object { $_.Path -and $_.Path.StartsWith($bin, 'OrdinalIgnoreCase') })
if ($fromBin.Count -and (AskClose $fromBin) -ne 'closed') {
    Fail 'hay un XTreme abierto desde bin\ y no lo cerré. Cerralo y volvé a correr esto.'
}

$turn = TakeTurn
$env:XTREME_TURNO = '1'
(Get-Process -Id $PID).PriorityClass = 'BelowNormal'

Push-Location $root
try {
    $commit = (git rev-parse --short HEAD).Trim()
    $dirty = [bool](git status --porcelain)
} finally { Pop-Location }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
# prueba.txt: commit, "dirty" or "clean", build time (ISO); the app formats it in its own language
$mark = "$commit$(if ($dirty) { ' + cambios sin commit' })"

Write-Host "Compilando ($mark)..." -ForegroundColor Cyan
dotnet build (Join-Path $root 'XtremeDaw.slnx') -c Debug -nologo -v:minimal -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { Fail 'la compilación falló (arriba están los errores).' }
if ($SoloCompilar) { exit 0 }

if (-not $SinComprobar) {
    Write-Host 'Comprobando el DSP (xtlab comprobar-dsp)...' -ForegroundColor Cyan
    $out = & $xtlab comprobar-dsp 2>&1
    if ($LASTEXITCODE -ne 0) { $out | Write-Host; Fail "xtlab comprobar-dsp falló (código $LASTEXITCODE)." }
    Write-Host '  todo bien' -ForegroundColor DarkGray
}

$dest = Join-Path $copies "$stamp-$commit"
New-Item -ItemType Directory -Force $dest | Out-Null
# runtimes\ holds native libraries for every OS (~120 MB); the app only needs win-x64's
Get-ChildItem $bin | Where-Object Name -ne 'runtimes' | Copy-Item -Destination $dest -Recurse -Force
$native = Join-Path $bin 'runtimes\win-x64'
if (Test-Path $native) {
    New-Item -ItemType Directory -Force (Join-Path $dest 'runtimes') | Out-Null
    Copy-Item $native (Join-Path $dest 'runtimes') -Recurse -Force
}
Set-Content -Path (Join-Path $dest 'prueba.txt') -Value @($commit, $(if ($dirty) { 'dirty' } else { 'clean' }), (Get-Date -Format 's')) -Encoding utf8

Get-ChildItem $copies -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'prueba.txt') } |
    Sort-Object Name -Descending | Select-Object -Skip $Keep | ForEach-Object {
    try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop } catch { } # still open: next time
}

Write-Host "Copia: $dest" -ForegroundColor DarkGray
# Every copy leaves the desktop shortcut right, even if this folder moved: that is how the old one ended up pointing nowhere
try { [void](& (Join-Path $PSScriptRoot 'make-shortcut.ps1')) } catch { Write-Host "No se pudo actualizar el acceso directo: $_" -ForegroundColor Yellow }
if ($Que) { Write-Host "Para probar: $Que" -ForegroundColor DarkGray }
$turn.Dispose()
if (-not $NoAbrir) { Launch $dest }
