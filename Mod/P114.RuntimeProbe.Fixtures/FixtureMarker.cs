namespace P114Fixtures;

/// <summary>
/// Phase 1.1.4 的 <c>Assembly.LoadFrom</c> fixture 标记。
///
/// 同一个源文件用不同 MSBuild 属性编出多份：<c>P114Same</c>（一份，复制两份）、
/// <c>P114Diff</c> A/B（同名同版本、不同字节）、<c>P114Alpha</c> / <c>P114Beta</c>（不同简单名）。
/// <see cref="Describe"/> 的返回值必须能区分 A 与 B，否则 R-06 无法证明"加载的是哪一份"。
/// </summary>
public static class Marker
{
    /// <summary>A/B 变体的标记；报告与测试都读它。</summary>
    public static string Describe()
    {
#if VARIANT_B
        return "variant=B";
#else
        return "variant=A";
#endif
    }

    /// <summary>简单名 + 版本，便于在没有反射的情况下核对身份。</summary>
    public static string Identity()
    {
        var name = typeof(Marker).Assembly.GetName();
        return name.Name + " v" + name.Version;
    }
}
