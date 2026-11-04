using StardewValley;

namespace ExampleMod.HelloWorld;

/// <summary>
/// 编译期验证：直接 `using StardewValley;` 并访问 <c>Game1</c> 的静态成员。
/// 只要能编译通过，就说明模组作者可以像写桌面 SMAPI 模组那样对着游戏类型编程。
/// </summary>
public static class ModEntry
{
    public static string Greet()
    {
        // Game1.IsMultiplayer 是静态属性（探针 T07 已确认存在）。
        return "hello from a mod; multiplayer=" + Game1.IsMultiplayer;
    }

    /// <summary>访问游戏类型的一个字段，验证字段绑定也能成立。</summary>
    public static int TicksSnapshot() => Game1.ticks;
}
