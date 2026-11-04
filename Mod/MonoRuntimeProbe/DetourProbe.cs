using System.Reflection;
using System.Runtime.InteropServices;

namespace MonoRuntimeProbe;

/// <summary>
/// 原生 detour 的最小实验（E5 第二半）。
///
/// 只对**本程序集里的方法**动手，不碰游戏代码：
///   1. 先调用一次，强制 JIT；
///   2. 取两个方法的代码地址；
///   3. 把目标方法的页改成 RWX，写入 16 字节 ARM64 绝对跳转桩；
///   4. 调用目标方法，看是否跳到替换方法；
///   5. 还原原始 16 字节与页权限。
///
/// 已知局限：没有做指令缓存刷新（ARM64 需要 dc/ic 操作），
/// 在 houdini 翻译层下还可能命中翻译缓存——若 detour 未生效，
/// 未必说明真机上也不行。
/// </summary>
internal static class DetourProbe
{
    [DllImport("libc", EntryPoint = "mprotect")]
    private static extern int Mprotect(IntPtr addr, IntPtr len, int prot);

    private const long PageSize = 4096;

    private static int Target(int x) => x + 1;

    private static int Replacement(int x) => x + 100;

    public static string Run()
    {
        // 1. 强制 JIT（未调用过的方法可能没有代码地址）
        var before = Target(1);
        _ = Replacement(1);

        var target = typeof(DetourProbe).GetMethod(nameof(Target), BindingFlags.NonPublic | BindingFlags.Static)!;
        var replacement = typeof(DetourProbe).GetMethod(nameof(Replacement), BindingFlags.NonPublic | BindingFlags.Static)!;

        var fnTarget = target.MethodHandle.Value;
        var fnReplacement = replacement.MethodHandle.Value;
        if (fnTarget == IntPtr.Zero || fnReplacement == IntPtr.Zero)
        {
            return "no-code-pointer (target=" + fnTarget + ", replacement=" + fnReplacement + ")";
        }

        var page = (long)fnTarget & ~(PageSize - 1);
        var original = new byte[16];

        unsafe
        {
            var p = (byte*)fnTarget;
            for (var i = 0; i < 16; i++)
            {
                original[i] = p[i];
            }
        }

        var rwx = Mprotect((IntPtr)page, (IntPtr)PageSize, 7);
        if (rwx != 0)
        {
            return "mprotect-rwx-failed rc=" + rwx;
        }

        // ARM64 绝对跳转桩：ldr x16, #8 ; br x16 ; .quad replacement
        var stub = new byte[16];
        stub[0] = 0x50; stub[1] = 0x00; stub[2] = 0x00; stub[3] = 0x58;   // ldr x16, #8
        stub[4] = 0x00; stub[5] = 0x02; stub[6] = 0x1F; stub[7] = 0xD6;   // br x16
        BitConverter.GetBytes(fnReplacement.ToInt64()).CopyTo(stub, 8);

        unsafe
        {
            var p = (byte*)fnTarget;
            for (var i = 0; i < 16; i++)
            {
                p[i] = stub[i];
            }
        }

        var restorePerm = Mprotect((IntPtr)page, (IntPtr)PageSize, 5);
        var after = Target(1);

        // 还原原始字节，避免影响后续用例
        Mprotect((IntPtr)page, (IntPtr)PageSize, 7);
        unsafe
        {
            var p = (byte*)fnTarget;
            for (var i = 0; i < 16; i++)
            {
                p[i] = original[i];
            }
        }
        Mprotect((IntPtr)page, (IntPtr)PageSize, 5);
        var restored = Target(1);

        return "before=" + before + " after=" + after + " restored=" + restored
               + " (detour " + (after == 101 ? "WORKS" : "did-not-take-effect") + ")"
               + " restore_perm_rc=" + restorePerm
               + " fn=0x" + fnTarget.ToInt64().ToString("x");
    }
}
