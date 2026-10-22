# ARCH P-11 - coverage gate assertion script.
# Parses the Cobertura file(s) produced by coverlet.collector (coverage.cobertura.xml)
# and asserts per-production-assembly line coverage is not below its threshold.
# If any production assembly is below threshold, exits with code 1 (CI fails).
#
# Design notes:
#   - Only production assemblies are evaluated (Core / Infrastructure / Agents / Desktop).
#     Test projects, Avalonia / CommunityToolkit and other 3rd-party assemblies are not in
#     the threshold table, so they never enter the denominator. (Therefore coverlet.runsettings
#     needs no Exclude block - which would otherwise make coverlet.collector v6 emit 0 coverage
#     due to its broken [Asm*]* wildcard handling.)
#   - Multiple test projects each emit one cobertura file. Production assemblies do not overlap
#     across the two files (main tests reference Core/Infra/Agents; Desktop tests only reference
#     Desktop), so summing per-assembly across files is safe (no double counting).
#   - $Thresholds = the measured baseline locked in at P-11 close (clean build + coverlet.runsettings
#     + fast bucket). The section 7.3 long-term targets (Core/Engine >=85% / Infra >=70% /
#     Desktop >=50%) are a ratchet-up goal and are intentionally NOT hard-gated here (otherwise CI
#     would stay red). Raise the values in $Thresholds as coverage improves in later phases.

[CmdletBinding()]
param(
    [string]$CoverageGlob = 'tests/*/TestResults/*/coverage.cobertura.xml'
)

# ARCH P-11 close baseline (measured 2026-10-22). Value = floor(measured line coverage %),
# leaving a small buffer. Raise in later phases together with the ARCH doc.
$Thresholds = @{}
$Thresholds['BatchAgentRunner.Core']           = 44
$Thresholds['BatchAgentRunner.Infrastructure'] = 39
$Thresholds['BatchAgentRunner.Agents']         = 23
$Thresholds['BatchAgentRunner.Desktop']        = 50

$files = @(Get-Item $CoverageGlob -ErrorAction SilentlyContinue)
if ($files.Count -eq 0) {
    Write-Error "No coverage file found at '$CoverageGlob'. Run dotnet test with --collect:'XPlat Code Coverage' --settings tests/coverlet.runsettings first."
    exit 1
}

# Per-assembly accumulators: covered / valid (line level).
$asmCov = @{}
$asmVal = @{}

foreach ($file in $files) {
    Write-Host "Parsing coverage file: $($file.FullName)"
    [xml]$doc = Get-Content $file.FullName
    $classes = $doc.SelectNodes('//class')
    foreach ($cls in $classes) {
        $fn = ($cls.filename -replace '\\', '/')
        $parts = $fn.Split('/')
        if ($parts.Length -gt 1 -and $parts[0] -eq 'src') {
            $asm = $parts[1]
        }
        else {
            $asm = $parts[0]
        }
        if (-not $Thresholds.ContainsKey($asm)) {
            # Only evaluate production assemblies.
            continue
        }
        if (-not $asmVal.ContainsKey($asm)) {
            $asmVal[$asm] = 0
            $asmCov[$asm] = 0
        }
        $lines = $cls.SelectNodes('./lines/line')
        foreach ($line in $lines) {
            $asmVal[$asm] = $asmVal[$asm] + 1
            $hits = [int]$line.GetAttribute('hits')
            if ($hits -gt 0) {
                $asmCov[$asm] = $asmCov[$asm] + 1
            }
        }
    }
}

$ok = $true
Write-Host ''
Write-Host ('{0,-34}{1,10}{2,10}{3,10}{4,8}{5,8}' -f 'ASSEMBLY', 'COVERED', 'VALID', 'RATE', 'THRESH', 'RESULT')
Write-Host ('-' * 84)

foreach ($asm in $Thresholds.Keys) {
    $th = $Thresholds[$asm]
    if (-not $asmVal.ContainsKey($asm) -or $asmVal[$asm] -eq 0) {
        Write-Host ('{0,-34}{1,10}{2,10}{3,10}{4,8}{5,8}' -f $asm, '-', '-', 'n/a', $th, 'FAIL')
        Write-Host "  !! No coverage data collected for assembly '$asm'." -ForegroundColor Red
        $ok = $false
    }
    else {
        $rate = [math]::Round($asmCov[$asm] / $asmVal[$asm] * 100, 2)
        $pass = $rate -ge $th
        if (-not $pass) {
            $ok = $false
        }
        if ($pass) {
            $result = 'PASS'
            $color = 'Green'
        }
        else {
            $result = 'FAIL'
            $color = 'Red'
        }
        Write-Host ('{0,-34}{1,10}{2,10}{3,9}%{4,8}{5,8}' -f $asm, $asmCov[$asm], $asmVal[$asm], $rate, $th, $result) -ForegroundColor $color
    }
}

Write-Host ('-' * 84)
if (-not $ok) {
    Write-Host 'Coverage gate FAILED: a production assembly is below its threshold. Add tests or raise the threshold (see script header).' -ForegroundColor Red
    exit 1
}
Write-Host 'Coverage gate PASSED: all production assemblies meet the locked baseline.' -ForegroundColor Green
exit 0
