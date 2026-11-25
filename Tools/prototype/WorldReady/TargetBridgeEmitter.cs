using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Phase21.WorldReady;

/// <summary>
/// Emits a self-contained bridge type into a <b>temporary copy</b> of the game assembly.
///
/// <para>Why in-module: the temporary copy is never installed and never executed, so it must not
/// acquire an AssemblyRef to a prototype assembly that does not exist on a device. The emitted
/// bridge only stores counters and the observed arguments; it proves the call sites, the argument
/// shape and the instruction order, not runtime behaviour.</para>
///
/// <para><b>Capability difference from the test bridge.</b> The synthetic fixture calls
/// <c>Phase21.WorldReady.PrototypeBridge</c>, which forwards to a live
/// <c>WorldReadyCoordinator</c> (epoch comparison, pending attempt, latch, observations). This
/// target bridge only counts calls and remembers the last arguments: its <c>EpochCounter</c> is a
/// stand-in, not the coordinator's epoch, and it implements no refusal semantics. Each bridge is
/// therefore verified by its own written-out structural check and the report never attributes
/// coordinator behaviour to the target bridge.</para>
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

        var resetBeganCalls = AddField(type, "ResetBeganCalls", module.TypeSystem.Int32);
        var resetCompletedCalls = AddField(type, "ResetCompletedCalls", module.TypeSystem.Int32);
        var getEpochCalls = AddField(type, "GetResetEpochCalls", module.TypeSystem.Int32);
        var epochCounter = AddField(type, "EpochCounter", module.TypeSystem.Int64);
        var lastEpochAtStart = AddField(type, "LastResetEpochAtStart", module.TypeSystem.Int64);
        var lastResetEpochOnExit = AddField(type, "LastResetEpochOnExit", module.TypeSystem.Int64);
        var worldLoadingCalls = AddField(type, "WorldLoadingCalls", module.TypeSystem.Int32);
        var dayCompletedCalls = AddField(type, "DayCompletedCalls", module.TypeSystem.Int32);
        var lastLoadedGame = AddField(type, "LastLoadedGame", module.TypeSystem.Boolean);
        var lastHasLoadedGame = AddField(type, "LastHasLoadedGame", module.TypeSystem.Boolean);
        var lastGameMode = AddField(type, "LastGameMode", module.TypeSystem.Int32);
        var lastLoaderWasNull = AddField(type, "LastLoaderWasNull", module.TypeSystem.Boolean);
        var loadAdvancedCalls = AddField(type, "LoadAdvancedCalls", module.TypeSystem.Int32);
        var lastAdvanceGameMode = AddField(type, "LastAdvanceGameMode", module.TypeSystem.Int32);
        var lastAdvanceMoved = AddField(type, "LastAdvanceMoved", module.TypeSystem.Boolean);
        var lastAdvanceHasLoadedGame =
            AddField(type, "LastAdvanceHasLoadedGame", module.TypeSystem.Boolean);
        var lastAdvanceLoaderWasNull =
            AddField(type, "LastAdvanceLoaderWasNull", module.TypeSystem.Boolean);

        // void ResetBegan(): ResetBeganCalls++; EpochCounter++;
        var resetBegan = NewStaticVoid(module, type, PrototypeInjector.ResetEntryHookName);
        var beganIl = resetBegan.Body.GetILProcessor();
        EmitIntIncrement(beganIl, resetBeganCalls);
        EmitLongIncrement(beganIl, epochCounter);
        beganIl.Emit(OpCodes.Ret);

        // void ResetCompleted(): ResetCompletedCalls++; LastResetEpochOnExit = EpochCounter;
        var resetCompleted = NewStaticVoid(module, type, PrototypeInjector.ResetExitHookName);
        var completedResetIl = resetCompleted.Body.GetILProcessor();
        EmitIntIncrement(completedResetIl, resetCompletedCalls);
        completedResetIl.Emit(OpCodes.Ldsfld, epochCounter);
        completedResetIl.Emit(OpCodes.Stsfld, lastResetEpochOnExit);
        completedResetIl.Emit(OpCodes.Ret);

        // long GetResetEpoch(): GetResetEpochCalls++; return EpochCounter;
        var getEpoch = new MethodDefinition(PrototypeInjector.ResetEpochGetterName,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Int64);
        type.Methods.Add(getEpoch);
        var epochIl = getEpoch.Body.GetILProcessor();
        EmitIntIncrement(epochIl, getEpochCalls);
        epochIl.Emit(OpCodes.Ldsfld, epochCounter);
        epochIl.Emit(OpCodes.Ret);

        // void WorldLoading(long resetEpoch, bool loadedGame, int gameMode, object loader)
        var worldLoading = NewStaticVoid(module, type, PrototypeInjector.StartHookName);
        worldLoading.Parameters.Add(new ParameterDefinition("resetEpoch", ParameterAttributes.None,
            module.TypeSystem.Int64));
        worldLoading.Parameters.Add(new ParameterDefinition("loadedGame", ParameterAttributes.None,
            module.TypeSystem.Boolean));
        worldLoading.Parameters.Add(new ParameterDefinition("gameMode", ParameterAttributes.None,
            module.TypeSystem.Int32));
        worldLoading.Parameters.Add(new ParameterDefinition("loader", ParameterAttributes.None,
            module.TypeSystem.Object));
        var loadingIl = worldLoading.Body.GetILProcessor();
        EmitIntIncrement(loadingIl, worldLoadingCalls);
        loadingIl.Emit(OpCodes.Ldarg_0);
        loadingIl.Emit(OpCodes.Stsfld, lastEpochAtStart);
        loadingIl.Emit(OpCodes.Ldarg_1);
        loadingIl.Emit(OpCodes.Stsfld, lastLoadedGame);
        loadingIl.Emit(OpCodes.Ldarg_2);
        loadingIl.Emit(OpCodes.Stsfld, lastGameMode);
        EmitNullCheck(loadingIl, lastLoaderWasNull, 3);
        loadingIl.Emit(OpCodes.Ret);

        // void DayCompleted(int gameMode, bool hasLoadedGame, object loader)
        var dayCompleted = NewStaticVoid(module, type, PrototypeInjector.CompletionHookName);
        dayCompleted.Parameters.Add(new ParameterDefinition("gameMode", ParameterAttributes.None,
            module.TypeSystem.Int32));
        dayCompleted.Parameters.Add(new ParameterDefinition("hasLoadedGame", ParameterAttributes.None,
            module.TypeSystem.Boolean));
        dayCompleted.Parameters.Add(new ParameterDefinition("loader", ParameterAttributes.None,
            module.TypeSystem.Object));
        var completedIl = dayCompleted.Body.GetILProcessor();
        EmitIntIncrement(completedIl, dayCompletedCalls);
        completedIl.Emit(OpCodes.Ldarg_0);
        completedIl.Emit(OpCodes.Stsfld, lastGameMode);
        completedIl.Emit(OpCodes.Ldarg_1);
        completedIl.Emit(OpCodes.Stsfld, lastHasLoadedGame);
        EmitNullCheck(completedIl, lastLoaderWasNull, 2);
        completedIl.Emit(OpCodes.Ret);

        // void LoadAdvanced(object loader, int gameMode, bool moved, bool hasLoadedGame)
        var loadAdvanced = NewStaticVoid(module, type, PrototypeInjector.AdvanceHookName);
        loadAdvanced.Parameters.Add(new ParameterDefinition("loader", ParameterAttributes.None,
            module.TypeSystem.Object));
        loadAdvanced.Parameters.Add(new ParameterDefinition("gameMode", ParameterAttributes.None,
            module.TypeSystem.Int32));
        loadAdvanced.Parameters.Add(new ParameterDefinition("moved", ParameterAttributes.None,
            module.TypeSystem.Boolean));
        loadAdvanced.Parameters.Add(new ParameterDefinition("hasLoadedGame",
            ParameterAttributes.None, module.TypeSystem.Boolean));
        var advanceIl = loadAdvanced.Body.GetILProcessor();
        EmitIntIncrement(advanceIl, loadAdvancedCalls);
        advanceIl.Emit(OpCodes.Ldarg_1);
        advanceIl.Emit(OpCodes.Stsfld, lastAdvanceGameMode);
        advanceIl.Emit(OpCodes.Ldarg_2);
        advanceIl.Emit(OpCodes.Stsfld, lastAdvanceMoved);
        advanceIl.Emit(OpCodes.Ldarg_3);
        advanceIl.Emit(OpCodes.Stsfld, lastAdvanceHasLoadedGame);
        EmitNullCheck(advanceIl, lastAdvanceLoaderWasNull, 0);
        advanceIl.Emit(OpCodes.Ret);

        return new PrototypeHooks(resetBegan, resetCompleted, getEpoch, worldLoading, dayCompleted,
            loadAdvanced);
    }

    private static MethodDefinition NewStaticVoid(ModuleDefinition module, TypeDefinition type, string name)
    {
        var method = new MethodDefinition(name,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        type.Methods.Add(method);
        return method;
    }

    private static FieldDefinition AddField(TypeDefinition type, string name, TypeReference fieldType)
    {
        var field = new FieldDefinition(name, FieldAttributes.Public | FieldAttributes.Static, fieldType);
        type.Fields.Add(field);
        return field;
    }

    private static void EmitIntIncrement(ILProcessor il, FieldDefinition field)
    {
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, field);
    }

    private static void EmitLongIncrement(ILProcessor il, FieldDefinition field)
    {
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Conv_I8);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, field);
    }

    private static void EmitNullCheck(ILProcessor il, FieldDefinition field, int argumentIndex)
    {
        il.Emit(OpCodes.Ldarg, argumentIndex);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Stsfld, field);
    }
}
