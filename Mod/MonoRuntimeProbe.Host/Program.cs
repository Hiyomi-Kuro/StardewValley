using MonoRuntimeProbe;

// 桌面基线：验证探针本身可运行，并给出一份可对比的 host 能力快照。
// 用法: dotnet run --project Mod/MonoRuntimeProbe.Host -- <outputDir> [--allow-game-invocation]
var outputDir = args.Length > 0 && !args[0].StartsWith("--")
    ? args[0]
    : Path.Combine(Path.GetTempPath(), "sv-probe-desktop");
var allowInvocation = args.Contains("--allow-game-invocation");

Console.WriteLine($"output   : {outputDir}");
Console.WriteLine($"game-call: {allowInvocation}");

var path = Probe.Run(outputDir, allowInvocation);
Console.WriteLine($"report   : {path}");
Console.WriteLine(File.ReadAllText(Path.ChangeExtension(path, ".log")));
return 0;
