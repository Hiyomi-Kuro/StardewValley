# 计划：绕过 ILLink 裁剪，用 Mono C API 加载模组程序集

> **状态：已被取代（SUPERSEDED，2026-11-04）。**
> 本计划的前提是"`Assembly.Load(byte[])` 缺失 ⇒ 必须走 P/Invoke"。
> 后续实验证明 `Assembly.LoadFrom(string)` **可用**，动态加载已实测跑通
> （见 `phase0-status.md` §3.19/§3.23）。本文仅保留为决策轨迹，**不要按它实施**。

- 状态：**计划（未实现）**
- 前置结论：`Assembly.Load(byte[])` 在运行时不存在（`runtime-feasibility.md` §6.5，真机 + APK 身份证明）
- 目标：让 `Bootstrap.Start()` 在不依赖 `System.Reflection.Assembly.Load` 的前提下加载
  `Mods/ModFramework.Runtime.dll` 并调用 `ModFramework.Runtime.Loader.Start()`

---

## 1. 为什么改走 P/Invoke

| 观察 | 推论 |
| --- | --- |
| 三种作用域下 `Assembly.Load(byte[])` 都报 `MissingMethodException` | 不是引用写法问题 |
| Android 的 `System.Private.CoreLib` 是强名称（160 字节公钥） | 不是标识不匹配 |
| 该成员在游戏代码里从未被调用 | 最可能是 **ILLink 裁剪**掉了 |

因此凡是"游戏不用、我们要用"的 BCL 面都不可靠，包括 `Reflection.Emit`、`AppDomain.Load`、
`Assembly.LoadFrom`。而 **Mono 的 C API 在 `libmonosgen-2.0.so` 的导出表里**（已观测）：

```
mono_image_open / mono_image_open_full / mono_image_open_from_data_with_name
mono_assembly_load_from_full / mono_assembly_get_image
mono_class_from_name / mono_class_get_method_from_name
mono_runtime_invoke
```

这些是运行时自身的入口，裁剪动不了它们。

## 2. 目标调用链（Bootstrap 里要生成 IL 的 6 步）

```
status = 0
img  = mono_image_open("/sdcard/…/Mods/ModFramework.Runtime.dll", &status)
if (img == 0) return
asm  = mono_assembly_load_from_full(img, samePath, &status, 0)
if (asm == 0) return
img2 = mono_assembly_get_image(asm)
klass = mono_class_from_name(img2, "ModFramework.Runtime", "Loader")
if (klass == 0) return
method = mono_class_get_method_from_name(klass, "Start", 0)
if (method == 0) return
mono_runtime_invoke(method, 0, 0, 0)
```

注意：

- 用 `mono_image_open(path, …)` 而不是 `mono_image_open_from_data*`，**避免在托管侧读文件**
  （`File.ReadAllBytes` 同样可能被裁剪，且少一次大缓冲分配）；
- `mono_assembly_load_from_full` 的第二个参数是"显示名/路径"，必须与 image 的来源一致；
- `MonoImageOpenStatus*` 用 `stackalloc` 或局部 `int32` 的地址传；用 `int32` 局部变量 +
  `ldloca` 即可，不需要 `Marshal`。

## 3. Cecil 侧要生成的东西

| 元素 | 说明 |
| --- | --- |
| `ModuleRef("monosgen-2.0")` | P/Invoke 的目标模块名；Android 上解析为 `libmonosgen-2.0.so` |
| 6 个 `MethodDefinition`，`PInvokeInfo` 指向该 ModuleRef | `impl_flags = Native \| PreserveSig`，`IsStatic`，`IsPInvokeImpl = true` |
| 1 个局部 `int32 status` | 供 `&status` 使用 |
| 调用序列 | 按 §2 生成；每步判空后 `ret` |

Cecil 里构造 P/Invoke 方法的关键点（供实现时照抄）：

```csharp
var moduleRef = new ModuleReference("monosgen-2.0");
module.ModuleReferences.Add(moduleRef);
var m = new MethodDefinition("mono_image_open",
    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl | MethodAttributes.HideBySig,
    module.TypeSystem.IntPtr);
m.PInvokeInfo = new PInvokeInfo(PInvokeAttributes.CallConvCdecl, "mono_image_open", moduleRef);
m.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
m.Parameters.Add(new ParameterDefinition(new PointerType(module.TypeSystem.Int32)));
```

> `PInvokeAttributes.CallConvCdecl` 必须显式给，否则 Cecil 默认 Winapi，Mono 可能按 stdcall 处理。

## 4. 先做一次"BCL 存活面"探测（更便宜，先于实现）

在写这 6 个 P/Invoke 之前，先用一次 `--no-catch` 部署回答：**BCL 到底剩下什么**。

方法：把 `Bootstrap.Start()` 换成"逐个 BCL 调用 + 把结果拼进异常消息"，
一次部署就能得到清单：

| 探测项 | 期望 |
| --- | --- |
| `File.Exists(path)` | 若 false/抛异常 → 连 `System.IO` 都被裁，P/Invoke 是唯一路 |
| `File.ReadAllBytes(path).Length` | 同上 |
| `Assembly.Load(byte[])` | 已知缺失（对照组） |
| `AppDomain.CurrentDomain.Load(byte[])` | 若存在，可省掉 6 个 P/Invoke |
| `Assembly.LoadFrom(path)` | 同上 |
| `new DynamicMethod(...)` | 决定 Harmony/Transpiler 是否可能有戏 |

拼接结果用 `string.Concat` 或 `StringBuilder`（后者若被裁则退化）。

**这一步的产出直接决定 §2 要不要写**：如果 `AppDomain.Load` 或 `LoadFrom` 还在，
Bootstrap 只需一次调用，不必碰 P/Invoke。

## 5. 验收标准（本计划的完成定义）

1. `Bootstrap.Start()` 能加载 `Mods/ModFramework.Runtime.dll`；
2. `Loader.Start()` 落盘 `loader.log`（首行 `loader-entered`）；
3. `MonoRuntimeProbe.dll` 被同一路径加载并产出 `probe-*.json`；
4. 无模组时（无 `ModFramework.Runtime.dll`）游戏**照常启动**，`bootstrap-marker` 不产生副作用；
5. 全过程用 `deploy-apk.ps1` 部署，并在输出里留下 `local apk == device apk` 的哈希证据。

达成 1–4 后，E1 才算通过；否则 Phase 1 不启动。

## 6. 已知风险

| 风险 | 缓解 |
| --- | --- |
| `DllImport("monosgen-2.0")` 在 Mono 上解析不到 | 先试 `"monosgen-2.0"`，失败再试 `"libmonosgen-2.0.so"`；两次都在同一轮部署里试 |
| `mono_image_open` 对"从未注册过的程序集"返回 null | 检查 `status`，把状态码带进抛出的异常消息里 |
| `mono_runtime_invoke` 要求线程已 attach 到 Mono | 我们是从托管代码调用的，线程必然已 attach，风险低 |
| P/Invoke 的 `impl_flags` 写错导致调用约定不符 | 显式 `CallConvCdecl` + `PreserveSig`，并在 x86_64 模拟器与 ARM64 真机各验一次 |
