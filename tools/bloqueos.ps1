<#
.SYNOPSIS
  Keeps two Claude sessions from changing the same file at once (pedido del usuario, 2026-11-03: se perdían cambios).

.DESCRIPTION
  Claude Code runs it as a hook (.claude/settings.json); the first argument is the event:

    editar   PreToolUse of Edit/Write/MultiEdit/NotebookEdit: takes the file for this session. If another session
             holds it, joins its line (q-*.json): waits up to SliceSec, then refuses saying its place in line ("estás
             2° de 3"). The place is kept while the agent retries; whoever joined first gets the file first. A line
             that loops back to a file this session holds (A waits B waits C waits A) is refused at once: the agent
             ends its turn and lets go.
    consola  PreToolUse of Bash/PowerShell: refuses git commands that throw away uncommitted work, waits if the
             command names a file another session holds (by its path when it gives one: Bench/es.json isn't
             Lang/es.json), and remembers what the dirty files looked like.
    despues  PostToolUse of Bash/PowerShell: whatever the command changed (scripts, generators, git…) is taken for this
             session; if it changed a file another session holds, tells the agent so it stops and warns the user.
             What someone else changed while it ran (an edit, a command of theirs) is theirs: never taken or warned.

  A session and its subagents are one owner for each other (the session is waiting for them); sibling subagents take
  turns. Refusals go out as a denial with the reason, never as a hook error, and a bug in this script lets the tool
  run and writes .claude\bloqueos\errores.log.
    soltar   Stop / SubagentStop: the turn ended, so its changes are done: lets go of everything it held.
    espera   Notification (#278): a permission prompt or a question waits for the person; u-<session hash>.json tells
             the bench's map (a turn that ended it reads from the transcript itself).

  By hand: tools\bloqueos.ps1 -Lista shows who holds what; tools\bloqueos.ps1 -Limpiar drops every lock.

  Each session's traffic light for the bench (#167): a wait in line the bench reads from the lock and line files
  themselves (l-*, q-*: LockLight.cs), so places stay current and say whom they wait for. The reds go in
  t-<owner hash>.json: 'bloqueado' when its line loops back, 'atencion' when a command of its changed another session's
  files; they outlive the turn (the agent stopped because of them) until the session's next tool call.

  Alerts between sessions (#263): m-<target hash>-<ms>-<n>.json, one per alert, for a session or one owner (a
  subagent). The hook writes them by itself (a file let go for whoever was first in line, a file taken that another
  session changed, a command that changed a file someone holds, a build that fails in a file another session changed)
  and agents send them by hand (-Avisa, -PideArchivo). They reach the agent on its next edit or command (PostToolUse
  'avisos' and 'despues', as context), before its turn ends (Stop keeps it going to read them) and with the user's next
  message; the bench shows them as they arrive. A read one stays AlertKeepSec for the bench, then goes.

  A lock is a file in .claude\bloqueos named by the path's hash, created by a move so only one session can win it.
  A session that died without its Stop leaves locks behind: they expire after StaleSec without being touched.
#>
param(
    [Parameter(Position = 0)][string]$Evento = '',
    [switch]$Lista,
    [switch]$Limpiar,
    # tools\cerrar-sesion.ps1: the session whose locks go (soltar), without a hook's stdin
    [string]$Sesion = '',
    # Alerts between sessions (#263), run by an agent (its session from CLAUDE_CODE_SESSION_ID):
    #   -Avisa <todos|#60|1a2b3c4d> -Texto "…" [-Atencion]   a message to a session, the one working on a ticket, or all
    #   -PideArchivo <ruta>     asks whoever holds the file to let it go
    #   -SueltaArchivo <ruta>   lets go of a file this session holds before its turn ends
    #   -Buzon                  reads this session's unread alerts
    [string]$Avisa = '',
    [string]$Texto = '',
    [switch]$Atencion,
    [string]$PideArchivo = '',
    [string]$SueltaArchivo = '',
    [switch]$Buzon
)

$ErrorActionPreference = 'Stop'
$SliceSec = 90      # one try waits this long in line, then tells the agent its place (the hook's timeout is 600)
$KeepSec = 300      # a place in line is kept this long after the last try
$ActiveSec = 5      # someone who tried this recently is still waiting in the hook right now
$ReserveSec = 120   # a file let go waits this long for the first in line, even if it's off doing something else
$StaleSec = 1800    # a lock nobody touched in 30 min belongs to a session that's gone
$EditLagSec = 600   # #299: an edit's write can come this long after its PreToolUse (a permission prompt in between)
$AlertKeepSec = 1800  # a read alert stays this long, so the bench can still show it
$LogKeepSec = 86400   # the conversation in the bench (#269, avisos.jsonl) keeps a day
$ChangedSec = 7200  # a change mark this old no longer makes its author someone to warn
$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Dir = Join-Path $Root '.claude\bloqueos'
$Utf8 = New-Object Text.UTF8Encoding($false)
# The Banco de pruebas (xtbench) is a program of its own (asked by the user, 2026-11-05): a session working on it never
# waits for, takes or warns about XTreme's files through a command, and the other way round. Everything else is XTreme.
# The files XTreme lends the Banco (Loc.cs, App.axaml, UiTheme.cs…) are XTreme's.
$BenchFiles = @(
    '^src\\XtremeDaw\.Bench\\'
    '^src\\XtremeDaw\.App\\Assets\\Lang\\Bench\\'
    '^src\\XtremeDaw\.App\\Dialogs\\TestListWindow\.'
    '^src\\XtremeDaw\.App\\Bench\.cs$'
    '^src\\XtremeDaw\.App\\MainWindow\.TestList\.cs$'
    '^src\\XtremeDaw\.Projects\\TestList\.cs$'
    '^src\\XtremeDaw\.Lab\\TestListCommands\.cs$'
    '^tools\\probar\.ps1$'
    '^src\\XtremeDaw\.App\\LockLight\.cs$'
    '^src\\XtremeDaw\.App\\AgentAlert\.cs$'
    '^src\\XtremeDaw\.App\\AgentTranscript\.cs$'
)

function Now { [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() }

function Domain([string]$full) {
    $rel = Rel $full
    foreach ($re in $BenchFiles) { if ($rel -match $re) { return 'banco' } }
    'xtreme'
}

# The domains this session has edited in this turn (d-<owner hash>.json); none yet = it may touch anything.
function MyDomains($me) { $d = ReadJson (Join-Path $Dir "d-$(Hash $me.owner).json"); if ($d) { @($d.domains) } else { @() } }

function AddDomain($me, [string]$domain) {
    $mine = @(MyDomains $me)
    if ($mine -contains $domain) { return }
    $file = Join-Path $Dir "d-$(Hash $me.owner).json"
    Move-Item -Force (WriteJson $file @{ domains = @($mine + $domain) }) $file
}

# Is $full outside what this session works on? Then its commands neither wait for it nor take it.
function Foreign($me, [string]$full) { $mine = @(MyDomains $me); $mine.Count -and $mine -notcontains (Domain $full) }

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

# Did the Claude process that took it end? A session closed without its Stop (its console closed, a crash) can't let
# go by itself: its locks are free at once, not after StaleSec. Not a timeout: a live session keeps everything.
function Gone($lock) {
    if (-not $lock.pid) { return $false }
    $p = Get-Process -Id ([int]$lock.pid) -ErrorAction SilentlyContinue
    # a pid reused by a process we may not read (elevated, a service) has no StartTime: it's not that Claude (#313)
    -not $p -or -not $p.StartTime -or $p.StartTime.ToUniversalTime().Ticks -ne [long]$lock.started
}

function Stale($lock) { (Now) - [long]$lock.touched -gt $StaleSec -or (Gone $lock) }

# The claude.exe this hook runs under, found once per session (p-<session hash>.json; a resumed session runs in a new
# process, so a cached one that's gone is looked up again). $null when there's none (a test, a hand run). It keeps the
# session too: the bench finds a ticket's agent by it (its "Ver consola", #164).
function ClaudeProcess($me) {
    $file = Join-Path $Dir "p-$(Hash $me.session).json"
    $c = ReadJson $file
    # one written before it kept the model (#315) is looked up again once
    if ($c -and $c.session -and $null -ne $c.model -and -not (Gone $c)) { return $c }
    $id = $PID
    for ($n = 0; $n -lt 10; $n++) {
        $p = Get-CimInstance Win32_Process -Filter "ProcessId = $id" -ErrorAction SilentlyContinue
        if (-not $p) { return $null }
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId = $($p.ParentProcessId)" -ErrorAction SilentlyContinue
        if (-not $parent) { return $null }
        if ($parent.Name -match '^claude(\.exe)?$') {
            $proc = Get-Process -Id ([int]$parent.ProcessId) -ErrorAction SilentlyContinue
            if (-not $proc) { return $null }
            # its model and effort, as its command line gives them (#315: the bench shows them); "" = Claude's default
            $cmd = [string]$parent.CommandLine
            $model = if ($cmd -match '--model[=\s]+"?([^\s"]+)') { $Matches[1] } else { '' }
            $effort = if ($cmd -match '--effort[=\s]+"?([^\s"]+)') { $Matches[1] } else { '' }
            $c = [pscustomobject]@{ pid = [int]$parent.ProcessId; started = $proc.StartTime.ToUniversalTime().Ticks; session = $me.session; model = $model; effort = $effort }
            New-Item -ItemType Directory -Force $Dir | Out-Null
            Move-Item -Force (WriteJson $file $c) $file
            return $c
        }
        $id = $parent.ProcessId
    }
    $null
}

# A session and its subagents are one: the session waits for its subagent while it works, so neither may wait for the
# other (they'd never move). Sibling subagents still take turns.
function Mine($lock, $me) {
    $lock.owner -eq $me.owner -or ($lock.session -eq $me.session -and (-not $lock.agent -or -not $me.agent))
}

# Who changed each file last, by hand or through a command (e-<path hash>.json), so a command never takes what someone
# else changed while it ran.
# An edit marks it twice (#299): pending before the write and again after it, so a command that started in between
# never takes that write as its own.
function MarkChanged([string]$full, $me, [bool]$pending = $false) {
    $file = Join-Path $Dir "e-$(Hash $full).json"
    Move-Item -Force (WriteJson $file @{ owner = $me.owner; session = $me.session; agent = $me.agent; at = (Now); pending = $pending }) $file
}

# The edit's full path, or '' when it's outside the repository.
function EditPath($in) {
    $p = if ($in.tool_input.file_path) { $in.tool_input.file_path } else { $in.tool_input.notebook_path }
    if (-not $p) { return '' }
    $full = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $in.cwd $p }))
    if ($full.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { $full } else { '' }
}

# When each session's last command ran (c-<owner hash>.json; end = 0 while it runs).
function CommandStarts($me) {
    $file = Join-Path $Dir "c-$(Hash $me.owner).json"
    $c = ReadJson $file
    $start = if ($c -and [long]$c.end -eq 0) { [long]$c.start } else { Now }
    Move-Item -Force (WriteJson $file @{ start = $start; end = 0 }) $file
}

function CommandEnds($me) {
    $file = Join-Path $Dir "c-$(Hash $me.owner).json"
    $c = ReadJson $file
    $start = if ($c) { [long]$c.start } else { Now }
    Move-Item -Force (WriteJson $file @{ start = $start; end = (Now) }) $file
}

# Was $owner running a command at some point since $since? Then a change to its files in that time is its own.
function RanSince([string]$owner, [long]$since) {
    $c = ReadJson (Join-Path $Dir "c-$(Hash $owner).json")
    $c -and ([long]$c.end -eq 0 -or [long]$c.end -ge $since)
}

# Does the command name this file? A bare name (es.json) counts for any file with it; a path (Bench/es.json) only
# for the file it points to.
function Names([string]$cmd, [string]$full) {
    $name = [IO.Path]::GetFileName($full)
    # Quoted text with spaces is a message (a commit, a note for the Banco), not a path: what it mentions isn't touched.
    $cmd = $cmd -replace '"[^"]*\s[^"]*"|''[^'']*\s[^'']*''', '""'
    foreach ($m in [regex]::Matches($cmd, "(?i)([^\s'`"=<>|;&()]*?)$([regex]::Escape($name))(?=$|[\s'`";&|)<>])")) {
        $at = $m.Index
        if ($at -gt 0 -and $cmd[$at - 1] -notmatch "[\s'`"=<>|;&()]") { continue }
        $token = ($m.Value -replace '/', '\').TrimStart('.', '\')
        if ($token -eq $name -or $full.EndsWith('\' + $token, [StringComparison]::OrdinalIgnoreCase) -or
            $full.Equals($token, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    $false
}

# The bench's traffic light for this session (#167): what it waits for; no state = nothing stops it.
function SetLight($me, [string]$state, [string]$path = '', [int]$place = 0, [int]$line = 0, [string]$holder = '') {
    $file = Join-Path $Dir "t-$(Hash $me.owner).json"
    if (-not $state) { Remove-Item $file -Force -ErrorAction SilentlyContinue; return }
    $old = ReadJson $file
    if ($old -and $old.state -eq $state -and $old.path -eq $path -and [int]$old.place -eq $place -and [int]$old.line -eq $line) { return }
    $t = [ordered]@{ session = $me.session; agent = $me.agent; state = $state; path = $path; place = $place; line = $line; holder = $holder; at = (Now) }
    New-Item -ItemType Directory -Force $Dir | Out-Null
    Move-Item -Force (WriteJson $file $t) $file
}

# A new tool call: a wait that went through, or a red left by the last turn (the person read it), is over. 'atencion'
# stays for the rest of the turn: the agent was told to stop.
function ClearLight($me) {
    $t = ReadJson (Join-Path $Dir "t-$(Hash $me.owner).json")
    if ($t -and ($t.ended -or $t.state -ne 'atencion')) { SetLight $me '' }
}

# Who the light says it waits for: the session's first characters, as the bench shows a ticket's agent.
function HolderText($held) {
    if (-not $held) { return '' }
    $s = [string]$held.session
    $s.Substring(0, [Math]::Min(8, $s.Length))
}

# The agent reads why the tool didn't run: a denial, not a hook failure.
function Deny([string]$why) {
    [Console]::Out.Write((@{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $why } } | ConvertTo-Json -Compress -Depth 4))
    exit 0
}

# Which ticket each session works on (the newest it took): pruebas\lista.json read once, keyed by the session's first
# characters, as TestList writes them (agent:1a2b3c4d).
function LoadTickets {
    if ($null -ne $script:tickets) { return }
    $script:tickets = @{}
    $script:takers = @{}
    $j = ReadJson (Join-Path $Root 'pruebas\lista.json')
    if (-not $j) { return }
    foreach ($i in $j.items) {
        $by = [string]$i.takenBy
        if (-not $by.StartsWith('agent:') -or $by.Length -le 6) { continue }
        $script:takers[[int]$i.id] = $by.Substring(6)
        if ([int]$i.id -gt [int]$script:tickets[$by.Substring(6)]) { $script:tickets[$by.Substring(6)] = [int]$i.id }
    }
}

function TicketOf([string]$session) {
    LoadTickets
    foreach ($k in $script:tickets.Keys) { if ($session.StartsWith($k, [StringComparison]::OrdinalIgnoreCase)) { return $script:tickets[$k] } }
    0
}

# The first characters of the session that took ticket $n ('' when nobody has it).
function TakerOf([int]$n) { LoadTickets; [string]$script:takers[$n] }

# How an alert names a session: its ticket, or its first characters; '' is the user (a hand run).
function SessionName([string]$session) {
    if (-not $session) { return 'el usuario' }
    $t = TicketOf $session
    if ($t) { "la sesión del #$t" } else { "la sesión $($session.Substring(0, [Math]::Min(8, $session.Length)))" }
}

# The sessions whose Claude is open now (p-*.json of a live process).
function LiveSessions {
    @(foreach ($f in Get-ChildItem $Dir -Filter 'p-*.json' -ErrorAction SilentlyContinue) {
        $c = ReadJson $f.FullName
        if ($c -and $c.session -and $c.pid -and -not (Gone $c)) { [string]$c.session }
    }) | Select-Object -Unique
}

# Leaves an alert for $to (a session, or an owner: session/agent). Nothing to oneself, and an alert already waiting
# unread with the same text isn't written twice (a build that fails again).
function Send([string]$to, [string]$kind, [string]$text, [string]$path = '', [string]$from = '') {
    if (-not $to -or $to -eq $from -or -not $text) { return $false }
    $text = $text.Substring(0, 1).ToUpper() + $text.Substring(1)
    New-Item -ItemType Directory -Force $Dir | Out-Null
    $h = Hash $to
    foreach ($f in Get-ChildItem $Dir -Filter "m-$h-*.json" -ErrorAction SilentlyContinue) {
        $m = ReadJson $f.FullName
        if ($m -and -not [long]$m.read -and $m.text -eq $text) { return $true }
    }
    $ms = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $file = Join-Path $Dir "m-$h-$ms-$(Get-Random -Maximum 100000).json"
    $m = [ordered]@{ to = $to; from = $from; kind = $kind; text = $text; path = $path; at = (Now); read = 0 }
    Move-Item -Force (WriteJson $file $m) $file
    LogAlert $m
    $true
}

# The bench's conversation between agents (#269): every alert also goes, one JSON per line, to avisos.jsonl, which keeps
# LogKeepSec (the m-*.json go once read). A line is written in one call, retried while another session appends.
function LogAlert($m) {
    $line = ($m | ConvertTo-Json -Compress) + "`n"
    $log = Join-Path $Dir 'avisos.jsonl'
    for ($n = 0; $n -lt 20; $n++) {
        try { [IO.File]::AppendAllText($log, $line, $Utf8); return } catch [IO.IOException] { Start-Sleep -Milliseconds 25 }
    }
}

# Drops the log's lines older than LogKeepSec, at most once an hour (a turn that ends looks).
function TrimLog {
    $log = Join-Path $Dir 'avisos.jsonl'
    if (-not (Test-Path $log) -or (Get-Item $log).CreationTimeUtc -gt [DateTime]::UtcNow.AddHours(-1)) { return }
    try {
        $now = Now
        $keep = @(foreach ($l in [IO.File]::ReadAllLines($log, $Utf8)) {
            if (-not $l) { continue }
            $j = $null
            try { $j = $l | ConvertFrom-Json } catch { }
            if ($j -and $now - [long]$j.at -le $LogKeepSec) { $l }
        })
        $tmp = "$log.$PID.tmp"
        [IO.File]::WriteAllText($tmp, $(if ($keep.Count) { ($keep -join "`n") + "`n" } else { '' }), $Utf8)
        Move-Item -Force $tmp $log
        # the new file starts the hour over
        (Get-Item $log).CreationTimeUtc = [DateTime]::UtcNow
    } catch { }
}

# This owner's unread alerts, and its session's, oldest first, marked read: what the agent gets told now ('' if none).
function Inbox($me) {
    if (-not $me.session -or -not (Test-Path $Dir)) { return '' }
    $files = @(Get-ChildItem $Dir -Filter "m-$(Hash $me.owner)-*.json" -ErrorAction SilentlyContinue)
    if ($me.owner -ne $me.session) { $files += @(Get-ChildItem $Dir -Filter "m-$(Hash $me.session)-*.json" -ErrorAction SilentlyContinue) }
    $lines = @()
    $stop = $false
    foreach ($f in $files | Sort-Object { [long]$_.Name.Split('-')[2] }) {
        $m = ReadJson $f.FullName
        if (-not $m -or [long]$m.read) { continue }
        $m.read = Now
        Move-Item -Force (WriteJson $f.FullName $m) $f.FullName
        $label = switch ([string]$m.kind) { 'atencion' { $stop = $true; 'ATENCIÓN' } 'pedido' { 'PEDIDO' } 'mensaje' { 'MENSAJE' } default { 'AVISO' } }
        $lines += "- $label de $(SessionName ([string]$m.from)): $($m.text)"
    }
    if (-not $lines.Count) { return '' }
    $how = if ($stop) { 'Hay una ATENCIÓN: pará y avisale al usuario qué dice.' } else { 'Tenelos en cuenta y seguí (si un archivo tuyo cambió, volvé a leerlo antes de editarlo).' }
    "Avisos de otras sesiones de XTreme (sistema de bloqueos, #263):`n$($lines -join "`n")`n$how Para contestar: tools\bloqueos.ps1 -Avisa <#ticket|sesión|todos> -Texto `"…`"."
}

# What a PostToolUse hook says: the alerts as context, and a block reason when the command clashed.
function Post([string]$context, [string]$why = '') {
    if (-not $context -and -not $why) { return }
    $o = @{}
    if ($why) { $o.decision = 'block'; $o.reason = $why }
    if ($context) { $o.hookSpecificOutput = @{ hookEventName = 'PostToolUse'; additionalContext = $context } }
    [Console]::Out.Write(($o | ConvertTo-Json -Compress -Depth 4))
}

# A file let go: whoever is first in its line hears it's kept for them.
function TellFreed([string]$full, $me) {
    $h = Hash $full
    $rel = Rel $full
    $q = @(Queue $h)
    if ($q.Count) { $null = Send ([string]$q[0].owner) 'aviso' "Se liberó $rel y queda reservado para vos $([int]($ReserveSec / 60)) min: volvé a intentar la acción que estaba en cola." $rel $me.session }
    # and whoever asked for it (-PideArchivo) without being in line
    foreach ($f in Get-ChildItem $Dir -Filter "k-$h-*.json" -ErrorAction SilentlyContinue) {
        $k = ReadJson $f.FullName
        Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue
        if (-not $k -or ($q.Count -and [string]$q[0].owner -eq [string]$k.session)) { continue }
        $why = if ($q.Count) { "; hay $($q.Count) en la cola antes que vos" } else { '' }
        $null = Send ([string]$k.session) 'aviso' "Se liberó $rel, que habías pedido$why." $rel $me.session
    }
}

# Lets go of one lock, keeping it a while for whoever is in line.
function Release($lock, $me) {
    $h = Hash $lock.path
    if (@(Queue $h).Count) { Move-Item -Force (WriteJson (Join-Path $Dir "r-$h.json") @{ at = (Now) }) (Join-Path $Dir "r-$h.json") }
    Remove-Item $lock.File -Force -ErrorAction SilentlyContinue
    TellFreed $lock.path $me
}

# Who should hear about $full: whoever holds it, else whoever changed it last and is still open; never $me's own.
function Concerned([string]$full, $me) {
    $l = ReadJson (Join-Path $Dir "l-$(Hash $full).json")
    if ($l -and -not (Mine $l $me) -and -not (Stale $l)) { return [string]$l.owner }
    $e = ReadJson (Join-Path $Dir "e-$(Hash $full).json")
    if ($e -and -not (Mine $e $me) -and (Now) - [long]$e.at -le $ChangedSec -and (LiveSessions) -contains [string]$e.session) { return [string]$e.owner }
    ''
}

# A build this command ran failed: each file with an error that another session holds or changed tells that session.
function TellBuild([string]$cmd, $response, $me) {
    if ($cmd -notmatch '(?i)dotnet\s+(build|msbuild)|probar\.ps1|msbuild') { return }
    $text = if ($response -is [string]) { $response } else { ($response.PSObject.Properties | ForEach-Object { [string]$_.Value }) -join "`n" }
    $seen = @{}
    foreach ($m in [regex]::Matches($text, '(?m)(?<file>[A-Za-z]:\\[^\r\n(:]+?)\((?<line>\d+)(,\d+)?\)\s*:\s*error\s+(?<code>\w+)\s*:\s*(?<msg>[^\r\n\[]+)')) {
        $full = [IO.Path]::GetFullPath($m.Groups['file'].Value)
        if ($seen[$full] -or -not $full.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $seen[$full] = 1
        $to = Concerned $full $me
        if (-not $to) { continue }
        $rel = Rel $full
        $null = Send $to 'aviso' "La compilación falla en $rel($($m.Groups['line'].Value)): $($m.Groups['code'].Value) $($m.Groups['msg'].Value.Trim()). Es un archivo tuyo: arreglalo apenas puedas." $rel $me.session
    }
}

function Who($lock) {
    $at = if ($lock.since) { [long]$lock.since } else { [long]$lock.joined / 1000 }
    $since = [DateTimeOffset]::FromUnixTimeSeconds($at).ToLocalTime().ToString('HH:mm')
    $who = "la sesión $($lock.session.Substring(0, [Math]::Min(8, $lock.session.Length)))"
    if ($lock.agent) { $who += " (subagente)" }
    "$who, desde las $since"
}

# Places in line for a file: q-<path hash>-<owner hash>.json, oldest first. One left alone longer than KeepSec is gone.
function Queue([string]$h) {
    if (-not (Test-Path $Dir)) { return @() }
    $all = foreach ($f in Get-ChildItem $Dir -Filter "q-$h-*.json") {
        $q = ReadJson $f.FullName
        if (-not $q -or (Now) - [long]$q.seen -gt $KeepSec) { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue; continue }
        $q
    }
    @($all | Sort-Object { [long]$_.joined }, owner)
}

# Joins the line (or keeps the place it already had) and says it's still there.
function Enqueue([string]$h, [string]$full, $me) {
    $file = Join-Path $Dir "q-$h-$(Hash $me.owner).json"
    $q = ReadJson $file
    $joined = if ($q) { [long]$q.joined } else { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
    $mine = [ordered]@{ owner = $me.owner; session = $me.session; agent = $me.agent; path = $full; joined = $joined; seen = (Now) }
    $tmp = WriteJson $file $mine
    [IO.File]::Copy($tmp, $file, $true); Remove-Item $tmp -Force
    [pscustomobject]$mine
}

# Whoever is ahead of $mine. One who is off doing something else only keeps the place while the file is held, and for
# ReserveSec after it was let go: the line doesn't stall on someone who isn't there.
function Ahead([string]$h, $mine, [bool]$held) {
    $freed = ReadJson (Join-Path $Dir "r-$h.json")
    $reserved = $held -or ($freed -and (Now) - [long]$freed.at -le $ReserveSec)
    @(Queue $h | Where-Object {
        $_.owner -ne $mine.owner -and
        ([long]$_.joined -lt [long]$mine.joined -or ([long]$_.joined -eq [long]$mine.joined -and $_.owner -lt $mine.owner)) -and
        ($reserved -or (Now) - [long]$_.seen -le $ActiveSec)
    })
}

# Does $from wait, through any chain of sessions, for something $me holds? Then nobody in the chain would ever move.
function Cycle([string]$from, $me) {
    $seen = @{}
    $next = @($from)
    while ($next.Count) {
        $o = $next[0]; $next = @($next | Select-Object -Skip 1)
        if ($o -eq $me.owner) { return $true }
        if ($seen[$o]) { continue }
        $seen[$o] = 1
        foreach ($f in Get-ChildItem $Dir -Filter "q-*-$(Hash $o).json" -ErrorAction SilentlyContinue) {
            $q = ReadJson $f.FullName
            if (-not $q -or (Now) - [long]$q.seen -gt $KeepSec) { continue }
            $l = ReadJson (Join-Path $Dir "l-$(Hash $q.path).json")
            if ($l -and -not (Stale $l)) { $next += [string]$l.owner }
        }
    }
    $false
}

# Takes $full for $me. Returns $null when it's ours; else, without $wait, the lock that has it; with $wait, after
# SliceSec in line, where this session stands (or that the line is a cycle).
function Take([string]$full, $me, [bool]$wait) {
    New-Item -ItemType Directory -Force $Dir | Out-Null
    $h = Hash $full
    $file = Join-Path $Dir "l-$h.json"
    $qfile = Join-Path $Dir "q-$h-$(Hash $me.owner).json"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $held = ReadJson $file
        if ($held -and (Mine $held $me)) {
            if ($held.owner -ne $me.owner) { Remove-Item $qfile -Force -ErrorAction SilentlyContinue; return $null }
            $held.touched = Now
            $tmp = WriteJson $file $held
            [IO.File]::Copy($tmp, $file, $true); Remove-Item $tmp -Force
            Remove-Item $qfile -Force -ErrorAction SilentlyContinue
            return $null
        }
        if ($held -and (Stale $held)) { Remove-Item $file -Force -ErrorAction SilentlyContinue; continue }

        $ahead = @()
        if ($wait) {
            $mine = Enqueue $h $full $me
            $ahead = @(Ahead $h $mine ([bool]$held))
        }
        if (-not $held -and -not $ahead.Count) {
            $lock = [ordered]@{ owner = $me.owner; session = $me.session; agent = $me.agent; path = $full; since = (Now); touched = (Now) }
            $proc = ClaudeProcess $me
            if ($proc) { $lock.pid = $proc.pid; $lock.started = $proc.started }
            $tmp = WriteJson $file $lock
            try {
                [IO.File]::Move($tmp, $file)
                Remove-Item $qfile, (Join-Path $Dir "r-$h.json") -Force -ErrorAction SilentlyContinue
                # #263: whoever changed it last, from another session still open, hears it's someone else's now
                $e = ReadJson (Join-Path $Dir "e-$h.json")
                if ($e -and -not (Mine $e $me) -and [string]$e.session -ne $me.session -and (Now) - [long]$e.at -le $ChangedSec -and
                    (LiveSessions) -contains [string]$e.session) {
                    $rel = Rel $full
                    $at = [DateTimeOffset]::FromUnixTimeSeconds([long]$e.at).ToLocalTime().ToString('HH:mm')
                    $null = Send ([string]$e.owner) 'aviso' "$(SessionName $me.session) tomó $rel, que cambiaste vos a las $at. Si te falta algo ahí, pedíselo: tools\bloqueos.ps1 -PideArchivo $rel" $rel $me.session
                }
                return $null
            }
            catch { Remove-Item $tmp -Force -ErrorAction SilentlyContinue; continue }
        }
        if (-not $wait) { return $held }

        $blocker = if ($held) { [string]$held.owner } else { [string]$ahead[0].owner }
        if (Cycle $blocker $me) {
            Remove-Item $qfile -Force -ErrorAction SilentlyContinue
            # who it waits for: the holder, or, a file let go, whoever is ahead in its line (#183)
            SetLight $me 'bloqueado' (Rel $full) 0 0 (HolderText $(if ($held) { $held } else { $ahead[0] }))
            return [pscustomobject]@{ Cycle = $true; Held = $held; Path = $full }
        }
        # No light for a wait: the bench reads the line itself (LockLight), so every place in it stays current.
        if ($clock.Elapsed.TotalSeconds -gt $SliceSec) {
            return [pscustomobject]@{ Cycle = $false; Held = $held; Path = $full; Place = $ahead.Count + 1; Line = @(Queue $h).Count }
        }
        Start-Sleep -Seconds 1
    }
}

function Refuse($r) {
    $what = Rel $r.Path
    $holder = if ($r.Held) { "lo tiene $(Who $r.Held)" } else { 'está reservado para quien estaba antes en la cola' }
    if ($r.Cycle) {
        Deny "BLOQUEADO: $what $holder, y esa espera termina en un archivo que tenés vos: nadie avanzaría. Terminá tu turno ya, así soltás tus archivos, y avisale al usuario qué quedó sin hacer. No lo cambies por otro camino (shell, Write, git, scripts)."
    }
    Deny "EN COLA: $what $holder. Estás $($r.Place)° de $($r.Line) en la espera. Tu lugar queda guardado $([int]($KeepSec / 60)) min: seguí con otra parte del trabajo si la hay y volvé a intentar esta misma acción (contale al usuario en qué puesto estás). Si lo necesitás ya, pedíselo: tools\bloqueos.ps1 -PideArchivo $what (te llega un aviso cuando se libere). No lo cambies por otro camino (shell, Write, git, scripts)."
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
    foreach ($l in Locks) { if (-not (Mine $l $me) -and -not (Stale $l)) { $paths[$l.path] = 1 } }
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
    $lines = @{}
    foreach ($f in Get-ChildItem $Dir -Filter 'q-*.json' -ErrorAction SilentlyContinue) { $lines[$f.Name.Split('-')[1]] = 1 }
    $paths = @{}
    foreach ($l in $all) { $paths[[string]$l.path] = $l }
    foreach ($h in $lines.Keys) { $q = @(Queue $h); if ($q.Count -and -not $paths.ContainsKey([string]$q[0].path)) { $paths[[string]$q[0].path] = $null } }
    if (-not $paths.Count) { 'Nadie tiene archivos tomados.'; exit 0 }
    foreach ($p in $paths.Keys | Sort-Object) {
        $l = $paths[$p]
        if ($l) { "$(Rel $p)  ←  $(Who $l)$(if (Gone $l) { ' (su sesión se cerró: está libre)' } elseif (Stale $l) { ' (vencido)' })" } else { "$(Rel $p)  ←  libre, reservado para el primero en la cola" }
        $i = 0
        foreach ($q in Queue (Hash $p)) { $i++; "    $i° en la cola: $(Who $q)" }
    }
    exit 0
}

# Alerts by hand (#263): the agent's own session comes from CLAUDE_CODE_SESSION_ID ('' = the user, from a console).
if ($Avisa -or $PideArchivo -or $SueltaArchivo -or $Buzon) {
    $mine = [string]$env:CLAUDE_CODE_SESSION_ID
    $me = @{ session = $mine; agent = ''; owner = $mine }
    if ($Buzon) {
        if (-not $mine) { 'Sin sesión de Claude (CLAUDE_CODE_SESSION_ID): no hay buzón que leer.'; exit 1 }
        $t = Inbox $me
        if ($t) { $t } else { 'No hay avisos nuevos.' }
        exit 0
    }
    if ($Avisa) {
        # -Avisa 60 "texto": the text lands in the first positional parameter
        if (-not $Texto) { $Texto = $Evento }
        if (-not $Texto) { 'Falta el texto: -Texto "…".'; exit 1 }
        $live = @(LiveSessions | Where-Object { $_ -ne $mine })
        $to = if ($Avisa -eq 'todos') { $live }
              elseif ($Avisa -match '^#?(\d+)$') { $p = TakerOf ([int]$Matches[1]); if ($p) { @($live | Where-Object { $_.StartsWith($p, [StringComparison]::OrdinalIgnoreCase) }) } else { @() } }
              else { @($live | Where-Object { $_.StartsWith($Avisa, [StringComparison]::OrdinalIgnoreCase) }) }
        if (-not @($to).Count) { "No hay otra sesión abierta que corresponda a «$Avisa»."; exit 1 }
        $kind = if ($Atencion) { 'atencion' } else { 'mensaje' }
        foreach ($s in $to) { $null = Send $s $kind $Texto '' $mine }
        "Avisado: $((@($to) | ForEach-Object { SessionName $_ }) -join ', ')."
        exit 0
    }
    $p = if ($PideArchivo) { $PideArchivo } else { $SueltaArchivo }
    $full = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $Root $p }))
    $rel = Rel $full
    $file = Join-Path $Dir "l-$(Hash $full).json"
    $lock = ReadJson $file
    if ($lock) { $lock | Add-Member -NotePropertyName File -NotePropertyValue $file }
    if ($SueltaArchivo) {
        if (-not $lock -or -not $mine -or [string]$lock.session -ne $mine) { "$rel no lo tiene esta sesión: no hay nada que soltar."; exit 1 }
        Release $lock $me
        "Se soltó $rel."
        exit 0
    }
    if (-not $lock -or (Stale $lock)) { "$rel está libre."; exit 0 }
    if ($mine -and [string]$lock.session -eq $mine) { "$rel ya es de esta sesión."; exit 0 }
    $null = Send ([string]$lock.owner) 'pedido' "$(SessionName $mine) necesita $rel. Cuando termines con él, soltalo: tools\bloqueos.ps1 -SueltaArchivo $rel (o terminá tu turno)." $rel $mine
    if ($mine) {
        $k = Join-Path $Dir "k-$(Hash $full)-$(Hash $mine).json"
        Move-Item -Force (WriteJson $k @{ session = $mine; path = $full; at = (Now) }) $k
    }
    "Se lo pedí a $(SessionName ([string]$lock.session)). Cuando lo suelte, te llega un aviso."
    exit 0
}

[Console]::InputEncoding = $Utf8
try {
    $in = if ($Sesion) { [pscustomobject]@{ session_id = $Sesion } } else { [Console]::In.ReadToEnd() | ConvertFrom-Json }
    $agent = if ($in.agent_id) { [string]$in.agent_id } else { '' }
    $me = @{ session = [string]$in.session_id; agent = $agent; owner = if ($agent) { "$($in.session_id)/$agent" } else { [string]$in.session_id } }

    switch ($Evento) {
        'editar' {
            $full = EditPath $in
            if (-not $full) { exit 0 }
            $r = Take $full $me $true
            if ($r) { Refuse $r }
            ClearLight $me
            AddDomain $me (Domain $full)
            MarkChanged $full $me $true
            exit 0
        }
        'consola' {
            $cmd = [string]$in.tool_input.command
            # a session that only took a ticket (-Toma) and reads has its process written down too (#164)
            if ($me.session) { $null = ClaudeProcess $me }
            # Each of these throws away changes nobody committed, and on master they may be another session's.
            # Only git where a command starts, so a mention inside a text or a string doesn't count.
            $git = '(?i)(^|[;&|(\r\n])\s*git\s+(-C\s+\S+\s+|-c\s+\S+\s+)*(restore\b(?![^;&|\r\n]*--staged)|stash\b(?!\s+(list|show)\b)|clean\b|reset\b[^;&|\r\n]*--hard|checkout\b[^;&|\r\n]*(\s--(\s|$)|\s\.(\s|$)|\sHEAD\b))'
            if ($cmd -match $git) {
                Deny "BLOQUEADO: '$($Matches[0].Trim())' puede tirar cambios sin commitear, propios o de otra sesión. No lo hagas ni lo reemplaces por otro comando: si de verdad hace falta, pedile al usuario que lo corra él."
            }
            # A leading "cd somewhere &&" doesn't make a read a write.
            $bare = $cmd -replace '^(\s*(cd|Set-Location)\s+("[^"]*"|''[^'']*''|\S+)\s*(&&|;))+', ''
            $readOnly = $bare -match '^\s*(cat|head|tail|grep|rg|less|wc|ls|Get-Content|Select-String|git\s+(diff|log|status|show|blame))\b' -and $cmd -notmatch '>'
            if (-not $readOnly) {
                foreach ($l in @(Locks)) {
                    if ((Mine $l $me) -or (Stale $l) -or (Foreign $me $l.path)) { continue }
                    if (Names $cmd $l.path) {
                        $r = Take $l.path $me $true
                        if ($r) { Refuse $r }
                    }
                }
            }
            ClearLight $me
            $snap = Snapshot $me
            $snap['@start'] = Now
            New-Item -ItemType Directory -Force $Dir | Out-Null
            $file = Join-Path $Dir "s-$(Hash ([string]$in.tool_use_id)).json"
            Move-Item -Force (WriteJson $file $snap) $file
            CommandStarts $me
            exit 0
        }
        'pedido' {
            # #173: a session sent to tickets by number (/xt_revisar-workbench 60 61, from the bench or typed) takes
            # them at once, before the agent reads anything: the bench shows them in progress while its Claude runs,
            # also after the bench restarts, even if the agent never gets to its own -Toma. Only the alerts go to
            # stdout (#263): here it is added to the prompt.
            if (-not $me.session) { exit 0 }
            $null = ClaudeProcess $me
            $context = Inbox $me
            if ($context) { [Console]::Out.Write($context) }
            if ([string]$in.prompt -notmatch '^\s*/xt_revisar-workbench\b(?<args>[^\r\n]*)') { exit 0 }
            $nums = @([regex]::Matches($Matches['args'], '\d+') | ForEach-Object { [int]$_.Value } | Where-Object { $_ -gt 0 } | Select-Object -Unique)
            $xtlab = Join-Path $Root 'src\XtremeDaw.Lab\bin\Debug\net10.0-windows\xtlab.exe'
            $list = Join-Path $Root 'pruebas\lista.json'
            if (-not $nums.Count -or -not (Test-Path $xtlab) -or -not (Test-Path $list)) { exit 0 }
            # the ticket is written down as this session's, as its own -Toma would (TestList.Who)
            $env:CLAUDE_CODE_SESSION_ID = $me.session
            # one taken by another session (exit 2) stays theirs: the skill sees it at its -Toma
            foreach ($n in $nums) { & $xtlab lista tomar $n --archivo $list *> $null }
            exit 0
        }
        'espera' {
            # Notification (#278): Claude waits for the person. A permission prompt leaves nothing in the transcript,
            # so it is written down here for the bench's map (AgentTranscript.Waiting); any newer line there clears it.
            $kind = switch ([string]$in.notification_type) { 'permission_prompt' { 'permiso' } 'elicitation_dialog' { 'pregunta' } default { '' } }
            if (-not $kind -or -not $me.session) { exit 0 }
            New-Item -ItemType Directory -Force $Dir | Out-Null
            $file = Join-Path $Dir "u-$(Hash $me.session).json"
            Move-Item -Force (WriteJson $file @{ session = $me.session; kind = $kind; at = (Now) }) $file
            exit 0
        }
        'avisos' {
            # PostToolUse of an edit (#263): the alerts that came meanwhile. #299: the write is done now; its time is
            # what a command running meanwhile compares against.
            $full = EditPath $in
            if ($full) { $null = Take $full $me $false; MarkChanged $full $me }
            Post (Inbox $me)
            exit 0
        }
        'despues' {
            $context = Inbox $me
            TellBuild ([string]$in.tool_input.command) $in.tool_response $me
            $file = Join-Path $Dir "s-$(Hash ([string]$in.tool_use_id)).json"
            $before = ReadJson $file
            if (-not $before) { Post $context; exit 0 }
            Remove-Item $file -Force -ErrorAction SilentlyContinue
            CommandEnds $me
            $old = @{}
            foreach ($pr in $before.PSObject.Properties) { $old[$pr.Name] = $pr.Value }
            $start = if ($old.ContainsKey('@start')) { [long]$old['@start'] } else { [long]::MaxValue }
            $old.Remove('@start')
            $now = Snapshot $me
            $changed = @()
            foreach ($p in $now.Keys) {
                if ($old.ContainsKey($p) -and $old[$p] -eq $now[$p]) { continue }
                if (-not $old.ContainsKey($p) -and $now[$p] -eq 'x') { continue }
                if (-not $p.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) { continue }
                $changed += $p
            }
            foreach ($p in $old.Keys) { if (-not $now.ContainsKey($p)) { $changed += $p } }
            $clash = @()
            foreach ($p in $changed) {
                # The other program's files changed under this command because its own sessions are working on them.
                if (Foreign $me $p) { continue }
                # Someone else changed it, by hand or by a command, while this one ran: the change is theirs.
                $e = ReadJson (Join-Path $Dir "e-$(Hash $p).json")
                # #299: an edit still under way (its PostToolUse not run yet) is theirs too.
                if ($e -and -not (Mine $e $me) -and ([long]$e.at -ge $start -or ($e.pending -and [long]$e.at -ge $start - $EditLagSec))) { continue }
                $held = Take $p $me $false
                if (-not $held) { MarkChanged $p $me; continue }
                # The holder edited it, or ran a command of its own, while this one ran: most likely its change.
                if ([long]$held.touched -ge $start -or (RanSince ([string]$held.owner) $start)) { continue }
                $clash += "$(Rel $p) ($(Who $held))"
                # #263: and the holder hears it too, so it reads the file again before its next edit
                $null = Send ([string]$held.owner) 'aviso' "Un comando de $(SessionName $me.session) cambió $(Rel $p), que tenés tomado. Volvé a leerlo antes de editarlo; si se perdió algo tuyo, avisale al usuario." (Rel $p) $me.session
            }
            $why = ''
            if ($clash.Count) {
                SetLight $me 'atencion' ($clash -join '; ')
                $why = "ATENCIÓN: este comando cambió archivos que está editando otra sesión: $($clash -join '; '). No intentes arreglarlo ni deshacerlo vos (podés pisar lo del otro): pará y avisale al usuario exactamente qué archivos y qué comando."
            }
            Post $context $why
            exit 0
        }
        'soltar' {
            # #263: alerts that came while the agent was finishing keep it going one more step to read them
            if (-not $Sesion -and -not $in.stop_hook_active) {
                $context = Inbox $me
                if ($context) { [Console]::Out.Write((@{ decision = 'block'; reason = $context } | ConvertTo-Json -Compress)); exit 0 }
            }
            foreach ($l in @(Locks)) {
                # -Sesion (the session is closing): its subagents' too
                if ($l.session -ne $me.session -or (-not $Sesion -and $l.agent -ne $me.agent)) { continue }
                # Someone is in line: the file is kept for them a while, even if they're off doing something else.
                Release $l $me
            }
            Remove-Item (Join-Path $Dir "d-$(Hash $me.owner).json"), (Join-Path $Dir "c-$(Hash $me.owner).json") -Force -ErrorAction SilentlyContinue
            # The traffic light (#167): a place in line ends with the turn; a red stays, marked, until the next tool
            # call. A session that closes (-Sesion) takes its subagents' lights with it.
            foreach ($f in Get-ChildItem $Dir -Filter 't-*.json' -ErrorAction SilentlyContinue) {
                $t = ReadJson $f.FullName
                if (-not $t -or $t.session -ne $me.session -or (-not $Sesion -and [string]$t.agent -ne $me.agent)) { continue }
                if ($Sesion -or $t.state -eq 'cola') { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue; continue }
                $t | Add-Member -Force -NotePropertyName ended -NotePropertyValue $true
                Move-Item -Force (WriteJson $f.FullName $t) $f.FullName
            }
            # A turn that ended gives up its places in line.
            Get-ChildItem $Dir -Filter "q-*-$(Hash $me.owner).json" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
            Get-ChildItem $Dir -Filter 'r-*.json' -ErrorAction SilentlyContinue |
                Where-Object { $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddSeconds(-$ReserveSec) } | Remove-Item -Force -ErrorAction SilentlyContinue
            # Snapshots of commands that never got their PostToolUse (refused by another hook, interrupted), old
            # change marks, and the w-*.json of the version before the queue.
            # Alerts read a while ago (the bench showed them), and requests for a file never let go.
            foreach ($f in Get-ChildItem $Dir -Filter 'm-*.json' -ErrorAction SilentlyContinue) {
                $m = ReadJson $f.FullName
                if (-not $m -or ([long]$m.read -and (Now) - [long]$m.read -gt $AlertKeepSec)) { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue }
            }
            TrimLog
            foreach ($pat in 's-*.json', 'e-*.json', 'c-*.json', 'p-*.json', 'w-*.json', 't-*.json', 'm-*.json', 'k-*.json', 'u-*.json') {
                Get-ChildItem $Dir -Filter $pat -ErrorAction SilentlyContinue |
                    Where-Object { $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-2) } |
                    # a session working longer than that keeps its process written down: the bench tells by it that its tickets are in progress (#173)
                    Where-Object { $pat -ne 'p-*.json' -or -not ($c = ReadJson $_.FullName) -or -not $c.pid -or (Gone $c) } |
                    Remove-Item -Force -ErrorAction SilentlyContinue
            }
            exit 0
        }
    }
}
catch {
    # A bug here must never stop the agent's work: let the tool run and leave a note for whoever fixes the hook.
    try {
        New-Item -ItemType Directory -Force $Dir | Out-Null
        [IO.File]::AppendAllText((Join-Path $Dir 'errores.log'), "$(Get-Date -Format s) $Evento $($_.InvocationInfo.ScriptLineNumber): $($_.Exception.Message)`r`n", $Utf8)
    } catch { }
    exit 0
}
exit 0
