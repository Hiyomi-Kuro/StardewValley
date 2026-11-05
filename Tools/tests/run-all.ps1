# Run every local test suite and print one summary table.
# ASCII only: PowerShell round-trips of non-ASCII files are unsafe in this workspace.
#
# What runs:
#   1. CI offline suite        (python, no game binaries)          always
#   2. privacy scan            (python, no game binaries)          always
#   3. device-precheck self-test (PowerShell, no device)           always
#   4. mod pipeline            (needs the 65 game assemblies)      when --Refs is usable
#   5. loader + host tests     (needs dotnet; --Refs for the example mod)
#
# Nothing here installs anything, touches a device, or writes outside --LogDir.
#
# Usage (repo root):
#   pwsh -NoProfile -File Tools/tests/run-all.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/tests/run-all.ps1 -Refs .tmp/phase0.5/refs
param(
    [string]$Dotnet = "",
    [string]$Refs = "",
    [string]$LogDir = "",
    [switch]$SkipDeviceSelfTest
)

$ErrorActionPreference = "Continue"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $LogDir) { $LogDir = Join-Path $root ".tmp\phase0.5.1\logs" }
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

if (-not $Dotnet) {
    $candidate = Join-Path $root ".tmp\phase0.5\dotnet\dotnet.exe"
    $Dotnet = if (Test-Path $candidate) { $candidate } else { "dotnet" }
}

$refsUsable = $false
if ($Refs) {
    $full = if ([System.IO.Path]::IsPathRooted($Refs)) { $Refs } else { Join-Path $root $Refs }
    if (Test-Path $full) {
        $count = (Get-ChildItem $full -Filter *.dll -ErrorAction SilentlyContinue | Measure-Object).Count
        if ($count -ge 58) { $refsUsable = $true; $Refs = $full }
    }
}

$results = New-Object System.Collections.Generic.List[object]

function Invoke-Suite([string]$name, [string]$logName, [scriptblock]$body) {
    $log = Join-Path $LogDir $logName
    Write-Host ""
    Write-Host ("=== " + $name + " ===")
    $started = Get-Date
    & $body 2>&1 | Tee-Object -FilePath $log | ForEach-Object { Write-Host $_ }
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 0 }
    $secs = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    $results.Add([pscustomobject]@{
        Suite = $name
        Exit = $code
        Seconds = $secs
        Status = if ($code -eq 0) { "PASS" } else { "FAIL" }
        Log = $log
    })
}

function Invoke-Skipped([string]$name, [string]$why) {
    Write-Host ""
    Write-Host ("=== " + $name + " ===")
    Write-Host ("SKIPPED: " + $why)
    $results.Add([pscustomobject]@{ Suite = $name; Exit = ""; Seconds = 0; Status = "SKIPPED"; Log = $why })
}

Push-Location $root
try {
    Invoke-Suite "ci-offline" "ci-offline.txt" { python Tools\tests\test_ci_offline.py }
    Invoke-Suite "privacy-scan" "privacy-scan.txt" { python Tools\privacy-scan.py --include-untracked }

    if ($SkipDeviceSelfTest) {
        Invoke-Skipped "device-precheck-selftest" "-SkipDeviceSelfTest"
    } else {
        Invoke-Suite "device-precheck-selftest" "device-precheck-selftest.txt" {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_device_precheck.ps1
        }
    }

    if ($refsUsable) {
        Invoke-Suite "mod-pipeline" "test-mod-pipeline.txt" {
            python Tools\tests\test_mod_pipeline.py --dotnet $Dotnet --refs $Refs
        }
    } else {
        Invoke-Skipped "mod-pipeline" "needs --Refs pointing at the 65 game assemblies (game content is not in CI)"
    }

    if ($refsUsable) {
        Invoke-Suite "loader" "test-loader.txt" {
            python Tools\tests\test_loader.py --dotnet $Dotnet --refs $Refs
        }
    } else {
        Invoke-Suite "loader (no example mod)" "test-loader-norefs.txt" {
            python Tools\tests\test_loader.py --dotnet $Dotnet
        }
    }

    Invoke-Suite "git-diff-check" "git-diff-check.txt" {
        git diff --check
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "=== summary ==="
$results | ForEach-Object {
    Write-Host ("{0,-28} {1,-8} {2,7}s  {3}" -f $_.Suite, $_.Status, $_.Seconds, $_.Log)
}
$failed = @($results | Where-Object { $_.Status -eq "FAIL" })
Write-Host ""
Write-Host ("{0} suites, {1} passed, {2} failed, {3} skipped" -f $results.Count,
    (@($results | Where-Object { $_.Status -eq "PASS" })).Count, $failed.Count,
    (@($results | Where-Object { $_.Status -eq "SKIPPED" })).Count)
if ($failed.Count -gt 0) { exit 1 }
exit 0
