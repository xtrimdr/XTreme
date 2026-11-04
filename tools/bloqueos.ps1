<#
.SYNOPSIS
  Keeps two Claude sessions from changing the same file at once (pedido del usuario, 2026-11-03: se perdían cambios).

.DESCRIPTION
  Claude Code runs it as a hook (.claude/settings.json); the first argument is the event:

    editar   PreToolUse of Edit/Write/MultiEdit/NotebookEdit: takes the file for this session. If another session
             holds it, waits until it lets go (up to MaxWaitSec), then refuses.
    consola  PreToolUse of Bash/PowerShell: refuses git commands that throw away uncommitted work, waits if the
             command names a file another session holds, and remembers what the dirty files looked like.
    despues  PostToolUse of Bash/PowerShell: whatever the command changed (scripts, generators, git…) is taken for this
             session; if it changed a file another session holds, tells the agent so it stops and warns the user.
    soltar   Stop / SubagentStop: the turn ended, so its changes are done: lets go of everything it held.

  By hand: tools\bloqueos.ps1 -Lista shows who holds what; tools\bloqueos.ps1 -Limpiar drops every lock.

  A lock is a file in .claude\bloqueos named by the path's hash, created by a move so only one session can win it.
  A session that died without its Stop leaves locks behind: they expire after StaleSec without being touched.
#>
param(
    [Parameter(Position = 0)][string]$Evento = '',
    [switch]$Lista,
    [switch]$Limpiar
)

$ErrorActionPreference = 'Stop'
$MaxWaitSec = 540   # as in tools\turno.targets: the hook's own timeout is 600
$StaleSec = 1800    # a lock nobody touched in 30 min belongs to a session that's gone
$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Dir = Join-Path $Root '.claude\bloqueos'
$Utf8 = New-Object Text.UTF8Encoding($false)

function Now { [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }

function Hash([string]$text) {
    $sha = [Security.Cryptography.SHA1]::Create()
    ($sha.ComputeHash($Utf8.GetBytes($text.ToLowerInvariant())) | ForEach-Object { $_.ToString('x2') }) -join ''
}

function Rel([string]$full) {
    if ($full.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) { return $full.Substring($Root.Length).TrimStart('\') }
    $full
}

function ReadJson([string]$file) {
    try { return [IO.File]::ReadAllText($file, $Utf8) | ConvertFrom-Json } catch { return $null }
}

function WriteJson([string]$file, $obj) {
    $tmp = "$file.$PID.tmp"
    [IO.File]::WriteAllText($tmp, ($obj | ConvertTo-Json -Compress), $Utf8)
    $tmp
}

function Locks {
    if (-not (Test-Path $Dir)) { return @() }
    foreach ($f in Get-ChildItem $Dir -Filter 'l-*.json') {
        $l = ReadJson $f.FullName
        if ($l) { $l | Add-Member -NotePropertyName File -NotePropertyValue $f.FullName -PassThru }
    }
}

function Stale($lock) { (Now) - [long]$lock.touched -gt $StaleSec }

function Who($lock) {
    $since = [DateTimeOffset]::FromUnixTimeSeconds([long]$lock.since).ToLocalTime().ToString('HH:mm')
    $who = "la sesión $($lock.session.Substring(0, [Math]::Min(8, $lock.session.Length)))"
    if ($lock.agent) { $who += " (subagente)" }
    "$who, desde las $since"
}

# Takes $full for $me. Returns $null when it's ours, else the lock that kept it after waiting.
function Take([string]$full, $me, [bool]$wait) {
    New-Item -ItemType Directory -Force $Dir | Out-Null
    $h = Hash $full
    $file = Join-Path $Dir "l-$h.json"
    $waitFile = Join-Path $Dir "w-$(Hash $me.owner).json"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        while ($true) {
            $mine = [ordered]@{ owner = $me.owner; session = $me.session; agent = $me.agent; path = $full; since = (Now); touched = (Now) }
            $tmp = WriteJson $file $mine
            try { [IO.File]::Move($tmp, $file); return $null }
            catch { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }

            $held = ReadJson $file
            if (-not $held) { Start-Sleep -Milliseconds 200; continue }
            if ($held.owner -eq $me.owner) {
                $held.touched = Now
                $tmp = WriteJson $file $held
                [IO.File]::Copy($tmp, $file, $true); Remove-Item $tmp -Force
                return $null
            }
            if (Stale $held) { Remove-Item $file -Force -ErrorAction SilentlyContinue; continue }
            if (-not $wait -or $clock.Elapsed.TotalSeconds -gt $MaxWaitSec) { return $held }

            # Deadlock: the holder is waiting for a file we hold. Neither would ever let go.
            $theirs = ReadJson (Join-Path $Dir "w-$(Hash $held.owner).json")
            if ($theirs) {
                $want = ReadJson (Join-Path $Dir "l-$($theirs.hash).json")
                if ($want -and $want.owner -eq $me.owner) {
                    $held | Add-Member -NotePropertyName Deadlock -NotePropertyValue $true -Force
                    return $held
                }
            }
            $tmp = WriteJson $waitFile ([ordered]@{ owner = $me.owner; hash = $h })
            [IO.File]::Copy($tmp, $waitFile, $true); Remove-Item $tmp -Force
            Start-Sleep -Seconds 1
        }
    }
    finally { Remove-Item $waitFile -Force -ErrorAction SilentlyContinue }
}

function Refuse([string]$full, $held) {
    $what = Rel $full
    if ($held.Deadlock) {
        [Console]::Error.WriteLine("BLOQUEADO: $what lo tiene $(Who $held), que a su vez espera un archivo que tenés vos. No lo cambies por otro camino (shell, Write, git, scripts). Terminá tu turno ya, así soltás tus archivos, y avisale al usuario qué quedó sin hacer.")
    } else {
        [Console]::Error.WriteLine("BLOQUEADO: $what lo está cambiando $(Who $held) y no lo soltó en $([int]($MaxWaitSec / 60)) min. No lo cambies por otro camino (shell, Write, git, scripts). Terminá tu turno y avisale al usuario qué quedó sin hacer en ese archivo.")
    }
    exit 2
}

# Dirty files (tracked or not, never ignored) plus every file another session holds, with what they look like now.
function Snapshot($me) {
    $paths = @{}
    $out = & git -C $Root status --porcelain -z --untracked-files=all 2>$null
    if ($LASTEXITCODE -eq 0 -and $out) {
        $items = ($out -join "`0").Split([char]0)
        for ($i = 0; $i -lt $items.Length; $i++) {
            $e = $items[$i]
            if ($e.Length -lt 4) { continue }
            $full = [IO.Path]::GetFullPath((Join-Path $Root $e.Substring(3)))
            if (-not $full.StartsWith($Dir, [StringComparison]::OrdinalIgnoreCase)) { $paths[$full] = 1 }
            if ($e[0] -eq 'R' -or $e[0] -eq 'C') { $i++ }   # the original path follows a rename
        }
    }
    foreach ($l in Locks) { if ($l.owner -ne $me.owner -and -not (Stale $l)) { $paths[$l.path] = 1 } }
    $snap = @{}
    foreach ($p in $paths.Keys) {
        $f = Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
        $snap[$p] = if ($f -and -not $f.PSIsContainer) { "$($f.LastWriteTimeUtc.Ticks):$($f.Length)" } else { 'x' }
    }
    $snap
}

[Console]::OutputEncoding = $Utf8
if ($Lista -or $Limpiar) {
    $all = @(Locks)
    if ($Limpiar) { Remove-Item (Join-Path $Dir '*') -Force -ErrorAction SilentlyContinue; "Se soltaron $($all.Count) archivos."; exit 0 }
    if (-not $all.Count) { 'Nadie tiene archivos tomados.'; exit 0 }
    foreach ($l in $all | Sort-Object path) {
        $state = if (Stale $l) { ' (vencido)' } else { '' }
        "$(Rel $l.path)  ←  $(Who $l)$state"
    }
    exit 0
}

[Console]::InputEncoding = $Utf8
$in = [Console]::In.ReadToEnd() | ConvertFrom-Json
$agent = if ($in.agent_id) { [string]$in.agent_id } else { '' }
$me = @{ session = [string]$in.session_id; agent = $agent; owner = if ($agent) { "$($in.session_id)/$agent" } else { [string]$in.session_id } }

switch ($Evento) {
    'editar' {
        $p = if ($in.tool_input.file_path) { $in.tool_input.file_path } else { $in.tool_input.notebook_path }
        if (-not $p) { exit 0 }
        $full = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $in.cwd $p }))
        if (-not $full.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { exit 0 }
        $held = Take $full $me $true
        if ($held) { Refuse $full $held }
        exit 0
    }
    'consola' {
        $cmd = [string]$in.tool_input.command
        # Each of these throws away changes nobody committed, and on master they may be another session's.
        # Only git where a command starts, so a mention inside a text or a string doesn't count.
        $git = '(?i)(^|[;&|(\r\n])\s*git\s+(-C\s+\S+\s+|-c\s+\S+\s+)*(restore\b(?![^;&|\r\n]*--staged)|stash\b(?!\s+(list|show)\b)|clean\b|reset\b[^;&|\r\n]*--hard|checkout\b[^;&|\r\n]*(\s--(\s|$)|\s\.(\s|$)|\sHEAD\b))'
        if ($cmd -match $git) {
            [Console]::Error.WriteLine("BLOQUEADO: '$($Matches[0].Trim())' puede tirar cambios sin commitear, propios o de otra sesión. No lo hagas ni lo reemplaces por otro comando: si de verdad hace falta, pedile al usuario que lo corra él.")
            exit 2
        }
        $readOnly = $cmd -match '^\s*(cat|head|tail|grep|rg|less|wc|ls|Get-Content|Select-String|git\s+(diff|log|status|show|blame))\b' -and $cmd -notmatch '>'
        if (-not $readOnly) {
            foreach ($l in @(Locks)) {
                if ($l.owner -eq $me.owner -or (Stale $l)) { continue }
                $name = [IO.Path]::GetFileName($l.path)
                if ($cmd -match "(?i)(^|[\\/\s'`"])$([regex]::Escape($name))($|[\s'`"])") {
                    $held = Take $l.path $me $true
                    if ($held) { Refuse $l.path $held }
                }
            }
        }
        $snap = Snapshot $me
        New-Item -ItemType Directory -Force $Dir | Out-Null
        $file = Join-Path $Dir "s-$(Hash ([string]$in.tool_use_id)).json"
        Move-Item -Force (WriteJson $file $snap) $file
        exit 0
    }
    'despues' {
        $file = Join-Path $Dir "s-$(Hash ([string]$in.tool_use_id)).json"
        $before = ReadJson $file
        if (-not $before) { exit 0 }
        Remove-Item $file -Force -ErrorAction SilentlyContinue
        $old = @{}
        foreach ($pr in $before.PSObject.Properties) { $old[$pr.Name] = $pr.Value }
        $now = Snapshot $me
        $clash = @()
        foreach ($p in $now.Keys) {
            if ($old.ContainsKey($p) -and $old[$p] -eq $now[$p]) { continue }
            if (-not $old.ContainsKey($p) -and $now[$p] -eq 'x') { continue }
            if (-not $p.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
            $held = Take $p $me $false
            if ($held) { $clash += "$(Rel $p) ($(Who $held))" }
        }
        foreach ($p in $old.Keys) {
            if (-not $now.ContainsKey($p)) {
                $held = Take $p $me $false
                if ($held) { $clash += "$(Rel $p) ($(Who $held))" }
            }
        }
        if ($clash.Count) {
            [Console]::Error.WriteLine("ATENCIÓN: este comando cambió archivos que está editando otra sesión: $($clash -join '; '). No intentes arreglarlo ni deshacerlo vos (podés pisar lo del otro): pará y avisale al usuario exactamente qué archivos y qué comando.")
            exit 2
        }
        exit 0
    }
    'soltar' {
        foreach ($l in @(Locks)) {
            if ($l.session -eq $me.session -and $l.agent -eq $me.agent) { Remove-Item $l.File -Force -ErrorAction SilentlyContinue }
        }
        # Snapshots of commands that never got their PostToolUse (refused by another hook, interrupted).
        Get-ChildItem $Dir -Filter 's-*.json' -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-1) } | Remove-Item -Force -ErrorAction SilentlyContinue
        exit 0
    }
}
exit 0
