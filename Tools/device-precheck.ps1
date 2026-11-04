# E0 read-only device precheck for the Stardew Valley mod framework.
# ASCII only: this workspace has been corrupted by PowerShell round-trips of
# non-ASCII files before (see AGENTS.md 10.3, item 7).
#
# Read-only by construction: getprop / pm path / dumpsys / ls / sha256sum only.
# It never installs, uninstalls, force-stops, clears data, or writes to the device.
#
# Usage:
#   pwsh -NoProfile -File Tools/device-precheck.ps1
#   pwsh -NoProfile -File Tools/device-precheck.ps1 -Serial 127.0.0.1:16384
#   pwsh -NoProfile -File Tools/device-precheck.ps1 -OutFile .tmp/phase0.5/logs/e0.txt
param(
    [string]$Serial = "",
    [string]$Adb = "",
    [string]$Package = "com.chucklefish.stardewvalley",
    [string]$OutFile = "",
    [string[]]$HashFiles = @()
)

$ErrorActionPreference = "Continue"
$lines = New-Object System.Collections.Generic.List[string]

function Say([string]$text) {
    $lines.Add($text)
    Write-Host $text
}

function Adb-Prop([string]$serial, [string]$prop) {
    $v = & $script:AdbBin -s $serial shell "getprop $prop" 2>$null
    if ($null -eq $v) { return "" }
    return (($v -join " ") -replace "`r", "").Trim()
}

if (-not $Adb) {
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($cmd) { $Adb = $cmd.Source }
}
if (-not $Adb) {
    $candidates = @()
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA "Android\Sdk\platform-tools\adb.exe") }
    if ($env:ANDROID_HOME) { $candidates += (Join-Path $env:ANDROID_HOME "platform-tools\adb.exe") }
    if ($env:ANDROID_SDK_ROOT) { $candidates += (Join-Path $env:ANDROID_SDK_ROOT "platform-tools\adb.exe") }
    $candidates += "C:\Users\Kaori Izuki\AppData\Local\Android\Sdk\platform-tools\adb.exe"
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { $Adb = $c; break }
    }
}
if (-not $Adb -or -not (Test-Path $Adb)) {
    Say "verdict=BLOCKED reason=no-adb no adb client found"
    exit 2
}
$script:AdbBin = $Adb
Say "adb=$Adb"

Say "`n== 1. attached devices =="
$devices = & $Adb devices -l 2>&1
$devices | ForEach-Object { Say ("  " + $_) }

$serialList = @()
foreach ($line in $devices) {
    if ($line -match '^(\S+)\s+device\b') { $serialList += $Matches[1] }
}
if ($serialList.Count -eq 0) {
    Say "`nverdict=BLOCKED reason=no-device"
    exit 3
}
if (-not $Serial) { $Serial = $serialList[0] }
Say "`nselected serial: $Serial"

Say "`n== 2. ABI / build identity (read-only) =="
$props = [ordered]@{
    "ro.product.cpu.abilist" = ""
    "ro.product.cpu.abi" = ""
    "ro.build.version.sdk" = ""
    "ro.build.version.release" = ""
    "ro.build.version.security_patch" = ""
    "ro.product.manufacturer" = ""
    "ro.product.model" = ""
    "ro.product.device" = ""
    "ro.build.fingerprint" = ""
    "ro.build.characteristics" = ""
    "ro.dalvik.vm.native.bridge" = ""
    "ro.kernel.qemu" = ""
    "ro.hardware" = ""
}
foreach ($k in @($props.Keys)) {
    $props[$k] = Adb-Prop $Serial $k
    Say ("  {0,-30} = {1}" -f $k, $props[$k])
}

$abiPrimary = $props["ro.product.cpu.abi"]
$abiList = $props["ro.product.cpu.abilist"]
$bridge = $props["ro.dalvik.vm.native.bridge"]
$isArm64Device = ($abiPrimary -eq "arm64-v8a")
$isTranslated = ($bridge -ne "" -and $bridge -ne "0")
$looksEmulator = ($abiPrimary -eq "x86_64" -or $props["ro.kernel.qemu"] -eq "1")

if ($isArm64Device -and -not $isTranslated -and -not $looksEmulator) {
    Say "  device-class                  = ARM64-PHYSICAL (candidate)"
} else {
    Say "  device-class                  = NOT-ARM64-PHYSICAL (emulator/translated/x86_64)"
}
Say "  abi-primary=$abiPrimary abilist=$abiList native-bridge=$bridge"

Say "`n== 3. root (do not assume) =="
$whoami = & $Adb -s $Serial shell "id" 2>&1
Say ("  adb shell id      : " + (($whoami -join " ") -replace "`r", "").Trim())
$suId = & $Adb -s $Serial shell "su -c id" 2>&1
$suText = (($suId -join " ") -replace "`r", "").Trim()
Say ("  su -c id          : " + $(if ($suText -match "uid=0") { "uid=0 (available)" } else { "unavailable -> $suText" }))
$hasSu = ($suText -match "uid=0")

Say "`n== 4. target package (read-only) =="
$pkgPath = & $Adb -s $Serial shell "pm path $Package" 2>&1
$pkgPathText = (($pkgPath -join " ") -replace "`r", "").Trim()
Say ("  pm path           : " + $pkgPathText)
$installed = $pkgPathText -match "package:"
if ($installed) {
    $dumpsys = & $Adb -s $Serial shell "dumpsys package $Package" 2>&1
    foreach ($pat in @("versionCode", "versionName", "firstInstallTime", "lastUpdateTime", "primaryCpuAbi")) {
        $hit = $dumpsys | Where-Object { $_ -match "$pat=" } | Select-Object -First 1
        if ($hit) { Say ("  " + ($hit -replace "`r", "").Trim()) }
    }

    $apkPath = ($pkgPathText -split "\s+" | Select-Object -First 1) -replace "package:", ""
    if ($hasSu -and $apkPath) {
        $devHash = & $Adb -s $Serial shell "su -c 'sha256sum $apkPath'" 2>&1
        Say ("  installed apk sha : " + ((($devHash -join " ") -replace "`r", "").Trim()))
    } else {
        Say "  installed apk sha : UNKNOWN (needs root; do not assume root)"
    }

    Say "`n== 5. mod/framework leftovers in the app's private dir (read-only) =="
    if ($hasSu) {
        $ls = & $Adb -s $Serial shell "su -c 'ls -la /data/data/$Package/files' " 2>&1
        $ls | ForEach-Object { Say ("  " + ($_ -replace "`r", "")) }
        $mods = & $Adb -s $Serial shell "su -c 'ls -la /sdcard/Android/data/$Package/files/Mods'" 2>&1
        $mods | ForEach-Object { Say ("  mods: " + ($_ -replace "`r", "")) }
        $log = & $Adb -s $Serial shell "su -c 'cat /data/data/$Package/files/Mods/loader.log'" 2>&1
        Say ("  loader.log (Mods) : " + ((($log -join " ") -replace "`r", "").Trim()))
    } else {
        Say "  skipped: no root"
    }
} else {
    Say "  package not installed"
}

Say "`n== 6. optional local artifact hashes =="
if ($HashFiles.Count -eq 0) {
    Say "  (none requested; pass -HashFiles a.apk,b.dll)"
} else {
    foreach ($f in $HashFiles) {
        if (Test-Path $f) {
            $h = (Get-FileHash -Path $f -Algorithm SHA256).Hash.ToLower()
            $size = (Get-Item $f).Length
            Say ("  {0}  {1}  {2}" -f $h, $size, $f)
        } else {
            Say ("  MISSING " + $f)
        }
    }
}

Say "`n== 7. verdict =="
if (-not $installed) {
    Say "  E1/E2: BLOCKED reason=package-not-installed"
} elseif (-not ($isArm64Device -and -not $isTranslated -and -not $looksEmulator)) {
    Say "  E0: PASS (read-only)"
    Say "  E1/E2: BLOCKED reason=no-arm64-physical-device (this target is emulator/translated/x86_64)"
} else {
    Say "  E0: PASS (read-only)"
    Say "  E1/E2: READY-TO-RUN (requires explicit approval before install/replace)"
}

$text = ($lines -join "`r`n")
if ($OutFile) {
    $dir = Split-Path -Parent $OutFile
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Set-Content -Path $OutFile -Value $text -Encoding utf8
    Write-Host "`nwritten: $OutFile"
}
exit 0
