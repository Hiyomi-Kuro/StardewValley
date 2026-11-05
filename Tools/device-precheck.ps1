# E0 read-only device precheck for the Stardew Valley mod framework.
# ASCII only: this workspace has been corrupted by PowerShell round-trips of
# non-ASCII files before (see AGENTS.md 10.3, item 7).
#
# Read-only by construction: getprop / pm path / dumpsys / ls / sha256sum only.
# It never installs, uninstalls, force-stops, clears data, or writes to the device.
#
# adb discovery (no hardcoded user directory, ever):
#   1. -Adb <path>            explicit override (validated, wins over everything)
#   2. PATH                   Get-Command adb
#   3. ANDROID_HOME           $ANDROID_HOME/platform-tools/adb[.exe]
#   4. ANDROID_SDK_ROOT       $ANDROID_SDK_ROOT/platform-tools/adb[.exe]
#   5. LOCALAPPDATA           %LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe (Windows)
#   6. ADB_PATH               explicit environment override for unusual layouts
# When several candidates exist the highest-priority one is used and the conflict
# is printed, so "which adb did I measure with" is never ambiguous.
#
# Usage:
#   pwsh -NoProfile -File Tools/device-precheck.ps1
#   pwsh -NoProfile -File Tools/device-precheck.ps1 -Serial 127.0.0.1:16384
#   pwsh -NoProfile -File Tools/device-precheck.ps1 -OutFile .tmp/phase0.5/logs/e0.txt
#   pwsh -NoProfile -File Tools/device-precheck.ps1 -ResolveAdbOnly   # no device needed
param(
    [string]$Serial = "",
    [string]$Adb = "",
    [string]$Package = "com.chucklefish.stardewvalley",
    [string]$OutFile = "",
    [string[]]$HashFiles = @(),
    [switch]$ResolveAdbOnly
)

$ErrorActionPreference = "Continue"
$lines = New-Object System.Collections.Generic.List[string]

function Say([string]$text) {
    $lines.Add($text)
    Write-Host $text
}

# Replace the operator's home directory with a stable placeholder so that pasted
# output never leaks a user name. Only the home prefix is rewritten; nothing else
# about the path is changed.
function Redact-Path([string]$text) {
    if (-not $text) { return $text }
    $roots = @()
    if ($env:USERPROFILE) { $roots += $env:USERPROFILE }
    if ($env:HOME) { $roots += $env:HOME }
    if ($env:LOCALAPPDATA) { $roots += (Split-Path -Parent (Split-Path -Parent $env:LOCALAPPDATA)) }
    foreach ($root in $roots) {
        if ($root -and $root.Trim().Length -gt 3) {
            $text = $text -replace [regex]::Escape($root.TrimEnd('\', '/')), '<home>'
        }
    }
    # Any remaining Windows profile path (e.g. a stale log line) is redacted too.
    $text = $text -replace '(?i)[A-Z]:\\Users\\[^\\\s"]+', '<home>'
    return $text
}

function Test-WindowsHost {
    # Windows PowerShell 5.1 has no $IsWindows; $env:OS is set on Windows in both hosts.
    return [bool]($env:OS -eq "Windows_NT")
}

function Get-AdbFileName {
    if (Test-WindowsHost) { return "adb.exe" }
    return "adb"
}

# Ordered candidate list: @{ Path = <full path>; Source = <where it came from> }.
# Nothing here guesses a user name; every candidate comes from an env var, PATH or
# an explicit argument.
function Get-AdbCandidates([string]$Explicit) {
    $exe = Get-AdbFileName
    $list = New-Object System.Collections.Generic.List[object]

    if ($Explicit) {
        $list.Add([pscustomobject]@{ Path = $Explicit; Source = "argument" })
        return $list
    }

    $onPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($onPath -and $onPath.Source) {
        $list.Add([pscustomobject]@{ Path = $onPath.Source; Source = "PATH" })
    }

    foreach ($pair in @(@{ Var = "ANDROID_HOME"; Name = "ANDROID_HOME" },
                        @{ Var = "ANDROID_SDK_ROOT"; Name = "ANDROID_SDK_ROOT" })) {
        $root = [Environment]::GetEnvironmentVariable($pair.Var)
        if ($root) {
            $list.Add([pscustomobject]@{
                Path   = (Join-Path (Join-Path $root "platform-tools") $exe)
                Source = $pair.Name
            })
        }
    }

    if ($env:LOCALAPPDATA) {
        $list.Add([pscustomobject]@{
            Path   = (Join-Path (Join-Path (Join-Path $env:LOCALAPPDATA "Android") "Sdk") (Join-Path "platform-tools" $exe))
            Source = "LOCALAPPDATA"
        })
    }

    if ($env:ADB_PATH) {
        $list.Add([pscustomobject]@{ Path = $env:ADB_PATH; Source = "ADB_PATH" })
    }

    return $list
}

# Picks the first candidate that exists. Returns @{ Ok; Path; Source; All; Conflict }.
function Resolve-Adb([string]$Explicit) {
    $all = Get-AdbCandidates $Explicit
    $existing = @($all | Where-Object { $_.Path -and (Test-Path -LiteralPath $_.Path -PathType Leaf) })
    $chosen = $existing | Select-Object -First 1
    $conflict = $existing.Count -gt 1
    $chosenPath = ""
    $chosenSource = "none"
    if ($chosen) {
        $chosenPath = $chosen.Path
        $chosenSource = $chosen.Source
    }
    return [pscustomobject]@{
        Ok       = [bool]$chosen
        Path     = $chosenPath
        Source   = $chosenSource
        All      = $all
        Existing = $existing
        Conflict = $conflict
    }
}

function Report-AdbResolution($res) {
    Say "adb-resolution:"
    if ($res.All.Count -eq 0) {
        Say "  candidates        : (none: adb not on PATH and no Android SDK env var is set)"
    }
    foreach ($c in $res.All) {
        $mark = if ($c.Path -and (Test-Path -LiteralPath $c.Path -PathType Leaf)) { "found" } else { "missing" }
        Say ("  {0,-14} {1,-7} {2}" -f $c.Source, $mark, (Redact-Path $c.Path))
    }
    if (-not $res.Ok) {
        Say "  verdict           : BLOCKED reason=no-adb"
        Say "  fix               : put platform-tools on PATH, or set ANDROID_HOME /"
        Say "                      ANDROID_SDK_ROOT to an Android SDK root, or pass -Adb <path>."
        return
    }
    Say ("  selected          : {0} ({1})" -f (Redact-Path $res.Path), $res.Source)
    if ($res.Conflict) {
        Say ("  adb-conflict      : {0} candidates exist; the highest-priority one was used." -f $res.Existing.Count)
        foreach ($c in $res.Existing) {
            Say ("                      {0,-14} {1}" -f $c.Source, (Redact-Path $c.Path))
        }
    }
}

function Adb-Prop([string]$serial, [string]$prop) {
    # A cold adb server (or a busy emulator) can return nothing for the first few
    # calls. Retry a couple of times so an empty value means "property is empty",
    # not "adb was still starting".
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $v = & $script:AdbBin -s $serial shell "getprop $prop" 2>$null
        if ($null -ne $v) {
            $text = (($v -join " ") -replace "`r", "").Trim()
            if ($text -ne "" -or $attempt -eq 2) { return $text }
        }
        Start-Sleep -Milliseconds 250
    }
    return ""
}

$resolved = Resolve-Adb $Adb

if ($ResolveAdbOnly) {
    Say "device-precheck: adb resolution self-test (no device is contacted)"
    Report-AdbResolution $resolved
    $text = ($lines -join "`r`n")
    if ($OutFile) {
        $dir = Split-Path -Parent $OutFile
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        Set-Content -Path $OutFile -Value $text -Encoding utf8
    }
    if (-not $resolved.Ok) { exit 2 }
    exit 0
}

if (-not $resolved.Ok) {
    Report-AdbResolution $resolved
    Say "verdict=BLOCKED reason=no-adb no adb client found"
    exit 2
}
$script:AdbBin = $resolved.Path
Say ("adb=" + (Redact-Path $resolved.Path))
Say ("adb-source=" + $resolved.Source)
if ($resolved.Conflict) {
    Say ("adb-conflict=" + (($resolved.Existing | ForEach-Object { $_.Source }) -join ","))
}

Say "`n== 1. attached devices =="
$devices = & $resolved.Path devices -l 2>&1
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
$whoami = & $resolved.Path -s $Serial shell "id" 2>&1
Say ("  adb shell id      : " + (($whoami -join " ") -replace "`r", "").Trim())
$suId = & $resolved.Path -s $Serial shell "su -c id" 2>&1
$suText = (($suId -join " ") -replace "`r", "").Trim()
Say ("  su -c id          : " + $(if ($suText -match "uid=0") { "uid=0 (available)" } else { "unavailable -> $suText" }))
$hasSu = ($suText -match "uid=0")

Say "`n== 4. target package (read-only) =="
$pkgPath = & $resolved.Path -s $Serial shell "pm path $Package" 2>&1
$pkgPathText = (($pkgPath -join " ") -replace "`r", "").Trim()
Say ("  pm path           : " + $pkgPathText)
$installed = $pkgPathText -match "package:"
if ($installed) {
    $dumpsys = & $resolved.Path -s $Serial shell "dumpsys package $Package" 2>&1
    foreach ($pat in @("versionCode", "versionName", "firstInstallTime", "lastUpdateTime", "primaryCpuAbi")) {
        $hit = $dumpsys | Where-Object { $_ -match "$pat=" } | Select-Object -First 1
        if ($hit) { Say ("  " + ($hit -replace "`r", "").Trim()) }
    }

    $apkPath = ($pkgPathText -split "\s+" | Select-Object -First 1) -replace "package:", ""
    if ($hasSu -and $apkPath) {
        $devHash = & $resolved.Path -s $Serial shell "su -c 'sha256sum $apkPath'" 2>&1
        Say ("  installed apk sha : " + ((($devHash -join " ") -replace "`r", "").Trim()))
    } else {
        Say "  installed apk sha : UNKNOWN (needs root; do not assume root)"
    }

    Say "`n== 5. mod/framework leftovers in the app's private dir (read-only) =="
    if ($hasSu) {
        $ls = & $resolved.Path -s $Serial shell "su -c 'ls -la /data/data/$Package/files' " 2>&1
        $ls | ForEach-Object { Say ("  " + ($_ -replace "`r", "")) }
        $mods = & $resolved.Path -s $Serial shell "su -c 'ls -la /sdcard/Android/data/$Package/files/Mods'" 2>&1
        $mods | ForEach-Object { Say ("  mods: " + ($_ -replace "`r", "")) }
        $log = & $resolved.Path -s $Serial shell "su -c 'cat /data/data/$Package/files/Mods/loader.log'" 2>&1
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
            Say ("  {0}  {1}  {2}" -f $h, $size, (Redact-Path $f))
        } else {
            Say ("  MISSING " + (Redact-Path $f))
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
    Write-Host "`nwritten: $(Redact-Path $OutFile)"
}
exit 0
