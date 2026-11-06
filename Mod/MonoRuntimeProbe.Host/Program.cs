using MonoRuntimeProbe;

// 桌面基线：验证探针**安全入口**本身可运行，并给出一份可对比的 host 能力快照。
//
// Phase 1.1.4 起只有安全入口：T11/T12（mprotect / detour）不在默认编译里，
// --allow-game-invocation 也不再存在（探针永远不调用 Game1 成员）。
//
// 用法: dotnet run --project Mod/MonoRuntimeProbe.Host -- <outputDir>
var outputDir = args.Length > 0 && !args[0].StartsWith("--")
    ? args[0]
    : Path.Combine(Path.GetTempPath(), "sv-probe-desktop");

Console.WriteLine($"mode     : {Probe.SafeModeMarker}");
Console.WriteLine($"output   : {outputDir}");

var path = Probe.RunSafe(outputDir);
Console.WriteLine($"report   : {path}");
if (path.Length > 0 && path[0] != '!')
{
    Console.WriteLine(File.ReadAllText(path));
}
return 0;
