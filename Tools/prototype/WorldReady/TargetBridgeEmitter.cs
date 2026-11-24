using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Phase21.WorldReady;

/// <summary>
/// Emits a self-contained bridge type into a <b>temporary copy</b> of the game assembly.
///
/// <para>Why in-module: the temporary copy is never installed and never executed, so it must not
/// acquire an AssemblyRef to a prototype assembly that does not exist on a device. The emitted
/// bridge only stores counters and the observed arguments; it proves the call sites and argument
/// shape, not runtime behaviour.</para>
/// </summary>
public static class TargetBridgeEmitter
{
    public const string DefaultNamespace = "Phase21.Prototype";
    public const string DefaultTypeName = "WorldReadyBridge";

    public static PrototypeHooks Emit(ModuleDefinition module, string typeNamespace = DefaultNamespace,
        string typeName = DefaultTypeName)
    {
        if (module.GetType(typeNamespace, typeName) != null)
        {
            throw new PrototypeInjectionException(
                "bridge type already present: " + typeNamespace + "." + typeName);
        }

        var type = new TypeDefinition(typeNamespace, typeName,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class |
            TypeAttributes.BeforeFieldInit, module.TypeSystem.Object);
        module.Types.Add(type);

        var loadingCalls = AddField(type, "WorldLoadingCalls", module.TypeSystem.Int32);
        var completedCalls = AddField(type, "DayCompletedCalls", module.TypeSystem.Int32);
        var lastLoadedGame = AddField(type, "LastLoadedGame", module.TypeSystem.Boolean);
        var lastHasLoadedGame = AddField(type, "LastHasLoadedGame", module.TypeSystem.Boolean);
        var lastGameMode = AddField(type, "LastGameMode", module.TypeSystem.Int32);
        var lastLoaderWasNull = AddField(type, "LastLoaderWasNull", module.TypeSystem.Boolean);

        var worldLoading = new MethodDefinition(PrototypeInjector.StartHookName,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        worldLoading.Parameters.Add(new ParameterDefinition("loadedGame", ParameterAttributes.None,
            module.TypeSystem.Boolean));
        worldLoading.Parameters.Add(new ParameterDefinition("loader", ParameterAttributes.None,
            module.TypeSystem.Object));
        worldLoading.Parameters.Add(new ParameterDefinition("gameMode", ParameterAttributes.None,
            module.TypeSystem.Int32));
        type.Methods.Add(worldLoading);

        var loadingIl = worldLoading.Body.GetILProcessor();
        EmitIncrement(loadingIl, loadingCalls);
        loadingIl.Emit(OpCodes.Ldarg_0);
        loadingIl.Emit(OpCodes.Stsfld, lastLoadedGame);
        loadingIl.Emit(OpCodes.Ldarg_2);
        loadingIl.Emit(OpCodes.Stsfld, lastGameMode);
        loadingIl.Emit(OpCodes.Ldarg_1);
        loadingIl.Emit(OpCodes.Ldnull);
        loadingIl.Emit(OpCodes.Ceq);
        loadingIl.Emit(OpCodes.Stsfld, lastLoaderWasNull);
        loadingIl.Emit(OpCodes.Ret);

        var dayCompleted = new MethodDefinition(PrototypeInjector.CompletionHookName,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        dayCompleted.Parameters.Add(new ParameterDefinition("gameMode", ParameterAttributes.None,
            module.TypeSystem.Int32));
        dayCompleted.Parameters.Add(new ParameterDefinition("hasLoadedGame", ParameterAttributes.None,
            module.TypeSystem.Boolean));
        dayCompleted.Parameters.Add(new ParameterDefinition("loader", ParameterAttributes.None,
            module.TypeSystem.Object));
        type.Methods.Add(dayCompleted);

        var completedIl = dayCompleted.Body.GetILProcessor();
        EmitIncrement(completedIl, completedCalls);
        completedIl.Emit(OpCodes.Ldarg_0);
        completedIl.Emit(OpCodes.Stsfld, lastGameMode);
        completedIl.Emit(OpCodes.Ldarg_1);
        completedIl.Emit(OpCodes.Stsfld, lastHasLoadedGame);
        completedIl.Emit(OpCodes.Ldarg_2);
        completedIl.Emit(OpCodes.Ldnull);
        completedIl.Emit(OpCodes.Ceq);
        completedIl.Emit(OpCodes.Stsfld, lastLoaderWasNull);
        completedIl.Emit(OpCodes.Ret);

        return new PrototypeHooks(worldLoading, dayCompleted);
    }

    private static FieldDefinition AddField(TypeDefinition type, string name, TypeReference fieldType)
    {
        var field = new FieldDefinition(name, FieldAttributes.Public | FieldAttributes.Static, fieldType);
        type.Fields.Add(field);
        return field;
    }

    private static void EmitIncrement(ILProcessor il, FieldDefinition field)
    {
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, field);
    }
}
