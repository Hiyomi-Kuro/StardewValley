namespace MonoRuntimeProbe.Dependency;

/// <summary>
/// 外部依赖程序集的最小内容：用来验证"探针 DLL 依赖另一个 DLL"时加载器是否能一起解析。
/// 故意不引用任何 BCL 之外的东西，也不依赖调用方。
/// </summary>
public static class DependencyMarker
{
    public const string Marker = "dependency-assembly-alive";

    public static string Describe() => $"{Marker}: {typeof(DependencyMarker).Assembly.GetName().Name} v{typeof(DependencyMarker).Assembly.GetName().Version}";
}
