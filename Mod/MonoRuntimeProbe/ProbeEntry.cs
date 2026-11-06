using System.Reflection;

namespace MonoRuntimeProbe;

/// <summary>
/// 运行时探针（Phase 0 历史用例 + Phase 1.1.4 安全化）。
///
/// 编码纪律（来自 Phase 0 实测）：
///   ILLink 裁剪会让**整个方法**在 JIT 阶段失败，方法内的 try/catch 拦不住。
///   因此：任何"未证实可用"的 BCL 成员必须单独放进一个小方法，
///   由调用方通过 <see cref="Case"/> 包裹调用。
/// 只有"已证实可用"的成员才允许出现在聚合方法（<see cref="RunSafe"/>）的方法体里：
///   string 拼接 / Path.Combine / File.Exists / File.WriteAllText / Assembly.LoadFrom /
///   Assembly.GetType / Type.GetMethod / MethodBase.Invoke。
///
/// Phase 1.1.4 安全契约（重要）：
///   * <see cref="RunSafe"/> 是本探针**唯一的常规入口**，它只做无副作用的内存内验证 +
///     只读元数据枚举；不含 P/Invoke、mprotect、RWX、机器码写入、Reflection.Emit、
///     DynamicMethod 或 Harmony。
///   * 历史上的 T11（mprotect 把代码页改 RWX）与 T12（写 ARM64 跳转桩）已经**移出默认编译**，
///     只在显式定义 <c>LEGACY_NATIVE_EXPERIMENTS</c> 时才存在，见
///     <c>NativePageProbe.cs</c> / <c>DetourProbe.cs</c>。它们不属于本阶段测试，不得运行。
///   * 报告里**不写**任何绝对路径：只写末段名字（<see cref="Leaf"/>）。
/// </summary>
public static class Probe
{
    /// <summary>安全报告的文件名（写在调用方给的目录下）。</summary>
    public const string SafeReportFileName = "probe-safe.txt";

    /// <summary>安全入口的版本标记，写进报告第一行，便于区分历史输出。</summary>
    public const string SafeModeMarker = "safe-only";

    public static event EventHandler<ProbeEventArgs>? SomethingHappened;

    private static int _staticCounter;

    public static string StaticGreeting(string who) => "static:" + who;

    public static string GenericEcho<T>(T value) => "generic<" + typeof(T).Name + ">:" + value;

    public static string GenericPair<TFirst, TSecond>(TFirst a, TSecond b)
        => "pair<" + typeof(TFirst).Name + "," + typeof(TSecond).Name + ">:" + a + "|" + b;

    /// <summary>
    /// 聚合方法（安全）：只做字符串拼接与"自己的方法"调用。
    ///
    /// 这里刻意不引用 <see cref="DetourProbe"/>、也不引用 <c>NativePageProbe</c>：
    /// 危险实验必须在编译期就不在这个方法可达的图里。
    /// </summary>
    /// <param name="outputDirectory">报告目录；空则退化为探针自身所在目录。</param>
    /// <returns>报告文件路径，或 <c>!write:&lt;异常&gt;</c>。</returns>
    public static string RunSafe(string outputDirectory)
    {
        var report = "MonoRuntimeProbe report" + Environment.NewLine;
        report += "mode      : " + SafeModeMarker + Environment.NewLine;
        report += "native    : none (no P/Invoke, no mprotect, no RWX, no detour)" + Environment.NewLine;
        report += "time      : " + Try(TimeNow) + Environment.NewLine;
        report += "basedir   : " + Try(BaseDirLeaf) + Environment.NewLine;

        report += Case("T00 dyn-code", DynCode) + Environment.NewLine;
        report += Case("T01 static", () => StaticGreeting("probe")) + Environment.NewLine;
        report += Case("T02 instance", () => new ProbeSubject().InstanceGreeting("probe")) + Environment.NewLine;
        report += Case("T03 generic", () => GenericEcho(42) + " | " + GenericEcho("text") + " | " + GenericPair(1, "x")) + Environment.NewLine;
        report += Case("T04 event", EventRoundtrip) + Environment.NewLine;
        report += Case("T05 reflection", ReflectionRoundtrip) + Environment.NewLine;
        report += Case("T06 ext-assembly", () => MonoRuntimeProbe.Dependency.DependencyMarker.Describe()) + Environment.NewLine;
        report += Case("T07 game-types-readonly", GameTypeSurface) + Environment.NewLine;

        var dir = string.IsNullOrEmpty(outputDirectory) ? Try(BaseDir) : outputDirectory;
        return WriteReport(dir, report);
    }

    /// <summary>
    /// Phase 0 的历史签名，保留只为让旧调用点继续编译。
    ///
    /// Phase 1.1.4 起它**只委托到 <see cref="RunSafe"/>**：<paramref name="allowGameInvocation"/>
    /// 被忽略（永远不调用 <c>Game1</c> 成员），T11/T12 也不在默认编译里。
    /// </summary>
    public static string Run(string outputDirectory, bool allowGameInvocation)
    {
        // 参数故意不使用：保留签名兼容，行为固定为安全路径。
        _ = allowGameInvocation;
        return RunSafe(outputDirectory);
    }

    // ---------------------------------------------------------------- 隔离的 BCL 访问

    private static string TimeNow() => DateTimeOffset.Now.ToString("O");

    private static string BaseDir() => AppContext.BaseDirectory;

    /// <summary>只返回末尾一段目录名：报告里不允许出现用户的绝对路径。</summary>
    private static string BaseDirLeaf() => Leaf(AppContext.BaseDirectory);

    private static string IsDynamicCode()
        => "supported=" + System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported
           + " compiled=" + System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled;

    private static string EnsureDir(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    private static string DynCode() => IsDynamicCode();

    // ---------------------------------------------------------------- 用例

    private static string EventRoundtrip()
    {
        var hits = 0;
        EventHandler<ProbeEventArgs> handler = (_, e) => { hits++; e.Handled = true; };
        SomethingHappened += handler;
        var payload = new ProbeEventArgs();
        SomethingHappened?.Invoke(null, payload);
        SomethingHappened -= handler;
        SomethingHappened?.Invoke(null, payload);
        if (hits != 1)
        {
            throw new InvalidOperationException("expected 1 call, got " + hits);
        }
        return "hits=" + hits + " handled=" + payload.Handled;
    }

    private static string ReflectionRoundtrip()
    {
        var t = typeof(Probe);
        var f = t.GetField("_staticCounter", BindingFlags.NonPublic | BindingFlags.Static)!;
        f.SetValue(null, 41);
        var read = (int)f.GetValue(null)!;
        var m = t.GetMethod(nameof(StaticGreeting), BindingFlags.Public | BindingFlags.Static)!;
        var text = (string)m.Invoke(null, new object[] { "reflection" })!;
        if (read != 41 || text.IndexOf("reflection", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("reflection roundtrip failed");
        }
        return "field=" + read + " invoke=" + text;
    }

    /// <summary>
    /// 只读枚举 <c>StardewValley.Game1</c> 的成员数量。
    /// **不调用任何游戏成员**（Phase 1.1.4 起不再提供"允许调用"的分支）。
    /// </summary>
    private static string GameTypeSurface()
    {
        var asm = Assembly.Load("StardewValley");
        var game1 = asm.GetType("StardewValley.Game1");
        if (game1 is null)
        {
            return "Game1 missing";
        }
        var methods = game1.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        var fields = game1.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        return "Game1 " + methods.Length + " methods/" + fields.Length + " fields (metadata only, nothing invoked)";
    }

    // ---------------------------------------------------------------- 收尾（同样隔离）

    private static string WriteReport(string dir, string text)
    {
        var ensured = Try(() => EnsureDir(dir));
        // 不用 StartsWith(string, StringComparison)：那个重载是否被裁剪未知。
        var usable = ensured.Length > 0 && ensured[0] != '!';
        var path = Path.Combine(usable ? ensured : dir, SafeReportFileName);
        try
        {
            File.WriteAllText(path, text);
            return path;
        }
        catch (Exception ex)
        {
            return "!write:" + ex.GetType().Name + ":" + ex.Message;
        }
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>路径末段。手工实现：不依赖 <c>Path.GetFileName</c> 对目录尾分隔符的处理。</summary>
    internal static string Leaf(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "(empty)";
        }
        var end = path.Length;
        while (end > 0 && (path[end - 1] == '/' || path[end - 1] == '\\'))
        {
            end--;
        }
        var start = end;
        while (start > 0 && path[start - 1] != '/' && path[start - 1] != '\\')
        {
            start--;
        }
        if (end <= start)
        {
            return "(root)";
        }
        return path.Substring(start, end - start);
    }

    private static string Case(string name, Func<string> body)
    {
        try
        {
            return "PASS " + name + " : " + body();
        }
        catch (Exception ex)
        {
            return "FAIL " + name + " : " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static string Try(Func<string> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex)
        {
            return "!" + ex.GetType().Name + ":" + ex.Message;
        }
    }
}

public sealed class ProbeEventArgs : EventArgs
{
    public bool Handled { get; set; }
}

public sealed class ProbeSubject
{
    private int _counter;

    public string InstanceGreeting(string who) => "instance:" + who + "#" + (++_counter);
}
