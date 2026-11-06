using System.Text;
using ModFramework.Runtime;

namespace P114.RuntimeProbe;

/// <summary>
/// Phase 1.1.4 的普通诊断 Mod：由 Loader 通过**正常**的发现 → 校验 → 依赖解析 →
/// 加载前程序集预检 → 快照 → <c>Assembly.LoadFrom</c> → <c>Initialize</c> 链路启动。
///
/// 它不是一个特殊入口：没有 manifest 就不会被发现（<c>ModDiscovery</c> 只扫带
/// <c>manifest.json</c> 的一层子目录），Loader 也不会因为目录里存在某个 dll 就加载它。
///
/// 安全边界（与 <see cref="SafeProbes"/> 一致）：
///   * 不调用 <c>Game1</c>、不读写 Saves、不推进游戏世界；
///   * 不含 P/Invoke / mprotect / RWX / 机器码 / Reflection.Emit / DynamicMethod / Harmony；
///   * 报告只写到自己的包目录下（<c>probe-output/phase1.1.4.txt</c>），不写绝对路径；
///   * 报告写入失败只记录异常类型，不向上抛，不影响游戏启动。
/// </summary>
public sealed class P114RuntimeProbeEntry : IModEntry
{
    /// <summary>报告相对包目录的位置。契约的一部分：测试与文档都引用它。</summary>
    public const string OutputDirectoryName = "probe-output";

    /// <summary>报告文件名。</summary>
    public const string OutputFileName = "phase1.1.4.txt";

    /// <summary>报告格式标记，写在第一行之后。</summary>
    public const string ReportSchema = "phase1.1.4/safe/1";

    public void Initialize(IModContext context)
    {
        SafeProbes probes;
        try
        {
            probes = new SafeProbes(context.ModDirectory);
        }
        catch (Exception ex)
        {
            // 连构造都失败：只记录，不阻断游戏。
            context.Log.Error("probe could not start (" + ex.GetType().Name + ")", ex);
            return;
        }

        try
        {
            probes.RunAll();
        }
        catch (Exception ex)
        {
            // RunAll 自己逐项隔离；能到这里说明连框架都坏了，仍然不抛。
            context.Log.Error("probe aggregate failed (" + ex.GetType().Name + ")", ex);
        }

        var text = BuildReport(context, probes);
        var written = TryWriteReport(context, text);

        var summary = Summary(probes);
        context.Log.Info("probe mode=" + SafeProbesMode + " " + summary);
        if (written != null)
        {
            context.Log.Info("probe report=" + OutputDirectoryName + "/" + OutputFileName);
        }
    }

    /// <summary>报告里写明的模式标记：任何"这不是安全路径"的误读都能一眼看出。</summary>
    internal const string SafeProbesMode = "safe-only";

    private static string BuildReport(IModContext context, SafeProbes probes)
    {
        var newLine = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append("P114 RuntimeProbe report").Append(newLine);
        sb.Append("schema      : ").Append(ReportSchema).Append(newLine);
        sb.Append("mode        : ").Append(SafeProbesMode)
          .Append(" (no P/Invoke, no mprotect, no RWX, no detour, no game calls)").Append(newLine);
        sb.Append("time        : ").Append(SafeTime()).Append(newLine);
        sb.Append("mod         : ").Append(context.ModId).Append(' ').Append(context.ModVersion).Append(newLine);
        sb.Append("mod-dir     : ").Append(probes.ModDirectoryLeaf).Append(newLine);
        sb.Append("game        : ").Append(context.GameVersion).Append(newLine);
        sb.Append("framework   : ").Append(context.FrameworkVersion).Append(newLine);
        sb.Append("fixtures    : probe-fixtures/ (subdirectory, NOT declared in the manifest)").Append(newLine);
        sb.Append(newLine);

        var cases = probes.Cases;
        for (var i = 0; i < cases.Count; i++)
        {
            var item = cases[i];
            sb.Append(item.Id).Append(' ').Append(item.Status).Append(" : ").Append(item.Detail).Append(newLine);
        }

        // Loader 侧观察项：从 mod 内部不可见，由宿主/设备测试读 loader-report.txt 断言。
        sb.Append("R-03 NOT RUN : loader-observed (preflight-stage=complete / assembly-load=entry / state=Initialized) - see loader-report.txt").Append(newLine);
        sb.Append("R-12 NOT RUN : loader-observed (preflight-stage=complete precedes the first assembly-load=, path is .staging) - see loader-report.txt").Append(newLine);
        sb.Append(newLine);
        sb.Append(Summary(probes)).Append(newLine);
        return sb.ToString();
    }

    private static string Summary(SafeProbes probes)
    {
        var passed = 0;
        var failed = 0;
        var blocked = 0;
        var cases = probes.Cases;
        for (var i = 0; i < cases.Count; i++)
        {
            var status = cases[i].Status;
            if (string.Equals(status, "PASS", StringComparison.Ordinal))
            {
                passed++;
            }
            else if (string.Equals(status, "FAIL", StringComparison.Ordinal))
            {
                failed++;
            }
            else if (string.Equals(status, "BLOCKED", StringComparison.Ordinal))
            {
                blocked++;
            }
        }
        return "summary: cases=" + (cases.Count + 2) + " observed=" + cases.Count +
               " pass=" + passed + " fail=" + failed + " blocked=" + blocked + " not-run=2";
    }

    /// <summary>写报告；失败返回 null 并记一条 Error，绝不抛出。</summary>
    private static string? TryWriteReport(IModContext context, string text)
    {
        string directory;
        string path;
        try
        {
            directory = Path.Combine(context.ModDirectory, OutputDirectoryName);
            path = Path.Combine(directory, OutputFileName);
        }
        catch (Exception ex)
        {
            context.Log.Error("probe report path could not be built (" + ex.GetType().Name + ")", ex);
            return null;
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(path, text);
            return path;
        }
        catch (Exception ex)
        {
            // 契约：写入失败只记录异常类型，不中断游戏初始化。
            context.Log.Error("probe report write failed (" + ex.GetType().Name + ": " + ex.Message + ")", ex);
            return null;
        }
    }

    /// <summary>单独成方法：时间格式化在裁剪运行时上未逐一验证。</summary>
    private static string SafeTime()
    {
        try
        {
            return DateTimeOffset.Now.ToString("O");
        }
        catch (Exception ex)
        {
            return "!" + ex.GetType().Name;
        }
    }
}
