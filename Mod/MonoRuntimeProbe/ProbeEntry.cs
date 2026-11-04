using System.Reflection;

namespace MonoRuntimeProbe;

/// <summary>
/// 运行时探针。
///
/// 编码纪律（来自 Phase 0 实测）：
///   ILLink 裁剪会让**整个方法**在 JIT 阶段失败，方法内的 try/catch 拦不住。
///   因此：任何"未证实可用"的 BCL 成员必须单独放进一个小方法，
///   由调用方通过 <see cref="Try(Func{string})"/> 包裹调用。
/// 只有"已证实可用"的成员才允许出现在聚合方法（如 Run）的方法体里：
///   string 拼接 / Path.Combine / File.Exists / File.WriteAllText / Assembly.LoadFrom /
///   Assembly.GetType / Type.GetMethod / MethodBase.Invoke。
/// </summary>
public static class Probe
{
    public static event EventHandler<ProbeEventArgs>? SomethingHappened;

    private static int _staticCounter;

    public static string StaticGreeting(string who) => "static:" + who;

    public static string GenericEcho<T>(T value) => "generic<" + typeof(T).Name + ">:" + value;

    public static string GenericPair<TFirst, TSecond>(TFirst a, TSecond b)
        => "pair<" + typeof(TFirst).Name + "," + typeof(TSecond).Name + ">:" + a + "|" + b;

    /// <summary>聚合方法：只做字符串拼接与"自己的方法"调用。</summary>
    public static string Run(string outputDirectory, bool allowGameInvocation)
    {
        var report = "MonoRuntimeProbe report" + Environment.NewLine;
        report += "time    : " + Try(TimeNow) + Environment.NewLine;
        report += "basedir : " + Try(BaseDir) + Environment.NewLine;

        report += Case("T00 dyn-code", DynCode) + Environment.NewLine;
        report += Case("T01 static", () => StaticGreeting("probe")) + Environment.NewLine;
        report += Case("T02 instance", () => new ProbeSubject().InstanceGreeting("probe")) + Environment.NewLine;
        report += Case("T03 generic", () => GenericEcho(42) + " | " + GenericEcho("text") + " | " + GenericPair(1, "x")) + Environment.NewLine;
        report += Case("T04 event", EventRoundtrip) + Environment.NewLine;
        report += Case("T05 reflection", ReflectionRoundtrip) + Environment.NewLine;
        report += Case("T06 ext-assembly", () => MonoRuntimeProbe.Dependency.DependencyMarker.Describe()) + Environment.NewLine;
        report += Case("T07 game-types", () => GameTypes(allowGameInvocation)) + Environment.NewLine;
        report += Case("T11 native-detour", NativeDetourProbe) + Environment.NewLine;
        report += Case("T12 real-detour", DetourProbe.Run) + Environment.NewLine;

        var dir = string.IsNullOrEmpty(outputDirectory) ? Try(BaseDir) : outputDirectory;
        return WriteReport(dir, report);
    }

    // ---------------------------------------------------------------- 隔离的 BCL 访问

    private static string TimeNow() => DateTimeOffset.Now.ToString("O");

    private static string BaseDir() => AppContext.BaseDirectory;

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

    private static string GameTypes(bool allowInvocation)
    {
        var asm = Assembly.Load("StardewValley");
        var game1 = asm.GetType("StardewValley.Game1");
        if (game1 is null)
        {
            return "Game1 missing";
        }
        var methods = game1.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        var fields = game1.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        var text = "Game1 " + methods.Length + " methods/" + fields.Length + " fields";
        if (allowInvocation)
        {
            var prop = game1.GetProperty("IsMultiplayer");
            text += "; IsMultiplayer=" + prop?.GetValue(null);
        }
        return text;
    }

    // ---------------------------------------------------------------- 收尾（同样隔离）

    private static string WriteReport(string dir, string text)
    {
        var ensured = Try(() => EnsureDir(dir));
        // 不用 StartsWith(string, StringComparison)：那个重载是否被裁剪未知。
        var usable = ensured.Length > 0 && ensured[0] != '!';
        var path = Path.Combine(usable ? ensured : dir, "probe.txt");
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

    // ---------------------------------------------------------------- native detour 前提

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "mprotect")]
    private static extern int Mprotect(IntPtr addr, IntPtr len, int prot);

    /// <summary>
    /// 只验证"能不能"：拿到方法代码地址 → 把所在页改成 RWX → 立刻还原。
    /// **不写入任何字节**，所以不会破坏正在运行的代码。
    /// </summary>
    private static string NativeDetourProbe()
    {
        var method = typeof(Probe).GetMethod(nameof(StaticGreeting))!;
        var fn = method.MethodHandle.Value;   // GetFunctionPointer 不在 Android 参考面里
        var pageSize = 4096L;
        var page = (long)fn & ~(pageSize - 1);

        var rwx = Mprotect((IntPtr)page, (IntPtr)pageSize, 7);   // PROT_READ|WRITE|EXEC
        var back = Mprotect((IntPtr)page, (IntPtr)pageSize, 5);  // PROT_READ|EXEC

        return "fn=0x" + fn.ToInt64().ToString("x")
               + " page=0x" + page.ToString("x")
               + " rwx_rc=" + rwx + " restore_rc=" + back;
    }

    // ---------------------------------------------------------------- 工具

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
