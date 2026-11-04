using System.Reflection;

namespace ModFramework.Runtime;

/// <summary>
/// Bootstrap 通过反射调用的入口：<c>ModFramework.Runtime.Loader.Start()</c>。
///
/// 约定：**成功返回 null，失败返回诊断字符串**（Bootstrap 会把非 null 变成异常抛出，
/// 这样消息不会被 TargetInvocationException 吞掉）。
///
/// 结构上刻意"每个 BCL 依赖一个独立方法"：如果某个 API 被 ILLink 裁剪，
/// 只有那一个方法的 JIT 会失败，异常在调用点被 catch 住，其余探测照常进行——
/// 一次部署就能列清"这个运行时还剩哪些 BCL 可用"。
/// </summary>
public static class Loader
{
    public static string? Start()
    {
        var report = "";
        report += Probe("locate", LocateSelf);
        report += Probe("mods-exists", ModsExists);
        report += Probe("write-log", WriteLog);
        report += Probe("probe-run", RunProbe);

        // 诊断构建：无论成败都把报告带回去，让 Bootstrap 抛出来，logcat 就能看到。
        // 等确认哪些 API 存活后，改成"成功返回 null"。
        return "loader-report: " + report;
    }

    private static string Probe(string name, Func<string> body)
    {
        try
        {
            return name + "=" + body() + "; ";
        }
        catch (Exception ex)
        {
            return name + "!=" + ex.GetType().Name + ": " + ex.Message + "; ";
        }
    }

    private static string Where()
    {
        var self = typeof(Loader).Assembly.Location;
        return string.IsNullOrEmpty(self) ? "(no-location)" : self;
    }

    private static string LocateSelf() => Where();

    private static string BaseDir()
    {
        var self = typeof(Loader).Assembly.Location;
        if (string.IsNullOrEmpty(self))
        {
            return "/data/data/com.chucklefish.stardewvalley/files/Mods";
        }
        var dir = Path.GetDirectoryName(self);
        return string.IsNullOrEmpty(dir) ? "/data/data/com.chucklefish.stardewvalley/files/Mods" : dir;
    }

    private static string ModsExists() => Directory.Exists(BaseDir()) ? "yes" : "no";

    // 注意：Directory.GetFiles / EnumerateFiles 在这个运行时里被 ILLink 裁掉了
    // （用 Android 参考程序集编译会直接报 CS0117），所以不做目录枚举。
    private static string WriteLog()
    {
        File.WriteAllText(Path.Combine(BaseDir(), "loader.log"),
            "loader-entered " + DateTimeOffset.Now.ToString("O") + Environment.NewLine + "self=" + Where());
        return "ok";
    }

    private static string RunProbe()
    {
        var probePath = Path.Combine(BaseDir(), "MonoRuntimeProbe.dll");
        if (!File.Exists(probePath))
        {
            return "absent";
        }
        var asm = Assembly.LoadFrom(probePath);
        var probe = asm.GetType("MonoRuntimeProbe.Probe");
        var run = probe?.GetMethod("Run");
        if (run is null)
        {
            return "no-method";
        }
        var result = run.Invoke(null, new object?[] { Path.Combine(BaseDir(), "probe-output"), false });
        return Convert.ToString(result) ?? "null";
    }
}
