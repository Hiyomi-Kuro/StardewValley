# Fake-adb / fake-apksigner harness for Tools/deploy-apk-safe.ps1 (Phase 1.1.4).
#
# No device is contacted. Each case runs the real deployment script in a fresh pwsh with a
# controlled environment, a fake `adb` that logs EVERY invocation, and a fake `apksigner` that
# derives a certificate digest from the file it is handed.
#
# The assertions are about what the script DID, not only about its exit code:
# every case ends by proving that no `uninstall` / `pm clear` / `force-stop` / `am start`
# invocation appears in the fake adb log.
#
# Usage (repo root):
#   pwsh -NoProfile -File Tools/tests/test_deploy_safe.ps1
# Exit code 0 = every check passed.

$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $here "..\..")).Path
$scriptPath = Join-Path $root "Tools\deploy-apk-safe.ps1"
$pwshExe = (Get-Process -Id $PID).Path

$script:results = New-Object System.Collections.Generic.List[object]

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    $tag = if ($ok) { "PASS" } else { "FAIL" }
    Write-Host ("{0} {1,-46} {2}" -f $tag, $name, $detail)
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("sv-deploy-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

# The fake tools are plain .ps1 files: every case runs in a child started with
# -ExecutionPolicy Bypass, so the deployment script can launch them by path exactly like it
# would launch adb.exe. (This host otherwise forbids running .ps1 files directly.)
$fakeAdb = Join-Path $tmp "fake-adb.ps1"
$fakeSigner = Join-Path $tmp "fake-apksigner.ps1"
$emptyDir = Join-Path $tmp "empty-path"
New-Item -ItemType Directory -Force -Path $emptyDir | Out-Null

# A directory whose name exercises spaces, non-ASCII and shell metacharacters at once.
# The non-ASCII part is built from char codes: this file must stay pure ASCII so that a
# PowerShell host cannot mis-decode it (see AGENTS.md 10.3, item 7).
$han = [string][char]0x6C49
$oddDir = Join-Path $tmp ("sw test [x] (y) " + $han)
New-Item -ItemType Directory -Force -Path $oddDir | Out-Null
$oddApk = Join-Path $oddDir "app debug (1).apk"
$oddOut = Join-Path $oddDir ("log " + [string][char]0x8F93 + [string][char]0x51FA + ".txt")

# ---------------------------------------------------------------- fake tools (ASCII only)
$adbBody = @'
$log = $env:FAKE_ADB_LOG
if ($log) {
    [System.IO.File]::AppendAllText($log, "[adb] " + ($args -join " ") + "`r`n")
}
$a = @($args)
if ($a.Count -eq 0) { exit 1 }

if ($a[0] -eq "devices") {
    if ($env:FAKE_DEVICES) {
        foreach ($d in ($env:FAKE_DEVICES -split ";")) {
            if ($d) { Write-Output ($d + "`tdevice product:fake model:FakeDevice") }
        }
    }
    exit 0
}

$i = 0
$serial = ""
if ($a[0] -eq "-s" -and $a.Count -ge 2) { $serial = $a[1]; $i = 2 }
if ($i -ge $a.Count) { exit 1 }
$verb = $a[$i]

function PropValue([string]$name) {
    switch ($name) {
        "ro.product.cpu.abi" { return $env:FAKE_ABI }
        "ro.product.cpu.abilist" { return $env:FAKE_ABILIST }
        "ro.dalvik.vm.native.bridge" { return $env:FAKE_BRIDGE }
        "ro.kernel.qemu" { return $env:FAKE_QEMU }
        "ro.build.version.release" { return "12" }
        "ro.build.version.sdk" { return "31" }
        "ro.product.model" { return $env:FAKE_MODEL }
        default { return "" }
    }
}

switch ($verb) {
    "shell" {
        $rest = ($a[($i + 1)..($a.Count - 1)] -join " ")
        if ($rest -like "getprop *") {
            $p = ($rest -replace "^getprop\s+", "").Trim()
            Write-Output (PropValue $p)
            exit 0
        }
        if ($rest -like "pm path*") {
            if ($env:FAKE_PMPATH) { Write-Output $env:FAKE_PMPATH }
            exit 0
        }
        if ($rest -like "dumpsys package*") {
            if ($env:FAKE_DUMPSYS) { foreach ($line in ($env:FAKE_DUMPSYS -split ";")) { if ($line) { Write-Output $line } } }
            exit 0
        }
        if ($rest -like "su -c*") { Write-Output "(fake) su ok"; exit 0 }
        Write-Output "(fake) shell ok"
        exit 0
    }
    "pull" {
        if ($env:FAKE_PULL_SRC -and (Test-Path -LiteralPath $env:FAKE_PULL_SRC)) {
            $dest = $a[$a.Count - 1]
            Copy-Item -LiteralPath $env:FAKE_PULL_SRC -Destination $dest -Force
            Write-Output ("1 file pulled")
            exit 0
        }
        [Console]::Error.WriteLine("adb: error: failed to stat remote object")
        exit 1
    }
    "install" {
        Write-Output $env:FAKE_INSTALL_RESULT
        if ($env:FAKE_INSTALL_EXIT) { exit [int]$env:FAKE_INSTALL_EXIT }
        exit 0
    }
    "push" { Write-Output "1 file pushed"; exit 0 }
    default { Write-Output ("UNEXPECTED adb verb: " + $rest); exit 1 }
}
'@
Set-Content -LiteralPath $fakeAdb -Value $adbBody -Encoding ascii

$signerBody = @'
$a = @($args)
$apk = $a[$a.Count - 1]
if (-not (Test-Path -LiteralPath $apk)) {
    [Console]::Error.WriteLine("fake apksigner: no such file")
    exit 1
}
if ($env:FAKE_SIGNER_FAIL -eq "1") {
    [Console]::Error.WriteLine("fake apksigner: parsing failed")
    exit 2
}
$h = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLower()
Write-Output ("Signer #1 certificate SHA-256 digest: " + $h)
exit 0
'@
Set-Content -LiteralPath $fakeSigner -Value $signerBody -Encoding ascii

# Local APK and a different APK that stands for "the installed game built elsewhere".
$localApk = Join-Path $tmp "app-debug.apk"
$otherApk = Join-Path $tmp "other-signed.apk"
[System.IO.File]::WriteAllBytes($localApk, [byte[]](1..64))
[System.IO.File]::WriteAllBytes($otherApk, [byte[]](65..128))
[System.IO.File]::WriteAllBytes($oddApk, [byte[]](129..192))

$modsSource = Join-Path $tmp "ModsSrc"
New-Item -ItemType Directory -Force -Path $modsSource | Out-Null
Set-Content -LiteralPath (Join-Path $modsSource "ModFramework.Runtime.dll") -Value "fake" -Encoding ascii

function Invoke-Case([hashtable]$vars, [hashtable]$flags) {
    $wrapper = Join-Path $tmp ("case-" + [Guid]::NewGuid().ToString("N").Substring(0, 6) + ".ps1")
    $log = Join-Path $tmp ("adb-" + [Guid]::NewGuid().ToString("N").Substring(0, 6) + ".log")

    # Parameters are passed through a HASHTABLE splat. Splatting an ARRAY passes elements
    # positionally only, so "-Adb <path>" would arrive as the positional value of -Serial
    # (a real bug this harness caught). Values travel through the environment, which keeps
    # the generated file pure ASCII and survives quotes/spaces/non-ASCII characters.
    $values = [ordered]@{}
    foreach ($k in $vars.Keys) { $values[$k] = [string]$vars[$k] }
    $values["FAKE_ADB_LOG"] = $log
    $values["T_ADB"] = if ($flags.ContainsKey("Adb")) { $flags["Adb"] } else { $fakeAdb }
    $values["T_SIGNER"] = if ($flags.ContainsKey("ApkSigner")) { $flags["ApkSigner"] } else { $fakeSigner }
    $values["T_APK"] = if ($flags.ContainsKey("ApkPath")) { $flags["ApkPath"] } else { $localApk }
    $values["T_SERIAL"] = if ($flags.ContainsKey("Serial")) { $flags["Serial"] } else { "" }
    $values["T_MODS"] = if ($flags.ContainsKey("ModsSource")) { $flags["ModsSource"] } else { "" }
    $values["T_OUT"] = if ($flags.ContainsKey("OutFile")) { $flags["OutFile"] } else { "" }
    $values["T_INSTALL"] = if ($flags["Install"] -eq $true) { "1" } else { "0" }
    $values["T_PUSH"] = if ($flags["Push"] -eq $true) { "1" } else { "0" }

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('$ErrorActionPreference = "Continue"')
    foreach ($k in $values.Keys) {
        $escaped = ([string]$values[$k]) -replace "'", "''"
        [void]$sb.AppendLine(('$env:{0} = ''{1}''' -f $k, $escaped))
    }
    [void]$sb.AppendLine('$p = @{ Adb = $env:T_ADB; ApkPath = $env:T_APK }')
    [void]$sb.AppendLine('if ($env:T_SIGNER) { $p["ApkSigner"] = $env:T_SIGNER }')
    [void]$sb.AppendLine('if ($env:T_SERIAL) { $p["Serial"] = $env:T_SERIAL }')
    [void]$sb.AppendLine('if ($env:T_MODS) { $p["ModsSourceDirectory"] = $env:T_MODS }')
    [void]$sb.AppendLine('if ($env:T_OUT) { $p["OutFile"] = $env:T_OUT }')
    [void]$sb.AppendLine('if ($env:T_INSTALL -eq "1") { $p["InstallApproved"] = $true }')
    [void]$sb.AppendLine('if ($env:T_PUSH -eq "1") { $p["PushModsApproved"] = $true }')
    [void]$sb.AppendLine(('& ''{0}'' @p' -f ($scriptPath -replace "'", "''")))
    [void]$sb.AppendLine('exit $LASTEXITCODE')
    # UTF-8 WITH BOM: the generated file carries a non-ASCII path, and a BOM-less UTF-8 file
    # read by Windows PowerShell 5.1 is decoded as ANSI (the path would be mangled).
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText($wrapper, $sb.ToString(), $utf8Bom)

    # Native stderr (the fake adb reports a failed pull on stderr) must not terminate the
    # harness: with $ErrorActionPreference = "Stop", PowerShell turns it into an exception.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $text = & $pwshExe -NoProfile -ExecutionPolicy Bypass -File $wrapper 2>&1 | Out-String
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    $adbText = if (Test-Path -LiteralPath $log) { [System.IO.File]::ReadAllText($log) } else { "" }
    return [pscustomobject]@{ Output = $text; ExitCode = $code; AdbLog = $adbText; Wrapper = $wrapper }
}

# Base environment: no real adb on PATH, no real SDK, and a fake profile so redaction is testable.
$base = @{
    PATH = $emptyDir
    ANDROID_HOME = ""
    ANDROID_SDK_ROOT = ""
    ADB_PATH = ""
    LOCALAPPDATA = ""
    USERPROFILE = (Join-Path $tmp "home")
    HOME = (Join-Path $tmp "home")
    FAKE_DEVICES = "emulator-5554"
    FAKE_ABI = "x86_64"
    FAKE_ABILIST = "x86_64"
    FAKE_BRIDGE = "libnb.so"
    FAKE_QEMU = "1"
    FAKE_MODEL = "MuMu"
    FAKE_PMPATH = "package:/data/app/~~abc==/com.chucklefish.stardewvalley-1/base.apk"
    FAKE_DUMPSYS = "versionCode=239;versionName=1.6.15.0;primaryCpuAbi=x86_64;signatures=[aaaa]"
    FAKE_PULL_SRC = ""
    FAKE_INSTALL_RESULT = "Success"
    FAKE_INSTALL_EXIT = ""
    FAKE_SIGNER_FAIL = ""
}

function Merge([hashtable]$overrides) {
    $h = @{}
    foreach ($k in $base.Keys) { $h[$k] = $base[$k] }
    foreach ($k in $overrides.Keys) { $h[$k] = $overrides[$k] }
    return $h
}

function Test-NoDestructive([string]$adbLog) {
    if (-not $adbLog) { return $true }
    foreach ($needle in @("uninstall", "pm clear", "force-stop", "am start", "clear data")) {
        if ($adbLog -match [regex]::Escape($needle)) { return $false }
    }
    return $true
}

try {
    # 1. default run: read only
    $r = Invoke-Case (Merge @{}) @{ Install = $false }
    Check "default_no_device_write" ($r.ExitCode -eq 0 -and $r.Output -match "PRECHECK-ONLY") ("exit=" + $r.ExitCode)
    Check "default_never_installs" (-not ($r.AdbLog -match "\binstall\b")) "adb log has no install"
    Check "default_never_pulls" (-not ($r.AdbLog -match "\bpull\b")) "no device file transfer without approval"
    Check "default_never_pushes" (-not ($r.AdbLog -match "\bpush\b")) "no push without -PushModsApproved"
    Check "default_gate_not_evaluated" ($r.Output -match "signature-gate\s+: NOT EVALUATED") "gate needs -InstallApproved"
    Check "default_reports_plan" ($r.Output -match "planned commands" -and $r.Output -match "uninstall : NEVER") "plan is printed"
    Check "default_classifies_emulator" ($r.Output -match "EMULATOR-TRANSLATED" -and $r.Output -match "do not count as device evidence") "x86_64 + bridge is not ARM64"

    # 2. no device at all
    $r = Invoke-Case (Merge @{ FAKE_DEVICES = "" }) @{}
    Check "no_device_is_blocked" ($r.ExitCode -eq 2 -and $r.Output -match "BLOCKED reason=no-device") ("exit=" + $r.ExitCode)
    Check "no_device_runs_nothing" (Test-NoDestructive $r.AdbLog) "no adb verb beyond devices"

    # 3. two devices without -Serial: refuse to guess
    $r = Invoke-Case (Merge @{ FAKE_DEVICES = "emu-a;emu-b" }) @{}
    Check "ambiguous_device_is_blocked" ($r.ExitCode -eq 2 -and $r.Output -match "ambiguous-device") ("exit=" + $r.ExitCode)
    Check "ambiguous_device_runs_nothing" (-not ($r.AdbLog -match "\bshell\b")) "no per-device command was issued"

    # 3b. two devices WITH an explicit serial: proceed
    $r = Invoke-Case (Merge @{ FAKE_DEVICES = "emu-a;emu-b" }) @{ Serial = "emu-b" }
    Check "explicit_serial_selects_device" ($r.ExitCode -eq 0 -and $r.Output -match "emu-b \(explicit\)") ("exit=" + $r.ExitCode)

    # 4. serial that is not attached
    $r = Invoke-Case (Merge @{}) @{ Serial = "not-here" }
    Check "unknown_serial_is_blocked" ($r.ExitCode -eq 2 -and $r.Output -match "serial-not-attached") ("exit=" + $r.ExitCode)

    # 5. approved install, same signature -> install -r, never uninstall
    $r = Invoke-Case (Merge @{ FAKE_PULL_SRC = $localApk }) @{ Install = $true }
    Check "approved_same_signature_installs" ($r.ExitCode -eq 0 -and $r.AdbLog -match "\binstall\b") ("exit=" + $r.ExitCode)
    Check "approved_same_signature_uses_replace" ($r.AdbLog -match "install -r --no-incremental") "same-signature replace only"
    Check "approved_same_signature_never_uninstalls" (Test-NoDestructive $r.AdbLog) "no uninstall / clear / force-stop / launch"
    Check "approved_same_signature_gate_says_same" ($r.Output -match "signature-gate\s+: SAME") "gate evaluated and equal"

    # 6. approved install, different signature -> stop, nothing written
    $r = Invoke-Case (Merge @{ FAKE_PULL_SRC = $otherApk }) @{ Install = $true }
    Check "signature_mismatch_is_refused" ($r.ExitCode -eq 3 -and $r.Output -match "SIGNATURE_MISMATCH_REQUIRES_USER_ACTION") ("exit=" + $r.ExitCode)
    Check "signature_mismatch_never_installs" (-not ($r.AdbLog -match "\binstall\b")) "no install was attempted"
    Check "signature_mismatch_never_uninstalls" (Test-NoDestructive $r.AdbLog) "no uninstall / clear was attempted"
    Check "signature_mismatch_explains_manual_steps" ($r.Output -match "manual procedure" -and $r.Output -match "Saves") "operator gets the exact steps"

    # 7. approved install, installed certificate unreadable -> fail closed
    $r = Invoke-Case (Merge @{ FAKE_PULL_SRC = "" }) @{ Install = $true }
    Check "signature_unreadable_is_refused" ($r.ExitCode -eq 3 -and $r.Output -match "SIGNATURE_UNREADABLE_USE_MANUAL_VERIFICATION") ("exit=" + $r.ExitCode)
    Check "signature_unreadable_never_installs" (-not ($r.AdbLog -match "\binstall\b")) "no install was attempted"

    # 8. approved install but the local signer cannot be read
    $r = Invoke-Case (Merge @{ FAKE_PULL_SRC = $localApk; FAKE_SIGNER_FAIL = "1" }) @{ Install = $true }
    Check "local_signer_unreadable_is_refused" ($r.ExitCode -eq 3 -and $r.Output -match "SIGNATURE_UNREADABLE") ("exit=" + $r.ExitCode)
    Check "local_signer_unreadable_never_installs" (-not ($r.AdbLog -match "\binstall\b")) "no install was attempted"

    # 9. approved install but no APK was built
    $r = Invoke-Case (Merge @{}) @{ Install = $true; ApkPath = (Join-Path $tmp "missing.apk") }
    Check "missing_apk_is_refused" ($r.ExitCode -eq 3 -and $r.Output -match "reason=apk-missing") ("exit=" + $r.ExitCode)

    # 10. first install of an absent package: install WITHOUT -r, still no uninstall
    $r = Invoke-Case (Merge @{ FAKE_PMPATH = "" }) @{ Install = $true }
    Check "absent_package_first_install" ($r.ExitCode -eq 0 -and $r.AdbLog -match "install --no-incremental") ("exit=" + $r.ExitCode)
    Check "absent_package_not_installed_first" (-not ($r.AdbLog -match "install -r")) "no -r when nothing is installed"
    Check "absent_package_never_uninstalls" (Test-NoDestructive $r.AdbLog) "no uninstall / clear / launch"

    # 11. push needs its own approval
    $r = Invoke-Case (Merge @{}) @{ Install = $false; Push = $false; ModsSource = $modsSource }
    Check "push_without_approval_does_nothing" ($r.ExitCode -eq 0 -and -not ($r.AdbLog -match "\bpush\b")) "no push without -PushModsApproved"

    $r = Invoke-Case (Merge @{}) @{ Install = $false; Push = $true }
    Check "push_without_source_dir_is_refused" ($r.ExitCode -eq 3 -and $r.Output -match "missing-mods-source-directory") ("exit=" + $r.ExitCode)

    # 12. an approved push does reach the device (proof the gate is not simply "never push")
    $r = Invoke-Case (Merge @{}) @{ Install = $false; Push = $true; ModsSource = $modsSource }
    Check "approved_push_pushes" ($r.ExitCode -eq 0 -and $r.AdbLog -match "\bpush\b") ("exit=" + $r.ExitCode)
    Check "approved_push_never_uninstalls" (Test-NoDestructive $r.AdbLog) "no uninstall / clear / launch"

    # 13. paths with spaces / brackets / non-ASCII survive argument passing
    $r = Invoke-Case (Merge @{ FAKE_PULL_SRC = $oddApk }) @{ Install = $true; ApkPath = $oddApk; OutFile = $oddOut }
    $oddTail = (($r.Output -split "`r?`n") | Where-Object { $_.Trim() } | Select-Object -Last 2) -join " / "
    Check "odd_paths_are_safe" ($r.ExitCode -eq 0 -and (Test-Path -LiteralPath $oddOut)) ("exit=" + $r.ExitCode + " " + $oddTail)
    $oddText = if (Test-Path -LiteralPath $oddOut) { [System.IO.File]::ReadAllText($oddOut) } else { "" }
    Check "odd_paths_preserved_in_report" ($oddText -match "app debug \(1\)\.apk") "the APK name survives intact"

    # 14. no user profile path leaks into the output
    $r = Invoke-Case (Merge @{ FAKE_PULL_SRC = $localApk }) @{ Install = $true }
    $leaks = ($r.Output -match '(?i)[A-Z]:\\Users\\') -or ($r.Output -match [regex]::Escape($tmp))
    Check "no_home_path_in_output" (-not $leaks) "output shows <home> / leaf names only"
    # A user name containing spaces must be redacted as a whole, not just its first token.
    $partial = $r.Output -match '(?i)<home>[^\\\r\n]*\s+[^\\\r\n]+\\'
    Check "spaced_user_name_fully_redacted" (-not $partial) "no leftover name fragment after <home>"

    # 15. the fake harness itself must have recorded that adb was really invoked
    $r = Invoke-Case (Merge @{}) @{}
    Check "harness_records_adb_calls" ($r.AdbLog -match "devices" -and $r.AdbLog -match "getprop") "the fake adb log is the evidence base"

    # 16. static: the safe script has no uninstall path at all
    $text = [System.IO.File]::ReadAllText($scriptPath)
    $badLine = $false
    foreach ($line in ($text -split "`r?`n")) {
        $t = $line.Trim()
        if (-not $t -or $t.StartsWith("#") -or $t.ToLower().StartsWith("say")) { continue }
        if ($t -match "(?i)\buninstall\b|\bpm\s+clear\b|force-stop") { $badLine = $true }
    }
    Check "script_has_no_destructive_invocation" (-not $badLine) "no executable uninstall/clear/force-stop line"
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

$failed = @($script:results | Where-Object { -not $_.Ok })
Write-Host ""
Write-Host ("{0} checks, {1} passed, {2} failed" -f $script:results.Count, ($script:results.Count - $failed.Count), $failed.Count)
if ($failed.Count -gt 0) {
    foreach ($f in $failed) { Write-Host ("  FAILED: {0}  {1}" -f $f.Name, $f.Detail) }
    exit 1
}
exit 0
