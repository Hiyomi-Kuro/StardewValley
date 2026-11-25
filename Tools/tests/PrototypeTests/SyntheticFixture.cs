using Mono.Cecil;
using Mono.Cecil.Cil;
using Phase21.WorldReady;

namespace Phase21PrototypeTests;

/// <summary>Shape of the synthetic <c>ResetGameStateOnTitleScreen</c> body.</summary>
internal enum ResetShape
{
    /// <summary>Gate, then clear mode/loader/flag, then return (mirrors the real cleanup order).</summary>
    Normal = 0,

    /// <summary>Gate, then throw before any field is cleaned (the reset aborts).</summary>
    ThrowsBeforeCleanup = 1,

    /// <summary>A <c>ret</c> inside a try region (must be refused).</summary>
    RetInsideTry = 2,

    /// <summary>Only a return inside an exception handler (no normal completion, must be refused).</summary>
    HandlerOnlyRet = 3,

    /// <summary>The handler ends exactly at the normal <c>ret</c> (half-open boundary check).</summary>
    HandlerEndsAtRet = 4,

    /// <summary>A finally block before the normal return.</summary>
    WithFinally = 5,
}

/// <summary>
/// Deterministic synchronisation points for the reset race fixtures. Lives in the test assembly;
/// the synthetic fixture takes an AssemblyRef to it, exactly like it takes one to
/// <c>Phase21.WorldReady</c> for the bridge. It must be public: the fixture is a separate
/// assembly and calls <see cref="MidReset"/> from inside its reset body.
/// </summary>
public static class SyntheticGate
{
    private static readonly object Sync = new object();

    public static ManualResetEventSlim Reached { get; } = new ManualResetEventSlim(false);

    public static ManualResetEventSlim Resume { get; } = new ManualResetEventSlim(false);

    public static bool Paused { get; set; }

    /// <summary>Injected-into-fixture body call, placed after the reset entry hook.</summary>
    public static void MidReset()
    {
        lock (Sync)
        {
            if (!Paused)
            {
                return;
            }
        }

        Reached.Set();
        if (!Resume.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("synthetic reset gate timed out");
        }
    }

    public static void Open(bool paused)
    {
        Paused = paused;
        Reached.Reset();
        Resume.Reset();
    }

    public static void Release()
    {
        Resume.Set();
    }
}

/// <summary>
/// Builds a minimal synthetic "Game1" with the same shapes the real candidate methods have:
/// an instance <c>loadForNewGame(bool)</c>, a static <c>OnDayStarted()</c> with one normal
/// return and an optional finally block, an instance <c>ResetGameStateOnTitleScreen()</c> with the
/// real cleanup order, and the three state members the edges read.
///
/// <para>This is a fixture, not a model of Stardew Valley: it exists so the injector can be
/// executed against ordinary IL and against the shapes it must refuse.</para>
/// </summary>
internal sealed class SyntheticFixture
{
    public const string Game1Type = "Synthetic.Game1";
    public const string GateType = "Phase21PrototypeTests.SyntheticGate";

    public string AssemblyName = "";
    public ModuleDefinition Module = null!;
    public TypeDefinition Game1 = null!;
    public MethodDefinition LoadForNewGame = null!;
    public MethodDefinition OnDayStarted = null!;
    public MethodDefinition ResetGameStateOnTitleScreen = null!;
    public MethodDefinition UpdateTitleScreenDuringLoadingMode = null!;
    public MethodDefinition GetGameMode = null!;
    public MethodReference ResetBeganHook = null!;
    public MethodReference ResetCompletedHook = null!;
    public MethodReference GetResetEpochHook = null!;
    public MethodReference StartHook = null!;
    public MethodReference CompletionHook = null!;
    public MethodReference AdvanceHook = null!;

    public FieldDefinition GameMode = null!;
    public FieldDefinition HasLoadedGame = null!;
    public FieldDefinition CurrentLoader = null!;
    public FieldDefinition LoadCount = null!;
    public FieldDefinition BodyCount = null!;
    public FieldDefinition FinallyCount = null!;
    public FieldDefinition ResetCount = null!;
    public FieldDefinition AdvanceSuccessCount = null!;
    public FieldDefinition AdvanceExitCount = null!;

    public PrototypeHooks Hooks
    {
        get
        {
            return new PrototypeHooks(ResetBeganHook, ResetCompletedHook, GetResetEpochHook, StartHook,
                CompletionHook, AdvanceHook);
        }
    }

    public static SyntheticFixture Build(string tag, bool withState = true, bool withFinally = true,
        bool retInsideTry = false, bool handlerOnlyRet = false, bool throws = false,
        ResetShape resetShape = ResetShape.Normal, bool withAdvance = true)
    {
        var fixture = new SyntheticFixture();
        fixture.AssemblyName = "SyntheticGame." + tag;

        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(fixture.AssemblyName, new Version(1, 0, 0, 0)),
            tag + ".dll", ModuleKind.Dll);
        var module = assembly.MainModule;
        fixture.Module = module;

        var game1 = new TypeDefinition("Synthetic", "Game1",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);
        module.Types.Add(game1);
        fixture.Game1 = game1;

        AddDefaultConstructor(module, game1);

        fixture.GameMode = AddStaticField(game1, "_gameMode", module.TypeSystem.Byte);
        fixture.LoadCount = AddStaticField(game1, "LoadCount", module.TypeSystem.Int32);
        fixture.BodyCount = AddStaticField(game1, "BodyCount", module.TypeSystem.Int32);
        fixture.FinallyCount = AddStaticField(game1, "FinallyCount", module.TypeSystem.Int32);
        fixture.ResetCount = AddStaticField(game1, "ResetCount", module.TypeSystem.Int32);
        fixture.AdvanceSuccessCount =
            AddStaticField(game1, "AdvanceSuccessCount", module.TypeSystem.Int32);
        fixture.AdvanceExitCount = AddStaticField(game1, "AdvanceExitCount", module.TypeSystem.Int32);

        if (withState)
        {
            fixture.HasLoadedGame = AddStaticField(game1, "hasLoadedGame", module.TypeSystem.Boolean);
            // Typed like the real field's base interface so the emitted `callvirt MoveNext` matches
            // the shape the injector recognises.
            fixture.CurrentLoader =
                AddStaticField(game1, "currentLoader", module.ImportReference(typeof(System.Collections.IEnumerator)));
        }

        fixture.GetGameMode = new MethodDefinition("get_gameMode",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Byte);
        var getIl = fixture.GetGameMode.Body.GetILProcessor();
        getIl.Emit(OpCodes.Ldsfld, fixture.GameMode);
        getIl.Emit(OpCodes.Ret);
        game1.Methods.Add(fixture.GetGameMode);

        fixture.LoadForNewGame = new MethodDefinition("loadForNewGame",
            MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.Void);
        fixture.LoadForNewGame.Parameters.Add(new ParameterDefinition("loadedGame",
            ParameterAttributes.None, module.TypeSystem.Boolean));
        var loadIl = fixture.LoadForNewGame.Body.GetILProcessor();
        loadIl.Emit(OpCodes.Ldsfld, fixture.LoadCount);
        loadIl.Emit(OpCodes.Ldc_I4_1);
        loadIl.Emit(OpCodes.Add);
        loadIl.Emit(OpCodes.Stsfld, fixture.LoadCount);
        loadIl.Emit(OpCodes.Ret);
        game1.Methods.Add(fixture.LoadForNewGame);

        fixture.OnDayStarted = new MethodDefinition("OnDayStarted",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        game1.Methods.Add(fixture.OnDayStarted);

        if (retInsideTry)
        {
            EmitRetInsideTry(module, fixture.OnDayStarted);
        }
        else if (handlerOnlyRet)
        {
            EmitHandlerOnlyReturn(module, fixture.OnDayStarted);
        }
        else if (throws)
        {
            EmitThrowingBody(module, fixture.OnDayStarted);
        }
        else
        {
            EmitNormalBody(module, fixture, withFinally);
        }

        // public virtual instance void ResetGameStateOnTitleScreen()
        fixture.ResetGameStateOnTitleScreen = new MethodDefinition(
            PrototypeInjector.ResetMethodName,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        game1.Methods.Add(fixture.ResetGameStateOnTitleScreen);
        EmitResetBody(module, fixture, resetShape);

        AddBridgeReferences(module, fixture);

        if (withState && withAdvance)
        {
            BuildAdvanceMethod(module, fixture);
        }

        return fixture;
    }

    /// <summary>
    /// Mirrors <c>Game1.UpdateTitleScreenDuringLoadingMode</c>: the loader guard, the advance, the
    /// failure branch that nulls the loader and exits to title, and the success branch with exactly
    /// two returns. The injector's advance edge requires this shape.
    /// </summary>
    private static void BuildAdvanceMethod(ModuleDefinition module, SyntheticFixture fixture)
    {
        var method = new MethodDefinition(PrototypeInjector.AdvanceMethodName,
            MethodAttributes.Assembly | MethodAttributes.HideBySig, module.TypeSystem.Void);
        fixture.Game1.Methods.Add(method);
        fixture.UpdateTitleScreenDuringLoadingMode = method;

        var il = method.Body.GetILProcessor();

        var successStart = il.Create(OpCodes.Ldsfld, fixture.AdvanceSuccessCount);
        var successOne = il.Create(OpCodes.Ldc_I4_1);
        var successAdd = il.Create(OpCodes.Add);
        var successStore = il.Create(OpCodes.Stsfld, fixture.AdvanceSuccessCount);
        var successRet = il.Create(OpCodes.Ret);

        var exitStart = il.Create(OpCodes.Ldsfld, fixture.AdvanceExitCount);
        var exitOne = il.Create(OpCodes.Ldc_I4_1);
        var exitAdd = il.Create(OpCodes.Add);
        var exitStore = il.Create(OpCodes.Stsfld, fixture.AdvanceExitCount);

        // Shared target of both original guards, exactly like the real method's final `ret`.
        var end = il.Create(OpCodes.Ret);
        var exitJump = il.Create(OpCodes.Br, end);

        var branchExit = il.Create(OpCodes.Bne_Un, exitStart);
        var branchEnd = il.Create(OpCodes.Brtrue, end);

        var moveNext = new MethodReference("MoveNext", module.TypeSystem.Boolean,
            module.ImportReference(typeof(System.Collections.IEnumerator)))
        {
            HasThis = true,
        };

        il.Append(il.Create(OpCodes.Ldsfld, fixture.CurrentLoader));
        il.Append(il.Create(OpCodes.Brfalse, end));
        il.Append(il.Create(OpCodes.Ldsfld, fixture.CurrentLoader));
        il.Append(il.Create(OpCodes.Callvirt, moveNext));
        il.Append(branchEnd);
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Stsfld, fixture.CurrentLoader));
        il.Append(il.Create(OpCodes.Call, fixture.GetGameMode));
        il.Append(il.Create(OpCodes.Ldc_I4_3));
        il.Append(branchExit);
        il.Append(successStart);
        il.Append(successOne);
        il.Append(successAdd);
        il.Append(successStore);
        il.Append(successRet);
        il.Append(exitStart);
        il.Append(exitOne);
        il.Append(exitAdd);
        il.Append(exitStore);
        il.Append(exitJump);
        il.Append(end);

        method.Body.MaxStackSize = 2;
    }

    private static void EmitResetBody(ModuleDefinition module, SyntheticFixture fixture,
        ResetShape shape)
    {
        var method = fixture.ResetGameStateOnTitleScreen;
        var il = method.Body.GetILProcessor();

        switch (shape)
        {
            case ResetShape.RetInsideTry:
            {
                var tryStart = il.Create(OpCodes.Nop);
                var ret = il.Create(OpCodes.Ret);
                var tryEnd = il.Create(OpCodes.Nop);
                var handlerStart = il.Create(OpCodes.Pop);
                var handlerRet = il.Create(OpCodes.Ret);
                var end = il.Create(OpCodes.Nop);
                il.Append(tryStart);
                il.Append(ret);
                il.Append(tryEnd);
                il.Append(handlerStart);
                il.Append(handlerRet);
                il.Append(end);
                method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart,
                    TryEnd = tryEnd,
                    HandlerStart = handlerStart,
                    HandlerEnd = end,
                    CatchType = module.ImportReference(typeof(Exception)),
                });
                return;
            }

            case ResetShape.HandlerOnlyRet:
            {
                var tryStart = il.Create(OpCodes.Nop);
                var end = il.Create(OpCodes.Nop);
                var leaveTry = il.Create(OpCodes.Leave_S, end);
                var handlerStart = il.Create(OpCodes.Pop);
                var handlerRet = il.Create(OpCodes.Ret);
                il.Append(tryStart);
                il.Append(leaveTry);
                il.Append(handlerStart);
                il.Append(handlerRet);
                il.Append(end);
                method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart,
                    TryEnd = handlerStart,
                    HandlerStart = handlerStart,
                    HandlerEnd = end,
                    CatchType = module.ImportReference(typeof(Exception)),
                });
                return;
            }

            case ResetShape.HandlerEndsAtRet:
            {
                // The entry instruction must stay outside every protected region (the real method
                // is the same way), so a plain nop leads and the try starts after it.
                var entry = il.Create(OpCodes.Nop);
                var tryStart = il.Create(OpCodes.Nop);
                var ret = il.Create(OpCodes.Ret);
                var leaveTry = il.Create(OpCodes.Leave_S, ret);
                var handlerStart = il.Create(OpCodes.Pop);
                il.Append(entry);
                il.Append(tryStart);
                il.Append(leaveTry);
                il.Append(handlerStart);
                il.Append(ret);
                method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart,
                    TryEnd = handlerStart,
                    HandlerStart = handlerStart,
                    HandlerEnd = ret,
                    CatchType = module.ImportReference(typeof(Exception)),
                });
                return;
            }

            case ResetShape.ThrowsBeforeCleanup:
            {
                EmitGateCall(module, il);
                var ctor = module.ImportReference(
                    typeof(Exception).GetConstructor(new[] { typeof(string) })!);
                il.Emit(OpCodes.Ldstr, "synthetic reset failure");
                il.Emit(OpCodes.Newobj, ctor);
                il.Emit(OpCodes.Throw);
                il.Emit(OpCodes.Ret);
                return;
            }

            case ResetShape.WithFinally:
            {
                EmitGateCall(module, il);
                var tryStart = il.Create(OpCodes.Nop);
                var finallyStart = il.Create(OpCodes.Ldsfld, fixture.FinallyCount);
                var one = il.Create(OpCodes.Ldc_I4_1);
                var add = il.Create(OpCodes.Add);
                var store = il.Create(OpCodes.Stsfld, fixture.FinallyCount);
                var endFinally = il.Create(OpCodes.Endfinally);
                var afterFinally = il.Create(OpCodes.Nop);
                var leaveTry = il.Create(OpCodes.Leave_S, afterFinally);
                var ret = il.Create(OpCodes.Ret);

                il.Append(tryStart);
                EmitCleanup(module, il, fixture);
                il.Append(leaveTry);
                il.Append(finallyStart);
                il.Append(one);
                il.Append(add);
                il.Append(store);
                il.Append(endFinally);
                il.Append(afterFinally);
                il.Append(ret);

                method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
                {
                    TryStart = tryStart,
                    TryEnd = finallyStart,
                    HandlerStart = finallyStart,
                    HandlerEnd = afterFinally,
                });
                return;
            }

            default:
            {
                EmitGateCall(module, il);
                EmitCleanup(module, il, fixture);
                il.Emit(OpCodes.Ret);
                return;
            }
        }
    }

    /// <summary>The real cleanup order: gameMode first, then currentLoader, then hasLoadedGame.</summary>
    private static void EmitCleanup(ModuleDefinition module, ILProcessor il, SyntheticFixture fixture)
    {
        if (fixture.CurrentLoader != null && fixture.HasLoadedGame != null)
        {
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stsfld, fixture.GameMode);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Stsfld, fixture.CurrentLoader);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stsfld, fixture.HasLoadedGame);
        }

        il.Emit(OpCodes.Ldsfld, fixture.ResetCount);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, fixture.ResetCount);
    }

    private static void EmitGateCall(ModuleDefinition module, ILProcessor il)
    {
        var gate = GateReference(module);
        il.Emit(OpCodes.Call, gate);
    }

    private static MethodReference GateReference(ModuleDefinition module)
    {
        var existing = module.AssemblyReferences.FirstOrDefault(a => a.Name == "Phase21.PrototypeTests");
        if (existing is null)
        {
            existing = new AssemblyNameReference("Phase21.PrototypeTests",
                typeof(SyntheticGate).Assembly.GetName().Version ?? new Version(1, 0, 0, 0));
            module.AssemblyReferences.Add(existing);
        }

        var type = new TypeReference("Phase21PrototypeTests", "SyntheticGate", module, existing);
        var method = new MethodReference("MidReset", module.TypeSystem.Void, type)
        {
            HasThis = false,
        };
        return method;
    }

    private static void EmitNormalBody(ModuleDefinition module, SyntheticFixture fixture, bool withFinally)
    {
        var method = fixture.OnDayStarted;
        var il = method.Body.GetILProcessor();

        if (!withFinally)
        {
            var retOnly = il.Create(OpCodes.Ret);
            il.Emit(OpCodes.Call, fixture.GetGameMode);
            il.Emit(OpCodes.Ldc_I4_3);
            il.Emit(OpCodes.Bne_Un_S, retOnly);
            il.Emit(OpCodes.Nop);
            il.Append(retOnly);
            return;
        }

        var tryStart = il.Create(OpCodes.Nop);
        var incrementBody = il.Create(OpCodes.Ldsfld, fixture.BodyCount);
        var one = il.Create(OpCodes.Ldc_I4_1);
        var add = il.Create(OpCodes.Add);
        var storeBody = il.Create(OpCodes.Stsfld, fixture.BodyCount);
        var leaveTry = il.Create(OpCodes.Leave_S, tryStart);
        var finallyStart = il.Create(OpCodes.Ldsfld, fixture.FinallyCount);
        var oneFinally = il.Create(OpCodes.Ldc_I4_1);
        var addFinally = il.Create(OpCodes.Add);
        var storeFinally = il.Create(OpCodes.Stsfld, fixture.FinallyCount);
        var endFinally = il.Create(OpCodes.Endfinally);
        var afterFinally = il.Create(OpCodes.Call, fixture.GetGameMode);
        var three = il.Create(OpCodes.Ldc_I4_3);
        var ret = il.Create(OpCodes.Ret);
        var branchToRet = il.Create(OpCodes.Bne_Un_S, ret);
        var nop = il.Create(OpCodes.Nop);

        il.Append(tryStart);
        il.Append(incrementBody);
        il.Append(one);
        il.Append(add);
        il.Append(storeBody);
        il.Append(leaveTry);
        il.Append(finallyStart);
        il.Append(oneFinally);
        il.Append(addFinally);
        il.Append(storeFinally);
        il.Append(endFinally);
        il.Append(afterFinally);
        il.Append(three);
        il.Append(branchToRet);
        il.Append(nop);
        il.Append(ret);

        leaveTry.Operand = afterFinally;

        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally)
        {
            TryStart = tryStart,
            TryEnd = finallyStart,
            HandlerStart = finallyStart,
            HandlerEnd = afterFinally,
        });
    }

    private static void EmitRetInsideTry(ModuleDefinition module, MethodDefinition method)
    {
        var il = method.Body.GetILProcessor();
        var tryStart = il.Create(OpCodes.Nop);
        var ret = il.Create(OpCodes.Ret);
        var tryEnd = il.Create(OpCodes.Nop);
        var handlerStart = il.Create(OpCodes.Pop);
        var handlerRet = il.Create(OpCodes.Ret);
        var end = il.Create(OpCodes.Nop);

        il.Append(tryStart);
        il.Append(ret);
        il.Append(tryEnd);
        il.Append(handlerStart);
        il.Append(handlerRet);
        il.Append(end);

        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = tryEnd,
            HandlerStart = handlerStart,
            HandlerEnd = end,
            CatchType = module.ImportReference(typeof(Exception)),
        });
    }

    private static void EmitHandlerOnlyReturn(ModuleDefinition module, MethodDefinition method)
    {
        var il = method.Body.GetILProcessor();
        var tryStart = il.Create(OpCodes.Nop);
        var end = il.Create(OpCodes.Nop);
        var leaveTry = il.Create(OpCodes.Leave_S, end);
        var handlerStart = il.Create(OpCodes.Pop);
        var handlerRet = il.Create(OpCodes.Ret);

        il.Append(tryStart);
        il.Append(leaveTry);
        il.Append(handlerStart);
        il.Append(handlerRet);
        il.Append(end);

        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = end,
            CatchType = module.ImportReference(typeof(Exception)),
        });
    }

    private static void EmitThrowingBody(ModuleDefinition module, MethodDefinition method)
    {
        var il = method.Body.GetILProcessor();
        var ctor = module.ImportReference(typeof(Exception).GetConstructor(new[] { typeof(string) })!);
        il.Emit(OpCodes.Ldstr, "synthetic failure");
        il.Emit(OpCodes.Newobj, ctor);
        il.Emit(OpCodes.Throw);
        il.Emit(OpCodes.Ret);
    }

    private static void AddDefaultConstructor(ModuleDefinition module, TypeDefinition type)
    {
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName |
            MethodAttributes.RTSpecialName, module.TypeSystem.Void);
        var objectCtor = new MethodReference(".ctor", module.TypeSystem.Void, module.TypeSystem.Object)
        {
            HasThis = true,
        };
        var il = ctor.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, objectCtor);
        il.Emit(OpCodes.Ret);
        type.Methods.Add(ctor);
    }

    private static FieldDefinition AddStaticField(TypeDefinition type, string name, TypeReference fieldType)
    {
        var field = new FieldDefinition(name, FieldAttributes.Public | FieldAttributes.Static, fieldType);
        type.Fields.Add(field);
        return field;
    }

    private static void AddBridgeReferences(ModuleDefinition module, SyntheticFixture fixture)
    {
        var version = typeof(PrototypeBridge).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
        var assembly = new AssemblyNameReference("Phase21.WorldReady", version);
        module.AssemblyReferences.Add(assembly);

        var type = new TypeReference("Phase21.WorldReady", "PrototypeBridge", module, assembly);

        fixture.ResetBeganHook = new MethodReference(PrototypeInjector.ResetEntryHookName,
            module.TypeSystem.Void, type)
        {
            HasThis = false,
        };

        fixture.ResetCompletedHook = new MethodReference(PrototypeInjector.ResetExitHookName,
            module.TypeSystem.Void, type)
        {
            HasThis = false,
        };

        fixture.GetResetEpochHook = new MethodReference(PrototypeInjector.ResetEpochGetterName,
            module.TypeSystem.Int64, type)
        {
            HasThis = false,
        };

        fixture.StartHook = new MethodReference(PrototypeInjector.StartHookName, module.TypeSystem.Void,
            type)
        {
            HasThis = false,
        };
        fixture.StartHook.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int64));
        fixture.StartHook.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
        fixture.StartHook.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        fixture.StartHook.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));

        fixture.CompletionHook = new MethodReference(PrototypeInjector.CompletionHookName,
            module.TypeSystem.Void, type)
        {
            HasThis = false,
        };
        fixture.CompletionHook.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        fixture.CompletionHook.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
        fixture.CompletionHook.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));

        fixture.AdvanceHook = new MethodReference(PrototypeInjector.AdvanceHookName,
            module.TypeSystem.Void, type)
        {
            HasThis = false,
        };
        fixture.AdvanceHook.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        fixture.AdvanceHook.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        fixture.AdvanceHook.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
        fixture.AdvanceHook.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
    }
}

/// <summary>
/// Stand-in load state machine. It implements the same interface the real <c>currentLoader</c> exposes
/// (<see cref="System.Collections.IEnumerator"/>) so the fixture's <c>callvirt MoveNext</c> has the
/// real shape, and it lets a test script the sequence of <c>MoveNext</c> results.
/// </summary>
public sealed class TestLoader : System.Collections.IEnumerator
{
    private readonly Queue<bool> _script;

    public TestLoader(params bool[] script)
    {
        _script = new Queue<bool>(script ?? Array.Empty<bool>());
    }

    public int MoveNextCount { get; private set; }

    public object Current
    {
        get { return 0; }
    }

    public bool MoveNext()
    {
        MoveNextCount++;
        return _script.Count > 0 && _script.Dequeue();
    }

    public void Reset()
    {
    }
}
