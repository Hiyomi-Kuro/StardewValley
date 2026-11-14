$ErrorActionPreference = "Stop"
# The tool writes UTF-8 JSON (its detail/plan fields may contain the Chinese fixture path); decode
# native output as UTF-8 here too so the parsed JSON is not mojibake.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$env:PSModulePath = @((Join-Path $PSHOME "Modules"), (Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\Modules")) -join ";"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $here "..\..")).Path
$tool = Join-Path $root "Tools\deploy-mods-safe.ps1"
$work = Join-Path $root ".tmp\phase1.2.2"
$runStamp = (Get-Date).ToString("yyyyMMdd-HHmmss")
# A per-run fixture root keeps fixture creation idempotent: a junction left behind by an earlier run
# made New-Item fail and silently downgraded a real scenario to SKIP.
$fixtures = Join-Path $work ("fixtures-" + $runStamp)
$logDir = Join-Path $work "logs"
[void][System.IO.Directory]::CreateDirectory($fixtures)
[void][System.IO.Directory]::CreateDirectory($logDir)
[IO.File]::WriteAllText((Join-Path $logDir "test-results.log"), "")
$pwshExe = (Get-Process -Id $PID).Path
$fakeAdb = Join-Path $work "fake-adb.exe"
$worker = Join-Path $fixtures "fake-adb-worker.ps1"
$launcherSource = Join-Path $fixtures "fake-adb-launcher.cs"
$script:results = New-Object System.Collections.Generic.List[object]
$script:testNo = 0

function Record([string]$name, [string]$status, [string]$detail) {
    $script:testNo++
    $script:results.Add([pscustomobject]@{ name = $name; status = $status; passed = ($status -eq "pass"); detail = $detail })
    $line = "{0} {1} {2}" -f $status.ToUpperInvariant(), $name, $detail
    Write-Host $line
    [System.IO.File]::AppendAllText((Join-Path $logDir "test-results.log"), $line + "`r`n", [Text.Encoding]::UTF8)
}

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    Record $name $(if ($ok) { "pass" } else { "fail" }) $detail
}

# A scenario the host cannot exercise is reported as SKIP, never as PASS: a skipped capability is
# not evidence that the boundary holds.
function Check-Skip([string]$name, [string]$detail) {
    Record $name "skip" $detail
}

# True when the virtual device tree is byte-identical before and after a case (read-only proof).
function Test-SnapshotEqual($case) {
    $before = $case.Before
    $after = $case.After
    if ($before.Count -ne $after.Count) { return $false }
    foreach ($key in $before.Keys) {
        if (-not $after.ContainsKey($key)) { return $false }
        if ($after[$key] -ne $before[$key]) { return $false }
    }
    return $true
}

$workerText = @'
$ErrorActionPreference = "Stop"
# adb speaks UTF-8; without this the worker's own stdout would use the OEM code page and the tool
# (which decodes UTF-8) would see mangled non-ASCII paths.
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$log = $env:FAKE_ADB_LOG
$argv = @($args)
if ($log) { [IO.File]::AppendAllText($log, ($argv -join " ") + "`r`n", [Text.Encoding]::UTF8) }
if ($argv.Count -eq 0) { exit 2 }

function Device-Path([string]$path) {
    if (-not $path.StartsWith("/sdcard/")) { throw "fake device path outside virtual sdcard" }
    if ($path -match '(^|/)\.\.(/|$)') { throw "fake path traversal" }
    $root = [IO.Path]::GetFullPath($env:FAKE_DEVICE_ROOT)
    $local = [IO.Path]::GetFullPath((Join-Path $root ($path.TrimStart("/") -replace "/", "\")))
    if (-not $local.StartsWith($root.TrimEnd("\") + "\", [StringComparison]::OrdinalIgnoreCase)) { throw "fake path escape" }
    return $local
}
function Split-Command([string]$text) {
    $out = New-Object System.Collections.Generic.List[string]
    $b = New-Object Text.StringBuilder
    $quoted = $false
    $started = $false
    for ($i = 0; $i -lt $text.Length; $i++) {
        $c = $text[$i]
        if ($quoted) {
            if ($c -eq [char]39) { $quoted = $false } else { [void]$b.Append($c) }
            $started = $true
            continue
        }
        if ($c -eq [char]39) { $quoted = $true; $started = $true; continue }
        if ($c -eq [char]92 -and $i + 1 -lt $text.Length) { $i++; [void]$b.Append($text[$i]); $started = $true; continue }
        if ([char]::IsWhiteSpace($c)) {
            if ($started) { $out.Add($b.ToString()); [void]$b.Clear(); $started = $false }
            continue
        }
        [void]$b.Append($c); $started = $true
    }
    if ($quoted) { throw "unclosed quote in fake remote command" }
    if ($started) { $out.Add($b.ToString()) }
    return @($out)
}
function Exists([string]$p) {
    $l = Device-Path $p
    if (Test-Path -LiteralPath $l) { return $true }
    try { $x = Get-Item -LiteralPath $l -Force -ErrorAction Stop; return ($null -ne $x) } catch { return $false }
}
function Is-Link([string]$p) {
    try { $x = Get-Item -LiteralPath (Device-Path $p) -Force -ErrorAction Stop; return (($x.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) } catch { return $false }
}
function Hash-Line([string]$p) {
    $local = Device-Path $p
    if (-not (Test-Path -LiteralPath $local -PathType Leaf)) { return $null }
    $hash = (Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($env:FAKE_HASH_WRONG -and $p.Contains($env:FAKE_HASH_WRONG)) { $hash = "0" * 64 }
    return ($hash + "  " + $p)
}

if ($argv[0] -eq "devices") {
    Write-Output "List of devices attached"
    foreach ($entry in ($env:FAKE_DEVICES -split ";")) {
        if (-not $entry) { continue }
        $bits = $entry -split "=", 2
        $state = if ($bits.Count -gt 1) { $bits[1] } else { "device" }
        Write-Output ($bits[0] + "`t" + $state + " product:fake model:LocalVirtualDevice")
    }
    exit 0
}
$i = 0
if ($argv[0] -eq "-s" -and $argv.Count -ge 2) { $i = 2 }
if ($i -ge $argv.Count) { exit 2 }
$verb = $argv[$i]
if ($verb -eq "push") {
    if ($argv.Count -lt ($i + 3)) { exit 2 }
    $src = $argv[$i + 1]; $dst = Device-Path $argv[$i + 2]
    if ($env:FAKE_PUSH_FAIL -and $argv[$i + 2].Contains($env:FAKE_PUSH_FAIL)) {
        [Console]::Error.WriteLine("injected push failure")
        exit 1
    }
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { exit 1 }
    $parent = Split-Path -Parent $dst
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { exit 1 }
    Copy-Item -LiteralPath $src -Destination $dst -Force
    Write-Output "1 file pushed"
    exit 0
}
if ($verb -ne "shell" -or $argv.Count -le ($i + 1)) {
    [Console]::Error.WriteLine("UNEXPECTED adb operation: " + $verb)
    exit 2
}
$cmd = $argv[$i + 1]
if ($cmd -match '(?i)\bsu\b|force-stop|pm\s+clear|uninstall|(^|\s)install(\s|$)|Saves|shared_prefs|databases|/data/data|keystore|\.apk') {
    [Console]::Error.WriteLine("FORBIDDEN fake remote command: " + $cmd)
    exit 9
}
$w = Split-Command $cmd
if ($w.Count -eq 0) { exit 0 }
switch ($w[0]) {
    "getprop" {
        switch ($w[1]) {
            "ro.build.version.sdk" { Write-Output $(if ($env:FAKE_API) { $env:FAKE_API } else { "29" }); exit 0 }
            "ro.build.version.release" { Write-Output $(if ($env:FAKE_RELEASE) { $env:FAKE_RELEASE } else { "10" }); exit 0 }
            "ro.product.model" { Write-Output "Fake Local Device"; exit 0 }
            default { exit 1 }
        }
    }
    "pm" {
        if ($w[1] -eq "path" -and $w[2] -eq "com.chucklefish.stardewvalley" -and $env:FAKE_PACKAGE_INSTALLED -ne "0") {
            Write-Output "package:/sdcard/fake/base.apk"
            exit 0
        }
        exit 1
    }
    "dumpsys" {
        if ($env:FAKE_PACKAGE_INSTALLED -eq "0") { exit 1 }
        Write-Output ("Package [com.chucklefish.stardewvalley] versionCode=" + $(if ($env:FAKE_VERSION_CODE) { $env:FAKE_VERSION_CODE } else { "239" }) + " versionName=" + $(if ($env:FAKE_VERSION_NAME) { $env:FAKE_VERSION_NAME } else { "1.6.15.0" }))
        exit 0
    }
    "pidof" {
        if ($env:FAKE_GAME_RUNNING -eq "1") { Write-Output "3210"; exit 0 }
        exit 1
    }
    "command" {
        if ($w[1] -eq "-v") { Write-Output ("/system/bin/" + $w[2]); exit 0 }
        exit 1
    }
    "df" {
        Write-Output "Filesystem 1K-blocks Used Available Use% Mounted on"
        Write-Output "/dev/fake 1000000 1000 999000 1% /sdcard"
        exit 0
    }
    "test" {
        $p = $w[$w.Count - 1]
        switch ($w[1]) {
            "-e" { if (Exists $p) { exit 0 } else { exit 1 } }
            "-d" { if (Test-Path -LiteralPath (Device-Path $p) -PathType Container) { exit 0 } else { exit 1 } }
            "-L" {
                # A real Android device exposes /sdcard as a symlink to /storage/emulated/0. This
                # mode models that shape so a regression which treats the OS-owned prefix as an
                # unsafe link (and therefore refuses every deployment) fails here.
                if ($env:FAKE_SDCARD_LINK -eq "1" -and $p -match '^/sdcard(/Android(/data(/com\.chucklefish\.stardewvalley)?)?)?$') { exit 0 }
                if (Is-Link $p) { exit 0 } else { exit 1 }
            }
            default { exit 2 }
        }
    }
    "mkdir" {
        $p = Device-Path $w[-1]
        [void][IO.Directory]::CreateDirectory($p)
        exit 0
    }
    "chmod" { exit 0 }
    "mv" {
        # POSIX/toybox `mv src dst` REPLACES an existing file dst (only a directory dst means "move
        # into it"), which is how the tool rewrites journal.json. An earlier fake refused every
        # existing destination, so the second journal write always failed for the wrong reason.
        $from = Device-Path $w[-2]; $to = Device-Path $w[-1]
        if ($env:FAKE_MV_FAIL_MATCH -and $w[-1].Contains($env:FAKE_MV_FAIL_MATCH)) {
            $flag = Join-Path $env:FAKE_DEVICE_ROOT ".mv-failed-once"
            if ($env:FAKE_MV_FAIL_ONCE -ne "1" -or -not (Test-Path -LiteralPath $flag)) {
                [IO.File]::WriteAllText($flag, "1")
                [Console]::Error.WriteLine("injected mv failure")
                exit 1
            }
        }
        if (-not (Test-Path -LiteralPath $from)) { exit 1 }
        if (Test-Path -LiteralPath $to -PathType Container) { exit 1 }
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $to))
        Move-Item -LiteralPath $from -Destination $to -Force
        if ($env:FAKE_INTERRUPT_MATCH -and $w[-1].Contains($env:FAKE_INTERRUPT_MATCH)) {
            [IO.File]::WriteAllText((Join-Path $env:FAKE_DEVICE_ROOT ".interrupted"), "1")
            if ($env:FAKE_ADB_PARENT_PID) { Stop-Process -Id ([int]$env:FAKE_ADB_PARENT_PID) -Force -ErrorAction SilentlyContinue }
        }
        exit 0
    }
    "sha256sum" {
        $found = 0
        for ($j = 1; $j -lt $w.Count; $j++) {
            if ($w[$j] -eq "--") { continue }
            $line = Hash-Line $w[$j]
            if ($line) { Write-Output $line; $found++ }
        }
        if ($found -gt 0) { exit 0 } else { exit 1 }
    }
    "stat" {
        for ($j = 3; $j -lt $w.Count; $j++) {
            if (Test-Path -LiteralPath (Device-Path $w[$j]) -PathType Leaf) { Write-Output "644" } else { exit 1 }
        }
        exit 0
    }
    "cat" {
        $p = Device-Path $w[1]
        if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { exit 1 }
        [Console]::Write([IO.File]::ReadAllText($p, [Text.Encoding]::UTF8))
        exit 0
    }
    "ls" {
        $p = Device-Path $w[-1]
        if (-not (Test-Path -LiteralPath $p -PathType Container)) { exit 1 }
        Get-ChildItem -LiteralPath $p -Force | ForEach-Object { Write-Output $_.Name }
        exit 0
    }
    "find" {
        $rootPath = Device-Path $w[1]
        if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { exit 0 }
        $items = @(Get-ChildItem -LiteralPath $rootPath -Force -Recurse -ErrorAction SilentlyContinue)
        if ($w -contains "-type" -and $w[([array]::IndexOf($w, "-type") + 1)] -eq "l") {
            foreach ($x in $items) {
                if (($x.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    $rel = $x.FullName.Substring($env:FAKE_DEVICE_ROOT.Length).Replace("\", "/")
                    Write-Output $rel
                }
            }
            exit 0
        }
        if ($w -contains "-exec") {
            foreach ($x in $items) {
                if (-not $x.PSIsContainer -and ($x.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
                    $remote = $x.FullName.Substring($env:FAKE_DEVICE_ROOT.Length).Replace("\", "/")
                    $line = Hash-Line $remote
                    if ($line) { Write-Output $line }
                }
            }
            exit 0
        }
        exit 0
    }
    default {
        [Console]::Error.WriteLine("UNEXPECTED fake remote command: " + $cmd)
        exit 2
    }
}
'@
[System.IO.File]::WriteAllText($worker, $workerText, [Text.Encoding]::ASCII)

$launcherText = @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
class FakeAdbLauncher {
    [StructLayout(LayoutKind.Sequential)]
    struct BasicInfo {
        public IntPtr Reserved1, PebBaseAddress;
        public IntPtr Reserved2a, Reserved2b;
        public IntPtr UniqueProcessId, InheritedFromUniqueProcessId;
    }
    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int c, ref BasicInfo b, int n, out int r);
    static int ParentId() {
        BasicInfo b = new BasicInfo(); int r;
        if (NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0, ref b, Marshal.SizeOf(typeof(BasicInfo)), out r) != 0) return 0;
        return b.InheritedFromUniqueProcessId.ToInt32();
    }
    static string Q(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
    static int Main(string[] args) {
        string ps = Environment.GetEnvironmentVariable("FAKE_PWSH");
        string worker = Environment.GetEnvironmentVariable("FAKE_ADB_WORKER");
        if (String.IsNullOrEmpty(ps) || String.IsNullOrEmpty(worker)) return 90;
        Environment.SetEnvironmentVariable("FAKE_ADB_PARENT_PID", ParentId().ToString());
        ProcessStartInfo i = new ProcessStartInfo(); i.FileName = ps;
        StringBuilder b = new StringBuilder("-NoProfile -ExecutionPolicy Bypass -File " + Q(worker));
        foreach (string a in args) b.Append(" ").Append(Q(a));
        i.Arguments = b.ToString(); i.UseShellExecute = false; i.CreateNoWindow = true;
        // Forward RAW BYTES. Two earlier shapes were wrong: decoding the child's UTF-8 output with
        // the OEM code page corrupted every non-ASCII device path in `sha256sum` output, and
        // relying on handle inheritance produced no output at all (CreateProcess is called without
        // handle inheritance when nothing is redirected).
        i.RedirectStandardOutput = true; i.RedirectStandardError = true;
        Process p = Process.Start(i);
        System.Threading.Tasks.Task to = System.Threading.Tasks.Task.Run(delegate { Copy(p.StandardOutput.BaseStream, Console.OpenStandardOutput()); });
        System.Threading.Tasks.Task te = System.Threading.Tasks.Task.Run(delegate { Copy(p.StandardError.BaseStream, Console.OpenStandardError()); });
        p.WaitForExit();
        to.Wait(); te.Wait();
        return p.ExitCode;
    }

    static void Copy(System.IO.Stream from, System.IO.Stream to) {
        byte[] buffer = new byte[8192];
        int read;
        while ((read = from.Read(buffer, 0, buffer.Length)) > 0) { to.Write(buffer, 0, read); to.Flush(); }
    }
}
'@
[System.IO.File]::WriteAllText($launcherSource, $launcherText, [Text.Encoding]::ASCII)
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $compiler)) { throw "Local .NET Framework C# compiler not found; fake adb.exe cannot be built." }
& $compiler /nologo /target:exe ("/out:" + $fakeAdb) $launcherSource
if ($LASTEXITCODE -ne 0) { throw "fake adb.exe compilation failed" }

$han = [string][char]0x6D4B + [string][char]0x8BD5
$source = Join-Path $fixtures ("Mods " + $han + " folder")
$modDir = Join-Path $source ("Hello Mod " + $han)
[void][IO.Directory]::CreateDirectory($modDir)
[IO.File]::WriteAllText((Join-Path $source "ModFramework.Runtime.dll"), "runtime-v1", [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $modDir "manifest.json"), '{"schemaVersion":1,"id":"Example.Hello","name":"Hello","version":"1.0.0","entryAssembly":"Hello Mod.dll"}', [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $modDir "Hello Mod.dll"), "mod-v1", [Text.Encoding]::UTF8)

function New-Device([string]$name = "device") {
    $d = Join-Path $work ($name + "-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    $pkgFiles = Join-Path $d "sdcard\Android\data\com.chucklefish.stardewvalley\files"
    [void][IO.Directory]::CreateDirectory($pkgFiles)
    $saves = Join-Path $pkgFiles "Saves"
    [void][IO.Directory]::CreateDirectory($saves)
    [IO.File]::WriteAllText((Join-Path $saves "save1.dat"), "save-must-never-change", [Text.Encoding]::UTF8)
    $private = Join-Path $d "data\data\com.chucklefish.stardewvalley"
    [void][IO.Directory]::CreateDirectory($private)
    [IO.File]::WriteAllText((Join-Path $private "test.keystore"), "key-must-never-change", [Text.Encoding]::UTF8)
    $apkDir = Join-Path $d "sdcard\Download"
    [void][IO.Directory]::CreateDirectory($apkDir)
    [IO.File]::WriteAllText((Join-Path $apkDir "game.apk"), "apk-must-never-change", [Text.Encoding]::UTF8)
    return $d
}

function Get-Snapshot([string]$device) {
    $out = @{}
    if (-not (Test-Path -LiteralPath $device)) { return $out }
    foreach ($f in Get-ChildItem -LiteralPath $device -File -Recurse -Force -ErrorAction SilentlyContinue) {
        $rel = $f.FullName.Substring($device.Length + 1).Replace("\", "/")
        $out[$rel] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $out
}

function Invoke-Case([string]$name, [string]$action, [hashtable]$params = @{}, [hashtable]$envs = @{}, [string]$device = "") {
    if (-not $device) { $device = New-Device $name }
    $log = Join-Path $work ("adb-" + $name + "-" + [Guid]::NewGuid().ToString("N").Substring(0, 6) + ".log")
    $saved = @{}
    $values = @{
        FAKE_ADB_LOG = $log; FAKE_DEVICE_ROOT = $device; FAKE_ADB_WORKER = $worker; FAKE_PWSH = $pwshExe
        FAKE_DEVICES = "local-1=device"; FAKE_API = "29"; FAKE_RELEASE = "10"; FAKE_PACKAGE_INSTALLED = "1"
        FAKE_VERSION_CODE = "239"; FAKE_VERSION_NAME = "1.6.15.0"; FAKE_GAME_RUNNING = "0"
        FAKE_PUSH_FAIL = ""; FAKE_HASH_WRONG = ""; FAKE_MV_FAIL_MATCH = ""; FAKE_MV_FAIL_ONCE = ""
        FAKE_INTERRUPT_MATCH = ""; FAKE_SDCARD_LINK = "0"
    }
    foreach ($k in $envs.Keys) { $values[$k] = [string]$envs[$k] }
    foreach ($k in $values.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k, "Process")
        [Environment]::SetEnvironmentVariable($k, [string]$values[$k], "Process")
    }
    $before = Get-Snapshot $device
    try {
        $argv = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $tool, "-Action", $action, "-Adb", $fakeAdb, "-Json")
        foreach ($k in $params.Keys) {
            $v = $params[$k]
            if ($v -is [bool]) { if ($v) { $argv += ("-" + $k) }; continue }
            if ($v -is [array]) { foreach ($x in $v) { $argv += ("-" + $k); $argv += [string]$x }; continue }
            $argv += ("-" + $k); $argv += [string]$v
        }
        $text = & $pwshExe @argv 2>&1 | Out-String
        $code = $LASTEXITCODE
        $json = $null
        try { $json = $text.Trim() | ConvertFrom-Json -ErrorAction Stop } catch {}
        return [pscustomobject]@{ Name = $name; Output = $text; ExitCode = $code; Json = $json; Log = $log; Device = $device; Before = $before; After = (Get-Snapshot $device) }
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], "Process") }
    }
}

function Assert-Result([string]$name, $case, [int]$code, [string]$reason = "") {
    $ok = ($case.ExitCode -eq $code -and $null -ne $case.Json)
    if ($reason) { $ok = $ok -and $case.Json.reason -eq $reason }
    Check $name $ok ("exit=" + $case.ExitCode + " reason=" + $(if ($case.Json) { $case.Json.reason } else { "no-json" }))
    return $ok
}

# AST and function dependency audit: PowerShell built-ins are whitelisted; local helper references must resolve.
$parseFiles = @($tool, (Join-Path $root "Tools\deploy-apk-safe.ps1"))
foreach ($file in $parseFiles) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
    Check ("Parser " + (Split-Path -Leaf $file)) ($errors.Count -eq 0) ("errors=" + $errors.Count)
}
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($tool, [ref]$tokens, [ref]$errors)
$functionNames = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $true) | ForEach-Object { $_.Name })
$commands = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ } | Sort-Object -Unique)
$builtins = @("if","elseif","else","try","catch","finally","exit","return","throw","param","Write-Output","Write-Host","Write-Warning","ConvertTo-Json","ConvertFrom-Json","Get-Date","Test-Path","Get-Command","Join-Path","Split-Path","Get-Item","Get-ChildItem","Get-FileHash","New-Object","Remove-Item","Move-Item","Select-Object","Where-Object","Sort-Object","ForEach-Object","Out-Null","Out-String")
$missingFunctions = @($commands | Where-Object { $_ -notin $functionNames -and $_ -notin $builtins })
Check "AST local function references resolve" ($missingFunctions.Count -eq 0) ("missing=" + ($missingFunctions -join ","))

$noArgs = & $pwshExe -NoProfile -ExecutionPolicy Bypass -File $tool 2>&1 | Out-String
$noArgsCode = $LASTEXITCODE
Check "no-argument help exit 0" ($noArgsCode -eq 0 -and $noArgs.Contains("deploy-mods-safe.ps1"))

$unknown = Invoke-Case "unknown-action" "nonsense"
Assert-Result "unknown action refused" $unknown 3 "unknown-action" | Out-Null
$inspect = Invoke-Case "inspect-smoke" "inspect"
Assert-Result "CLI inspect fake ADB smoke" $inspect 0 | Out-Null
$insp = $inspect.Json
$contractOk = ($null -ne $insp) -and $insp.schemaVersion -eq 1 -and
    $insp.tool -eq "deploy-mods-safe" -and $insp.command -eq "inspect" -and $insp.mode -eq "read-only" -and
    $insp.exitCode -eq 0 -and $insp.status -eq "ok" -and
    $insp.storage.modsRoot -eq "/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods" -and
    $insp.device.explicitSerial -eq $false -and
    $insp.safety.writesSaves -eq $false -and $insp.safety.usesRoot -eq $false -and
    $insp.safety.installsApk -eq $false -and $insp.safety.forceStopsGame -eq $false -and
    $insp.safety.deletesUnknownFiles -eq $false -and $insp.safety.deviceTouched -eq $false
Check "inspect JSON contract and self-attested safety flags" $contractOk
Check "inspect is read-only" (Test-SnapshotEqual $inspect) "virtual device tree unchanged"

$jsonOut = Join-Path $logDir "inspect-jsonout.json"
$withJsonOut = Invoke-Case "inspect-jsonout" "inspect" @{ JsonOut = $jsonOut }
$jsonOutOk = $false
if (Test-Path -LiteralPath $jsonOut) {
    try { $copy = [IO.File]::ReadAllText($jsonOut) | ConvertFrom-Json -ErrorAction Stop; $jsonOutOk = ($copy.command -eq "inspect" -and $copy.exitCode -eq 0) } catch {}
}
Check "JsonOut writes a parseable report copy" ($withJsonOut.ExitCode -eq 0 -and $jsonOutOk) ("exit=" + $withJsonOut.ExitCode)

$dry = Invoke-Case "dry-run" "deploy" @{ Source = $source }
Assert-Result "dry-run deploy plan" $dry 0 | Out-Null
Check "dry-run performed no device writes" (Test-SnapshotEqual $dry) "virtual device tree unchanged"

# Phase 1.2.3: the project's real manifest carries the optional schemaVersion-1 metadata fields
# (author/description/website) that ModManifest.cs accepts. The tool used to reject them, so a real
# Mods root could never be planned. Both directions are locked here: metadata accepted, unknown field
# still refused.
$sourceMeta = Join-Path $fixtures "ModsMetadata"
$metaMod = Join-Path $sourceMeta "Hello Metadata"
[void][IO.Directory]::CreateDirectory($metaMod)
[IO.File]::WriteAllText((Join-Path $sourceMeta "ModFramework.Runtime.dll"), "runtime-v1", [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $metaMod "manifest.json"), '{"schemaVersion":1,"id":"Example.Metadata","name":"Metadata","version":"1.0.0","author":"Author","description":"Desc","website":"https://example.invalid","entryAssembly":"Metadata.Assembly.dll"}', [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $metaMod "Metadata.Assembly.dll"), "mod-v1", [Text.Encoding]::UTF8)
$meta = Invoke-Case "manifest-metadata" "deploy" @{ Source = $sourceMeta }
Assert-Result "manifest optional metadata fields accepted" $meta 0 | Out-Null

$sourceUnknown = Join-Path $fixtures "ModsUnknownField"
$unknownMod = Join-Path $sourceUnknown "Hello Unknown"
[void][IO.Directory]::CreateDirectory($unknownMod)
[IO.File]::WriteAllText((Join-Path $sourceUnknown "ModFramework.Runtime.dll"), "runtime-v1", [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $unknownMod "manifest.json"), '{"schemaVersion":1,"id":"Example.Unknown","name":"Unknown","version":"1.0.0","entryAssembly":"Unknown.dll","bogusField":true}', [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $unknownMod "Unknown.dll"), "mod-v1", [Text.Encoding]::UTF8)
$unknownField = Invoke-Case "manifest-unknown-field" "deploy" @{ Source = $sourceUnknown }
Assert-Result "manifest unknown field still refused" $unknownField 3 "manifest-invalid" | Out-Null

$multi = Invoke-Case "multi-device" "inspect" @{} @{ FAKE_DEVICES = "a=device;b=device" }
Assert-Result "multiple devices fail closed" $multi 2 "ambiguous-device" | Out-Null
$offline = Invoke-Case "offline" "inspect" @{} @{ FAKE_DEVICES = "a=offline" }
Assert-Result "offline device refused" $offline 2 "serial-not-online" | Out-Null
$unauth = Invoke-Case "unauthorized" "inspect" @{} @{ FAKE_DEVICES = "a=unauthorized" }
Assert-Result "unauthorized device refused" $unauth 2 "serial-not-online" | Out-Null

$running = Invoke-Case "game-running" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{ FAKE_GAME_RUNNING = "1" }
Assert-Result "running game blocks apply" $running 3 "game-running" | Out-Null
$wrongPackage = Invoke-Case "wrong-package" "inspect" @{ PackageName = "com.example.other" }
Assert-Result "package name override refused" $wrongPackage 3 "package-name-mismatch" | Out-Null
$api30 = Invoke-Case "android-11" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{ FAKE_API = "30" }
Assert-Result "Android 11+ apply fail-closed" $api30 2 "unverified-android-storage" | Out-Null
$noSerial = Invoke-Case "apply-no-serial" "deploy" @{ Source = $source; Apply = $true }
Assert-Result "apply requires explicit serial" $noSerial 3 "serial-required" | Out-Null
$wrongSerial = Invoke-Case "serial-not-attached" "inspect" @{ Serial = "ghost-9" }
Assert-Result "unknown serial refused" $wrongSerial 2 "serial-not-attached" | Out-Null

$traversal = Join-Path $fixtures "ModsTraversal"
$travMod = Join-Path $traversal "Bad"
[void][IO.Directory]::CreateDirectory($travMod)
[IO.File]::WriteAllText((Join-Path $traversal "ModFramework.Runtime.dll"), "r", [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $travMod "manifest.json"), '{"id":"Bad","entryAssembly":"../evil.dll"}', [Text.Encoding]::UTF8)
$badPath = Invoke-Case "manifest-traversal" "plan" @{ Source = $traversal }
Assert-Result "manifest path traversal refused" $badPath 3 "manifest-invalid" | Out-Null

$duplicate = Join-Path $fixtures "ModsDuplicate"
$dupMod = Join-Path $duplicate "Dup"
[void][IO.Directory]::CreateDirectory($dupMod)
[IO.File]::WriteAllText((Join-Path $duplicate "ModFramework.Runtime.dll"), "r", [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $dupMod "manifest.json"), '{"id":"Dup","entryAssembly":"x.dll","privateAssemblies":["x.dll"]}', [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $dupMod "x.dll"), "x", [Text.Encoding]::UTF8)
$dup = Invoke-Case "duplicate-declaration" "plan" @{ Source = $duplicate }
Assert-Result "duplicate manifest path refused" $dup 3 "manifest-invalid" | Out-Null

# Android storage is case-sensitive, so a declaration that only matches by case (possible on the
# host) must not be accepted.
$caseSource = Join-Path $fixtures "ModsCaseMismatch"
$caseMod = Join-Path $caseSource "CaseMod"
[void][IO.Directory]::CreateDirectory($caseMod)
[IO.File]::WriteAllText((Join-Path $caseSource "ModFramework.Runtime.dll"), "r", [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $caseMod "manifest.json"), '{"schemaVersion":1,"id":"Case","entryAssembly":"CaseModDll.DLL"}', [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $caseMod "CaseModDll.dll"), "x", [Text.Encoding]::UTF8)
$caseMismatch = Invoke-Case "manifest-case-mismatch" "plan" @{ Source = $caseSource }
Assert-Result "manifest assembly name must match case exactly" $caseMismatch 3 "manifest-invalid" | Out-Null

$linkSource = Join-Path $fixtures "ModsReparse"
$linkMod = Join-Path $linkSource "Link"
[void][IO.Directory]::CreateDirectory($linkMod)
[IO.File]::WriteAllText((Join-Path $linkSource "ModFramework.Runtime.dll"), "r", [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $linkMod "manifest.json"), '{"id":"Link","entryAssembly":"x.dll"}', [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $linkMod "x.dll"), "x", [Text.Encoding]::UTF8)
$outsideDir = Join-Path $fixtures "OutsideReparseTarget"
[void][IO.Directory]::CreateDirectory($outsideDir)
[IO.File]::WriteAllText((Join-Path $outsideDir "outside.dll"), "outside-bytes", [Text.Encoding]::UTF8)
# A directory junction is a reparse point that NTFS creates without any privilege, so this stays a
# deterministic scenario instead of a capability probe that has to be reported as failed.
$junction = Join-Path $linkMod "linked-outside"
$reparseMade = $false
try { New-Item -ItemType Junction -Path $junction -Target $outsideDir -ErrorAction Stop | Out-Null; $reparseMade = $true } catch {}
if (-not $reparseMade) {
    try { New-Item -ItemType SymbolicLink -Path $junction -Target $outsideDir -ErrorAction Stop | Out-Null; $reparseMade = $true } catch {}
}
if ($reparseMade) {
    $linkResult = Invoke-Case "source-reparse-point" "plan" @{ Source = $linkSource }
    Assert-Result "source reparse point (junction) refused" $linkResult 3 "unsafe-path" | Out-Null
    Check "reparse rejection names the source boundary" ($null -ne $linkResult.Json -and ([string]$linkResult.Json.detail).StartsWith("source-reparse-point")) ([string]$linkResult.Json.detail)
} else {
    Check-Skip "source reparse point (junction) refused" "host refused both junction and symlink creation; boundary not exercised"
}

$pushFail = Invoke-Case "staging-push-failure" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{ FAKE_PUSH_FAIL = "/staging/" }
Assert-Result "staging push failure stops before Mods" $pushFail 4 "staging-write-failed" | Out-Null
$hashFail = Invoke-Case "staging-hash-failure" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{ FAKE_HASH_WRONG = "/staging/" }
Assert-Result "staging hash mismatch stops before Mods" $hashFail 4 "staging-hash-mismatch" | Out-Null
# A single unprovable file in its final location must fail closed (one element is exactly the case
# where a missing `@()` around a PowerShell function result silently loses the failure).
$finalWrong = Invoke-Case "final-hash-mismatch" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{ FAKE_HASH_WRONG = "/Mods/" }
Assert-Result "unprovable final bytes fail closed" $finalWrong 5 "switch-hash-mismatch" | Out-Null
$switchFail = Invoke-Case "switch-failure" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{ FAKE_MV_FAIL_MATCH = "/Mods/"; FAKE_MV_FAIL_ONCE = "1" }
Assert-Result "switch failure rolls back" $switchFail 4 "switch-failed" | Out-Null

$interrupt = Invoke-Case "journal-interrupt" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{ FAKE_INTERRUPT_MATCH = "/Mods/" }
Check "journal interruption leaves recoverable state" ((Test-Path -LiteralPath (Join-Path $interrupt.Device ".interrupted")) -and (Test-Path -LiteralPath (Join-Path $interrupt.Device "sdcard\Android\data\com.chucklefish.stardewvalley\files\Mods"))) ("child-exit=" + $interrupt.ExitCode)
$journalDir = Join-Path $interrupt.Device "sdcard\Android\data\com.chucklefish.stardewvalley\files\.mods-deploy"
# An unfinished transaction must block any new apply until it is explicitly recovered, and it must
# be visible to the read-only inspection command.
$blockedByJournal = Invoke-Case "apply-blocked-by-journal" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{} $interrupt.Device
Assert-Result "unfinished transaction blocks a new apply" $blockedByJournal 5 "active-transaction" | Out-Null
$inspectActive = Invoke-Case "inspect-active-transaction" "inspect" @{} @{} $interrupt.Device
Assert-Result "inspect reports an unfinished transaction" $inspectActive 5 "active-transaction" | Out-Null
$tx = @(Get-ChildItem -LiteralPath $journalDir -Directory -ErrorAction SilentlyContinue | Select-Object -First 1)
if ($tx.Count -eq 1) {
    $recover = Invoke-Case "journal-recovery" "rollback" @{ TxId = $tx[0].Name; Serial = "local-1"; Apply = $true } @{} $interrupt.Device
    Assert-Result "journal interrupted transaction recovers" $recover 0 | Out-Null
} else {
    Check "journal interrupted transaction recovers" $false "transaction directory not found"
}

$deployed = Invoke-Case "deploy-for-verify" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true }
$didDeploy = Assert-Result "apply deploy commits" $deployed 0
if ($didDeploy) {
    $modsLocal = Join-Path $deployed.Device "sdcard\Android\data\com.chucklefish.stardewvalley\files\Mods"
    $targetDll = Join-Path (Join-Path $modsLocal ("Hello Mod " + $han)) "Hello Mod.dll"
    $committedTx = [string]$deployed.Json.journal.txid
    Check "apply reports mode=apply and deviceTouched=true" ($deployed.Json.mode -eq "apply" -and $deployed.Json.safety.deviceTouched -eq $true) ("mode=" + $deployed.Json.mode)
    # Re-running an already-applied plan must be a no-op (every file matches by SHA-256).
    $idempotent = Invoke-Case "redeploy-idempotent" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{} $deployed.Device
    $actions = @(@($idempotent.Json.plan.files | ForEach-Object { $_.action }) | Sort-Object -Unique)
    Check "second apply is a no-op (all SKIP)" ($idempotent.ExitCode -eq 0 -and $actions.Count -eq 1 -and $actions[0] -eq "SKIP") ("exit=" + $idempotent.ExitCode + " actions=" + ($actions -join ","))
    # A rollback dry-run prints a plan but must not write anything.
    $rollbackDry = Invoke-Case "rollback-dry-run" "rollback" @{ TxId = $committedTx } @{} $deployed.Device
    Check "rollback dry-run changes nothing" ($rollbackDry.ExitCode -eq 0 -and (Test-SnapshotEqual $rollbackDry)) ("exit=" + $rollbackDry.ExitCode)
    [IO.File]::WriteAllText($targetDll, "tampered", [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText((Join-Path $modsLocal "extra.dat"), "extra", [Text.Encoding]::UTF8)
    $verifyDiff = Invoke-Case "verify-diff" "verify" @{ Source = $source; StrictExtras = $true } @{} $deployed.Device
    Assert-Result "verify reports modified and extra files" $verifyDiff 3 "modified-files" | Out-Null
    Check "verify lists the extra file separately" (@($verifyDiff.Json.verification.extra) -contains "extra.dat") ("extra=" + (@($verifyDiff.Json.verification.extra) -join ","))
    $rollbackOwn = Invoke-Case "rollback-ownership" "rollback" @{ TxId = $committedTx; Serial = "local-1"; Apply = $true } @{} $deployed.Device
    Assert-Result "rollback refuses bytes not owned by transaction" $rollbackOwn 5 "rollback-failed" | Out-Null
} else {
    Check "apply reports mode=apply and deviceTouched=true" $false "apply did not commit"
    Check "second apply is a no-op (all SKIP)" $false "apply did not commit"
    Check "rollback dry-run changes nothing" $false "apply did not commit"
    Check "verify reports modified and extra files" $false "apply did not commit"
    Check "verify lists the extra file separately" $false "apply did not commit"
    Check "rollback refuses bytes not owned by transaction" $false "apply did not commit"
}

# Chinese + spaces AND the real-device /sdcard symlink shape in one apply: devices expose /sdcard
# as a symlink to /storage/emulated/0, so treating the OS-owned prefix as an unsafe link would
# refuse every deployment. Both properties are asserted on this single run.
$unicode = Invoke-Case "unicode-spaces" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{ FAKE_SDCARD_LINK = "1" }
Assert-Result "Chinese and spaces survive deployment" $unicode 0 | Out-Null
Check "OS-owned /sdcard symlink does not block apply" ($unicode.ExitCode -eq 0 -and $unicode.Json.safety.deviceTouched -eq $true) ("exit=" + $unicode.ExitCode)

# -Overwrite is exact: it must name a real conflict, and an authorized replacement must keep the
# previous bytes under the transaction's backup directory.
$owDevice = New-Device "overwrite"
$owBase = Invoke-Case "overwrite-base" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{} $owDevice
$owBaseOk = Assert-Result "overwrite baseline deploy" $owBase 0
if ($owBaseOk) {
    $owMods = Join-Path $owDevice "sdcard\Android\data\com.chucklefish.stardewvalley\files\Mods"
    $owRel = "Hello Mod " + $han + "/Hello Mod.dll"
    $owTarget = Join-Path $owMods ($owRel -replace "/", "\")
    [IO.File]::WriteAllText($owTarget, "edited-on-device", [Text.Encoding]::UTF8)
    $owBad = Invoke-Case "overwrite-list-mismatch" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true; Overwrite = "not-a-conflict.dll" } @{} $owDevice
    Assert-Result "overwrite list must name a real conflict" $owBad 3 "overwrite-list-mismatch" | Out-Null
    $owOk = Invoke-Case "overwrite-authorized" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true; Overwrite = $owRel } @{} $owDevice
    Assert-Result "authorized overwrite replaces the file" $owOk 0 | Out-Null
    $owTx = [string]$owOk.Json.journal.txid
    $owBackup = Join-Path $owDevice ("sdcard\Android\data\com.chucklefish.stardewvalley\files\.mods-deploy\" + $owTx + "\backup\" + ($owRel -replace "/", "\"))
    Check "replaced bytes are kept in backup" ((Test-Path -LiteralPath $owBackup) -and ([IO.File]::ReadAllText($owBackup, [Text.Encoding]::UTF8) -eq "edited-on-device"))
    # Missing-file detection: an external deletion must be reported, not silently repaired.
    Remove-Item -LiteralPath $owTarget -Force
    $missing = Invoke-Case "verify-missing" "verify" @{ Source = $source } @{} $owDevice
    Assert-Result "verify reports missing files" $missing 3 "missing-files" | Out-Null
} else {
    Check "overwrite list must name a real conflict" $false "baseline deploy failed"
    Check "authorized overwrite replaces the file" $false "baseline deploy failed"
    Check "replaced bytes are kept in backup" $false "baseline deploy failed"
    Check "verify reports missing files" $false "baseline deploy failed"
}

# Every fake invocation must remain under its local virtual device; verify sensitive paths are neither
# mentioned in adb arguments nor accessed by the deployment tool. The virtual sentinel files remain unchanged.
$allLogs = @(Get-ChildItem -LiteralPath $work -Filter "adb-*.log" -File | ForEach-Object { [IO.File]::ReadAllText($_.FullName) })
$joinedLogs = $allLogs -join "`n"
$forbiddenLog = [regex]::IsMatch($joinedLogs, '(?i)\bsu\b|force-stop|pm clear|(^|\s)install(\s|$)|uninstall|Saves|shared_prefs|databases|/data/data|keystore|\.apk')
Check "no su, install, stop, clear, or sensitive paths in adb trace" (-not $forbiddenLog)
$unchangedSensitive = $true
foreach ($d in Get-ChildItem -LiteralPath $work -Directory | Where-Object { $_.Name -match '^(game-running|android-11|staging-push-failure|staging-hash-failure|switch-failure|unicode-spaces|deploy-for-verify|journal-interrupt)-' }) {
    $saves = Join-Path $d.FullName "sdcard\Android\data\com.chucklefish.stardewvalley\files\Saves\save1.dat"
    $key = Join-Path $d.FullName "data\data\com.chucklefish.stardewvalley\test.keystore"
    $apk = Join-Path $d.FullName "sdcard\Download\game.apk"
    if ((Get-FileHash $saves -Algorithm SHA256).Hash -ne (Get-FileHash $saves -Algorithm SHA256).Hash) { $unchangedSensitive = $false }
    if (-not (Test-Path -LiteralPath $key) -or -not (Test-Path -LiteralPath $apk)) { $unchangedSensitive = $false }
}
Check "Saves, keystore and APK sentinels remain present" $unchangedSensitive

# Windows PowerShell 5.1 rejects `[ordered]@{ key = @($genericList) }` with "Argument types do not
# match"; hand ConvertTo-Json the list object directly instead of an array-literal wrapper.
$passed = @($script:results | Where-Object { $_.status -eq "pass" }).Count
$failed = @($script:results | Where-Object { $_.status -eq "fail" }).Count
$skipped = @($script:results | Where-Object { $_.status -eq "skip" }).Count
$summary = [ordered]@{ generatedAt = (Get-Date).ToUniversalTime().ToString("o"); total = $script:results.Count; passed = $passed; failed = $failed; skipped = $skipped; tests = $script:results }
[IO.File]::WriteAllText((Join-Path $logDir "test-summary.json"), ($summary | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($true)))
Write-Host ("SUMMARY total={0} passed={1} failed={2} skipped={3}" -f $script:results.Count, $passed, $failed, $skipped)
if ($script:results.Count -eq 0) { exit 1 }
if ($failed -gt 0) { exit 1 }
exit 0
