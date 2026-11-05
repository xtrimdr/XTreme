<#
.SYNOPSIS
  Closes the console of Claude that the Banco de pruebas opened, once its agent finished well (pedido del usuario, #143).

.DESCRIPTION
  The bench's console (AgentLauncher.InTerminal) runs Claude with XT_BENCH_CLOSE = a file that does not exist yet, and
  closes when Claude ends and that file is there. This script, run by the agent as its very last step, lets go of the
  session's file locks (tools\bloqueos.ps1 soltar: the Stop hook will not run), leaves the file and ends the Claude
  process it runs under. Elsewhere (the desktop app, a console opened by hand) it says so and does nothing.

  Run it only when everything went well: the fixes were sent with -Arregla, nothing is left to ask and nothing was
  dropped. What the agent would have said in the chat is lost with the console: it goes in the ticket first.
#>
$ErrorActionPreference = 'Stop'
$flag = $env:XT_BENCH_CLOSE
if (-not $flag) {
    Write-Host 'Esta sesión no la abrió el Banco en una consola: queda abierta.'
    exit 0
}

# the Claude process this shell runs under (a tool's shell is its child, maybe through another shell)
$claude = $null
$id = $PID
for ($n = 0; $n -lt 8 -and -not $claude; $n++) {
    $p = Get-CimInstance Win32_Process -Filter "ProcessId = $id" -ErrorAction SilentlyContinue
    if (-not $p) { break }
    $parent = Get-CimInstance Win32_Process -Filter "ProcessId = $($p.ParentProcessId)" -ErrorAction SilentlyContinue
    if (-not $parent) { break }
    if ($parent.Name -match '^claude(\.exe)?$') { $claude = $parent }
    $id = $parent.ProcessId
}
if (-not $claude) {
    Write-Host 'No encontré el proceso de Claude de esta sesión: la consola queda abierta.'
    exit 1
}

# the Stop hook will not come: the session's locks go now, not after they expire
if ($env:CLAUDE_CODE_SESSION_ID) {
    # by parameter: piped into another process, Windows PowerShell mangles the text and the hook couldn't read it, so
    # the locks stayed until they expired
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'bloqueos.ps1') soltar -Sesion $env:CLAUDE_CODE_SESSION_ID | Out-Null
}

[IO.File]::WriteAllText($flag, '')
Write-Host 'Listo: la consola se cierra.'
Stop-Process -Id $claude.ProcessId -Force
