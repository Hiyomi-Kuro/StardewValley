# Resolve a .NET SDK host for the Mod/ builds. Prints the absolute path on success.
# ASCII only: PowerShell round-trips of non-ASCII files are unsafe in this workspace.
#
# Why: the earlier documentation and scripts pointed at `.tmp/phase0.5/dotnet/dotnet.exe`,
# a private SDK directory that does not survive a fresh checkout. A missing path there looked
# like "no .NET SDK available" on machines that had one. This resolver never hardcodes a user
# directory; every candidate comes from an argument, an environment variable, a gitignored
# `.tmp/` directory of this repository, or the PATH.
#
# A host WITHOUT an SDK (runtime only) is rejected: `dotnet --list-sdks` must be non-empty,
# otherwise the build fails later inside MSBuild with a less obvious message.
#
# Usage:
#   pwsh -NoProfile -File Tools/find-dotnet.ps1
#   $dotnet = pwsh -NoProfile -File Tools/find-dotnet.ps1
#   pwsh -NoProfile -File Tools/find-dotnet.ps1 -Dotnet "C:\Program Files\dotnet\dotnet.exe"
#
# Exit codes: 0 = found (path on stdout), 1 = not found.
param(
    [string]$Dotnet = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$exeName = if ($env:OS -eq "Windows_NT") { "dotnet.exe" } else { "dotnet" }

function Test-Sdk([string]$path) {
    if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    try {
        $sdks = & $path --list-sdks 2>$null
    } catch {
        return $false
    }
    if ($LASTEXITCODE -ne 0) { return $false }
    foreach ($line in @($sdks)) {
        if ($line -and $line.Trim().Length -gt 0) { return $true }
    }
    return $false
}

$candidates = New-Object System.Collections.Generic.List[string]
if ($Dotnet) { $candidates.Add($Dotnet) }
if ($env:STARDEW_DOTNET) { $candidates.Add($env:STARDEW_DOTNET) }

# Private SDKs unpacked under .tmp/ by an earlier phase (gitignored, machine-local).
$tmpRoot = Join-Path $root ".tmp"
if (Test-Path -LiteralPath $tmpRoot) {
    foreach ($dir in Get-ChildItem -LiteralPath $tmpRoot -Directory -ErrorAction SilentlyContinue) {
        $candidates.Add((Join-Path (Join-Path $dir.FullName "dotnet") $exeName))
    }
}

$onPath = Get-Command dotnet -ErrorAction SilentlyContinue
if ($onPath -and $onPath.Source) { $candidates.Add($onPath.Source) }

foreach ($candidate in $candidates) {
    if ($candidate -and (Test-Sdk $candidate)) {
        Write-Output (Resolve-Path -LiteralPath $candidate).Path
        exit 0
    }
}

[Console]::Error.WriteLine("no .NET SDK found.")
[Console]::Error.WriteLine("  tried: -Dotnet, `$env:STARDEW_DOTNET, .tmp/*/dotnet/" + $exeName + ", PATH")
[Console]::Error.WriteLine("  an SDK (not only a runtime) is required: dotnet --list-sdks must be non-empty")
exit 1
