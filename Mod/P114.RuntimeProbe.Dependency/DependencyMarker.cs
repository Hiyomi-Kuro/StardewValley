namespace P114.RuntimeProbe.Dependency;

/// <summary>
/// Phase 1.1.4 的私有依赖标记（R-11）。
///
/// 这个程序集**不是**一个独立 mod：它没有 manifest.json，Loader 的发现逻辑不会看到它。
/// 它只能通过 <c>P114.RuntimeProbe</c> 的 manifest 里 <c>privateAssemblies</c> 的声明
/// 被 Loader 预加载；探针在 <c>Initialize</c> 里调用本类的 <see cref="Describe"/>，
/// 用来证明"声明的私有依赖确实先被加载且可调用"。
/// </summary>
public static class DependencyMarker
{
    /// <summary>稳定的 marker 文本，报告与测试都断言它。</summary>
    public const string Marker = "p114-private-dependency-alive";

    /// <summary>调用方断言返回值包含 <see cref="Marker"/>。</summary>
    public static string Describe()
    {
        var name = typeof(DependencyMarker).Assembly.GetName();
        return Marker + ":" + name.Name + " v" + name.Version;
    }
}
