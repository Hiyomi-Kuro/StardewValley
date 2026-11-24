using Mono.Cecil;
using Mono.Cecil.Cil;
using Phase21.WorldReady;

namespace Phase21PrototypeTests;

/// <summary>
/// Builds a minimal synthetic "Game1" with the same shapes the real candidate methods have:
/// an instance <c>loadForNewGame(bool)</c>, a static <c>OnDayStarted()</c> with one normal
/// return and an optional finally block, and the three state members the completion edge reads.
///
/// <para>This is a fixture, not a model of Stardew Valley: it exists so the injector can be
/// executed against ordinary IL and against the shapes it must refuse.</para>
/// </summary>
internal sealed class SyntheticFixture
{
    public const string Game1Type = "Synthetic.Game1";

    public string AssemblyName = "";
    public ModuleDefinition Module = null!;
    public TypeDefinition Game1 = null!;
    public MethodDefinition LoadForNewGame = null!;
    public MethodDefinition OnDayStarted = null!;
    public MethodDefinition GetGameMode = null!;
    public MethodReference StartHook = null!;
    public MethodReference CompletionHook = null!;

    public FieldDefinition GameMode = null!;
    public FieldDefinition HasLoadedGame = null!;
    public FieldDefinition CurrentLoader = null!;
    public FieldDefinition LoadCount = null!;
    public FieldDefinition BodyCount = null!;
    public FieldDefinition FinallyCount = null!;

    public static SyntheticFixture Build(string tag, bool withState = true, bool withFinally = true,
        bool retInsideTry = false, bool handlerOnlyRet = false, bool throws = false)
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

        if (withState)
        {
            fixture.HasLoadedGame = AddStaticField(game1, "hasLoadedGame", module.TypeSystem.Boolean);
            fixture.CurrentLoader = AddStaticField(game1, "currentLoader", module.TypeSystem.Object);
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
        loadIl.Emit(OpCodes.Ldc_I4_1);
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

        AddBridgeReferences(module, out var start, out var completion);
        fixture.StartHook = start;
        fixture.CompletionHook = completion;

        return fixture;
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

    private static void AddBridgeReferences(ModuleDefinition module, out MethodReference start,
        out MethodReference completion)
    {
        var version = typeof(PrototypeBridge).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
        var assembly = new AssemblyNameReference("Phase21.WorldReady", version);
        module.AssemblyReferences.Add(assembly);

        var type = new TypeReference("Phase21.WorldReady", "PrototypeBridge", module, assembly);

        start = new MethodReference(PrototypeInjector.StartHookName, module.TypeSystem.Void, type)
        {
            HasThis = false,
        };
        start.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
        start.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
        start.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));

        completion = new MethodReference(PrototypeInjector.CompletionHookName, module.TypeSystem.Void, type)
        {
            HasThis = false,
        };
        completion.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        completion.Parameters.Add(new ParameterDefinition(module.TypeSystem.Boolean));
        completion.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
    }
}
