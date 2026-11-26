using System.Globalization;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Phase21.WorldReady;

/// <summary>Raised when the prototype cannot place or verify a hook. Always fail closed.</summary>
public sealed class PrototypeInjectionException : Exception
{
    public PrototypeInjectionException(string message)
        : base(message)
    {
    }
}

/// <summary>The three game methods and the three state members the prototype reads.</summary>
public sealed class PrototypeTargets
{
    public PrototypeTargets(ModuleDefinition module, MethodDefinition loadForNewGame,
        MethodDefinition onDayStarted, MethodDefinition resetGameStateOnTitleScreen,
        MethodReference getGameMode, FieldDefinition hasLoadedGame, FieldDefinition currentLoader,
        MethodDefinition updateTitleScreenDuringLoadingMode, MethodDefinition loadAfterPermissionCheck,
        MethodReference loaderCreationCall)
    {
        Module = module;
        LoadForNewGame = loadForNewGame;
        OnDayStarted = onDayStarted;
        ResetGameStateOnTitleScreen = resetGameStateOnTitleScreen;
        GetGameMode = getGameMode;
        HasLoadedGame = hasLoadedGame;
        CurrentLoader = currentLoader;
        UpdateTitleScreenDuringLoadingMode = updateTitleScreenDuringLoadingMode;
        LoadAfterPermissionCheck = loadAfterPermissionCheck;
        LoaderCreationCall = loaderCreationCall;
    }

    public ModuleDefinition Module { get; }

    public MethodDefinition LoadForNewGame { get; }

    public MethodDefinition OnDayStarted { get; }

    /// <summary>The reset/invalidations boundary: <c>public virtual instance void()</c>.</summary>
    public MethodDefinition ResetGameStateOnTitleScreen { get; }

    public MethodReference GetGameMode { get; }

    public FieldDefinition HasLoadedGame { get; }

    public FieldDefinition CurrentLoader { get; }

    /// <summary>
    /// The P.2 success-handoff edge: <c>Game1.UpdateTitleScreenDuringLoadingMode()</c>, the only place
    /// the game advances <c>currentLoader</c> and observes its terminal result.
    /// </summary>
    public MethodDefinition UpdateTitleScreenDuringLoadingMode { get; }

    /// <summary>
    /// The P.3 attempt-creation edge: <c>SaveGame.LoadAfterPermissionCheck()</c>, whose tail creates the
    /// load state machine and stores it in <c>Game1.currentLoader</c>.
    /// </summary>
    public MethodDefinition LoadAfterPermissionCheck { get; }

    /// <summary>
    /// The exact creation call the injected sequence intercepts (<c>SaveGame.getLoadEnumerator(...)</c>),
    /// so verification can require that the identity still comes from that call's return value.
    /// </summary>
    public MethodReference LoaderCreationCall { get; }
}

/// <summary>The hook methods to call, with the exact prototypes this injector emits.</summary>
public sealed class PrototypeHooks
{
    public PrototypeHooks(MethodReference resetBegan, MethodReference resetCompleted,
        MethodReference getResetEpoch, MethodReference worldLoading, MethodReference dayCompleted,
        MethodReference loadAdvanced, MethodReference loadEnumeratorCreated)
    {
        ResetBegan = resetBegan;
        ResetCompleted = resetCompleted;
        GetResetEpoch = getResetEpoch;
        WorldLoading = worldLoading;
        DayCompleted = dayCompleted;
        LoadAdvanced = loadAdvanced;
        LoadEnumeratorCreated = loadEnumeratorCreated;
    }

    /// <summary><c>void ResetBegan()</c>.</summary>
    public MethodReference ResetBegan { get; }

    /// <summary><c>void ResetCompleted()</c>.</summary>
    public MethodReference ResetCompleted { get; }

    /// <summary><c>long GetResetEpoch()</c>.</summary>
    public MethodReference GetResetEpoch { get; }

    /// <summary><c>void WorldLoading(long resetEpoch, bool loadedGame, int gameMode, object loader)</c>.</summary>
    public MethodReference WorldLoading { get; }

    /// <summary><c>void DayCompleted(int gameMode, bool hasLoadedGame, object loader)</c>.</summary>
    public MethodReference DayCompleted { get; }

    /// <summary>
    /// <c>void LoadAdvanced(object loader, int gameMode, bool moved, bool hasLoadedGame)</c>: the
    /// identity-carrying completion edge. <c>loader</c> is the exact object whose <c>MoveNext()</c>
    /// was just called, held in a local — not re-read from <c>currentLoader</c>.
    /// </summary>
    public MethodReference LoadAdvanced { get; }

    /// <summary>
    /// <c>void LoadEnumeratorCreated(long resetEpoch, int gameMode, object identity)</c>: the P.3
    /// attempt-creation edge. <c>identity</c> is the load state machine returned by
    /// <c>getLoadEnumerator</c>, taken from that call's result before it is stored in
    /// <c>currentLoader</c>.
    /// </summary>
    public MethodReference LoadEnumeratorCreated { get; }
}

/// <summary>Counts returned by one injection pass.</summary>
public sealed class PrototypeInjectionResult
{
    public int ResetEntryCalls { get; internal set; }

    public int ResetCompletedCalls { get; internal set; }

    public int ResetSkippedHandlerReturns { get; internal set; }

    public int WorldLoadingCalls { get; internal set; }

    public int DayCompletedCalls { get; internal set; }

    public int SkippedHandlerReturns { get; internal set; }

    public int LoadMaxStackBefore { get; internal set; }

    public int LoadMaxStackAfter { get; internal set; }

    public int CompletionMaxStackBefore { get; internal set; }

    public int CompletionMaxStackAfter { get; internal set; }

    public int ResetMaxStackBefore { get; internal set; }

    public int ResetMaxStackAfter { get; internal set; }

    public int AdvanceCalls { get; internal set; }

    public int AdvanceMaxStackBefore { get; internal set; }

    public int AdvanceMaxStackAfter { get; internal set; }

    public int LoaderCreationCalls { get; internal set; }

    public int LoaderCreationMaxStackBefore { get; internal set; }

    public int LoaderCreationMaxStackAfter { get; internal set; }
}

/// <summary>Structural verification of one (possibly already written) module.</summary>
public sealed class PrototypeVerification
{
    private readonly List<string> _problems = new List<string>();

    public IReadOnlyList<string> Problems
    {
        get { return _problems; }
    }

    public bool Ok
    {
        get { return _problems.Count == 0; }
    }

    public int ResetEntryCalls { get; internal set; }

    public int ResetCompletedCalls { get; internal set; }

    public int ResetNormalReturns { get; internal set; }

    public int ResetHandlerReturns { get; internal set; }

    public int WorldLoadingCalls { get; internal set; }

    public int DayCompletedCalls { get; internal set; }

    public int NormalReturns { get; internal set; }

    public int HandlerReturns { get; internal set; }

    public int EdgesToNormalReturns { get; internal set; }

    /// <summary>P.2: <c>LoadAdvanced</c> call sites in <c>UpdateTitleScreenDuringLoadingMode</c>.</summary>
    public int AdvanceCalls { get; internal set; }

    /// <summary>
    /// P.2: true when the completion edge takes its identity from a local that was loaded from
    /// <c>currentLoader</c> once, used for <c>MoveNext()</c>, and passed to the hook — i.e. the hook
    /// never re-reads the global.
    /// </summary>
    public bool AdvanceIdentityFromLocal { get; internal set; }

    /// <summary>P.2: true when both original loader guards still target the same instruction.</summary>
    public bool AdvanceGuardsIntact { get; internal set; }

    /// <summary>P.2: true when the injected region is outside every try/handler region.</summary>
    public bool AdvanceSiteUnprotected { get; internal set; }

    /// <summary>P.3: <c>LoadEnumeratorCreated</c> call sites in <c>LoadAfterPermissionCheck</c>.</summary>
    public int LoaderCreationCalls { get; internal set; }

    /// <summary>
    /// P.3: true when the creation edge takes its identity from the local that received the
    /// <c>getLoadEnumerator</c> result, and that local is what the original <c>stsfld currentLoader</c>
    /// still stores.
    /// </summary>
    public bool LoaderCreationIdentityFromFactory { get; internal set; }

    /// <summary>P.3: true when the original <c>stsfld currentLoader</c> is still executed exactly once.</summary>
    public bool LoaderWriteIntact { get; internal set; }

    /// <summary>P.3: true when the injected creation site is outside every try/handler region.</summary>
    public bool LoaderCreationSiteUnprotected { get; internal set; }

    /// <summary>P.3: true when the creation edge reads the epoch before the mode and the identity.</summary>
    public bool LoaderCreationEpochFirst { get; internal set; }

    /// <summary>
    /// True when the start call site reads the epoch before <c>gameMode</c> and the loader. This is
    /// the instruction order the reset/start race depends on.
    /// </summary>
    public bool EpochReadFirst { get; internal set; }

    internal void Fail(string problem)
    {
        _problems.Add(problem);
    }
}

/// <summary>
/// Cecil prototype injector, independent from <c>Tools/ModInjector</c>.
///
/// <para>It shares the structural rules that matter (retarget edges that jump straight at a
/// <c>ret</c>, move exception-handler boundaries, skip handler returns, widen short branches),
/// but it is a separate implementation on purpose: the production injector must not gain an
/// experimental path just because this phase wanted one.</para>
///
/// <para>The emitted call sequences are exactly the minimum the coordinator needs:</para>
/// <code>
/// ResetGameStateOnTitleScreen() entry:
///   call   ResetBegan()
///
/// ResetGameStateOnTitleScreen() normal ret:
///   call   ResetCompleted()
///   ret
///
/// loadForNewGame(bool) entry:
///   call   GetResetEpoch()        ; epoch FIRST, before any game field is read
///   ldarg.1                       ; loadedGame
///   call   Game1::get_gameMode()  ; then the mode
///   ldsfld Game1::currentLoader    ; then the loader
///   call   WorldLoading(long, bool, int, object)
///
/// OnDayStarted() normal ret:
///   call   Game1::get_gameMode()
///   ldsfld Game1::hasLoadedGame
///   ldsfld Game1::currentLoader
///   call   DayCompleted(int, bool, object)
///   ret
///
/// SaveGame.LoadAfterPermissionCheck(), between getLoadEnumerator and its store:
///   call   SaveGame::getLoadEnumerator(string, bool, bool)   ; the state machine (P.3 attempt identity)
///   stloc  V_identity                                        ; kept, never re-read from the field
///   call   GetResetEpoch()                                   ; epoch FIRST
///   call   Game1::get_gameMode()                             ; then the mode
///   ldloc  V_identity
///   call   LoadEnumeratorCreated(long, int, object)
///   ldloc  V_identity
///   stsfld Game1::currentLoader                               ; the original store, unchanged
/// </code>
/// </summary>
public static class PrototypeInjector
{
    public const string DefaultGame1Type = "StardewValley.Game1";
    public const string DefaultSaveGameType = "StardewValley.SaveGame";
    public const string StartHookName = "WorldLoading";
    public const string CompletionHookName = "DayCompleted";
    public const string ResetEntryHookName = "ResetBegan";
    public const string ResetExitHookName = "ResetCompleted";
    public const string ResetEpochGetterName = "GetResetEpoch";
    public const string ResetMethodName = "ResetGameStateOnTitleScreen";
    public const string AdvanceHookName = "LoadAdvanced";
    public const string AdvanceMethodName = "UpdateTitleScreenDuringLoadingMode";
    public const string LoaderCreationHookName = "LoadEnumeratorCreated";
    public const string LoaderCreationMethodName = "LoadAfterPermissionCheck";

    private static readonly string[] NoParameters = Array.Empty<string>();

    private static readonly string[] EpochGetterSignature = NoParameters;

    private static readonly string[] StartHookSignature =
    {
        "System.Int64", "System.Boolean", "System.Int32", "System.Object",
    };

    private static readonly string[] CompletionHookSignature =
    {
        "System.Int32", "System.Boolean", "System.Object",
    };

    /// <summary>Parameters of <c>LoadAdvanced(object, int, bool, bool)</c>, in push order.</summary>
    private static readonly string[] AdvanceHookSignature =
    {
        "System.Object", "System.Int32", "System.Boolean", "System.Boolean",
    };

    /// <summary>Parameters of <c>LoadEnumeratorCreated(long, int, object)</c>, in push order.</summary>
    private static readonly string[] LoaderCreationHookSignature =
    {
        "System.Int64", "System.Int32", "System.Object",
    };

    /// <summary>Resolves the four methods, the creation boundary and three state members by exact shape.</summary>
    public static PrototypeTargets Resolve(ModuleDefinition module, string game1TypeName = DefaultGame1Type,
        string saveGameTypeName = DefaultSaveGameType)
    {
        var type = module.GetType(game1TypeName)
                   ?? throw new PrototypeInjectionException("type not found: " + game1TypeName);

        var loadMatches = new List<MethodDefinition>();
        var dayMatches = new List<MethodDefinition>();
        var resetMatches = new List<MethodDefinition>();
        foreach (var method in type.Methods)
        {
            if (method.HasGenericParameters)
            {
                continue;
            }

            if (string.Equals(method.Name, "loadForNewGame", StringComparison.Ordinal) &&
                method.ReturnType.FullName == "System.Void" &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.FullName == "System.Boolean" &&
                method.HasBody && method.Body.Instructions.Count > 0)
            {
                loadMatches.Add(method);
            }

            if (string.Equals(method.Name, "OnDayStarted", StringComparison.Ordinal) &&
                method.IsStatic &&
                method.ReturnType.FullName == "System.Void" &&
                method.Parameters.Count == 0 &&
                method.HasBody && method.Body.Instructions.Count > 0)
            {
                dayMatches.Add(method);
            }

            if (string.Equals(method.Name, ResetMethodName, StringComparison.Ordinal) &&
                !method.IsStatic && method.HasThis &&
                method.ReturnType.FullName == "System.Void" &&
                method.Parameters.Count == 0 &&
                method.HasBody && method.Body.Instructions.Count > 0)
            {
                resetMatches.Add(method);
            }
        }

        if (loadMatches.Count != 1)
        {
            throw new PrototypeInjectionException(
                "expected exactly one void " + game1TypeName + "::loadForNewGame(bool); found " +
                Describe(type, "loadForNewGame"));
        }

        if (dayMatches.Count != 1)
        {
            throw new PrototypeInjectionException(
                "expected exactly one static void " + game1TypeName +
                "::OnDayStarted(); found " + Describe(type, "OnDayStarted"));
        }

        if (resetMatches.Count != 1)
        {
            throw new PrototypeInjectionException(
                "expected exactly one instance void " + game1TypeName + "::" + ResetMethodName +
                "(); found " + Describe(type, ResetMethodName));
        }

        var getGameMode = type.Methods.FirstOrDefault(m =>
            string.Equals(m.Name, "get_gameMode", StringComparison.Ordinal) &&
            m.IsStatic && !m.HasGenericParameters && m.Parameters.Count == 0 &&
            (m.ReturnType.FullName == "System.Byte" || m.ReturnType.FullName == "System.Int32") &&
            m.HasBody);
        if (getGameMode is null)
        {
            throw new PrototypeInjectionException(
                "missing static int/byte " + game1TypeName + "::get_gameMode()");
        }

        var advanceMatches = type.Methods.Where(m =>
            string.Equals(m.Name, AdvanceMethodName, StringComparison.Ordinal) &&
            !m.IsStatic && m.HasThis &&
            m.ReturnType.FullName == "System.Void" &&
            m.Parameters.Count == 0 &&
            m.HasBody && m.Body.Instructions.Count > 0).ToList();
        if (advanceMatches.Count != 1)
        {
            throw new PrototypeInjectionException(
                "expected exactly one instance void " + game1TypeName + "::" + AdvanceMethodName +
                "(); found " + Describe(type, AdvanceMethodName));
        }

        var hasLoadedGame = RequireStaticField(type, "hasLoadedGame", "System.Boolean");
        var currentLoader = RequireStaticField(type, "currentLoader", null);
        if (currentLoader.FieldType.IsValueType || currentLoader.FieldType is PointerType)
        {
            throw new PrototypeInjectionException(
                "currentLoader must be a reference type, found " + currentLoader.FieldType.FullName);
        }

        var saveGame = module.GetType(saveGameTypeName)
                       ?? throw new PrototypeInjectionException(
                           "type not found: " + saveGameTypeName);
        var creationMatches = saveGame.Methods.Where(m =>
            string.Equals(m.Name, LoaderCreationMethodName, StringComparison.Ordinal) &&
            m.IsStatic &&
            m.ReturnType.FullName == "System.Void" &&
            m.Parameters.Count == 0 &&
            m.HasBody && m.Body.Instructions.Count > 0).ToList();
        if (creationMatches.Count != 1)
        {
            throw new PrototypeInjectionException(
                "expected exactly one static void " + saveGameTypeName + "::" + LoaderCreationMethodName +
                "(); found " + Describe(saveGame, LoaderCreationMethodName));
        }

        var creationCall = FindLoaderCreationCall(creationMatches[0], currentLoader);

        return new PrototypeTargets(module, loadMatches[0], dayMatches[0], resetMatches[0],
            getGameMode, hasLoadedGame, currentLoader, advanceMatches[0], creationMatches[0],
            creationCall);
    }

    /// <summary>
    /// Finds the single <c>call getLoadEnumerator(...)</c> whose result is stored straight into
    /// <c>currentLoader</c>. That pair is the attempt-creation boundary: the object identity and the
    /// field store happen at the same instant, on the thread that performed the load request.
    ///
    /// <para>Two shapes are recognised so the same resolver works before and after injection: the
    /// pristine <c>call; stsfld currentLoader</c>, and the injected
    /// <c>call; stloc V; …; ldloc V; stsfld currentLoader</c> form.</para>
    /// </summary>
    private static MethodReference FindLoaderCreationCall(MethodDefinition method,
        FieldDefinition currentLoader)
    {
        var instructions = method.Body.Instructions;
        MethodReference? found = null;
        var stores = 0;
        for (var i = 0; i < instructions.Count; i++)
        {
            if (IsCurrentLoaderStore(instructions[i], currentLoader))
            {
                stores++;
            }

            if (instructions[i].OpCode != OpCodes.Call ||
                instructions[i].Operand is not MethodReference call)
            {
                continue;
            }

            if (call.ReturnType.IsValueType || call.ReturnType is PointerType)
            {
                continue;
            }

            if (!StoresIntentionally(instructions, i, currentLoader))
            {
                continue;
            }

            if (found != null)
            {
                throw new PrototypeInjectionException(
                    "ambiguous creation boundary in " + method.FullName +
                    ": more than one call result is stored into currentLoader");
            }

            found = call;
        }

        if (stores != 1)
        {
            throw new PrototypeInjectionException(
                "unsupported shape: " + method.FullName + " writes currentLoader " + stores +
                " times (expected exactly 1)");
        }

        if (found is null)
        {
            var tail = new System.Text.StringBuilder();
            for (var i = Math.Max(0, instructions.Count - 8); i < instructions.Count; i++)
            {
                tail.Append(" | ").Append(i).Append(':').Append(instructions[i].OpCode.Code)
                    .Append(' ').Append(instructions[i].Operand);
            }

            throw new PrototypeInjectionException(
                "unsupported shape: " + method.FullName +
                " has no 'call <factory>; stsfld currentLoader' creation boundary; tail:" + tail);
        }

        return found;
    }

    private static bool IsCurrentLoaderStore(Instruction instruction, FieldDefinition currentLoader)
    {
        return instruction.OpCode == OpCodes.Stsfld &&
               instruction.Operand is FieldReference field &&
               field.FullName == currentLoader.FullName;
    }

    /// <summary>
    /// True when the value produced by the call at <paramref name="callIndex"/> is what is stored into
    /// <c>currentLoader</c>, either directly or through the injected identity local.
    /// </summary>
    private static bool StoresIntentionally(IList<Instruction> instructions, int callIndex,
        FieldDefinition currentLoader)
    {
        var next = callIndex + 1;
        if (next >= instructions.Count)
        {
            return false;
        }

        if (IsCurrentLoaderStore(instructions[next], currentLoader))
        {
            return true;
        }

        if (instructions[next].OpCode != OpCodes.Stloc ||
            instructions[next].Operand is not VariableDefinition identity)
        {
            return false;
        }

        for (var j = next + 1; j + 1 < instructions.Count && j <= next + 8; j++)
        {
            if (instructions[j].OpCode == OpCodes.Ldloc &&
                ReferenceEquals(instructions[j].Operand, identity) &&
                IsCurrentLoaderStore(instructions[j + 1], currentLoader))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Places all three hooks. Refuses an already-injected or unsupported method.</summary>
    public static PrototypeInjectionResult Inject(PrototypeTargets targets, PrototypeHooks hooks)
    {
        RequireHookShape(hooks.ResetBegan, ResetEntryHookName, "System.Void", NoParameters);
        RequireHookShape(hooks.ResetCompleted, ResetExitHookName, "System.Void", NoParameters);
        RequireHookShape(hooks.GetResetEpoch, ResetEpochGetterName, "System.Int64",
            EpochGetterSignature);
        RequireHookShape(hooks.WorldLoading, StartHookName, "System.Void", StartHookSignature);
        RequireHookShape(hooks.DayCompleted, CompletionHookName, "System.Void",
            CompletionHookSignature);
        RequireHookShape(hooks.LoadAdvanced, AdvanceHookName, "System.Void", AdvanceHookSignature);
        RequireHookShape(hooks.LoadEnumeratorCreated, LoaderCreationHookName, "System.Void",
            LoaderCreationHookSignature);

        var load = targets.LoadForNewGame;
        var day = targets.OnDayStarted;
        var reset = targets.ResetGameStateOnTitleScreen;
        var advance = targets.UpdateTitleScreenDuringLoadingMode;
        var creation = targets.LoadAfterPermissionCheck;

        var existing = CountCalls(reset, hooks.ResetBegan) + CountCalls(reset, hooks.ResetCompleted) +
                       CountCalls(load, hooks.WorldLoading) + CountCalls(load, hooks.GetResetEpoch) +
                       CountCalls(day, hooks.DayCompleted) + CountCalls(advance, hooks.LoadAdvanced) +
                       CountCalls(creation, hooks.LoadEnumeratorCreated) +
                       CountCalls(creation, hooks.GetResetEpoch);
        if (existing > 0)
        {
            throw new PrototypeInjectionException(
                "prototype hooks already present (total call sites=" + existing +
                "); refusing to patch twice");
        }

        var result = new PrototypeInjectionResult
        {
            LoadMaxStackBefore = load.Body.MaxStackSize,
            LoadMaxStackAfter = load.Body.MaxStackSize,
            CompletionMaxStackBefore = day.Body.MaxStackSize,
            CompletionMaxStackAfter = day.Body.MaxStackSize,
            ResetMaxStackBefore = reset.Body.MaxStackSize,
            ResetMaxStackAfter = reset.Body.MaxStackSize,
            AdvanceMaxStackBefore = advance.Body.MaxStackSize,
            AdvanceMaxStackAfter = advance.Body.MaxStackSize,
            LoaderCreationMaxStackBefore = creation.Body.MaxStackSize,
            LoaderCreationMaxStackAfter = creation.Body.MaxStackSize,
        };

        InjectResetEntry(targets, hooks, result);
        InjectResetCompletion(targets, hooks, result);
        InjectEntry(targets, hooks, result);
        InjectCompletion(targets, hooks, result);
        InjectAdvance(targets, hooks, result);
        InjectLoaderCreation(targets, hooks, result);
        return result;
    }

    /// <summary>
    /// Places the P.3 attempt-creation edge between <c>getLoadEnumerator</c> and its store.
    ///
    /// <para>The original tail is:</para>
    /// <code>
    /// call  SaveGame::getLoadEnumerator(string, bool, bool)   ; the state machine, on the stack
    /// stsfld Game1::currentLoader
    /// ret
    /// </code>
    ///
    /// <para>After injection the returned object is held in a fresh local, the reset epoch and the mode
    /// are read, the hook receives the local (never a re-read of the field), and the original
    /// <c>stsfld</c> stores that same local. The attempt identity is therefore the object this
    /// boundary produced, at the instant it was produced.</para>
    /// </summary>
    private static void InjectLoaderCreation(PrototypeTargets targets, PrototypeHooks hooks,
        PrototypeInjectionResult result)
    {
        var method = targets.LoadAfterPermissionCheck;
        var body = method.Body;
        var instructions = body.Instructions;

        var callIndex = -1;
        for (var i = 0; i + 1 < instructions.Count; i++)
        {
            if (instructions[i].OpCode == OpCodes.Call &&
                instructions[i].Operand is MethodReference candidate &&
                candidate.FullName == targets.LoaderCreationCall.FullName &&
                instructions[i + 1].OpCode == OpCodes.Stsfld &&
                instructions[i + 1].Operand is FieldReference storeTarget &&
                storeTarget.FullName == targets.CurrentLoader.FullName)
            {
                callIndex = i;
                break;
            }
        }

        if (callIndex < 0)
        {
            throw new PrototypeInjectionException(
                "unsupported shape: " + method.FullName +
                " no longer holds 'call " + targets.LoaderCreationCall.Name +
                "; stsfld currentLoader'");
        }

        var call = instructions[callIndex];
        if (IsInsideTry(call, body) || IsInsideHandler(call, body))
        {
            throw new PrototypeInjectionException(
                "unsupported control flow: the creation boundary of " + method.FullName +
                " is inside a protected region");
        }

        var identityLocal = new VariableDefinition(targets.CurrentLoader.FieldType);
        body.Variables.Add(identityLocal);
        body.InitLocals = true;

        var il = body.GetILProcessor();
        var afterCall = call;
        foreach (var emitted in new[]
                 {
                     il.Create(OpCodes.Stloc, identityLocal),
                     il.Create(OpCodes.Call, hooks.GetResetEpoch),
                     il.Create(OpCodes.Call, targets.GetGameMode),
                     il.Create(OpCodes.Ldloc, identityLocal),
                     il.Create(OpCodes.Call, hooks.LoadEnumeratorCreated),
                     il.Create(OpCodes.Ldloc, identityLocal),
                 })
        {
            il.InsertAfter(afterCall, emitted);
            afterCall = emitted;
        }

        body.MaxStackSize = Math.Max(body.MaxStackSize, 3);
        result.LoaderCreationCalls = 1;
        result.LoaderCreationMaxStackAfter = body.MaxStackSize;

        WidenShortBranches(method);
    }

    /// <summary>
    /// Places the identity-carrying completion edge in <c>UpdateTitleScreenDuringLoadingMode</c>.
    ///
    /// <para>The original sequence is:</para>
    /// <code>
    /// ldsfld  currentLoader          ; guard
    /// brfalse END
    /// ldsfld  currentLoader          ; the object to advance
    /// callvirt IEnumerator::MoveNext()
    /// brtrue  END
    /// ldnull / stsfld currentLoader  ; cleared only on the failure branch
    /// </code>
    ///
    /// <para>After injection the loader value is held in a single local, <c>MoveNext</c> is invoked on
    /// that local, and the hook receives that same local plus the post-advance mode, the
    /// <c>MoveNext</c> result and <c>hasLoadedGame</c>. The hook therefore never re-reads the global,
    /// which is what makes "this completion belongs to the attempt that created this enumerator"
    /// a data-transfer fact instead of a global comparison.</para>
    /// </summary>
    private static void InjectAdvance(PrototypeTargets targets, PrototypeHooks hooks,
        PrototypeInjectionResult result)
    {
        var method = targets.UpdateTitleScreenDuringLoadingMode;
        var body = method.Body;

        if (body.ExceptionHandlers.Count != 0)
        {
            throw new PrototypeInjectionException(
                "unsupported control flow: " + method.FullName + " has exception handlers");
        }

        var instructions = body.Instructions;
        var guardLoad = -1;
        for (var i = 0; i + 4 < instructions.Count; i++)
        {
            if (!IsFieldLoad(instructions[i], targets.CurrentLoader))
            {
                continue;
            }

            if (instructions[i + 1].OpCode != OpCodes.Brfalse &&
                instructions[i + 1].OpCode != OpCodes.Brfalse_S)
            {
                continue;
            }

            if (!IsFieldLoad(instructions[i + 2], targets.CurrentLoader))
            {
                continue;
            }

            if (!(instructions[i + 3].OpCode == OpCodes.Callvirt &&
                  instructions[i + 3].Operand is MethodReference moved &&
                  moved.Name == "MoveNext") &&
                !(instructions[i + 3].OpCode == OpCodes.Call &&
                  instructions[i + 3].Operand is MethodReference direct && direct.Name == "MoveNext"))
            {
                continue;
            }

            if (instructions[i + 4].OpCode != OpCodes.Brtrue &&
                instructions[i + 4].OpCode != OpCodes.Brtrue_S)
            {
                continue;
            }

            guardLoad = i;
            break;
        }

        if (guardLoad < 0)
        {
            throw new PrototypeInjectionException(
                "unsupported shape: " + method.FullName + " does not match " +
                "ldsfld currentLoader; brfalse; ldsfld currentLoader; MoveNext; brtrue");
        }

        var loaderLocal = new VariableDefinition(targets.CurrentLoader.FieldType);
        var movedLocal = new VariableDefinition(targets.Module.TypeSystem.Boolean);
        body.Variables.Add(loaderLocal);
        body.Variables.Add(movedLocal);
        body.InitLocals = true;

        var il = body.GetILProcessor();
        var firstLoad = instructions[guardLoad];
        var secondLoad = instructions[guardLoad + 2];
        var moveNext = instructions[guardLoad + 3];

        // ldsfld currentLoader ; stloc L ; ldloc L ; brfalse END ; ldloc L ; MoveNext ; stloc M
        il.InsertAfter(firstLoad, il.Create(OpCodes.Stloc, loaderLocal));
        il.InsertAfter(firstLoad.Next!, il.Create(OpCodes.Ldloc, loaderLocal));

        secondLoad.OpCode = OpCodes.Ldloc;
        secondLoad.Operand = loaderLocal;

        // ldloc L ; get_gameMode ; ldloc M ; ldsfld hasLoadedGame ; call LoadAdvanced ; ldloc M
        var afterMoveNext = moveNext;
        foreach (var emitted in new[]
                 {
                     il.Create(OpCodes.Stloc, movedLocal),
                     il.Create(OpCodes.Ldloc, loaderLocal),
                     il.Create(OpCodes.Call, targets.GetGameMode),
                     il.Create(OpCodes.Ldloc, movedLocal),
                     il.Create(OpCodes.Ldsfld, targets.HasLoadedGame),
                     il.Create(OpCodes.Call, hooks.LoadAdvanced),
                     il.Create(OpCodes.Ldloc, movedLocal),
                 })
        {
            il.InsertAfter(afterMoveNext, emitted);
            afterMoveNext = emitted;
        }

        body.MaxStackSize = Math.Max(body.MaxStackSize, 4);
        result.AdvanceCalls = 1;
        result.AdvanceMaxStackAfter = body.MaxStackSize;

        WidenShortBranches(method);
    }

    private static void InjectResetEntry(PrototypeTargets targets, PrototypeHooks hooks,
        PrototypeInjectionResult result)
    {
        var method = targets.ResetGameStateOnTitleScreen;
        var body = method.Body;
        var first = body.Instructions[0];
        if (IsInsideTry(first, body) || IsInsideHandler(first, body))
        {
            throw new PrototypeInjectionException(
                "unsupported control flow: the first instruction of " + method.FullName +
                " is inside a protected region");
        }

        var il = body.GetILProcessor();
        var call = il.Create(OpCodes.Call, hooks.ResetBegan);
        il.InsertBefore(first, call);

        result.ResetEntryCalls = 1;
        result.ResetMaxStackAfter = body.MaxStackSize;

        WidenShortBranches(method);
    }

    private static void InjectResetCompletion(PrototypeTargets targets, PrototypeHooks hooks,
        PrototypeInjectionResult result)
    {
        var method = targets.ResetGameStateOnTitleScreen;
        var body = method.Body;
        var normalReturns = new List<Instruction>();
        var handlerReturns = 0;

        foreach (var ins in body.Instructions)
        {
            if (ins.OpCode.Code != Code.Ret)
            {
                continue;
            }

            if (IsInsideTry(ins, body))
            {
                throw new PrototypeInjectionException(
                    "unsupported control flow: a ret inside a try region in " + method.FullName);
            }

            if (IsInsideHandler(ins, body))
            {
                handlerReturns++;
                continue;
            }

            normalReturns.Add(ins);
        }

        if (normalReturns.Count == 0)
        {
            throw new PrototypeInjectionException(
                "no normal completion path in " + method.FullName +
                " (all returns are inside handlers or there is none)");
        }

        var il = body.GetILProcessor();
        foreach (var ret in normalReturns)
        {
            var call = il.Create(OpCodes.Call, hooks.ResetCompleted);
            il.InsertBefore(ret, call);
            RetargetEdges(body, ret, call);
            MoveBoundaries(body, ret, call);
        }

        result.ResetCompletedCalls = normalReturns.Count;
        result.ResetSkippedHandlerReturns = handlerReturns;
        result.ResetMaxStackAfter = Math.Max(body.MaxStackSize, 1);

        WidenShortBranches(method);
    }

    private static void InjectEntry(PrototypeTargets targets, PrototypeHooks hooks,
        PrototypeInjectionResult result)
    {
        var method = targets.LoadForNewGame;
        var body = method.Body;
        var first = body.Instructions[0];
        if (IsInsideTry(first, body) || IsInsideHandler(first, body))
        {
            throw new PrototypeInjectionException(
                "unsupported control flow: the first instruction of " + method.FullName +
                " is inside a protected region");
        }

        var il = body.GetILProcessor();
        var call = il.Create(OpCodes.Call, hooks.WorldLoading);
        var loadLoader = il.Create(OpCodes.Ldsfld, targets.CurrentLoader);
        var getMode = il.Create(OpCodes.Call, targets.GetGameMode);
        var loadFlag = il.Create(OpCodes.Ldarg, method.Parameters[0]);
        var getEpoch = il.Create(OpCodes.Call, hooks.GetResetEpoch);

        // Push order == parameter order: epoch, loadedGame, gameMode, loader.
        il.InsertBefore(first, call);
        il.InsertBefore(call, loadLoader);
        il.InsertBefore(loadLoader, getMode);
        il.InsertBefore(getMode, loadFlag);
        il.InsertBefore(loadFlag, getEpoch);

        body.MaxStackSize = Math.Max(body.MaxStackSize, 4);
        result.LoadMaxStackAfter = body.MaxStackSize;
        result.WorldLoadingCalls = 1;

        WidenShortBranches(method);
    }

    private static void InjectCompletion(PrototypeTargets targets, PrototypeHooks hooks,
        PrototypeInjectionResult result)
    {
        var method = targets.OnDayStarted;
        var body = method.Body;
        var normalReturns = new List<Instruction>();
        var handlerReturns = 0;

        foreach (var ins in body.Instructions)
        {
            if (ins.OpCode.Code != Code.Ret)
            {
                continue;
            }

            if (IsInsideTry(ins, body))
            {
                throw new PrototypeInjectionException(
                    "unsupported control flow: a ret inside a try region in " + method.FullName);
            }

            if (IsInsideHandler(ins, body))
            {
                // An exception handler returning normally must not look like a successful day.
                handlerReturns++;
                continue;
            }

            normalReturns.Add(ins);
        }

        if (normalReturns.Count == 0)
        {
            throw new PrototypeInjectionException(
                "no normal completion path in " + method.FullName +
                " (all returns are inside handlers or there is none)");
        }

        var il = body.GetILProcessor();
        foreach (var ret in normalReturns)
        {
            var call = il.Create(OpCodes.Call, hooks.DayCompleted);
            var loadLoader = il.Create(OpCodes.Ldsfld, targets.CurrentLoader);
            var loadFlag = il.Create(OpCodes.Ldsfld, targets.HasLoadedGame);
            var getMode = il.Create(OpCodes.Call, targets.GetGameMode);

            il.InsertBefore(ret, call);
            il.InsertBefore(call, loadLoader);
            il.InsertBefore(loadLoader, loadFlag);
            il.InsertBefore(loadFlag, getMode);

            // Every edge that jumped straight at the ret now runs the hook first.
            RetargetEdges(body, ret, getMode);
            // Handler/try ends that named the ret now end before the call, exactly like the ret.
            MoveBoundaries(body, ret, getMode);
        }

        body.MaxStackSize = Math.Max(body.MaxStackSize, 3);
        result.CompletionMaxStackAfter = body.MaxStackSize;
        result.DayCompletedCalls = normalReturns.Count;
        result.SkippedHandlerReturns = handlerReturns;

        WidenShortBranches(method);
    }

    /// <summary>Checks the exact hook call sites, prototypes and instruction order on a module.</summary>
    public static PrototypeVerification Verify(PrototypeTargets targets, PrototypeHooks hooks)
    {
        var report = new PrototypeVerification();
        RequireHookShape(hooks.ResetBegan, ResetEntryHookName, "System.Void", NoParameters, report);
        RequireHookShape(hooks.ResetCompleted, ResetExitHookName, "System.Void", NoParameters, report);
        RequireHookShape(hooks.GetResetEpoch, ResetEpochGetterName, "System.Int64",
            EpochGetterSignature, report);
        RequireHookShape(hooks.WorldLoading, StartHookName, "System.Void", StartHookSignature, report);
        RequireHookShape(hooks.DayCompleted, CompletionHookName, "System.Void",
            CompletionHookSignature, report);
        RequireHookShape(hooks.LoadAdvanced, AdvanceHookName, "System.Void", AdvanceHookSignature,
            report);
        RequireHookShape(hooks.LoadEnumeratorCreated, LoaderCreationHookName, "System.Void",
            LoaderCreationHookSignature, report);

        VerifyReset(targets, hooks, report);
        VerifyStart(targets, hooks, report);
        VerifyCompletion(targets, hooks, report);
        VerifyAdvance(targets, hooks, report);
        VerifyLoaderCreation(targets, hooks, report);
        return report;
    }

    /// <summary>
    /// Checks the P.3 creation edge: one <c>LoadEnumeratorCreated</c> call placed immediately between
    /// the <c>getLoadEnumerator</c> call and the original <c>stsfld currentLoader</c>, fed from the
    /// local that holds that call's result, with the epoch read first and the write still single.
    /// </summary>
    private static void VerifyLoaderCreation(PrototypeTargets targets, PrototypeHooks hooks,
        PrototypeVerification report)
    {
        var method = targets.LoadAfterPermissionCheck;
        var body = method.Body;
        var instructions = body.Instructions;

        report.LoaderCreationCalls = CountCalls(method, hooks.LoadEnumeratorCreated);
        if (report.LoaderCreationCalls != 1)
        {
            report.Fail("LoadEnumeratorCreated call sites != 1: " + report.LoaderCreationCalls);
        }

        var stores = 0;
        foreach (var ins in instructions)
        {
            if (ins.OpCode == OpCodes.Stsfld && ins.Operand is FieldReference field &&
                field.FullName == targets.CurrentLoader.FullName)
            {
                stores++;
            }
        }

        report.LoaderWriteIntact = stores == 1;
        if (!report.LoaderWriteIntact)
        {
            report.Fail("LoadAfterPermissionCheck writes currentLoader " + stores +
                        " times (expected exactly 1)");
        }

        var hookIndex = -1;
        for (var i = 0; i < instructions.Count; i++)
        {
            if (IsCallOf(instructions[i], hooks.LoadEnumeratorCreated))
            {
                hookIndex = i;
                break;
            }
        }

        if (hookIndex < 0)
        {
            report.LoaderCreationIdentityFromFactory = false;
            report.LoaderCreationSiteUnprotected = false;
            report.LoaderCreationEpochFirst = false;
            report.Fail("LoadEnumeratorCreated call site not found for structural verification");
            return;
        }

        // Exact injected layout (see InjectLoaderCreation):
        //   -5 call getLoadEnumerator | -4 stloc ID | -3 GetResetEpoch | -2 get_gameMode
        //   -1 ldloc ID | +0 LoadEnumeratorCreated | +1 ldloc ID | +2 stsfld currentLoader
        VariableDefinition? identityLocal = null;
        var ok = hookIndex >= 5 && hookIndex + 2 < instructions.Count;
        if (ok)
        {
            ok = instructions[hookIndex - 5].OpCode == OpCodes.Call &&
                 instructions[hookIndex - 5].Operand is MethodReference factory &&
                 factory.FullName == targets.LoaderCreationCall.FullName &&
                 instructions[hookIndex - 4].OpCode == OpCodes.Stloc &&
                 instructions[hookIndex - 4].Operand is VariableDefinition v &&
                 v.VariableType.FullName == targets.CurrentLoader.FieldType.FullName;
            if (ok)
            {
                identityLocal = (VariableDefinition)instructions[hookIndex - 4].Operand!;
            }
        }

        report.LoaderCreationIdentityFromFactory = ok;
        if (!ok)
        {
            report.Fail("LoadEnumeratorCreated identity does not come from the getLoadEnumerator result");
        }

        report.LoaderCreationEpochFirst =
            hookIndex >= 3 &&
            IsMethodCall(instructions[hookIndex - 3], hooks.GetResetEpoch) &&
            IsMethodCall(instructions[hookIndex - 2], targets.GetGameMode) &&
            instructions[hookIndex - 1].OpCode == OpCodes.Ldloc &&
            ReferenceEquals(instructions[hookIndex - 1].Operand, identityLocal);
        if (!report.LoaderCreationEpochFirst)
        {
            report.Fail("LoadEnumeratorCreated call site is not GetResetEpoch, get_gameMode, ldloc ID");
        }

        // The identity must not be re-read from the field anywhere in the injected window.
        for (var i = hookIndex - 5; i <= hookIndex; i++)
        {
            if (IsFieldLoad(instructions[i], targets.CurrentLoader))
            {
                report.Fail("LoadEnumeratorCreated receives a re-read of currentLoader");
                report.LoaderCreationIdentityFromFactory = false;
            }
        }

        var storeOk = instructions[hookIndex + 1].OpCode == OpCodes.Ldloc &&
                      ReferenceEquals(instructions[hookIndex + 1].Operand, identityLocal) &&
                      instructions[hookIndex + 2].OpCode == OpCodes.Stsfld &&
                      instructions[hookIndex + 2].Operand is FieldReference target &&
                      target.FullName == targets.CurrentLoader.FullName;
        if (!storeOk)
        {
            report.LoaderWriteIntact = false;
            report.Fail("the original stsfld currentLoader no longer stores the created state machine");
        }

        report.LoaderCreationSiteUnprotected = !IsInsideTry(instructions[hookIndex], body) &&
                                              !IsInsideHandler(instructions[hookIndex], body);
        if (!report.LoaderCreationSiteUnprotected)
        {
            report.Fail("the LoadEnumeratorCreated call site is inside a protected region");
        }
    }

    /// <summary>
    /// Checks the injected completion edge: one <c>LoadAdvanced</c> call, the loader held in a local,
    /// <c>MoveNext</c> invoked on that local, the hook fed from that local, both original guards still
    /// targeting one instruction, and the region unprotected.
    /// </summary>
    private static void VerifyAdvance(PrototypeTargets targets, PrototypeHooks hooks,
        PrototypeVerification report)
    {
        var method = targets.UpdateTitleScreenDuringLoadingMode;
        var body = method.Body;
        var instructions = body.Instructions;

        report.AdvanceCalls = CountCalls(method, hooks.LoadAdvanced);
        if (report.AdvanceCalls != 1)
        {
            report.Fail("LoadAdvanced call sites != 1: " + report.AdvanceCalls);
        }

        if (body.ExceptionHandlers.Count != 0)
        {
            report.Fail("UpdateTitleScreenDuringLoadingMode gained exception handlers");
        }

        var hookIndex = -1;
        for (var i = 0; i < instructions.Count; i++)
        {
            if (IsCallOf(instructions[i], hooks.LoadAdvanced))
            {
                hookIndex = i;
                break;
            }
        }

        if (hookIndex < 0)
        {
            report.AdvanceIdentityFromLocal = false;
            report.AdvanceGuardsIntact = false;
            report.AdvanceSiteUnprotected = false;
            report.Fail("LoadAdvanced call site not found for structural verification");
            return;
        }

        // Exact injected layout around the hook (see InjectAdvance):
        //   -8 brfalse END | -7 ldloc L | -6 MoveNext | -5 stloc M | -4 ldloc L
        //   -3 get_gameMode | -2 ldloc M | -1 ldsfld hasLoadedGame | +0 LoadAdvanced
        //   +1 ldloc M | +2 brtrue END
        var ok = hookIndex >= 8 && hookIndex + 2 < instructions.Count;

        VariableDefinition? loaderLocal = null;
        if (ok)
        {
            ok = instructions[hookIndex - 4].OpCode == OpCodes.Ldloc &&
                 instructions[hookIndex - 4].Operand is VariableDefinition l1 &&
                 l1.VariableType.FullName == targets.CurrentLoader.FieldType.FullName &&
                 IsMethodCall(instructions[hookIndex - 3], targets.GetGameMode) &&
                 instructions[hookIndex - 2].OpCode == OpCodes.Ldloc &&
                 instructions[hookIndex - 1].OpCode == OpCodes.Ldsfld &&
                 instructions[hookIndex - 1].Operand is FieldReference flag &&
                 flag.FullName == targets.HasLoadedGame.FullName;
            if (ok)
            {
                loaderLocal = (VariableDefinition)instructions[hookIndex - 4].Operand!;
            }
        }

        report.AdvanceIdentityFromLocal = ok;
        if (!ok)
        {
            report.Fail("LoadAdvanced arguments are not (local loader, get_gameMode, local moved, " +
                        "hasLoadedGame)");
        }

        // The hook must not receive a fresh read of the global.
        for (var i = hookIndex - 4; i < hookIndex; i++)
        {
            if (IsFieldLoad(instructions[i], targets.CurrentLoader))
            {
                report.Fail("LoadAdvanced receives a re-read of currentLoader instead of the local");
                report.AdvanceIdentityFromLocal = false;
            }
        }

        var moveNextOk = hookIndex >= 7 &&
                         instructions[hookIndex - 6].Operand is MethodReference mr &&
                         mr.Name == "MoveNext" &&
                         (instructions[hookIndex - 6].OpCode == OpCodes.Callvirt ||
                          instructions[hookIndex - 6].OpCode == OpCodes.Call) &&
                         instructions[hookIndex - 7].OpCode == OpCodes.Ldloc &&
                         ReferenceEquals(instructions[hookIndex - 7].Operand, loaderLocal) &&
                         instructions[hookIndex - 5].OpCode == OpCodes.Stloc;
        if (!moveNextOk)
        {
            report.Fail("MoveNext is not invoked on the captured loader local with its result stored");
        }

        var nullGuard = hookIndex >= 8 ? instructions[hookIndex - 8] : null;
        var finishGuard = hookIndex + 2 < instructions.Count ? instructions[hookIndex + 2] : null;
        report.AdvanceGuardsIntact =
            nullGuard != null && finishGuard != null &&
            (nullGuard.OpCode == OpCodes.Brfalse || nullGuard.OpCode == OpCodes.Brfalse_S) &&
            (finishGuard.OpCode == OpCodes.Brtrue || finishGuard.OpCode == OpCodes.Brtrue_S) &&
            nullGuard.Operand is Instruction a && finishGuard.Operand is Instruction b &&
            ReferenceEquals(a, b);
        if (!report.AdvanceGuardsIntact)
        {
            report.Fail("the two original currentLoader guards no longer target one instruction");
        }

        report.AdvanceSiteUnprotected = !IsInsideTry(instructions[hookIndex], body) &&
                                        !IsInsideHandler(instructions[hookIndex], body);
        if (!report.AdvanceSiteUnprotected)
        {
            report.Fail("the LoadAdvanced call site is inside a protected region");
        }

        var returns = 0;
        foreach (var ins in instructions)
        {
            if (ins.OpCode.Code == Code.Ret)
            {
                returns++;
            }
        }

        if (returns != 2)
        {
            report.Fail("UpdateTitleScreenDuringLoadingMode no longer has 2 returns: " + returns);
        }
    }

    private static bool IsCallOf(Instruction ins, MethodReference hook)
    {
        return ins.OpCode == OpCodes.Call && IsHookCall(ins, hook);
    }

    private static void VerifyReset(PrototypeTargets targets, PrototypeHooks hooks,
        PrototypeVerification report)
    {
        var reset = targets.ResetGameStateOnTitleScreen;
        report.ResetEntryCalls = CountCalls(reset, hooks.ResetBegan);
        if (report.ResetEntryCalls != 1)
        {
            report.Fail("ResetBegan call sites != 1: " + report.ResetEntryCalls);
        }

        var instructions = reset.Body.Instructions;
        var firstIndex = 0;
        while (firstIndex < instructions.Count && instructions[firstIndex].OpCode == OpCodes.Nop)
        {
            firstIndex++;
        }

        if (firstIndex >= instructions.Count ||
            instructions[firstIndex].OpCode != OpCodes.Call ||
            !IsHookCall(instructions[firstIndex], hooks.ResetBegan))
        {
            report.Fail("ResetGameStateOnTitleScreen does not start with the ResetBegan call");
        }

        var normalReturns = 0;
        foreach (var ins in reset.Body.Instructions)
        {
            if (ins.OpCode.Code != Code.Ret)
            {
                continue;
            }

            if (IsInsideTry(ins, reset.Body))
            {
                report.Fail("a ret is inside a try region in ResetGameStateOnTitleScreen");
                continue;
            }

            if (IsInsideHandler(ins, reset.Body))
            {
                report.ResetHandlerReturns++;
                continue;
            }

            normalReturns++;
            if (AnyEdgeTargets(reset.Body, ins))
            {
                report.Fail("a control-flow edge still targets the reset normal ret (hook bypassed)");
            }

            var previous = ins.Previous;
            while (previous != null && previous.OpCode == OpCodes.Nop)
            {
                previous = previous.Previous;
            }

            if (previous is null || previous.OpCode != OpCodes.Call ||
                !IsHookCall(previous, hooks.ResetCompleted))
            {
                report.Fail("the reset normal ret is not immediately preceded by ResetCompleted");
            }
        }

        report.ResetNormalReturns = normalReturns;
        report.ResetCompletedCalls = CountCalls(reset, hooks.ResetCompleted);
        if (report.ResetCompletedCalls != normalReturns)
        {
            report.Fail("ResetCompleted call sites (" + report.ResetCompletedCalls +
                        ") != normal returns (" + normalReturns + ")");
        }
    }

    private static void VerifyStart(PrototypeTargets targets, PrototypeHooks hooks,
        PrototypeVerification report)
    {
        var load = targets.LoadForNewGame;
        report.WorldLoadingCalls = CountCalls(load, hooks.WorldLoading);
        if (report.WorldLoadingCalls != 1)
        {
            report.Fail("WorldLoading call sites != 1: " + report.WorldLoadingCalls);
        }

        var instructions = load.Body.Instructions;
        var firstIndex = 0;
        while (firstIndex < instructions.Count && instructions[firstIndex].OpCode == OpCodes.Nop)
        {
            firstIndex++;
        }

        var epochFirst = firstIndex + 4 < instructions.Count &&
                         instructions[firstIndex].OpCode == OpCodes.Call &&
                         IsHookCall(instructions[firstIndex], hooks.GetResetEpoch) &&
                         instructions[firstIndex + 1].OpCode == OpCodes.Ldarg &&
                         instructions[firstIndex + 1].Operand is ParameterDefinition parameter &&
                         parameter.Index == load.Parameters[0].Index &&
                         IsMethodCall(instructions[firstIndex + 2], targets.GetGameMode) &&
                         IsFieldLoad(instructions[firstIndex + 3], targets.CurrentLoader) &&
                         instructions[firstIndex + 4].OpCode == OpCodes.Call &&
                         IsHookCall(instructions[firstIndex + 4], hooks.WorldLoading);

        report.EpochReadFirst = epochFirst;
        if (!epochFirst)
        {
            report.Fail("loadForNewGame does not start with GetResetEpoch, loadedGame, " +
                        "get_gameMode, currentLoader, WorldLoading (epoch must be read first)");
        }

        var epochReads = CountCalls(load, hooks.GetResetEpoch);
        if (epochReads != 1)
        {
            report.Fail("GetResetEpoch call sites != 1: " + epochReads);
        }
    }

    private static void VerifyCompletion(PrototypeTargets targets, PrototypeHooks hooks,
        PrototypeVerification report)
    {
        var day = targets.OnDayStarted;
        var normalReturns = 0;
        foreach (var ins in day.Body.Instructions)
        {
            if (ins.OpCode.Code != Code.Ret)
            {
                continue;
            }

            if (IsInsideTry(ins, day.Body))
            {
                report.Fail("a ret is inside a try region in OnDayStarted");
                continue;
            }

            if (IsInsideHandler(ins, day.Body))
            {
                report.HandlerReturns++;
                continue;
            }

            normalReturns++;
            if (AnyEdgeTargets(day.Body, ins))
            {
                report.Fail("a control-flow edge still targets a normal ret (hook can be bypassed)");
            }

            var previous = ins.Previous;
            while (previous != null && previous.OpCode == OpCodes.Nop)
            {
                previous = previous.Previous;
            }

            if (previous is null || previous.OpCode != OpCodes.Call ||
                !IsHookCall(previous, hooks.DayCompleted))
            {
                report.Fail("a normal ret is not immediately preceded by the DayCompleted call");
            }
        }

        report.NormalReturns = normalReturns;
        report.DayCompletedCalls = CountCalls(day, hooks.DayCompleted);
        if (report.DayCompletedCalls != normalReturns)
        {
            report.Fail("DayCompleted call sites (" + report.DayCompletedCalls +
                        ") != normal returns (" + normalReturns + ")");
        }

        foreach (var ins in day.Body.Instructions)
        {
            if (ins.OpCode.Code == Code.Ret && AnyEdgeTargets(day.Body, ins))
            {
                report.EdgesToNormalReturns++;
            }
        }
    }

    /// <summary>Exact hook call count (declaring type, name, return type, exact parameters).</summary>
    public static int CountCalls(MethodDefinition method, MethodReference hook)
    {
        var count = 0;
        foreach (var ins in method.Body.Instructions)
        {
            if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && IsHookCall(ins, hook))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Per-method structural fingerprint. Used to prove that only the intended methods changed
    /// when the prototype is applied to a temporary copy of the target assembly.
    /// </summary>
    public static Dictionary<string, string> Fingerprint(ModuleDefinition module)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                var key = method.FullName;
                var suffix = 1;
                while (result.ContainsKey(key))
                {
                    key = method.FullName + "#" + suffix++;
                }

                result[key] = FingerprintMethod(method);
            }
        }

        return result;
    }

    /// <summary>
    /// MaxStackSize per method, keyed like <see cref="Fingerprint"/>. Cecil recomputes maxstack
    /// when it writes a module, so this is reported separately: it is a metadata normalisation,
    /// not a semantic edit.
    /// </summary>
    public static Dictionary<string, int> MaxStackMap(ModuleDefinition module)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                var key = method.FullName;
                var suffix = 1;
                while (result.ContainsKey(key))
                {
                    key = method.FullName + "#" + suffix++;
                }

                result[key] = method.Body.MaxStackSize;
            }
        }

        return result;
    }

    private static string FingerprintMethod(MethodDefinition method)
    {
        var body = method.Body;
        var index = new Dictionary<Instruction, int>();
        var i = 0;
        foreach (var ins in body.Instructions)
        {
            index[ins] = i++;
        }

        var sb = new StringBuilder();
        // MaxStackSize is deliberately excluded: Cecil recomputes it for every written method
        // (the original metadata is often more generous than the IL needs). That is a metadata
        // normalisation, not a semantic edit; the hooked methods' stacks are checked separately
        // and the full normalisation range is reported by the target check.
        sb.Append("init=").Append(body.InitLocals).Append(';');
        foreach (var local in body.Variables)
        {
            sb.Append("L:").Append(local.VariableType.FullName).Append(';');
        }

        foreach (var eh in body.ExceptionHandlers)
        {
            sb.Append("EH:").Append(eh.HandlerType).Append(':')
                .Append(Index(index, eh.TryStart)).Append(':').Append(Index(index, eh.TryEnd)).Append(':')
                .Append(Index(index, eh.HandlerStart)).Append(':').Append(Index(index, eh.HandlerEnd))
                .Append(';');
        }

        foreach (var ins in body.Instructions)
        {
            sb.Append(ins.OpCode.Code).Append('(');
            switch (ins.Operand)
            {
                case null:
                    break;
                case MethodReference mr:
                    sb.Append("M:").Append(mr.FullName);
                    break;
                case FieldReference fr:
                    sb.Append("F:").Append(fr.FullName);
                    break;
                case TypeReference tr:
                    sb.Append("T:").Append(tr.FullName);
                    break;
                case string text:
                    sb.Append("S:").Append(text.Replace(";", "\\;"));
                    break;
                case Instruction target:
                    sb.Append("I:").Append(Index(index, target));
                    break;
                case Instruction[] targets:
                    for (var t = 0; t < targets.Length; t++)
                    {
                        if (t > 0)
                        {
                            sb.Append(',');
                        }

                        sb.Append(Index(index, targets[t]));
                    }

                    break;
                case ParameterDefinition pd:
                    sb.Append("P:").Append(pd.Index);
                    break;
                case VariableDefinition vd:
                    sb.Append("V:").Append(vd.Index);
                    break;
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double:
                    sb.Append("N:").Append(Convert.ToString(ins.Operand, CultureInfo.InvariantCulture));
                    break;
                default:
                    sb.Append("O:").Append(ins.Operand);
                    break;
            }

            sb.Append(");");
        }

        return sb.ToString();
    }

    private static string Index(Dictionary<Instruction, int> index, Instruction? instruction)
    {
        if (instruction is null)
        {
            return "-";
        }

        return index.TryGetValue(instruction, out var value) ? value.ToString(CultureInfo.InvariantCulture) : "?";
    }

    private static void RequireHookShape(MethodReference hook, string name, string returnType,
        string[] parameters, PrototypeVerification? report = null)
    {
        void Fail(string problem)
        {
            if (report is null)
            {
                throw new PrototypeInjectionException(problem);
            }

            report.Fail(problem);
        }

        if (!string.Equals(hook.Name, name, StringComparison.Ordinal))
        {
            Fail("hook name mismatch: expected " + name + ", got " + hook.Name);
            return;
        }

        if (hook.ReturnType.FullName != returnType)
        {
            Fail("hook " + name + " must return " + returnType + ", got " + hook.ReturnType.FullName);
            return;
        }

        if (hook.Parameters.Count != parameters.Length)
        {
            Fail("hook " + name + " expects " + parameters.Length + " parameters, got " +
                 hook.Parameters.Count);
            return;
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            if (hook.Parameters[i].ParameterType.FullName != parameters[i])
            {
                Fail("hook " + name + " parameter " + i + " must be " + parameters[i] + ", got " +
                     hook.Parameters[i].ParameterType.FullName);
            }
        }
    }

    private static FieldDefinition RequireStaticField(TypeDefinition type, string name, string? fieldType)
    {
        var matches = type.Fields.Where(f =>
            string.Equals(f.Name, name, StringComparison.Ordinal) && f.IsStatic &&
            (fieldType == null || f.FieldType.FullName == fieldType)).ToList();
        if (matches.Count != 1)
        {
            throw new PrototypeInjectionException(
                "expected exactly one static field " + type.FullName + "::" + name +
                (fieldType is null ? "" : " : " + fieldType));
        }

        return matches[0];
    }

    private static bool IsHookCall(Instruction ins, MethodReference hook)
    {
        return ins.Operand is MethodReference mr &&
               string.Equals(mr.DeclaringType.FullName, hook.DeclaringType.FullName, StringComparison.Ordinal) &&
               string.Equals(mr.Name, hook.Name, StringComparison.Ordinal) &&
               mr.ReturnType.FullName == hook.ReturnType.FullName &&
               mr.Parameters.Count == hook.Parameters.Count &&
               ParametersMatch(mr, hook);
    }

    private static bool IsFieldLoad(Instruction ins, FieldReference field)
    {
        return ins.OpCode == OpCodes.Ldsfld && ins.Operand is FieldReference fr &&
               string.Equals(fr.FullName, field.FullName, StringComparison.Ordinal);
    }

    private static bool IsMethodCall(Instruction ins, MethodReference method)
    {
        return ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr &&
               string.Equals(mr.FullName, method.FullName, StringComparison.Ordinal);
    }

    private static bool ParametersMatch(MethodReference a, MethodReference b)
    {
        for (var i = 0; i < a.Parameters.Count; i++)
        {
            if (a.Parameters[i].ParameterType.FullName != b.Parameters[i].ParameterType.FullName)
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(TypeDefinition type, string name)
    {
        var parts = new List<string>();
        foreach (var method in type.Methods)
        {
            if (!string.Equals(method.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = new List<string>();
            foreach (var p in method.Parameters)
            {
                parameters.Add(p.ParameterType.FullName);
            }

            parts.Add((method.IsStatic ? "static " : "instance ") + method.ReturnType.FullName +
                      "(" + string.Join(",", parameters) + ")");
        }

        return parts.Count == 0 ? "(none)" : string.Join(" | ", parts);
    }

    private static bool IsInsideTry(Instruction ins, MethodBody body)
    {
        foreach (var eh in body.ExceptionHandlers)
        {
            if (InRange(ins, eh.TryStart, eh.TryEnd))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideHandler(Instruction ins, MethodBody body)
    {
        foreach (var eh in body.ExceptionHandlers)
        {
            if (InRange(ins, eh.HandlerStart, eh.HandlerEnd))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Region is [start, end). The end check runs first: an instruction that *is* the end is
    /// outside the region, otherwise a ret a handler ends on would look protected.
    /// </summary>
    private static bool InRange(Instruction ins, Instruction start, Instruction? end)
    {
        var current = start;
        while (current != null)
        {
            if (end != null && ReferenceEquals(current, end))
            {
                return false;
            }

            if (ReferenceEquals(current, ins))
            {
                return true;
            }

            current = current.Next;
        }

        return false;
    }

    private static bool AnyEdgeTargets(MethodBody body, Instruction target)
    {
        foreach (var ins in body.Instructions)
        {
            if (ReferenceEquals(ins.Operand, target))
            {
                return true;
            }

            if (ins.Operand is Instruction[] targets)
            {
                foreach (var candidate in targets)
                {
                    if (ReferenceEquals(candidate, target))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void RetargetEdges(MethodBody body, Instruction from, Instruction to)
    {
        foreach (var ins in body.Instructions)
        {
            if (ReferenceEquals(ins.Operand, from))
            {
                ins.Operand = to;
                continue;
            }

            if (ins.Operand is Instruction[] targets)
            {
                for (var i = 0; i < targets.Length; i++)
                {
                    if (ReferenceEquals(targets[i], from))
                    {
                        targets[i] = to;
                    }
                }
            }
        }
    }

    private static void MoveBoundaries(MethodBody body, Instruction from, Instruction to)
    {
        foreach (var eh in body.ExceptionHandlers)
        {
            if (ReferenceEquals(eh.TryStart, from))
            {
                throw new PrototypeInjectionException(
                    "unsupported control flow: an exception-handler try region starts at a ret");
            }

            if (ReferenceEquals(eh.HandlerStart, from))
            {
                throw new PrototypeInjectionException(
                    "unsupported control flow: an exception-handler handler starts at a ret");
            }

            if (ReferenceEquals(eh.TryEnd, from))
            {
                eh.TryEnd = to;
            }

            if (eh.HandlerEnd != null && ReferenceEquals(eh.HandlerEnd, from))
            {
                eh.HandlerEnd = to;
            }
        }
    }

    private static void WidenShortBranches(MethodDefinition method)
    {
        foreach (var ins in method.Body.Instructions)
        {
            var longForm = LongForm(ins.OpCode);
            if (longForm != null)
            {
                ins.OpCode = longForm.Value;
            }
        }
    }

    private static OpCode? LongForm(OpCode op)
    {
        return op.Code switch
        {
            Code.Br_S => OpCodes.Br,
            Code.Brfalse_S => OpCodes.Brfalse,
            Code.Brtrue_S => OpCodes.Brtrue,
            Code.Beq_S => OpCodes.Beq,
            Code.Bge_S => OpCodes.Bge,
            Code.Bgt_S => OpCodes.Bgt,
            Code.Ble_S => OpCodes.Ble,
            Code.Blt_S => OpCodes.Blt,
            Code.Bne_Un_S => OpCodes.Bne_Un,
            Code.Bge_Un_S => OpCodes.Bge_Un,
            Code.Bgt_Un_S => OpCodes.Bgt_Un,
            Code.Ble_Un_S => OpCodes.Ble_Un,
            Code.Blt_Un_S => OpCodes.Blt_Un,
            Code.Leave_S => OpCodes.Leave,
            _ => null,
        };
    }
}
