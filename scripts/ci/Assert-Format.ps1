# ARCH P-11 - format gate assertion script.
# Runs `dotnet format --verify-no-changes` over the solution and fails on any REAL
# formatting violation (exit code 1 -> CI job fails).
#
# Known false positive (filtered on purpose):
#   SDK 10.0.401's charset detector flags EVERY file with
#   "error CHARSET: 修复文件编码" even though all sources are valid UTF-8 without BOM
#   (repo convention: CodePage 65001, see Directory.Build.props). This is the same
#   CHARSET regression that the retired pre-commit hook used to filter
#   (TECH-DEBT v2.0 §4.7 #9 history; the hook itself is a permanent no-op stub).
#   Everything else (ENDOFLINE / IDExxxx / whitespace / using-order ...) is enforced
#   as-is - do NOT widen the filter.
#
# Usage:
#   pwsh -File scripts/ci/Assert-Format.ps1
#   # custom solution:
#   pwsh -File scripts/ci/Assert-Format.ps1 -Solution BatchAgentRunner.slnx

[CmdletBinding()]
param(
    [string]$Solution = 'BatchAgentRunner.slnx'
)

$charsetPattern = 'error CHARSET'

Write-Host "Running: dotnet format $Solution --verify-no-changes"
# Capture line-by-line (NO Out-String - it wraps at 80 cols and would split
# "error CHARSET:" into "error CH" / "error CHA" ..., defeating the filter).
$lines = @(& dotnet format $Solution --verify-no-changes 2>&1)

$real = @($lines | Where-Object { $_ -match 'error ' -and $_ -notmatch $charsetPattern })
$charset = @($lines | Where-Object { $_ -match $charsetPattern }).Count

if ($real.Count -gt 0) {
    Write-Host ''
    Write-Host "Format gate FAILED: $($real.Count) real formatting violation(s) (CHARSET false positives filtered):" -ForegroundColor Red
    foreach ($line in $real) {
        Write-Host "  $line"
    }
    Write-Host ''
    Write-Host 'Fix with: dotnet format BatchAgentRunner.slnx (then review the diff before committing).' -ForegroundColor Yellow
    exit 1
}

Write-Host ''
Write-Host "Format gate PASSED: no real formatting violations ($charset CHARSET false positive(s) filtered, see script header)." -ForegroundColor Green
exit 0
