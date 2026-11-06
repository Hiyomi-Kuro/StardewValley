using System.Reflection;
using System.Security.Cryptography;

namespace P114.RuntimeProbe;

/// <summary>一条用例的观察结果。</summary>
internal sealed class ProbeCase
{
    internal ProbeCase(string id, string status, string detail)
    {
        Id = id;
        Status = status;
        Detail = detail;
    }

    /// <summary>R-01 … R-14。</summary>
    internal string Id { get; }

    /// <summary>PASS / FAIL / BLOCKED / NOT RUN。</summary>
    internal string Status { get; }

    /// <summary>一行观察值。不含绝对路径。</summary>
    internal string Detail { get; }
}

/// <summary>前置条件不成立（例如 fixture 没随包带过来）：记为 BLOCKED，不是 FAIL。</summary>
internal sealed class BlockedException : Exception
{
    internal BlockedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Phase 1.1.4 的安全运行时用例（R-01 … R-14）。
///
/// 安全边界（写代码时不能违反）：
///   * 不用 P/Invoke、mprotect、RWX、机器码写入、Reflection.Emit、DynamicMethod、Harmony；
///   * 不碰 <c>Game1</c>、不读 Saves、不推进游戏状态；
///   * 报告里不出现绝对路径（只留末段/文件名）；
///   * 每个用例单独 try/catch，一个失败不影响其余（R-14）；
///   * 每个"未证实可用"的 BCL 成员单独成方法（ILLink 裁剪的失败是方法级的）。
///
/// 状态语义：
///   PASS    = 观察完成并记录了真实返回值（**不代表**行为符合桌面 .NET 的预期）；
///   FAIL    = 该 API 抛出异常，已记录异常类型与消息；
///   BLOCKED = 前置条件不成立（fixture 缺失等），没有观察值；
///   NOT RUN = 未执行。
/// </summary>
internal sealed class SafeProbes
{
    private const string FixturesDirectoryName = "probe-fixtures";

    /// <summary>SHA-256 空输入的官方向量（R-01 对照值）。</summary>
    private const string EmptyVector = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>SHA-256("abc") 的官方向量（R-02 对照值）。</summary>
    private const string AbcVector = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private readonly string _modDirectory;
    private readonly string _fixturesRoot;
    private readonly List<ProbeCase> _cases = new();
    private int _failed;

    internal SafeProbes(string modDirectory)
    {
        _modDirectory = modDirectory;
        _fixturesRoot = Path.Combine(modDirectory, FixturesDirectoryName);
    }

    internal string ModDirectoryLeaf => Leaf(_modDirectory);

    internal List<ProbeCase> Cases => _cases;

    internal int FailedCount => _failed;

    /// <summary>逐项执行全部安全用例。任何单项失败都不会中断其余用例。</summary>
    internal void RunAll()
    {
        Run("R-01", Sha256EmptyVector);
        Run("R-02", Sha256AbcVector);
        Run("R-04", LoadSamePathTwice);
        Run("R-05", LoadSameBytesDifferentPaths);
        Run("R-06", LoadSameIdentityDifferentBytes);
        Run("R-07", LoadDifferentSimpleNames);
        Run("R-08", AssemblyIdentityFields);
        Run("R-09", AssemblyLocationField);
        Run("R-10", AppDomainAssemblyEnumeration);
        Run("R-11", PrivateDependencyCallable);
        Run("R-13", ProductionHashPath);
        Run("R-14", IsolationSummary);
    }

    // ------------------------------------------------------------------ 执行框架

    private void Run(string id, Func<string> body)
    {
        try
        {
            var detail = body();
            _cases.Add(new ProbeCase(id, "PASS", Sanitize(detail)));
        }
        catch (BlockedException blocked)
        {
            _cases.Add(new ProbeCase(id, "BLOCKED", Sanitize(blocked.Message)));
        }
        catch (Exception ex)
        {
            _failed++;
            _cases.Add(new ProbeCase(id, "FAIL", Sanitize(ex.GetType().Name + ": " + ex.Message)));
        }
    }

    // ------------------------------------------------------------------ R-01 / R-02

    /// <summary>R-01：空字节向量的 SHA-256。</summary>
    private string Sha256EmptyVector()
    {
        var bytes = new byte[0];
        var hash = Sha256(bytes);
        return "callable=yes len=" + hash.Length + " hex=" + ToHex(hash) +
               " matches-vector=" + (string.Equals(ToHex(hash), EmptyVector, StringComparison.Ordinal) ? "yes" : "no");
    }

    /// <summary>R-02：<c>abc</c> 的 UTF-8 字节向量。</summary>
    private string Sha256AbcVector()
    {
        var bytes = new byte[] { (byte)'a', (byte)'b', (byte)'c' };
        var hash = Sha256(bytes);
        return "callable=yes len=" + hash.Length + " hex=" + ToHex(hash) +
               " matches-vector=" + (string.Equals(ToHex(hash), AbcVector, StringComparison.Ordinal) ? "yes" : "no");
    }

    /// <summary>单独成方法：<c>SHA256.HashData(byte[])</c> 在目标运行时是否真的存在。</summary>
    private static byte[] Sha256(byte[] bytes) => SHA256.HashData(bytes);

    // ------------------------------------------------------------------ R-04 … R-07

    /// <summary>R-04：同一文件路径连续 LoadFrom 两次。</summary>
    private string LoadSamePathTwice()
    {
        var path = FixturePath("same-a", "P114Same.dll");
        var firstStatus = TryLoad(path, out var first);
        if (first == null)
        {
            throw new InvalidOperationException("first LoadFrom failed: " + firstStatus);
        }
        var secondStatus = TryLoad(path, out var second);
        var detail = "first=" + firstStatus + " second=" + secondStatus +
                     " same-reference=" + (second != null && ReferenceEquals(first, second));
        if (second != null)
        {
            detail += " location-a=" + Leaf(TryPart(() => first.Location)) +
                      " location-b=" + Leaf(TryPart(() => second.Location));
        }
        return detail;
    }

    /// <summary>R-05：相同身份/相同字节、不同路径。</summary>
    private string LoadSameBytesDifferentPaths()
    {
        var pathA = FixturePath("same-a", "P114Same.dll");
        var pathB = FixturePath("same-b", "P114Same.dll");
        var sameBytes = SameFileBytes(pathA, pathB);
        var before = AssemblyCount();

        var statusA = TryLoad(pathA, out var a);
        if (a == null)
        {
            throw new InvalidOperationException("LoadFrom(same-a) failed: " + statusA);
        }
        var statusB = TryLoad(pathB, out var b);
        var after = AssemblyCount();

        return "same-bytes=" + sameBytes + " load-a=" + statusA + " load-b=" + statusB +
               " same-reference=" + (b != null && ReferenceEquals(a, b)) +
               " location-a=" + Leaf(TryPart(() => a.Location)) +
               " location-b=" + (b == null ? "(not loaded)" : Leaf(TryPart(() => b.Location))) +
               " appdomain-before=" + before + " appdomain-after=" + after + " delta=" + (after - before);
    }

    /// <summary>R-06：相同身份、不同字节、不同路径。**不预设** Mono 该返回哪一种。</summary>
    private string LoadSameIdentityDifferentBytes()
    {
        var pathA = FixturePath("diff-a", "P114Diff.dll");
        var pathB = FixturePath("diff-b", "P114Diff.dll");
        var sameBytes = SameFileBytes(pathA, pathB);
        var before = AssemblyCount();

        var statusA = TryLoad(pathA, out var a);
        if (a == null)
        {
            throw new InvalidOperationException("LoadFrom(diff-a) failed: " + statusA);
        }
        var statusB = TryLoad(pathB, out var b);
        var after = AssemblyCount();

        return "same-bytes=" + sameBytes + " load-a=" + statusA + " load-b=" + statusB +
               " same-reference=" + (b != null && ReferenceEquals(a, b)) +
               " marker-a=" + InvokeMarker(a) +
               " marker-b=" + (b == null ? "(not loaded)" : InvokeMarker(b)) +
               " location-a=" + Leaf(TryPart(() => a.Location)) +
               " location-b=" + (b == null ? "(not loaded)" : Leaf(TryPart(() => b.Location))) +
               " appdomain-before=" + before + " appdomain-after=" + after + " delta=" + (after - before);
    }

    /// <summary>R-07：两个不同 SimpleName 的 fixture。</summary>
    private string LoadDifferentSimpleNames()
    {
        var pathAlpha = FixturePath("alpha", "P114Alpha.dll");
        var pathBeta = FixturePath("beta", "P114Beta.dll");

        var statusAlpha = TryLoad(pathAlpha, out var alpha);
        if (alpha == null)
        {
            throw new InvalidOperationException("LoadFrom(alpha) failed: " + statusAlpha);
        }
        var statusBeta = TryLoad(pathBeta, out var beta);
        if (beta == null)
        {
            throw new InvalidOperationException("LoadFrom(beta) failed: " + statusBeta);
        }

        return "name-alpha=" + SimpleName(alpha) + " name-beta=" + SimpleName(beta) +
               " independent-reference=" + !ReferenceEquals(alpha, beta) +
               " location-alpha=" + Leaf(TryPart(() => alpha.Location)) +
               " location-beta=" + Leaf(TryPart(() => beta.Location));
    }

    // ------------------------------------------------------------------ R-08 / R-09 / R-10

    /// <summary>R-08：入口程序集与一个 fixture 的 <c>Assembly.GetName()</c> 字段。</summary>
    private string AssemblyIdentityFields()
    {
        var entry = typeof(SafeProbes).Assembly;
        var fixture = FixturePath("alpha", "P114Alpha.dll");
        var status = TryLoad(fixture, out var loaded);

        var detail = "entry=" + IdentityText(entry);
        if (loaded != null)
        {
            detail += " fixture=" + IdentityText(loaded);
        }
        else
        {
            detail += " fixture-load=" + status;
        }
        return detail;
    }

    /// <summary>R-09：<c>Assembly.Location</c> 是否可读、是否为空。只记录文件名。</summary>
    private string AssemblyLocationField()
    {
        var entry = typeof(SafeProbes).Assembly;
        var location = TryPart(() => entry.Location);
        var empty = location.Length == 0;
        var readable = !empty && File.Exists(location);
        return "empty=" + empty + " readable=" + readable + " leaf=" + Leaf(location) +
               " absolute-path-redacted=yes";
    }

    /// <summary>R-10：<c>AppDomain.CurrentDomain.GetAssemblies()</c> 的数量、是否包含目标、顺序。</summary>
    private string AppDomainAssemblyEnumeration()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var entryName = SimpleName(typeof(SafeProbes).Assembly);
        var entryIndex = -1;
        var dependencyIndex = -1;
        var sameIndex = -1;
        var ordered = "";

        for (var i = 0; i < assemblies.Length; i++)
        {
            var name = SimpleName(assemblies[i]);
            if (i > 0)
            {
                ordered += ",";
            }
            ordered += name;
            if (entryIndex < 0 && string.Equals(name, entryName, StringComparison.Ordinal))
            {
                entryIndex = i;
            }
            if (dependencyIndex < 0 && string.Equals(name, "P114.RuntimeProbe.Dependency", StringComparison.Ordinal))
            {
                dependencyIndex = i;
            }
            if (sameIndex < 0 && string.Equals(name, "P114Same", StringComparison.Ordinal))
            {
                sameIndex = i;
            }
        }

        return "count=" + assemblies.Length + " entry-index=" + entryIndex +
               " dependency-index=" + dependencyIndex + " same-fixture-index=" + sameIndex +
               " order-read-from-one-enumeration=yes order-is-a-contract=no" +
               " names=" + ordered;
    }

    // ------------------------------------------------------------------ R-11 / R-13 / R-14

    /// <summary>R-11：manifest 声明的 private dependency 是否已被 Loader 先加载且可调用。</summary>
    private string PrivateDependencyCallable()
    {
        var text = P114.RuntimeProbe.Dependency.DependencyMarker.Describe();
        var assembly = typeof(P114.RuntimeProbe.Dependency.DependencyMarker).Assembly;
        var loadedByLoader = AppDomainHas(SimpleName(assembly));
        return "marker=" + text + " preloaded-before-entry=" + loadedByLoader +
               " location=" + Leaf(TryPart(() => assembly.Location));
    }

    /// <summary>
    /// R-13：生产路径的 SHA-256 —— 对**实际被 LoadFrom 的那份文件**（Loader 的快照）算摘要。
    /// 只有在探针自己确实跑在 Loader 的快照上时才有意义，所以同时记录 location 末段。
    /// </summary>
    private string ProductionHashPath()
    {
        var assembly = typeof(SafeProbes).Assembly;
        var location = TryPart(() => assembly.Location);
        if (location.Length == 0)
        {
            throw new BlockedException("entry assembly has an empty Location (not loaded from a file)");
        }
        var bytes = File.ReadAllBytes(location);
        var hash = Sha256(bytes);
        return "loaded-from=" + Leaf(location) + " bytes=" + bytes.Length +
               " sha256=" + ToHex(hash) + " snapshot-leaf=" + Leaf(location);
    }

    /// <summary>
    /// R-14：异常隔离汇总。跑在最后，因此前面的失败已经记录在案。
    ///
    /// 两条真实证据：
    ///   1. <c>corrupt-load=</c>：对一份故意损坏的 fixture 调用 <c>Assembly.LoadFrom</c>，
    ///      记录它抛出的异常类型；这条失败**不**中断本次运行，其余用例结果照常写出。
    ///   2. <c>deliberate-throw-caught=</c>：在同一个用例包装里故意抛一次，
    ///      证明"方法级 JIT/异常"在本运行时上确实能被上层 try/catch 收起。
    /// </summary>
    private string IsolationSummary()
    {
        var corruptLoad = "not-staged";
        var corruptLoaded = false;
        var corruptPath = Path.Combine(Path.Combine(_fixturesRoot, "corrupt"), "P114Broken.dll");
        if (File.Exists(corruptPath))
        {
            var status = TryLoad(corruptPath, out var loaded);
            corruptLoad = status;
            corruptLoaded = loaded != null;
        }

        var caught = "";
        try
        {
            ThrowDeliberately();
        }
        catch (Exception ex)
        {
            caught = ex.GetType().Name;
        }

        return "cases-before-this=" + _cases.Count + " failed=" + _failed +
               " corrupt-load=" + corruptLoad + " corrupt-loaded=" + corruptLoaded +
               " deliberate-throw-caught=" + (caught.Length == 0 ? "(none)" : caught) +
               " aggregate-continued-after-failures=yes";
    }

    private static void ThrowDeliberately()
    {
        throw new InvalidOperationException("R-14 isolation self-check");
    }

    // ------------------------------------------------------------------ 工具

    private string FixturePath(string directory, string fileName)
    {
        if (!Directory.Exists(_fixturesRoot))
        {
            throw new BlockedException("fixture root missing: " + FixturesDirectoryName + "/");
        }
        var path = Path.Combine(Path.Combine(_fixturesRoot, directory), fileName);
        if (!File.Exists(path))
        {
            throw new BlockedException("fixture missing: " + FixturesDirectoryName + "/" + directory + "/" + fileName);
        }
        return path;
    }

    /// <summary>只加载、不抛：返回 "ok" 或 "异常类型: 消息"。</summary>
    private static string TryLoad(string path, out Assembly? assembly)
    {
        try
        {
            assembly = Assembly.LoadFrom(path);
            return "ok";
        }
        catch (Exception ex)
        {
            assembly = null;
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static string InvokeMarker(Assembly assembly)
    {
        try
        {
            var type = assembly.GetType("P114Fixtures.Marker", false, false);
            if (type == null)
            {
                return "(type P114Fixtures.Marker missing)";
            }
            var method = type.GetMethod("Describe", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                return "(method Describe missing)";
            }
            var value = method.Invoke(null, null);
            return value == null ? "(null)" : value.ToString() ?? "(null)";
        }
        catch (Exception ex)
        {
            return "!" + ex.GetType().Name + ":" + ex.Message;
        }
    }

    private static string IdentityText(Assembly assembly)
    {
        var name = assembly.GetName();
        var simple = TryPart(() => name.Name ?? "");
        var version = TryPart(() => name.Version == null ? "" : name.Version.ToString());
        var culture = Part(() => CultureOf(name));
        var token = Part(() => TokenOf(name));
        return "name=" + simple + " version=" + version + " culture=" + culture + " token=" + token;
    }

    // 单独成方法：这三个成员在裁剪后的运行时里是否可用，本身就是要观察的对象。
    private static string CultureOf(AssemblyName name) => name.CultureName ?? "";

    private static string TokenOf(AssemblyName name)
    {
        var token = name.GetPublicKeyToken();
        if (token == null || token.Length == 0)
        {
            return "null";
        }
        return ToHex(token);
    }

    private static string SimpleName(Assembly assembly) => TryPart(() => assembly.GetName().Name ?? "");

    private static int AssemblyCount() => AppDomain.CurrentDomain.GetAssemblies().Length;

    private static bool AppDomainHas(string simpleName)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (var i = 0; i < assemblies.Length; i++)
        {
            if (string.Equals(SimpleName(assemblies[i]), simpleName, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string SameFileBytes(string a, string b)
    {
        var left = File.ReadAllBytes(a);
        var right = File.ReadAllBytes(b);
        if (left.Length != right.Length)
        {
            return "no(len " + left.Length + " vs " + right.Length + ")";
        }
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return "no(byte " + i + ")";
            }
        }
        return "yes";
    }

    private static string TryPart(Func<string> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex)
        {
            return "!" + ex.GetType().Name;
        }
    }

    /// <summary>把"是否可用"折叠成可用/不可用，便于一行读完。</summary>
    private static string Part(Func<string> f)
    {
        var text = TryPart(f);
        return text.Length > 0 && text[0] == '!' ? "unavailable" + text : (text.Length == 0 ? "empty" : text);
    }

    private static string ToHex(byte[] bytes)
    {
        var chars = "0123456789abcdef";
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        for (var i = 0; i < bytes.Length; i++)
        {
            sb.Append(chars[(bytes[i] >> 4) & 0xF]);
            sb.Append(chars[bytes[i] & 0xF]);
        }
        return sb.ToString();
    }

    /// <summary>末段名字；报告里永远不出现绝对路径。</summary>
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

    /// <summary>去掉可能出现在异常消息里的绝对路径（本 mod 目录与 fixture 根目录）。</summary>
    private string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        var scrubbed = text.Replace(_modDirectory, "<mod>");
        scrubbed = scrubbed.Replace(_fixturesRoot, "<fixtures>");
        return scrubbed.Replace(Environment.NewLine, " ");
    }
}
