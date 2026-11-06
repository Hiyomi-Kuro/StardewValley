// Phase 1.1.4: this file is NOT compiled by default.
//
// T11 changed a live code page to RWX with mprotect and immediately restored it. It does not
// write bytes, but it is still a native, page-permission-mutating experiment and Phase 1.1.4
// forbids running it (and any P/Invoke) from the safe path. Kept verbatim for source audit.
//
// To even compile it you must opt in explicitly:
//   dotnet build Mod/MonoRuntimeProbe -p:LegacyNativeExperiments=true
// No test, script, Loader path or manifest mod of Phase 1.1.4 builds or runs that configuration.
#if LEGACY_NATIVE_EXPERIMENTS

using System.Reflection;
using System.Runtime.InteropServices;

namespace MonoRuntimeProbe;

/// <summary>
/// T11：只验证"能不能"把代码页改成 RWX，**不写入任何字节**。
/// **历史危险实验，不属于 Phase 1.1.4 测试范围，不得运行。**
/// </summary>
internal static class NativePageProbe
{
    [DllImport("libc", EntryPoint = "mprotect")]
    private static extern int Mprotect(IntPtr addr, IntPtr len, int prot);

    internal static string Run()
    {
        var method = typeof(Probe).GetMethod(nameof(Probe.StaticGreeting))!;
        var fn = method.MethodHandle.Value;   // GetFunctionPointer 不在 Android 参考面里
        var pageSize = 4096L;
        var page = (long)fn & ~(pageSize - 1);

        var rwx = Mprotect((IntPtr)page, (IntPtr)pageSize, 7);   // PROT_READ|WRITE|EXEC
        var back = Mprotect((IntPtr)page, (IntPtr)pageSize, 5);  // PROT_READ|EXEC

        return "fn=0x" + fn.ToInt64().ToString("x")
               + " page=0x" + page.ToString("x")
               + " rwx_rc=" + rwx + " restore_rc=" + back;
    }
}

/// <summary>
/// 历史原生实验的**唯一**显式入口（T11 + T12）。
///
/// 只有当调用方用 <c>-p:LegacyNativeExperiments=true</c> 编出这个程序集并显式反射调用它时
/// 才会存在。Phase 1.1.4 的任何默认路径（<see cref="Probe.RunSafe"/>、Loader、manifest mod、
/// 本阶段脚本）都不会到达这里。
/// </summary>
public static class LegacyNativeExperiments
{
    /// <summary>历史危险实验。**不要调用。**</summary>
    public static string Run()
    {
        var text = "";
        try
        {
            text += "T11 native-detour : " + NativePageProbe.Run() + Environment.NewLine;
        }
        catch (Exception ex)
        {
            text += "T11 native-detour : " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine;
        }
        try
        {
            text += "T12 real-detour   : " + DetourProbe.Run() + Environment.NewLine;
        }
        catch (Exception ex)
        {
            text += "T12 real-detour   : " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine;
        }
        return text;
    }
}

#endif
