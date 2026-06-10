# Thin PowerShell wrapper for ci/ci.sh (T33 — ADR-0014): finds Git's bash and runs the
# canonical bash mirror so there is exactly ONE source of truth for the local CI steps.
# Usage: .\ci\ci.ps1 [--no-smoke] [--export]

$ErrorActionPreference = "Stop"

$candidates = @(
    (Get-Command bash -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    "$env:ProgramFiles\Git\bin\bash.exe",
    "$env:ProgramFiles\Git\usr\bin\bash.exe"
) | Where-Object { $_ -and (Test-Path $_) }

if (-not $candidates) {
    Write-Error "ci.ps1: no bash found (install Git for Windows, or run ci/ci.sh from git-bash directly)."
    exit 1
}

$bash = $candidates | Select-Object -First 1
$script = Join-Path $PSScriptRoot "ci.sh"

& $bash $script @args
exit $LASTEXITCODE
