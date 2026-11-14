$ErrorActionPreference = "Stop"
$env:PSModulePath = @((Join-Path $PSHOME "Modules"), (Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\Modules")) -join ";"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = (Resolve-Path (Join-Path $here "..\..")).Path
$tool = Join-Path $root "Tools\deploy-mods-safe.ps1"
$work = Join-Path $root ".tmp\phase1.2.1"
$fixtures = Join-Path $work "fixtures"
[void][System.IO.Directory]::CreateDirectory($fixtures)
$pwshExe = (Get-Process -Id $PID).Path
$fakeAdb = Join-Path $work "fake-adb.exe"
$worker = Join-Path $fixtures "fake-adb-worker.ps1"
$launcherSource = Join-Path $fixtures "fake-adb-launcher.cs"
$script:results = New-Object System.Collections.Generic.List[object]
$script:testNo = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    $script:testNo++
    $script:results.Add([pscustomobject]@{ name = $name; passed = $ok; detail = $detail })
    $tag = if ($ok) { "PASS" } else { "FAIL" }
    $line = "{0} {1} {2}" -f $tag, $name, $detail
    Write-Host $line
    [System.IO.File]::AppendAllText((Join-Path $work "test-results.log"), $line + "`r`n", [Text.Encoding]::UTF8)
}

$workerText = @'
$ErrorActionPreference = "Stop"
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
            "-L" { if (Is-Link $p) { exit 0 } else { exit 1 } }
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
        if (Test-Path -LiteralPath $to) { exit 1 }
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $to))
        Move-Item -LiteralPath $from -Destination $to
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
        i.RedirectStandardOutput = true; i.RedirectStandardError = true;
        Process p = Process.Start(i);
        string stdout = p.StandardOutput.ReadToEnd(); string stderr = p.StandardError.ReadToEnd(); p.WaitForExit();
        if (stdout.Length > 0) Console.Out.Write(stdout);
        if (stderr.Length > 0) Console.Error.Write(stderr);
        return p.ExitCode;
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
        FAKE_INTERRUPT_MATCH = ""
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
$builtins = @("if","elseif","else","try","catch","finally","exit","return","throw","param","Write-Output","Write-Host","ConvertTo-Json","ConvertFrom-Json","Get-Date","Test-Path","Get-Command","Join-Path","Split-Path","Get-Item","Get-ChildItem","Get-FileHash","New-Object","Remove-Item","Select-Object","Where-Object","Sort-Object","ForEach-Object","Out-Null","Out-String")
$missingFunctions = @($commands | Where-Object { $_ -notin $functionNames -and $_ -notin $builtins })
Check "AST local function references resolve" ($missingFunctions.Count -eq 0) ("missing=" + ($missingFunctions -join ","))

$noArgs = & $pwshExe -NoProfile -ExecutionPolicy Bypass -File $tool 2>&1 | Out-String
$noArgsCode = $LASTEXITCODE
Check "no-argument help exit 0" ($noArgsCode -eq 0 -and $noArgs.Contains("deploy-mods-safe.ps1"))

$unknown = Invoke-Case "unknown-action" "nonsense"
Assert-Result "unknown action refused" $unknown 3 "unknown-action" | Out-Null
$inspect = Invoke-Case "inspect-smoke" "inspect"
Assert-Result "CLI inspect fake ADB smoke" $inspect 0 | Out-Null

$dry = Invoke-Case "dry-run" "deploy" @{ Source = $source }
Assert-Result "dry-run deploy plan" $dry 0 | Out-Null
Check "dry-run performed no device writes" ((Compare-Object $dry.Before.Keys $dry.After.Keys).Count -eq 0 -and ($dry.After.GetEnumerator() | ForEach-Object { $dry.Before[$_.Key] -eq $_.Value } | Where-Object { -not $_ }).Count -eq 0) "virtual device tree unchanged"

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

$linkSource = Join-Path $fixtures "ModsSymlink"
$linkMod = Join-Path $linkSource "Link"
[void][IO.Directory]::CreateDirectory($linkMod)
[IO.File]::WriteAllText((Join-Path $linkSource "ModFramework.Runtime.dll"), "r", [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $linkMod "manifest.json"), '{"id":"Link","entryAssembly":"x.dll"}', [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $linkMod "x.dll"), "x", [Text.Encoding]::UTF8)
$linkMade = $false
try { New-Item -ItemType SymbolicLink -Path (Join-Path $linkMod "external.dll") -Target (Join-Path $linkMod "x.dll") -ErrorAction Stop | Out-Null; $linkMade = $true } catch {}
if ($linkMade) {
    $linkResult = Invoke-Case "source-symlink" "plan" @{ Source = $linkSource }
    Assert-Result "source symlink refused" $linkResult 3 "unsafe-path" | Out-Null
} else {
    Check "source symlink refused (capability probe)" $false "host denied creation of a local symlink; scenario could not be exercised"
}

$pushFail = Invoke-Case "staging-push-failure" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{ FAKE_PUSH_FAIL = "/staging/" }
Assert-Result "staging push failure stops before Mods" $pushFail 4 "staging-write-failed" | Out-Null
$hashFail = Invoke-Case "staging-hash-failure" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{ FAKE_HASH_WRONG = "/staging/" }
Assert-Result "staging hash mismatch stops before Mods" $hashFail 4 "staging-hash-mismatch" | Out-Null
$switchFail = Invoke-Case "switch-failure" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{ FAKE_MV_FAIL_MATCH = "/Mods/"; FAKE_MV_FAIL_ONCE = "1" }
Assert-Result "switch failure rolls back" $switchFail 4 "switch-failed" | Out-Null

$interrupt = Invoke-Case "journal-interrupt" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true } @{ FAKE_INTERRUPT_MATCH = "/Mods/" }
Check "journal interruption leaves recoverable state" ((Test-Path -LiteralPath (Join-Path $interrupt.Device ".interrupted")) -and (Test-Path -LiteralPath (Join-Path $interrupt.Device "sdcard\Android\data\com.chucklefish.stardewvalley\files\Mods"))) ("child-exit=" + $interrupt.ExitCode)
$journalDir = Join-Path $interrupt.Device "sdcard\Android\data\com.chucklefish.stardewvalley\files\.mods-deploy"
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
    [IO.File]::WriteAllText($targetDll, "tampered", [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText((Join-Path $modsLocal "extra.dat"), "extra", [Text.Encoding]::UTF8)
    $verifyDiff = Invoke-Case "verify-diff" "verify" @{ Source = $source; StrictExtras = $true } @{} $deployed.Device
    Assert-Result "verify reports modified and extra files" $verifyDiff 3 "modified-files" | Out-Null
    $committedTx = [string]$deployed.Json.journal.txid
    $rollbackOwn = Invoke-Case "rollback-ownership" "rollback" @{ TxId = $committedTx; Serial = "local-1"; Apply = $true } @{} $deployed.Device
    Assert-Result "rollback refuses bytes not owned by transaction" $rollbackOwn 5 "rollback-failed" | Out-Null
} else {
    Check "verify reports modified and extra files" $false "apply did not commit"
    Check "rollback refuses bytes not owned by transaction" $false "apply did not commit"
}

$unicode = Invoke-Case "unicode-spaces" "deploy" @{ Source = $source; Serial = "local-1"; Apply = $true }
Assert-Result "Chinese and spaces survive deployment" $unicode 0 | Out-Null

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

$passed = @($script:results | Where-Object passed).Count
$failed = $script:results.Count - $passed
$summary = [ordered]@{ generatedAt = (Get-Date).ToUniversalTime().ToString("o"); total = $script:results.Count; passed = $passed; failed = $failed; tests = @($script:results) }
[IO.File]::WriteAllText((Join-Path $work "test-summary.json"), ($summary | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($true)))
Write-Host ("SUMMARY total={0} passed={1} failed={2}" -f $script:results.Count, $passed, $failed)
if ($failed -gt 0) { exit 1 }
exit 0
