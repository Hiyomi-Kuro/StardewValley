# Run every local test suite and print one summary table.
# ASCII only: PowerShell round-trips of non-ASCII files are unsafe in this workspace.
#
# What runs:
#   1. CI offline suite        (python, no game binaries)          always
#   2. modded-build static     (python, no game binaries)          always
#   3. P114 static safety audit (python, no game binaries)         always
#   4. privacy scan            (python, no game binaries)          always
#   5. device-precheck self-test (PowerShell, no device)           always
#   6. deploy-apk-safe fake-adb suite (PowerShell, no device)      always
#   7. mod pipeline            (needs the 65 game assemblies)      when --Refs is usable
#   8. loader + host + P114 probe (needs dotnet; --Refs for the example mod)
#
# Nothing here installs anything, touches a device, or writes outside --LogDir.
#
# Usage (repo root):
#   pwsh -NoProfile -File Tools/tests/run-all.ps1
#   pwsh -NoProfile -File Tools/tests/run-all.ps1 -Refs .tmp/p113/refs
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
    # Machine-independent discovery: argument, $env:STARDEW_DOTNET, .tmp/*/dotnet, PATH.
    $found = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "Tools\find-dotnet.ps1") 2>$null
    if ($LASTEXITCODE -eq 0 -and $found) { $Dotnet = ($found | Select-Object -First 1).Trim() }
    if (-not $Dotnet) { $Dotnet = "dotnet" }
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
    Invoke-Suite "modded-build-static" "modded-build-static.txt" { python Tools\tests\test_modded_build.py }
    Invoke-Suite "p114-static-audit" "p114-static-audit.txt" { python Tools\tests\test_p114_probe.py }
    Invoke-Suite "privacy-scan" "privacy-scan.txt" { python Tools\privacy-scan.py --include-untracked }

    if ($SkipDeviceSelfTest) {
        Invoke-Skipped "device-precheck-selftest" "-SkipDeviceSelfTest"
    } else {
        Invoke-Suite "device-precheck-selftest" "device-precheck-selftest.txt" {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_device_precheck.ps1
        }
    }

    Invoke-Suite "deploy-safe-fake-adb" "deploy-safe-fake-adb.txt" {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File Tools\tests\test_deploy_safe.ps1
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
