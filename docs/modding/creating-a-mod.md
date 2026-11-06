# 从零创建一个 Mod

- 阶段：Phase 1（最小可用）
- 前置阅读：`manifest-spec.md`（字段与错误码）、`../architecture/phase1-runtime-constraints.md`（能用哪些 API）

本页的每条命令都在当前仓库验证过；标 **仅本地** 的命令需要游戏自带的程序集，不能在公开 CI 上跑。

---

## 0. 你能做什么、不能做什么（先说清楚）

Phase 1 提供的是：**发现 → 校验 → 依赖排序 → 加载 → 实例化 → 调用 `Initialize` → 记日志**。

**没有**（不要假设有）：

- Harmony / 方法补丁 / 事件钩子；
- SMAPI 兼容层；
- Events / Content / Data / Save API；
- 配置 UI、游戏内 Mod 管理器；
- 热重载、运行时卸载。

`Initialize` 里**不要**碰 `Game1` 实例、不要推进世界、不要读写存档。
框架只在"游戏对象创建之前"调用它。

---

## 1. 目录

```text
Mod/ExampleMod.HelloWorld/          ← 仓库里的参考实现，直接抄
├── ExampleMod.HelloWorld.csproj
├── manifest.json
└── ModEntry.cs                     ← IModEntry 实现
```

## 2. 工程文件

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>12</LangVersion>
    <Nullable>enable</Nullable>
    <AssemblyName>MyFirstMod</AssemblyName>
    <RootNamespace>MyFirstMod</RootNamespace>
    <Version>1.0.0</Version>
  </PropertyGroup>

  <ItemGroup>
    <!-- 编译期引用契约；Private="false" 表示不要把 Runtime 复制进 mod 包 -->
    <ProjectReference Include="..\ModFramework.Runtime\ModFramework.Runtime.csproj" Private="false" />
  </ItemGroup>

  <ItemGroup>
    <None Update="manifest.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

> **千万不要**把 `ModFramework.Runtime.dll` 打进 mod 包。第二份副本会让 `IModEntry`
> 变成另一个类型，Loader 会以 `MF0204` 拒绝加载（这是故意的：类型身份必须唯一）。

## 3. 入口类

```csharp
using ModFramework.Runtime;

namespace MyFirstMod;

public sealed class Entry : IModEntry
{
    public void Initialize(IModContext context)
    {
        context.Log.Info("hello from " + context.ModId + " " + context.ModVersion);
        context.Log.Info("game=" + context.GameVersion + " framework=" + context.FrameworkVersion);
    }
}
```

要求（Loader 会逐条检查，见 `manifest-spec.md` §7）：
`public`、非 `abstract`、非泛型、实现 `IModEntry`、有 public 无参构造函数。

## 4. manifest.json

```json
{
  "schemaVersion": 1,
  "id": "MyFirstMod",
  "name": "My First Mod",
  "version": "1.0.0",
  "entryAssembly": "MyFirstMod.dll",
  "entryType": "MyFirstMod.Entry",
  "gameVersion": ">=1.6.0 <2.0.0",
  "frameworkVersion": "^1.0.0"
}
```

`entryType` 必须是**完整类型名**（含命名空间）。`entryAssembly` 必须是**纯文件名**。

## 5. 构建

**仅本地**（需要游戏自带的 65 个程序集当参考程序集）：

```powershell
# 1) 若还没有 refs：
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.blob .tmp/phase0.5/refs --manifest app/src/main/apkroot/assemblies/assemblies.manifest
python Tools/AssemblyStore/store.py extract app/src/main/apkroot/assemblies/assemblies.arm64_v8a.blob .tmp/phase0.5/refs --manifest app/src/main/apkroot/assemblies/assemblies.manifest

# 2) 构建（输出目录本身就是一个合法的 mod 包）
.tmp/phase0.5/dotnet/dotnet.exe build Mod/ExampleMod.HelloWorld -c Release `
    -p:AndroidRefsDir=(Resolve-Path .tmp/phase0.5/refs)
```

为什么必须用 `AndroidRefsDir`：桌面 net8.0 编译能过、设备上却会 `TypeLoadException`
（facade 缺 `AppContext`/`RuntimeInformation` 等）。用游戏自己的程序集编译，
"用了不存在的 BCL 成员"会在**编译期**变成错误。

## 6. 安装到设备

**Phase 1 没有自动安装器，也未经授权在真实设备上安装过。** 目录与权限事实：

| 位置 | 谁能写 | 应用能读吗 |
| --- | --- | --- |
| `/data/data/com.chucklefish.stardewvalley/files/Mods` | 应用自己；adb 需要 root | 能 |
| `/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods` | 应用自己可写；root/shell 创建的文件**属主错**，应用读不到 | 视属主而定 |

所以（需要 root，且**必须先获得设备所有者批准**）：

```powershell
# 把包推到应用私有目录，并把属主改成应用 uid（<uid> 用下面这条命令查）
adb -s <serial> shell "su -c 'stat -c %u /data/data/com.chucklefish.stardewvalley'"

adb -s <serial> push <build>/MyFirstMod.dll /data/local/tmp/
adb -s <serial> push <build>/manifest.json  /data/local/tmp/
adb -s <serial> shell "su -c 'mkdir -p /data/data/com.chucklefish.stardewvalley/files/Mods/MyFirstMod && cp /data/local/tmp/MyFirstMod.dll /data/local/tmp/manifest.json /data/data/com.chucklefish.stardewvalley/files/Mods/MyFirstMod/ && chown -R <uid>:<uid> /data/data/com.chucklefish.stardewvalley/files/Mods && chmod -R 0770 /data/data/com.chucklefish.stardewvalley/files/Mods'"
```

**不要**卸载游戏、不要 `pm clear`、不要动 `Saves/`。装机脚本 `Tools/deploy-apk.ps1`
会卸载+重装（丢存档），本阶段没有执行过。

## 7. 看日志

| 文件 | 内容 |
| --- | --- |
| `<Mods>/loader-report.txt` | 每次运行完整报告：发现数、每个 mod 的状态/错误码/历史、警告 |
| `<Mods>/loader.log` | 同一份报告（方便只记一个文件名） |
| `<Mods>/logs/<mod id>.log` | 该 mod 自己的日志，每行带时间、级别、mod id |

一行日志长这样：

```
2026-11-05T01:50:23.9235940+08:00 INFO  [ExampleMod.HelloWorld] Hello World from "Hello World" 1.0.0
```

报告里的关键行：

```
mods-discovered=2
mods-initialized=1
mods-failed=1
result=ok
mods:
  - id=ExampleMod.HelloWorld state=Initialized
    history=ManifestValidated>DependenciesResolved>AssemblyLoaded>EntryValidated>Instantiated>Initialized
  - id=BrokenMod state=Failed code=MF0309 message=Initialize threw: ...
```

`state=AssemblyLoaded` **不等于**成功——只有 `state=Initialized` 才是入口真的跑完了。

## 8. 本地验证（不需要设备）

```powershell
# 全套宿主测试：manifest → Loader → LoadFrom → Initialize，含 HelloWorld 端到端
python Tools/tests/test_loader.py --refs .tmp/phase0.5/refs

# 只跑不需要游戏程序集的部分（CI 用的就是这条）
python Tools/tests/test_loader.py --dotnet dotnet
```

`test_loader.py` 会构建 `Tools/tests/ModFramework.Tests`，其中包含：

- 单元：SemVer、版本范围、JSON、manifest 夹具、发现、依赖图、确定性；
- 宿主：11 个夹具 mod 包（正常/抛异常/抽象/无无参构造/非 public/缺 dll/名不副实/缺依赖/坏 manifest…）；
- 示例：`Mod/ExampleMod.HelloWorld` 的真实 manifest 走完整链路。

## 9. 常见失败对照

| 报告里的码 | 多半是什么 |
| --- | --- |
| `MF0104` / `MF0106` | manifest 不是严格 JSON / 有重复 key |
| `MF0111` | 字段拼错（schemaVersion 1 拒绝未知字段） |
| `MF0113` | `id` 里有空格或路径分隔符 |
| `MF0116` | 版本写成 `1.0` 或 `v1.0.0` |
| `MF0118` | `entryAssembly` 写成了路径，或不是 `.dll` |
| `MF0201` | dll 没拷进包目录，或文件名与 `entryAssembly` 不一致 |
| `MF0203` | 文件被改名了（程序集内部名仍是旧名） |
| `MF0209` | 预检字节写不成快照（磁盘/预算问题，或运行时没有 SHA-256） |
| `MF0210` | 加载出来的程序集身份与预检身份不一致（多见于包内 dll 在运行期被别的进程改写） |
| `MF0204` | mod 包里带了第二份 `ModFramework.Runtime.dll` |
| `MF0305` | 忘了 `: IModEntry` |
| `MF0307` | 只写了带参数的构造函数 |
| `MF0309` | `Initialize` 抛了异常（看 `logs/<id>.log` 的堆栈） |
| `MF0401` / `MF0402` | 依赖没装 / 版本不在范围内 |
| `MF0404` | 依赖的 mod 自己失败了 |
| `MF0501` | `gameVersion` 范围不包含当前游戏版本 |
