[CmdletBinding()]
param(
    [Alias("Command")]
    [string]$Action = "",
    [Alias("ModsSource")]
    [string]$Source = "",
    [string]$Serial = "",
    [string]$PackageName = "com.chucklefish.stardewvalley",
    [string]$Adb = "",
    [string]$TxId = "",
    [string[]]$Overwrite = @(),
    [switch]$Apply,
    [switch]$StrictExtras,
    [switch]$Json,
    [string]$JsonOut = "",
    [string]$ReportOut = "",
    [switch]$Help
)

$ErrorActionPreference = "Stop"

# `adb shell` output is UTF-8 (Android file names are UTF-8 bytes passed through unchanged), while
# Windows PowerShell 5.1 decodes native stdout with the console OEM code page. On a non-UTF-8 code
# page that turns every non-ASCII device path into mojibake, so a perfectly good staged file looks
# like a hash mismatch ("staging-hash-mismatch") and the tool fails closed for the wrong reason.
# Decoding as UTF-8 here keeps the path round-trip byte-exact; a wrong guess still fails closed.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$script:ToolVersion = "1.2.3"
$script:FixedPackage = "com.chucklefish.stardewvalley"
$script:FilesRoot = "/sdcard/Android/data/com.chucklefish.stardewvalley/files"
$script:ModsRoot = "/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods"
$script:TransactionRoot = "/sdcard/Android/data/com.chucklefish.stardewvalley/files/.mods-deploy"
$script:TxIdPattern = '^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$'
$script:ActiveStates = @("prepared", "staging-verified", "applying", "rolling-back", "rollback-failed", "recovery-required")
$script:SerialExplicit = [bool]$Serial
$script:Lines = New-Object System.Collections.Generic.List[string]
$script:AdbBin = ""
$script:SelectedSerial = ""
# Scratch space for the transient journal.json.new upload. The authoritative journal lives on the
# device, so this host copy is disposable and must never land inside the project tree.
$script:WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) "deploy-mods-safe"
$script:Result = [ordered]@{
    schemaVersion = 1; tool = "deploy-mods-safe"; command = ""; mode = "read-only"
    generatedAt = (Get-Date).ToUniversalTime().ToString("o"); exitCode = 0; status = "ok"
    category = ""; reason = ""; detail = ""; device = [ordered]@{ serial = ""; state = ""; apiLevel = 0; androidRelease = ""; model = ""; explicitSerial = [bool]$Serial }
    package = [ordered]@{ name = $script:FixedPackage; installed = $false; versionCode = ""; versionName = ""; running = $false; pid = "" }
    storage = [ordered]@{ modsRoot = $script:ModsRoot; transactionRoot = $script:TransactionRoot; scopedStorageRisk = ""; safSupported = $false; writeProbe = "not-run" }
    plan = [ordered]@{ fileCount = 0; totalBytes = 0; files = @(); directories = @(); conflicts = @(); overwriteApproved = @() }
    journal = [ordered]@{ txid = ""; path = ""; state = ""; createdAt = ""; committedAt = "" }
    transactions = @()
    verification = [ordered]@{ expectedFiles = 0; matchedFiles = 0; missing = @(); modified = @(); extra = @(); symlinks = @(); unparseable = @(); strictExtras = [bool]$StrictExtras }
    safety = [ordered]@{ deviceTouched = $false; writesSaves = $false; usesRoot = $false; installsApk = $false; forceStopsGame = $false; deletesUnknownFiles = $false }
}

function Show-Help {
    @"
deploy-mods-safe.ps1 -Action inspect|plan|deploy|verify|rollback
  -Source <complete Mods root>  required by plan/deploy and verify without TxId
  -Serial <device serial>       required for every apply; never guessed for writes
  -Apply                        explicitly authorize deploy/rollback writes (default is dry-run)
  -TxId <transaction id>        select a journal for verify/rollback
  -Overwrite <relative path>    explicitly authorize replacement (repeat or comma-separate)
  -Adb <fake or installed adb path>
  -Json                         emit a machine-readable JSON result
  -JsonOut <host file>          also write that JSON result to a host file (report copy only)
  -ReportOut <host file>        also write the human-readable log to a host file
  -StrictExtras                 make extra files fail verify

No arguments prints this help. Only the fixed external Mods path is writable.
"@ | Write-Output
}

function Say([string]$text) {
    $script:Lines.Add($text)
    if (-not $Json) { Write-Host $text }
}

function Write-HostTextFile([string]$path, [string]$text) {
    # Evidence files only: the authoritative transaction journal stays on the device.
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { [void][System.IO.Directory]::CreateDirectory($dir) }
    $temp = $path + ".tmp-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
    [System.IO.File]::WriteAllText($temp, $text, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temp -Destination $path -Force
}

function Exit-Tool([int]$code, [string]$status, [string]$category, [string]$reason, [string]$detail) {
    $script:Result.exitCode = $code
    $script:Result.status = $status
    $script:Result.category = $category
    $script:Result.reason = $reason
    $script:Result.detail = $detail
    $jsonText = $script:Result | ConvertTo-Json -Depth 20 -Compress
    if ($Json) {
        $jsonText | Write-Output
    } else {
        Say ("result          : " + $status + $(if ($reason) { " reason=" + $reason } else { "" }))
        if ($detail) { Say ("detail          : " + $detail) }
    }
    if ($JsonOut) {
        try { Write-HostTextFile $JsonOut $jsonText }
        catch { Write-Warning ("json-out-failed: " + $_.Exception.Message + "; the device journal is unaffected") }
    }
    if ($ReportOut) {
        try { Write-HostTextFile $ReportOut (($script:Lines -join "`r`n") + "`r`n") }
        catch { Write-Warning ("report-out-failed: " + $_.Exception.Message) }
    }
    exit $code
}

function Quote-Posix([string]$value) {
    return "'" + $value.Replace("'", "'\''") + "'"
}

function Resolve-Adb([string]$explicit) {
    if ($explicit) {
        if (Test-Path -LiteralPath $explicit -PathType Leaf) { return [pscustomobject]@{ Ok = $true; Path = [System.IO.Path]::GetFullPath($explicit) } }
        return [pscustomobject]@{ Ok = $false; Path = "" }
    }
    $found = Get-Command adb -ErrorAction SilentlyContinue
    if ($found -and $found.Source) { return [pscustomobject]@{ Ok = $true; Path = $found.Source } }
    foreach ($root in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "Android\Sdk" }))) {
        if (-not $root) { continue }
        $candidate = Join-Path $root "platform-tools\adb.exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [pscustomobject]@{ Ok = $true; Path = $candidate } }
    }
    return [pscustomobject]@{ Ok = $false; Path = "" }
}

function Invoke-Tool([string]$tool, [string[]]$argv) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $out = @(& $tool @argv 2>&1)
        $code = $LASTEXITCODE
        if ($null -eq $code) { $code = 0 }
        return [pscustomobject]@{ Exit = [int]$code; Text = (($out | Out-String) -replace "`r", "").Trim() }
    } catch {
        return [pscustomobject]@{ Exit = 127; Text = $_.Exception.Message }
    } finally { $ErrorActionPreference = $old }
}

function Adb([string[]]$argv) {
    return Invoke-Tool $script:AdbBin $argv
}

function Adb-Shell([string]$command) {
    if (-not $script:SelectedSerial) { return [pscustomobject]@{ Exit = 2; Text = "serial-not-selected" } }
    return Adb @("-s", $script:SelectedSerial, "shell", $command)
}

function Test-RemoteAllowed([string]$path) {
    if ($path -eq $script:ModsRoot -or $path.StartsWith($script:ModsRoot + "/", [StringComparison]::Ordinal)) { return $true }
    if ($path -eq $script:TransactionRoot -or $path.StartsWith($script:TransactionRoot + "/", [StringComparison]::Ordinal)) { return $true }
    if ($path -eq $script:FilesRoot) { return $true }
    return $false
}

function Assert-RemoteAllowed([string]$path) {
    if (-not (Test-RemoteAllowed $path)) { throw "remote-path-outside-allowlist" }
    if ($path -match '(^|/)\.\.(/|$)|[\r\n\0]') { throw "unsafe-remote-path" }
}

function Invoke-Remote([string]$command) {
    # All callers construct commands from fixed verbs and POSIX-quoted allowlisted paths.
    return Adb-Shell $command
}

function Get-DeviceContext {
    $r = Adb @("devices", "-l")
    if ($r.Exit -ne 0) { Exit-Tool 2 "blocked" "EnvironmentBlocked" "device-list-failed" "adb devices failed" }
    $entries = New-Object System.Collections.Generic.List[object]
    foreach ($line in ($r.Text -split "`r?`n")) {
        if ($line -match '^(\S+)\s+(device|offline|unauthorized)\b') {
            $entries.Add([pscustomobject]@{ Serial = $Matches[1]; State = $Matches[2] })
        }
    }
    if ($Serial) {
        # $Matches is an automatic variable; shadowing it here would survive only until the next
        # successful -match, so use a plain name instead.
        $hits = @($entries | Where-Object { $_.Serial -eq $Serial })
        if ($hits.Count -ne 1) { Exit-Tool 2 "blocked" "EnvironmentBlocked" "serial-not-attached" "explicit serial is not uniquely attached" }
        if ($hits[0].State -ne "device") { Exit-Tool 2 "blocked" "EnvironmentBlocked" "serial-not-online" ("device state is " + $hits[0].State) }
        $script:SelectedSerial = $Serial
    } else {
        if ($entries.Count -eq 0) { Exit-Tool 2 "blocked" "EnvironmentBlocked" "no-device" "no attached device" }
        if ($entries.Count -ne 1) { Exit-Tool 2 "blocked" "EnvironmentBlocked" "ambiguous-device" "pass -Serial to select exactly one device" }
        if ($entries[0].State -ne "device") { Exit-Tool 2 "blocked" "EnvironmentBlocked" "serial-not-online" ("device state is " + $entries[0].State) }
        $script:SelectedSerial = $entries[0].Serial
    }
    $script:Result.device.serial = $script:SelectedSerial
    $script:Result.device.state = "device"
    $script:Result.device.explicitSerial = [bool]$Serial
    foreach ($pair in @(@("ro.build.version.sdk", "apiLevel"), @("ro.build.version.release", "androidRelease"), @("ro.product.model", "model"))) {
        $prop = Adb-Shell ("getprop " + $pair[0])
        if ($prop.Exit -ne 0 -or -not $prop.Text) { Exit-Tool 2 "blocked" "EnvironmentBlocked" "device-property-unreadable" $pair[0] }
        if ($pair[1] -eq "apiLevel") {
            $api = 0
            if (-not [int]::TryParse($prop.Text.Trim(), [ref]$api) -or $api -lt 1) { Exit-Tool 2 "blocked" "EnvironmentBlocked" "api-level-unreadable" "device API level is not a positive integer" }
            $script:Result.device.apiLevel = $api
            $script:Result.storage.scopedStorageRisk = if ($api -ge 30) { "unverified-api$api" } else { "verified-api29-or-lower" }
        } else { $script:Result.device[$pair[1]] = $prop.Text.Trim() }
    }
}

function Get-PackageState([switch]$RequireInstalled) {
    if ($PackageName -ne $script:FixedPackage) { Exit-Tool 3 "refused" "PolicyRefused" "package-name-mismatch" "PackageName is fixed to com.chucklefish.stardewvalley" }
    $path = Adb-Shell ("pm path " + $script:FixedPackage)
    if ($path.Exit -ne 0 -or $path.Text -notmatch '(?m)^package:\S+') {
        $script:Result.package.installed = $false
        if ($RequireInstalled) { Exit-Tool 3 "refused" "PolicyRefused" "package-not-installed" "the fixed game package is not installed" }
        return
    }
    $dump = Adb-Shell ("dumpsys package " + $script:FixedPackage)
    if ($dump.Exit -ne 0) { Exit-Tool 2 "blocked" "EnvironmentBlocked" "package-state-unreadable" "dumpsys package failed" }
    $vc = [regex]::Match($dump.Text, '(?m)\bversionCode=(\d+)')
    $vn = [regex]::Match($dump.Text, '(?m)\bversionName=([^\s]+)')
    if (-not $vc.Success -or -not $vn.Success) { Exit-Tool 2 "blocked" "EnvironmentBlocked" "package-version-unreadable" "package version could not be verified" }
    $script:Result.package.installed = $true
    $script:Result.package.name = $script:FixedPackage
    $script:Result.package.versionCode = $vc.Groups[1].Value
    $script:Result.package.versionName = $vn.Groups[1].Value
    if ($vc.Groups[1].Value -ne "239" -or $vn.Groups[1].Value -ne "1.6.15.0") {
        Exit-Tool 3 "refused" "PolicyRefused" "package-version-mismatch" "expected game version 239 / 1.6.15.0"
    }
    # $PID is a READ-ONLY automatic variable in Windows PowerShell 5.1 and PowerShell 7; assigning
    # to it throws SessionStateUnauthorizedAccessException and used to abort every command here.
    $processState = Adb-Shell ("pidof " + $script:FixedPackage)
    if ($processState.Exit -eq 0 -and $processState.Text -match '\d+') {
        $script:Result.package.running = $true
        $script:Result.package.pid = $Matches[0]
    } elseif ($processState.Exit -ne 0 -and $processState.Text -and $processState.Text -notmatch '(?i)(not found|no process|unknown command)') {
        Exit-Tool 2 "blocked" "EnvironmentBlocked" "process-state-unreadable" "could not establish whether the game is running"
    }
    if ($RequireInstalled -and $Apply -and $script:Result.package.running) {
        Exit-Tool 3 "refused" "PolicyRefused" "game-running" "close the game manually; this tool never stops it"
    }
}

function Get-StorageState {
    Assert-RemoteAllowed $script:FilesRoot
    $filesExists = Test-RemoteDir $script:FilesRoot
    $modsExists = Test-RemoteDir $script:ModsRoot
    $script:Result.storage.modsRoot = $script:ModsRoot
    $script:Result.storage.transactionRoot = $script:TransactionRoot
    if ($script:Result.device.apiLevel -ge 30 -and $Apply -and ($Action -eq "deploy" -or $Action -eq "rollback")) {
        Exit-Tool 2 "blocked" "StorageUnavailable" "unverified-android-storage" "Android 11+ has no project evidence; writes are fail-closed"
    }
    if ($Apply -and -not $filesExists) { Exit-Tool 2 "blocked" "StorageUnavailable" "package-data-missing" "app-specific external files directory is unavailable" }
    return [pscustomobject]@{ FilesExists = $filesExists; ModsExists = $modsExists }
}

function Test-RemoteDir([string]$path) {
    Assert-RemoteAllowed $path
    $r = Invoke-Remote ("test -d " + (Quote-Posix $path))
    return ($r.Exit -eq 0)
}

function Test-RemotePath([string]$path) {
    Assert-RemoteAllowed $path
    $r = Invoke-Remote ("test -e " + (Quote-Posix $path))
    return ($r.Exit -eq 0)
}

function Test-RemoteLink([string]$path) {
    if ($path -notin @("/sdcard", "/sdcard/Android", "/sdcard/Android/data", "/sdcard/Android/data/com.chucklefish.stardewvalley")) {
        Assert-RemoteAllowed $path
    }
    $r = Invoke-Remote ("test -L " + (Quote-Posix $path))
    return ($r.Exit -eq 0)
}

function Test-RemotePathChain([string]$path) {
    return (Test-RemotePathChains @($path))
}

# One batched, de-duplicated link check for one or more allowlisted paths.
#
# Two facts drive this shape:
#   * On Android `/sdcard` is normally a SYMLINK to /storage/emulated/0 (and `/sdcard/Android/...`
#     sits below it). Treating those OS-owned prefixes as an unsafe link would refuse every single
#     deployment on a real device, so the trusted boundary starts at the app-specific files root.
#     Everything at or below it is created by the app or by this tool and MUST NOT be a link.
#   * `adb shell` is one process per call, so checking the shared prefix once per operation instead
#     of once per path keeps a deploy from issuing hundreds of `test -L` round trips.
function Test-RemotePathChains([string[]]$paths) {
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($path in $paths) {
        if (-not $path) { continue }
        Assert-RemoteAllowed $path
        $current = ""
        foreach ($part in $path.Split('/')) {
            if (-not $part) { continue }
            $current += "/" + $part
            if (-not $current.StartsWith($script:FilesRoot, [StringComparison]::Ordinal)) { continue }
            if (-not $seen.Add($current)) { continue }
            if (Test-RemoteLink $current) { return $false }
        }
    }
    return $true
}

function Get-RemoteTargetState([string]$path) {
    Assert-RemoteAllowed $path
    if (-not (Test-RemotePathChain $path)) { return [pscustomobject]@{ Kind = "symlink"; Hash = "" } }
    if (-not (Test-RemotePath $path)) { return [pscustomobject]@{ Kind = "missing"; Hash = "" } }
    if (Test-RemoteDir $path) { return [pscustomobject]@{ Kind = "directory"; Hash = "" } }
    $map = Get-RemoteHashMap @($path)
    if ($null -eq $map -or -not $map.ContainsKey($path)) { return [pscustomobject]@{ Kind = "unknown"; Hash = "" } }
    return [pscustomobject]@{ Kind = "file"; Hash = $map[$path] }
}

function New-RemoteDirs([string[]]$paths) {
    $clean = @($paths | Where-Object { $_ } | Sort-Object Length)
    if (-not (Test-RemotePathChains $clean)) { return $false }
    foreach ($p in $clean) {
        Assert-RemoteAllowed $p
        if (Test-RemotePath $p) {
            if (-not (Test-RemoteDir $p)) { return $false }
            continue
        }
        $r = Invoke-Remote ("mkdir -p " + (Quote-Posix $p))
        if ($r.Exit -ne 0) { return $false }
    }
    return $true
}

function Set-RemoteMode([string]$path, [string]$mode) {
    Assert-RemoteAllowed $path
    if ($mode -notmatch '^0[0-7]{3}$') { return $false }
    return ((Invoke-Remote ("chmod " + $mode + " " + (Quote-Posix $path))).Exit -eq 0)
}

function Get-TxDir([string]$txid) {
    if ($txid -notmatch $script:TxIdPattern) { throw "invalid-txid" }
    return $script:TransactionRoot + "/" + $txid
}

function Get-StagingPath([string]$txid, [string]$rel) {
    Assert-RelativePath $rel
    return (Get-TxDir $txid) + "/staging/" + $rel
}
function Get-BackupPath([string]$txid, [string]$rel) {
    Assert-RelativePath $rel
    return (Get-TxDir $txid) + "/backup/" + $rel
}
function Get-RollbackPath([string]$txid, [string]$rel) {
    Assert-RelativePath $rel
    return (Get-TxDir $txid) + "/rollback/" + $rel
}

function Push-RemoteFile([string]$local, [string]$remote) {
    Assert-RemoteAllowed $remote
    return Adb @("-s", $script:SelectedSerial, "push", $local, $remote)
}

function Move-Remote([string]$from, [string]$to, [switch]$Force) {
    Assert-RemoteAllowed $from; Assert-RemoteAllowed $to
    if (-not (Test-RemotePathChains @($from, $to))) { return [pscustomobject]@{ Exit = 1; Text = "symlink in path" } }
    # `-f` is explicit for the journal replacement (design §4.2); POSIX mv already replaces files,
    # so it changes nothing on a real device but keeps the intent readable.
    $verb = if ($Force) { "mv -f " } else { "mv " }
    return Invoke-Remote ($verb + (Quote-Posix $from) + " " + (Quote-Posix $to))
}

function Set-RemoteJournalText([string]$path, [string]$text) {
    $local = Join-Path $script:WorkDir ("journal-" + [Guid]::NewGuid().ToString("N") + ".json")
    [void][System.IO.Directory]::CreateDirectory($script:WorkDir)
    [System.IO.File]::WriteAllText($local, $text, (New-Object System.Text.UTF8Encoding($false)))
    try {
        $push = Push-RemoteFile $local ($path + ".new")
        if ($push.Exit -ne 0) { return $false }
        $check = Get-RemoteHashMap @($path + ".new")
        $expected = (Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash.ToLower()
        if ($null -eq $check -or -not $check.ContainsKey($path + ".new") -or $check[$path + ".new"] -ne $expected) { return $false }
        return ((Move-Remote ($path + ".new") $path -Force).Exit -eq 0)
    } finally {
        Remove-Item -LiteralPath $local -Force -ErrorAction SilentlyContinue
    }
}

function Write-Journal($journal) {
    $dir = Get-TxDir ([string]$journal.txid)
    $path = $dir + "/journal.json"
    $json = $journal | ConvertTo-Json -Depth 30 -Compress
    return (Set-RemoteJournalText $path $json)
}

function Get-ReadJournal([string]$txid) {
    try {
        $path = (Get-TxDir $txid) + "/journal.json"
        $r = Invoke-Remote ("cat " + (Quote-Posix $path))
        if ($r.Exit -ne 0) { return [pscustomobject]@{ Ok = $false; Detail = "journal unreadable"; Journal = $null } }
        $journal = $r.Text | ConvertFrom-Json -ErrorAction Stop
        if ($journal.txid -ne $txid -or $journal.schemaVersion -ne 1 -or $journal.expected -isnot [array]) {
            return [pscustomobject]@{ Ok = $false; Detail = "journal shape or txid mismatch"; Journal = $null }
        }
        foreach ($e in @($journal.expected)) {
            Assert-RelativePath ([string]$e.path)
            if ([string]$e.sha256 -notmatch '^[0-9a-f]{64}$') { throw "invalid journal hash" }
        }
        return [pscustomobject]@{ Ok = $true; Detail = ""; Journal = $journal }
    } catch { return [pscustomobject]@{ Ok = $false; Detail = "journal validation failed"; Journal = $null } }
}

function Get-Transactions {
    $out = New-Object System.Collections.Generic.List[object]
    if (-not (Test-RemoteDir $script:TransactionRoot)) { return @() }
    $r = Invoke-Remote ("ls -1 " + (Quote-Posix $script:TransactionRoot))
    if ($r.Exit -ne 0 -and $r.Text) { return @([pscustomobject]@{ txid = "unknown"; state = "recovery-required"; journalReadable = $false }) }
    foreach ($id in ($r.Text -split "`r?`n" | Where-Object { $_ })) {
        if ($id -notmatch $script:TxIdPattern) { $out.Add([pscustomobject]@{ txid = $id; state = "recovery-required"; journalReadable = $false }); continue }
        $read = Get-ReadJournal $id
        if ($read.Ok) { $out.Add([pscustomobject]@{ txid = $id; state = [string]$read.Journal.state; journalReadable = $true }) }
        else { $out.Add([pscustomobject]@{ txid = $id; state = "recovery-required"; journalReadable = $false }) }
    }
    # Not `@($out)`: Windows PowerShell 5.1 throws "Argument types do not match" on List[object].
    return $out.ToArray()
}

function Assert-NoActiveTransaction {
    $active = @(Get-Transactions | Where-Object { $script:ActiveStates -contains [string]$_.state -or -not $_.journalReadable })
    if ($active.Count -gt 0) { Exit-Tool 5 "recovery-required" "RecoveryRequired" "active-transaction" "an unfinished or unreadable transaction requires explicit recovery" }
}

function Get-RequiredCommands {
    foreach ($name in @("sha256sum", "stat", "find", "mv", "mkdir", "cat", "chmod")) {
        $r = Invoke-Remote ("command -v " + $name)
        if ($r.Exit -ne 0) { Exit-Tool 2 "blocked" "StorageUnavailable" "device-command-missing" ("required device command missing: " + $name) }
    }
}

function Get-DiskAvailableKb {
    $r = Invoke-Remote ("df -k " + (Quote-Posix $script:FilesRoot))
    if ($r.Exit -ne 0) { return -1 }
    $lines = @($r.Text -split "`r?`n" | Where-Object { $_.Trim() })
    if ($lines.Count -lt 2) { return -1 }
    $cols = @($lines[-1] -split '\s+')
    if ($cols.Count -lt 4) { return -1 }
    $n = 0L
    if ([int64]::TryParse($cols[3], [ref]$n)) { return $n }
    return -1
}

function Get-RemotePlan($sourcePlan, [string[]]$approved) {
    $files = New-Object System.Collections.Generic.List[object]
    $conflicts = New-Object System.Collections.Generic.List[object]
    foreach ($f in $sourcePlan.Files) {
        $target = $script:ModsRoot + "/" + $f.Rel
        $state = Get-RemoteTargetState $target
        if ($state.Kind -eq "missing") { $action = "ADD"; $existing = "" }
        elseif ($state.Kind -eq "file" -and $state.Hash -eq $f.Sha256) { $action = "SKIP"; $existing = $state.Hash }
        elseif ($state.Kind -eq "file") {
            $action = "REPLACE"; $existing = $state.Hash
            $conflicts.Add([pscustomobject]@{ path = $f.Rel; existingSha256 = $state.Hash; newSha256 = $f.Sha256 })
        } else {
            return [pscustomobject]@{ Ok = $false; Reason = "target-conflict"; Detail = $f.Rel + " is " + $state.Kind; Files = @(); Conflicts = @(); Directories = $sourcePlan.Directories }
        }
        $files.Add([pscustomobject]@{ Path = $f.Rel; Full = $f.Full; Bytes = $f.Bytes; Sha256 = $f.Sha256; Action = $action; ExistingSha256 = $existing })
    }
    # NOTE: never wrap a List[object] in `@(...)` here. Windows PowerShell 5.1 throws
    # "Argument types do not match" for `@($genericListOfObject)`; List[string] and HashSet[string]
    # are unaffected. ToArray() is the portable spelling.
    $conflictPaths = @($conflicts | ForEach-Object { $_.path })
    foreach ($p in $approved) {
        if ($conflictPaths -notcontains $p) {
            return [pscustomobject]@{ Ok = $false; Reason = "overwrite-list-mismatch"; Detail = $p + " is not a conflict"; Files = $files.ToArray(); Conflicts = $conflicts.ToArray(); Directories = $sourcePlan.Directories }
        }
    }
    $notApproved = @($conflictPaths | Where-Object { $approved -notcontains $_ })
    if ($notApproved.Count -gt 0) {
        return [pscustomobject]@{ Ok = $false; Reason = "target-conflict"; Detail = ($notApproved -join ", "); Files = $files.ToArray(); Conflicts = $conflicts.ToArray(); Directories = $sourcePlan.Directories }
    }
    return [pscustomobject]@{ Ok = $true; Reason = ""; Detail = ""; Files = $files.ToArray(); Conflicts = $conflicts.ToArray(); Directories = $sourcePlan.Directories }
}

function Get-ApprovedOverwrites {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($item in @($Overwrite)) {
        foreach ($p in ($item -split ',')) {
            if ($p) { Assert-RelativePath $p; $out.Add($p) }
        }
    }
    if (@($out | Select-Object -Unique).Count -ne $out.Count) { Exit-Tool 3 "refused" "PolicyRefused" "overwrite-list-mismatch" "duplicate overwrite path" }
    return $out.ToArray()
}

function Set-PlanResult($sourcePlan, $remotePlan) {
    $files = @()
    foreach ($f in @($remotePlan.Files)) {
        $files += [pscustomobject]@{ path = $f.Path; bytes = $f.Bytes; sha256 = $f.Sha256; action = $f.Action; existingSha256 = $f.ExistingSha256 }
    }
    $script:Result.plan.fileCount = $files.Count
    $script:Result.plan.totalBytes = $sourcePlan.TotalBytes
    $script:Result.plan.files = $files
    $script:Result.plan.directories = @($remotePlan.Directories)
    $script:Result.plan.conflicts = @($remotePlan.Conflicts)
    $script:Result.plan.overwriteApproved = @(Get-ApprovedOverwrites)
}

function Show-Plan($sourcePlan, $remotePlan) {
    Say ("plan            : files=" + @($remotePlan.Files).Count + " bytes=" + $sourcePlan.TotalBytes)
    foreach ($f in @($remotePlan.Files)) { Say ("  " + $f.Action + " " + $f.Path + " " + $f.Sha256) }
}

function Assert-RelativePath([string]$rel) {
    if (-not $rel -or $rel.Length -gt 900 -or $rel.StartsWith("/") -or $rel.Contains('\') -or $rel.Contains(':') -or $rel -match '[\x00-\x1f\x7f]') { throw "unsafe-path" }
    $parts = $rel.Split('/')
    foreach ($part in $parts) {
        if (-not $part -or $part -eq "." -or $part -eq ".." -or $part.Length -gt 255 -or $part.EndsWith(".") -or $part.EndsWith(" ")) { throw "unsafe-path" }
        if ($part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') { throw "unsafe-path" }
    }
}

function Get-SourcePlan([string]$source) {
    $plan = [pscustomobject]@{ Ok = $false; Reason = ""; Detail = ""; Files = @(); Directories = @(); TotalBytes = [int64]0 }
    try {
        if (-not $source -or -not (Test-Path -LiteralPath $source -PathType Container)) { $plan.Reason = "mods-source-required"; return $plan }
        $root = [System.IO.Path]::GetFullPath($source).TrimEnd('\', '/')
        $rootItem = Get-Item -LiteralPath $root -Force
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "source-root-is-reparse-point" }
        # Walk explicitly instead of `Get-ChildItem -Recurse`: Windows PowerShell 5.1 -Recurse
        # follows directory junctions/symlinks while enumerating, so a link cycle inside the
        # source tree could be traversed (or loop) before any per-item check ran. This stack walk
        # inspects every child BEFORE descending, so a reparse point is rejected deterministically
        # and the walk never leaves the source root.
        $items = New-Object System.Collections.Generic.List[object]
        $pending = New-Object System.Collections.Generic.Stack[string]
        $pending.Push($root)
        while ($pending.Count -gt 0) {
            $dir = $pending.Pop()
            foreach ($child in @(Get-ChildItem -LiteralPath $dir -Force -ErrorAction Stop)) {
                if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ("source-reparse-point:" + $child.Name) }
                $items.Add($child)
                if ($child.PSIsContainer) { $pending.Push($child.FullName) }
            }
        }
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        $normalized = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        $files = New-Object System.Collections.Generic.List[object]
        $dirs = New-Object System.Collections.Generic.List[string]
        foreach ($item in $items) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ("source-reparse-point:" + $item.Name) }
            $full = [System.IO.Path]::GetFullPath($item.FullName)
            $prefix = $root + [IO.Path]::DirectorySeparatorChar
            if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "source-path-escape" }
            $rel = $full.Substring($prefix.Length).Replace('\', '/')
            Assert-RelativePath $rel
            if (-not $seen.Add($rel)) { throw ("duplicate-path:" + $rel) }
            $nfc = $rel.Normalize([Text.NormalizationForm]::FormC)
            if (-not $normalized.Add($nfc)) { throw ("duplicate-normalized-path:" + $rel) }
            foreach ($part in $rel.Split('/')) {
                if ($part -match '(?i)^(logs|\.staging|loader\.log|loader-report\.txt)$') { throw ("reserved-path:" + $rel) }
            }
            $top = $rel.Split('/')[0]
            if ($top.StartsWith(".") -or $top.StartsWith("_")) { throw ("ignored-top-level-path:" + $rel) }
            if ($item.PSIsContainer) { $dirs.Add($rel); continue }
            if ($files.Count -ge 10000) { throw "too-many-files" }
            $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $files.Add([pscustomobject]@{ Rel = $rel; Full = $item.FullName; Bytes = [int64]$item.Length; Sha256 = $hash })
            $plan.TotalBytes += [int64]$item.Length
            if ($plan.TotalBytes -gt 4294967296) { throw "source-too-large" }
        }
        if ($files.Count -eq 0) { throw "mods-source-empty" }
        if (-not ($files | Where-Object { $_.Rel -ceq "ModFramework.Runtime.dll" })) { throw "missing-runtime-dll" }
        $topDirs = @($dirs | Where-Object { -not $_.Contains("/") })
        if ($topDirs.Count -eq 0) { throw "missing-mod-directory" }
        $ids = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($dir in $topDirs) {
            $manifestRel = $dir + "/manifest.json"
            $manifestFile = @($files | Where-Object { $_.Rel -ceq $manifestRel }) | Select-Object -First 1
            if (-not $manifestFile) { throw ("missing-manifest:" + $manifestRel) }
            if ($manifestFile.Bytes -gt 262144) { throw ("manifest-too-large:" + $manifestRel) }
            $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
            $doc = [System.IO.File]::ReadAllText($manifestFile.Full, $utf8) | ConvertFrom-Json -ErrorAction Stop
            # Known fields of manifest schemaVersion 1, matching Mod/ModFramework.Runtime/ModManifest.cs
            # (KnownFields) and docs/modding/manifest-spec.md. The optional metadata fields used to be
            # missing here, so the tool refused the project's own real manifest with
            # "manifest-unknown-field:author" even though the Loader accepts it.
            $allowed = @("schemaVersion", "id", "name", "version", "author", "description", "website", "entryAssembly", "entryType", "privateAssemblies", "dependencies", "gameVersion", "frameworkVersion")
            foreach ($prop in $doc.PSObject.Properties) { if ($allowed -notcontains $prop.Name) { throw ("manifest-unknown-field:" + $prop.Name) } }
            $id = [string]$doc.id
            if (-not $id -or -not $ids.Add($id)) { throw ("duplicate-mod-id:" + $id) }
            $entry = [string]$doc.entryAssembly
            $declared = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
            foreach ($name in @($entry) + @($doc.privateAssemblies)) {
                if (-not $name) { continue }
                # Mirror ModManifest.IsValidEntryAssemblyName instead of using a character-class
                # regex: the previous pattern excluded every '.', so ordinary assembly names such
                # as P114.RuntimeProbe.dll were refused as "unsafe-manifest-path" even though the
                # Loader accepts them. Separators, ':', "..", control characters and a missing
                # .dll suffix remain rejected.
                if ($name.Length -gt 128 -or $name -match '[/\\:]' -or $name.Contains('..') -or
                    $name -match '[\x00-\x1f\x7f]' -or $name -notmatch '(?i)\.dll$') { throw ("unsafe-manifest-path:" + $name) }
                if (-not $declared.Add($name)) { throw ("duplicate-manifest-path:" + $name) }
                if (-not (@($files | Where-Object { $_.Rel -ceq ($dir + "/" + $name) }).Count)) { throw ("manifest-assembly-missing:" + $dir + "/" + $name) }
            }
        }
        $plan.Files = @($files | Sort-Object Rel)
        $plan.Directories = @($dirs | Sort-Object)
        $plan.Ok = $true
    } catch {
        $plan.Reason = "unsafe-path"
        $plan.Detail = $_.Exception.Message
        if ($plan.Detail -match 'missing|empty|too-large|too-many|manifest|duplicate') { $plan.Reason = "manifest-invalid" }
    }
    return $plan
}

function Get-StagingVerification($journal) {
    $paths = @()
    foreach ($e in @($journal.expected)) {
        if ($e.action -eq "ADD" -or $e.action -eq "REPLACE") { $paths += (Get-StagingPath ([string]$journal.txid) ([string]$e.path)) }
    }
    $map = Get-RemoteHashMap $paths
    $bad = New-Object System.Collections.Generic.List[string]
    if ($null -eq $map) { return [pscustomobject]@{ Ok = $false; Bad = @("staging-hash-query-failed") } }
    foreach ($e in @($journal.expected)) {
        if ($e.action -eq "SKIP") { continue }
        $p = Get-StagingPath ([string]$journal.txid) ([string]$e.path)
        if (-not $map.ContainsKey($p) -or $map[$p] -ne $e.sha256) { $bad.Add([string]$e.path) }
    }
    return [pscustomobject]@{ Ok = ($bad.Count -eq 0); Bad = @($bad) }
}

function Test-PreState($journal) {
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($e in @($journal.expected)) {
        $target = $script:ModsRoot + "/" + $e.path
        $state = Get-RemoteTargetState $target
        if ($e.action -eq "ADD" -and $state.Kind -ne "missing") { $problems.Add([string]$e.path + " changed from absent") }
        elseif ($e.action -eq "REPLACE" -and ($state.Kind -ne "file" -or $state.Hash -ne $e.existingSha256)) { $problems.Add([string]$e.path + " changed before switch") }
        elseif ($e.action -eq "SKIP" -and ($state.Kind -ne "file" -or $state.Hash -ne $e.sha256)) { $problems.Add([string]$e.path + " changed after plan") }
    }
    return @($problems)
}

function New-Journal([string]$txid, $sourcePlan, $remotePlan) {
    $expected = @()
    $ops = @()
    foreach ($f in $remotePlan.Files) {
        $expected += [ordered]@{ path = $f.Path; bytes = $f.Bytes; sha256 = $f.Sha256; action = $f.Action; existingSha256 = $f.ExistingSha256 }
        if ($f.Action -ne "SKIP") { $ops += [ordered]@{ path = $f.Path; state = "pending" } }
    }
    return [ordered]@{ schemaVersion = 1; txid = $txid; state = "prepared"; stateReason = ""; createdAt = (Get-Date).ToUniversalTime().ToString("o"); committedAt = ""; expected = $expected; operations = $ops; createdDirectories = @() }
}

function Get-RemoteTree {
    $files = @{}
    $symlinks = New-Object System.Collections.Generic.List[string]
    $unparseable = New-Object System.Collections.Generic.List[string]
    if (-not (Test-RemoteDir $script:ModsRoot)) { return [pscustomobject]@{ Files = $files; Symlinks = @(); Unparseable = @(); Present = $false } }
    $linkResult = Invoke-Remote ("find " + (Quote-Posix $script:ModsRoot) + " -type l")
    if ($linkResult.Exit -eq 0) {
        foreach ($line in ($linkResult.Text -split "`r?`n")) {
            if ($line.Trim()) {
                $rel = $line.Trim()
                if ($rel.StartsWith($script:ModsRoot + "/")) { $rel = $rel.Substring($script:ModsRoot.Length + 1) }
                $symlinks.Add($rel)
            }
        }
    }
    $r = Invoke-Remote ("find " + (Quote-Posix $script:ModsRoot) + " -type f -exec sha256sum {} +")
    if ($r.Exit -ne 0) {
        $unparseable.Add("find/sha256sum failed")
        return [pscustomobject]@{ Files = $files; Symlinks = @($symlinks); Unparseable = @($unparseable); Present = $true }
    }
    foreach ($line in ($r.Text -split "`r?`n")) {
        if (-not $line.Trim()) { continue }
        $m = [regex]::Match($line, '^([0-9a-fA-F]{64})\s+\*?(.+)$')
        if (-not $m.Success) { $unparseable.Add($line.Trim()); continue }
        $abs = $m.Groups[2].Value.Trim()
        if (-not $abs.StartsWith($script:ModsRoot + "/")) { $unparseable.Add($abs); continue }
        $files[$abs.Substring($script:ModsRoot.Length + 1)] = $m.Groups[1].Value.ToLowerInvariant()
    }
    return [pscustomobject]@{ Files = $files; Symlinks = @($symlinks); Unparseable = @($unparseable); Present = $true }
}

function Invoke-Rollback($journal, [switch]$Automatic) {
    $journal.state = "rolling-back"
    if (-not (Write-Journal $journal)) { return [pscustomobject]@{ Ok = $false; Detail = "could not persist rolling-back journal" } }
    $items = @($journal.expected | Where-Object { $_.action -ne "SKIP" } | Sort-Object path -Descending)
    foreach ($e in $items) {
        $target = $script:ModsRoot + "/" + $e.path
        $backup = Get-BackupPath ([string]$journal.txid) ([string]$e.path)
        $rolled = Get-RollbackPath ([string]$journal.txid) ([string]$e.path)
        $ts = Get-RemoteTargetState $target
        $bs = Get-RemoteTargetState $backup
        if ($e.action -eq "ADD") {
            if ($ts.Kind -eq "file" -and $ts.Hash -eq $e.sha256) {
                if (-not (New-RemoteDirs @((Split-PathPosix $rolled)))) { return [pscustomobject]@{ Ok = $false; Detail = "rollback directory unavailable" } }
                if ((Move-Remote $target $rolled).Exit -ne 0) { return [pscustomobject]@{ Ok = $false; Detail = "could not preserve deployed file " + $e.path } }
            } elseif ($ts.Kind -ne "missing") { return [pscustomobject]@{ Ok = $false; Detail = "rollback ownership unproven for " + $e.path } }
        } else {
            if ($ts.Kind -eq "file" -and $ts.Hash -eq $e.sha256) {
                if (-not (New-RemoteDirs @((Split-PathPosix $rolled)))) { return [pscustomobject]@{ Ok = $false; Detail = "rollback directory unavailable" } }
                if ((Move-Remote $target $rolled).Exit -ne 0) { return [pscustomobject]@{ Ok = $false; Detail = "could not preserve replacement " + $e.path } }
            } elseif ($ts.Kind -ne "missing" -and -not ($ts.Kind -eq "file" -and $ts.Hash -eq $e.existingSha256)) {
                return [pscustomobject]@{ Ok = $false; Detail = "rollback ownership unproven for " + $e.path }
            }
            $ts = Get-RemoteTargetState $target
            if ($ts.Kind -eq "missing") {
                $bs = Get-RemoteTargetState $backup
                if ($bs.Kind -ne "file" -or $bs.Hash -ne $e.existingSha256) { return [pscustomobject]@{ Ok = $false; Detail = "backup ownership unproven for " + $e.path } }
                if ((Move-Remote $backup $target).Exit -ne 0) { return [pscustomobject]@{ Ok = $false; Detail = "backup restore failed for " + $e.path } }
            }
        }
    }
    $journal.state = "rolled-back"
    $journal.stateReason = "pre-state restored; deployed bytes retained under rollback"
    if (-not (Write-Journal $journal)) { return [pscustomobject]@{ Ok = $false; Detail = "rollback completed but journal update failed" } }
    return [pscustomobject]@{ Ok = $true; Detail = "rollback completed; transaction-owned bytes were retained" }
}

function Split-PathPosix([string]$path) {
    $i = $path.LastIndexOf('/')
    if ($i -le 0) { return $script:TransactionRoot }
    return $path.Substring(0, $i)
}

function Get-RemoteHashMap([string[]]$paths) {
    $map = @{}
    $clean = @($paths | Where-Object { $_ } | Sort-Object -Unique)
    if ($clean.Count -eq 0) { return $map }
    $hashArgs = (($clean | ForEach-Object { Quote-Posix $_ }) -join " ")
    $r = Invoke-Remote ("sha256sum " + $hashArgs)
    if ($r.Exit -ne 0) { return $null }
    foreach ($line in ($r.Text -split "`r?`n")) {
        $m = [regex]::Match($line, '^([0-9a-fA-F]{64})\s+\*?(.+)$')
        if ($m.Success) { $map[$m.Groups[2].Value.Trim()] = $m.Groups[1].Value.ToLower() }
    }
    return $map
}

function Test-FinalState($journal) {
    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($e in @($journal.expected)) { $paths.Add($script:ModsRoot + "/" + $e.path) }
    $map = Get-RemoteHashMap @($paths)
    if ($null -eq $map) { return @("sha256sum failed for the final verification") }
    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($e in @($journal.expected)) {
        $p = $script:ModsRoot + "/" + $e.path
        if (-not $map.ContainsKey($p)) { $problems.Add([string]$e.path + " is missing") }
        elseif ($map[$p] -ne $e.sha256) { $problems.Add([string]$e.path + " hash mismatch") }
    }
    return @($problems)
}

function Test-StagingModes($journal) {
    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($e in @($journal.expected)) {
        if ($e.action -eq "ADD" -or $e.action -eq "REPLACE") { $paths.Add((Get-StagingPath ([string]$journal.txid) ([string]$e.path))) }
    }
    if ($paths.Count -eq 0) { return @() }
    $statArgs = (($paths | ForEach-Object { Quote-Posix $_ }) -join " ")
    $r = Invoke-Remote ("stat -c " + (Quote-Posix "%a") + " " + $statArgs)
    if ($r.Exit -ne 0) { return @("stat failed for staged files") }
    $problems = New-Object System.Collections.Generic.List[string]
    $lines = @($r.Text -split "`r?`n" | Where-Object { $_.Trim() })
    $i = 0
    foreach ($e in @($journal.expected)) {
        if ($e.action -ne "ADD" -and $e.action -ne "REPLACE") { continue }
        if ($i -ge $lines.Count) { $problems.Add([string]$e.path + " has no stat result"); continue }
        $mode = $lines[$i].Trim(); $i++
        if ($mode -notmatch '^[0-7]{3,4}$') { $problems.Add([string]$e.path + " mode is unreadable"); continue }
        $bits = [Convert]::ToInt32($mode, 8)
        if (($bits -band 0x100) -eq 0) { $problems.Add([string]$e.path + " is not owner-readable (mode " + $mode + ")") }
    }
    return @($problems)
}

function Invoke-Deploy($sourcePlan, $remotePlan) {
    if (-not $script:SerialExplicit) { Exit-Tool 3 "refused" "PolicyRefused" "serial-required" "apply requires an explicit -Serial; no device is ever guessed" }
    Assert-NoActiveTransaction
    [void](Get-PackageState -RequireInstalled)

    $txid = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ") + "-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $txDir = Get-TxDir $txid
    $stagingRoot = $txDir + "/staging"
    $backupRoot = $txDir + "/backup"
    $rollbackRoot = $txDir + "/rollback"

    Say ""
    Say ("transaction     : " + $txid)
    if (-not (New-RemoteDirs @($txDir, $stagingRoot, $backupRoot, $rollbackRoot))) {
        Exit-Tool 2 "blocked" "StorageUnavailable" "staging-create-failed" ($txDir + " could not be created; no Mods file was written")
    }
    $script:Result.storage.writeProbe = "ok"
    # From here on this process writes to the device; the self-attested flag must say so.
    $script:Result.safety.deviceTouched = $true
    [void](Set-RemoteMode $txDir "0755")
    [void](Set-RemoteMode $stagingRoot "0755")
    [void](Set-RemoteMode $backupRoot "0755")
    [void](Set-RemoteMode $rollbackRoot "0755")

    $neededStagingDirs = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    $neededBackupDirs = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($f in $remotePlan.Files) {
        if ($f.Action -eq "SKIP") { continue }
        $parts = $f.Path.Split('/')
        $acc = ""
        for ($i = 0; $i -lt ($parts.Count - 1); $i++) {
            $acc = if ($acc) { $acc + "/" + $parts[$i] } else { $parts[$i] }
            [void]$neededStagingDirs.Add($stagingRoot + "/" + $acc)
            if ($f.Action -eq "REPLACE") { [void]$neededBackupDirs.Add($backupRoot + "/" + $acc) }
        }
    }
    if ($neededStagingDirs.Count -gt 0 -and -not (New-RemoteDirs @($neededStagingDirs))) {
        Exit-Tool 2 "blocked" "StorageUnavailable" "staging-create-failed" "staging subdirectories could not be created; no Mods file was written"
    }
    if ($neededBackupDirs.Count -gt 0 -and -not (New-RemoteDirs @($neededBackupDirs))) {
        Exit-Tool 2 "blocked" "StorageUnavailable" "staging-create-failed" "backup subdirectories could not be created; no Mods file was written"
    }

    $journal = New-Journal $txid $sourcePlan $remotePlan
    if (-not (Write-Journal $journal)) {
        Exit-Tool 5 "recovery-required" "RecoveryRequired" "journal-write-failed" ("journal could not be written under " + $txDir + "; inspect it manually before retrying")
    }
    $script:Result.journal.txid = $txid
    $script:Result.journal.path = $txDir + "/journal.json"
    $script:Result.journal.state = "prepared"
    $script:Result.journal.createdAt = [string]$journal.createdAt

    foreach ($f in $remotePlan.Files) {
        if ($f.Action -eq "SKIP") { continue }
        $target = Get-StagingPath $txid $f.Path
        $push = Push-RemoteFile $f.Full $target
        if ($push.Exit -ne 0) {
            $journal.state = "staging-failed"
            $journal.stateReason = "push failed for " + $f.Path
            [void](Write-Journal $journal)
            Exit-Tool 4 "failed" "DeployFailed" "staging-write-failed" ($f.Path + ": " + ($push.Text -replace "`r?`n", " "))
        }
    }
    $stage = Get-StagingVerification $journal
    if (-not $stage.Ok) {
        $journal.state = "staging-failed"
        $journal.stateReason = "staging hash mismatch"
        [void](Write-Journal $journal)
        Exit-Tool 4 "failed" "DeployFailed" "staging-hash-mismatch" (($stage.Bad | Select-Object -First 6) -join "; ")
    }
    Say ("verify-staged   : " + @($journal.expected | Where-Object { $_.action -ne "SKIP" }).Count + " ordered file(s) match local SHA-256")

    $stagePaths = New-Object System.Collections.Generic.List[string]
    foreach ($e in @($journal.expected)) {
        if ($e.action -eq "ADD" -or $e.action -eq "REPLACE") { $stagePaths.Add((Get-StagingPath $txid ([string]$e.path))) }
    }
    if ($stagePaths.Count -gt 0) {
        $chmodArgs = (($stagePaths | ForEach-Object { Quote-Posix $_ }) -join " ")
        [void](Invoke-Remote ("chmod 0644 " + $chmodArgs))
        $modeProblems = @(Test-StagingModes $journal)
        if ($modeProblems.Count -gt 0) {
            $journal.state = "staging-failed"
            $journal.stateReason = "staging mode unreadable"
            [void](Write-Journal $journal)
            Exit-Tool 4 "failed" "DeployFailed" "staging-mode-unreadable" (($modeProblems | Select-Object -First 6) -join "; ")
        }
    }

    # The plan was made before staging. Re-read every target so an external change between plan
    # and switch aborts before the first Mods write.
    $pre = @(Test-PreState $journal)
    if ($pre.Count -gt 0) {
        $journal.state = "staging-verified"
        $journal.stateReason = "target changed before switch"
        [void](Write-Journal $journal)
        Exit-Tool 3 "refused" "PolicyRefused" "target-changed-since-plan" (($pre | Select-Object -First 6) -join "; ")
    }
    [void](Get-PackageState -RequireInstalled)

    $created = New-Object System.Collections.Generic.List[string]
    if (-not (Test-RemoteDir $script:ModsRoot)) { $created.Add(".") }
    foreach ($d in @($remotePlan.Directories)) {
        $abs = $script:ModsRoot + "/" + $d
        if (-not (Test-RemoteDir $abs)) { $created.Add($d) }
    }
    $journal.createdDirectories = @($created)
    $journal.state = "applying"
    $journal.stateReason = "creating target directories and switching files"
    if (-not (Write-Journal $journal)) {
        Exit-Tool 5 "recovery-required" "RecoveryRequired" "journal-write-failed" "could not record the applying state; staging and backup were kept"
    }
    $script:Result.journal.state = "applying"

    $allDirs = @($script:ModsRoot)
    foreach ($d in @($remotePlan.Directories)) { $allDirs += ($script:ModsRoot + "/" + $d) }
    if (-not (New-RemoteDirs $allDirs)) {
        $rb = Invoke-Rollback $journal -Automatic
        $code = if ($rb.Ok) { 4 } else { 5 }
        Exit-Tool $code "failed" $(if ($rb.Ok) { "DeployFailed" } else { "RecoveryRequired" }) "target-directory-create-failed" $rb.Detail
    }
    foreach ($d in $allDirs) { [void](Set-RemoteMode $d "0755") }

    $opIndex = 0
    foreach ($op in @($journal.operations)) {
        $e = @($journal.expected | Where-Object { $_.path -eq $op.path })[0]
        $journal.operations[$opIndex].state = "started"
        if (-not (Write-Journal $journal)) {
            $rb = Invoke-Rollback $journal -Automatic
            Exit-Tool 5 "recovery-required" "RecoveryRequired" "journal-write-failed" $rb.Detail
        }
        $target = $script:ModsRoot + "/" + $e.path
        $staged = Get-StagingPath $txid ([string]$e.path)
        if ($e.action -eq "REPLACE") {
            $backup = Get-BackupPath $txid ([string]$e.path)
            $mvBackup = Move-Remote $target $backup
            if ($mvBackup.Exit -ne 0) {
                $rb = Invoke-Rollback $journal -Automatic
                Exit-Tool $(if ($rb.Ok) { 4 } else { 5 }) $(if ($rb.Ok) { "failed" } else { "recovery-required" }) $(if ($rb.Ok) { "DeployFailed" } else { "RecoveryRequired" }) "switch-backup-failed" ([string]$e.path + ": " + ($mvBackup.Text -replace "`r?`n", " "))
            }
            $bstate = Get-RemoteTargetState $backup
            if ($bstate.Kind -ne "file" -or $bstate.Hash -ne $e.existingSha256) {
                $rb = Invoke-Rollback $journal -Automatic
                Exit-Tool 5 "recovery-required" "RecoveryRequired" "backup-hash-mismatch" ([string]$e.path + " backup could not be proven")
            }
        }
        $mvNew = Move-Remote $staged $target
        if ($mvNew.Exit -ne 0) {
            $rb = Invoke-Rollback $journal -Automatic
            Exit-Tool $(if ($rb.Ok) { 4 } else { 5 }) $(if ($rb.Ok) { "failed" } else { "recovery-required" }) $(if ($rb.Ok) { "DeployFailed" } else { "RecoveryRequired" }) "switch-failed" ([string]$e.path + ": " + ($mvNew.Text -replace "`r?`n", " "))
        }
        $tstate = Get-RemoteTargetState $target
        if ($tstate.Kind -ne "file" -or $tstate.Hash -ne $e.sha256) {
            $rb = Invoke-Rollback $journal -Automatic
            Exit-Tool 5 "recovery-required" "RecoveryRequired" "switch-hash-mismatch" ([string]$e.path + " final bytes do not match")
        }
        $journal.operations[$opIndex].state = "done"
        if (-not (Write-Journal $journal)) {
            $rb = Invoke-Rollback $journal -Automatic
            Exit-Tool 5 "recovery-required" "RecoveryRequired" "journal-write-failed" $rb.Detail
        }
        Say ("  switched      : " + $e.action + " " + $e.path + " " + $e.sha256)
        $opIndex++
    }

    # @(...) is REQUIRED: one failing file would otherwise arrive as a plain string with a $null
    # Count and the failure would be treated as success.
    $final = @(Test-FinalState $journal)
    if ($final.Count -gt 0) {
        $rb = Invoke-Rollback $journal -Automatic
        Exit-Tool $(if ($rb.Ok) { 4 } else { 5 }) $(if ($rb.Ok) { "failed" } else { "recovery-required" }) $(if ($rb.Ok) { "DeployFailed" } else { "RecoveryRequired" }) "final-verify-failed" (($final | Select-Object -First 6) -join "; ")
    }
    Say ("verify-remote   : " + @($journal.expected).Count + "/" + @($journal.expected).Count + " planned files match local SHA-256")
    $journal.state = "committed"
    $journal.stateReason = "all planned files verified in their final location"
    $journal.committedAt = (Get-Date).ToUniversalTime().ToString("o")
    if (-not (Write-Journal $journal)) {
        Exit-Tool 5 "recovery-required" "RecoveryRequired" "journal-write-failed" "Mods is correct but the committed state could not be recorded; do not redeploy before inspecting"
    }
    $script:Result.journal.state = "committed"
    $script:Result.journal.committedAt = [string]$journal.committedAt
    $script:Result.plan.fileCount = @($journal.expected).Count
    $script:Result.plan.totalBytes = $sourcePlan.TotalBytes
    $replaceCount = @($journal.expected | Where-Object { $_.action -eq "REPLACE" }).Count
    $addCount = @($journal.expected | Where-Object { $_.action -eq "ADD" }).Count
    Say ("backup-kept     : " + $replaceCount + " replaced file(s) under " + $backupRoot)
    Exit-Tool 0 "ok" "" "" ($addCount.ToString() + " added, " + $replaceCount.ToString() + " replaced; old versions were kept")
}

function Get-ExpectedMap([string]$txid) {
    $map = @{}
    if ($ModsSource) {
        $plan = Get-SourcePlan $ModsSource
        if (-not $plan.Ok) { Exit-Tool 3 "refused" "PolicyRefused" $plan.Reason $plan.Detail }
        foreach ($f in $plan.Files) { $map[$f.Rel] = $f.Sha256 }
        return [pscustomobject]@{ Map = $map; Source = $plan; Journal = $null }
    }
    $read = Get-ReadJournal $txid
    if (-not $read.Ok) { Exit-Tool 5 "recovery-required" "RecoveryRequired" "journal-unknown" $read.Detail }
    foreach ($e in @($read.Journal.expected)) { $map[[string]$e.path] = [string]$e.sha256 }
    return [pscustomobject]@{ Map = $map; Source = $null; Journal = $read.Journal }
}

function Invoke-Verify {
    if (-not $ModsSource -and -not $TxId) { Exit-Tool 3 "refused" "PolicyRefused" "verify-input-required" "pass -ModsSource or -TxId" }
    [void](Get-DeviceContext)
    $expected = Get-ExpectedMap $TxId
    $tree = Get-RemoteTree
    $missing = New-Object System.Collections.Generic.List[string]
    $modified = New-Object System.Collections.Generic.List[string]
    $extra = New-Object System.Collections.Generic.List[string]
    $matched = 0
    foreach ($rel in @($expected.Map.Keys | Sort-Object)) {
        if (-not $tree.Files.ContainsKey($rel)) { $missing.Add($rel); continue }
        if ($tree.Files[$rel] -ne $expected.Map[$rel]) { $modified.Add($rel); continue }
        $matched++
    }
    foreach ($rel in @($tree.Files.Keys | Sort-Object)) {
        if (-not $expected.Map.ContainsKey($rel)) { $extra.Add($rel) }
    }
    $script:Result.verification.expectedFiles = $expected.Map.Count
    $script:Result.verification.matchedFiles = $matched
    $script:Result.verification.missing = @($missing)
    $script:Result.verification.modified = @($modified)
    $script:Result.verification.extra = @($extra)
    $script:Result.verification.symlinks = @($tree.Symlinks)
    $script:Result.verification.unparseable = @($tree.Unparseable)
    if ($expected.Journal) {
        $script:Result.journal.txid = [string]$expected.Journal.txid
        $script:Result.journal.state = [string]$expected.Journal.state
        $script:Result.journal.path = (Get-TxDir ([string]$expected.Journal.txid)) + "/journal.json"
    }
    Say ("verify          : expected=" + $expected.Map.Count + " matched=" + $matched + " missing=" + $missing.Count + " modified=" + $modified.Count + " extra=" + $extra.Count + " symlinks=" + @($tree.Symlinks).Count)
    foreach ($m in $missing) { Say ("  missing       : " + $m) }
    foreach ($m in $modified) { Say ("  modified      : " + $m) }
    foreach ($m in $extra) { Say ("  extra         : " + $m) }
    foreach ($m in @($tree.Symlinks)) { Say ("  symlink       : " + $m) }
    foreach ($m in @($tree.Unparseable)) { Say ("  unparseable   : " + $m) }
    $fail = ($missing.Count -gt 0 -or $modified.Count -gt 0 -or @($tree.Symlinks).Count -gt 0 -or @($tree.Unparseable).Count -gt 0 -or ($StrictExtras -and $extra.Count -gt 0))
    if ($fail) {
        $reason = if ($missing.Count -gt 0) { "missing-files" } elseif ($modified.Count -gt 0) { "modified-files" } elseif (@($tree.Symlinks).Count -gt 0) { "unexpected-symlink" } elseif (@($tree.Unparseable).Count -gt 0) { "unparseable-tree" } else { "strict-extras" }
        Exit-Tool 3 "refused" "VerifyMismatch" $reason "verification found differences; no file was written"
    }
    Exit-Tool 0 "ok" "" "" ($matched.ToString() + " files matched; extras are reported but not fatal unless -StrictExtras is passed")
}

function Invoke-RollbackCommand {
    if (-not $TxId) { Exit-Tool 3 "refused" "PolicyRefused" "txid-required" "pass -TxId <id>" }
    if ($TxId -notmatch $script:TxIdPattern) { Exit-Tool 3 "refused" "PolicyRefused" "invalid-txid" $TxId }
    [void](Get-DeviceContext)
    $read = Get-ReadJournal $TxId
    if (-not $read.Ok) { Exit-Tool 5 "recovery-required" "RecoveryRequired" "journal-unknown" $read.Detail }
    $journal = $read.Journal
    $script:Result.journal.txid = $TxId
    $script:Result.journal.path = (Get-TxDir $TxId) + "/journal.json"
    $script:Result.journal.state = [string]$journal.state
    $script:Result.journal.createdAt = [string]$journal.createdAt
    if ($journal.PSObject.Properties["committedAt"]) { $script:Result.journal.committedAt = [string]$journal.committedAt }
    Say ("journal         : " + $TxId + " state=" + $journal.state)
    foreach ($e in @($journal.expected)) { Say ("  expected      : " + $e.action + " " + $e.path + " " + $e.sha256) }
    if ($journal.state -eq "rolled-back") { Exit-Tool 0 "ok" "" "" "transaction is already rolled back; staging and backup were kept" }
    if ($journal.state -eq "committed") { Say "warning         : this transaction was committed; rollback will revert the deployed bytes" }
    if (-not $Apply) {
        Exit-Tool 0 "ok" "" "" "rollback dry-run only; pass -Apply -Serial <serial> to restore the recorded pre-state"
    }
    if (-not $script:SerialExplicit) { Exit-Tool 3 "refused" "PolicyRefused" "serial-required" "rollback -Apply requires an explicit -Serial" }
    [void](Get-PackageState -RequireInstalled)
    [void](Get-StorageState)
    # The rollback write path also mutates device state; report it truthfully.
    $script:Result.safety.deviceTouched = $true
    $rb = Invoke-Rollback $journal
    if ($rb.Ok) { Exit-Tool 0 "ok" "" "" $rb.Detail }
    Exit-Tool 5 "recovery-required" "RecoveryRequired" "rollback-failed" $rb.Detail
}

function Invoke-Inspect {
    [void](Get-DeviceContext)
    [void](Get-PackageState)
    Get-RequiredCommands
    $storage = Get-StorageState
    # @(...) is REQUIRED: a function that returns one element is unwrapped by PowerShell, and a
    # scalar PSCustomObject has a $null Count, so `if ($txs.Count -gt 0)` would silently skip the
    # whole transaction check.
    $txs = @(Get-Transactions)
    $script:Result.transactions = @($txs)
    if ($storage.ModsExists) {
        $ls = Invoke-Remote ("ls -1 " + (Quote-Posix $script:ModsRoot))
        if ($ls.Exit -eq 0) {
            foreach ($line in ($ls.Text -split "`r?`n")) {
                if ($line.Trim()) { Say ("  mods-entry    : " + $line.Trim()) }
            }
        }
    }
    if ($txs.Count -gt 0) {
        foreach ($t in $txs) { Say ("  transaction   : " + $t.txid + " state=" + $t.state + " journal=" + $(if ($t.journalReadable) { "readable" } else { "unreadable" })) }
        $active = @($txs | Where-Object { $script:ActiveStates -contains [string]$_.state -or -not $_.journalReadable })
        if ($active.Count -gt 0) {
            Exit-Tool 5 "recovery-required" "RecoveryRequired" "active-transaction" ("unfinished transaction(s): " + (($active | ForEach-Object { $_.txid + "=" + $_.state }) -join ", ") + "; inspect staging/backup before acting")
        }
    } else {
        Say "  transaction   : none"
    }
    Exit-Tool 0 "ok" "" "" "read-only inspection complete"
}

function Invoke-PlanOrDeploy {
    if (-not $ModsSource) { Exit-Tool 3 "refused" "PolicyRefused" "mods-source-required" "pass -ModsSource <complete Mods root>" }
    $sourcePlan = Get-SourcePlan $ModsSource
    if (-not $sourcePlan.Ok) { Exit-Tool 3 "refused" "PolicyRefused" $sourcePlan.Reason $sourcePlan.Detail }
    [void](Get-DeviceContext)
    Get-RequiredCommands
    [void](Get-StorageState)
    [void](Get-PackageState -RequireInstalled)
    if ($Apply) {
        $kb = Get-DiskAvailableKb
        $needKb = [int64](($sourcePlan.TotalBytes * 2) / 1024)
        if ($kb -ge 0 -and $needKb -gt $kb) {
            Exit-Tool 2 "blocked" "StorageUnavailable" "insufficient-space" ("need about " + $needKb + " KiB, available " + $kb + " KiB")
        }
        if ($kb -ge 0) { Say ("space-check     : " + $kb + " KiB available, about " + $needKb + " KiB needed for staging + backup") }
    }
    $approved = Get-ApprovedOverwrites
    $remotePlan = Get-RemotePlan $sourcePlan $approved
    Set-PlanResult $sourcePlan $remotePlan
    Show-Plan $sourcePlan $remotePlan
    if (-not $remotePlan.Ok) {
        if ($remotePlan.Conflicts.Count -gt 0) {
            Exit-Tool 3 "refused" "PolicyRefused" $remotePlan.Reason ("existing target files differ; list each one explicitly with -Overwrite: " + $remotePlan.Detail)
        }
        Exit-Tool 3 "refused" "PolicyRefused" $remotePlan.Reason $remotePlan.Detail
    }
    if ($Command -eq "deploy" -and $Apply) {
        Invoke-Deploy $sourcePlan $remotePlan
        return
    }
    Exit-Tool 0 "ok" "" "" "dry-run plan is valid; no device file was written"
}

$script:Command = $Action
$script:ModsSource = $Source
$script:Result.command = $Action
if ($PSBoundParameters.Count -eq 0 -or $Help) {
    Show-Help
    exit 0
}
if (-not $Action) {
    Exit-Tool 3 "refused" "PolicyRefused" "action-required" "pass -Action inspect|plan|deploy|verify|rollback"
}
$Action = $Action.ToLowerInvariant()
$script:Command = $Action
$script:Result.command = $Action
if ($Action -notin @("inspect", "plan", "deploy", "verify", "rollback")) {
    Exit-Tool 3 "refused" "PolicyRefused" "unknown-action" "supported actions: inspect, plan, deploy, verify, rollback"
}
if ($PackageName -ne $script:FixedPackage) {
    Exit-Tool 3 "refused" "PolicyRefused" "package-name-mismatch" "PackageName is fixed to com.chucklefish.stardewvalley"
}
$adbInfo = Resolve-Adb $Adb
if (-not $adbInfo.Ok) {
    Exit-Tool 2 "blocked" "EnvironmentBlocked" "no-adb" "pass -Adb or install adb"
}
$script:AdbBin = $adbInfo.Path

try {
    Say ("deploy-mods-safe: Phase " + $script:ToolVersion + " (no root, no Saves, default dry-run)")
    Say ("command         : " + $Command)
    if ($Command -eq "inspect" -or $Command -eq "verify" -or $Command -eq "rollback") {
        if ($Apply -and $Command -ne "rollback") { Exit-Tool 3 "refused" "PolicyRefused" "apply-not-supported" ("-Apply is not valid for " + $Command) }
    }
    if ($Command -eq "plan" -and $Apply) { Exit-Tool 3 "refused" "PolicyRefused" "apply-not-supported" "-Apply is not valid for plan" }

    if ($Command -eq "inspect") {
        $script:Result.mode = "read-only"
        Invoke-Inspect
    } elseif ($Command -eq "verify") {
        $script:Result.mode = "read-only"
        Invoke-Verify
    } elseif ($Command -eq "rollback") {
        $script:Result.mode = if ($Apply) { "apply" } else { "dry-run" }
        Invoke-RollbackCommand
    } else {
        $script:Result.mode = if ($Command -eq "deploy" -and $Apply) { "apply" } else { "dry-run" }
        Invoke-PlanOrDeploy
    }
} catch {
    Exit-Tool 5 "recovery-required" "RecoveryRequired" "unexpected-exception" ($_.Exception.Message)
}
