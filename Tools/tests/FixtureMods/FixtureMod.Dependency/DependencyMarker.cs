namespace FixtureMod.Dependency;

/// <summary>
/// Proves that a mod can ship a private dependency next to its entry assembly and have the
/// loader's <c>Assembly.LoadFrom</c> resolve it from the mod directory.
/// </summary>
public static class DependencyMarker
{
    public static string Describe() => "fixture-dependency-alive";
}
