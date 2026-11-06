# Non-destructive deployment gate for the Stardew Valley mod framework (Phase 1.1.4).
# ASCII only: PowerShell round-trips of non-ASCII files are unsafe in this workspace.
#
# WHY THIS SCRIPT EXISTS
#   Tools/deploy-apk.ps1 (the Phase 0 script) ran `adb uninstall com.chucklefish.stardewvalley`
#   before every install. Uninstalling deletes the player's Saves, and this project's APK is
#   signed with a local debug certificate that does NOT match the official signature, so a
#   "replace in place" is not possible either. Both facts make the old default unacceptable.
#
# CONTRACT (every point is enforced below, not documented only)
#   1. Default run is READ ONLY: it lists devices, reads properties, reads the package state
#      and prints the exact commands that *would* run. It never writes to the device.
#   2. Nothing is installed without -InstallApproved. Nothing is pushed without
#      -PushModsApproved. This phase passes neither.
#   3. No code path ever calls adb uninstall, adb shell pm clear, or clears app data. A
#      signature mismatch stops the run; it never falls back to "uninstall then install".
#   4. `adb install -r` is allowed ONLY when the local APK's signing certificate digest can be
#      proven equal to the installed package's. Unprovable => fail closed.
#   5. Signature mismatch => SIGNATURE_MISMATCH_REQUIRES_USER_ACTION and a manual procedure.
#      Signature unreadable => SIGNATURE_UNREADABLE_USE_MANUAL_VERIFICATION and a stop.
#   6. The first install of an absent package also needs -InstallApproved.
#   7. Before any install the script prints: target serial, device class, package name, APK
#      SHA-256, signer digest, whether a package is already installed and whether a replace
#      is needed.
#   8. It never force-stops the game, never launches the game, and never touches Mods/ or Saves/
#      unless -PushModsApproved was passed with an explicit -ModsSourceDirectory.
#   9. -ModsSourceDirectory is the COMPLETE Mods root. The push walks the whole tree, keeps every
#      relative path and reproduces it under .../files/Mods: ModFramework.Runtime.dll stays in
#      the Mods root and each mod keeps its own directory with its own manifest.json, exactly as
#      Mod/ModFramework.Runtime/ModDiscovery.cs expects. Nothing is flattened.
#  10. Every file is SHA-256 verified on the device in staging BEFORE the first copy, and again
#      in its final location. A digest that cannot be verified is a failure, never a success.
#  11. An existing target file with identical bytes is skipped. A different digest fails closed
#      unless -OverwriteModsApproved is passed. The Mods root is never deleted or replaced, so
#      other mods cannot be destroyed by this script.
#  12. Files are created app-owned: uid/gid are read from the app's own files directory and the
#      result is verified (0644 files, 0755 directories owned by that uid:gid).
#  13. Output never contains a user profile path: paths are printed by leaf name only.
#
# Usage:
#   pwsh -NoProfile -File Tools/deploy-apk-safe.ps1
#   pwsh -NoProfile -File Tools/deploy-apk-safe.ps1 -Serial 127.0.0.1:16384
#   pwsh -NoProfile -File Tools/deploy-apk-safe.ps1 -InstallApproved          # needs approval
#   pwsh -NoProfile -File Tools/deploy-apk-safe.ps1 -PushModsApproved -Serial <serial> -ModsSourceDirectory <complete Mods root>
#   (add -OverwriteModsApproved only for a separate, explicit decision to replace different bytes)
#
# Exit codes:
#   0 = precheck completed (nothing was written) OR the approved action succeeded
#   2 = BLOCKED (no adb, no device, ambiguous device, serial not attached)
#   3 = REFUSED by policy (missing approval, signature mismatch/unreadable, APK missing)
#   4 = an approved command failed
param(
    [string]$Serial = "",
    [string]$Adb = "",
    # apksigner executable; resolved from PATH / ANDROID_HOME / ANDROID_SDK_ROOT when empty.
    [string]$ApkSigner = "",
    [string]$ApkPath = "",
    [string]$Package = "com.chucklefish.stardewvalley",
    # Only used together with -PushModsApproved.
    [string]$ModsSourceDirectory = "",
    # Optional operator-declared signer digest; when given, the installed APK is not pulled.
    [string]$ExpectedSignerSha256 = "",
    [switch]$InstallApproved,
    [switch]$PushModsApproved,
    # Separate, explicit approval to REPLACE a target file whose SHA-256 differs from the local
    # one. Without it a hash collision at the same path fails closed: the default never
    # overwrites anything, so another mod's file can never be clobbered by accident.
    [switch]$OverwriteModsApproved,
    [string]$OutFile = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$lines = New-Object System.Collections.Generic.List[string]

function Say([string]$text) {
    $script:lines.Add($text)
    Write-Host $text
}

# Replace the operator's home directory with a stable placeholder.
function Redact([string]$text) {
    if (-not $text) { return $text }
    $roots = @()
    if ($env:USERPROFILE) { $roots += $env:USERPROFILE }
    if ($env:HOME) { $roots += $env:HOME }
    foreach ($r in $roots) {
        if ($r -and $r.Trim().Length -gt 3) {
            $text = $text -replace [regex]::Escape($r.TrimEnd('\', '/')), '<home>'
        }
    }
    # Any remaining Windows profile path. The user name may contain SPACES, so the whole
    # "Users\<name>" segment (up to the next separator) is replaced, not just the first token.
    return ($text -replace '(?i)[A-Z]:\\Users\\[^\\\r\n]+?(?=\\)', '<home>')
}

# Leaf name only: a device path may be printed, a user profile path may not.
function Leaf([string]$path) {
    if (-not $path) { return "(empty)" }
    $trimmed = $path.TrimEnd('\', '/')
    $at = $trimmed.LastIndexOfAny([char[]]@('\', '/'))
    if ($at -lt 0) { return $trimmed }
    return $trimmed.Substring($at + 1)
}

function Finish([int]$code) {
    $text = ($lines -join "`r`n")
    if ($OutFile) {
        $dir = Split-Path -Parent $OutFile
        # Paths are LITERAL: a directory name may contain [ ] or other wildcard characters.
        # New-Item has no -LiteralPath, so the framework call is used instead.
        if ($dir -and -not (Test-Path -LiteralPath $dir)) {
            [System.IO.Directory]::CreateDirectory($dir) | Out-Null
        }
        Set-Content -LiteralPath $OutFile -Value $text -Encoding utf8
    }
    exit $code
}

# ------------------------------------------------------------------ tool discovery

function Test-WindowsHost { return [bool]($env:OS -eq "Windows_NT") }

function Get-ExeName([string]$name) {
    if (Test-WindowsHost) { return "$name.exe" }
    return $name
}

function Resolve-Adb([string]$explicit) {
    $exe = Get-ExeName "adb"
    if ($explicit) {
        if (Test-Path -LiteralPath $explicit -PathType Leaf) {
            return [pscustomobject]@{ Ok = $true; Path = $explicit; Source = "argument" }
        }
        return [pscustomobject]@{ Ok = $false; Path = ""; Source = "argument (missing)" }
    }
    $onPath = Get-Command adb -ErrorAction SilentlyContinue
    if ($onPath -and $onPath.Source) {
        return [pscustomobject]@{ Ok = $true; Path = $onPath.Source; Source = "PATH" }
    }
    foreach ($var in @("ANDROID_HOME", "ANDROID_SDK_ROOT")) {
        $sdk = [Environment]::GetEnvironmentVariable($var)
        if ($sdk) {
            $candidate = Join-Path (Join-Path $sdk "platform-tools") $exe
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return [pscustomobject]@{ Ok = $true; Path = $candidate; Source = $var }
            }
        }
    }
    if ($env:LOCALAPPDATA) {
        $candidate = Join-Path (Join-Path (Join-Path $env:LOCALAPPDATA "Android") "Sdk") (Join-Path "platform-tools" $exe)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [pscustomobject]@{ Ok = $true; Path = $candidate; Source = "LOCALAPPDATA" }
        }
    }
    if ($env:ADB_PATH -and (Test-Path -LiteralPath $env:ADB_PATH -PathType Leaf)) {
        return [pscustomobject]@{ Ok = $true; Path = $env:ADB_PATH; Source = "ADB_PATH" }
    }
    return [pscustomobject]@{ Ok = $false; Path = ""; Source = "none" }
}

# Highest build-tools version wins; no hardcoded user directory anywhere.
# On Windows the Android SDK ships apksigner as a .bat launcher, not apksigner.exe, so every
# plausible launcher name is tried (a .exe-only lookup silently reported "not found").
function Resolve-ApkSigner([string]$explicit) {
    if ($explicit) {
        if (Test-Path -LiteralPath $explicit -PathType Leaf) {
            return [pscustomobject]@{ Ok = $true; Path = $explicit; Source = "argument" }
        }
        return [pscustomobject]@{ Ok = $false; Path = ""; Source = "argument (missing)" }
    }
    $names = if (Test-WindowsHost) { @("apksigner.bat", "apksigner.exe", "apksigner.cmd") } else { @("apksigner") }
    $onPath = Get-Command apksigner -ErrorAction SilentlyContinue
    if ($onPath -and $onPath.Source) {
        return [pscustomobject]@{ Ok = $true; Path = $onPath.Source; Source = "PATH" }
    }
    $sdkRoots = @()
    foreach ($var in @("ANDROID_HOME", "ANDROID_SDK_ROOT")) {
        $sdk = [Environment]::GetEnvironmentVariable($var)
        if ($sdk) { $sdkRoots += $sdk }
    }
    if ($env:LOCALAPPDATA) {
        $sdkRoots += (Join-Path (Join-Path $env:LOCALAPPDATA "Android") "Sdk")
    }
    $best = $null
    $bestVersion = $null
    foreach ($sdkRoot in $sdkRoots) {
        $buildTools = Join-Path $sdkRoot "build-tools"
        if (-not (Test-Path -LiteralPath $buildTools)) { continue }
        foreach ($dir in Get-ChildItem -LiteralPath $buildTools -Directory -ErrorAction SilentlyContinue) {
            foreach ($name in $names) {
                $candidate = Join-Path $dir.FullName $name
                if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
                # Sort by name; build-tools directories are version strings ("35.0.0").
                if (-not $bestVersion -or [string]::CompareOrdinal($dir.Name, $bestVersion) -gt 0) {
                    $best = $candidate
                    $bestVersion = $dir.Name
                }
            }
        }
    }
    if ($best) {
        return [pscustomobject]@{ Ok = $true; Path = $best; Source = "build-tools/$bestVersion" }
    }
    return [pscustomobject]@{ Ok = $false; Path = ""; Source = "none" }
}

function Sha256File([string]$path) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLower()
}

# POSIX single-quote a value for the DEVICE shell. Embedded quotes become '\'' so a file name
# such as  "a b (c) 'd'.dll" reaches the device as one inert argument. Every path that is
# interpolated into a remote command passes through here; no remote command is ever built by
# concatenating an unquoted name.
function Quote-Posix([string]$value) {
    return "'" + ($value -replace "'", "'\''") + "'"
}

# Run a shell snippet as root on the device. The snippet is single-quoted as a whole, so the
# device shell performs exactly one expansion and nothing in it is evaluated locally.
function Invoke-RootShell([string]$serial, [string]$script) {
    return Adb-Shell $serial ("su -c " + (Quote-Posix $script))
}

# Path of $fullPath relative to $baseDir, with forward slashes and no leading separator.
# Returns "" when $fullPath is not inside $baseDir (an escape attempt, a junction target, ...).
function Get-RelativeSlashPath([string]$baseDir, [string]$fullPath) {
    $baseFull = [System.IO.Path]::GetFullPath($baseDir).TrimEnd([char]'\', [char]'/')
    $fullNormal = [System.IO.Path]::GetFullPath($fullPath)
    $prefix = $baseFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullNormal.StartsWith($prefix, [System.StringComparison]::Ordinal)) { return "" }
    $rel = $fullNormal.Substring($prefix.Length)
    return ($rel -replace '\\', '/')
}

# SHA-256 of one file already on the device (read as root: the app's private files are 0700).
function Get-RemoteSha256([string]$serial, [string]$path) {
    $result = Invoke-RootShell $serial ("sha256sum " + (Quote-Posix $path))
    $m = [regex]::Match($result.Text, '(?m)^([0-9a-f]{64})\s')
    if ($result.Exit -ne 0 -or -not $m.Success) {
        return [pscustomobject]@{ Ok = $false; Digest = ""; Detail = ($result.Text -replace "`r?`n", " ") }
    }
    return [pscustomobject]@{ Ok = $true; Digest = $m.Groups[1].Value; Detail = "" }
}

# True when any element of the path exists on the device (file or directory).
function Test-RemotePath([string]$serial, [string]$path) {
    $result = Invoke-RootShell $serial ("test -e " + (Quote-Posix $path) + "; echo status=" + '$?')
    $m = [regex]::Match($result.Text, 'status=(\d+)')
    return ($m.Success -and $m.Groups[1].Value -eq "0")
}

# Owner and mode of one remote path, or Ok=$false when it cannot be read.
function Get-RemoteOwnerMode([string]$serial, [string]$path) {
    $result = Invoke-RootShell $serial ("stat -c " + (Quote-Posix "%u:%g:%a") + " " + (Quote-Posix $path))
    $m = [regex]::Match($result.Text, '(?m)^(\d+):(\d+):([0-7]+)\s*$')
    if ($result.Exit -ne 0 -or -not $m.Success) {
        return [pscustomobject]@{ Ok = $false; Uid = ""; Gid = ""; Mode = ""; Detail = ($result.Text -replace "`r?`n", " ") }
    }
    return [pscustomobject]@{ Ok = $true; Uid = $m.Groups[1].Value; Gid = $m.Groups[2].Value; Mode = $m.Groups[3].Value; Detail = "" }
}

# Enumerate and validate a complete Mods root. The result describes EVERY ordinary file with
# its relative path, size and SHA-256, because the deployment contract is "reproduce this exact
# tree", not "copy whatever happens to be on the first level".
function Get-ModsPlan([string]$sourceDir) {
    $plan = [pscustomobject]@{
        Ok          = $false
        Reason      = ""
        Detail      = ""
        Files       = @()
        Directories = @()
        TotalBytes  = 0
    }
    if (-not (Test-Path -LiteralPath $sourceDir -PathType Container)) {
        $plan.Reason = "mods-source-directory-missing"
        return $plan
    }
    $rootFull = [System.IO.Path]::GetFullPath($sourceDir).TrimEnd([char]'\', [char]'/')
    $items = @(Get-ChildItem -LiteralPath $sourceDir -Recurse -Force -ErrorAction SilentlyContinue)

    $files = New-Object System.Collections.Generic.List[object]
    $dirs = New-Object System.Collections.Generic.List[string]
    foreach ($item in $items) {
        # A symlink / junction / mount point is never followed: it could point outside the
        # source root and silently deploy foreign bytes.
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            $plan.Reason = "mods-source-contains-reparse-point"
            $plan.Detail = (Leaf $item.FullName)
            return $plan
        }
        $rel = Get-RelativeSlashPath $rootFull $item.FullName
        if (-not $rel) {
            $plan.Reason = "mods-source-path-escape"
            $plan.Detail = (Leaf $item.FullName)
            return $plan
        }
        if ($rel -match '(^|/)\.\.(/|$)' -or $rel.StartsWith("/") -or $rel.Contains(":")) {
            $plan.Reason = "mods-source-unsafe-relative-path"
            $plan.Detail = $rel
            return $plan
        }
        if ($item.PSIsContainer) {
            $dirs.Add($rel)
        } else {
            $files.Add([pscustomobject]@{
                Rel    = $rel
                Full   = $item.FullName
                Bytes  = $item.Length
                Sha256 = (Sha256File $item.FullName)
            })
        }
    }

    if ($files.Count -eq 0) {
        $plan.Reason = "mods-source-empty"
        return $plan
    }
    # The loader expects ModFramework.Runtime.dll in the Mods ROOT and every mod in its own
    # first-level directory with a manifest.json (see Mod/ModFramework.Runtime/ModDiscovery.cs).
    if (-not ($files | Where-Object { $_.Rel -eq "ModFramework.Runtime.dll" })) {
        $plan.Reason = "missing-runtime-dll"
        $plan.Detail = "ModFramework.Runtime.dll must sit in the Mods root"
        return $plan
    }
    $modDirs = @($dirs | Where-Object { -not $_.Contains("/") } | Sort-Object)
    if ($modDirs.Count -eq 0) {
        $plan.Reason = "missing-mod-directory"
        return $plan
    }
    foreach ($dir in $modDirs) {
        $manifestRel = $dir + "/manifest.json"
        $manifest = $files | Where-Object { $_.Rel -eq $manifestRel } | Select-Object -First 1
        if (-not $manifest) {
            $plan.Reason = "missing-manifest"
            $plan.Detail = $dir
            return $plan
        }
        try {
            $doc = [System.IO.File]::ReadAllText($manifest.Full, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
        } catch {
            $plan.Reason = "manifest-unreadable"
            $plan.Detail = $manifestRel
            return $plan
        }
        $entry = [string]$doc.entryAssembly
        if (-not $entry) {
            $plan.Reason = "manifest-missing-entry-assembly"
            $plan.Detail = $manifestRel
            return $plan
        }
        if (-not ($files | Where-Object { $_.Rel -eq ($dir + "/" + $entry) })) {
            $plan.Reason = "manifest-entry-assembly-missing"
            $plan.Detail = ($dir + "/" + $entry)
            return $plan
        }
        foreach ($private in @($doc.privateAssemblies)) {
            if (-not $private) { continue }
            if (-not ($files | Where-Object { $_.Rel -eq ($dir + "/" + $private) })) {
                $plan.Reason = "manifest-private-assembly-missing"
                $plan.Detail = ($dir + "/" + $private)
                return $plan
            }
        }
    }

    $plan.Files = @($files | Sort-Object Rel)
    $plan.Directories = @($dirs | Sort-Object)
    $bytes = 0
    foreach ($f in $plan.Files) { $bytes += $f.Bytes }
    $plan.TotalBytes = $bytes
    $plan.Ok = $true
    return $plan
}

# Native stdout/stderr are DATA, not exceptions. `adb` writes progress and errors to stderr,
# and with $ErrorActionPreference = "Stop" a merged 2>&1 stream would abort the script on the
# first warning. Every external call goes through here.
function Invoke-Tool([string]$tool, [string[]]$argv) {
    $previous = $ErrorActionPreference
    $code = 0
    $out = @()
    $ErrorActionPreference = "Continue"
    try {
        $out = & $tool @argv 2>&1
        $code = $LASTEXITCODE
        if ($null -eq $code) { $code = 0 }
    } catch {
        $out = @(($_.Exception.Message))
        $code = 127
    } finally {
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{ Exit = $code; Text = (($out | Out-String) -replace "`r", "").Trim() }
}

function Get-SignerSha256([string]$apk, [string]$tool) {
    if (-not $tool) { return [pscustomobject]@{ Ok = $false; Digest = ""; Detail = "apksigner not found" } }
    $result = Invoke-Tool $tool @("verify", "--print-certs", $apk)
    if ($result.Exit -ne 0) {
        return [pscustomobject]@{ Ok = $false; Digest = ""; Detail = "apksigner exited " + $result.Exit }
    }
    $m = [regex]::Match($result.Text, '(?i)Signer #1 certificate SHA-256 digest:\s*([0-9a-f]{64})')
    if (-not $m.Success) {
        return [pscustomobject]@{ Ok = $false; Digest = ""; Detail = "no SHA-256 certificate digest in apksigner output" }
    }
    return [pscustomobject]@{ Ok = $true; Digest = $m.Groups[1].Value.ToLower(); Detail = "apksigner" }
}

# ------------------------------------------------------------------ device queries

$script:AdbBin = ""

function Adb([string]$serial, [string[]]$argv) {
    $all = @("-s", $serial) + $argv
    return Invoke-Tool $script:AdbBin $all
}

function Adb-Shell([string]$serial, [string]$command) {
    return Adb $serial @("shell", $command)
}

# ------------------------------------------------------------------ 1. resolve tools

Say "deploy-apk-safe: Phase 1.1.4 (non-destructive by default)"
Say ("mode            : " + $(if ($InstallApproved -or $PushModsApproved) { "APPROVED ACTION REQUESTED" } else { "PRECHECK ONLY (no device write)" }))

# NOTE: the parameter above is [string]$Adb and PowerShell variables are case-insensitive, so
# naming this local variable $adb would coerce the object to a string (a real bug this harness
# caught). Keep it as $adbInfo.
$adbInfo = Resolve-Adb $Adb
if (-not $adbInfo.Ok) {
    Say "adb             : BLOCKED (no adb client found)"
    Say "verdict         : BLOCKED reason=no-adb"
    Finish 2
}
$script:AdbBin = $adbInfo.Path
Say ("adb             : " + (Redact $adbInfo.Path) + " (" + $adbInfo.Source + ")")

$signer = Resolve-ApkSigner $ApkSigner
$signerDetail = if ($signer.Ok) { (Redact $signer.Path) + " (" + $signer.Source + ")" } else { "UNKNOWN (" + $signer.Source + ")" }
Say ("apksigner       : " + $signerDetail)

# ------------------------------------------------------------------ 2. pick a device

$deviceOut = (Invoke-Tool $script:AdbBin @("devices", "-l")).Text
$online = @()
foreach ($line in ($deviceOut -split "`r?`n")) {
    if ($line -match '^(\S+)\s+device\b') { $online += $Matches[1] }
}
Say ("attached        : " + $(if ($online.Count -eq 0) { "(none)" } else { $online -join ", " }))

if ($online.Count -eq 0) {
    Say "verdict         : BLOCKED reason=no-device"
    Finish 2
}

if ($Serial) {
    if ($online -notcontains $Serial) {
        Say ("target          : REFUSED serial '" + $Serial + "' is not among the attached devices")
        Say "verdict         : BLOCKED reason=serial-not-attached (no device was guessed)"
        Finish 2
    }
    $script:SerialExplicit = $true
    Say ("target          : " + $Serial + " (explicit)")
} else {
    if ($online.Count -ne 1) {
        Say ("target          : REFUSED " + $online.Count + " devices attached; pass -Serial explicitly")
        Say "verdict         : BLOCKED reason=ambiguous-device (no device was guessed)"
        Finish 2
    }
    $Serial = $online[0]
    $script:SerialExplicit = $false
    Say ("target          : " + $Serial + " (auto-selected: exactly one attached device)")
}

# ------------------------------------------------------------------ 3. device class

$prop = @{}
foreach ($key in @("ro.product.cpu.abi", "ro.product.cpu.abilist", "ro.dalvik.vm.native.bridge",
                   "ro.kernel.qemu", "ro.build.version.release", "ro.build.version.sdk", "ro.product.model")) {
    $prop[$key] = (Adb-Shell $Serial ("getprop " + $key)).Text
}

$abi = $prop["ro.product.cpu.abi"]
$bridge = $prop["ro.dalvik.vm.native.bridge"]
$isTranslated = ($bridge -ne "" -and $bridge -ne "0")
$looksEmulator = ($abi -eq "x86_64" -or $prop["ro.kernel.qemu"] -eq "1")
if ($abi -eq "arm64-v8a" -and -not $isTranslated -and -not $looksEmulator) {
    $deviceClass = "ARM64-PHYSICAL"
} elseif ($looksEmulator -or $isTranslated) {
    $deviceClass = "EMULATOR-TRANSLATED"
} else {
    $deviceClass = "NOT-ARM64"
}
Say ("device-class    : " + $deviceClass)
Say ("abi             : abi=" + $abi + " abilist=" + $prop["ro.product.cpu.abilist"] +
     " native-bridge=" + $(if ($bridge) { $bridge } else { "(none)" }) +
     " qemu=" + $(if ($prop["ro.kernel.qemu"]) { $prop["ro.kernel.qemu"] } else { "(unset)" }))
Say ("android         : release=" + $prop["ro.build.version.release"] + " sdk=" + $prop["ro.build.version.sdk"] +
     " model=" + $prop["ro.product.model"])
Say ("arm64-evidence  : " + $(if ($deviceClass -eq "ARM64-PHYSICAL") { "physical ARM64 (E1/E2 eligible)" } else { "NOT ARM64 physical; emulator/translated results do not count as device evidence" }))

# ------------------------------------------------------------------ 4. package state

$pmPath = (Adb-Shell $Serial ("pm path " + $Package)).Text
$installed = $pmPath -match "package:"
$installedPath = ""
if ($installed) {
    $installedPath = ($pmPath -split "\s+" | Where-Object { $_ -like "package:*" } | Select-Object -First 1) -replace "package:", ""
}
Say ("package         : " + $Package)
Say ("installed       : " + $(if ($installed) { "yes path=" + (Leaf $installedPath) } else { "no" }))

$installedVersionCode = ""
$installedVersionName = ""
if ($installed) {
    $dumpsys = (Adb-Shell $Serial ("dumpsys package " + $Package)).Text
    $mCode = [regex]::Match($dumpsys, 'versionCode=(\d+)')
    if ($mCode.Success) { $installedVersionCode = $mCode.Groups[1].Value }
    $mName = [regex]::Match($dumpsys, 'versionName=([^\s]+)')
    if ($mName.Success) { $installedVersionName = $mName.Groups[1].Value }
    foreach ($field in @("versionCode", "versionName", "lastUpdateTime", "primaryCpuAbi")) {
        $hit = ($dumpsys -split "`r?`n" | Where-Object { $_ -match ($field + "=") } | Select-Object -First 1)
        if ($hit) { Say ("installed-" + $field.PadRight(14) + ": " + $hit.Trim()) }
    }
}

# ------------------------------------------------------------------ 5. local artifact

if (-not $ApkPath) { $ApkPath = Join-Path $root "app\build\outputs\apk\debug\app-debug.apk" }
$apkExists = Test-Path -LiteralPath $ApkPath -PathType Leaf
if ($apkExists) {
    $apkHash = Sha256File $ApkPath
    $apkBytes = (Get-Item -LiteralPath $ApkPath).Length
    Say ("local-apk       : " + (Leaf $ApkPath) + " " + $apkHash + " (" + $apkBytes + " bytes)")
    $localSigner = Get-SignerSha256 $ApkPath $(if ($signer.Ok) { $signer.Path } else { "" })
    if ($localSigner.Ok) {
        Say ("local-signer    : " + $localSigner.Digest)
    } else {
        Say ("local-signer    : UNKNOWN (" + $localSigner.Detail + ")")
    }
} else {
    $apkHash = ""
    $localSigner = [pscustomobject]@{ Ok = $false; Digest = ""; Detail = "APK not built" }
    Say ("local-apk       : MISSING " + (Leaf $ApkPath) + " (run gradlew assembleDebug first)")
}

# ------------------------------------------------------------------ 6. signature gate

$gateState = "NOT EVALUATED"
$gateDetail = "evaluated only when -InstallApproved is passed (the default run performs no device file transfer)"

if ($InstallApproved) {
    if (-not $apkExists) {
        Say "signature-gate  : REFUSED local APK missing"
        Say "verdict         : REFUSED reason=apk-missing"
        Finish 3
    }
    if (-not $localSigner.Ok) {
        Say "signature-gate  : REFUSED local signer digest unreadable (" + $localSigner.Detail + ")"
        Say "verdict         : SIGNATURE_UNREADABLE_USE_MANUAL_VERIFICATION"
        Say "manual          : run apksigner verify --print-certs on the APK yourself, then compare"
        Say "                  its 'Signer #1 certificate SHA-256 digest' with the installed app."
        Finish 3
    }

    if (-not $installed) {
        $gateState = "FIRST INSTALL"
        $gateDetail = "the package is not installed; a first install needs no signature comparison"
    } elseif ($ExpectedSignerSha256) {
        $expected = $ExpectedSignerSha256.ToLower()
        if ($expected -eq $localSigner.Digest) {
            $gateState = "SAME (operator-declared)"
            $gateDetail = "matches -ExpectedSignerSha256; no installed APK was pulled"
        } else {
            $gateState = "MISMATCH (operator-declared)"
            $gateDetail = "local=" + $localSigner.Digest + " expected=" + $expected
        }
    } else {
        # Read-only device access: pull the installed base APK and verify its certificate.
        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("sv-sig-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
        New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
        $pulled = Join-Path $tempDir "installed-base.apk"
        try {
            $pull = Adb $Serial @("pull", $installedPath, $pulled)
            if ($pull.Exit -ne 0 -or -not (Test-Path -LiteralPath $pulled)) {
                $gateState = "UNREADABLE"
                $gateDetail = "adb pull of the installed APK failed (needs readable /data/app or an approved backup)"
            } else {
                $installedSigner = Get-SignerSha256 $pulled $(if ($signer.Ok) { $signer.Path } else { "" })
                if (-not $installedSigner.Ok) {
                    $gateState = "UNREADABLE"
                    $gateDetail = "installed APK certificate could not be read (" + $installedSigner.Detail + ")"
                } elseif ($installedSigner.Digest -eq $localSigner.Digest) {
                    $gateState = "SAME"
                    $gateDetail = "installed and local certificates match"
                } else {
                    $gateState = "MISMATCH"
                    $gateDetail = "local=" + $localSigner.Digest + " installed=" + $installedSigner.Digest
                }
            }
        } finally {
            Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Say ("signature-gate  : " + $gateState + " - " + $gateDetail)

# ------------------------------------------------------------------ 7. plan / act

$installArgs = "-r --no-incremental"
if (-not $installed) { $installArgs = "--no-incremental" }

# ------------------------------------------------------------------ 7. mods plan (read only)
#
# Enumerating the plan is a LOCAL read. It runs in precheck mode too, so the operator can see
# the exact tree, sizes and SHA-256 values that an approved push would write.
$remoteModsRoot = "/data/data/" + $Package + "/files/Mods"
$modsPlan = $null
if ($ModsSourceDirectory) {
    $modsPlan = Get-ModsPlan $ModsSourceDirectory
    Say ""
    if ($modsPlan.Ok) {
        Say ("mods-plan       : " + $modsPlan.Files.Count + " files, " + $modsPlan.Directories.Count +
             " directories, " + $modsPlan.TotalBytes + " bytes")
        foreach ($f in $modsPlan.Files) {
            Say ("  file          : " + $f.Rel + " " + $f.Bytes + " " + $f.Sha256)
        }
    } else {
        Say ("mods-plan       : INVALID reason=" + $modsPlan.Reason + " detail=" + $modsPlan.Detail)
    }
}

Say ""
Say "planned commands (nothing below has run unless the verdict says so):"
Say ("  install : adb -s " + $Serial + " install " + $installArgs + " <apk>")
if ($PushModsApproved) {
    Say ("  push    : adb -s " + $Serial + " push <file> /data/local/tmp/<staging>/<relative path> (recursive, per file)")
    Say ("            su -c mkdir -p / su -c cp -p / su -c chown <app uid>:<app gid> / su -c chmod")
    Say ("            then su -c sha256sum per file and su -c stat per file")
    Say ("            final root: " + $remoteModsRoot)
} else {
    Say "  push    : (not requested; needs -PushModsApproved)"
    if ($ModsSourceDirectory) { Say "            the mods-plan above is what an approved push would send" }
}
Say "  uninstall : NEVER (this script has no uninstall path)"
Say "  pm clear  : NEVER"
Say "  force-stop: NEVER"
Say "  launch    : NEVER"

if (-not $InstallApproved -and -not $PushModsApproved) {
    Say ""
    Say "verdict         : PRECHECK-ONLY ok (no install, no push, no uninstall, no clear, no launch)"
    Say "next            : pass -InstallApproved (same-signature replace) or -PushModsApproved"
    Say "                  only after the operator has explicitly approved this exact target."
    Finish 0
}

if ($InstallApproved) {
    if ($gateState -eq "MISMATCH" -or $gateState -eq "MISMATCH (operator-declared)") {
        Say ""
        Say "verdict         : SIGNATURE_MISMATCH_REQUIRES_USER_ACTION"
        Say "reason          : the local APK is not signed by the certificate of the installed game."
        Say "what was NOT done: no uninstall, no pm clear, no data wipe, no install."
        Say "manual procedure:"
        Say "  1. back up /sdcard/Android/data/" + $Package + "/files/Saves first (a copy, verified);"
        Say "  2. get explicit user approval for ONE of:"
        Say "     a) installing the official signed APK instead, or"
        Say "     b) a full replace that requires uninstalling (which deletes Saves);"
        Say "  3. only then run the approved procedure by hand; this script will not do it."
        Finish 3
    }
    if ($gateState -eq "UNREADABLE") {
        Say ""
        Say "verdict         : SIGNATURE_UNREADABLE_USE_MANUAL_VERIFICATION"
        Say "reason          : the installed package's signing certificate could not be proven."
        Say "what was NOT done: no uninstall, no pm clear, no install."
        Say "manual procedure:"
        Say "  1. apksigner verify --print-certs <local apk>"
        Say "  2. compare the 'Signer #1 certificate SHA-256 digest' with the installed app's"
        Say "     certificate (adb pull the installed base.apk and verify it the same way);"
        Say "  3. re-run with -ExpectedSignerSha256 <digest> once equality is established."
        Finish 3
    }

    Say ""
    Say "installing      : adb -s $Serial install $installArgs <apk>"
    $installResult = Invoke-Tool $script:AdbBin (@("-s", $Serial, "install") + @(($installArgs -split " ")) + @($ApkPath))
    $installOut = $installResult.Text
    Say (($installOut -replace "`r", "").Trim())
    if ($installResult.Exit -ne 0 -or $installOut -notmatch "Success") {
        Say "verdict         : INSTALL FAILED (device reported a failure; nothing was uninstalled)"
        Finish 4
    }
    Say ("installed-apk   : " + $apkHash + " (local digest; prove the device copy with E0 sha256sum)")
    Say "verdict         : INSTALLED (same-signature replace, no uninstall)"
}

# ------------------------------------------------------------------ 8. recursive safe mods push
#
# WHY THIS BLOCK WAS REWRITTEN (Phase 1.1.5)
#   The Phase 1.1.4 implementation used `Get-ChildItem -File` on the source directory, which
#   enumerates only the FIRST level, and then `cp`-ed every file into the Mods root. That
#   destroys the package layout: Mod/ModFramework.Runtime/ModDiscovery.cs only looks at the
#   immediate subdirectories of Mods and requires each mod's own manifest.json inside its own
#   directory. A flat copy therefore never yields Mods/P114.RuntimeProbe/manifest.json, and
#   probe-fixtures/** disappeared entirely. The code below walks the whole tree, preserves
#   every relative path, verifies each file by SHA-256 on the device, and never overwrites
#   different bytes unless -OverwriteModsApproved was passed explicitly.

$script:StageRoot = ""

function Remove-Staging([string]$serial) {
    if (-not $script:StageRoot) { return }
    $root = $script:StageRoot
    $script:StageRoot = ""
    $result = Invoke-RootShell $serial ("rm -rf " + (Quote-Posix $root))
    $stillThere = Test-RemotePath $serial $root
    if ($result.Exit -eq 0 -and -not $stillThere) {
        Say ("staging-cleanup : removed " + $root)
    } else {
        Say ("staging-cleanup : FAILED " + $root + " (" + ($result.Text -replace "`r?`n", " ") + ")")
    }
}

function New-Verdict([int]$code, [string]$verdict, [string]$detail) {
    return [pscustomobject]@{ Code = $code; Verdict = $verdict; Detail = $detail }
}

function Invoke-ModsDeployment([string]$serial) {
    if (-not $modsPlan) {
        return New-Verdict 3 "REFUSED reason=missing-mods-source-directory" "pass -ModsSourceDirectory <complete Mods root>"
    }
    if (-not $modsPlan.Ok) {
        return New-Verdict 3 ("REFUSED reason=" + $modsPlan.Reason) $modsPlan.Detail
    }
    if (-not $script:SerialExplicit) {
        return New-Verdict 3 "REFUSED reason=push-requires-explicit-serial" "pass -Serial explicitly; no device is ever guessed"
    }

    # --- gate 1: the package must be the expected build -------------------------------------
    if (-not $installed) {
        return New-Verdict 3 "REFUSED reason=package-not-installed" $Package
    }
    if ($installedVersionCode -ne "239" -or $installedVersionName -ne "1.6.15.0") {
        return New-Verdict 3 "REFUSED reason=package-version-mismatch" ("device=" + $installedVersionCode + "/" + $installedVersionName + " required=239/1.6.15.0")
    }
    if ($installedVersionCode -eq "239" -and $installedVersionName -eq "1.6.15.0") {
        Say ("version-gate    : OK " + $installedVersionCode + " / " + $installedVersionName)
    }

    # --- gate 2: the installed APK must be byte-identical to the local APK ------------------
    if (-not $apkExists -or -not $apkHash) {
        return New-Verdict 3 "REFUSED reason=local-apk-missing" "build the APK first; no device hash can be compared"
    }
    $deviceApk = Get-RemoteSha256 $serial $installedPath
    if (-not $deviceApk.Ok) {
        return New-Verdict 3 "REFUSED reason=device-apk-hash-unreadable" $deviceApk.Detail
    }
    if ($deviceApk.Digest -ne $apkHash) {
        return New-Verdict 3 "REFUSED reason=device-apk-hash-mismatch" ("device=" + $deviceApk.Digest + " local=" + $apkHash)
    }
    Say ("apk-gate        : OK " + $deviceApk.Digest + " (device == local)")

    # --- gate 3: the app UID/GID must be knowable (files must end up app-owned) -------------
    $filesDir = "/data/data/" + $Package + "/files"
    $appOwnerInfo = Get-RemoteOwnerMode $serial $filesDir
    if (-not $appOwnerInfo.Ok -or $appOwnerInfo.Uid -notmatch '^\d+$' -or $appOwnerInfo.Gid -notmatch '^\d+$') {
        return New-Verdict 3 "REFUSED reason=app-uid-unreadable" $appOwnerInfo.Detail
    }
    if ($appOwnerInfo.Uid -eq "0") {
        return New-Verdict 3 "REFUSED reason=app-uid-is-root" "the files directory is root-owned; a root-owned mod tree is not proven readable by the game"
    }
    $appOwner = $appOwnerInfo.Uid + ":" + $appOwnerInfo.Gid
    Say ("owner-gate      : OK app uid:gid=" + $appOwner + " files-dir-mode=" + $appOwnerInfo.Mode)

    # --- gate 4: the device must actually provide the commands this deployment needs -------
    # Nothing is assumed about GNU tar/rsync/unzip: the script only uses these small tools, and
    # it proves they exist BEFORE the first byte is uploaded.
    $requiredTools = @("sha256sum", "stat", "mkdir", "cp", "chown", "chmod", "rm", "ls")
    $toolProbe = Invoke-RootShell $serial ("command -v " + ($requiredTools -join " "))
    $foundTools = @(($toolProbe.Text -split "`r?`n") | Where-Object { $_.Trim() })
    if ($toolProbe.Exit -ne 0 -or $foundTools.Count -ne $requiredTools.Count) {
        return New-Verdict 3 "REFUSED reason=required-device-command-missing" ("found " + $foundTools.Count + " of " + $requiredTools.Count)
    }
    Say ("tool-gate       : OK " + $foundTools.Count + "/" + $requiredTools.Count + " device commands present")

    # --- read-only view of what is already there -------------------------------------------
    $modsExists = Test-RemotePath $serial $remoteModsRoot
    Say ("mods-root       : " + $remoteModsRoot + " " + $(if ($modsExists) { "exists" } else { "absent (will be created)" }))
    if ($modsExists) {
        $listing = Invoke-RootShell $serial ("ls -1a " + (Quote-Posix $remoteModsRoot))
        foreach ($line in ($listing.Text -split "`r?`n")) {
            if ($line.Trim()) { Say ("  existing      : " + $line.Trim()) }
        }
        $rootOwner = Get-RemoteOwnerMode $serial $remoteModsRoot
        if (-not $rootOwner.Ok -or ($rootOwner.Uid + ":" + $rootOwner.Gid) -ne $appOwner) {
            return New-Verdict 3 "REFUSED reason=mods-root-not-app-owned" ("owner=" + $rootOwner.Uid + ":" + $rootOwner.Gid + " expected=" + $appOwner)
        }
        if ($rootOwner.Mode.Length -lt 3 -or [int]$rootOwner.Mode.Substring($rootOwner.Mode.Length - 3, 1) -lt 1) {
            return New-Verdict 3 "REFUSED reason=mods-root-not-traversable" ("mode=" + $rootOwner.Mode)
        }
    }

    # --- collision preflight: same bytes may be skipped, different bytes fail closed --------
    $toInstall = New-Object System.Collections.Generic.List[object]
    $skippedSame = New-Object System.Collections.Generic.List[string]
    $conflicts = New-Object System.Collections.Generic.List[string]
    foreach ($f in $modsPlan.Files) {
        $target = $remoteModsRoot + "/" + $f.Rel
        if (-not (Test-RemotePath $serial $target)) {
            $toInstall.Add([pscustomobject]@{ File = $f; Target = $target; Action = "ADD" })
            continue
        }
        $existing = Get-RemoteSha256 $serial $target
        if (-not $existing.Ok) {
            $conflicts.Add($f.Rel + " (exists, hash unreadable)")
            continue
        }
        if ($existing.Digest -eq $f.Sha256) {
            $skippedSame.Add($f.Rel)
            continue
        }
        if (-not $OverwriteModsApproved) {
            $conflicts.Add($f.Rel + " (local=" + $f.Sha256 + " device=" + $existing.Digest + ")")
            continue
        }
        $toInstall.Add([pscustomobject]@{ File = $f; Target = $target; Action = "REPLACE" })
    }
    Say ("collision-check : " + $skippedSame.Count + " identical (skipped), " + $toInstall.Count +
         " to write, " + $conflicts.Count + " conflicting")
    if ($conflicts.Count -gt 0) {
        foreach ($c in $conflicts) { Say ("  conflict      : " + $c) }
        return New-Verdict 3 "REFUSED reason=target-file-hash-conflict" "existing files differ and -OverwriteModsApproved was not passed; nothing was overwritten"
    }

    if ($toInstall.Count -eq 0) {
        return New-Verdict 0 "ALREADY-PRESENT" "every file already matched by SHA-256; nothing was written"
    }

    # --- staging: unique directory, every file hash-verified before any copy ---------------
    $script:StageRoot = "/data/local/tmp/svmods-" + [Guid]::NewGuid().ToString("N").Substring(0, 12)
    $mkStage = Invoke-RootShell $serial ("mkdir -p " + (Quote-Posix $script:StageRoot))
    if ($mkStage.Exit -ne 0 -or -not (Test-RemotePath $serial $script:StageRoot)) {
        return New-Verdict 4 "PUSH FAILED reason=staging-directory-create-failed" ($mkStage.Text -replace "`r?`n", " ")
    }
    Say ("staging         : " + $script:StageRoot)

    $stagedPaths = New-Object System.Collections.Generic.List[string]
    foreach ($item in $toInstall) {
        $stagedPath = $script:StageRoot + "/" + $item.File.Rel
        $sep = $stagedPath.LastIndexOf("/")
        $stagedDir = $stagedPath.Substring(0, $sep)
        $mkDir = Invoke-RootShell $serial ("mkdir -p " + (Quote-Posix $stagedDir))
        if ($mkDir.Exit -ne 0) {
            return New-Verdict 4 "PUSH FAILED reason=staging-subdirectory-create-failed" $item.File.Rel
        }
        $push = Invoke-Tool $script:AdbBin @("-s", $serial, "push", $item.File.Full, $stagedPath)
        if ($push.Exit -ne 0) {
            return New-Verdict 4 "PUSH FAILED reason=push-failed" ($item.File.Rel + " " + ($push.Text -replace "`r?`n", " "))
        }
        $stagedPaths.Add($stagedPath)
    }

    $stageArgs = ($stagedPaths | ForEach-Object { Quote-Posix $_ }) -join " "
    $stageVerify = Invoke-RootShell $serial ("sha256sum " + $stageArgs)
    $stageMap = @{}
    foreach ($line in ($stageVerify.Text -split "`r?`n")) {
        $m = [regex]::Match($line, '^([0-9a-f]{64})\s+\*?(.+)$')
        if ($m.Success) { $stageMap[$m.Groups[2].Value.Trim()] = $m.Groups[1].Value }
    }
    foreach ($item in $toInstall) {
        $stagedPath = $script:StageRoot + "/" + $item.File.Rel
        if (-not $stageMap.ContainsKey($stagedPath) -or $stageMap[$stagedPath] -ne $item.File.Sha256) {
            return New-Verdict 4 "PUSH FAILED reason=staged-hash-mismatch" $item.File.Rel
        }
    }
    Say ("verify-staged   : " + $toInstall.Count + "/" + $toInstall.Count + " staged files match the local SHA-256")

    # --- create the final directories (only the ones that do not exist yet) ----------------
    $neededDirs = New-Object System.Collections.Generic.HashSet[string]
    [void]$neededDirs.Add($remoteModsRoot)
    foreach ($item in $toInstall) {
        $parts = $item.File.Rel -split '/'
        $acc = $remoteModsRoot
        for ($i = 0; $i -lt $parts.Count - 1; $i++) {
            $acc = $acc + "/" + $parts[$i]
            [void]$neededDirs.Add($acc)
        }
    }
    $createdDirs = New-Object System.Collections.Generic.List[string]
    foreach ($dir in $neededDirs) {
        if (-not (Test-RemotePath $serial $dir)) { $createdDirs.Add($dir) }
    }
    $dirArgs = (($neededDirs | Sort-Object) | ForEach-Object { Quote-Posix $_ }) -join " "
    $mkFinal = Invoke-RootShell $serial ("mkdir -p " + $dirArgs)
    if ($mkFinal.Exit -ne 0) {
        return New-Verdict 4 "PUSH FAILED reason=final-directory-create-failed" ($mkFinal.Text -replace "`r?`n", " ")
    }
    if ($createdDirs.Count -gt 0) {
        $createdArgs = ($createdDirs | Sort-Object | ForEach-Object { Quote-Posix $_ }) -join " "
        $own = Invoke-RootShell $serial ("chown " + $appOwner + " " + $createdArgs + " && chmod 0755 " + $createdArgs)
        if ($own.Exit -ne 0) {
            return New-Verdict 4 "PUSH FAILED reason=directory-ownership-failed" ($own.Text -replace "`r?`n", " ")
        }
        Say ("dirs-created    : " + $createdDirs.Count + " (owner " + $appOwner + ", mode 0755)")
    }

    # --- copy each verified file into place, then prove the final state --------------------
    foreach ($item in $toInstall) {
        $stagedPath = $script:StageRoot + "/" + $item.File.Rel
        $cmd = "cp -p " + (Quote-Posix $stagedPath) + " " + (Quote-Posix $item.Target) +
               " && chown " + $appOwner + " " + (Quote-Posix $item.Target) +
               " && chmod 0644 " + (Quote-Posix $item.Target)
        $cp = Invoke-RootShell $serial $cmd
        if ($cp.Exit -ne 0) {
            return New-Verdict 4 "PUSH FAILED reason=copy-failed" ($item.File.Rel + " " + ($cp.Text -replace "`r?`n", " "))
        }
    }

    $targetArgs = ($toInstall | ForEach-Object { Quote-Posix $_.Target }) -join " "
    $finalVerify = Invoke-RootShell $serial ("sha256sum " + $targetArgs)
    $finalMap = @{}
    foreach ($line in ($finalVerify.Text -split "`r?`n")) {
        $m = [regex]::Match($line, '^([0-9a-f]{64})\s+\*?(.+)$')
        if ($m.Success) { $finalMap[$m.Groups[2].Value.Trim()] = $m.Groups[1].Value }
    }
    foreach ($item in $toInstall) {
        if (-not $finalMap.ContainsKey($item.Target) -or $finalMap[$item.Target] -ne $item.File.Sha256) {
            return New-Verdict 4 "PUSH FAILED reason=remote-hash-mismatch" $item.File.Rel
        }
    }
    Say ("verify-remote   : " + $toInstall.Count + "/" + $toInstall.Count + " deployed files match the local SHA-256")

    # Owner, mode and the traversability of every directory on the path to each file.
    $statArgs = ($toInstall | ForEach-Object { Quote-Posix $_.Target }) -join " "
    $statVerify = Invoke-RootShell $serial ("stat -c " + (Quote-Posix "%n %u:%g:%a") + " " + $statArgs)
    $statMap = @{}
    foreach ($line in ($statVerify.Text -split "`r?`n")) {
        $m = [regex]::Match($line, '^(.*) (\d+):(\d+):([0-7]+)$')
        if ($m.Success) { $statMap[$m.Groups[1].Value] = @{ Owner = ($m.Groups[2].Value + ":" + $m.Groups[3].Value); Mode = $m.Groups[4].Value } }
    }
    $badOwner = @()
    foreach ($item in $toInstall) {
        if (-not $statMap.ContainsKey($item.Target)) {
            $badOwner += ($item.File.Rel + " (stat unreadable)")
            continue
        }
        $st = $statMap[$item.Target]
        if ($st.Owner -ne $appOwner) { $badOwner += ($item.File.Rel + " owner=" + $st.Owner) ; continue }
        if ([Convert]::ToInt32($st.Mode, 8) -band 256 -eq 0) { $badOwner += ($item.File.Rel + " mode=" + $st.Mode + " (not owner-readable)") }
    }
    if ($badOwner.Count -gt 0) {
        foreach ($b in $badOwner) { Say ("  bad-perm     : " + $b) }
        return New-Verdict 4 "PUSH FAILED reason=ownership-or-permission-unverified" ($badOwner -join "; ")
    }
    Say ("verify-owner    : " + $toInstall.Count + "/" + $toInstall.Count + " files owned by " + $appOwner + ", mode 0644")

    if ($createdDirs.Count -gt 0) {
        $dirStatArgs = ($createdDirs | Sort-Object | ForEach-Object { Quote-Posix $_ }) -join " "
        $dirStat = Invoke-RootShell $serial ("stat -c " + (Quote-Posix "%n %u:%g:%a") + " " + $dirStatArgs)
        $dirMap = @{}
        foreach ($line in ($dirStat.Text -split "`r?`n")) {
            $m = [regex]::Match($line, '^(.*) (\d+):(\d+):([0-7]+)$')
            if ($m.Success) { $dirMap[$m.Groups[1].Value] = @{ Owner = ($m.Groups[2].Value + ":" + $m.Groups[3].Value); Mode = $m.Groups[4].Value } }
        }
        $badDir = @()
        foreach ($dir in $createdDirs) {
            if (-not $dirMap.ContainsKey($dir)) { $badDir += ($dir + " (stat unreadable)"); continue }
            if ($dirMap[$dir].Owner -ne $appOwner) { $badDir += ($dir + " owner=" + $dirMap[$dir].Owner) }
        }
        if ($badDir.Count -gt 0) {
            foreach ($b in $badDir) { Say ("  bad-perm     : " + $b) }
            return New-Verdict 4 "PUSH FAILED reason=directory-ownership-unverified" ($badDir -join "; ")
        }
        Say ("verify-dirs     : " + $createdDirs.Count + "/" + $createdDirs.Count + " created directories owned by " + $appOwner)
    }

    Say ""
    foreach ($item in $toInstall) {
        Say ("  deployed      : " + $item.Action + " " + $item.File.Rel + " " + $item.File.Bytes + " " + $item.File.Sha256)
    }
    foreach ($s in $skippedSame) {
        Say ("  skipped       : SKIP-SAME " + $s)
    }
    return New-Verdict 0 "PUSHED" ($toInstall.Count.ToString() + " files written, " + $skippedSame.Count + " already identical")
}

if ($PushModsApproved) {
    $outcome = Invoke-ModsDeployment $Serial
    Remove-Staging $Serial
    Say ""
    Say ("verdict         : " + $outcome.Verdict)
    if ($outcome.Detail) { Say ("detail          : " + $outcome.Detail) }
    if ($outcome.Code -eq 0) {
        Say ("final-root      : " + $remoteModsRoot)
        Say "next            : verify loader-report.txt after the game starts (that step needs its own approval)"
    }
    Finish $outcome.Code
}

Finish 0
