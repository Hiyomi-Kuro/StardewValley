# Fake-adb / fake-apksigner harness for Tools/deploy-apk-safe.ps1 (Phase 1.1.4, extended 1.1.5).
#
# No device is contacted. Each case runs the real deployment script in a fresh child process
# with a controlled environment, a fake `adb` that models a DEVICE FILESYSTEM, and a fake
# `apksigner` that derives a certificate digest from the file it is handed.
#
# Why the fake adb models a filesystem (Phase 1.1.5):
#   The Phase 1.1.4 fake only printed "1 file pushed" and answered any `su -c` with "ok". That
#   could not observe the defect this phase fixes: the old implementation flattened the mod
#   tree. Every assertion here reads the simulated device tree and its SHA-256 values, so the
#   checks are about the deployed PATHS AND BYTES, never merely "adb push was called".
#
# The fake understands exactly the remote commands the deployment script may issue:
#   mkdir -p, cp -p, chown, chmod, sha256sum, stat -c, test -e, ls -1a, rm -rf, echo.
# Anything else is reported as UNEXPECTED, which fails the case loudly.
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

$devRoot = $env:FAKE_DEVICE_ROOT
$pkg = "com.chucklefish.stardewvalley"
$filesDirDev = "/data/data/" + $pkg + "/files"
$modsRootDev = "/data/data/" + $pkg + "/files/Mods"
$appUid = if ($env:FAKE_APP_UID) { $env:FAKE_APP_UID } else { "10152" }
$appGid = if ($env:FAKE_APP_GID) { $env:FAKE_APP_GID } else { "10152" }

function To-DevPath([string]$devicePath) {
    if (-not $devRoot) { throw "fake adb: FAKE_DEVICE_ROOT is not set" }
    $rel = ($devicePath.TrimStart([char]'/') -replace '/', [char]'\')
    if (-not $rel) { return $devRoot }
    return [System.IO.Path]::Combine($devRoot, $rel)
}

function Expand-DevicePath([string]$p) { return (To-DevPath $p) }

function Get-Owner([string]$devicePath) {
    if ($env:FAKE_OWNER_ALL) { return $env:FAKE_OWNER_ALL }
    if ($devicePath -eq $modsRootDev -and $env:FAKE_MODS_OWNER) { return $env:FAKE_MODS_OWNER }
    if ($devicePath.StartsWith($modsRootDev + "/") -and $env:FAKE_OWNER_MODS_FILES) { return $env:FAKE_OWNER_MODS_FILES }
    if ($devicePath -eq $filesDirDev -or $devicePath.StartsWith($filesDirDev + "/")) {
        return ($appUid + ":" + $appGid)
    }
    return "0:0"
}

function Get-Mode([string]$devicePath) {
    if ($devicePath -eq $filesDirDev) { return "771" }
    $local = To-DevPath $devicePath
    if (Test-Path -LiteralPath $local -PathType Container) { return "755" }
    return "644"
}

# Split one POSIX command line into words. Single quotes group; a backslash escapes the next
# character outside quotes. Adjacent fragments form ONE word (so 'a'\''b' becomes "a'b").
function Split-ShellWords([string]$s) {
    $tokens = New-Object System.Collections.Generic.List[string]
    $cur = New-Object System.Text.StringBuilder
    $inQ = $false
    $has = $false
    for ($k = 0; $k -lt $s.Length; $k++) {
        $ch = $s[$k]
        if ($inQ) {
            if ($ch -eq "'") { $inQ = $false } else { [void]$cur.Append($ch) }
        } elseif ($ch -eq "'") {
            $inQ = $true
            $has = $true
        } elseif ($ch -eq [char]'\') {
            $k++
            if ($k -lt $s.Length) { [void]$cur.Append($s[$k]); $has = $true }
        } elseif ($ch -eq ' ' -or $ch -eq [char]9) {
            if ($has) { $tokens.Add($cur.ToString()); [void]$cur.Clear(); $has = $false }
        } else {
            [void]$cur.Append($ch)
            $has = $true
        }
    }
    if ($has) { $tokens.Add($cur.ToString()) }
    return ,$tokens
}

# Split a script into && separated segments, respecting quotes.
function Split-AndList([string]$s) {
    $segments = New-Object System.Collections.Generic.List[string]
    $cur = New-Object System.Text.StringBuilder
    $inQ = $false
    for ($k = 0; $k -lt $s.Length; $k++) {
        $ch = $s[$k]
        if ($inQ) {
            [void]$cur.Append($ch)
            if ($ch -eq "'") { $inQ = $false }
            continue
        }
        if ($ch -eq "'") { $inQ = $true; [void]$cur.Append($ch); continue }
        if ($ch -eq [char]'\') {
            [void]$cur.Append($ch)
            if ($k + 1 -lt $s.Length) { $k++; [void]$cur.Append($s[$k]) }
            continue
        }
        if ($ch -eq '&' -and $k + 1 -lt $s.Length -and $s[$k + 1] -eq '&') {
            $segments.Add($cur.ToString())
            [void]$cur.Clear()
            $k++
            continue
        }
        if ($ch -eq ';') {
            $segments.Add($cur.ToString())
            [void]$cur.Clear()
            continue
        }
        [void]$cur.Append($ch)
    }
    $segments.Add($cur.ToString())
    return ,$segments
}

function Invoke-Segment([string]$segment) {
    # Command output is written with Write-Output; the exit status travels in $script:ExitCode
    # so that a caller assignment can never swallow the output as if it were the status.
    $script:ExitCode = 0
    $words = Split-ShellWords $segment
    if ($null -eq $words -or $words.Count -eq 0) { return }
    $cmd = $words[0]
    switch ($cmd) {
        "echo" {
            $parts = @()
            for ($k = 1; $k -lt $words.Count; $k++) { $parts += $words[$k] }
            $text = ($parts -join " ")
            $text = $text.Replace('$?', [string]$script:ExitCode)
            Write-Output $text
            $script:ExitCode = 0
            return
        }
        "test" {
            if ($words.Count -ge 3 -and $words[1] -eq "-e") {
                if (Test-Path -LiteralPath (To-DevPath $words[2])) { $script:ExitCode = 0 } else { $script:ExitCode = 1 }
                return
            }
            if ($words.Count -ge 3 -and $words[1] -eq "-d") {
                if (Test-Path -LiteralPath (To-DevPath $words[2]) -PathType Container) { $script:ExitCode = 0 } else { $script:ExitCode = 1 }
                return
            }
            $script:ExitCode = 1
            return
        }
        "mkdir" {
            $k = 1
            if ($words.Count -gt 1 -and $words[1] -eq "-p") { $k = 2 }
            if ($env:FAKE_MKDIR_FAIL -eq "1") {
                [Console]::Error.WriteLine("mkdir: failed to create directory")
                $script:ExitCode = 1
                return
            }
            for (; $k -lt $words.Count; $k++) {
                $local = To-DevPath $words[$k]
                if (-not (Test-Path -LiteralPath $local)) {
                    [void][System.IO.Directory]::CreateDirectory($local)
                }
            }
            $script:ExitCode = 0
            return
        }
        "sha256sum" {
            $rc = 0
            for ($k = 1; $k -lt $words.Count; $k++) {
                $p = $words[$k]
                $local = To-DevPath $p
                if (-not (Test-Path -LiteralPath $local -PathType Leaf)) {
                    [Console]::Error.WriteLine("sha256sum: " + $p + ": No such file or directory")
                    $rc = 1
                    continue
                }
                $h = (Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash.ToLower()
                Write-Output ($h + "  " + $p)
            }
            $script:ExitCode = $rc
            return
        }
        "stat" {
            $k = 1
            $fmt = ""
            if ($words.Count -gt 2 -and $words[1] -eq "-c") { $fmt = $words[2]; $k = 3 }
            $rc = 0
            for (; $k -lt $words.Count; $k++) {
                $p = $words[$k]
                if ($env:FAKE_STAT_FAIL -eq "1") {
                    [Console]::Error.WriteLine("stat: cannot stat " + $p)
                    $rc = 1
                    continue
                }
                if (-not (Test-Path -LiteralPath (To-DevPath $p))) {
                    [Console]::Error.WriteLine("stat: cannot stat " + $p + ": No such file or directory")
                    $rc = 1
                    continue
                }
                $owner = Get-Owner $p
                $mode = Get-Mode $p
                if ($fmt -eq "%n %u:%g:%a") { Write-Output ($p + " " + $owner + ":" + $mode) }
                elseif ($fmt -eq "%u:%g:%a") { Write-Output ($owner + ":" + $mode) }
                else { Write-Output ($p + " " + $owner + ":" + $mode) }
            }
            $script:ExitCode = $rc
            return
        }
        "ls" {
            $k = 1
            while ($k -lt $words.Count -and $words[$k].StartsWith("-")) { $k++ }
            if ($k -ge $words.Count) { $script:ExitCode = 1; return }
            $p = $words[$k]
            $local = To-DevPath $p
            if (-not (Test-Path -LiteralPath $local)) {
                [Console]::Error.WriteLine("ls: " + $p + ": No such file or directory")
                $script:ExitCode = 1
                return
            }
            Write-Output "."
            Write-Output ".."
            foreach ($child in (Get-ChildItem -LiteralPath $local -Force -ErrorAction SilentlyContinue)) {
                Write-Output $child.Name
            }
            $script:ExitCode = 0
            return
        }
        "cp" {
            $k = 1
            if ($words.Count -gt 1 -and $words[1] -eq "-p") { $k = 2 }
            if ($words.Count -lt ($k + 2)) { $script:ExitCode = 1; return }
            if ($env:FAKE_COPY_FAIL -eq "1") {
                [Console]::Error.WriteLine("cp: I/O error")
                $script:ExitCode = 1
                return
            }
            $src = To-DevPath $words[$k]
            $dst = To-DevPath $words[$k + 1]
            if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
                [Console]::Error.WriteLine("cp: " + $words[$k] + ": No such file or directory")
                $script:ExitCode = 1
                return
            }
            $parent = Split-Path -Parent $dst
            if (-not (Test-Path -LiteralPath $parent)) {
                [Console]::Error.WriteLine("cp: " + $words[$k + 1] + ": No such file or directory")
                $script:ExitCode = 1
                return
            }
            Copy-Item -LiteralPath $src -Destination $dst -Force
            $script:ExitCode = 0
            return
        }
        "chown" {
            if ($env:FAKE_CHOWN_FAIL -eq "1") {
                [Console]::Error.WriteLine("chown: Operation not permitted")
                $script:ExitCode = 1
                return
            }
            $script:ExitCode = 0
            return
        }
        "chmod" { $script:ExitCode = 0; return }
        "command" {
            $k = 1
            if ($words.Count -gt 1 -and $words[1] -eq "-v") { $k = 2 }
            $missing = $false
            for (; $k -lt $words.Count; $k++) {
                $name = $words[$k]
                if ($env:FAKE_MISSING_TOOL -and $env:FAKE_MISSING_TOOL -eq $name) {
                    $missing = $true
                    continue
                }
                Write-Output ("/system/bin/" + $name)
            }
            if ($missing) { $script:ExitCode = 1 } else { $script:ExitCode = 0 }
            return
        }
        "rm" {
            $k = 1
            while ($k -lt $words.Count -and $words[$k].StartsWith("-")) { $k++ }
            for (; $k -lt $words.Count; $k++) {
                $local = To-DevPath $words[$k]
                if (Test-Path -LiteralPath $local) {
                    Remove-Item -LiteralPath $local -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
            $script:ExitCode = 0
            return
        }
        default {
            Write-Output ("UNEXPECTED remote command: " + $cmd)
            $script:ExitCode = 2
            return
        }
    }
    $script:ExitCode = 0
}

function Invoke-AndList([string]$script) {
    $script:ExitCode = 0
    $segments = Split-AndList $script
    foreach ($segment in $segments) {
        if (-not $segment.Trim()) { continue }
        Invoke-Segment $segment
        if ($script:ExitCode -ne 0) { return }
    }
}

# `su -c <one quoted script>` is unwrapped ONE level: the quoted word is unquoted once and then
# parsed as a script. Re-tokenising the unquoted text would destroy the inner quoting.
function Invoke-CommandLine([string]$command) {
    $t = $command.TrimStart()
    if ($t -match '^su\s+-c\s+') {
        $restRaw = $t.Substring($Matches[0].Length)
        $words = Split-ShellWords $restRaw
        if ($null -ne $words -and $words.Count -ge 1) { Invoke-AndList $words[0] } else { $script:ExitCode = 1 }
        return
    }
    if ($t -match '^su\s+\d+\s+') {
        $restRaw = $t.Substring($Matches[0].Length)
        Invoke-AndList $restRaw
        return
    }
    Invoke-AndList $command
}

switch ($verb) {
    "shell" {
        $rest = ($a[($i + 1)..($a.Count - 1)] -join " ")
        if ($rest -like "getprop *") {
            $p = ($rest -replace "^getprop\s+", "").Trim()
            switch ($p) {
                "ro.product.cpu.abi" { Write-Output $env:FAKE_ABI }
                "ro.product.cpu.abilist" { Write-Output $env:FAKE_ABILIST }
                "ro.dalvik.vm.native.bridge" { Write-Output $env:FAKE_BRIDGE }
                "ro.kernel.qemu" { Write-Output $env:FAKE_QEMU }
                "ro.build.version.release" { Write-Output "12" }
                "ro.build.version.sdk" { Write-Output "31" }
                "ro.product.model" { Write-Output $env:FAKE_MODEL }
                default { Write-Output "" }
            }
            exit 0
        }
        if ($rest -like "pm path*") {
            if ($env:FAKE_PMPATH) { Write-Output $env:FAKE_PMPATH }
            exit 0
        }
        if ($rest -like "dumpsys package*") {
            if ($env:FAKE_DUMPSYS) {
                foreach ($line in ($env:FAKE_DUMPSYS -split ";")) { if ($line) { Write-Output $line } }
            }
            exit 0
        }
        Invoke-CommandLine $rest
        exit $script:ExitCode
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
    "push" {
        $local = $a[$i + 1]
        $remote = $a[$i + 2]
        if ($env:FAKE_PUSH_FAIL -and $remote -like ("*" + $env:FAKE_PUSH_FAIL + "*")) {
            [Console]::Error.WriteLine("adb: error: failed to copy")
            exit 1
        }
        if (-not (Test-Path -LiteralPath $local -PathType Leaf)) {
            [Console]::Error.WriteLine("adb: error: cannot stat '" + $local + "'")
            exit 1
        }
        $target = To-DevPath $remote
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent)) {
            [Console]::Error.WriteLine("adb: error: remote path is not a directory")
            exit 1
        }
        Copy-Item -LiteralPath $local -Destination $target -Force
        Write-Output "1 file pushed"
        exit 0
    }
    default {
        Write-Output ("UNEXPECTED adb verb: " + $verb)
        exit 1
    }
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

# ---------------------------------------------------------------- mods source trees
# A complete, correct Mods root: the framework DLL in the root, the mod in its own first-level
# directory with manifest.json, and a multi-level probe-fixtures tree. This is the shape the
# loader requires (Mod/ModFramework.Runtime/ModDiscovery.cs).
$modsSource = Join-Path $tmp "ModsSrc"
$probeDir = Join-Path $modsSource "P114.RuntimeProbe"
New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
Set-Content -LiteralPath (Join-Path $modsSource "ModFramework.Runtime.dll") -Value "runtime-bytes" -Encoding ascii
Set-Content -LiteralPath (Join-Path $probeDir "manifest.json") -Value '{"id":"P114.RuntimeProbe","entryAssembly":"P114.RuntimeProbe.dll","privateAssemblies":["P114.RuntimeProbe.Dependency.dll"]}' -Encoding ascii
Set-Content -LiteralPath (Join-Path $probeDir "P114.RuntimeProbe.dll") -Value "probe-bytes" -Encoding ascii
Set-Content -LiteralPath (Join-Path $probeDir "P114.RuntimeProbe.Dependency.dll") -Value "dependency-bytes" -Encoding ascii
$fixtureNames = @(
    @("same-a", "P114Same.dll", "same-a-bytes"),
    @("same-b", "P114Same.dll", "same-a-bytes"),
    @("diff-a", "P114Diff.dll", "diff-a-bytes"),
    @("diff-b", "P114Diff.dll", "diff-b-bytes"),
    @("alpha", "P114Alpha.dll", "alpha-bytes"),
    @("beta", "P114Beta.dll", "beta-bytes"),
    @("corrupt", "P114Broken.dll", "broken-bytes")
)
foreach ($fx in $fixtureNames) {
    $dir = Join-Path (Join-Path $probeDir "probe-fixtures") $fx[0]
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Set-Content -LiteralPath (Join-Path $dir $fx[1]) -Value $fx[2] -Encoding ascii
}
# A deeper level exercises genuine recursion (probe-fixtures/nested/deep/one.txt).
$deep = Join-Path (Join-Path (Join-Path $probeDir "probe-fixtures") "nested") "deep"
New-Item -ItemType Directory -Force -Path $deep | Out-Null
Set-Content -LiteralPath (Join-Path $deep "one.txt") -Value "deep-bytes" -Encoding ascii

# Sources for the negative cases.
$bareSource = Join-Path $tmp "ModsBare"
New-Item -ItemType Directory -Force -Path $bareSource | Out-Null
Set-Content -LiteralPath (Join-Path $bareSource "ModFramework.Runtime.dll") -Value "fake" -Encoding ascii

$noManifestSource = Join-Path $tmp "ModsNoManifest"
New-Item -ItemType Directory -Force -Path (Join-Path $noManifestSource "SomeMod") | Out-Null
Set-Content -LiteralPath (Join-Path $noManifestSource "ModFramework.Runtime.dll") -Value "fake" -Encoding ascii
Set-Content -LiteralPath (Join-Path (Join-Path $noManifestSource "SomeMod") "x.dll") -Value "x" -Encoding ascii

$emptySource = Join-Path $tmp "ModsEmpty"
New-Item -ItemType Directory -Force -Path $emptySource | Out-Null

# A source with a path that tries to escape the root through a junction-like layout is not
# constructible portably; instead an "absolute-looking" name is impossible on Windows, so the
# escape case is covered by the reparse-point check below.
$linkSource = Join-Path $tmp "ModsLink"
New-Item -ItemType Directory -Force -Path (Join-Path $linkSource "P114.RuntimeProbe") | Out-Null
Set-Content -LiteralPath (Join-Path $linkSource "ModFramework.Runtime.dll") -Value "r" -Encoding ascii
Set-Content -LiteralPath (Join-Path (Join-Path $linkSource "P114.RuntimeProbe") "manifest.json") -Value '{"entryAssembly":"a.dll"}' -Encoding ascii
Set-Content -LiteralPath (Join-Path (Join-Path $linkSource "P114.RuntimeProbe") "a.dll") -Value "a" -Encoding ascii
$linkPath = Join-Path $linkSource "link.dll"
$linkCreated = $false
try {
    $null = New-Item -ItemType SymbolicLink -Path $linkPath -Target $localApk -ErrorAction Stop
    $linkCreated = $true
} catch {
    $linkCreated = $false
}

# An odd-named Mods source: spaces, brackets, parentheses and a Chinese character in a file
# name that must survive the whole pipeline.
$oddMods = Join-Path $tmp ("Mods odd " + $han)
$oddModDir = Join-Path $oddMods "P114.RuntimeProbe"
New-Item -ItemType Directory -Force -Path $oddModDir | Out-Null
Set-Content -LiteralPath (Join-Path $oddMods "ModFramework.Runtime.dll") -Value "r" -Encoding ascii
Set-Content -LiteralPath (Join-Path $oddModDir "manifest.json") -Value '{"entryAssembly":"a.dll"}' -Encoding ascii
Set-Content -LiteralPath (Join-Path $oddModDir "a.dll") -Value "a" -Encoding ascii
$oddFx = Join-Path (Join-Path $oddModDir "probe-fixtures") ("sub [" + $han + "] (1)")
New-Item -ItemType Directory -Force -Path $oddFx | Out-Null
Set-Content -LiteralPath (Join-Path $oddFx "P114 Same.dll") -Value "odd" -Encoding ascii

$deviceApkPath = "/data/app/base/com.chucklefish.stardewvalley-1/base.apk"

function Get-RelativeTree([string]$baseDir) {
    $out = @{}
    foreach ($f in (Get-ChildItem -LiteralPath $baseDir -Recurse -File -Force)) {
        $rel = $f.FullName.Substring($baseDir.Length + 1) -replace '\\', '/'
        $out[$rel] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLower()
    }
    return $out
}

$deviceModsLocal = $null
function Get-DeviceModsTree([string]$devRoot) {
    $local = Join-Path $devRoot ("data\data\com.chucklefish.stardewvalley\files\Mods")
    if (-not (Test-Path -LiteralPath $local)) { return $null }
    return Get-RelativeTree $local
}

function Invoke-Case([hashtable]$vars, [hashtable]$flags) {
    $wrapper = Join-Path $tmp ("case-" + [Guid]::NewGuid().ToString("N").Substring(0, 6) + ".ps1")
    $log = Join-Path $tmp ("adb-" + [Guid]::NewGuid().ToString("N").Substring(0, 6) + ".log")

    # A fresh simulated device for every case: no state can leak between cases. A case that
    # passes -DeviceRoot reuses one, which is how "deploy twice" and "pre-existing file" are
    # modelled.
    if ($flags.ContainsKey("DeviceRoot") -and $flags["DeviceRoot"]) {
        $devRoot = [string]$flags["DeviceRoot"]
    } else {
        $devRoot = Join-Path $tmp ("dev-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    }
    $pkgFiles = Join-Path $devRoot ("data\data\com.chucklefish.stardewvalley\files")
    [void][System.IO.Directory]::CreateDirectory($pkgFiles)
    $seedApk = if ($flags.ContainsKey("SeedApk")) { [string]$flags["SeedApk"] } elseif ($vars.ContainsKey("FAKE_SEED_APK")) { [string]$vars["FAKE_SEED_APK"] } else { "match" }
    if ($seedApk -ne "none") {
        $apkLocal = Join-Path $devRoot ("data\app\base\com.chucklefish.stardewvalley-1\base.apk")
        [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $apkLocal))
        $source = if ($seedApk -eq "other") { $otherApk } else { $localApk }
        Copy-Item -LiteralPath $source -Destination $apkLocal -Force
    }

    # Parameters are passed through a HASHTABLE splat. Splatting an ARRAY passes elements
    # positionally only, so "-Adb <path>" would arrive as the positional value of -Serial
    # (a real bug this harness caught). Values travel through the environment, which keeps
    # the generated file pure ASCII and survives quotes/spaces/non-ASCII characters.
    $values = [ordered]@{}
    foreach ($k in $vars.Keys) { $values[$k] = [string]$vars[$k] }
    $values["FAKE_DEVICE_ROOT"] = $devRoot
    $values["FAKE_ADB_LOG"] = $log
    $values["T_ADB"] = if ($flags.ContainsKey("Adb")) { $flags["Adb"] } else { $fakeAdb }
    $values["T_SIGNER"] = if ($flags.ContainsKey("ApkSigner")) { $flags["ApkSigner"] } else { $fakeSigner }
    $values["T_APK"] = if ($flags.ContainsKey("ApkPath")) { $flags["ApkPath"] } else { $localApk }
    $values["T_SERIAL"] = if ($flags.ContainsKey("Serial")) { $flags["Serial"] } else { "" }
    $values["T_MODS"] = if ($flags.ContainsKey("ModsSource")) { $flags["ModsSource"] } else { "" }
    $values["T_OUT"] = if ($flags.ContainsKey("OutFile")) { $flags["OutFile"] } else { "" }
    $values["T_INSTALL"] = if ($flags["Install"] -eq $true) { "1" } else { "0" }
    $values["T_PUSH"] = if ($flags["Push"] -eq $true) { "1" } else { "0" }
    $values["T_OVERWRITE"] = if ($flags["Overwrite"] -eq $true) { "1" } else { "0" }

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
    [void]$sb.AppendLine('if ($env:T_OVERWRITE -eq "1") { $p["OverwriteModsApproved"] = $true }')
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
    return [pscustomobject]@{
        Output    = $text
        ExitCode  = $code
        AdbLog    = $adbText
        Wrapper   = $wrapper
        DevRoot   = $devRoot
        ModsTree  = (Get-DeviceModsTree $devRoot)
        ModsLocal = (Join-Path $devRoot ("data\data\com.chucklefish.stardewvalley\files\Mods"))
    }
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
    FAKE_ABILIST = "x86_64,arm64-v8a"
    FAKE_BRIDGE = "libnb.so"
    FAKE_QEMU = "1"
    FAKE_MODEL = "MuMu"
    FAKE_PMPATH = ("package:" + $deviceApkPath)
    FAKE_DUMPSYS = "versionCode=239;versionName=1.6.15.0;primaryCpuAbi=arm64-v8a;signatures=[aaaa]"
    FAKE_PULL_SRC = ""
    FAKE_INSTALL_RESULT = "Success"
    FAKE_INSTALL_EXIT = ""
    FAKE_SIGNER_FAIL = ""
    FAKE_SEED_APK = "match"
    FAKE_APP_UID = "10152"
    FAKE_APP_GID = "10152"
    FAKE_OWNER_ALL = ""
    FAKE_OWNER_MODS_FILES = ""
    FAKE_MODS_OWNER = ""
    FAKE_STAT_FAIL = ""
    FAKE_MKDIR_FAIL = ""
    FAKE_COPY_FAIL = ""
    FAKE_CHOWN_FAIL = ""
    FAKE_PUSH_FAIL = ""
    FAKE_MISSING_TOOL = ""
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

function Get-StagingDirs([string]$devRoot) {
    $tmpRoot = Join-Path $devRoot "data\local\tmp"
    if (-not (Test-Path -LiteralPath $tmpRoot)) { return @() }
    return @(Get-ChildItem -LiteralPath $tmpRoot -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "svmods-*" })
}

try {
    # ---------------------------------------------------------------- Phase 1.1.4 regression
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
    $r = Invoke-Case (Merge @{}) @{ Install = $false; Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
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

    # ---------------------------------------------------------------- Phase 1.1.5: plan gates
    # 17. a default run that names a source directory PRINTS the plan and still writes nothing
    $r = Invoke-Case (Merge @{}) @{ Install = $false; ModsSource = $modsSource }
    $planCount = ([regex]::Matches($r.Output, "  file\s+:")).Count
    Check "precheck_prints_mods_plan" ($r.ExitCode -eq 0 -and $r.Output -match "mods-plan\s+: 12 files" -and $planCount -eq 12) ("files=" + $planCount)
    Check "precheck_plan_writes_nothing" (-not ($r.AdbLog -match "\bpush\b") -and $null -eq $r.ModsTree) "device Mods tree untouched"

    # 18. push requires an explicit serial even when exactly one device is attached
    $r = Invoke-Case (Merge @{}) @{ Push = $true; ModsSource = $modsSource }
    Check "push_requires_explicit_serial" ($r.ExitCode -eq 3 -and $r.Output -match "push-requires-explicit-serial") ("exit=" + $r.ExitCode)

    # 19. source directory validation
    $r = Invoke-Case (Merge @{}) @{ Push = $true; Serial = "emulator-5554"; ModsSource = (Join-Path $tmp "nope") }
    Check "push_missing_source_dir_refused" ($r.ExitCode -eq 3 -and $r.Output -match "mods-source-directory-missing") ("exit=" + $r.ExitCode)

    $r = Invoke-Case (Merge @{}) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $emptySource }
    Check "push_empty_source_refused" ($r.ExitCode -eq 3 -and $r.Output -match "mods-source-empty") ("exit=" + $r.ExitCode)

    $r = Invoke-Case (Merge @{}) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $bareSource }
    Check "push_missing_runtime_dll_refused" ($r.ExitCode -eq 3 -and $r.Output -match "missing-mod-directory") ("exit=" + $r.ExitCode)

    $r = Invoke-Case (Merge @{}) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $noManifestSource }
    Check "push_missing_manifest_refused" ($r.ExitCode -eq 3 -and $r.Output -match "missing-manifest") ("exit=" + $r.ExitCode)

    # 20. a reparse point in the source tree is rejected before anything is pushed
    if ($linkCreated) {
        $r = Invoke-Case (Merge @{}) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $linkSource }
        Check "push_reparse_point_refused" ($r.ExitCode -eq 3 -and $r.Output -match "reparse-point") ("exit=" + $r.ExitCode)
        Check "push_reparse_point_pushes_nothing" (-not ($r.AdbLog -match "\bpush\b")) "no push was attempted"
    } else {
        Check "push_reparse_point_refused" $true "SKIPPED: symlink creation not permitted on this host"
        Check "push_reparse_point_pushes_nothing" $true "SKIPPED: depends on the symlink case"
    }

    # ---------------------------------------------------------------- Phase 1.1.5: recursive tree
    # 21. the complete tree is reproduced with every relative path preserved
    $r = Invoke-Case (Merge @{}) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
    $localTree = Get-RelativeTree $modsSource
    $remoteTree = $r.ModsTree
    Check "recursive_push_succeeds" ($r.ExitCode -eq 0 -and $remoteTree -ne $null) ("exit=" + $r.ExitCode)
    $missing = @()
    $mismatch = @()
    if ($remoteTree) {
        foreach ($rel in $localTree.Keys) {
            if (-not $remoteTree.ContainsKey($rel)) { $missing += $rel; continue }
            if ($remoteTree[$rel] -ne $localTree[$rel]) { $mismatch += $rel }
        }
    }
    Check "recursive_all_paths_present" ($missing.Count -eq 0) ("missing=" + $missing.Count)
    Check "recursive_local_remote_hash_equal" ($mismatch.Count -eq 0) ("mismatched=" + $mismatch.Count)
    Check "recursive_file_count_matches" ($remoteTree -ne $null -and $remoteTree.Count -eq $localTree.Count) ("local=" + $localTree.Count + " remote=" + $(if ($remoteTree) { $remoteTree.Count } else { 0 }))

    # 22. manifest sits in its own mod directory, not in the Mods root
    Check "manifest_in_mod_subdirectory" ($remoteTree -ne $null -and $remoteTree.ContainsKey("P114.RuntimeProbe/manifest.json")) "Mods/P114.RuntimeProbe/manifest.json"
    Check "manifest_not_in_mods_root" ($remoteTree -ne $null -and -not $remoteTree.ContainsKey("manifest.json")) "no manifest.json at the Mods root"
    Check "runtime_dll_in_mods_root" ($remoteTree -ne $null -and $remoteTree.ContainsKey("ModFramework.Runtime.dll")) "ModFramework.Runtime.dll at the Mods root"

    # 23. every probe fixture landed in its own subdirectory
    $fixturePaths = @(
        "P114.RuntimeProbe/probe-fixtures/same-a/P114Same.dll",
        "P114.RuntimeProbe/probe-fixtures/same-b/P114Same.dll",
        "P114.RuntimeProbe/probe-fixtures/diff-a/P114Diff.dll",
        "P114.RuntimeProbe/probe-fixtures/diff-b/P114Diff.dll",
        "P114.RuntimeProbe/probe-fixtures/alpha/P114Alpha.dll",
        "P114.RuntimeProbe/probe-fixtures/beta/P114Beta.dll",
        "P114.RuntimeProbe/probe-fixtures/corrupt/P114Broken.dll",
        "P114.RuntimeProbe/probe-fixtures/nested/deep/one.txt"
    )
    $fixtureMissing = @()
    foreach ($p in $fixturePaths) {
        if (-not $remoteTree -or -not $remoteTree.ContainsKey($p)) { $fixtureMissing += $p }
    }
    Check "probe_fixtures_all_deployed" ($fixtureMissing.Count -eq 0) ("missing=" + $fixtureMissing.Count)
    Check "probe_fixtures_deep_nesting" ($remoteTree -ne $null -and $remoteTree.ContainsKey("P114.RuntimeProbe/probe-fixtures/nested/deep/one.txt")) "three-level nesting preserved"

    # 24. same bytes appear twice under one identity; different bytes stay different
    $sameA = if ($remoteTree) { $remoteTree["P114.RuntimeProbe/probe-fixtures/same-a/P114Same.dll"] } else { "" }
    $sameB = if ($remoteTree) { $remoteTree["P114.RuntimeProbe/probe-fixtures/same-b/P114Same.dll"] } else { "" }
    $diffA = if ($remoteTree) { $remoteTree["P114.RuntimeProbe/probe-fixtures/diff-a/P114Diff.dll"] } else { "" }
    $diffB = if ($remoteTree) { $remoteTree["P114.RuntimeProbe/probe-fixtures/diff-b/P114Diff.dll"] } else { "" }
    Check "fixture_same_bytes_identical" ($sameA -and $sameA -eq $sameB) "same-a == same-b"
    Check "fixture_diff_bytes_differ" ($diffA -and $diffB -and $diffA -ne $diffB) "diff-a != diff-b"

    # 25. staging was removed after a successful deployment
    Check "staging_cleaned_on_success" ((Get-StagingDirs $r.DevRoot).Count -eq 0) "no svmods-* directory remains"
    Check "push_reports_hash_verification" ($r.Output -match "verify-staged\s+: 12/12" -and $r.Output -match "verify-remote\s+: 12/12") "staged and final digests verified"
    Check "push_reports_owner_verification" ($r.Output -match "verify-owner\s+: 12/12" -and $r.Output -match "10152:10152") "files owned by the app uid"

    # ---------------------------------------------------------------- Phase 1.1.5: overwrite rules
    # 26. an identical existing file is skipped, not rewritten
    $r = Invoke-Case (Merge @{}) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
    $firstPushes = ([regex]::Matches($r.AdbLog, "\[adb\] -s emulator-5554 push")).Count
    # Deploy the same source a second time onto the SAME simulated device.
    $second = Invoke-Case (Merge @{}) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource; DeviceRoot = $r.DevRoot }
    Check "first_deploy_pushes_every_file" ($firstPushes -eq 12) ("pushes=" + $firstPushes)
    $secondOut = $second.Output
    $secondPushes = ([regex]::Matches($second.AdbLog, "\[adb\] -s emulator-5554 push")).Count
    Check "second_deploy_reports_identical" ($second.ExitCode -eq 0 -and $secondOut -match "ALREADY-PRESENT") ("exit=" + $second.ExitCode)
    Check "second_deploy_pushes_nothing" ($secondPushes -eq 0) ("pushes=" + $secondPushes)

    # 27. a pre-existing DIFFERENT file fails closed and is left untouched
    $conflictDev = Join-Path $tmp ("devconf-" + [Guid]::NewGuid().ToString("N").Substring(0, 6))
    $conflictMods = Join-Path $conflictDev ("data\data\com.chucklefish.stardewvalley\files\Mods\P114.RuntimeProbe")
    [void][System.IO.Directory]::CreateDirectory($conflictMods)
    Set-Content -LiteralPath (Join-Path $conflictMods "P114.RuntimeProbe.dll") -Value "FOREIGN-BYTES" -Encoding ascii
    $r = Invoke-Case (Merge @{}) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource; DeviceRoot = $conflictDev }
    Check "conflict_fails_closed" ($r.ExitCode -eq 3 -and $r.Output -match "target-file-hash-conflict") ("exit=" + $r.ExitCode)
    $conflictContent = Get-Content -LiteralPath (Join-Path $conflictMods "P114.RuntimeProbe.dll") -Raw
    Check "conflict_leaves_target_untouched" ($conflictContent.Trim() -eq "FOREIGN-BYTES") "the existing file was not overwritten"
    Check "conflict_pushes_nothing" (-not ($r.AdbLog -match "\bpush\b")) "no staging push happened"
    Check "conflict_cleans_staging" ((Get-StagingDirs $r.DevRoot).Count -eq 0) "no staging directory remains"

    # 28. -OverwriteModsApproved replaces the differing file
    $overDev = Join-Path $tmp ("devover-" + [Guid]::NewGuid().ToString("N").Substring(0, 6))
    $overMods = Join-Path $overDev ("data\data\com.chucklefish.stardewvalley\files\Mods\P114.RuntimeProbe")
    [void][System.IO.Directory]::CreateDirectory($overMods)
    Set-Content -LiteralPath (Join-Path $overMods "P114.RuntimeProbe.dll") -Value "FOREIGN-BYTES" -Encoding ascii
    $r = Invoke-Case (Merge @{}) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource; Overwrite = $true; DeviceRoot = $overDev }
    $overContent = if (Test-Path -LiteralPath (Join-Path $overMods "P114.RuntimeProbe.dll")) { Get-Content -LiteralPath (Join-Path $overMods "P114.RuntimeProbe.dll") -Raw } else { "" }
    Check "overwrite_approved_replaces" ($r.ExitCode -eq 0 -and $overContent.Trim() -eq "probe-bytes") ("exit=" + $r.ExitCode)
    Check "overwrite_approved_never_uninstalls" (Test-NoDestructive $r.AdbLog) "no destructive verb"

    # ---------------------------------------------------------------- Phase 1.1.5: odd names
    # 29. spaces, brackets, parentheses and non-ASCII survive the whole path pipeline
    $r = Invoke-Case (Merge @{}) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $oddMods }
    $oddRemote = $r.ModsTree
    $oddKey = "P114.RuntimeProbe/probe-fixtures/sub [" + $han + "] (1)/P114 Same.dll"
    Check "odd_mods_names_deployed" ($r.ExitCode -eq 0 -and $oddRemote -ne $null -and ($oddRemote.Keys -contains $oddKey)) ("exit=" + $r.ExitCode)
    $oddLocalTree = Get-RelativeTree $oddMods
    $oddHashOk = $oddRemote -ne $null -and $oddRemote.ContainsKey($oddKey) -and $oddRemote[$oddKey] -eq $oddLocalTree["P114.RuntimeProbe/probe-fixtures/sub [" + $han + "] (1)/P114 Same.dll"]
    Check "odd_mods_names_hash_equal" ($oddHashOk) "digest preserved through quoting"

    # ---------------------------------------------------------------- Phase 1.1.5: failure paths
    # 30. a device-side push failure stops the run and cleans staging
    $r = Invoke-Case (Merge @{ FAKE_PUSH_FAIL = "P114Alpha.dll" }) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
    Check "push_failure_stops" ($r.ExitCode -eq 4 -and $r.Output -match "reason=push-failed") ("exit=" + $r.ExitCode)
    Check "push_failure_cleans_staging" ((Get-StagingDirs $r.DevRoot).Count -eq 0) "no staging directory remains"
    Check "push_failure_copies_nothing" ($null -eq $r.ModsTree) "no file reached the final directory"

    # 31. a copy failure stops the run and cleans staging
    $r = Invoke-Case (Merge @{ FAKE_COPY_FAIL = "1" }) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
    Check "copy_failure_stops" ($r.ExitCode -eq 4 -and $r.Output -match "reason=copy-failed") ("exit=" + $r.ExitCode)
    Check "copy_failure_cleans_staging" ((Get-StagingDirs $r.DevRoot).Count -eq 0) "no staging directory remains"

    # 32. a staging directory that cannot be created stops before any transfer
    $r = Invoke-Case (Merge @{ FAKE_MKDIR_FAIL = "1" }) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
    Check "mkdir_failure_stops" ($r.ExitCode -eq 4 -and $r.Output -match "staging-directory-create-failed") ("exit=" + $r.ExitCode)
    Check "mkdir_failure_pushes_nothing" (-not ($r.AdbLog -match "\bpush\b")) "no file was transferred"

    # 33. ownership that does not take effect is a hard failure, not a success
    $r = Invoke-Case (Merge @{ FAKE_OWNER_MODS_FILES = "0:0" }) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
    Check "ownership_verified_or_stopped" ($r.ExitCode -eq 4 -and $r.Output -match "ownership-or-permission-unverified") ("exit=" + $r.ExitCode)

    # ---------------------------------------------------------------- Phase 1.1.5: device gates
    # 34. package not installed
    $r = Invoke-Case (Merge @{ FAKE_PMPATH = "" }) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
    Check "push_package_not_installed_refused" ($r.ExitCode -eq 3 -and $r.Output -match "package-not-installed") ("exit=" + $r.ExitCode)

    # 35. wrong game version
    $r = Invoke-Case (Merge @{ FAKE_DUMPSYS = "versionCode=238;versionName=1.6.14.0" }) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
    Check "push_version_mismatch_refused" ($r.ExitCode -eq 3 -and $r.Output -match "package-version-mismatch") ("exit=" + $r.ExitCode)
    Check "push_version_mismatch_pushes_nothing" (-not ($r.AdbLog -match "\bpush\b")) "no file was transferred"

    # 36. device APK does not match the local build
    $r = Invoke-Case (Merge @{ FAKE_SEED_APK = "other" }) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
    Check "push_apk_hash_mismatch_refused" ($r.ExitCode -eq 3 -and $r.Output -match "device-apk-hash-mismatch") ("exit=" + $r.ExitCode)
    Check "push_apk_hash_mismatch_pushes_nothing" (-not ($r.AdbLog -match "\bpush\b")) "no file was transferred"

    # 37. the app uid cannot be read
    $r = Invoke-Case (Merge @{ FAKE_STAT_FAIL = "1" }) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
    Check "push_uid_unreadable_refused" ($r.ExitCode -eq 3 -and $r.Output -match "app-uid-unreadable") ("exit=" + $r.ExitCode)

    # 37b. a required device command is missing: stop before uploading anything
    $r = Invoke-Case (Merge @{ FAKE_MISSING_TOOL = "chown" }) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
    Check "push_missing_device_tool_refused" ($r.ExitCode -eq 3 -and $r.Output -match "required-device-command-missing") ("exit=" + $r.ExitCode)
    Check "push_missing_device_tool_pushes_nothing" (-not ($r.AdbLog -match "\bpush\b")) "no file was transferred"

    # 38. a Mods root owned by another uid is refused instead of silently chowned
    $preDev = Join-Path $tmp ("devpre-" + [Guid]::NewGuid().ToString("N").Substring(0, 6))
    $preMods = Join-Path $preDev ("data\data\com.chucklefish.stardewvalley\files\Mods\OtherMod")
    [void][System.IO.Directory]::CreateDirectory($preMods)
    Set-Content -LiteralPath (Join-Path $preMods "manifest.json") -Value '{"id":"OtherMod"}' -Encoding ascii
    $r = Invoke-Case (Merge @{ FAKE_MODS_OWNER = "0:0" }) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource; DeviceRoot = $preDev }
    Check "mods_root_wrong_owner_refused" ($r.ExitCode -eq 3 -and $r.Output -match "mods-root-not-app-owned") ("exit=" + $r.ExitCode)
    $otherIntact = Test-Path -LiteralPath (Join-Path $preMods "manifest.json")
    Check "other_mod_left_intact" ($otherIntact -and -not ($r.AdbLog -match "\bpush\b")) "the other mod was neither removed nor modified"

    # 39. every push scenario stays non-destructive
    $r = Invoke-Case (Merge @{}) @{ Push = $true; Serial = "emulator-5554"; ModsSource = $modsSource }
    Check "push_never_uninstalls" (Test-NoDestructive $r.AdbLog) "no uninstall / pm clear / force-stop / am start"
    Check "push_never_launches_game" (-not ($r.AdbLog -match "am start|monkey")) "the game was not started"
    Check "push_never_touches_saves" (-not ($r.AdbLog -match "Saves")) "Saves are not read or written"
    Check "push_never_installs_apk" (-not ($r.AdbLog -match "\binstall\b")) "no APK install was attempted"
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
