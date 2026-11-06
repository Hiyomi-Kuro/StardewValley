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
#   9. Output never contains a user profile path: paths are printed by leaf name only.
#
# Usage:
#   pwsh -NoProfile -File Tools/deploy-apk-safe.ps1
#   pwsh -NoProfile -File Tools/deploy-apk-safe.ps1 -Serial 127.0.0.1:16384
#   pwsh -NoProfile -File Tools/deploy-apk-safe.ps1 -InstallApproved          # needs approval
#   pwsh -NoProfile -File Tools/deploy-apk-safe.ps1 -PushModsApproved -ModsSourceDirectory <dir>
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
    Say ("target          : " + $Serial + " (explicit)")
} else {
    if ($online.Count -ne 1) {
        Say ("target          : REFUSED " + $online.Count + " devices attached; pass -Serial explicitly")
        Say "verdict         : BLOCKED reason=ambiguous-device (no device was guessed)"
        Finish 2
    }
    $Serial = $online[0]
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

if ($installed) {
    $dumpsys = (Adb-Shell $Serial ("dumpsys package " + $Package)).Text
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

Say ""
Say "planned commands (nothing below has run unless the verdict says so):"
Say ("  install : adb -s " + $Serial + " install " + $installArgs + " <apk>")
if ($PushModsApproved) {
    Say ("  push    : adb -s " + $Serial + " push <file> /data/local/tmp/  then su -c cp into /data/data/" + $Package + "/files/Mods")
} else {
    Say "  push    : (not requested; needs -PushModsApproved)"
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

if ($PushModsApproved) {
    if (-not $ModsSourceDirectory) {
        Say "verdict         : REFUSED reason=missing-mods-source-directory"
        Finish 3
    }
    if (-not (Test-Path -LiteralPath $ModsSourceDirectory -PathType Container)) {
        Say "verdict         : REFUSED reason=mods-source-directory-missing"
        Finish 3
    }
    $remoteRoot = "/data/data/" + $Package + "/files/Mods"
    Say ""
    Say ("pushing         : local Mods -> " + $remoteRoot)
    $staged = @()
    foreach ($file in Get-ChildItem -LiteralPath $ModsSourceDirectory -File -ErrorAction SilentlyContinue) {
        $remote = "/data/local/tmp/" + $file.Name
        $pushResult = Invoke-Tool $script:AdbBin @("-s", $Serial, "push", $file.FullName, $remote)
        if ($pushResult.Exit -ne 0) {
            Say ("push failed     : " + $file.Name)
            Say "verdict         : PUSH FAILED (partial; the device was not cleaned)"
            Finish 4
        }
        $staged += $remote
    }
    if ($staged.Count -eq 0) {
        Say "verdict         : REFUSED reason=no-files-to-push"
        Finish 3
    }
    $copyCommand = "mkdir -p " + $remoteRoot + " && cp " + ($staged -join " ") + " " + $remoteRoot + "/"
    $suOut = (Adb-Shell $Serial ("su -c '" + $copyCommand + "'")).Text
    Say ("su cp           : " + $suOut)
    Say "verdict         : PUSHED (manual verification of ownership is required before launch)"
}

Finish 0
