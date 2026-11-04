# Deploy one injected APK and prove which build is actually running.
# ASCII only: PowerShell round-trips of non-ASCII files are unsafe in this workspace.
#
# Usage:
#   pwsh -File Tools/deploy-apk.ps1 -Mode full -HookMethod OnCreate
#   pwsh -File Tools/deploy-apk.ps1 -Mode append -HookMethod OnCreate -Restore
param(
    [ValidateSet("full", "append")][string]$Mode = "full",
    [string]$HookMethod = "OnCreate",
    # Do not register the catch handler in Bootstrap.Start(): failures surface as a crash.
    [switch]$NoCatch,
    # Push ModFramework.Runtime.dll + probe into the app's Mods dir before launching.
    [switch]$PushMods,
    # Restore the repository blob only (no build/install); used to leave the tree clean.
    [switch]$Restore,
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"

$root      = Split-Path -Parent $PSScriptRoot
$adb       = Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe"
$serial    = "127.0.0.1:16384"
$dotnet    = Join-Path $root ".tmp\phase0\dotnet\dotnet.exe"
$python    = "C:\Users\Kaori Izuki\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe"
$blob      = Join-Path $root "app\src\main\apkroot\assemblies\assemblies.blob"
$manifest  = Join-Path $root "app\src\main\apkroot\assemblies\assemblies.manifest"
$original  = Join-Path $root ".tmp\phase0\out\assemblies.original.blob"
$apk       = Join-Path $root "app\build\outputs\apk\debug\app-debug.apk"
$env:JAVA_HOME = "C:\Program Files\Eclipse Adoptium\jdk-11.0.32.101-hotspot"
$env:DOTNET_NOLOGO = "1"

function Sha256([string]$path) {
    (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLower()
}

if ($Restore) {
    Copy-Item $original $blob -Force
    Write-Host "restored repo blob -> $(Sha256 $blob)"
    exit 0
}

if (-not (Test-Path $original)) { throw "missing original blob backup: $original" }

# 1. patch
$injector = Join-Path $root ".tmp\phase0\artifacts\bin\ModInjector\release\ModInjector.dll"
& $dotnet build (Join-Path $root "Tools\ModInjector\ModInjector.csproj") -c Release `
    --artifacts-path (Join-Path $root ".tmp\phase0\artifacts") -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ModInjector build failed" }

$patched = Join-Path $root ".tmp\phase0\out\sv.$Mode.dll"
$patchArgs = @("patch",
    (Join-Path $root ".tmp\phase0\extract\blob000\StardewValley.dll"), $patched,
    "--mode", $Mode, "--hook-method", $HookMethod)
if ($NoCatch) { $patchArgs += "--no-catch" }
& $dotnet $injector @patchArgs
if ($LASTEXITCODE -ne 0) { throw "patch failed (see self-check above)" }

# 2. rebuild blob
$newBlob = Join-Path $root ".tmp\phase0\out\assemblies.$Mode.blob"
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

# 4. install without incremental streaming, then PROVE which APK is installed
& $adb -s $serial uninstall com.chucklefish.stardewvalley 2>&1 | Out-Null
& $adb -s $serial install --no-incremental $apk 2>&1 | Select-Object -Last 2
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
    $bin = Join-Path $root ".tmp\phase0\artifacts\bin"
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
