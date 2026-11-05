# Self-test for Tools/device-precheck.ps1 adb discovery (no device required).
#
# Covers the six situations the audit asked for:
#   PATH, ANDROID_HOME, ANDROID_SDK_ROOT, LOCALAPPDATA fallback,
#   no adb at all, and multiple conflicting candidates.
# Plus: an explicit -Adb wins, and personal home paths are redacted in output.
#
# Each case runs in its own pwsh process with a controlled environment, so the
# operator's real PATH / SDK variables can never influence the result.
#
# Usage (repo root):
#   pwsh -NoProfile -File Tools/tests/test_device_precheck.ps1
# Exit code 0 = every check passed.

$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $here "..\..")).Path
$precheck = Join-Path $root "Tools\device-precheck.ps1"
$pwshExe = (Get-Process -Id $PID).Path

$script:results = New-Object System.Collections.Generic.List[object]

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    $tag = if ($ok) { "PASS" } else { "FAIL" }
    Write-Host ("{0} {1,-42} {2}" -f $tag, $name, $detail)
}

function Skip([string]$name, [string]$why) {
    $script:results.Add([pscustomobject]@{ Name = $name; Ok = $true; Detail = "SKIPPED: $why" })
    Write-Host ("SKIP {0,-42} {1}" -f $name, $why)
}

$isWindowsHost = $IsWindows -or ($env:OS -eq "Windows_NT")
$adbName = if ($isWindowsHost) { "adb.exe" } else { "adb" }

# A scratch tree: <tmp>/home/AppData/Local/Android/Sdk/platform-tools/adb.exe,
# <tmp>/sdkhome/platform-tools/adb.exe, <tmp>/sdkroot/platform-tools/adb.exe,
# <tmp>/onpath/adb.exe, <tmp>/empty (PATH with no adb).
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("sv-precheck-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$fakeHome = Join-Path $tmp "home"
$localAppData = Join-Path (Join-Path $fakeHome "AppData") "Local"
$sdkFromLocal = Join-Path (Join-Path (Join-Path $localAppData "Android") "Sdk") "platform-tools"
$sdkHome = Join-Path $tmp "sdkhome\platform-tools"
$sdkRoot = Join-Path $tmp "sdkroot\platform-tools"
$onPath = Join-Path $tmp "onpath"
$empty = Join-Path $tmp "empty"
foreach ($d in @($sdkFromLocal, $sdkHome, $sdkRoot, $onPath, $empty)) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}
foreach ($d in @($sdkFromLocal, $sdkHome, $sdkRoot, $onPath)) {
    Set-Content -Path (Join-Path $d $adbName) -Value "# fake adb for tests" -Encoding ascii
}
$explicitAdb = Join-Path $tmp ("explicit\" + $adbName)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $explicitAdb) | Out-Null
Set-Content -Path $explicitAdb -Value "# fake adb for tests" -Encoding ascii

# Runs the precheck in a fresh pwsh with exactly the environment we hand it.
# $argLine is raw command-line text: a quoted switch name would bind as a
# positional string value, so switches must stay unquoted here.
function Invoke-Case([hashtable]$env, [string]$argLine) {
    $wrapper = Join-Path $tmp ("case-" + [Guid]::NewGuid().ToString("N").Substring(0, 6) + ".ps1")
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('$ErrorActionPreference = "Continue"')
    foreach ($k in $env.Keys) {
        $v = [string]$env[$k]
        [void]$sb.AppendLine(('$env:{0} = ''{1}''' -f $k, ($v -replace "'", "''")))
    }
    [void]$sb.AppendLine(('& ''{0}'' {1}' -f ($precheck -replace "'", "''"), $argLine))
    [void]$sb.AppendLine('exit $LASTEXITCODE')
    Set-Content -Path $wrapper -Value $sb.ToString() -Encoding utf8

    $out = & $pwshExe -NoProfile -ExecutionPolicy Bypass -File $wrapper 2>&1 | Out-String
    return [pscustomobject]@{ Output = $out; ExitCode = $LASTEXITCODE }
}

$base = @{
    PATH = $empty
    ANDROID_HOME = ""
    ANDROID_SDK_ROOT = ""
    ADB_PATH = ""
    LOCALAPPDATA = ""
    USERPROFILE = $fakeHome
    HOME = $fakeHome
}

function Merge([hashtable]$overrides) {
    $h = @{}
    foreach ($k in $base.Keys) { $h[$k] = $base[$k] }
    foreach ($k in $overrides.Keys) { $h[$k] = $overrides[$k] }
    return $h
}

try {
    # 1. PATH
    $r = Invoke-Case (Merge @{ PATH = $onPath }) "-ResolveAdbOnly"
    Check "adb_from_path" ($r.ExitCode -eq 0 -and $r.Output -match "selected\s+.*\(PATH\)") ("exit=" + $r.ExitCode)

    # 2. ANDROID_HOME
    $r = Invoke-Case (Merge @{ ANDROID_HOME = (Join-Path $tmp "sdkhome") }) "-ResolveAdbOnly"
    Check "adb_from_android_home" ($r.ExitCode -eq 0 -and $r.Output -match "selected\s+.*\(ANDROID_HOME\)") ("exit=" + $r.ExitCode)

    # 3. ANDROID_SDK_ROOT
    $r = Invoke-Case (Merge @{ ANDROID_SDK_ROOT = (Join-Path $tmp "sdkroot") }) "-ResolveAdbOnly"
    Check "adb_from_android_sdk_root" ($r.ExitCode -eq 0 -and $r.Output -match "selected\s+.*\(ANDROID_SDK_ROOT\)") ("exit=" + $r.ExitCode)

    # 4. LOCALAPPDATA fallback (Windows layout only) + home redaction
    if ($isWindowsHost) {
        $r = Invoke-Case (Merge @{ LOCALAPPDATA = $localAppData }) "-ResolveAdbOnly"
        Check "adb_from_localappdata" ($r.ExitCode -eq 0 -and $r.Output -match "selected\s+.*\(LOCALAPPDATA\)") ("exit=" + $r.ExitCode)
        $redacted = ($r.Output -notmatch [regex]::Escape($fakeHome)) -and ($r.Output -match "<home>")
        Check "home_path_redacted" $redacted "output must show <home>, not the real profile"
    } else {
        Skip "adb_from_localappdata" "LOCALAPPDATA is a Windows-only layout"
        Skip "home_path_redacted" "covered by the Windows case"
    }

    # 5. no adb anywhere -> actionable BLOCKED, exit 2, no guessed user directory
    $r = Invoke-Case (Merge @{}) "-ResolveAdbOnly"
    $actionable = ($r.Output -match "no-adb") -and ($r.Output -match "ANDROID_HOME")
    Check "adb_missing_is_blocked" ($r.ExitCode -eq 2 -and $actionable) ("exit=" + $r.ExitCode)
    $leaksUserDir = $r.Output -match '(?i)[A-Z]:\\Users\\'
    Check "no_guessed_user_directory" (-not $leaksUserDir) "output must not contain a C:\Users\... guess"

    # 6. conflicting candidates -> highest priority wins and the conflict is reported
    $r = Invoke-Case (Merge @{ PATH = $onPath; ANDROID_HOME = (Join-Path $tmp "sdkhome"); ANDROID_SDK_ROOT = (Join-Path $tmp "sdkroot") }) "-ResolveAdbOnly"
    Check "adb_conflict_reported" ($r.ExitCode -eq 0 -and $r.Output -match "adb-conflict" -and $r.Output -match "selected\s+.*\(PATH\)") ("exit=" + $r.ExitCode)

    # 7. explicit -Adb wins over PATH
    $r = Invoke-Case (Merge @{ PATH = $onPath }) ("-ResolveAdbOnly -Adb '" + $explicitAdb + "'")
    Check "explicit_adb_wins" ($r.ExitCode -eq 0 -and $r.Output -match "selected\s+.*\(argument\)") ("exit=" + $r.ExitCode)

    # 8. static: the script must not contain a hardcoded user profile path
    $text = [System.IO.File]::ReadAllText($precheck)
    $hardcoded = $text -match '(?i)[A-Z]:\\Users\\[^\\\s"'']+'
    Check "no_hardcoded_user_path_in_script" (-not $hardcoded) "source must resolve adb from PATH/env only"
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
