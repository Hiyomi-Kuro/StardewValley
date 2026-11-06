# ============================================================================
#  DEPRECATED / DESTRUCTIVE - DO NOT USE AS THE DEFAULT DEPLOY PATH
#
#  This Phase 0 script runs `adb uninstall com.chucklefish.stardewvalley` before every
#  install. Uninstalling DELETES THE PLAYER'S SAVES, and this project's APK is signed with
#  a local debug certificate that does not match the official signature, so an in-place
#  replace is not possible either.
#
#  Phase 1.1.4 added Tools/deploy-apk-safe.ps1: read-only by default, install only with
#  -InstallApproved, and a signature gate that stops on mismatch. USE THAT INSTEAD.
#
#  This file is kept only so the historical build/patch pipeline stays auditable. The
#  destructive part is now behind an explicit -LegacyDestructiveInstall switch; without it
#  the script stops before touching any device.
# ============================================================================
#
# Deploy one injected APK and prove which build is actually running.
# ASCII only: PowerShell round-trips of non-ASCII files are unsafe in this workspace.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy-apk.ps1 -Mode full -HookMethod OnCreate
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy-apk.ps1 -Mode append -HookMethod OnCreate
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/deploy-apk.ps1 -Restore
#
# Layout note (Phase 0.5): every intermediate lives under .tmp/phase0.5/.
#   .tmp/phase0.5/dotnet/                 private .NET SDK (dotnet.exe)
#   .tmp/phase0.5/refs/                   the 65 assemblies extracted from the blobs
#   .tmp/phase0.5/out/assemblies.original.blob   pristine copy of the repo blob
#   .tmp/phase0.5/artifacts/              ModInjector build output
#
# Default hook method is OnCreate: that is the configuration Phase 0 actually proved
# (bootstrap-ran in logcat). The ADR's primary choice, OnCreatePartTwo, is one flag away.
param(
    [ValidateSet("full", "append")][string]$Mode = "full",
    [string]$HookMethod = "OnCreate",
    # Empty = auto-select the single attached device. Never default to a specific
    # endpoint: this script uninstalls and reinstalls the game.
    [string]$Serial = "",
    [string]$Dotnet = "",
    [string]$JavaHome = "",
    # Do not register the catch handler in Bootstrap.Start(): failures surface as a crash.
    [switch]$NoCatch,
    # Push ModFramework.Runtime.dll + probe into the app's Mods dir before launching.
    [switch]$PushMods,
    # Restore the repository blob only (no build/install); used to leave the tree clean.
    [switch]$Restore,
    [switch]$SkipInstall,
    # Phase 1.1.4: required for the historical uninstall+install and for -PushMods.
    # Without it the script still builds the APK, then stops before any device write.
    # Uninstalling deletes Saves; prefer Tools/deploy-apk-safe.ps1.
    [switch]$LegacyDestructiveInstall
)

$ErrorActionPreference = "Stop"

$root      = Split-Path -Parent $PSScriptRoot
$phase     = Join-Path $root ".tmp\phase0.5"
$dotnet    = if ($Dotnet) { $Dotnet } else { Join-Path $phase "dotnet\dotnet.exe" }
$python    = "python"
$blob      = Join-Path $root "app\src\main\apkroot\assemblies\assemblies.blob"
$manifest  = Join-Path $root "app\src\main\apkroot\assemblies\assemblies.manifest"
$original  = Join-Path $phase "out\assemblies.original.blob"

# Same portable adb resolution as Tools/device-precheck.ps1: no hardcoded user path.
function Resolve-Adb {
    $exe = if ($env:OS -eq "Windows_NT") { "adb.exe" } else { "adb" }
    $onPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($onPath -and $onPath.Source) { return $onPath.Source }
    foreach ($var in @("ANDROID_HOME", "ANDROID_SDK_ROOT")) {
        $sdk = [Environment]::GetEnvironmentVariable($var)
        if ($sdk) {
            $candidate = Join-Path (Join-Path $sdk "platform-tools") $exe
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    if ($env:ADB_PATH -and (Test-Path -LiteralPath $env:ADB_PATH -PathType Leaf)) { return $env:ADB_PATH }
    if ($env:LOCALAPPDATA) {
        $candidate = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

$adb = Resolve-Adb
if (-not $adb) {
    throw "no adb client found: put platform-tools on PATH or set ANDROID_HOME / ANDROID_SDK_ROOT"
}

if (-not $JavaHome) {
    if ($env:JAVA_HOME -and (Test-Path $env:JAVA_HOME)) {
        $JavaHome = $env:JAVA_HOME
    } else {
        $JavaHome = "C:\Program Files\Eclipse Adoptium\jdk-11.0.32.101-hotspot"
    }
}
if (-not (Test-Path $JavaHome)) {
    throw "JAVA_HOME not found: $JavaHome (pass -JavaHome or set JAVA_HOME)"
}
$env:JAVA_HOME = $JavaHome
$env:DOTNET_NOLOGO = "1"

$refs = Join-Path $phase "refs"
$apk  = Join-Path $root "app\build\outputs\apk\debug\app-debug.apk"

function Sha256([string]$path) {
    (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLower()
}

if ($Restore) {
    Copy-Item $original $blob -Force
    Write-Host "restored repo blob -> $(Sha256 $blob)"
    exit 0
}

if (-not (Test-Path $original)) { throw "missing original blob backup: $original (run the offline pipeline test first)" }
if (-not (Test-Path (Join-Path $refs "StardewValley.dll"))) {
    throw "missing extracted assemblies: $refs (run: python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.blob $refs --manifest $manifest)"
}
if (-not (Test-Path $dotnet)) { throw "missing .NET SDK: $dotnet (see Phase 0.5 report for the private-SDK setup)" }

# 1. patch
$injector = Join-Path $phase "artifacts\bin\ModInjector\release\ModInjector.dll"
& $dotnet build (Join-Path $root "Tools\ModInjector\ModInjector.csproj") -c Release `
    --artifacts-path (Join-Path $phase "artifacts") -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ModInjector build failed" }

$patched = Join-Path $phase "out\sv.$Mode.dll"
$patchArgs = @("patch",
    (Join-Path $refs "StardewValley.dll"), $patched,
    "--mode", $Mode, "--hook-method", $HookMethod, "--refs-dir", $refs)
if ($NoCatch) { $patchArgs += "--no-catch" }
& $dotnet $injector @patchArgs
if ($LASTEXITCODE -ne 0) { throw "patch failed (see self-check above)" }

# 2. rebuild blob
$newBlob = Join-Path $phase "out\assemblies.$Mode.blob"
& $python (Join-Path $root "Tools\AssemblyStore\store.py") replace $blob "StardewValley" $patched $newBlob --manifest $manifest
if ($LASTEXITCODE -ne 0) { throw "blob replace failed" }

# 3. build APK with the patched blob, then always restore the repo blob
try {
    Copy-Item $newBlob $blob -Force
    Push-Location $root
    & (Join-Path $root "gradlew.bat") --no-daemon assembleDebug | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { throw "gradle build failed" }
} finally {
    Pop-Location
    Copy-Item $original $blob -Force
}

$localApkHash = Sha256 $apk
Write-Host "local apk  : $localApkHash ($((Get-Item $apk).Length) bytes)"

if ($SkipInstall) { exit 0 }

# 3.4 Phase 1.1.4 safety gate. Everything from here on writes to the device, and the
# historical path uninstalls the game (which deletes Saves). Refuse unless the operator
# asked for the legacy behaviour explicitly.
if (-not $LegacyDestructiveInstall) {
    Write-Host ""
    Write-Host "DANGER: the rest of this script uninstalls com.chucklefish.stardewvalley,"
    Write-Host "        which deletes the player's Saves, and installs an APK signed with a"
    Write-Host "        certificate that does not match the official one."
    Write-Host ""
    Write-Host "Nothing was done to the device."
    Write-Host "Use the non-destructive gate instead:"
    Write-Host "  pwsh -NoProfile -File Tools/deploy-apk-safe.ps1"
    Write-Host "  pwsh -NoProfile -File Tools/deploy-apk-safe.ps1 -InstallApproved   # after approval"
    Write-Host ""
    Write-Host "To reproduce the historical behaviour anyway, re-run with -LegacyDestructiveInstall."
    exit 2
}

# 3.5 pick a device explicitly. Auto-select only when exactly one is attached: this
# script uninstalls the game, so silently targeting a guessed endpoint is unacceptable.
if (-not $Serial) {
    $online = @()
    foreach ($line in (& $adb devices)) {
        if ($line -match '^(\S+)\s+device\b') { $online += $Matches[1] }
    }
    if ($online.Count -ne 1) {
        throw ("expected exactly one attached device, found {0}: {1}. Pass -Serial <serial>." -f $online.Count, ($online -join ", "))
    }
    $Serial = $online[0]
    Write-Host "device     : $Serial (auto-selected, single attached device)"
} else {
    Write-Host "device     : $Serial"
}

# 4. install without incremental streaming, then PROVE which APK is installed
# uninstall -> install has a race in this workspace (one Phase 0 run failed here); retry once.
& $adb -s $serial uninstall com.chucklefish.stardewvalley 2>&1 | Out-Null
& $adb -s $serial install --no-incremental $apk 2>&1 | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) {
    Write-Host "install failed once, retrying after 3s"
    Start-Sleep -Seconds 3
    & $adb -s $serial install --no-incremental $apk 2>&1 | Select-Object -Last 2
}
if ($LASTEXITCODE -ne 0) { throw "install failed" }

$installedPath = (& $adb -s $serial shell "pm path com.chucklefish.stardewvalley") -replace "package:", "" -replace "`r", ""
$installedPath = $installedPath.Trim()
$deviceHash = (& $adb -s $serial shell "su -c 'sha256sum $installedPath'") -split "\s+" | Select-Object -First 1
Write-Host "device apk : $deviceHash"
if ($deviceHash -ne $localApkHash) {
    throw "INSTALL MISMATCH: device APK hash does not match the build we just produced"
}
Write-Host "verdict    : installed APK == freshly built APK (proven by sha256)"

# 5. optionally stage the runtime + probe into the app's Mods dir (owned by the app uid)
if ($PushMods) {
    $mods = "/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods"
    $bin = Join-Path $phase "artifacts\bin"
    $uid = ((& $adb -s $serial shell "su -c 'stat -c %u /data/data/com.chucklefish.stardewvalley'") -replace "`r", "").Trim()
    $owner = "${uid}:${uid}"
    $files = @(
        @{ src = (Join-Path $bin "ModFramework.Runtime\release\ModFramework.Runtime.dll"); name = "ModFramework.Runtime.dll" },
        @{ src = (Join-Path $bin "MonoRuntimeProbe\release\MonoRuntimeProbe.dll"); name = "MonoRuntimeProbe.dll" },
        @{ src = (Join-Path $bin "MonoRuntimeProbe\release\MonoRuntimeProbe.Dependency.dll"); name = "MonoRuntimeProbe.Dependency.dll" }
    )
    foreach ($f in $files) {
        & $adb -s $serial push $f.src ("/data/local/tmp/" + $f.name) 2>&1 | Out-Null
    }
    $cp = ($files | ForEach-Object { "/data/local/tmp/" + $_.name }) -join " "
    & $adb -s $serial shell "su -c 'mkdir -p $mods && cp $cp $mods/ && chown $owner $mods/*.dll && chmod 0770 $mods && chmod 0660 $mods/*.dll'"
    & $adb -s $serial shell "su -c 'ls -l $mods'"
}

# 6. launch and leave evidence gathering to the caller
& $adb -s $serial logcat -c
& $adb -s $serial shell "am start -W -n com.chucklefish.stardewvalley/com.chucklefish.stardewvalley.MainActivity" |
    Select-Object -First 3
