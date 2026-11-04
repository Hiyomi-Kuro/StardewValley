using System.Reflection;

namespace ModFramework.Runtime;

/// <summary>
/// Bootstrap 通过反射调用的入口：<c>ModFramework.Runtime.Loader.Start()</c>。
///
/// 返回契约（Bootstrap 依赖它）：
///   * **成功 → 返回 null**；
///   * **有探测项失败 → 返回诊断字符串**，Bootstrap 会把它包成异常。
/// 两种情况下完整报告都会写到 <c>&lt;自身目录&gt;/loader-report.txt</c>，
/// 因为正式构建里 Bootstrap 的 catch 会吞掉异常（不能让模组问题挡住游戏启动），
/// 只靠异常消息在默认构建里是取不到诊断的。
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
        var ok = true;
        ok &= Probe("locate", LocateSelf, ref report);
        ok &= Probe("mods-exists", ModsExists, ref report);
        ok &= Probe("write-log", WriteLog, ref report);
        ok &= Probe("probe-run", RunProbe, ref report);

        var saveNote = SaveReport(report);
        return ok ? null : "loader-report: " + report + saveNote;
    }

    private static bool Probe(string name, Func<string> body, ref string report)
    {
        try
        {
            report += name + "=" + body() + "; ";
            return true;
        }
        catch (Exception ex)
        {
            report += name + "!=" + ex.GetType().Name + ": " + ex.Message + "; ";
            return false;
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

    /// <summary>把完整探测报告落盘；写失败只作为附注返回，不改变成功/失败判定。</summary>
    private static string SaveReport(string report)
    {
        try
        {
            File.WriteAllText(Path.Combine(BaseDir(), "loader-report.txt"),
                "loader-report " + DateTimeOffset.Now.ToString("O") + Environment.NewLine + report);
            return "";
        }
        catch (Exception ex)
        {
            return " (report-save-failed: " + ex.GetType().Name + ": " + ex.Message + ")";
        }
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
